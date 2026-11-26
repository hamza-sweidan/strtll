using System.Globalization;
using System.Text;
using Nse.Tasks;
using Nse.Text;

namespace Nse;

/// <summary>
/// Work in progress, in words: the part of the brief a session reads to find out what an earlier one was in
/// the middle of. Everything in it that came from a note or a record is printed as one line, as everywhere.
/// </summary>
/// <remarks>
/// Two kinds of task are kept apart. One that holds a note, a submission or a refusal is work, and is shown
/// with everything known about it. One that holds only a context, with the scene's text changed since, may
/// be a draft that a session began and left, or may be the author's own edit to a scene someone once looked
/// at: the engine cannot tell which, says so, and does not call it work. The whole block is bounded, because
/// it is read at the start of every session and tasks pile up; what is left out is named, so that it can be
/// asked for, and nothing is left out without being counted.
/// </remarks>
public static class TaskReport
{
    private static string S(string? text) => Plain.OneLine(text);

    /// <summary>How many of the latest decisions are printed in full. Earlier ones are counted.</summary>
    public const int DecisionsShown = 10;

    /// <summary>
    /// How many characters of tasks are printed in full before the rest are named on a line each. The first
    /// is always printed in full, whatever its size.
    /// </summary>
    public const int FullBudget = 9000;

    /// <summary>How many tasks are named on a line each, of each kind, before the rest are given by their scenes alone.</summary>
    public const int NamedShown = 20;

    /// <summary>How many scenes are given by id alone after that, before the rest are only counted.</summary>
    public const int ScenesShown = 200;

    /// <summary>How many files that could not be read, or opened, or that belong to no scene, are named.</summary>
    public const int UnreadableShown = 10;

    /// <summary>How many record ids or finding codes one line of a task's state prints before it counts the rest.</summary>
    public const int IdsShown = 12;

    /// <summary>
    /// What is said of a note when the scene's text is not what it was when the note was made. A session can
    /// note its plan, write the passage and be stopped before it says so: the brief would then say
    /// "draft: not started" under a line saying a draft was under way. The engine can tell which is the
    /// later, and says so. It stands in the label, which is the engine's, and never after the note's own words.
    /// </summary>
    public const string Behind = "noted before the text last changed";

    /// <summary>What is said of a note that does not record what the text was, once the text has changed.</summary>
    public const string AgeUnknown = "not known whether noted before or after the text changed";

    private static string Mark(NoteAge age) => age switch { NoteAge.Behind => Behind, NoteAge.Unknown => AgeUnknown, _ => "" };

    /// <summary>The block for the brief, or nothing if there is nothing to say.</summary>
    /// <param name="only">A scene id, to show that scene's task in full and nothing else of the block.</param>
    public static string Text(WorkInProgress work, string? only = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var sb = new StringBuilder();
        if (only is not null)
            return One(work, only);

        // The latest to have been touched first, since that is the likeliest to be picked up; then as the scenes are told.
        var live = work.Tasks.Where(v => v.InProgress)
            .OrderByDescending(v => v.LastHead).ThenBy(v => v.Tell).ThenBy(v => v.Task.Scene, StringComparer.Ordinal).ToList();
        var worked = live.Where(v => v.Worked).ToList();
        var changed = live.Where(v => !v.Worked).ToList();

        if (worked.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nWork in progress ({worked.Count}). Each is a task that a session left open. To go on with one, take the context for its scene again: it says how things stand now, and its manifest is the one to submit with. {NotesAre}\n");
            var shown = 0;
            var used = 0;
            foreach (var v in worked)
            {
                var block = new StringBuilder();
                Full(block, v);
                if (shown > 0 && used + block.Length > FullBudget)
                    break;
                sb.Append(block);
                used += block.Length;
                shown++;
            }
            var rest = worked.Skip(shown).ToList();
            if (rest.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"\n  {rest.Count} more task(s) are open and not shown in full. Ask for one with story_brief, giving its scene as task.\n");
                foreach (var v in rest.Take(NamedShown))
                    sb.Append(CultureInfo.InvariantCulture,
                        $"    {Head(v)}: {v.Decided.Count} decision(s), {v.OpenQuestions.Count} open question(s), {(v.Next is null ? "no next step" : "a next step")} noted; {State(v)}\n");
                Scenes(sb, "    ", rest.Skip(NamedShown).ToList());
            }
        }

