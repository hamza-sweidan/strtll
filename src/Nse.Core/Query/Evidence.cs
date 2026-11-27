using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>
/// The words in the manuscript that a record rests on, and where they stand now. <see cref="File"/> and
/// <see cref="Line"/> are null when the words are no longer in the scene they were quoted from.
/// </summary>
public sealed record Passage(string Quote, string? Scene, string RecordId, string? File, int? Line)
{
    /// <summary>The passage as it is printed: the words, the scene, and the line its paragraph starts on.</summary>
    public override string ToString()
    {
        var where = Scene is null ? "no scene is named"
            : File is null ? $"{Scene}; the words are not in that scene's text as it stands"
            : FormattableString.Invariant($"{Scene}, manuscript/{File} line {Line}");
        return $"\"{Plain.OneLine(QuoteMatcher.Normalize(Quote))}\" ({where})";
    }
}

/// <summary>
/// The passage behind a record. A belief may quote the text itself, or rest on an event or a statement that
/// does. Nothing here judges whether the passage bears the record out: it is put in front of whoever asks.
/// </summary>
public static class Evidence
{
    /// <summary>The words a belief change quotes, or failing that the words of what it came from. Null when neither cites any.</summary>
    public static Passage? Of(StoryState s, BeliefChange b)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(b);
        return Own(s, b) ?? (b.Via is { } via ? OfSource(s, via) : null);
    }

    /// <summary>The words a belief change quotes itself.</summary>
    public static Passage? Own(StoryState s, BeliefChange b)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(b);
        return b.Quote is { } quote ? At(s, quote, b.QuoteScene ?? b.Scene, b.Id) : null;
    }

    /// <summary>The words of the event or statement a belief came from.</summary>
    public static Passage? OfSource(StoryState s, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Model.Assertions.TryGetValue(sourceId, out var said))
            return At(s, said.Quote, said.Scene, said.Id);
        return s.Model.Events.TryGetValue(sourceId, out var happened) && happened.Quote is { } quote
            ? At(s, quote, happened.QuoteScene ?? happened.Scene, happened.Id)
            : null;
    }

    /// <summary>The words a disclosure quotes.</summary>
    public static Passage? Of(StoryState s, Disclosure d)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(d);
        return d.Quote is { } quote ? At(s, quote, d.Scene, d.Id) : null;
    }

    /// <summary>
    /// The passage behind any record of what happens: the words it quotes, or for a belief that quotes none the
    /// words of what it came from. Null for a record that cites no words, and for one of a kind that never does.
    /// </summary>
    public static Passage? Of(StoryState s, LedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(s);
        return record switch
        {
            BeliefChange b => Of(s, b),
            Disclosure d => Of(s, d),
            Assertion a => At(s, a.Quote, a.Scene, a.Id),
            Event { Quote: { } quote } e => At(s, quote, e.QuoteScene ?? e.Scene, e.Id),
            Placement { Quote: { } quote } p => At(s, quote, p.QuoteScene ?? p.Scene, p.Id),
            _ => null,
        };
    }

    private static Passage At(StoryState s, string quote, string? scene, string record)
    {
        if (scene is not null && s.Telling.FirstOrDefault(t => t.Id == scene) is { } text)
        {
            var words = QuoteMatcher.Readable(quote);
            foreach (var block in text.Blocks)
                if (words.Length > 0 && QuoteMatcher.Readable(block.Text).Contains(words, StringComparison.Ordinal))
                    return new Passage(quote, scene, record, text.File, block.LineStart);
        }
        return new Passage(quote, scene, record, null, null);
    }
}
