namespace Nse.Query;

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
    public static IReadOnlyList<string> Differences(IReadOnlyList<ContextSection> then, IReadOnlyList<ContextSection> now)
    {
        var result = new List<string>();
        var before = Basis(then);
        var after = Basis(now);
        foreach (var title in before.Keys.Concat(after.Keys).Distinct().ToList())
        {
            var was = before.GetValueOrDefault(title) ?? [];
            var isNow = after.GetValueOrDefault(title) ?? [];
            // Two contexts for one scene are mostly the same lines in the same order, and a part of which that
            // is true has no difference to find. Only the others are compared line against line.
            if (was.SequenceEqual(isNow, StringComparer.Ordinal))
                continue;
            var (wasPlain, isNowPlain) = (was.Select(Plain).ToList(), isNow.Select(Plain).ToList());
            result.AddRange(wasPlain.Except(isNowPlain, StringComparer.Ordinal).Select(line => $"- {title}: {line}"));
            result.AddRange(isNowPlain.Except(wasPlain, StringComparer.Ordinal).Select(line => $"+ {title}: {line}"));
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

    /// <summary>The lines of each part that is compared, as written, under the part's title.</summary>
    private static Dictionary<string, List<string>> Basis(IReadOnlyList<ContextSection> sections) =>
        sections.Where(x => !x.Title.StartsWith(ContextCompiler.ProseTitle, StringComparison.Ordinal))
            .GroupBy(x => Plain(x.Title), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Lines).ToList(), StringComparer.Ordinal);

    private static string Plain(string line) =>
        line.Trim().Replace(ContextCompiler.ProvisionalMark, "", StringComparison.Ordinal)
            .Replace(ContextCompiler.ProvisionalEmbargoMark, "", StringComparison.Ordinal);
}
