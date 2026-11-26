using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nse.Text;

/// <summary>How a quote stands against the text it cites.</summary>
public enum QuoteMatch
{
    /// <summary>In one paragraph, word for word, starting and ending on whole words.</summary>
    Exact,

    /// <summary>There, but too little to point at anything: one word that is not a whole paragraph.</summary>
    TooShort,

    /// <summary>The words are there, but only by running from one paragraph into the next.</summary>
    SpansParagraphs,

    /// <summary>The letters are there, but only by starting or ending inside a word.</summary>
    InsideWord,

    /// <summary>Not there.</summary>
    NotFound,
}

/// <summary>
/// Evidence is a quote that must be in the text word for word, as a reader sees the text. Line wrapping is
/// forgiven, and so is markup that is not words: the block-quote mark at the start of a line, and the
/// asterisks and underscores of emphasis. A quote lies within one paragraph, begins and ends on whole words,
/// and is at least two words long unless it is a whole paragraph (a line of dialogue may be a single word).
/// </summary>
public static partial class QuoteMatcher
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The block-quote marks at the start of a line. Every line of a quoted letter carries one, so a quote that
    /// runs over a line break inside the letter would otherwise have to include it. List markers are left
    /// alone: only the first line of an item has one, and a number and a full stop can open a line of prose.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?:>[ \t]?)+", RegexOptions.Multiline)]
    private static partial Regex LineMarkup();

    /// <summary>Asterisks, and underscores that are not between two letters or digits.</summary>
    [GeneratedRegex(@"\*+|(?<![\p{L}\p{N}])_+|_+(?![\p{L}\p{N}])")]
    private static partial Regex Emphasis();

    /// <summary>
    /// Collapse runs of whitespace so a quote survives re-wrapped lines. Nothing else is folded: the engine
    /// runs without the Unicode tables that would be needed to treat an accented letter typed as one character
    /// and the same letter typed as a letter plus a combining accent as equal, so a quote has to spell such a
    /// letter the way the text does.
    /// </summary>
    public static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>A paragraph, or a quote, as a reader sees it: without the markup that is not words.</summary>
    public static string Readable(string text) =>
        Normalize(Emphasis().Replace(LineMarkup().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), ""), ""));

    public static QuoteMatch Match(IEnumerable<string> paragraphs, string quote) => MatchIn(ReadableParagraphs(paragraphs), quote);

    /// <summary>
    /// The paragraphs of a scene as a reader sees them, leaving out any that are markup and nothing else. Reading
    /// a paragraph this way costs far more than looking for a quote in it, so when many quotes cite one scene its
    /// paragraphs are read once, and each quote is matched with <see cref="MatchIn"/>.
    /// </summary>
    public static IReadOnlyList<string> ReadableParagraphs(IEnumerable<string> paragraphs) =>
        paragraphs.Select(Readable).Where(t => t.Length > 0).ToList();

    /// <summary>How a quote stands against paragraphs that <see cref="ReadableParagraphs"/> has already read.</summary>
    public static QuoteMatch MatchIn(IReadOnlyList<string> texts, string quote)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var q = Readable(quote);
        if (q.Length == 0)
            return QuoteMatch.NotFound;
        var (tooShort, insideWord) = (false, false);
        foreach (var text in texts)
            foreach (var at in Occurrences(text, q))
            {
                if (!WholeWords(text, at, q.Length))
                    insideWord = true;
                else if (q.Length == text.Length || Words(q) >= 2)
                    return QuoteMatch.Exact;
                else
                    tooShort = true;
            }
        if (tooShort)
            return QuoteMatch.TooShort;
        // Not in any one paragraph. If the words are there across a break, say that, so the message can.
        var all = string.Join(" ", texts);
        if (Occurrences(all, q).Any(at => WholeWords(all, at, q.Length)))
            return QuoteMatch.SpansParagraphs;
        return insideWord ? QuoteMatch.InsideWord : QuoteMatch.NotFound;
    }

    /// <summary>Whether a quote is in one of the paragraphs as evidence must be.</summary>
    public static bool Contains(IEnumerable<string> paragraphs, string quote) => Match(paragraphs, quote) == QuoteMatch.Exact;

    private static IEnumerable<int> Occurrences(string text, string q)
    {
        for (var at = text.IndexOf(q, StringComparison.Ordinal); at >= 0; at = text.IndexOf(q, at + 1, StringComparison.Ordinal))
            yield return at;
    }

    private static bool WholeWords(string text, int at, int length) => !InsideWord(text, at) && !InsideWord(text, at + length);

    private static int Words(string text) => text.Split(' ').Count(w => w.Any(char.IsLetterOrDigit));

    /// <summary>Whether cutting the text just before position <paramref name="i"/> would cut a word in two.</summary>
    private static bool InsideWord(string text, int i) =>
        i > 0 && i < text.Length && WordChar(text, i - 1) && WordChar(text, i);

    /// <summary>
    /// A letter or digit; an accent that combines with the letter before it; or an apostrophe or hyphen with a
    /// letter or digit on both sides, as in "she'd" and "grey-coated".
    /// </summary>
    private static bool WordChar(string text, int k)
    {
        var c = text[k];
        if (char.IsLetterOrDigit(c))
            return true;
        if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark)
            return true;
        return c is '\'' or '’' or '-' or '‐' or '‑'
               && k > 0 && k + 1 < text.Length && char.IsLetterOrDigit(text[k - 1]) && char.IsLetterOrDigit(text[k + 1]);
    }

    /// <summary>
    /// The passage in the text closest to a quote that is not there, so the error can say what was probably meant.
    /// Compares word by word over a sliding window. Returns null when nothing is at least half alike.
    /// </summary>
    public static string? Nearest(string text, string quote)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var want = Normalize(quote).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || want.Length == 0)
            return null;
        var size = Math.Min(want.Length, words.Length);
        var (best, bestAt) = (0, -1);
        for (var start = 0; start + size <= words.Length; start++)
        {
            var score = CommonInOrder(want, words.AsSpan(start, size));
            if (score > best)
                (best, bestAt) = (score, start);
        }
        if (bestAt < 0 || best * 2 < want.Length)
            return null;
        var sb = new StringBuilder();
        for (var i = bestAt; i < bestAt + size; i++)
            sb.Append(i > bestAt ? " " : "").Append(words[i]);
        return sb.ToString();
    }

    /// <summary>Length of the longest run of words the two share in order, not necessarily adjacent.</summary>
    private static int CommonInOrder(string[] a, ReadOnlySpan<string> b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        foreach (var word in a)
        {
            for (var j = 1; j <= b.Length; j++)
                cur[j] = string.Equals(word, b[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return prev[b.Length];
    }
}