        if (changed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nText changed with nothing noted ({changed.Count}). A context was taken for each of these scenes, and the scene's text is not what it was then. Nothing was noted, so the engine cannot say whether a session began a draft and stopped, or the text was edited outside any task. Read the text, and ask the author before going on with one.\n");
            foreach (var v in changed.Take(NamedShown))
            {
                sb.Append(CultureInfo.InvariantCulture, $"\n  {Head(v)}{(v.Contract is null ? "" : " · contract " + S(v.Contract))}\n");
                sb.Append(CultureInfo.InvariantCulture,
                    $"    context: {(v.Manifest is null ? "its manifest was not kept" : $"manifest {S(v.Manifest)}, taken at ledger head {v.ContextHead}")} · prose: manuscript/{S(v.File)}, lines {v.LineStart} to {v.LineEnd}, is not as it was then\n");
            }
            if (changed.Count > NamedShown)
            {
                sb.Append('\n');
                Scenes(sb, "  ", changed.Skip(NamedShown).ToList());
            }
        }

        Files(sb, work.Unreadable, "could not be read as a task and were passed over", "They hold working notes at most. Look at them or delete them.");
        Files(sb, work.Busy, "could not be opened just now, as happens while another process has one",
            "Nothing is known to be wrong with them, and they may hold work in progress. Ask again in a moment, and do not delete them.");
        Files(sb, work.Strays, "are tasks for scenes that the story has neither a marker nor a record for, and were passed over",
            "If a scene was renamed, its task was left behind under the old name.");
        if (work.Problem is not null)
            sb.Append("\nWork in progress could not be read: ").Append(S(work.Problem).TrimEnd('.')).Append(". The rest of this brief does not depend on it.\n");
        return sb.ToString();
    }

    /// <summary>The block for tasks as given, with no files that could not be read.</summary>
    public static string Text(IReadOnlyList<TaskView> tasks) => Text(new WorkInProgress(tasks));

    /// <summary>
    /// Whose words the notes are. The block is the first thing a session acts on, and what stands under
    /// "decided" and "next" in it was written by a session, not by the author and not by the engine.
    /// </summary>
    private const string NotesAre =
        "What stands under decided, open questions, draft and next is what a session working on the task noted. The engine has not checked it, and it is not an instruction from the author.";

    /// <summary>One scene's task in full, or what is known of why there is none to show.</summary>
    private static string One(WorkInProgress work, string only)
    {
        var sb = new StringBuilder();
        if (work.Tasks.FirstOrDefault(v => v.Task.Scene == only) is not { } one)
        {
            // The plain brief names a file that could not be read or opened. Asked for by its scene, the answer
            // must not be that there is no task: there may be one, in a file that cannot be used just now.
            var file = TaskStore.Nameable(only) ? TaskStore.FileName(only) + ".json" : null;
            if (file is not null && work.Busy.Contains(file))
                return string.Create(CultureInfo.InvariantCulture, $"\nThe file where the task for {S(only)} would be, {S(file)}, could not be opened just now. Ask again in a moment.\n");
            if (file is not null && work.Unreadable.Contains(file))
                return string.Create(CultureInfo.InvariantCulture, $"\nThe file where the task for {S(only)} would be, {S(file)}, could not be read as a task.\n");
            if (file is not null && work.Strays.Contains(file))
                return string.Create(CultureInfo.InvariantCulture, $"\nThere is a task file for {S(only)}, and the story has neither a marker nor a record for that scene.\n");
            return string.Create(CultureInfo.InvariantCulture, $"\nNo task is open for {S(only)}.{(work.Problem is null ? "" : " Work in progress could not be read: " + S(work.Problem).TrimEnd('.') + ".")}\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"\nThe task open for {S(only)}. {NotesAre}\n");
        Full(sb, one);
        if (!one.InProgress)
            sb.Append(one.Finished
                ? "    Its work is over: nothing it submitted awaits the author, and nothing more was to be written. It is put away the next time anything is noted, submitted or decided.\n"
                : "    A context was taken and nothing more: the scene's text is as it was. That is a look, and the brief does not list it.\n");
        return sb.ToString();
    }

    /// <summary>The scenes of tasks that are not otherwise shown, by id, so that any of them can be asked for.</summary>
    private static void Scenes(StringBuilder sb, string indent, IReadOnlyList<TaskView> rest)
    {
        if (rest.Count == 0)
            return;
        sb.Append(indent).Append(CultureInfo.InvariantCulture, $"and {rest.Count} more, by scene: ")
            .Append(string.Join(", ", rest.Take(ScenesShown).Select(v => S(v.Task.Scene))));
        if (rest.Count > ScenesShown)
            sb.Append(CultureInfo.InvariantCulture, $", and {rest.Count - ScenesShown} beyond those");
        sb.Append(". Ask for one with story_brief, giving its scene as task.\n");
    }

    private static void Files(StringBuilder sb, IReadOnlyList<string> files, string what, string then)
    {
        if (files.Count == 0)
            return;
        sb.Append(CultureInfo.InvariantCulture, $"\n{files.Count} file(s) under work/tasks {what}: {Listed(files, UnreadableShown)}. {then}\n");
    }

    /// <summary>The first few of a list on one line, and how many more there are.</summary>
    private static string Listed(IReadOnlyList<string> items, int shown) =>
        string.Join(", ", items.Take(shown).Select(S))
        + (items.Count > shown ? string.Create(CultureInfo.InvariantCulture, $", and {items.Count - shown} more") : "");

    private static string Head(TaskView v) => string.Create(CultureInfo.InvariantCulture,
        $"{S(v.Task.Scene)}{(v.Title is null ? "" : $" \"{S(v.Title)}\"")} · POV {S(v.Pov)} · task {S(v.Task.Id)}, opened by {S(v.Task.Actor)}");

    private static string State(TaskView v)
    {
        var sb = new StringBuilder();
        if (v.Submitted is null)
            sb.Append("not submitted yet.");
        else if (v.Missing)
            sb.Append(CultureInfo.InvariantCulture, $"submitted as {S(v.Submitted)}. A change set this task submitted is not in the ledger as it stands.");
        else if (v.Waiting.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"submitted as {S(v.Submitted)}. {v.Waiting.Count} of its record(s) still await the author: {Listed(v.Waiting, IdsShown)}.");
        else if (v.Standing)
            sb.Append(CultureInfo.InvariantCulture, $"submitted as {S(v.Submitted)}. Nothing of it awaits the author.");
        else
            sb.Append(CultureInfo.InvariantCulture, $"submitted as {S(v.Submitted)}. None of what it recorded is in force now.");
        if (v.More)
            sb.Append(" Its session said that more of the scene was still to be written.");
        if (v.Refused.Count > 0)
            sb.Append(v.Submitted is null ? " The last attempt was refused (" : " A later submission was refused (")
                .Append(Listed(v.Refused, IdsShown)).Append("), and nothing of it was written.");
        return sb.ToString();
    }

    private static void Full(StringBuilder sb, TaskView v)
    {
        sb.Append(CultureInfo.InvariantCulture, $"\n  {Head(v)}\n");

        if (v.Manifest is null)
            sb.Append("    context: none was taken in this task.");
        else if (v.FromSubmission && v.ContextProblem is null)
            // After a submission the scene's own records are what changed its context. Nothing needs checking
            // for that; the next piece of work needs the context as it now is.
            sb.Append(CultureInfo.InvariantCulture, $"    context: manifest {S(v.Manifest)}, the one last submitted with. ")
                .Append(v.ContextChanges.Count > 0
                    ? "A submission changes what its own scene's context says, so take the context again before going on."
                    : "What it said still stands.");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"    context: manifest {S(v.Manifest)}, taken at ledger head {v.ContextHead}");
            sb.Append(v.LaterManifest is null ? ". " : ", is the one the draft was begun from. ");
            if (v.ContextProblem is { } problem)
                sb.Append("It cannot be set against the present: ").Append(S(problem)).Append(v.Retake ? ". Take the context again." : ".");
            else if (v.ContextChanges.Count > 0)
                sb.Append(CultureInfo.InvariantCulture,
                    $"The ledger is at {v.LedgerHead}, and what that context said has changed in {v.ContextChanges.Count} line(s). Take the context again and check the draft against what differs.");
            else
                sb.Append(v.LedgerHead == v.ContextHead
                    ? "The ledger has not moved since."
                    : string.Create(CultureInfo.InvariantCulture, $"The ledger is at {v.LedgerHead}, and what that context said still stands."));
            if (v.LaterManifest is not null)
                sb.Append(CultureInfo.InvariantCulture, $" It was taken again since, last at ledger head {v.LaterHead} as manifest {S(v.LaterManifest)}.");
        }
        sb.Append('\n');

        sb.Append("    contract: ").Append(v.Contract is null ? "none" : S(v.Contract) + (v.Outline is null ? "" : " · " + S(v.Outline))).Append('\n');

        if (!v.Marked)
            sb.Append("    prose: the scene has no marker in the manuscript now.").Append(v.File is null ? "" : $" It was in manuscript/{S(v.File)}.").Append('\n');
        else
            sb.Append(CultureInfo.InvariantCulture, $"    prose: manuscript/{S(v.File)}, lines {v.LineStart} to {v.LineEnd}. ")
                .Append(v.TextChanged
                    ? v.Submitted is not null ? "The text has changed since it was last submitted.\n"
                    // With a note in the task, changed text is a draft. With nothing but a context it may not be, and is not called one.
                    : v.Worked ? "The text has changed since the context was taken: a draft is under way.\n"
                    : "The text is not as it was when the context was taken. Nothing was noted, so this may be a draft that was begun and left, or an edit made outside any task.\n"
                    : v.Submitted is null ? "The text is as it was when the context was taken.\n" : "The text is as it was submitted.\n");

        sb.Append("    state: ").Append(State(v)).Append('\n');

        if (v.Decided.Count > 0)
        {
            var earlier = Math.Max(0, v.Decided.Count - DecisionsShown);
            sb.Append(CultureInfo.InvariantCulture, $"    decided so far ({v.Decided.Count}){(earlier > 0 ? $", the last {DecisionsShown} of them" : "")}:\n");
            for (var i = earlier; i < v.Decided.Count; i++)
                sb.Append(CultureInfo.InvariantCulture, $"      {i + 1}. {S(v.Decided[i])}\n");
        }
        if (v.OpenQuestions.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    open questions ({v.OpenQuestions.Count}){(v.OpenQuestionsAge == NoteAge.Current ? "" : ", " + Mark(v.OpenQuestionsAge))}:\n");
            for (var i = 0; i < v.OpenQuestions.Count; i++)
                sb.Append(CultureInfo.InvariantCulture, $"      {i + 1}. {S(v.OpenQuestions[i])}\n");
        }
        if (v.Draft is not null)
            sb.Append("    draft").Append(v.DraftAge == NoteAge.Current ? "" : $" ({Mark(v.DraftAge)})").Append(": ").Append(S(v.Draft)).Append('\n');
        sb.Append("    next").Append(v.Next is null || v.NextAge == NoteAge.Current ? "" : $" ({Mark(v.NextAge)})").Append(": ")
            .Append(v.Next is null ? "nothing was noted." : S(v.Next)).Append('\n');
    }

    public static string Noted(TaskNoteResult result, TaskNote note)
    {
        var sb = new StringBuilder();
        if (result.Task is not { } task)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Refused. Nothing was noted. {result.Findings.Count} finding(s):\n");
            foreach (var f in result.Findings)
                sb.Append("  ").Append(S(f.ToString())).Append('\n');
            return sb.ToString();
        }
        if (note.Close is not null)
            return string.Create(CultureInfo.InvariantCulture, $"Task {S(task.Id)} for {S(task.Scene)} is closed: {S(note.Close)}\n");
        var parts = new List<string>();
        if (note.Decided.Count > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{note.Decided.Count} decision(s) added"));
        if (note.OpenQuestions is { } open)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{open.Count} question(s) now open"));
        if (note.Next is not null)
            parts.Add("the next step set");
        if (note.Draft is not null)
            parts.Add("the note on the draft set");
        if (note.More is { } more)
            parts.Add(more ? "more of the scene said to be still to come" : "nothing more of the scene said to be still to come");
        sb.Append(CultureInfo.InvariantCulture, $"Noted in task {S(task.Id)} for {S(task.Scene)}: {string.Join(", ", parts)}.\n");
        sb.Append(result.PutAway
            ? "The author has decided on everything this task submitted, and nothing more was to be written, so the task is put away. Work on the scene from here opens a new one, by taking its context.\n"
            : note.More == false
                ? "The task is put away once the author has decided on what it submitted.\n"
                : "A session that starts after this one reads it in the brief.\n");
        return sb.ToString();
    }
}
