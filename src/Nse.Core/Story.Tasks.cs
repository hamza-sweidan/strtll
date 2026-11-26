using System.Globalization;
using Nse.Index;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Query;
using Nse.Tasks;
using Nse.Text;
using Nse.Validation;

namespace Nse;

/// <summary>
/// What an agent adds to the task open on a scene: the things only it knows. What it has decided is added to
/// what was decided before. The open questions, the next step and the note on the draft each take the place
/// of the one given before, so they always say how things stand now.
/// </summary>
public sealed record TaskNote
{
    public required string Scene { get; init; }

    /// <summary>Decisions made since the last note, one line each. They are kept in order and never dropped.</summary>
    public IReadOnlyList<string> Decided { get; init; } = [];

    /// <summary>The questions that are open now. Leave out to keep the list as it was; give an empty list when none are left.</summary>
    public IReadOnlyList<string>? OpenQuestions { get; init; }

    /// <summary>What to do next, as a session that knows nothing else would need it said.</summary>
    public string? Next { get; init; }

    /// <summary>Where the draft is and how far it has got.</summary>
    public string? Draft { get; init; }

    /// <summary>
    /// For a note made after the scene was submitted, and required then: whether more of the scene is still to
    /// be written. Whether a task is over cannot be read off what a session happens to note last, so it says.
    /// </summary>
    public bool? More { get; init; }

    /// <summary>Set to close the task without a submission, with the reason: abandoned, or set aside by the author.</summary>
    public string? Close { get; init; }
}

/// <param name="PutAway">Whether the note left the task over, so that it was put away as soon as it was made.</param>
public sealed record TaskNoteResult(TaskRecord? Task, IReadOnlyList<Finding> Findings, bool PutAway = false)
{
    public bool Noted => Task is not null;
}

/// <summary>How old a note is beside the scene's text.</summary>
public enum NoteAge
{
    /// <summary>The text is what it was when the note was made.</summary>
    Current,

    /// <summary>The text has changed since the note was made.</summary>
    Behind,

    /// <summary>The text has changed since the task began, and the note does not say what the text was when it was made.</summary>
    Unknown,
}

/// <summary>
/// A task as a session that picks it up needs to see it: what the record says, set against how things stand
/// now. Whether the context the draft was begun from still holds, whether the scene's text has been written
/// to since, and what of its submission still waits on the author, are worked out when it is asked for.
/// </summary>
/// <param name="Manifest">The manifest of the context the present draft was begun from. Null if none was taken in this task.</param>
/// <param name="FromSubmission">Whether that manifest is the one the scene was last submitted with, no context having been taken since.</param>
/// <param name="ContextProblem">Why that context could not be compared with the present, if it could not.</param>
/// <param name="Retake">Whether taking the context again is a way out of the problem.</param>
/// <param name="ContextChanges">The lines that differ now from what that context said. Empty if it still stands.</param>
/// <param name="LaterManifest">The manifest of the last context taken after that one, if the context was taken again and came out different.</param>
/// <param name="Marked">Whether the scene has a marker in the manuscript now.</param>
/// <param name="TextChanged">Whether the scene's text differs from what it was when the scene was last submitted, or failing that when the task's first context was taken.</param>
/// <param name="Submitted">The change set of the latest submission that was applied, if any was.</param>
/// <param name="Missing">Whether a change set the task submitted is not in the ledger as it stands.</param>
/// <param name="Waiting">The records the task submitted that still await the author's decision.</param>
/// <param name="Standing">Whether anything the task submitted is in force now.</param>
/// <param name="Refused">The findings of the latest submission, if it was refused and nothing was applied after it.</param>
/// <param name="More">Whether the session said, after its last submission, that more of the scene is still to be written.</param>
/// <param name="Worked">Whether the task holds more than contexts: a note, a submission or a refusal.</param>
/// <param name="Finished">Whether the task's work is over: see <see cref="Story.Tasks"/>.</param>
/// <param name="LastHead">The ledger head at the task's latest step.</param>
/// <param name="Tell">The scene's place in the telling, for putting tasks in order.</param>
public sealed record TaskView(
    TaskRecord Task, string? Title, string Pov,
    string? Manifest, bool FromSubmission, int ContextHead, int LedgerHead, string? ContextProblem, bool Retake, IReadOnlyList<string> ContextChanges,
    string? LaterManifest, int LaterHead,
    string? Contract, string? Outline,
    string? File, bool Marked, int LineStart, int LineEnd, bool TextChanged,
    string? Submitted, bool Missing, IReadOnlyList<string> Waiting, bool Standing, IReadOnlyList<string> Refused,
    IReadOnlyList<string> Decided, IReadOnlyList<string> OpenQuestions, string? Next, string? Draft,
    NoteAge OpenQuestionsAge, NoteAge NextAge, NoteAge DraftAge,
    bool More, bool Worked, bool Finished, int LastHead, int Tell)
{
    /// <summary>
    /// Whether there is anything here for a session to pick up. A context that was taken and nothing more,
    /// with the scene's text as it was, is a look and not work. A task that is finished is waiting only to
    /// be put away.
    /// </summary>
    public bool InProgress => !Finished && (Worked || TextChanged);
}

