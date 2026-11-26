using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nse.Text;

namespace Nse.Manuscript;

/// <summary>One paragraph of a scene, with the lines it occupies in its file.</summary>
public sealed record Block(int Index, int LineStart, int LineEnd, string Text);

/// <summary>A scene as it stands in the manuscript files. Its place in the telling is its position here.</summary>
public sealed record ManuscriptScene(
    string Id, int Tell, string? Title, string File, int LineStart, int LineEnd, string Text, string Hash,
    IReadOnlyList<Block> Blocks);

/// <summary>
/// The manuscript at one moment: scenes in telling order, and a hash that changes when the text of any scene
/// does, or the order, or the file a scene is in.
/// </summary>
public sealed record ManuscriptSnapshot(
    IReadOnlyList<ManuscriptScene> Scenes, string Hash, IReadOnlyList<string> Problems)
{
    private readonly Dictionary<string, ManuscriptScene> _byId =
        Scenes.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

    public ManuscriptScene? Find(string sceneId) => _byId.GetValueOrDefault(sceneId);

    /// <summary>
    /// A hash of where everything sits: the lines each scene and each paragraph occupy. Blank lines added
    /// between two scenes change no scene's text, so <see cref="Hash"/> stays as it was, but every line number
    /// after them moves. Anything that stores line numbers is stale when this differs.
    /// </summary>
    public string Layout { get; } = ManuscriptReader.Sha256(string.Join("\n", Scenes.Select(s =>
        FormattableString.Invariant($"{s.File}\0{s.Id}\0{s.Title}\0{s.LineStart}-{s.LineEnd}\0")
        + string.Join(",", s.Blocks.Select(b => FormattableString.Invariant($"{b.LineStart}-{b.LineEnd}"))))));
}

/// <summary>
/// Reads Markdown files in name order. A scene starts at a marker comment, <c>&lt;!-- scene: id --&gt;</c>, and runs
/// to the next marker or the end of the file. The telling order is never entered by hand: it is this reading order.
/// </summary>
public static partial class ManuscriptReader
{
    [GeneratedRegex(@"^\s*<!--\s*scene:\s*([A-Za-z0-9_\-]+)\s*-->\s*$")]
    private static partial Regex Marker();

    public static ManuscriptSnapshot Read(string manuscriptDir)
    {
        var scenes = new List<ManuscriptScene>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.Exists(manuscriptDir)
            ? Directory.GetFiles(manuscriptDir, "*.md").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
            : [];
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            // The engine prints file names and headings from the manuscript, and agents write the manuscript.
            // What is printed from it is held to what is printed from a record: one line of ordinary characters.
            if (Plain.Odd(name) is { } inName)
            {
                problems.Add($"{Plain.OneLine(name)}: the file's name holds a character that is not ordinary text ({inName}). The file is not read; rename it");
                continue;
            }
            var lines = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Plain.Odd(lines[i].Replace("\t", "")) is { } inLine)
                    problems.Add(FormattableString.Invariant(
                        $"{name}, line {i + 1}: a character that is not ordinary text ({inLine}). Prose has no use for it; where the engine prints from this line it is shown spelled out"));
            var starts = new List<(int Line, string Id)>();
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Marker().Match(lines[i]);
                if (m.Success)
                    starts.Add((i, m.Groups[1].Value));
            }
            var firstMarker = starts.Count > 0 ? starts[0].Line : lines.Length;
            if (lines.Take(firstMarker).Any(l => l.Trim().Length > 0))
                problems.Add($"{name}: text before the first scene marker belongs to no scene");
            for (var s = 0; s < starts.Count; s++)
            {
                var (markerLine, id) = starts[s];
                var end = s + 1 < starts.Count ? starts[s + 1].Line : lines.Length;
                if (!seen.Add(id))
                {
                    problems.Add($"{name}: scene id {id} is used more than once; the second is ignored");
                    continue;
                }
                if (id.StartsWith('-'))
                    problems.Add($"{name}: scene id {id} begins with a hyphen, which no record's id may. Rename the scene, or it can never have a scene record");
                scenes.Add(ReadScene(id, scenes.Count + 1, name, lines, markerLine + 1, end));
            }
        }
        var all = new StringBuilder();
        foreach (var s in scenes)
            all.Append(s.File).Append('\0').Append(s.Id).Append('\0').Append(s.Hash).Append('\n');
        return new ManuscriptSnapshot(scenes, Sha256(all.ToString()), problems);
    }

    private static ManuscriptScene ReadScene(string id, int tell, string file, string[] lines, int from, int to)
    {
        // Trim trailing blank lines so an edit to the next scene's spacing does not change this one's hash.
        while (to > from && lines[to - 1].Trim().Length == 0)
            to--;
        var (title, blocks) = Paragraphs(lines, from, to);
        var body = string.Join("\n", lines[from..to]);
        return new ManuscriptScene(id, tell, title, file, from + 1, to, body, Sha256(body), blocks);
    }

    /// <summary>
    /// The paragraphs in a run of one scene's lines, and the scene's title if the run opens with one. A paragraph
    /// is a run of lines with no blank line in it. They are numbered from one, and each carries the lines it
    /// occupies, counted from the first line of <paramref name="lines"/> as line one. This is the one place that
    /// says what a paragraph is: a scene in the manuscript and a draft handed over on its own are both cut here,
    /// so a quote or a scan that points at "paragraph 3" means the same paragraph either way.
    /// </summary>
    public static (string? Title, IReadOnlyList<Block> Blocks) Paragraphs(string[] lines, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? title = null;
        var blocks = new List<Block>();
        var start = -1;
        for (var i = from; i <= to; i++)
        {
            var blank = i == to || lines[i].Trim().Length == 0;
            if (!blank && start < 0)
                start = i;
            if (blank && start >= 0)
            {
                // The title is the heading line alone. Text that follows it with no blank line between is a
                // paragraph of the scene like any other.
                if (title is null && blocks.Count == 0 && lines[start].StartsWith("# ", StringComparison.Ordinal))
                {
                    title = Plain.OneLine(lines[start][2..].Trim());
                    start++;
                }
                if (start < i)
                    blocks.Add(new Block(blocks.Count + 1, start + 1, i, string.Join("\n", lines[start..i])));
                start = -1;
            }
        }
        return (title, blocks);
    }

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The scene markers in one file's lines, read as <see cref="Read"/> reads them: the line each stands on, from
    /// nought, and the scene id it names. A scene runs from its marker to the line before the next one.
    /// </summary>
    public static IReadOnlyList<(int Line, string Id)> Markers(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var found = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = Marker().Match(lines[i]);
            if (m.Success)
                found.Add((i, m.Groups[1].Value));
        }
        return found;
    }
}
