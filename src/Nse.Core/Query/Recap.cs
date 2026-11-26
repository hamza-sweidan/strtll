using System.Globalization;
using System.Text;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>One thing in a person's story: something they did or saw, something said, or a change of mind.</summary>
public sealed record RecapItem(double Key, int Order, string Id, string? Scene, string Text);

/// <summary>
/// The story as one person knows it. In story-time order, up to a point: what they
/// took part in or saw, what was said to them, by them or in their hearing, what they read, and what they came to
/// believe. By default in their own terms, with no truth labels, which is material for writing in their voice;
/// with the author's labels on request. It is drawn from the records only, and says nothing about anyone else's mind.
/// </summary>
public static class Recap
{
    /// <summary>
    /// What a person did, saw, heard and came to believe from one story time up to, not including, another, in
    /// story-time order. The person is named "they"; everyone else by name.
    /// </summary>
    public static IReadOnlyList<RecapItem> Items(StoryState s, string person, double from, double to, bool truth)
    {
        ArgumentNullException.ThrowIfNull(s);
        bool Within(double k) => k >= from && k < to;
        // Everyone this person is, under any name: a persona is them as others saw them.
        var selves = s.Model.Entities.Where(e => e.Id == person || e.PersonaOf == person).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        string Name(string id) => selves.Contains(id) ? "they" : Plain.OneLine(s.Model.Entity(id)?.Name ?? id);
        string Fact(string prop) => s.Model.Proposition(prop) is { } p ? Plain.OneLine(p.Text) : prop;

        var items = new List<RecapItem>();
        foreach (var e in s.Model.Events.Values.Where(e => Within(e.StoryKey)))
        {
            var mine = e.Participants.Where(p => selves.Contains(p.Key)).Select(p => p.Value).ToList();
            if (mine.Count == 0)
                continue;
            var role = mine.Contains(ParticipantRole.Actor) ? "took part" : mine.Contains(ParticipantRole.Addressee) ? "were addressed" : "saw it";
            var others = e.Participants.Keys.Where(k => !selves.Contains(k)).Select(Name).ToList();
            items.Add(new RecapItem(e.StoryKey, 0, e.Id, e.Scene,
                $"{Plain.OneLine(e.Summary)} ({role}{(others.Count > 0 ? "; with " + string.Join(", ", others) : "")})"));
        }
        foreach (var a in s.Model.Assertions.Values.Where(a => Within(a.StoryKey)))
        {
            var says = a.Polarity ? "that" : "that it is not so that";
            string? line = null;
            if (selves.Contains(a.Speaker) && a.Type != AssertionKind.Narration)
                line = a.Type switch
                {
                    AssertionKind.Thought => $"they thought {says}: {Fact(a.Prop)}",
                    AssertionKind.Document => $"they wrote {says}: {Fact(a.Prop)}",
                    _ => $"they said{(a.Addressees.Count > 0 ? " to " + string.Join(", ", a.Addressees.Select(Name)) : "")} {says}: {Fact(a.Prop)}",
                };
            else if (a.Addressees.Any(selves.Contains))
                line = $"{Name(a.Speaker)} told them {says}: {Fact(a.Prop)}";
            else if (a.Overhearers.Any(selves.Contains))
                line = $"they overheard {Name(a.Speaker)} say {says}: {Fact(a.Prop)}";
            else if (a.ReadBy is { } reader && selves.Contains(reader))
                line = $"they read, in what {Name(a.Speaker)} wrote, {says}: {Fact(a.Prop)}";
            if (line is null)
                continue;
            if (truth)
                line += $" [{s.ClassifyAssertion(a.Id)}]";
            items.Add(new RecapItem(a.StoryKey, 1, a.Id, a.Scene, line));
        }
        foreach (var b in s.Model.Beliefs.Where(b => b.Holder == person && Within(b.StoryKey)))
        {
            var how = b.Stance switch
            {
                Stance.HoldsTrue => b.Strength switch
                {
                    Strength.Certain => "came to be sure",
                    Strength.Suspects => "came to suspect",
                    _ => "came to believe",
                } + " that",
                Stance.HoldsFalse => b.Strength switch
                {
                    Strength.Certain => "came to be sure it is not so that",
                    Strength.Suspects => "came to doubt that",
                    _ => "came to believe it is not so that",
                },
                Stance.Undecided => "could not decide whether",
                _ => "did not know that",
            };
            var line = $"they {how}: {Fact(b.Prop)}"
                       + (b.Channel is Channel.Background ? " (as they always had)" : $" ({Names.Of(b.Channel)}{(b.Via is null ? "" : ", from " + b.Via)})");
            if (truth)
            {
                var view = new BeliefView(b.Id, b.Holder, b.Prop, b.Stance, b.Strength, b.StoryKey, b.Scene, b.Channel, b.Via);
                line += $" [{s.Classify(view)}]";
            }
            items.Add(new RecapItem(b.StoryKey, 2, b.Id, b.Scene, line));
        }
        return items.OrderBy(i => i.Key).ThenBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>A story time as the recap prints it.</summary>
    public static string At(double key) =>
        double.IsNegativeInfinity(key) ? "before the story" : key.ToString(CultureInfo.InvariantCulture);

    public static string Text(StoryState s, string holder, string? atScene, bool truth)
    {
        ArgumentNullException.ThrowIfNull(s);
        _ = s.Model.Entity(holder) ?? throw new KeyNotFoundException($"No entity {holder}.");
        var person = s.Model.Base(holder);
        var who = s.Model.Entity(person)!;
        if (who.Type != EntityKind.Character)
            throw new ArgumentException($"{holder} is not a person, so it has no story of its own to recap");
        double key;
        string where;
        if (atScene is null)
        {
            key = StoryState.End;
            where = "to the end of the story";
        }
        else
        {
            key = s.Key(atScene);
            where = string.Create(CultureInfo.InvariantCulture,
                $"up to entry to {(s.Tell(atScene) is int n ? $"scene {n}, " : "")}{atScene} (story time {key})");
        }
        var items = Items(s, person, double.NegativeInfinity, key, truth);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"RECAP FOR {Plain.OneLine(who.Name)} ({person}), {where} · ledger head {s.Model.Head}\n");
        sb.Append(truth
            ? "With the author's labels in square brackets: what was really so, which they may not know.\n"
            : "In their own terms: what they did, saw, heard and came to believe, with no word on whether any of it is so.\n");
        if (person != holder)
            sb.Append(CultureInfo.InvariantCulture, $"{holder} is a persona of {person}; the recap is the person's.\n");
        if (items.Count == 0)
        {
            sb.Append("Nothing is recorded for them before this point. That is a gap in the records, not a blank life.\n");
            return sb.ToString();
        }
        string In(string? scene) => scene is null ? "off the page" : (s.Tell(scene) is int n ? $"scene {n}, {scene}" : scene);
        string? heading = null;
        foreach (var item in items)
        {
            var here = In(item.Scene);
            if (here != heading)
            {
                sb.Append(here == "off the page" ? "Off the page:\n" : $"In {here}:\n");
                heading = here;
            }
            sb.Append(CultureInfo.InvariantCulture, $"  {At(item.Key)}  {item.Text} ({item.Id})\n");
        }
        return sb.ToString();
    }
}