/// <summary>
/// What is open under work/tasks, for the brief: every task as it stands; the files there that are not tasks;
/// those that could not be opened just now; those that are tasks for scenes the story does not have; and why
/// none of it could be read, if it could not.
/// </summary>
public sealed record WorkInProgress(
    IReadOnlyList<TaskView> Tasks, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Busy, IReadOnlyList<string> Strays, string? Problem)
{
    public WorkInProgress(IReadOnlyList<TaskView> tasks) : this(tasks, [], [], [], null)
    {
    }
}

public sealed partial class Story
{
    public string WorkDir => Path.Combine(Root, "work");

    /// <summary>
    /// The context for a scene as things stand, taken to work from. It is compiled as <see cref="ContextCompiler.Compile"/>
    /// compiles it, and the scene's task notes that it was taken: which manifest, and what the scene's text was
    /// at that moment. Nothing has to be remembered by whoever asks. If a session stops after this and before
    /// anything else, what it leaves behind is enough to see that a context was taken and from what.
    /// </summary>
    /// <remarks>
    /// The text ends by saying what to do before any prose is written: note the plan. A session that drafts
    /// first and notes afterwards leaves nothing behind if it stops in between, and left to itself that is what
    /// a session does. The words stand at the end of the context because that is the last
    /// thing read before drafting begins. If the task could not be kept, they say that instead.
    /// </remarks>
    public ContextPackage TakeContext(string scene, int budget = ContextCompiler.DefaultBudget, string actor = "agent")
    {
        var state = State();
        var package = ContextCompiler.Compile(state, scene, budget, NoteFirst);
        var prose = state.Telling.FirstOrDefault(t => t.Id == scene);
        // A task whose work is over is put away first, so that this context opens the next one.
        PutAwayFinished();
        var problem = Keep(scene, actor, open: true, task =>
        {
            // Asked again with nothing moved since the last time: there is nothing to add.
            if (task?.Steps[^1] is { Step: StepKind.Context } last && last.Manifest == package.ManifestId && last.TextHash == prose?.Hash)
                return null;
            return Step(StepKind.Context, package.Head) with
            {
                Manifest = package.ManifestId, File = prose?.File, TextHash = prose?.Hash,
                // Kept as a convenience, and only if they are ids of the shape a task holds. A ledger from
                // before ids were held to plain letters may name them otherwise, and the task does without.
                Pov = TaskStore.Plainly(state.Model.Scenes[scene].Pov), Contract = TaskStore.Plainly(state.Contract(scene)?.Id),
            };
        });
        if (problem is null)
            return package;
        var text = package.Text[..^NoteFirst.Length] + NotKept(problem);
        return package with { Text = text, EstimatedTokens = ContextCompiler.EstimateTokens(text) };
    }

    /// <summary>What a context taken to work from says last.</summary>
    public const string NoteFirst =
        "\n\nA task is open for this scene. Before you write or change any prose, call task_note with your plan: " +
        "what you have decided, the questions still open, and the next step. Call it again each time you decide something more. " +
        "A session can end at any moment without warning, and a later one has nothing but these notes to go on.";

