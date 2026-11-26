using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nse.Query;
using Nse.Text;

namespace Nse.Tasks;

/// <summary>The things that happen in the course of work on a scene.</summary>
public enum StepKind { Context, Note, Refused, Submitted, Decided, Closed }

/// <summary>
/// One thing that happened in a task, in the order it happened. Which fields are set depends on the kind:
/// a context names the manifest and where the scene's text sits; a note carries what the agent decided and
/// what it means to do next; a submission names the change set it became, or the findings it was refused with.
/// </summary>
public sealed record TaskStep
{
    public required StepKind Step { get; init; }

    /// <summary>The ledger head when it happened.</summary>
    public required int AtHead { get; init; }

    /// <summary>When, in universal time. For a person reading the file; the engine never prints it or acts on it.</summary>
    public string? Time { get; init; }

    public string? Manifest { get; init; }
    public string? Pov { get; init; }
    public string? Contract { get; init; }
    public string? File { get; init; }

    /// <summary>
    /// A hash of the scene's text when this step happened. On a context or a submission it is what the text is
    /// later compared with, to say whether someone has been writing. On a note it says how old the note is
    /// beside the text: if the text is different now, the note was made before it was last changed.
    /// </summary>
    public string? TextHash { get; init; }

    public IReadOnlyList<string>? Decided { get; init; }
    public IReadOnlyList<string>? OpenQuestions { get; init; }
    public string? Next { get; init; }
    public string? Draft { get; init; }

    /// <summary>
    /// On a note made after the scene was submitted: whether more of the scene is still to be written. True
    /// keeps the task open once the author has decided on what was submitted. False lets it be put away then.
    /// </summary>
    public bool? More { get; init; }

