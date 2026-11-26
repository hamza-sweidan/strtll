using System.Text;
using Nse.Manuscript;
using Nse.Tasks;
using Nse.Text;

namespace Nse;

/// <summary>What the engine says about an edit an agent is about to make to a file.</summary>
/// <param name="Refused">Whether the edit should not go ahead.</param>
/// <param name="Reason">Why, in words for the agent, when it should not; null when it may.</param>
/// <param name="Scenes">The scenes whose text the edit would change, as far as can be told before it is made.</param>
public sealed record EditCheck(bool Refused, string? Reason, IReadOnlyList<string> Scenes)
{
    public static readonly EditCheck Fine = new(false, null, []);
}

/// <summary>
/// The engine's side of the harness hooks: questions a hook asks about the files an agent is touching, answered
/// from the tasks and the manuscript. Nothing here writes anything.
/// </summary>
/// <remarks>
/// The protocol asks for the plan before any prose, and for a note again once prose is saved. Left to an
/// instruction, a session does the first only when the engine asks, and the second hardly at all, so the
/// questions that come up while writing are lost. An instruction is not a
/// guard. These are: the harness puts them in front of every edit, every save and the end of every turn.
/// They guard against the easy mistake, as the terminal check on the author's commands does, not against an
/// agent set on getting round them: an edit made through a shell command is not seen.
/// </remarks>
public sealed partial class Story
{
    /// <summary>What a refusal ends with: the reason the order matters.</summary>
    public const string WhyNoteFirst =
        "A session can end at any moment without warning, and a later one has nothing but the task's notes to go on.";

