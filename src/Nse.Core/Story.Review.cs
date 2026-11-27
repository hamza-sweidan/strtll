using Nse.Index;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Text;
using Nse.Validation;

namespace Nse;

/// <summary>The outcome of asking for a change to be held for the author. Nothing is held unless there are no findings.</summary>
public sealed record ProposeResult(Proposal? Proposal, IReadOnlyList<Finding> Findings, bool AlreadyHeld = false)
{
    public bool Held => Proposal is not null;
}

/// <summary>One operation of a held proposal, in words.</summary>
/// <param name="Verb">put or retire.</param>
/// <param name="Detail">
/// Lines under it: what a put would take the place of, the entries of a contract, or the record a retire
/// would take away.
/// </param>
public sealed record ProposedOp(string Verb, string Id, string Text, IReadOnlyList<string> Detail);

/// <summary>A held proposal as the author is shown it.</summary>
/// <param name="Why">What makes it the author's to decide.</param>
/// <param name="WouldBeRefused">Empty if it would apply to the ledger as it stands; otherwise why not.</param>
public sealed record HeldProposal(
    Proposal Proposal, IReadOnlyList<string> Why, IReadOnlyList<Finding> WouldBeRefused, IReadOnlyList<ProposedOp> Ops);

/// <summary>A provisional record that still awaits the author's decision.</summary>
public sealed record PendingRecord(string Id, bool Flagged, string Text);

/// <summary>The records one change set brought that still await a decision.</summary>
public sealed record PendingGroup(StoredChangeSet ChangeSet, IReadOnlyList<PendingRecord> Records);

/// <summary>Everything that waits on the author: proposals held, and provisional records not yet decided.</summary>
public sealed record ReviewQueue(
    int Head, IReadOnlyList<HeldProposal> Proposals, IReadOnlyList<PendingGroup> Provisional,
    IReadOnlyList<UnreadableProposal> Unreadable)
{
    public int Records => Provisional.Sum(g => g.Records.Count);

    public bool Empty => Proposals.Count == 0 && Records == 0 && Unreadable.Count == 0;

    /// <summary>The queue narrowed to one held proposal or one change set, or null if neither has that id.</summary>
    public ReviewQueue? Only(string id)
    {
        var proposals = Proposals.Where(p => p.Proposal.Id == id).ToList();
        var groups = Provisional.Where(g => g.ChangeSet.ChangeSet.Id == id).ToList();
        return proposals.Count + groups.Count == 0 ? null : this with { Proposals = proposals, Provisional = groups, Unreadable = [] };
    }
}

/// <summary>
/// What the author decided. Each id is a record, a change set, or a held proposal. Accepting a record
/// ratifies it. Accepting a change set ratifies every record it brought that still awaits a decision, except
/// the flagged ones, which are left to be named. Accepting a proposal applies it with its records ratified.
/// Rejecting a record rejects it; rejecting a change set rejects everything it brought that still awaits a
/// decision; rejecting a proposal declines it. A record named by itself is decided as named, whatever is
/// said about the change set that brought it.
/// </summary>
public sealed record Decision
{
    public IReadOnlyList<string> Accept { get; init; } = [];
    public IReadOnlyList<string> Reject { get; init; } = [];
    public string Actor { get; init; } = "author";

    /// <summary>The author's reason, if they gave one. It goes on record with the decision.</summary>
    public string? Note { get; init; }
}

/// <summary>What a decision comes to once every id has been looked up, before anything is written.</summary>
/// <param name="LeftFlagged">Flagged records that ratifying a whole change set left alone.</param>
/// <param name="Shown">
/// The whole of it in words, line by line, with each record and each operation spelled out: what the author
/// is asked to confirm. A confirmed decision is written only if these lines are still what it comes to.
/// </param>
public sealed record DecisionPlan(
    IReadOnlyList<string> Ratify, IReadOnlyList<string> RejectRecords,
    IReadOnlyList<Proposal> AcceptProposals, IReadOnlyList<Proposal> DeclineProposals,
    IReadOnlyList<string> LeftFlagged, IReadOnlyList<string> Shown)
{
    /// <summary>
    /// What the decision does to what is already written: the records of later scenes it leaves without what
    /// they rested on, each with its passage and the reason. They are among the lines shown for confirmation,
    /// and are kept apart here so that they can be said again once the decision is written.
    /// </summary>
    public IReadOnlyList<string> Consequences { get; init; } = [];
}