    /// <summary>The findings a submission was refused with, or the notes it was applied with.</summary>
    public IReadOnlyList<string>? Codes { get; init; }
    public string? ChangeSet { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// What a session was in the middle of, for one scene: every step so far. It is working state, not story
/// state. Nothing in it is true of the story; the ledger is where that lives. It exists so that a session
/// which stops, or loses its context, leaves behind where it was.
/// </summary>
public sealed record TaskRecord
{
    public int Format { get; init; } = 1;
    public required string Id { get; init; }
    public required string Scene { get; init; }

    /// <summary>Who opened it: "author", "agent", or "agent:" and a plain name.</summary>
    public required string Actor { get; init; }
    public required IReadOnlyList<TaskStep> Steps { get; init; }

    public TaskRecord With(TaskStep step) => this with { Steps = [.. Steps, step] };
}

/// <summary>
/// What is under work/tasks: the tasks that could be read; the names of the files that were read and are not
/// tasks; and the names of those that could not be opened at all, which may be perfectly good tasks that
/// another process had for a moment. The last two are never to be confused: one may be thrown away and the
/// other must not be.
/// </summary>
public sealed record OpenTasks(IReadOnlyList<TaskRecord> Tasks, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Busy);

/// <summary>
/// Task records live under work/tasks, one file for each scene that has work open on it, and are moved to
/// work/tasks/closed when the work is done. They are kept apart from the ledger because they are not part of
/// the story, and apart from the index because they cannot be rebuilt from anything: deleting the index
/// must not lose what a session had decided.
/// </summary>
/// <remarks>
/// The folder is unprotected, and anything can write there. So a file is held, when it is read, to everything
/// the engine holds a task to when it writes one: the same shapes, the same lengths, one line of ordinary text
/// in every place that is printed. A file that fails is not a task. It is named in the brief and never
/// written over; it is set aside when a task has to be written where it sits.
/// </remarks>
public static partial class TaskStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long one line of a note may be, and how many lines of a kind one note may carry.</summary>
    public const int MaxLine = 400, MaxEntries = 12;

    /// <summary>More steps than a task could come to by being worked on. A file with more was not written by the engine.</summary>
    public const int MaxSteps = 2000;

    /// <summary>How many codes or record ids one step keeps. More than any submission comes to.</summary>
    public const int MaxCodes = 500;

    /// <summary>The highest number a closed task is given. A file numbered above it was not put there by the engine, and is not counted.</summary>
    public const int MaxNumber = 999_999;

    public static string Dir(string root) => Path.Combine(root, "work", "tasks");

    public static string ClosedDir(string root) => Path.Combine(Dir(root), "closed");

    /// <summary>Where a file that could not be read as a task is put when a task has to be written in its place.</summary>
    public static string AsideDir(string root) => Path.Combine(Dir(root), "unreadable");

    public static string LockPath(string root) => Path.Combine(Dir(root), ".lock");

    /// <summary>A scene id as the manuscript's markers allow it. Anything else is never made into a file name.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_-]{0,63}\z")]
    private static partial Regex SceneShape();

    [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z")]
    private static partial Regex Device();

    [GeneratedRegex(@"\A(author|agent(:[A-Za-z0-9_.-]{1,40})?)\z")]
    private static partial Regex ActorShape();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.:-]{0,63}\z")]
    private static partial Regex PlainWord();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashShape();

    [GeneratedRegex(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z")]
    private static partial Regex TimeShape();

    public static bool Nameable(string? scene) => scene is not null && SceneShape().IsMatch(scene);

    /// <summary>
    /// The name of a scene's task file, without its extension. A scene id is its own file name, except that a
    /// capital letter is written as "!" and the small letter, and a name the system keeps for a device is given
    /// a "!" at its end. So two ids that differ only in case never share a file, on a file system that cannot
    /// tell them apart, and no id names a device.
    /// </summary>
    public static string FileName(string scene)
    {
        var name = new StringBuilder(scene.Length + 8);
        foreach (var c in scene)
        {
            if (c is >= 'A' and <= 'Z')
                name.Append('!').Append((char)(c + 32));
            else
                name.Append(c);
        }
        if (Device().IsMatch(name.ToString()))
            name.Append('!');
        return name.ToString();
    }

    private static string PathOf(string root, string scene) => Path.Combine(Dir(root), FileName(scene) + ".json");

    /// <summary>
    /// Who is written down as having opened a task. It is printed in the brief on the engine's own line, so it
    /// is held to three shapes and anything else is put down as an agent with no name.
    /// </summary>
    public static string ActorName(string? actor) => actor is not null && ActorShape().IsMatch(actor) ? actor : "agent";

    /// <summary>
    /// An id as it may be kept in a task, or null if it is not of that shape. A ledger made before ids were
    /// held to plain letters may name a person or a record otherwise. Such an id is left out of the task,
    /// which only ever kept it as a convenience: what is shown is worked out from the story each time.
    /// </summary>
    public static string? Plainly(string? id) => id is not null && PlainWord().IsMatch(id) ? id : null;

    /// <summary>What is wrong with a line of a note, or null if nothing is. A note is one short line of ordinary text.</summary>
    public static string? LineProblem(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "is empty"
        : Plain.Odd(text) is { } odd ? $"holds a line break or another character that is not ordinary text ({odd}). A note is one line; give several decisions as several entries"
        : Plain.Padded(text) ? "holds a run of three or more spaces"
        : text.Length > MaxLine ? string.Create(CultureInfo.InvariantCulture, $"is {text.Length} characters long, and a note is at most {MaxLine}. It is read at the start of every session: say it shortly")
        : null;

    /// <summary>
    /// The task open on a scene. There may be none; or there may be a file where it would be that is not this
    /// scene's task, which is reported and left as it is. A file that cannot be opened at all, because another
    /// process has it this instant, comes out as the exception it raised: that is not the same as a bad file,
    /// and must never lead to one being moved away.
    /// </summary>
    public static (TaskRecord? Task, bool Unreadable) Look(string root, string scene)
    {
        if (!Nameable(scene))
            return (null, false);
        var path = PathOf(root, scene);
        if (!File.Exists(path))
            return (null, false);
        var task = Parse(File.ReadAllBytes(path));
        return task is not null && task.Scene == scene ? (task, false) : (null, true);
    }

    /// <summary>The task open on a scene, or null if there is none, or none that can be read.</summary>
    public static TaskRecord? Open(string root, string scene)
    {
        try
        {
            return Look(root, scene).Task;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every task that is open, in the order of their scenes' ids, and every file there that is not one.</summary>
    public static OpenTasks AllOpen(string root)
    {
        var dir = Dir(root);
        if (!Directory.Exists(dir))
            return new OpenTasks([], [], []);
        var tasks = new List<TaskRecord>();
        var unreadable = new List<string>();
        var busy = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var (shelf, task) = ReadWaiting(path);
            if (shelf == Shelf.Busy)
                busy.Add(Path.GetFileName(path));
            else if (shelf == Shelf.Task && FileName(task!.Scene) == name)
                tasks.Add(task);
            else if (shelf != Shelf.Gone)
                unreadable.Add(Path.GetFileName(path));
        }
        return new OpenTasks([.. tasks.OrderBy(t => t.Scene, StringComparer.Ordinal)], unreadable, busy);
    }

    /// <summary>What reading a file under work/tasks came to.</summary>
    private enum Shelf
    {
        /// <summary>It is a task.</summary>
        Task,

        /// <summary>It was read, and it is not a task.</summary>
        Bad,

        /// <summary>It could not be opened. Another process may have it, and it may be a good task.</summary>
        Busy,

        /// <summary>It was there a moment ago and is not now: put away while the folder was being read.</summary>
        Gone,
    }

    /// <summary>Read a task for showing. A file another process has this instant is tried again before it is given up on.</summary>
    private static (Shelf What, TaskRecord? Task) ReadWaiting(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var task = Parse(File.ReadAllBytes(path));
                return (task is null ? Shelf.Bad : Shelf.Task, task);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return (Shelf.Gone, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                    return (Shelf.Busy, null);
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>A task, if the bytes are one the engine could have written. Otherwise null.</summary>
    private static TaskRecord? Parse(byte[] bytes)
    {
        try
        {
            var task = NseJson.Read<TaskRecord>(bytes);
            return Sound(task) ? task : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a task read from a file is one the engine could have written. Every text in it that the brief
    /// prints is one short line of ordinary characters, every id has the shape of an id, and no list holds a
    /// null. The limits on a note are applied here as they are when a note is made, because a file written by
    /// hand never went through that.
    /// </summary>
    public static bool Sound(TaskRecord task) =>
        task is not null && task.Format == 1 && Nameable(task.Scene) && IdOf(task.Scene, task.Id)
        && task.Actor is not null && ActorShape().IsMatch(task.Actor)
        && task.Steps is { Count: > 0 and <= MaxSteps } && task.Steps.All(Sound);

    /// <summary>The id of a scene's nth task, and whether a text is such an id for that scene.</summary>
    public static string IdFor(string scene, int number) => string.Create(CultureInfo.InvariantCulture, $"tk_{scene}_{number}");

    private static bool IdOf(string scene, string? id)
    {
        var prefix = "tk_" + scene + "_";
        return id is not null && id.StartsWith(prefix, StringComparison.Ordinal)
               && id.Length - prefix.Length is >= 1 and <= 9 && id.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>A manifest id as it may be kept in a task: the text of one this engine could have issued.</summary>
    public static bool ManifestShape(string? manifest) => ManifestId.Parse(manifest) is not null;

    private static bool Sound(TaskStep? s) =>
        s is not null && s.AtHead >= 0 && Enum.IsDefined(s.Step)
        && (s.Time is null || TimeShape().IsMatch(s.Time))
        && (s.Manifest is null || ManifestShape(s.Manifest))
        && (s.Pov is null || PlainWord().IsMatch(s.Pov))
        && (s.Contract is null || PlainWord().IsMatch(s.Contract))
        && (s.File is null || (s.File.Length <= 255 && Plain.Odd(s.File) is null && !string.IsNullOrWhiteSpace(s.File)))
        && (s.TextHash is null || HashShape().IsMatch(s.TextHash))
        && Lines(s.Decided) && Lines(s.OpenQuestions)
        && (s.Next is null || LineProblem(s.Next) is null)
        && (s.Draft is null || LineProblem(s.Draft) is null)
        && (s.Reason is null || LineProblem(s.Reason) is null)
        && (s.Codes is null || (s.Codes.Count <= MaxCodes && s.Codes.All(c => c is not null && PlainWord().IsMatch(c))))
        && (s.ChangeSet is null || PlainWord().IsMatch(s.ChangeSet));

    private static bool Lines(IReadOnlyList<string>? lines) =>
        lines is null || (lines.Count <= MaxEntries && lines.All(l => LineProblem(l) is null));

    [GeneratedRegex(@"\A\.([0-9]{1,9})\.json\z")]
    private static partial Regex Numbered();

    /// <summary>
    /// The number the next task for a scene takes: one more than the highest among those already closed for it.
    /// Not one more than how many there are, so that a closed file someone deleted does not make the next
    /// close take the number of a file that is still there.
    /// </summary>
    public static int NextNumber(string root, string scene) => 1 + Highest(ClosedDir(root), FileName(scene));

    private static int Highest(string dir, string name)
    {
        if (!Directory.Exists(dir))
            return 0;
        var highest = 0;
        foreach (var path in Directory.GetFiles(dir, name + ".*.json"))
        {
            // What follows the name has to be a number and the extension, and nothing else.
            var m = Numbered().Match(Path.GetFileName(path)[name.Length..]);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxNumber)
                highest = Math.Max(highest, n);
        }
        return highest;
    }

    /// <summary>
    /// The engine writes nothing that it would refuse to read back. If it did, a context would say a task was
    /// open, the next note would find a file that is not one, and the context after that would move it aside
    /// and write another like it.
    /// </summary>
    private static void Hold(TaskRecord task)
    {
        if (!Sound(task))
            throw new IOException("it would hold something a task file may not, so it was not written");
    }

    /// <summary>The latest task that was put away for a scene, or null if there is none that can be read.</summary>
    public static TaskRecord? LastClosed(string root, string scene)
    {
        try
        {
            if (!Nameable(scene))
                return null;
            var name = FileName(scene);
            var number = Highest(ClosedDir(root), name);
            var path = Path.Combine(ClosedDir(root), string.Create(CultureInfo.InvariantCulture, $"{name}.{number:000}.json"));
            return number > 0 && File.Exists(path) && Parse(File.ReadAllBytes(path)) is { } task && task.Scene == scene ? task : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write a task. The file appears under its name only when it is complete.</summary>
    public static void Save(string root, TaskRecord task)
    {
        Hold(task);
        var final = PathOf(root, task.Scene);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temp = final + ".tmp";
        File.WriteAllBytes(temp, Utf8.GetBytes(NseJson.Write(task) + "\n"));
        File.Move(temp, final, overwrite: true);
    }

    /// <summary>Put a finished task away. The scene then has no task open, and the next context taken for it opens a new one.</summary>
    public static void Close(string root, TaskRecord task)
    {
        Hold(task);
        PutAway(ClosedDir(root), FileName(task.Scene), Utf8.GetBytes(NseJson.Write(task) + "\n"));
        File.Delete(PathOf(root, task.Scene));
    }

    /// <summary>
    /// Move the file that sits where a scene's task would be out of the way, because it cannot be read as that
    /// task and one has to be written. It is kept, under work/tasks/unreadable, for whoever wants to look.
    /// </summary>
    public static void SetAside(string root, string scene)
    {
        var path = PathOf(root, scene);
        PutAway(AsideDir(root), FileName(scene), File.ReadAllBytes(path));
        File.Delete(path);
    }

    /// <summary>Write bytes under the next free number for a name. A file that is already there is never written over.</summary>
    private static void PutAway(string dir, string name, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        for (var number = 1 + Highest(dir, name); ; number++)
        {
            var final = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{name}.{number:000}.json"));
            try
            {
                using var file = new FileStream(final, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes);
                return;
            }
            catch (IOException) when (File.Exists(final))
            {
                // Someone took the number between the look and the write. The next one, then.
            }
        }
    }
}
