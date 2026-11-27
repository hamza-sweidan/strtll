namespace Nse.Query;

/// <summary>One line that differs between two contexts for a scene.</summary>
/// <param name="Added">True for a line that is new, false for one that was there and is gone.</param>
/// <param name="Title">The part the line sits in.</param>
/// <param name="Fact">The fact the line is about, when it is about one. Null for a line about the scene itself, a rule, a place.</param>
public sealed record LineChange(bool Added, string Title, string Line, string? Fact)
{
    public override string ToString() => $"{(Added ? "+" : "-")} {Title}: {Line}";
}

/// <summary>
/// Whether the state a scene was drafted from is still the state. There is no cache and no dependency graph:
/// the same context is compiled again at the current ledger head and the two are compared. They are either
/// the same, or they differ in lines that can be named exactly.
/// </summary>
public static class Freshness
{
    /// <summary>
    /// The lines that differ between two contexts for one scene, each with the section it sits in: "-" for a
    /// line that was there and is gone, "+" for one that is new. Empty when nothing the context says about the
    /// story's state has changed.
    /// </summary>
    /// <remarks>
    /// Two things are left out on purpose. The pointers to the prose, because drafting the scene is what moves
    /// them. And whether a record is still provisional, because ratifying a record the draft relied on changes
    /// nothing the draft relied on. A record that is rejected or retired disappears, and that does show.
    /// </remarks>
    public static IReadOnlyList<string> Differences(ContextPackage then, ContextPackage now) => Differences(then.Sections, now.Sections);

    /// <summary>The same comparison, of the parts of two contexts. The text of a context plays no part in it.</summary>
    public static IReadOnlyList<string> Differences(IReadOnlyList<ContextSection> then, IReadOnlyList<ContextSection> now) =>
        Changes(then, now).Select(c => c.ToString()).ToList();

    /// <summary>
    /// The same lines, each with the fact it is about. A draft that records nothing about a fact did not lean on
    /// what the context said of it, as far as its records show, and that is how a change is told apart from one
    /// the draft has to be checked against.
    /// </summary>
    public static IReadOnlyList<LineChange> Changes(IReadOnlyList<ContextSection> then, IReadOnlyList<ContextSection> now)
    {
        ArgumentNullException.ThrowIfNull(then);
        ArgumentNullException.ThrowIfNull(now);
        var result = new List<LineChange>();
        var before = Basis(then);
        var after = Basis(now);
        foreach (var title in before.Keys.Concat(after.Keys).Distinct().ToList())
        {
            var was = before.GetValueOrDefault(title) ?? [];
            var isNow = after.GetValueOrDefault(title) ?? [];
            // Two contexts for one scene are mostly the same lines in the same order, and a part of which that
            // is true has no difference to find. Only the others are compared line against line.
            if (was.Select(x => x.Line).SequenceEqual(isNow.Select(x => x.Line), StringComparer.Ordinal))
                continue;
            var (wasPlain, isNowPlain) = (was.Select(x => (Line: Plain(x.Line), x.Fact)).ToList(), isNow.Select(x => (Line: Plain(x.Line), x.Fact)).ToList());
            var (had, has) = (wasPlain.Select(x => x.Line).ToHashSet(StringComparer.Ordinal), isNowPlain.Select(x => x.Line).ToHashSet(StringComparer.Ordinal));
            result.AddRange(wasPlain.Where(x => !has.Contains(x.Line)).DistinctBy(x => x.Line, StringComparer.Ordinal).Select(x => new LineChange(false, title, x.Line, x.Fact)));
            result.AddRange(isNowPlain.Where(x => !had.Contains(x.Line)).DistinctBy(x => x.Line, StringComparer.Ordinal).Select(x => new LineChange(true, title, x.Line, x.Fact)));
        }
        return result;
    }

    /// <summary>
    /// A short mark of everything a context says about the story's state, for one scene at one ledger head. Two
    /// contexts with the same mark say the same things. It is what makes a manifest id checkable: compile the
    /// scene again at the head the id names and the mark must come out the same.
    /// </summary>
    public static string Mark(string scene, int head, IEnumerable<ContextSection> sections)
    {
        var text = new System.Text.StringBuilder().Append(scene).Append('\n').Append(head.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var section in sections.Where(x => !x.Title.StartsWith(ContextCompiler.ProseTitle, StringComparison.Ordinal)))
        {
            text.Append("\n# ").Append(Plain(section.Title));
            foreach (var line in section.Lines)
                text.Append('\n').Append(Plain(line));
        }
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..8];
    }

    /// <summary>The lines of each part that is compared, as written, under the part's title, each with the fact it is about.</summary>
    private static Dictionary<string, List<(string Line, string? Fact)>> Basis(IReadOnlyList<ContextSection> sections) =>
        sections.Where(x => !x.Title.StartsWith(ContextCompiler.ProseTitle, StringComparison.Ordinal))
            .GroupBy(x => Plain(x.Title), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Lines.Select((line, i) => (line, x.Facts is { } facts && i < facts.Count ? facts[i] : null))).ToList(), StringComparer.Ordinal);

    private static string Plain(string line) =>
        line.Trim().Replace(ContextCompiler.ProvisionalMark, "", StringComparison.Ordinal)
            .Replace(ContextCompiler.ProvisionalEmbargoMark, "", StringComparison.Ordinal);
}
