using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Index;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Validation;

namespace Nse;

/// <summary>The outcome of asking for a change set to be applied. Nothing is written unless there are no findings.</summary>
public sealed record PutResult(StoredChangeSet? ChangeSet, IReadOnlyList<Finding> Findings)
{
    public bool Applied => ChangeSet is not null;
}

/// <summary>
/// What an agent declares about a scene it has drafted or revised: the records the scene's text supports, and
/// the id of the context the draft was written from.
/// </summary>
public sealed record SceneReport
{
    public required string Scene { get; init; }

    /// <summary>The manifest id printed at the end of the scene's context. It names the ledger head the draft was written from.</summary>
    public required string Manifest { get; init; }
    public string Actor { get; init; } = "agent";
    public string? Note { get; init; }
    public required IReadOnlyList<LedgerRecord> Records { get; init; }

    /// <summary>
    /// Ids of records an earlier submission for this scene left, which this draft no longer supports. Only
    /// records that came from a submission for the same scene and are still provisional can be withdrawn here.
    /// </summary>
    public IReadOnlyList<string> Withdraws { get; init; } = [];
}

/// <summary>
/// The outcome of a scene submission. <see cref="Findings"/> are reasons nothing was written. <see cref="Notes"/>
/// come with a submission that was applied: where the scene differs from its contract, and what was flagged.
/// They do not undo the write; each is the author's to settle.
/// </summary>
public sealed record SubmitResult(
    StoredChangeSet? ChangeSet, IReadOnlyList<Finding> Findings, IReadOnlyList<Finding> Notes, ChangeBasis? Basis)
{
    public bool Applied => ChangeSet is not null;
}

/// <summary>What re-reading the manuscript turned up.</summary>
public sealed record SyncReport(
    int Head, int Scenes, string ManuscriptHash,
    IReadOnlyList<string> Problems,
    IReadOnlyList<EvidenceStatus> BrokenEvidence,
    IReadOnlyList<string> TimeMismatches,
    IReadOnlyList<string> ScenesWithoutRecord,
    IReadOnlyList<string> RecordsWithoutScene,
    IReadOnlyList<string> EmbargoBreaches,
    IReadOnlyList<string> ContractDifferences,
    bool IndexRebuilt)
{
    public bool Clean => Problems.Count == 0 && BrokenEvidence.Count == 0 && TimeMismatches.Count == 0
                         && ScenesWithoutRecord.Count == 0 && RecordsWithoutScene.Count == 0 && EmbargoBreaches.Count == 0
                         && ContractDifferences.Count == 0;
}

/// <summary>
/// One story on disk: prose under manuscript/, state history under ledger/, and a disposable index under .nse/.
/// The two front ends, the command line and the MCP server, are thin layers over this class.
/// </summary>
public sealed partial class Story
{
    public string Root { get; }
    public string ManuscriptDir => Path.Combine(Root, "manuscript");
    public string LedgerDir => Path.Combine(Root, "ledger");
    public string IndexPath => Path.Combine(Root, ".nse", "index.sqlite");

    /// <summary>
    /// Held by whichever process is appending to the ledger. It sits beside what it guards and is never deleted:
    /// not on release, and not by anyone clearing out .nse/, which the index invites. The story's .gitignore
    /// keeps it out of git.
    /// </summary>
    public string WriteLockPath => Path.Combine(LedgerDir, ".lock");

    private Story(string root) => Root = Path.GetFullPath(root);

    /// <summary>Create the folders of a new story. Safe to run on an existing one.</summary>
    public static Story Init(string root)
    {
        var story = new Story(root);
        Directory.CreateDirectory(story.ManuscriptDir);
        Directory.CreateDirectory(story.LedgerDir);
        EnsureLines(Path.Combine(story.Root, ".gitignore"),
            ("# The index is rebuilt from ledger/ and manuscript/ and is never the truth.", ".nse/"),
            ("# Held while a change set is being appended. It stays in place and is not part of the story.", "ledger/.lock"),
            WorkIgnored);
        // Change sets are chained by the hash of their bytes, so git must not rewrite their line endings.
        WriteIfAbsent(Path.Combine(story.Root, ".gitattributes"), "ledger/** -text\n");
        return story;
    }

