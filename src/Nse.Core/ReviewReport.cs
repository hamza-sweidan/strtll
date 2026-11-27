using System.Globalization;
using System.Text;
using Nse.Ledger;
using Nse.Text;

namespace Nse;

/// <summary>
/// The review queue, and what proposing and deciding came to, in words. Both front ends print exactly this,
/// so what the author reads at a terminal is what an agent is given to show them.
/// </summary>
/// <remarks>
/// The author decides from this text, and much of it comes from records and proposals that an agent wrote.
/// So everything that did not come from the engine itself is printed through <see cref="Plain.OneLine"/>:
/// a line break or a terminal code inside a note or a record is spelled out, never obeyed, and nothing a
/// proposal says can pass for a line of the queue's own.
/// </remarks>
public static class ReviewReport
{
    /// <summary>The whole queue is shown to an agent only while it is at most about this many tokens; past that, in brief.</summary>
    public const int AgentBudget = 8000;

    private static string S(string? text) => Plain.OneLine(text);

    /// <param name="forAgent">True when the reader is an agent, which may show the queue and may not decide on it.</param>
    /// <param name="brief">
    /// Leave out what can be asked for one item at a time: the operations of each proposal and the records of
    /// each change set. Flagged records are still listed, because each has to be named to be ratified.
    /// </param>
    /// <param name="only">The id of the one proposal or change set the queue was narrowed to, if it was.</param>
    /// <param name="ratifyTool">For an agent: whether it has the tool that carries out the author's decision.</param>
    /// <param name="plain">
    /// The state the queue was read from, to give each provisional record as a sentence in plain words, after a
    /// paragraph on what its change set records. Null for the usual one line each.
    /// </param>
    public static string Queue(ReviewQueue queue, bool forAgent = false, bool brief = false, string? only = null, bool ratifyTool = true,
        Query.StoryState? plain = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"REVIEW QUEUE · ledger head {queue.Head}{(only is null ? "" : $" · {S(only)} alone")}{(brief ? " · in brief" : "")}{(plain is null ? "" : " · in plain words")}\n");
        if (queue.Empty)
        {
            sb.Append("\nNothing is waiting: no proposal is held and no record is provisional.\n");
            return sb.ToString();
        }
        if (queue.Proposals.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nProposals held for the author's decision ({queue.Proposals.Count}). None of this is in the ledger:\n");
            foreach (var held in queue.Proposals)
            {
                var p = held.Proposal;
                sb.Append(CultureInfo.InvariantCulture, $"\n  {S(p.Id)} · proposed by {S(p.Actor)} at ledger head {p.BaseHead} · ");
                sb.Append(held.WouldBeRefused.Count == 0 ? "would apply as things stand\n" : "WOULD BE REFUSED as things stand\n");
                sb.Append("    Why: ").Append(S(p.Note)).Append('\n');
                sb.Append("    The author's to decide because ").Append(string.Join("; ", held.Why.Select(S))).Append(".\n");
                if (brief)
                    sb.Append(CultureInfo.InvariantCulture,
                        $"      {held.Ops.Count} operation(s): {held.Ops.Count(o => o.Verb == "put")} to add, {held.Ops.Count(o => o.Verb == "retire")} to retire\n");
                else
                    foreach (var line in Operations(held, "      "))
                        sb.Append(line).Append('\n');
                foreach (var f in held.WouldBeRefused)
                    sb.Append("    refused by: ").Append(S(f.ToString())).Append('\n');
            }
        }
        if (queue.Unreadable.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nFiles among the proposals that cannot be used ({queue.Unreadable.Count}). Nothing in them will be applied, and they cannot be decided on: delete them to clear them away:\n");
            foreach (var u in queue.Unreadable)
                sb.Append(CultureInfo.InvariantCulture, $"  ledger/proposals/{S(u.File)}: {S(u.Why)}\n");
        }
        if (queue.Records > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nProvisional records awaiting the author's decision ({queue.Records}), by the change set that brought them:\n");
            foreach (var group in queue.Provisional)
            {
                var cs = group.ChangeSet.ChangeSet;
                var flagged = group.Records.Count(r => r.Flagged);
                sb.Append(CultureInfo.InvariantCulture, $"\n  {S(cs.Id)} · change set {cs.Seq} · {group.Records.Count} waiting{(flagged > 0 ? $", {flagged} flagged" : "")} · {S(cs.Actor)}");
                if (cs.Basis is { } basis)
                    sb.Append(CultureInfo.InvariantCulture, $" · submission for {S(basis.Scene)}{(basis.Rebased ? ", rebased" : "")}");
                if (cs.Note is { } note)
                    sb.Append(" · ").Append(S(note));
                sb.Append('\n');
                if (plain is not null)
                {
                    var records = group.Records.Select(r => plain.Model.ById.TryGetValue(r.Id, out var t) ? t.Record : null).OfType<Model.LedgerRecord>().ToList();
                    sb.Append("    ").Append(S(Query.PlainText.Summary(records, flagged, cs.Basis?.Scene))).Append('\n');
                    foreach (var r in group.Records.Where(r => !brief || r.Flagged))
                        sb.Append("    ").Append(r.Flagged ? "FLAGGED " : "").Append(S(r.Id)).Append(": ")
                            .Append(plain.Model.ById.TryGetValue(r.Id, out var t) ? Query.PlainText.Sentence(plain, t.Record) : S(r.Text)).Append('\n');
                    continue;
                }
                foreach (var line in Records(group.Records.Where(r => !brief || r.Flagged).ToList(), "    ", Width(group.Records)))
                    sb.Append(line).Append('\n');
            }
        }
        sb.Append('\n');
        if (brief)
            sb.Append(forAgent
                ? "This is the queue in brief. For every operation of one proposal, or every record of one change set, call review_queue with its id in only.\n"
                : "This is the queue in brief. For every operation of one proposal, or every record of one change set: nse review ID\n");
        sb.Append(!forAgent ? "To decide: nse ratify ID ...   or   nse reject ID ... --note WHY\n"
            : ratifyTool ? "Only the author decides. Show them what is here and ask. When they have said what to accept and what to reject, call ratify with exactly those ids: it shows them an approval prompt, and nothing is decided without it.\n"
            : "Only the author decides, and you have no tool that does. Show them what is here. They decide in their own terminal: nse ratify ID ...   or   nse reject ID ... --note WHY\n");
        sb.Append("An id is a record, a change set or a proposal. Ratifying a change set ratifies every record it brought that still awaits a decision, except the flagged ones, which are ratified by naming them. Accepting a proposal applies it with its records ratified.\n");
        return sb.ToString();
    }

    /// <summary>The column the ids of a list of records are set in: as wide as the longest, within reason.</summary>
    private static int Width(IReadOnlyList<PendingRecord> records) => records.Count == 0 ? 0 : Math.Min(24, records.Max(r => S(r.Id).Length));

    /// <summary>One line for each record: flagged or not, its id, and what it says.</summary>
    private static IEnumerable<string> Records(IReadOnlyList<PendingRecord> records, string indent, int width) =>
        records.Select(r => indent + (r.Flagged ? "FLAGGED " : "        ") + S(r.Id).PadRight(width) + "  " + S(r.Text));

    /// <summary>The operations of a proposal, numbered, each with what it takes away or takes the place of.</summary>
    private static IEnumerable<string> Operations(HeldProposal held, string indent)
    {
        for (var i = 0; i < held.Ops.Count; i++)
        {
            var op = held.Ops[i];
            yield return string.Create(CultureInfo.InvariantCulture, $"{indent}{i + 1}. {S(op.Verb)} {S(op.Id)}: {S(op.Text)}");
            foreach (var line in op.Detail)
                yield return indent + "     " + S(line);
        }
    }

    /// <summary>
    /// The queue as an agent is given it: whole while that is small enough to read in one answer, and in
    /// brief past that, with the way to ask for one item at a time.
    /// </summary>
    public static string ForAgent(ReviewQueue queue, string? only = null, bool ratifyTool = true, Query.StoryState? plain = null)
    {
        var whole = Queue(queue, forAgent: true, only: only, ratifyTool: ratifyTool, plain: plain);
        return Query.ContextCompiler.EstimateTokens(whole) <= AgentBudget
            ? whole
            : Queue(queue, forAgent: true, brief: true, only: only, ratifyTool: ratifyTool, plain: plain);
    }

    public static string Proposed(ProposeResult result)
    {
        var sb = new StringBuilder();
        if (result.Proposal is not { } p)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Refused. Nothing was held and nothing was written. {result.Findings.Count} finding(s):\n");
            foreach (var f in result.Findings)
                sb.Append("  ").Append(S(f.ToString())).Append('\n');
            return sb.ToString();
        }
        sb.Append(result.AlreadyHeld
            ? string.Create(CultureInfo.InvariantCulture, $"Already held as proposal {S(p.Id)}: the same change was proposed before and still waits for the author.\n")
            : string.Create(CultureInfo.InvariantCulture, $"Held as proposal {S(p.Id)}: {p.Ops.Count} operation(s), checked against ledger head {p.BaseHead}.\n"));
        sb.Append("Nothing was applied, and no answer about the story has changed. It waits for the author, who accepts or declines it. Tell the author it is there and why you proposed it. Do not work as though it had been accepted.\n");
        return sb.ToString();
    }

    /// <summary>
    /// What a decision comes to, line by line, with every record and every operation spelled out. These lines
    /// are what the author is asked to confirm, and what the engine compares when it works the decision out
    /// again before writing it.
    /// </summary>
    public static IReadOnlyList<string> PlanLines(
        IReadOnlyList<PendingRecord> ratify, IReadOnlyList<PendingRecord> reject,
        IReadOnlyList<HeldProposal> accept, IReadOnlyList<Proposal> decline, IReadOnlyList<string> leftFlagged,
        IReadOnlyList<string>? consequences = null)
    {
        var lines = new List<string>();
        var width = Width([.. ratify, .. reject]);
        if (ratify.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  ratify {ratify.Count} record(s):"));
            lines.AddRange(Records(ratify, "    ", width));
        }
        if (reject.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  reject {reject.Count} record(s):"));
            lines.AddRange(Records(reject, "    ", width));
        }
        foreach (var held in accept)
        {
            var p = held.Proposal;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  accept proposal {S(p.Id)}, from {S(p.Actor)}, and ratify what it adds: {S(p.Note)}"));
            lines.Add("    the author's to decide because " + string.Join("; ", held.Why.Select(S)));
            lines.AddRange(Operations(held, "      "));
        }
        foreach (var p in decline)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  decline proposal {S(p.Id)}, from {S(p.Actor)} ({p.Ops.Count} operation(s)): {S(p.Note)}"));
        if (leftFlagged.Count > 0)
            lines.Add("  left alone because they are flagged: " + string.Join(", ", leftFlagged.Select(S)));
        if (consequences is { Count: > 0 })
        {
            lines.Add("  and all of it together:");
            lines.AddRange(consequences.Select(line => "  " + line));
        }
        return lines;
    }

    /// <summary>What a decision would do, for the author to confirm before it is written.</summary>
    public static string Plan(DecisionPlan plan) => string.Concat(plan.Shown.Select(line => line + "\n"));

    public static string Decided(DecisionResult result)
    {
        var sb = new StringBuilder();
        if (!result.Confirmed)
            return "Not confirmed. Nothing was decided.\n";
        if (result.Findings.Count > 0 && result.Applied.Count == 0 && result.Declined.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Refused. Nothing was decided and nothing was written. {result.Findings.Count} finding(s):\n");
            foreach (var f in result.Findings)
                sb.Append("  ").Append(S(f.ToString())).Append('\n');
            return sb.ToString();
        }
        sb.Append(result.Findings.Count == 0 ? "Decided.\n" : "Decided in part, then stopped. This much was done:\n");
        foreach (var stored in result.Applied)
        {
            var cs = stored.ChangeSet;
            if (cs.Proposal is { } proposal)
                sb.Append(CultureInfo.InvariantCulture,
                    $"  Proposal {S(proposal.Id)} accepted and applied as {stored.File}: {cs.Ops.OfType<PutOp>().Count()} record(s) ratified, {cs.Ops.OfType<RetireOp>().Count()} retired.\n");
            else
                sb.Append(CultureInfo.InvariantCulture,
                    $"  {cs.Ops.OfType<RatifyOp>().Sum(o => o.Ids.Count)} record(s) ratified and {cs.Ops.OfType<RejectOp>().Sum(o => o.Ids.Count)} rejected, as {stored.File}.\n");
        }
        foreach (var id in result.Declined)
            sb.Append(CultureInfo.InvariantCulture, $"  Proposal {S(id)} declined. It is kept under ledger/proposals/declined/ with the decision.\n");
        if (result.Plan is { LeftFlagged.Count: > 0 } plan && result.Findings.Count == 0)
            sb.Append(CultureInfo.InvariantCulture, $"  Left provisional because they are flagged: {string.Join(", ", plan.LeftFlagged.Select(S))}. Each is ratified by naming it.\n");
        foreach (var f in result.Findings)
            sb.Append("  ").Append(S(f.ToString())).Append('\n');
        // Said again now that it is done: the prompt that showed it is gone, and an agent that carried the
        // decision out never saw one.
        if (result.Plan is { Consequences.Count: > 0 } told && result.Applied.Count > 0 && result.Findings.Count == 0)
        {
            sb.Append("This decision\n");
            foreach (var line in told.Consequences)
                sb.Append(line).Append('\n');
            sb.Append("The attention report lists what is in question at any time.\n");
        }
        if (result.Applied.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"Ledger head {result.Applied[^1].ChangeSet.Seq}.\n");
        return sb.ToString();
    }
}
