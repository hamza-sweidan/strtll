using System.Globalization;
using System.Text;

namespace Nse.Text;

/// <summary>
/// Text that came from a record, a file or a caller, as against text the engine writes itself. Such text is
/// kept to one line of ordinary characters on the way in, and on the way out anything else in it is spelled
/// out. Nothing a record says can then start a line of its own, pass for a line of the engine's, or drive
/// the terminal it is printed on.
/// </summary>
public static class Plain
{
    /// <summary>
    /// Whether a character is one a record's text may hold. Not ordinary: control characters, which include
    /// line breaks, tabs and the escape that starts a terminal sequence; the line and paragraph separators;
    /// the marks that reorder the text around them; and the few characters that draw nothing and yet take
    /// up room, which could pad a line out to the edge of the screen unseen.
    /// </summary>
    public static bool Ordinary(char c)
    {
        int code = c;
        return !char.IsControl(c)
               && code is not (0x2028 or 0x2029)          // line separator, paragraph separator
               && code is not (>= 0x202A and <= 0x202E)   // embeddings and overrides of direction
               && code is not (>= 0x2066 and <= 0x2069)   // isolates of direction
               && code is not (0x2800 or 0x3164 or 0x115F or 0x1160 or 0xFFA0);  // the braille blank and the Hangul fillers
    }

    /// <summary>The first character of a text that is not ordinary, named by its code point, or null if all of it is.</summary>
    public static string? Odd(string text)
    {
        foreach (var c in text)
            if (!Ordinary(c))
                return FormattableString.Invariant($"U+{(int)c:X4}");
        return null;
    }

    /// <summary>
    /// Whether a text holds a run of three or more spaces of any kind. Two are a typist's habit. A long run
    /// can push what follows it onto a row of its own on the author's screen, where it reads as a new line.
    /// </summary>
    public static bool Padded(string text)
    {
        var run = 0;
        foreach (var c in text)
        {
            run = char.IsWhiteSpace(c) ? run + 1 : 0;
            if (run >= 3)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether cutting a text just before position <paramref name="at"/> would cut one character in two. A
    /// character outside the basic plane is written as a pair of units, and half of one is not a character:
    /// it prints as a blot.
    /// </summary>
    public static bool SplitsCharacter(string text, int at) =>
        at > 0 && at < text.Length && char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at]);

    /// <summary>
    /// A text as it is safe to print inside one line: every character that is not ordinary is spelled out,
    /// and every run of spaces is shown as one.
    /// </summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length + 8);
        var afterSpace = false;
        foreach (var c in text)
        {
            if (Ordinary(c) && char.IsWhiteSpace(c))
            {
                if (!afterSpace)
                    sb.Append(' ');
                afterSpace = true;
                continue;
            }
            afterSpace = false;
            if (Ordinary(c))
                sb.Append(c);
            else if (c == (char)10)
                sb.Append('\\').Append('n');
            else if (c == (char)13)
                sb.Append('\\').Append('r');
            else if (c == (char)9)
                sb.Append('\\').Append('t');
            else
                sb.Append('\\').Append('u').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