    /// <summary>
    /// Whether an agent may make this edit. Under ledger/ and work/ never: the engine writes those. Under
    /// manuscript/, only to a scene whose task holds a note: the plan comes before the prose. Anything else
    /// is not the engine's to judge.
    /// </summary>
    /// <param name="filePath">The file, as the tool names it: absolute, or relative to the story's folder.</param>
    /// <param name="oldStrings">For an edit, the text it replaces, one entry for each replacement. Empty for a whole-file write.</param>
    /// <param name="replaceAll">Whether each replacement is made everywhere the text occurs.</param>
    /// <param name="newContent">For a whole-file write, the file as it would be. Null for an edit.</param>
    public EditCheck CheckEdit(string filePath, IReadOnlyList<string> oldStrings, bool replaceAll, string? newContent)
    {
        ArgumentNullException.ThrowIfNull(oldStrings);
        var path = FullPath(filePath);
        if (path is null)
            return EditCheck.Fine;
        if (Within(path, LedgerDir))
            return new EditCheck(true,
                "Files under ledger/ are written by the engine alone. The story's state changes through submit_scene for what a scene does, " +
                "and propose_changes for anything that is the author's to decide.", []);
        if (Within(path, WorkDir))
            return new EditCheck(true,
                "Files under work/ are the engine's records of work in progress. Add to a scene's task with task_note.", []);
        if (!Within(path, ManuscriptDir) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return EditCheck.Fine;

        var current = File.Exists(path) ? Lines(File.ReadAllText(path, Encoding.UTF8)) : [];
        var now = ManuscriptReader.Markers(current).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var touched = newContent is not null
            ? Changed(current, Lines(newContent))
            : Touched(current, oldStrings, replaceAll);
        // A scene with no marker in the file now is new: there is no work on it to guard yet. Once it has a
        // marker, the next edit to it asks for a task like any other.
        var guarded = touched.Where(now.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (guarded.Count == 0)
            return new EditCheck(false, null, touched);

        var views = Tasks().ToDictionary(v => v.Task.Scene, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var scene in guarded)
        {
            var (task, unreadable) = TaskStore.Look(Root, scene);
            if (unreadable)
                problems.Add($"{scene}: the file where its task should be cannot be read as a task. Take the scene's context with scene_context, which sets the file aside and opens a task, then call task_note with your plan.");
            else if (task is null)
                problems.Add($"{scene}: no task is open for it. Take its context with scene_context, which opens one, then call task_note with your plan: what you have decided, the questions still open, the next step.");
            else if (views.TryGetValue(scene, out var view) && view.Finished)
                problems.Add($"{scene}: the work of its open task is over. Take its context again with scene_context, which opens a new task, then call task_note with your plan.");
            else if (!task.Steps.Any(s => s.Step == StepKind.Note))
                problems.Add($"{scene}: its task holds no note yet. Call task_note with your plan first: what you have decided, the questions still open, the next step.");
        }
        if (problems.Count == 0)
            return new EditCheck(false, null, touched);
        return new EditCheck(true,
            "Not yet: this edit changes the prose of " + string.Join(", ", guarded) + ", and the plan comes before the prose. " +
            string.Join(" ", problems) + " Then make the edit again. " + WhyNoteFirst,
            touched);
    }

    /// <summary>
    /// After an agent has saved a file: the scenes in it whose text has moved on since their task's last note,
    /// and what to do about it. Null when there is nothing to say.
    /// </summary>
    public string? AfterEdit(string filePath)
    {
        var path = FullPath(filePath);
        if (path is null || !Within(path, ManuscriptDir) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Path.GetFileName(path);
        var behind = new List<string>();
        foreach (var scene in ReadManuscript().Scenes.Where(s => string.Equals(s.File, name, StringComparison.Ordinal)))
        {
            var (task, _) = TaskStore.Look(Root, scene.Id);
            if (task is not null && Unnoted(task, scene.Hash) is not null)
                behind.Add(scene.Id);
        }
        if (behind.Count == 0)
            return null;
        return "The prose of " + string.Join(", ", behind) + " has changed since its task's last note. Before going on, call task_note with " +
               "what you decided while writing, the questions still open now, the next step, and how far the draft has got. " + WhyNoteFirst;
    }

    /// <summary>
    /// Before an agent ends its turn: the tasks it has open whose prose has moved on since their last note, or
    /// that were submitted with nothing noted since. Null when the turn may end. Tasks the author opened are
    /// left alone: the engine cannot tell who changed a file, and the author's own edits are not an agent's to note.
    /// </summary>
    public string? BeforeStop()
    {
        var manuscript = ReadManuscript();
        var due = new List<string>();
        foreach (var view in Tasks().Where(v => v.InProgress && v.Task.Actor.StartsWith("agent", StringComparison.Ordinal)))
        {
            if (manuscript.Find(view.Task.Scene) is not { } prose)
                continue;
            if (Unnoted(view.Task, prose.Hash) is { } what)
                due.Add($"{view.Task.Scene} ({what})");
        }
        if (due.Count == 0)
            return null;
        return "Before you stop, call task_note for " + string.Join("; ", due) + ": what you decided, the questions still open, the next step, " +
               "and how far the draft has got. After a submission, say also whether more of the scene is still to be written (more true or false). " +
               WhyNoteFirst;
    }

    /// <summary>
    /// What the harness is asked to keep when it compacts the conversation: the ids a session needs to pick its
    /// work up again, and everything it decided that is not yet in a note. Under the harness's limit of 10,000 characters.
    /// </summary>
    public string CompactionNote()
    {
        var sb = new StringBuilder();
        sb.Append("This project keeps its story's state in the Narrative State Engine (the nse tools). In the summary, keep word for word, ")
          .Append("for every scene being drafted or revised: the scene id, the manifest id of the context the draft was written from, ")
          .Append("every decision made and every question still open, the next step, and how far the draft has got. ")
          .Append("Say plainly which of these were not yet recorded with task_note, so that they can be noted first thing after the summary. ")
          .Append("Keep ids such as sc_07, ch_daniel and P12 exactly as written.");
        var open = Tasks().Where(v => v.InProgress).OrderBy(v => v.Tell).Take(12).ToList();
        if (open.Count > 0)
        {
            sb.Append("\nTasks open now:");
            foreach (var v in open)
                sb.Append("\n- ").Append(v.Task.Scene).Append(v.Manifest is { } m ? ", manifest " + m : ", no context taken yet");
        }
        var text = sb.ToString();
        return text.Length <= 9000 ? text : text[..9000];
    }

    /// <summary>
    /// Why a task's notes are behind its scene, or null if they are not: the prose has changed since the last
    /// note, or it was changed with no note made at all, or the scene was submitted and nothing noted since.
    /// </summary>
    private static string? Unnoted(TaskRecord task, string textHash)
    {
        int lastNote = -1, lastSubmission = -1;
        for (var i = 0; i < task.Steps.Count; i++)
        {
            if (task.Steps[i].Step == StepKind.Note)
                lastNote = i;
            else if (task.Steps[i].Step == StepKind.Submitted)
                lastSubmission = i;
        }
        if (lastNote < 0)
        {
            var first = task.Steps.FirstOrDefault(s => s.TextHash is not null);
            if (first is not null && first.TextHash != textHash)
                return "prose written, nothing noted";
            return lastSubmission >= 0 ? "submitted, nothing noted since" : null;
        }
        // A note made while the manuscript could not be read says nothing about the text, and is not held against it.
        if (task.Steps[lastNote].TextHash is { } noted && noted != textHash)
            return "prose changed since the last note";
        return lastSubmission > lastNote ? "submitted, nothing noted since" : null;
    }

    /// <summary>The file named as a full path, or null if it cannot be one.</summary>
    private string? FullPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(filePath) ? filePath : Path.Combine(Root, filePath));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Within(string path, string dir)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, comparison);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>The scenes a whole-file write changes: any whose text differs, and any that appear or disappear.</summary>
    private static List<string> Changed(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var a = Bodies(before);
        var b = Bodies(after);
        return a.Keys.Union(b.Keys, StringComparer.Ordinal)
            .Where(id => !a.TryGetValue(id, out var x) || !b.TryGetValue(id, out var y) || x != y)
            .ToList();
    }

    private static Dictionary<string, string> Bodies(IReadOnlyList<string> lines)
    {
        var markers = ManuscriptReader.Markers(lines);
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < markers.Count; i++)
        {
            var end = i + 1 < markers.Count ? markers[i + 1].Line : lines.Count;
            bodies.TryAdd(markers[i].Id, string.Join("\n", lines.Skip(markers[i].Line).Take(end - markers[i].Line)).TrimEnd());
        }
        return bodies;
    }