    /// <summary>
    /// What it says instead when the task could not be written. Never longer than <see cref="NoteFirst"/>, so the
    /// budget still holds. A reason too long for the room keeps its start and its end, which is where a path
    /// says what it is.
    /// </summary>
    public static string NotKept(string problem)
    {
        const string Before = "\n\nNo task could be kept for this scene: ";
        const string After = ". What you note with task_note may not reach a later session. Tell the author.";
        const string Cut = " ... ";
        var room = NoteFirst.Length - Before.Length - After.Length;
        problem = (problem ?? "").TrimEnd('.', ' ');
        if (problem.Length > room)
        {
            var keep = room - Cut.Length;
            problem = problem[..(keep / 2)] + Cut + problem[^(keep - keep / 2)..];
        }
        return Before + problem + After;
    }

    /// <summary>
    /// Add to the task open on a scene what only the agent knows: what it decided, what is still open, what
    /// comes next, how far the draft has got. A task opens when the context for its scene is taken, so there
    /// is one to add to as soon as there is work.
    /// </summary>
    public TaskNoteResult Note(TaskNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var findings = new List<Finding>();
        void Line(string? text, string what)
        {
            if (TaskStore.LineProblem(text) is { } problem)
                findings.Add(new Finding("BAD_VALUE", null, $"{what} {problem}"));
        }
        var decided = note.Decided ?? [];
        if (decided.Count > MaxEntries || note.OpenQuestions?.Count > MaxEntries)
            findings.Add(new Finding("BAD_VALUE", null, $"a note carries at most {MaxEntries} decisions and {MaxEntries} open questions at a time"));
        Line(note.Scene, "the scene");
        foreach (var d in decided)
            Line(d, "a decision");
        foreach (var q in note.OpenQuestions ?? [])
            Line(q, "an open question");
        if (note.Next is not null)
            Line(note.Next, "the next step");
        if (note.Draft is not null)
            Line(note.Draft, "the note on the draft");
        if (note.Close is not null)
            Line(note.Close, "the reason for closing");
        if (note.Close is not null && note.More is not null)
            findings.Add(new Finding("BAD_VALUE", null, "a note that closes the task has nothing to say about more to come. Leave more out"));
        if (findings.Count > 0)
            return new TaskNoteResult(null, findings);
        if (decided.Count == 0 && note.OpenQuestions is null && note.Next is null && note.Draft is null && note.Close is null && note.More is null)
            return new TaskNoteResult(null, [new Finding("EMPTY", null, "the note says nothing: no decision, no open questions, no next step, nothing about the draft")]);

        // What the scene's text is as the note is made, so that a later reader can be told when the text has
        // moved on since. A manuscript that another process has open this instant is tried again. If it still
        // cannot be read the note is made all the same, and the brief says that its age is not known.
        string? text = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                text = ReadManuscript().Find(note.Scene)?.Hash;
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(40);
            }
        }

        TaskRecord noted;
        try
        {
            using var taskLock = AcquireTaskLock();
            if (taskLock is null)
                return new TaskNoteResult(null, [new Finding("LOCKED", null, "another process is writing to this story's tasks. Try again in a moment")]);
            var (task, unreadable) = TaskStore.Look(Root, note.Scene);
            if (task is null)
                return new TaskNoteResult(null, [new Finding("NO_TASK", null, NoTask(note.Scene, unreadable))]);
            // Room is always left for the two steps that close a task, so that closing it stays possible.
            if (note.Close is null && task.Steps.Count >= TaskStore.MaxSteps - 2)
                return new TaskNoteResult(null, [new Finding("TASK_FULL", null,
                    $"the task for {note.Scene} holds as many steps as a task may. Close it with a note that says why, and take the scene's context again")]);
            // Once the scene has been submitted, whether the task is over turns on whether more is to come,
            // and only the session knows that. What it notes last says nothing either way, so it is asked.
            var submitted = task.Steps.Any(s => s.Step == StepKind.Submitted);
            if (note.Close is null && submitted && note.More is null)
                return new TaskNoteResult(null, [new Finding("MORE_REQUIRED", null,
                    $"{note.Scene} has been submitted in this task, so a note made now has to say whether more of the scene is still to be written: more true if it is, false if not. " +
                    "With false the task is put away once the author has decided on what was submitted; with true it stays open")]);
            if (!submitted && note.More is not null)
                return new TaskNoteResult(null, [new Finding("BAD_VALUE", null,
                    "more says whether more of the scene is to come after a submission, and nothing has been submitted in this task yet. Leave it out, and say it in a note made after you submit")]);
            var head = ReadLedger().Count;
            noted = task.With(Step(StepKind.Note, head) with
            {
                Decided = decided.Count > 0 ? decided : null, OpenQuestions = note.OpenQuestions, Next = note.Next, Draft = note.Draft,
                More = note.More, TextHash = text,
            });
            if (note.Close is null)
                TaskStore.Save(Root, noted);
            else
            {
                noted = noted.With(Step(StepKind.Closed, head) with { Reason = note.Close });
                TaskStore.Close(Root, noted);
                return new TaskNoteResult(noted, []);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TaskNoteResult(null, [new Finding("NOT_WRITTEN", null, $"the note could not be written under work/tasks: {Plain.OneLine(e.Message)}")]);
        }
        // A note that says nothing more is to come, made when the author has already decided, leaves the task over.
        return new TaskNoteResult(noted, [], PutAway: PutAwayFinished().Contains(note.Scene));
    }

    /// <summary>Why a note found no task to go to. If the scene's last task was put away, that is what the session needs to be told.</summary>
    private string NoTask(string scene, bool unreadable)
    {
        var said = $"no task is open for {Plain.OneLine(scene)}. ";
        if (unreadable)
            return said + "There is a file where its task would be, and it cannot be read as one. Taking the scene's context to work from moves the file aside and opens a task";
        if (TaskStore.LastClosed(Root, scene) is { } last && last.Steps[^1] is { Step: StepKind.Closed, Reason: { } reason })
            return said + $"Its last task, {last.Id}, was put away: {reason}. If there is more to do on the scene, take its context again, which opens a new task, and note from there";
        return said + "A task opens when the context for a scene is taken to work from: ask for the scene's context first, and note what you decide from then on";
    }

    /// <summary>How long one line of a note may be, and how many lines of a kind one note may carry.</summary>
    public const int MaxNote = TaskStore.MaxLine, MaxEntries = TaskStore.MaxEntries;

    /// <summary>
    /// What is open under work/tasks, each task set against how things stand now. Nothing is written. Nothing
    /// that is wrong there stops the answer: the brief is the first thing every session asks for, and working
    /// notes are not worth losing it over.
    /// </summary>
    /// <remarks>
    /// A task's work is over when everything it submitted is settled, each change set it submitted is still in
    /// the ledger, the scene's text is as it was submitted, no later submission was refused, and the session
    /// did not say after its last submission that more was to come. Such a task is not shown. It is put away
    /// the next time anything writes to the tasks.
    /// </remarks>
    public WorkInProgress Work()
    {
        try
        {
            var open = TaskStore.AllOpen(Root);
            if (open.Tasks.Count == 0)
                return new WorkInProgress([], open.Unreadable, open.Busy, [], null);
            var ledger = ReadLedger();
            var manuscript = ReadManuscript();
            var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
            var now = StoryIndex.FromFiles(projection, null, manuscript);
            // A task for a scene the story has neither a marker nor a record for is about nothing in it.
            var strays = open.Tasks.Where(t => manuscript.Find(t.Scene) is null && !now.Model.Scenes.ContainsKey(t.Scene)).ToList();
            return new WorkInProgress(
                open.Tasks.Except(strays).Select(task => Seen(task, ledger, projection, manuscript, now)).ToList(),
                open.Unreadable, open.Busy, strays.Select(t => TaskStore.FileName(t.Scene) + ".json").ToList(), null);
        }
#pragma warning disable CA1031 // Whatever went wrong, the brief goes out without this part and says why.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new WorkInProgress([], [], [], [], Plain.OneLine(e.Message));
        }
    }

    /// <summary>Every task that is open, each set against how things stand now. Nothing is written.</summary>
    public IReadOnlyList<TaskView> Tasks() => Work().Tasks;

    private static TaskView Seen(
        TaskRecord task, IReadOnlyList<StoredChangeSet> ledger, Projection projection, ManuscriptSnapshot manuscript, StoryState now)
    {
        var scene = task.Scene;
        var steps = task.Steps;
        var prose = manuscript.Find(scene);
        var record = now.Model.Scenes.GetValueOrDefault(scene);

        // The latest submission that was applied, and what came after it.
        var submittedAt = steps.ToList().FindLastIndex(s => s.Step == StepKind.Submitted);
        var lastSubmitted = submittedAt >= 0 ? steps[submittedAt] : null;
        var after = steps.Skip(submittedAt + 1).ToList();

        // Whether the text has been written to: since it was last submitted, or since the task's first context.
        // A context taken again later never moves what the text is measured from. If it did, the step that
        // restores a session, taking the context again, would wipe out what the next session needs to be told.
        var baseline = lastSubmitted?.TextHash ?? steps.FirstOrDefault(s => s.Step == StepKind.Context)?.TextHash;
        var textChanged = prose is not null && baseline is not null && prose.Hash != baseline;

        // The context the present draft was begun from. While nothing has been written since the baseline,
        // that is simply the latest context taken: there is no draft for an older one to have misled. Once
        // the text has changed, it is the last context taken while the text was still as it had been, before
        // any step saw it changed. A later one does not take its place: the draft was not written from it, and
        // nobody has said the draft was checked against it. After a submission with no context since, it is
        // the one submitted with.
        var contexts = after.Where(s => s.Step == StepKind.Context).ToList();
        var seenChanged = after.FindIndex(s => s.TextHash is not null && s.TextHash != baseline);
        var beforeChange = seenChanged < 0 ? after : after.Take(seenChanged).ToList();
        (string? Manifest, int Head) FromSubmission() =>
            lastSubmitted?.Manifest is { } m && ManifestId.Parse(m) is { } at ? (m, at.Head) : (null, 0);
        var fromSubmission = false;
        (string? Manifest, int Head) begun;
        if (!textChanged && contexts.Count > 0)
            begun = (contexts[^1].Manifest, contexts[^1].AtHead);
        else if (textChanged && beforeChange.LastOrDefault(s => s.Step == StepKind.Context && s.TextHash == baseline) is { } clean)
            begun = (clean.Manifest, clean.AtHead);
        else if (lastSubmitted is not null)
            (begun, fromSubmission) = (FromSubmission(), true);
        else
            begun = contexts.Count > 0 ? (contexts[0].Manifest, contexts[0].AtHead) : (null, 0);
        var later = textChanged && contexts.Count > 0 && contexts[^1].Manifest != begun.Manifest ? contexts[^1] : null;

        // What the notes come to: decisions add up; the rest say how things stood at the last note that spoke of
        // them. For each of those, what the text was when it was said is kept beside it.
        var decided = new List<string>();
        IReadOnlyList<string> open = [];
        string? next = null, draft = null, openText = null, nextText = null, draftText = null;
        foreach (var step in steps.Where(s => s.Step == StepKind.Note))
        {
            decided.AddRange(step.Decided ?? []);
            if (step.OpenQuestions is not null)
                (open, openText) = (step.OpenQuestions, step.TextHash);
            if (step.Next is not null)
                (next, nextText) = (step.Next, step.TextHash);
            if (step.Draft is not null)
                (draft, draftText) = (step.Draft, step.TextHash);
        }
        // A note is behind the text if the text is not what it was when the note was made. A note that does
        // not say what the text was is of unknown age once the text has changed at all.
        NoteAge Age(string? then) =>
            prose is null ? NoteAge.Current
            : then is null ? textChanged ? NoteAge.Unknown : NoteAge.Current
            : prose.Hash != then ? NoteAge.Behind : NoteAge.Current;

        // What the task submitted: what of it still waits on the author, and whether any of it is in force at all.
        var submissions = steps.Where(s => s.Step == StepKind.Submitted && s.ChangeSet is not null).ToList();
        var mine = submissions.Select(s => ledger.FirstOrDefault(c => c.ChangeSet.Id == s.ChangeSet)).OfType<StoredChangeSet>().ToList();
        var missing = mine.Count < submissions.Count;
        var waiting = mine.SelectMany(c => Awaiting(projection, c)).Select(t => t.Record.Id).Distinct(StringComparer.Ordinal).ToList();
        var standing = mine.Any(c => c.ChangeSet.Ops.OfType<PutOp>().Select(put => projection.Find(put.Record.Id))
            .Any(t => t is not null && t.CreatedSeq == c.ChangeSet.Seq && t.LiveAt(projection.Head)));
        var refused = after.LastOrDefault(s => s.Step == StepKind.Refused);
        // Whether more is to come is what the session said last, after its last submission. Said before a
        // submission it would be a plan, and a plan is carried out by submitting.
        var more = lastSubmitted is not null && after.LastOrDefault(s => s.Step == StepKind.Note && s.More is not null)?.More == true;

        // Whether what the draft's context said still stands, by the same comparison a submission is held to.
        string? problem = null;
        var retake = true;
        IReadOnlyList<string> changes = [];
        var manifest = begun.Manifest;
        if (prose is null)
            (problem, retake) = ("the scene has no marker in the manuscript now", false);
        else if (record is null)
            (problem, retake) = ("the scene has no record now", false);
        else if (manifest is null)
        {
            // No context was taken in this task and no manifest was kept from a submission. There is nothing to compare.
        }
        else if (ManifestId.Parse(manifest) is not { } id)
            problem = "the task holds no manifest";
        else if (id.Head > projection.Head)
            problem = string.Create(CultureInfo.InvariantCulture, $"it names ledger head {id.Head}, and the ledger ends at {projection.Head}");
        else if (ContextCompiler.OrderMark(now) != id.Order)
            changes = ["the scenes in the manuscript are not the scenes, in the order, that they were"];
        else
        {
            try
            {
                var then = ContextCompiler.Compile(StoryIndex.FromFiles(projection, id.Head, manuscript), scene, int.MaxValue);
                if (then.ManifestId != manifest)
                    problem = "it is not the manifest of this scene's context at that head";
                else
                    changes = Freshness.Differences(then, ContextCompiler.Compile(now, scene, int.MaxValue));
            }
            catch (KeyNotFoundException e)
            {
                problem = e.Message.TrimEnd('.', ' ');
            }
        }

        var contract = now.Contract(scene);
        var pov = record?.Pov ?? steps.LastOrDefault(s => s.Pov is not null)?.Pov ?? "";
        return new TaskView(task, record?.Title ?? prose?.Title, now.Model.Entity(pov) is null ? pov : $"{now.Short(pov)} ({pov})",
            manifest, fromSubmission, begun.Head, projection.Head, problem, retake, changes,
            later?.Manifest, later?.AtHead ?? 0,
            contract?.Id, contract?.Outline,
            prose?.File ?? steps.LastOrDefault(s => s.File is not null)?.File, prose is not null, prose?.LineStart ?? 0, prose?.LineEnd ?? 0, textChanged,
            lastSubmitted?.ChangeSet, missing, waiting, standing, refused?.Codes ?? [],
            decided, open, next, draft, Age(openText), Age(nextText), Age(draftText),
            More: more,
            Worked: steps.Any(s => s.Step is StepKind.Note or StepKind.Submitted or StepKind.Refused),
            Finished: lastSubmitted is not null && !missing && waiting.Count == 0 && !textChanged && !more && refused is null,
            LastHead: steps[^1].AtHead, Tell: now.Tell(scene) ?? int.MaxValue);
    }

    private static TaskStep Step(StepKind kind, int head) => new()
    {
        Step = kind, AtHead = head, Time = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Add a step to the task open on a scene, opening one if there is none. This is bookkeeping beside the
    /// work, never a condition of it: if the task cannot be written, the context was still compiled and the
    /// submission still applied. What comes back is why it could not be written, so that whoever asked can be
    /// told, or null if there was nothing to tell.
    /// </summary>
    /// <param name="next">Given the task as it is, or null if none is open, the step to add; or null to add nothing.</param>
    /// <param name="open">Whether to open a task if none is open.</param>
    private string? Keep(string scene, string actor, bool open, Func<TaskRecord?, TaskStep?> next)
    {
        try
        {
            if (!TaskStore.Nameable(scene))
                return "the scene's id is not one a task can be named for";
            // With no task to add to and none to be opened, nothing is touched: not even the folder is made.
            if (!open && !Directory.Exists(TaskStore.Dir(Root)))
                return null;
            using var taskLock = AcquireTaskLock();
            if (taskLock is null)
                return "another process is writing to this story's tasks";
            var (task, unreadable) = TaskStore.Look(Root, scene);
            if (unreadable)
            {
                // A file sits where the task would be and is not one. It is not ours to add to. If a task has
                // to be written there it is moved aside, never written over.
                if (!open)
                    return null;
                TaskStore.SetAside(Root, scene);
            }
            if (task is null && !open)
                return null;
            if (task is not null && task.Steps.Count >= TaskStore.MaxSteps - 2)
                return "the task holds as many steps as a task may. Close it with a note that says why, and take the context again";
            if (next(task) is not { } step)
                return null;
            task = task is null
                ? new TaskRecord
                {
                    Id = TaskStore.IdFor(scene, TaskStore.NextNumber(Root, scene)), Scene = scene, Actor = TaskStore.ActorName(actor), Steps = [step],
                }
                : task.With(step);
            TaskStore.Save(Root, task);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LedgerException)
        {
            return Plain.OneLine(e.Message);
        }
    }

    /// <summary>
    /// What a submission came to, noted in the scene's task: the change set it became, or what it was refused
    /// with. One that was applied opens a task if none is open, because it is work. One that was refused does
    /// not: anything can be sent and refused, and a task is not opened on the strength of it.
    /// </summary>
    /// <returns>Why the task could not be written, if a submission was applied and it could not.</returns>
    private string? NoteSubmission(SceneReport report, SubmitResult result)
    {
        try
        {
            if (!TaskStore.Nameable(report.Scene) || ReadManuscript().Find(report.Scene) is not { } prose)
                return null;
            // Only what could be a manifest id is kept. Whatever else was sent in its place is not ours to store.
            var manifest = TaskStore.ManifestShape(report.Manifest) ? report.Manifest : null;
            IReadOnlyList<string> Kept(IEnumerable<string> codes) =>
                codes.Select(TaskStore.Plainly).OfType<string>().Distinct(StringComparer.Ordinal).Take(TaskStore.MaxCodes).ToList();
            if (result.ChangeSet is not { } stored)
            {
                Keep(report.Scene, report.Actor, open: false, _ => Step(StepKind.Refused, ReadLedger().Count) with
                {
                    Manifest = manifest, Codes = Kept(result.Findings.Select(f => f.Code).Where(c => c != "CHANGED")),
                });
                return null;
            }
            var problem = Keep(report.Scene, report.Actor, open: true, _ => Step(StepKind.Submitted, stored.ChangeSet.Seq) with
            {
                ChangeSet = stored.ChangeSet.Id, Manifest = manifest, TextHash = prose.Hash, Codes = Kept(result.Notes.Select(n => n.Code)),
            });
            // Other scenes' tasks may be over now. This scene's own is left for its session to say, in its next
            // note, whether more is to come.
            PutAwayFinished(except: report.Scene);
            return problem;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LedgerException)
        {
            return result.Applied ? Plain.OneLine(e.Message) : null;
        }
    }

    /// <summary>
    /// What the author decided, noted in every task whose submission it touched. A task whose work is over is
    /// then put away. If its session has gone on working, or said that more is to come, the decision is noted
    /// and the task stays open: closing it would take its notes out of the brief while the work they describe
    /// is unfinished.
    /// </summary>
    private void NoteDecision(DecisionResult result)
    {
        try
        {
            var decided = result.Applied.SelectMany(s => s.ChangeSet.Ops)
                .SelectMany(op => op switch { RatifyOp r => r.Ids, RejectOp r => r.Ids, _ => (IReadOnlyList<string>)[] })
                .ToHashSet(StringComparer.Ordinal);
            if (decided.Count == 0)
                return;
            var open = TaskStore.AllOpen(Root).Tasks;
            if (open.Count == 0)
                return;
            var ledger = ReadLedger();
            var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in open)
            {
                var mine = candidate.Steps.Where(s => s.Step == StepKind.Submitted && s.ChangeSet is not null)
                    .Select(s => ledger.FirstOrDefault(c => c.ChangeSet.Id == s.ChangeSet)).OfType<StoredChangeSet>().ToList();
                if (!mine.SelectMany(c => c.ChangeSet.Ops.OfType<PutOp>()).Any(put => decided.Contains(put.Record.Id)))
                    continue;
                using var taskLock = AcquireTaskLock();
                if (taskLock is null || TaskStore.Look(Root, candidate.Scene).Task is not { } task || task.Steps.Count >= TaskStore.MaxSteps - 2)
                    continue;
                // For a decision, the codes are the records of the task that still wait.
                var waiting = mine.SelectMany(c => Awaiting(projection, c)).Select(t => TaskStore.Plainly(t.Record.Id)).OfType<string>()
                    .Distinct(StringComparer.Ordinal).Take(TaskStore.MaxCodes).ToList();
                TaskStore.Save(Root, task.With(Step(StepKind.Decided, projection.Head) with { ChangeSet = result.Applied[0].ChangeSet.Id, Codes = waiting }));
                touched.Add(candidate.Scene);
            }
            PutAwayFinished(decidedFor: touched);
        }
#pragma warning disable CA1031 // The decision is written. Nothing that goes wrong in noting it may be reported as the decision failing.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Put away every open task whose work is over. It is done whenever something writes to the tasks or to
    /// the ledger, and not when the brief is read, which writes nothing. A task left open after its work was
    /// over would be taken for work again the day the author edited its scene.
    /// </summary>
    /// <param name="except">A scene whose task is left alone, because its session has just acted and has yet to say what comes next.</param>
    /// <param name="decidedFor">The scenes whose tasks a decision of the author's has just been noted in.</param>
    /// <returns>The scenes whose tasks were put away.</returns>
    private IReadOnlyList<string> PutAwayFinished(string? except = null, IReadOnlySet<string>? decidedFor = null)
    {
        var closed = new List<string>();
        try
        {
            var open = TaskStore.AllOpen(Root).Tasks.Where(t => t.Scene != except && t.Steps.Any(s => s.Step == StepKind.Submitted)).ToList();
            if (open.Count == 0)
                return closed;
            var ledger = ReadLedger();
            var manuscript = ReadManuscript();
            var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
            var now = StoryIndex.FromFiles(projection, null, manuscript);
            foreach (var candidate in open)
            {
                using var taskLock = AcquireTaskLock();
                if (taskLock is null)
                    return closed;
                // Read again under the lock: it may have been noted in, or put away, since it was listed.
                if (TaskStore.Look(Root, candidate.Scene).Task is not { } task || !Seen(task, ledger, projection, manuscript, now).Finished)
                    continue;
                TaskStore.Close(Root, task.With(Step(StepKind.Closed, projection.Head) with
                {
                    Reason = decidedFor?.Contains(task.Scene) == true
                        ? "everything it submitted has been decided by the author"
                        : "nothing it submitted awaits the author, and nothing more was to be written",
                }));
                closed.Add(task.Scene);
            }
        }
#pragma warning disable CA1031 // Tidying. Whatever it was called after has been done and is reported as done.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        return closed;
    }

    /// <summary>
    /// Tasks have a lock of their own. They are written on the way past by commands that only read the story,
    /// and those must not queue behind whoever is appending to the ledger.
    /// </summary>
    private FileStream? AcquireTaskLock()
    {
        var path = TaskStore.LockPath(Root);
        var first = !Directory.Exists(TaskStore.Dir(Root));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (first && File.Exists(Path.Combine(Root, ".gitignore")))
            EnsureLines(Path.Combine(Root, ".gitignore"), WorkIgnored);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    private static readonly (string Comment, string Line) WorkIgnored =
        ("# What a session was in the middle of: working notes, local to this machine and not part of the story.", "work/tasks/");
}