/// <summary>
/// The outcome of a decision. With findings and nothing applied or declined, nothing was done. Findings
/// together with something applied or declined mean the writing stopped part-way, which only a failure of
/// the disk should bring about; what was done by then is listed and stands.
/// </summary>
/// <param name="Declined">The ids of the proposals that were declined.</param>
public sealed record DecisionResult(
    DecisionPlan? Plan, IReadOnlyList<Finding> Findings, IReadOnlyList<StoredChangeSet> Applied, IReadOnlyList<string> Declined,
    bool Confirmed = true)
{
    public bool Done => Plan is not null && Confirmed && Findings.Count == 0;
}

public sealed partial class Story
{
    /// <summary>
    /// Hold a change for the author to decide on. This is how anything an agent may not apply itself reaches
    /// the author: a rule, an embargo, a contract, a retcon, the retiring of a record the author ratified.
    /// The change is checked against the ledger as it stands, exactly as it would be if applied, and refused
    /// with findings if it could not be. Otherwise it is written beside the ledger and nothing in the ledger
    /// changes: no answer about the story is affected until the author accepts it.
    /// </summary>
    public ProposeResult Propose(ChangeSetDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (clean, malformed) = Normalised(draft);
        if (clean is null)
            return new ProposeResult(null, malformed!.Findings);
        draft = clean with { Ops = clean.Ops.Select(op => op is PutOp put ? put with { Record = MadeBy(put.Record, clean.Actor) } : op).ToList() };
        if (draft.Ops.Count == 0)
            return new ProposeResult(null, [new Finding("EMPTY", null, "the proposal has no operations")]);
        var barred = Barred(draft.Ops);
        if (barred.Count > 0)
            return new ProposeResult(null, barred);
        if (string.IsNullOrWhiteSpace(draft.Note))
            return new ProposeResult(null, [new Finding("REASON_REQUIRED", null,
                "say why this is proposed, in the note. The author decides from the note and the changes, and it stays on record with the decision")]);

        using var writeLock = AcquireWriteLock();
        if (writeLock is null)
            return new ProposeResult(null, Locked().Findings);
        var ledger = ReadLedger();
        var before = Projection.Build(ledger.Select(s => s.ChangeSet));
        var proposal = ProposalStore.Make(draft, before.Head);
        var (_, findings) = CheckProposal(before, proposal, ReadManuscript());
        if (findings.Count > 0)
            return new ProposeResult(null, findings);
        Tidy(ledger);
        if (ProposalStore.FindDeclined(LedgerDir, proposal.Id) is { } declined)
            return new ProposeResult(null, [new Finding("ALREADY_DECLINED", null,
                $"this same proposal, {proposal.Id}, was declined by {declined.By}{(declined.Reason is null ? "" : $": {declined.Reason}")}. It is not held again. If something has changed since, say what in the note")]);
        if (Held(ledger).Held.Any(p => p.Id == proposal.Id))
            return new ProposeResult(proposal, [], AlreadyHeld: true);
        ProposalStore.Hold(LedgerDir, proposal);
        return new ProposeResult(proposal, []);
    }

    /// <summary>
    /// A record that arrives by a submission or a proposal was made by whoever sent it, and says so. It may
    /// say more, the sender's name and then a colon and, for instance, a model's name. Anything else it
    /// claims, the author above all, is replaced: the ledger does not put a sender's record down to someone else.
    /// </summary>
    private static LedgerRecord MadeBy(LedgerRecord record, string actor) =>
        string.IsNullOrWhiteSpace(actor) || record.Origin == actor || record.Origin.StartsWith(actor + ":", StringComparison.Ordinal)
            ? record
            : record with { Origin = actor };