    /// <summary>
    /// The scenes an edit touches: those whose lines hold the text it replaces. Text that is not in the file
    /// touches nothing, since the edit will fail by itself. A replacement that cannot be placed is taken to
    /// touch every scene in the file.
    /// </summary>
    private static List<string> Touched(IReadOnlyList<string> lines, IReadOnlyList<string> oldStrings, bool replaceAll)
    {
        var text = string.Join("\n", lines);
        var markers = ManuscriptReader.Markers(lines);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        int LineOf(int offset)
        {
            var i = lineStarts.BinarySearch(offset);
            return i >= 0 ? i : ~i - 1;
        }
        var touched = new List<string>();
        foreach (var raw in oldStrings)
        {
            var old = string.Join("\n", Lines(raw ?? ""));
            if (old.Length == 0)
                return markers.Select(m => m.Id).ToList();
            for (var at = text.IndexOf(old, StringComparison.Ordinal); at >= 0; at = text.IndexOf(old, at + 1, StringComparison.Ordinal))
            {
                int first = LineOf(at), last = LineOf(at + old.Length - 1);
                for (var m = 0; m < markers.Count; m++)
                {
                    var from = markers[m].Line;
                    var to = m + 1 < markers.Count ? markers[m + 1].Line - 1 : lines.Count - 1;
                    if (first <= to && last >= from)
                        touched.Add(markers[m].Id);
                }
                if (!replaceAll)
                    break;
            }
        }
        return touched;
    }
}
