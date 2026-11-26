using System.Globalization;
using System.Text.RegularExpressions;
using Nse.Manuscript;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>One passage of a draft, with the first line it occupies in the text it was read from.</summary>
public sealed record DraftParagraph(int Index, int Line, string Text);

/// <summary>
/// One place where a draft matches a tripwire of an embargo in force in its scene. A hit is a passage to read,
/// not a verdict: a hint the reader may miss is allowed, and a tripwire is a pattern of words, not a meaning.
/// </summary>
public sealed record TripwireHit(
    string Embargo, string Prop, string UntilScene, string Pattern, int Paragraph, int Line, string Matched, string Excerpt);

/// <summary>
/// What scanning one draft of a scene came to. <see cref="Problems"/> are patterns that could not be run on some
/// passage, each named: a scan that skipped something says so, so that no silence passes for a clean result.
/// </summary>
public sealed record ScanResult(
    string Scene, string Source, int Paragraphs, IReadOnlyList<Embargo> InForce, IReadOnlyList<TripwireHit> Hits,
    IReadOnlyList<string> Problems)
{
    public int Patterns => InForce.Sum(e => e.Tripwires.Count);

    public bool Clean => Hits.Count == 0 && Problems.Count == 0;
}

/// <summary>
/// The word scan. The author's embargoes carry tripwires: patterns of words that a
/// passage giving the fact away is likely to use. A draft of a scene is scanned for the tripwires of every
/// embargo still in force on entry to it, one paragraph at a time, as a reader sees the paragraph. Deterministic,
/// no model. It points a reader, or the auditor, at candidate passages; it decides nothing.
/// </summary>
public static class DraftScan
{
    /// <summary>
    /// How patterns are run: without regard to case, the same on every machine, and with a time limit. A pattern
    /// is the author's, and one that backtracks without end must not hang a submission.
    /// </summary>
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(250);

    /// <summary>Why a tripwire cannot be used, or null when it can. The validator refuses a put that carries one that cannot.</summary>
    public static string? Unusable(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "it is empty";
        try
        {
            var regex = new Regex(pattern, Options, Limit);
            // A pattern that matches nothing at all, or the empty string, would flag every paragraph of every draft.
            if (regex.IsMatch(""))
                return "it matches an empty passage, so it would flag every paragraph";
            return null;
        }
        catch (ArgumentException e)
        {
            return "it is not a pattern the engine can run: " + e.Message;
        }
        catch (RegexMatchTimeoutException)
        {
            return "it takes too long to run even on an empty passage";
        }
    }

    /// <summary>
    /// A draft's paragraphs, cut by the manuscript reader itself, so that the same text gives the same paragraphs,
    /// with the same numbers, whether it is scanned as a draft or as the scene standing in the manuscript. The
    /// text is one scene's, with no marker in it: <see cref="SceneOf"/> takes the scene's part out of a file.
    /// </summary>
    public static IReadOnlyList<DraftParagraph> Paragraphs(string text, int firstLine = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return ManuscriptReader.Paragraphs(lines, 0, lines.Length).Blocks
            .Select(b => new DraftParagraph(b.Index, firstLine + b.LineStart - 1, b.Text)).ToList();
    }

    /// <summary>The paragraphs of a scene as it stands in the manuscript, with the lines they occupy in its file.</summary>
    public static IReadOnlyList<DraftParagraph> Paragraphs(ManuscriptScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.Blocks.Select(b => new DraftParagraph(b.Index, b.LineStart, b.Text)).ToList();
    }

