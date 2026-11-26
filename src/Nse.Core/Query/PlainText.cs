using System.Globalization;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>
/// A record as one sentence in plain words, for an author with twenty-five of
/// them to review for every scene. People are named, facts are given in their words rather than by id, and the
/// quote that is the record's evidence follows. Whatever a record says is printed as one line.
/// </summary>
public static class PlainText
{
    public static string Sentence(StoryState s, LedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(record);
        return Plain.OneLine(Compose(s, record));
    }

    private static string Compose(StoryState s, LedgerRecord record)
    {
        string Who(string id) => s.Model.Entity(id) is not null ? s.Short(id) : id;
        string Fact(string prop) => s.Model.Proposition(prop) is { } p ? $"\"{p.Text.Trim().TrimEnd('.')}\" ({prop})" : prop;
        string Quote(string? q) => q is null ? "" : $" The text: \"{QuoteMatcher.Normalize(q)}\".";
        string When(double? key) => key is double k ? string.Create(CultureInfo.InvariantCulture, $"from story time {k}") : "from the outset";
        string Where(string? scene) => scene is null ? "Off the page" : $"In {scene}";

        switch (record)
        {
            case Event e:
                var roles = e.Participants.Select(p => p.Value switch
                {
                    ParticipantRole.Actor => $"{Who(p.Key)} takes part",
                    ParticipantRole.Witness => $"{Who(p.Key)} sees it",
                    _ => $"{Who(p.Key)} is addressed",
                }).ToList();
                return $"{Where(e.Scene)}, {e.Summary.Trim().TrimEnd('.')}{(roles.Count == 0 ? "" : $" ({string.Join("; ", roles)})")}.{Quote(e.Quote)}";

            case Assertion a:
                var verb = a.Type switch
                {
                    AssertionKind.Narration => $"the narration, through {Who(a.Focalizer ?? a.Speaker)}, says",
                    AssertionKind.Document => $"{Who(a.Speaker)} writes",
                    AssertionKind.Thought => $"{Who(a.Speaker)} thinks",
                    _ => $"{Who(a.Speaker)} says",
                };
                var to = a.Addressees.Count == 0 ? "" : " to " + string.Join(" and ", a.Addressees.Select(Who));
                var heard = a.Overhearers.Count == 0 ? "" : $", and {string.Join(" and ", a.Overhearers.Select(Who))} overhear{(a.Overhearers.Count == 1 ? "s" : "")} it";
                var classified = s.Model.Assertions.ContainsKey(a.Id) ? $" By the records this is {s.ClassifyAssertion(a.Id)}." : "";
                return $"In {a.Scene}, {verb}{to} that {(a.Polarity ? "" : "it is not so that ")}{Fact(a.Prop)}{heard}.{classified}{Quote(a.Quote)}";

            case BeliefChange b:
                var how = b.Stance switch
                {
                    Stance.HoldsTrue => b.Strength switch { Strength.Certain => "becomes sure", Strength.Suspects => "comes to suspect", _ => "comes to believe" } + " that ",
                    Stance.HoldsFalse => b.Strength switch { Strength.Certain => "becomes sure it is not so that ", Strength.Suspects => "comes to doubt that ", _ => "comes to believe it is not so that " },
                    Stance.Undecided => "cannot decide whether ",
                    _ => "does not know that ",
                };
                var by = b.Channel switch
                {
                    Channel.Witnessed => "by seeing it",
                    Channel.TookPart => "by taking part",
                    Channel.Told => "by being told",
                    Channel.Overheard => "by overhearing it",
                    Channel.Read => "by reading it",
                    Channel.Inferred => "by working it out",
                    Channel.Assumed => "by assuming it",
                    Channel.Background => "as something long known",
                    _ => "as the text says outright",
                };
                var marked = b.Directness == Directness.Stated ? "" : $" It is marked {Names.Of(b.Directness)}, not stated in the text.";
                return $"{Where(b.Scene)}, {Who(b.Holder)} {how}{Fact(b.Prop)}, {by}{(b.Via is null ? "" : $" ({b.Via})")}.{marked}{Quote(b.Quote)}";

            case Disclosure d:
                var given = d.Mode switch
                {
                    DisclosureMode.Stated => "is told outright",
                    DisclosureMode.Implied => "is given a hint",
                    DisclosureMode.Focalized => "is shown, through a character's eyes,",
                    DisclosureMode.AssertedByCharacter => "is told by a character",
                    DisclosureMode.Inferable => "is left able to work out",
                    _ => "is made to ask",
                };
                var takes = d.ReaderStance switch
                {
                    Stance.HoldsTrue => "as true",
                    Stance.HoldsFalse => "as false",
                    _ => "with no view",
                } + (d.Strength is { } st ? $", {Names.Of(st)}" : "");
                return $"In {d.Scene}, the reader {given} that {Fact(d.Prop)}, and takes it {takes}.{Quote(d.Quote)}";

            case Placement p:
                return $"{Where(p.Scene)}, {Who(p.Object)} moves to {Who(p.Place)}, because of {p.Cause}.{Quote(p.Quote)}";

            case FactChange f:
                return $"{char.ToUpperInvariant(When(f.StoryKey)[0])}{When(f.StoryKey)[1..]}, it is {(f.Value ? "so" : "not so")} that {Fact(f.Prop)}.";

            case Proposition p:
                return $"A new fact to keep track of: \"{p.Text.Trim()}\"{(p.Resolution == Resolution.Decided ? "" : $", left {Names.Of(p.Resolution)}")}.";

            case Entity e:
                return $"A new {Names.Of(e.Type)}: {e.Name}{(e.PersonaOf is { } behind ? $", which is {Who(behind)} as others see them" : "")}.";

            default:
                return RecordText.Line(s, record);
        }
    }

    /// <summary>What a change set's records come to, in one paragraph: how many of each kind, and how many are flagged.</summary>
    public static string Summary(IReadOnlyList<LedgerRecord> records, int flagged, string? scene)
    {
        ArgumentNullException.ThrowIfNull(records);
        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        var parts = new List<string>();
        void Add(int n, string one, string many)
        {
            if (n > 0)
                parts.Add(Count(n, one, many));
        }
        Add(records.OfType<Event>().Count(), "thing that happens", "things that happen");
        Add(records.OfType<Assertion>().Count(), "thing said", "things said");
        Add(records.OfType<BeliefChange>().Count(), "change in what someone believes", "changes in what people believe");
        Add(records.OfType<Disclosure>().Count(), "thing the reader is given", "things the reader is given");
        Add(records.OfType<Placement>().Count(), "move of a tracked object", "moves of tracked objects");
        Add(records.OfType<FactChange>().Count(), "change in what is so", "changes in what is so");
        var other = records.Count(r => r is not (Event or Assertion or BeliefChange or Disclosure or Placement or FactChange));
        Add(other, "other record", "other records");
        var text = $"{(scene is null ? "This change set" : $"This submission for {scene}")} records {(parts.Count == 0 ? "nothing still waiting" : Join(parts))}.";
        if (flagged > 0)
            text += $" {(flagged == 1 ? "One is" : $"{flagged} are")} flagged: a belief the text does not state, which is ratified only by naming it.";
        return text;
    }

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