    public static Story Open(string root)
    {
        var story = new Story(root);
        if (!Directory.Exists(story.LedgerDir) || !Directory.Exists(story.ManuscriptDir))
            throw new DirectoryNotFoundException(
                $"{story.Root} is not a story: it needs a manuscript/ and a ledger/ folder. Run 'nse init' there first.");
        return story;
    }

    private static void WriteIfAbsent(string path, string text)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, text);
    }

    /// <summary>Add each line a file lacks, with its comment. What is already in the file is left as it is.</summary>
    private static void EnsureLines(string path, params (string Comment, string Line)[] wanted)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var present = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(w => !present.Contains(w.Line)).ToList();
        if (missing.Count == 0)
            return;
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += "\n";
        File.WriteAllText(path, text + string.Concat(missing.Select(w => $"{w.Comment}\n{w.Line}\n")));
    }

    public ManuscriptSnapshot ReadManuscript() => ManuscriptReader.Read(ManuscriptDir);

    public IReadOnlyList<StoredChangeSet> ReadLedger() => LedgerStore.Load(LedgerDir);

    /// <summary>
    /// The one way records enter the ledger. The draft is checked against the ledger and the manuscript as they
    /// stand; if anything is wrong nothing is written and every finding is returned.
    /// </summary>
    /// <param name="confirm">
    /// If given, asked once the draft has passed every check and before anything is written. A no leaves the
    /// ledger as it was. It is asked with the write lock released: a question to a person may stay open for
    /// minutes, and nobody else should wait on it. On a yes the draft is checked again as it is written.
    /// </param>
    public PutResult Put(ChangeSetDraft draft, Func<ChangeSetDraft, bool>? confirm = null)
    {
        if (confirm is null)
            return Tidied(Apply(draft, gate: null).Result);
        var sound = Apply(draft, gate: null, write: false).Result;
        if (sound.Findings.Count > 0)
            return sound;
        return confirm(draft) ? Tidied(Apply(draft, gate: null).Result) : Refused(NotConfirmed, "it passed every check, and was not confirmed");
    }

    /// <summary>
    /// A change set of the author's can settle what a task submitted without being a decision on it: a retirement
    /// does. A task that is over is put away then and there, so that an edit to the scene later is not taken
    /// for more work on it.
    /// </summary>
    private PutResult Tidied(PutResult result)
    {
        if (result.Applied)
            PutAwayFinished();
        return result;
    }

    /// <summary>What a check made under the write lock decided: reasons to refuse, or the basis to record.</summary>
    private sealed record Gate(IReadOnlyList<Finding> Findings, ChangeBasis? Basis);

    private sealed record Outcome(PutResult Result, Projection? After = null, ManuscriptSnapshot? Manuscript = null, ChangeBasis? Basis = null);

    /// <summary>
    /// Check a draft and append it. The optional gate runs under the write lock, with the ledger and the
    /// manuscript exactly as the append will see them, so nothing can move between its check and the write.
    /// </summary>
    /// <param name="write">False to stop once every check has passed, with nothing written.</param>
    private Outcome Apply(
        ChangeSetDraft draft, Func<IReadOnlyList<StoredChangeSet>, Projection, ManuscriptSnapshot, Gate>? gate, bool write = true)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (clean, malformed) = Normalised(draft);
        if (clean is null)
            return new Outcome(malformed!);
        draft = clean;
        if (draft.Ops.Count == 0)
            return new Outcome(Refused("EMPTY", "the change set has no operations"));
        // Each Claude Code session runs its own process. Only one may read the head and append at a time,
        // or two would write the same sequence number.
        using var writeLock = AcquireWriteLock();
        if (writeLock is null)
            return new Outcome(Locked());
        var (desk, shortened) = OpenDesk(writeLock);
        if (desk is null)
            return new Outcome(shortened!);
        var passed = gate?.Invoke(desk.Ledger, desk.Before, desk.Manuscript);
        if (passed is { Findings.Count: > 0 })
            return new Outcome(new PutResult(null, passed.Findings));
        var (after, findings) = Check(desk.Before, draft, desk.Manuscript);
        if (after is null)
            return new Outcome(new PutResult(null, findings));
        if (!write)
            return new Outcome(new PutResult(null, []));
        StoredChangeSet stored;
        try
        {
            stored = Append(desk, new Unit(draft, passed?.Basis));
        }
        catch (LedgerException e)
        {
            return new Outcome(Refused("LEDGER", e.Message));
        }
        MarkSeen(writeLock, desk.Ledger.Count);
        RefreshIndex(desk, after);
        return new Outcome(new PutResult(stored, []), after, desk.Manuscript, passed?.Basis);
    }

    /// <summary>
    /// Whatever built a draft, a file or a tool call or code, hold it to what a ledger file must be: write it
    /// and read it back. A null where a value is required is left out on writing, so the file would be missing
    /// that field and nothing could read the ledger again. That is refused here, before anything else.
    /// </summary>
    private static (ChangeSetDraft? Draft, PutResult? Refusal) Normalised(ChangeSetDraft draft)
    {
        try
        {
            var text = NseJson.Write(draft);
            if (NseJson.NullInList(JsonNode.Parse(text)) is { } where)
                return (null, Refused("MALFORMED", $"the list at {where} holds a null. Leave the entry out or give it a value"));
            return (NseJson.Read<ChangeSetDraft>(text), null);
        }
        catch (JsonException e)
        {
            return (null, Refused("MALFORMED", "the change set is not in the shape a ledger file must have: " + e.Message));
        }
        catch (ArgumentException)
        {
            // How the writer refuses a number that is not finite: there is no JSON for infinity.
            return (null, Refused("MALFORMED", "a number in the change set is not finite. A story time is an ordinary number: not infinity, and not the result of a sum that has no answer"));
        }
    }

    /// <summary>The one finding of a put that was sound and that the author, asked, did not go ahead with.</summary>
    public const string NotConfirmed = "NOT_CONFIRMED";

    private static PutResult Locked() => Refused("LOCKED", "another process is writing to this ledger. Try again in a moment");

    /// <summary>The ledger and the manuscript as a writer reads them once it holds the write lock.</summary>
    private sealed record Desk(List<StoredChangeSet> Ledger, Projection Before, ManuscriptSnapshot Manuscript, int Seen);

    /// <summary>One change set to append: the draft, and what the engine records about where it came from.</summary>
    private sealed record Unit(ChangeSetDraft Draft, ChangeBasis? Basis = null, AcceptedProposal? Proposal = null);

    /// <summary>Read what a writer needs, through the write lock it holds.</summary>
    private (Desk? Desk, PutResult? Refusal) OpenDesk(FileStream writeLock)
    {
        var ledger = ReadLedger().ToList();
        // Nothing is added to a ledger that may have lost its end: a new change set on top would bury the loss
        // and leave two histories to untangle.
        var mark = ReadMark(writeLock);
        var seen = Math.Max(Math.Max(mark, StoryIndex.ReadInfo(IndexPath)?.SeenHead ?? 0), ledger.Count);
        if (seen > ledger.Count)
            return (null, Refused("LEDGER_SHORTENED",
                $"the ledger ends at change set {ledger.Count}, but this folder has seen it end at {seen}. Restore the missing files under ledger/ from git, or run 'nse rebuild' to accept the ledger as it stands, and then put this again"));
        if (mark < ledger.Count)
            MarkSeen(writeLock, ledger.Count);
        return (new Desk(ledger, Projection.Build(ledger.Select(s => s.ChangeSet)), ReadManuscript(), seen), null);
    }

    // The longest ledger this folder has seen is written in the lock file itself: beside the ledger, never
    // deleted, and written only by whoever holds the lock. The index carries the same number, but the index
    // is in a folder anyone may delete, and a ledger that has lost its end must not be forgotten that way.

    private static int ReadMark(FileStream file)
    {
        var bytes = new byte[32];
        file.Position = 0;
        var read = file.Read(bytes, 0, bytes.Length);
        return int.TryParse(Encoding.ASCII.GetString(bytes, 0, read).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mark) ? mark : 0;
    }

    /// <summary>Record how long the ledger now is. If it cannot be written, the index's copy of the number still stands.</summary>
    private static void MarkSeen(FileStream writeLock, int head)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(head.ToString(CultureInfo.InvariantCulture) + "\n");
            writeLock.SetLength(0);
            writeLock.Position = 0;
            writeLock.Write(bytes, 0, bytes.Length);
            writeLock.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The same mark for a reader. A reader does not wait for it: while a writer holds the lock the file
    /// cannot be opened, and the index's copy of the number is used for that one answer.
    /// </summary>
    private int SeenMark()
    {
        try
        {
            using var file = new FileStream(WriteLockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadMark(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether a draft could be appended to the ledger as a projection has it. Gives the projection it would
    /// leave, or the reasons it cannot be applied. Nothing is written.
    /// </summary>
    private static (Projection? After, IReadOnlyList<Finding> Findings) Check(Projection before, ChangeSetDraft draft, ManuscriptSnapshot manuscript)
    {
        var candidate = new ChangeSet
        {
            Seq = before.Head + 1, Id = "cs_draft", Actor = draft.Actor, Note = draft.Note,
            Manuscript = manuscript.Hash, Ops = draft.Ops,
        };
        Projection after;
        try
        {
            after = before.With(candidate);
        }
        catch (LedgerException e)
        {
            return (null, [new Finding("LEDGER", null, e.Message)]);
        }
        var findings = Validator.Check(before, after, draft, manuscript);
        return findings.Count > 0 ? (null, findings) : (after, []);
    }

    private StoredChangeSet Append(Desk desk, Unit unit)
    {
        var stored = LedgerStore.Append(LedgerDir, desk.Ledger, unit.Draft, desk.Manuscript.Hash, unit.Basis, unit.Proposal);
        desk.Ledger.Add(stored);
        return stored;
    }

    /// <summary>
    /// A change set that is in the ledger is applied, and that is what "applied" means. The index is only a
    /// cache: if it cannot be refreshed now, because another program has it open, the next read deals with it.
    /// </summary>
    private void RefreshIndex(Desk desk, Projection after)
    {
        try
        {
            StoryIndex.Rebuild(IndexPath, desk.Ledger, after, desk.Manuscript, desk.Seen);
        }
        catch (Exception e) when (IsIndexTrouble(e))
        {
        }
    }

    /// <summary>
    /// The way an agent's account of a drafted or revised scene enters the ledger. Three things are checked on
    /// top of everything <see cref="Put"/> checks. The submission carries only what a scene submission may:
    /// records of what the scene does, never rules, embargoes, contracts or decisions, which are the author's.
    /// Every record belongs to this scene: it names it, is dated within its span of story time, and has nobody
    /// present who is not in its cast. And the state the draft was written from is still the state. The manifest
    /// id must be the one this engine issues for the scene at the ledger head it names, with the scenes in the
    /// order they are in now. The scene's context is then compiled at that head and at the current one, and if
    /// they differ the submission is refused with the lines that differ. If the ledger moved on and nothing the
    /// context says has changed, the submission is applied and the rebase is recorded in the change set.
    /// Everything applied is provisional. Afterwards the scene is compared with its contract; the differences
    /// come back as notes and stop nothing.
    /// </summary>
    public SubmitResult SubmitScene(SceneReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var result = Submit(report);
        // Whatever it came to, the scene's task says so, for the session that comes after this one. If the
        // task could not be written, the submission stands and whoever sent it is told: the brief will not
        // show it, and the review queue will.
        return NoteSubmission(report, result) is { } problem
            ? result with
            {
                Notes = [.. result.Notes, new Finding("TASK_NOT_KEPT", null,
                    $"the scene's task could not be written ({problem}), so a later session's brief will not show this submission as work in progress. The submission itself was applied, and the review queue lists it")],
            }
            : result;
    }

    private SubmitResult Submit(SceneReport report)
    {
        static SubmitResult No(params Finding[] findings) => new(null, findings, [], null);
        var scene = report.Scene;
        if (ManifestId.Parse(report.Manifest) is not { } manifest)
        {
            // What was sent may be any length and hold anything. It is shown as one line, and only its start.
            var sent = Text.Plain.OneLine(report.Manifest);
            return No(new Finding("BAD_MANIFEST", null,
                $"'{(sent.Length > 60 ? sent[..60] + "..." : sent)}' is not a manifest id. Ask for the context of {scene} and give back the id printed at its end, exactly as printed. It looks like m_12_a1b2c3_0123abcd"));
        }
        var records = report.Records ?? [];
        var withdraws = report.Withdraws ?? [];
        if (records.Any(r => r is null) || withdraws.Any(string.IsNullOrWhiteSpace))
            return No(new Finding("MALFORMED", null, "the list of records or of withdrawals holds a null or an empty id"));
        records = records.Select(r => MadeBy(r, report.Actor)).ToList();
        if (records.Count == 0 && withdraws.Count == 0)
            return No(new Finding("EMPTY", null, "the submission records nothing and withdraws nothing"));
        var barred = records.Select(r => (Record: r, Why: Policy.NotInASceneSubmission(r))).Where(x => x.Why is not null)
            .Select(x => new Finding("PROPOSAL_ONLY", x.Record.Id, $"{x.Why}. It cannot come in with a scene; it goes to the author as a proposal")).ToArray();
        if (barred.Length > 0)
            return No(barred);

        var draft = new ChangeSetDraft
        {
            Actor = report.Actor, Note = report.Note,
            // Withdrawals first, so that a record and what replaces it can travel in one submission.
            Ops =
            [
                .. withdraws.Select(id => (Operation)new RetireOp { Id = id, Reason = "superseded by a new draft of the scene" }),
                .. records.Select(r => (Operation)new PutOp { Record = r }),
            ],
        };
        var outcome = Apply(draft, (ledger, before, manuscript) =>
        {
            static Gate Stop(params Finding[] findings) => new(findings, null);
            const string AskAgain = "Ask for the context of the scene again and work from that";
            if (manifest.Head > ledger.Count)
                return Stop(new Finding("STALE_CONTEXT", null,
                    $"the manifest names ledger head {manifest.Head}, and the ledger ends at {ledger.Count}. It is not a manifest of this ledger as it stands. {AskAgain}"));
            var now = StoryIndex.FromFiles(before, null, manuscript);
            if (!now.Model.Scenes.ContainsKey(scene) || now.Tell(scene) is null || !now.Model.SceneTimes.ContainsKey(scene))
                return Stop(new Finding("UNKNOWN_SCENE", null,
                    $"{scene} needs a scene record, a place in story time and a marker in the manuscript before a draft of it can be submitted. Those are the author's to set"));

            // The order the scenes are told in decides what the reader has been given on entry. It comes from
            // the manuscript, which keeps no history, so the manifest carries a mark of it.
            if (ContextCompiler.OrderMark(now) != manifest.Order)
                return Stop(new Finding("STALE_CONTEXT", null,
                    $"the scenes in the manuscript are not the scenes, in the order, that they were when this context was compiled. What the reader has been given on entry to {scene} may have changed. Nothing was written. {AskAgain}"));

            // The manifest must be the one this engine issues for this scene at that head. With the order of the
            // scenes unchanged, compiling again gives the same marks or the id is not what it claims to be.
            ContextPackage then;
            try
            {
                then = ContextCompiler.Compile(StoryIndex.FromFiles(before, manifest.Head, manuscript), scene, int.MaxValue);
            }
            catch (KeyNotFoundException e)
            {
                return Stop(new Finding("BAD_MANIFEST", null, $"no context for {scene} could have been compiled at ledger head {manifest.Head}: {e.Message} {AskAgain}"));
            }
            if (then.ManifestId != manifest.ToString())
                return Stop(new Finding("BAD_MANIFEST", null,
                    $"{report.Manifest} is not the manifest of the context for {scene} at ledger head {manifest.Head}. It belongs to another scene's context, or was not issued by this engine. {AskAgain}"));

            // Freshness by recomputation, before anything is said about the records: if the scene was moved or
            // recast under the draft, that is the thing to say, and the rest follows from it. The budget is left
            // open so that every part is compared, whatever a tighter budget would have left out of the text.
            var current = ContextCompiler.Compile(now, scene, int.MaxValue);
            var changed = Freshness.Differences(then, current);
            if (changed.Count > 0)
            {
                // Every line that differs, up to a point: a scene that has had thousands of records added is
                // better read afresh than listed.
                const int shown = 40;
                var which = changed.Count > shown ? $"{changed.Count} lines that changed, the first {shown} of which follow" : $"{changed.Count} line(s) below";
                return Stop([
                    new Finding("STALE_CONTEXT", null,
                        $"what the context for {scene} says has changed since ledger head {manifest.Head}, where this draft was written from; the ledger is now at {ledger.Count}. Nothing was written. Ask for the context again, check the draft against the {which}, and submit with the new manifest id"),
                    .. changed.Take(shown).Select(line => new Finding("CHANGED", null, line)),
                ]);
            }
            // What a submission may touch: its own scene, in its own span of story time, with its own cast, and
            // the provisional records earlier submissions for this scene left.
            var outside = new List<Finding>();
            foreach (var record in records)
            {
                if (Policy.NotOfScene(now, record, scene) is { } why)
                    outside.Add(new Finding("OUT_OF_SCENE", record.Id,
                        $"it is not a record of {scene}: {why}. A scene submission records what happens in the scene. A change to the story before or after it goes to the author as a proposal"));
                else if (Policy.OutsideCast(now, record, scene) is { Count: > 0 } strangers)
                    outside.Add(new Finding("NOT_IN_CAST", record.Id,
                        $"it has {string.Join(", ", strangers)} present in {scene}, and the cast of {scene} is {string.Join(", ", now.Model.Scenes[scene].Cast)}. The cast is the author's to set"));
            }
            foreach (var id in withdraws)
            {
                var old = before.Find(id);
                if (old is null || !old.LiveAt(before.Head))
                    outside.Add(new Finding("UNKNOWN_REFERENCE", id, "there is no such record in force to withdraw"));
                else if (old.ApprovalAt(before.Head) != Approval.Provisional || ledger[old.CreatedSeq - 1].ChangeSet.Basis?.Scene != scene)
                    outside.Add(new Finding("PROPOSAL_ONLY", id,
                        $"it was not recorded by a submission for {scene}, or the author has ratified it since. Withdrawing it is the author's decision"));
            }
            if (outside.Count > 0)
                return Stop([.. outside]);

            // A fact an embargo holds back, said on the page, with nothing recorded about what the reader is
            // given by it. An embargo is checked against disclosures, so an account of the scene that records
            // the words and leaves the reader out would pass every check and give the fact away. Who said what
            // is something the account has to get right in any case; what the reader takes from it is asked for
            // here, and the embargo then holds against the answer.
            var declared = records.OfType<Disclosure>().Select(d => d.Prop)
                .Concat(now.Model.DisclosuresIn(scene).Where(d => !withdraws.Contains(d.Id)).Select(d => d.Prop)).ToHashSet(StringComparer.Ordinal);
            var undeclared = new List<Finding>();
            foreach (var said in records.OfType<Assertion>().Where(a => !declared.Contains(a.Prop)))
                foreach (var embargo in now.EmbargoesInForce(scene).Where(e => e.Prop == said.Prop))
                    if (now.Canon(said.Prop) switch { "true" => said.Polarity, "false" => !said.Polarity, _ => true })
                        undeclared.Add(new Finding("EMBARGO_UNDECLARED", said.Id, Lint.Undeclared(now, embargo, said)));
            if (undeclared.Count > 0)
                return Stop([.. undeclared]);

            return new Gate([], new ChangeBasis(scene, report.Manifest, manifest.Head, Rebased: manifest.Head != ledger.Count));
        });
        if (!outcome.Result.Applied)
            return new SubmitResult(null, outcome.Result.Findings, [], null);

        var after = StoryIndex.FromFiles(outcome.After!, null, outcome.Manuscript!);
        var notes = new List<Finding>();
        if (after.Contract(scene) is null)
            notes.Add(new Finding("NO_CONTRACT", null, $"{scene} has no contract, so what it does was not compared with what it was meant to do"));
        notes.AddRange(after.CheckContract(scene).Select(f => new Finding(f.Code, f.RecordId, f.Message)));
        notes.AddRange(records.OfType<BeliefChange>().Where(b => b.Directness is Directness.Inferred or Directness.Speculative)
            .Select(b => new Finding("FLAGGED", b.Id, $"marked {Names.Of(b.Directness)}: applied as provisional and flagged for the author's review")));
        // A belief that leaves its holder where an earlier record put them is how a fact that joins two details
        // shows: the scene taught them something, and the fact as worded had no room for it. Said here, while
        // whoever recorded it still has both passages in front of them.
        foreach (var again in records.OfType<BeliefChange>())
        {
            var theirs = after.Model.BeliefsOf(again.Holder, again.Prop).OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
            var at = theirs.FindIndex(b => b.Id == again.Id);
            if (at > 0 && Lint.LearnedAgain(after, theirs[at - 1], theirs[at]) is { } twice)
                notes.Add(new Finding(twice.Code, twice.RecordId, twice.Message));
        }
        // The same join shows from the other side when nobody recorded a belief: a statement that tells nobody
        // anything, because everyone it is said to was already certain.
        notes.AddRange(records.OfType<Assertion>().Select(a => Lint.ToldAgain(after, a)).OfType<LintNote>()
            .Select(told => new Finding(told.Code, told.RecordId, told.Message)));
        // What the records declare is checked above. What the text gives away and nobody declared is not, so the
        // text itself is scanned for the tripwires of the embargoes in force. A hit stops nothing: a hint is
        // allowed, and a pattern of words is not a meaning. It is put in front of the author with the passage.
        // Whether to have the scene audited by a fresh reader is theirs to say: that is another model session.
        if (outcome.Manuscript!.Find(scene) is { } prose)
        {
            var scan = DraftScan.Run(after, scene, DraftScan.Paragraphs(prose), $"manuscript/{prose.File}");
            notes.AddRange(scan.Hits.Select(h => new Finding("TRIPWIRE", h.Embargo,
                $"line {h.Line} of manuscript/{prose.File}: \"{h.Matched}\" matches a tripwire of {h.Embargo}, which holds {h.Prop} back until {h.UntilScene}, in: {h.Excerpt}. A hint is allowed. Show the author the passage; an audit of the scene is theirs to ask for")));
            notes.AddRange(scan.Problems.Select(p => new Finding("TRIPWIRE_NOT_RUN", null, p)));
        }
        return new SubmitResult(outcome.Result.ChangeSet, [], notes, outcome.Basis);
    }

    private static PutResult Refused(string code, string message) => new(null, [new Finding(code, null, message)]);

    /// <summary>What goes wrong when another program holds the index, or two build it at once. None of it touches the truth.</summary>
    private static bool IsIndexTrouble(Exception e) =>
        e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException;

    /// <summary>
    /// Hold the write lock exclusively. Waits a few seconds for another writer, then gives up with null. The
    /// lock file stays where it is between writers: deleting it on release would let a writer that had already
    /// opened the old file and a writer that then created a new one both believe they hold it.
    /// </summary>
    private FileStream? AcquireWriteLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WriteLockPath)!);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return new FileStream(WriteLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
        return null;
    }

    /// <summary>
    /// Throw the index away and build it again from the ledger and the manuscript. This is also how a ledger
    /// that is shorter than this folder has seen it is accepted as it stands: the memory of the longer one
    /// goes with the old index.
    /// </summary>
    public IndexInfo Rebuild()
    {
        using var writeLock = AcquireWriteLock() ?? throw new IOException("Another process is writing to this ledger. Try again in a moment.");
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        MarkSeen(writeLock, ledger.Count);
        StoryIndex.Rebuild(IndexPath, ledger, Projection.Build(ledger.Select(s => s.ChangeSet)), manuscript, seenHead: ledger.Count);
        return StoryIndex.ReadInfo(IndexPath)
               ?? throw new IOException($"The index at {IndexPath} was rebuilt but cannot be read back.");
    }

    /// <summary>
    /// The state as of a ledger head (the latest by default). The answer always describes the ledger and the
    /// manuscript as this call read them. The index is used when it was built from exactly those files, and is
    /// rebuilt first if it was not. If it cannot be rebuilt or read, because another session is doing the same
    /// or another program has the file open, the answer is computed from the files, which gives the same result.
    /// </summary>
    public StoryState State(int? head = null)
    {
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        if (head is int h && (h < 0 || h > ledger.Count))
            throw new ArgumentOutOfRangeException(nameof(head), $"The ledger has {ledger.Count} change sets; there is no head {h}.");
        Projection? projection = null;
        Projection Replay() => projection ??= Projection.Build(ledger.Select(s => s.ChangeSet));
        var info = StoryIndex.ReadInfo(IndexPath);
        var seen = Math.Max(Math.Max(info?.SeenHead ?? 0, SeenMark()), ledger.Count);
        try
        {
            // An index that remembers a shorter ledger than the mark beside the ledger does is behind, even if
            // it was built from these very files: it would answer without the warning.
            if (info?.Matches(ledger, manuscript) != true || info.SeenHead < seen)
                StoryIndex.Rebuild(IndexPath, ledger, Replay(), manuscript, seen, patient: false);
            if (StoryIndex.Load(IndexPath, head, ledger, manuscript) is { } state)
                return state;
        }
        catch (Exception e) when (IsIndexTrouble(e))
        {
        }
        return StoryIndex.FromFiles(Replay(), head, manuscript, seen);
    }

    /// <summary>Every point-in-time answer as one deterministic document.</summary>
    public string Dump(int? head = null) => StateDump.Write(StateDump.Build(State(head)));

    /// <summary>
    /// Re-read the manuscript and report what no longer lines up with the ledger. Nothing in the ledger changes:
    /// the telling order is whatever the files say, so every reader-side answer simply recomputes.
    /// </summary>
    public SyncReport Sync()
    {
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
        // What this folder remembers of the ledger, read before the index is replaced. The files alone cannot
        // show that the newest change sets were removed: what is left is a whole, shorter chain.
        var seen = Math.Max(Math.Max(StoryIndex.ReadInfo(IndexPath)?.SeenHead ?? 0, SeenMark()), ledger.Count);
        var indexed = true;
        try
        {
            // The longest ledger seen is carried into the new index, so the report below is repeated on every
            // sync, and shown in every brief, until the files come back or 'nse rebuild' accepts the loss.
            StoryIndex.Rebuild(IndexPath, ledger, projection, manuscript, seen);
        }
        catch (Exception e) when (IsIndexTrouble(e))
        {
            indexed = false;
        }
        var state = StoryIndex.FromFiles(projection, null, manuscript, seen);
        var model = state.Model;
        var problems = manuscript.Problems.ToList();
        if (state.LedgerWarning is { } shortened)
            problems.Add(shortened);

        var broken = StoryIndex.Evidence(projection, manuscript).Where(e => e.Status != "exact").ToList();
        var inText = manuscript.Scenes.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var noRecord = manuscript.Scenes.Where(s => !model.Scenes.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        var noScene = model.Scenes.Keys.Where(id => !inText.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var ahead = state.EmbargoBreaches()
            .Select(b => $"{b.Disclosure.Id} {(b.Disclosure.Mode == DisclosureMode.Stated ? "states" : "leaves the reader certain of")} {b.Disclosure.Prop} in {b.Disclosure.Scene}; embargo {b.Embargo.Id} holds it back until {b.Embargo.UntilScene}"
                         + (state.Tell(b.Embargo.UntilScene) is null ? ", which is not in the manuscript" : ""))
            .ToList();
        var offContract = manuscript.Scenes.Where(m => model.Contracts.ContainsKey(m.Id) && model.SceneTimes.ContainsKey(m.Id))
            .SelectMany(m => state.CheckContract(m.Id)).Select(f => f.ToString()).ToList();
        return new SyncReport(projection.Head, manuscript.Scenes.Count, manuscript.Hash, problems, broken,
            TimeMismatches(model, manuscript), noRecord, noScene, ahead, offContract, indexed);
    }

    /// <summary>
    /// A scene whose story time was never asserted is taken to follow the scene before it in the telling. If the
    /// files are reordered that assumption may no longer hold, and the engine must say so instead of moving
    /// story time silently. A scene with an asserted time, such as a flashback, is left alone.
    /// </summary>
    private static List<string> TimeMismatches(StoryModel model, ManuscriptSnapshot manuscript)
    {
        var result = new List<string>();
        ManuscriptScene? previous = null;
        foreach (var scene in manuscript.Scenes)
        {
            if (!model.SceneTimes.TryGetValue(scene.Id, out var time) || time.Basis != TimeBasis.FollowsTelling)
                continue;
            if (previous is not null && time.StoryKey <= model.SceneTimes[previous.Id].StoryKey)
                result.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{scene.Id} now comes after {previous.Id} in the telling, but its recorded story time ({time.StoryKey}) is not after that scene's ({model.SceneTimes[previous.Id].StoryKey}). Its time was only ever 'follows the telling': assert a story time for it, or restate it."));
            previous = scene;
        }
        return result;
    }
}