    /// <summary>The operations of a proposal that no proposal may carry, as findings.</summary>
    private static List<Finding> Barred(IReadOnlyList<Operation> ops) =>
        ops.Select(op => (Op: op, Why: Policy.NotInAProposal(op))).Where(x => x.Why is not null)
            .Select(x => new Finding("AUTHOR_ONLY", x.Op is PutOp put ? put.Record.Id : null, x.Why!)).ToList();

    /// <summary>A proposal as the change set it becomes when the author accepts it: its records are ratified by that decision.</summary>
    private static ChangeSetDraft Accepted(Proposal proposal, string actor, string? note) => new()
    {
        Actor = actor, Note = note,
        Ops = proposal.Ops.Select(op => op is PutOp put ? put with { Approval = Approval.Ratified } : op).ToList(),
    };

    /// <summary>
    /// Whether a proposal could be applied to the ledger as a projection has it, checked as the change set it
    /// would become. Gives the projection it would leave, or the reasons it cannot be applied.
    /// </summary>
    private static (Projection? After, IReadOnlyList<Finding> Findings) CheckProposal(Projection before, Proposal proposal, ManuscriptSnapshot manuscript)
    {
        // Held proposals are files, and a file can be put in the folder by hand. Whatever wrote it, a
        // proposal that carries a decision is not applied.
        var barred = Barred(proposal.Ops);
        if (barred.Count > 0)
            return (null, barred);
        var (draft, malformed) = Normalised(Accepted(proposal, proposal.Actor, proposal.Note));
        return draft is null ? (null, malformed!.Findings) : Check(before, draft, manuscript);
    }