    /// <summary>
    /// The part of a file that is one scene's. A file with no scene markers is all draft. A file with markers is
    /// read as the manuscript reader reads it, and only the named scene's lines are taken; if it has markers and
    /// none is the scene's, there is nothing of the scene in it, and that is an error rather than a clean scan.
    /// </summary>
    public static (string Text, int FirstLine) SceneOf(string fileText, string sceneId)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        var lines = fileText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var markers = ManuscriptReader.Markers(lines);
        if (markers.Count == 0)
            return (string.Join("\n", lines), 1);
        var at = markers.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Id == sceneId);
        if (at.m.Id != sceneId)
            throw new KeyNotFoundException(
                $"The file has scene markers ({string.Join(", ", markers.Select(m => m.Id))}) and none of them is {sceneId}. Scan the file that holds the scene, or a file with no markers that holds only the draft.");
        var end = at.i + 1 < markers.Count ? markers[at.i + 1].Line : lines.Length;
        return (string.Join("\n", lines[(at.m.Line + 1)..end]), at.m.Line + 2);
    }

    /// <summary>Scan paragraphs of a draft of a scene against the tripwires of every embargo in force on entry to it.</summary>
    public static ScanResult Run(StoryState s, string sceneId, IReadOnlyList<DraftParagraph> paragraphs, string source)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(paragraphs);
        var inForce = s.EmbargoesInForce(sceneId);
        var hits = new List<TripwireHit>();
        var problems = new List<string>();
        foreach (var embargo in inForce)
            foreach (var pattern in embargo.Tripwires)
            {
                if (Unusable(pattern) is { } why)
                {
                    // A ledger written before tripwires were checked may hold one. It is reported every time.
                    problems.Add($"embargo {embargo.Id}: tripwire \"{Plain.OneLine(pattern)}\" was not run: {why}");
                    continue;
                }
                var regex = new Regex(pattern, Options, Limit);
                foreach (var paragraph in paragraphs)
                {
                    var readable = QuoteMatcher.Readable(paragraph.Text);
                    try
                    {
                        foreach (Match m in regex.Matches(readable))
                        {
                            if (m.Length == 0)
                                continue;
                            hits.Add(new TripwireHit(embargo.Id, embargo.Prop, embargo.UntilScene, pattern, paragraph.Index,
                                paragraph.Line, Plain.OneLine(m.Value), Excerpt(readable, m.Index, m.Length)));
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        problems.Add(string.Create(CultureInfo.InvariantCulture,
                            $"embargo {embargo.Id}: tripwire \"{Plain.OneLine(pattern)}\" took too long on paragraph {paragraph.Index} (line {paragraph.Line}) and was not finished there. Read that paragraph yourself, and ask the author to simplify the pattern"));
                    }
                }
            }
        var ordered = hits.OrderBy(h => h.Line).ThenBy(h => h.Paragraph).ThenBy(h => h.Embargo, StringComparer.Ordinal)
            .ThenBy(h => h.Pattern, StringComparer.Ordinal).ToList();
        return new ScanResult(sceneId, source, paragraphs.Count, inForce, ordered, problems);
    }

    /// <summary>The sentence a match sits in, cut to a readable length around it. It is one line, whatever the text held.</summary>
    private static string Excerpt(string text, int at, int length)
    {
        const int around = 80;
        var start = at;
        while (start > 0 && at - start < around && !EndsSentence(text, start - 1))
            start--;
        var end = at + length;
        while (end < text.Length && end - (at + length) < around && !EndsSentence(text, end - 1))
            end++;
        // Neither end falls inside a character that is written as two units: the whole of it is kept.
        if (Plain.SplitsCharacter(text, start))
            start--;
        if (Plain.SplitsCharacter(text, end))
            end++;
        var cut = text[start..end].Trim();
        return Plain.OneLine((start > 0 && !EndsSentence(text, start - 1) ? "..." : "") + cut + (end < text.Length && !EndsSentence(text, end - 1) ? "..." : ""));
    }

    private static bool EndsSentence(string text, int i) => text[i] is '.' or '!' or '?' && (i + 1 >= text.Length || text[i + 1] == ' ');

    /// <summary>The scan in words, as both front ends print it.</summary>
    public static string Text(ScanResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"TRIPWIRE SCAN OF {r.Scene} · {r.Source} · {r.Paragraphs} paragraph(s) · {r.InForce.Count} embargo(es) in force, {r.Patterns} pattern(s)\n");
        if (r.InForce.Count == 0)
            sb.Append("No embargo is in force in this scene, so there is nothing for the scan to look for.\n");
        else if (r.Patterns == 0)
            sb.Append("The embargoes in force carry no tripwires, so the scan cannot point at anything. Only a reading can check this draft.\n");
        foreach (var p in r.Problems)
            sb.Append("NOT CHECKED: ").Append(Plain.OneLine(p)).Append('\n');
        if (r.Hits.Count == 0)
        {
            if (r.Patterns > 0)
                sb.Append("No tripwire matched. That is not a clearance: a leak can use words no pattern foresaw.\n");
            return sb.ToString();
        }
        sb.Append(CultureInfo.InvariantCulture, $"Passages to read ({r.Hits.Count}). A hit is not a verdict: a hint the reader may miss is allowed.\n");
        foreach (var h in r.Hits)
            sb.Append(CultureInfo.InvariantCulture,
                $"  line {h.Line}, paragraph {h.Paragraph}: \"{h.Matched}\" matches {h.Embargo} ({h.Prop}, held back until {h.UntilScene}) in: {h.Excerpt}\n");
        return sb.ToString();
    }
}