    /// <summary>
    /// The proposals that wait on the author. A file whose proposal the ledger already records as accepted,
    /// or that has a record of being declined, is one a crash left behind, and does not count.
    /// </summary>
    private (IReadOnlyList<Proposal> Held, IReadOnlyList<UnreadableProposal> Unreadable) Held(IReadOnlyList<StoredChangeSet> ledger)
    {
        var (found, unreadable) = ProposalStore.Load(LedgerDir);
        var accepted = ledger.Select(s => s.ChangeSet.Proposal?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return (found.Where(p => !accepted.Contains(p.Id) && ProposalStore.FindDeclined(LedgerDir, p.Id) is null).ToList(), unreadable);
    }

    /// <summary>How many proposals wait on the author. Cheap enough for the brief.</summary>
    public int HeldProposals() => Held(ReadLedger()).Held.Count;

    /// <summary>Clear away proposal files a crash left behind after their decision was recorded. Call with the write lock held.</summary>
    private void Tidy(IReadOnlyList<StoredChangeSet> ledger)
    {
        try
        {
            var waiting = Held(ledger).Held.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var p in ProposalStore.Load(LedgerDir).Held.Where(p => !waiting.Contains(p.Id)))
                ProposalStore.Remove(LedgerDir, p.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on the tidying: readers go by the ledger and the record of what was declined.
        }
    }

    /// <summary>The records a change set brought that are still in force and still await a decision.</summary>
    private static IEnumerable<Tracked> Awaiting(Projection projection, StoredChangeSet changeSet) =>
        changeSet.ChangeSet.Ops.OfType<PutOp>().Select(put => projection.Find(put.Record.Id)).OfType<Tracked>()
            .Where(t => t.CreatedSeq == changeSet.ChangeSet.Seq && t.LiveAt(projection.Head)
                        && t.ApprovalAt(projection.Head) == Approval.Provisional);

    /// <summary>A held proposal as the author is shown it: what each operation does, what it would take the place of, and whether it would still apply.</summary>
    private static HeldProposal View(
        Proposal proposal, IReadOnlyList<StoredChangeSet> ledger, Projection before, ManuscriptSnapshot manuscript, StoryState now)
    {
        var (after, findings) = CheckProposal(before, proposal, manuscript);
        // Described with the proposal applied where it can be, so that people and things it introduces have their names.
        var view = after is null ? now : StoryIndex.FromFiles(after, null, manuscript);
        var ops = proposal.Ops.Select(op => op switch
        {
            PutOp put => new ProposedOp("put", put.Record.Id, RecordText.Line(view, put.Record),
                [.. RecordText.Replaces(now, put.Record), .. RecordText.Detail(view, put.Record)]),
            RetireOp retire => new ProposedOp("retire", retire.Id, retire.Reason,
                before.Find(retire.Id) is { } old
                    ? [$"takes away, {Names.Of(old.ApprovalAt(before.Head))}: {RecordText.Line(now, old.Record)}"]
                    : []),
            RatifyOp ratify => new ProposedOp("ratify", string.Join(", ", ratify.Ids), "a decision, which no proposal may carry", []),
            RejectOp reject => new ProposedOp("reject", string.Join(", ", reject.Ids), "a decision, which no proposal may carry", []),
            _ => new ProposedOp("unknown", "", "", []),
        }).ToList();
        var why = proposal.Ops.Select(op => Policy.WhyHeld(op, before, ledger, now)).Distinct(StringComparer.Ordinal).ToList();
        return new HeldProposal(proposal, why, findings, ops);
    }

    /// <summary>
    /// Everything that waits on the author, read from the files as they are: each held proposal with what it
    /// would do and whether it would still apply, and every provisional record not yet decided, grouped by the
    /// change set that brought it. Nothing is written.
    /// </summary>
    public ReviewQueue Review()
    {
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        var before = Projection.Build(ledger.Select(s => s.ChangeSet));
        var now = StoryIndex.FromFiles(before, null, manuscript);
        var (held, unreadable) = Held(ledger);
        var proposals = held.Select(p => View(p, ledger, before, manuscript, now)).ToList();
        var groups = ledger
            .Select(s => new PendingGroup(s, Awaiting(before, s)
                .Select(t => new PendingRecord(t.Record.Id, Policy.Flagged(t.Record), RecordText.Line(now, t.Record))).ToList()))
            .Where(g => g.Records.Count > 0).ToList();
        return new ReviewQueue(before.Head, proposals, groups, unreadable);
    }

    /// <summary>
    /// Carry out the author's decision. Every id is looked up and the whole of it is checked first, each part
    /// against the ledger as the parts before it would leave it; if anything is wrong, nothing is done. Then
    /// one change set is written for the records ratified and rejected, and one for each proposal accepted,
    /// in the order named.
    /// </summary>
    /// <param name="confirm">
    /// If given, it is shown what the decision comes to and may still say no. It is asked with the write lock
    /// released, because a question to a person may stay open for minutes and nobody else should have to wait
    /// on it. On a yes the decision is worked out again under the lock, and written only if it still comes to
    /// exactly what was shown; if the story moved under it, nothing is written and the author is told.
    /// </param>
    /// <remarks>
    /// This method does not know who is calling. It is the two front ends that keep it to the author: the
    /// command line runs it only at a terminal, and the MCP server offers it only as a tool marked as needing
    /// the user, which the harness will not run without showing its approval prompt.
    /// </remarks>
    public DecisionResult Decide(Decision decision, Func<DecisionPlan, bool>? confirm = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        static DecisionResult No(IEnumerable<Finding> findings) => new(null, findings.ToList(), [], []);
        var accept = (decision.Accept ?? []).ToList();
        var reject = (decision.Reject ?? []).ToList();
        if (accept.Concat(reject).Any(string.IsNullOrWhiteSpace))
            return No([new Finding("MALFORMED", null, "the list of ids to accept or to reject holds a null or an empty id")]);
        accept = accept.Distinct(StringComparer.Ordinal).ToList();
        reject = reject.Distinct(StringComparer.Ordinal).ToList();
        if (accept.Count == 0 && reject.Count == 0)
            return No([new Finding("EMPTY", null, "the decision names nothing to accept and nothing to reject")]);
        if (string.IsNullOrWhiteSpace(decision.Actor))
            return No([new Finding("BAD_VALUE", null, "the decision needs an actor: who is deciding")]);
        // A decision that only declines writes no change set, so nothing else would hold its words to the
        // rule every other text in the ledger keeps. The reason for a decline is shown again later.
        foreach (var (text, what) in new[] { (decision.Actor, "actor"), (decision.Note, "note") })
            if (text is not null && Plain.Odd(text) is { } odd)
                return No([new Finding("BAD_VALUE", null,
                    $"the decision's {what} holds a line break or another control character ({odd}). Text in the ledger is one line of ordinary characters: write a line break as a space")]);
        var both = accept.Intersect(reject, StringComparer.Ordinal).ToList();
        if (both.Count > 0)
            return No(both.Select(id => new Finding("BAD_VALUE", id, "it is named both to accept and to reject")));

        Prepared shown;
        using (var writeLock = AcquireWriteLock())
        {
            if (writeLock is null)
                return No(Locked().Findings);
            var (desk, shortened) = OpenDesk(writeLock);
            if (desk is null)
                return No(shortened!.Findings);
            var (prepared, findings) = Prepare(desk, decision, accept, reject);
            if (prepared is null)
                return No(findings);
            if (confirm is null)
                return Noted(Write(writeLock, desk, prepared, decision));
            shown = prepared;
        }

        if (!confirm(shown.Plan))
            return new DecisionResult(shown.Plan, [], [], [], Confirmed: false);

        using (var writeLock = AcquireWriteLock())
        {
            if (writeLock is null)
                return No(Locked().Findings);
            var (desk, shortened) = OpenDesk(writeLock);
            if (desk is null)
                return No(shortened!.Findings);
            var (prepared, findings) = Prepare(desk, decision, accept, reject);
            const string Moved = "Nothing was written. Run it again to see it as it now stands";
            if (prepared is null)
                return No([new Finding("CHANGED", null, $"the story changed while this decision was being confirmed, and it can no longer be made as it was shown. {Moved}"), .. findings]);
            if (!Same(shown, prepared))
                return No([new Finding("CHANGED", null, $"what this decision comes to changed while it was being confirmed. {Moved}")]);
            return Noted(Write(writeLock, desk, prepared, decision));
        }
    }

    /// <summary>A decision that was written is noted in the tasks whose submissions it touched.</summary>
    private DecisionResult Noted(DecisionResult result)
    {
        NoteDecision(result);
        return result;
    }

    /// <summary>A decision worked out and checked: what it comes to, the change sets it will write, and the ledger they leave.</summary>
    private sealed record Prepared(DecisionPlan Plan, List<Unit> Units, Projection After);

    /// <summary>Whether a decision worked out a second time comes to what it came to the first time: the same words shown, the same change sets to write.</summary>
    private static bool Same(Prepared shown, Prepared now) =>
        shown.Plan.Shown.SequenceEqual(now.Plan.Shown, StringComparer.Ordinal)
        && shown.Units.Select(u => (NseJson.Write(u.Draft), u.Proposal)).SequenceEqual(now.Units.Select(u => (NseJson.Write(u.Draft), u.Proposal)));

    /// <summary>Look every id up and check the whole decision against the ledger a writer holds. Nothing is written.</summary>
    private (Prepared? Prepared, List<Finding> Findings) Prepare(Desk desk, Decision decision, List<string> accept, List<string> reject)
    {
        Tidy(desk.Ledger);
        var (held, unreadable) = Held(desk.Ledger);
        var head = desk.Before.Head;

        // What each id names. A record named by itself is decided as named; a change set stands for the
        // records it brought that still await a decision, less any that were named by themselves.
        var findings = new List<Finding>();
        var named = new Dictionary<string, bool>(StringComparer.Ordinal);
        var bulk = new List<(string Id, bool Accepting)>();
        var acceptProposals = new List<Proposal>();
        var declineProposals = new List<Proposal>();
        var leftFlagged = new List<string>();
        foreach (var (id, accepting) in accept.Select(id => (id, true)).Concat(reject.Select(id => (id, false))))
        {
            var proposal = held.FirstOrDefault(p => p.Id == id);
            var changeSet = desk.Ledger.FirstOrDefault(s => s.ChangeSet.Id == id);
            var record = desk.Before.Find(id);
            // Only what can still be decided competes for a name. A record that was retired or rejected is past
            // deciding, and must not stand in the way of a proposal or a change set that has the same id.
            var inForce = record is not null && record.LiveAt(head);
            var names = new[] { proposal is null ? null : "a held proposal", changeSet is null ? null : "a change set", inForce ? "a record" : null }
                .OfType<string>().ToList();
            if (names.Count > 1)
                findings.Add(new Finding("AMBIGUOUS", id, $"it names {string.Join(" and ", names)}. Nothing was decided. Retire the record, with the reason, and decide again"));
            else if (proposal is not null)
                (accepting ? acceptProposals : declineProposals).Add(proposal);
            else if (changeSet is not null)
            {
                var waiting = Awaiting(desk.Before, changeSet).ToList();
                var mine = accepting ? waiting.Where(t => !Policy.Flagged(t.Record)).ToList() : waiting;
                if (waiting.Count == 0)
                    findings.Add(new Finding("NOTHING_TO_DECIDE", id, "none of the records it brought awaits a decision"));
                else if (mine.Count == 0)
                    findings.Add(new Finding("NOTHING_TO_DECIDE", id,
                        $"all that still awaits a decision in it is flagged: {string.Join(", ", waiting.Select(t => t.Record.Id))}. A flagged record is ratified by naming it"));
                else
                {
                    bulk.AddRange(mine.Select(t => (t.Record.Id, accepting)));
                    if (accepting)
                        leftFlagged.AddRange(waiting.Where(t => Policy.Flagged(t.Record)).Select(t => t.Record.Id));
                }
            }
            else if (record is not null)
                // In force, or not: if it is past deciding, the ledger says exactly why when the decision is checked.
                named[id] = accepting;
            else
                findings.Add(NotFound(id, desk.Ledger, unreadable));
        }
        if (findings.Count > 0)
            return (null, findings);
        foreach (var (id, accepting) in bulk)
            named.TryAdd(id, accepting);
        var ratify = named.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var rejectRecords = named.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        leftFlagged = leftFlagged.Where(id => !named.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();

        // Check the whole of it in the order it will be written, each part on top of the ones before.
        var units = new List<Unit>();
        var projection = desk.Before;
        void Try(Unit unit, string? inProposal)
        {
            if (findings.Count > 0)
                return;
            var (draft, malformed) = Normalised(unit.Draft);
            var (after, problems) = draft is null ? (null, malformed!.Findings) : Check(projection, draft, desk.Manuscript);
            if (after is null)
            {
                findings.AddRange(inProposal is null ? problems : problems.Select(f => f with { Message = $"in proposal {inProposal}: {f.Message}" }));
                return;
            }
            units.Add(unit with { Draft = draft! });
            projection = after;
        }
        if (ratify.Count + rejectRecords.Count > 0)
        {
            var ops = new List<Operation>();
            if (ratify.Count > 0)
                ops.Add(new RatifyOp { Ids = ratify });
            if (rejectRecords.Count > 0)
                ops.Add(new RejectOp { Ids = rejectRecords });
            Try(new Unit(new ChangeSetDraft { Actor = decision.Actor, Note = decision.Note, Ops = ops }), null);
        }
        foreach (var proposal in acceptProposals)
        {
            var barred = Barred(proposal.Ops);
            if (barred.Count > 0 && findings.Count == 0)
                findings.AddRange(barred.Select(f => f with { Message = $"in proposal {proposal.Id}: {f.Message}" }));
            Try(new Unit(Accepted(proposal, decision.Actor, decision.Note),
                Proposal: new AcceptedProposal(proposal.Id, proposal.Actor, proposal.Note, proposal.BaseHead)), proposal.Id);
        }
        if (findings.Count > 0)
            return (null, findings);

        // What it comes to, spelled out: this is what the author confirms, and what is compared when they have.
        var now = StoryIndex.FromFiles(desk.Before, null, desk.Manuscript);
        PendingRecord Pending(string id)
        {
            var record = desk.Before.Find(id)!.Record;
            return new PendingRecord(id, Policy.Flagged(record), RecordText.Line(now, record));
        }
        // A scene may be written on from records the author has not reviewed. So a rejection, or a proposal that
        // retires something, can leave a later passage resting on nothing, and the author is told which and why
        // before they confirm. Ratifying changes nothing anything rests on, and is not looked into.
        IReadOnlyList<string> consequences = [];
        if (rejectRecords.Count + acceptProposals.Count > 0)
        {
            try
            {
                var then = StoryIndex.FromFiles(projection, null, desk.Manuscript);
                consequences = Consequences.Between(now, then).Lines(then);
            }
            catch (KeyNotFoundException)
            {
                // The decision passed every check. What it does to the rest could not be worked out, and it is not refused for that.
            }
        }
        var lines = ReviewReport.PlanLines(
            ratify.Select(Pending).ToList(), rejectRecords.Select(Pending).ToList(),
            acceptProposals.Select(p => View(p, desk.Ledger, desk.Before, desk.Manuscript, now)).ToList(), declineProposals, leftFlagged, consequences);
        return (new Prepared(new DecisionPlan(ratify, rejectRecords, acceptProposals, declineProposals, leftFlagged, lines) { Consequences = consequences }, units, projection), findings);
    }

    /// <summary>Write a decision that was checked under this lock. Whatever happens, what was done is reported.</summary>
    private DecisionResult Write(FileStream writeLock, Desk desk, Prepared prepared, Decision decision)
    {
        var findings = new List<Finding>();
        var applied = new List<StoredChangeSet>();
        var declined = new List<string>();
        try
        {
            foreach (var unit in prepared.Units)
            {
                applied.Add(Append(desk, unit));
                if (unit.Proposal is { } done)
                    Forget(done.Id);
            }
            foreach (var proposal in prepared.Plan.DeclineProposals)
            {
                ProposalStore.Decline(LedgerDir, proposal, decision.Actor, decision.Note, desk.Ledger.Count);
                declined.Add(proposal.Id);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Everything was checked before the first write, so this is the disk, or something nobody foresaw.
            // Either way the author is owed an account of what was written before it.
            findings.Add(new Finding("LEDGER", null,
                $"the writing stopped part-way: {e.Message}. What is listed here as done was written before that and stands. Run 'nse review' to see what is still waiting"));
        }
        if (applied.Count > 0)
        {
            MarkSeen(writeLock, desk.Ledger.Count);
            RefreshIndex(desk, applied.Count == prepared.Units.Count ? prepared.After : Projection.Build(desk.Ledger.Select(s => s.ChangeSet)));
        }
        return new DecisionResult(prepared.Plan, findings, applied, declined);
    }

    /// <summary>Take an accepted proposal's file away. The ledger already says it was accepted, so a failure here loses nothing.</summary>
    private void Forget(string proposalId)
    {
        try
        {
            ProposalStore.Remove(LedgerDir, proposalId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Why an id names nothing that can be decided, said as usefully as the files allow.</summary>
    private Finding NotFound(string id, IReadOnlyList<StoredChangeSet> ledger, IReadOnlyList<UnreadableProposal> unreadable)
    {
        if (ledger.FirstOrDefault(s => s.ChangeSet.Proposal?.Id == id) is { } became)
            return new Finding("ALREADY_DECIDED", id, $"that proposal was accepted already, as change set {became.ChangeSet.Seq} ({became.ChangeSet.Id}). A decision is final");
        if (ProposalStore.FindDeclined(LedgerDir, id) is { } declined)
            return new Finding("ALREADY_DECIDED", id, $"that proposal was declined already by {declined.By}. A decision is final; if it should be looked at again, have it proposed again");
        if (unreadable.FirstOrDefault(u => u.File == id + ".json") is { } broken)
            return new Finding("BAD_PROPOSAL", id, $"the file for that proposal cannot be used: {broken.Why}. Nothing in it will be applied, and it cannot be declined either: delete ledger/proposals/{broken.File}, and have the change proposed again if it is wanted");
        return new Finding("UNKNOWN_REFERENCE", id, "it is not a record, a change set or a held proposal");
    }
}
