using System.Globalization;
using System.Text;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>One reveal the author has embargoed, with the earlier disclosures that prepare the reader for it.</summary>
public sealed record SeededReveal(Embargo Embargo, IReadOnlyList<Disclosure> Seeds);

/// <summary>One thing a lint suggests a person look at, with the record it starts from.</summary>
public sealed record LintNote(string Code, string RecordId, string Message);

/// <summary>
/// Checks that suggest and do not infer. None of them writes a belief or
/// refuses anything. Each names a place where the records are likely to be short of the text or of the author's
/// intent, for a person to look at. They are deterministic, and a clean story can still have some: a reveal
/// with no seed may be meant as a shock, and a witness may truly have noticed nothing.
/// </summary>
public static class Lint
{
    /// <summary>
    /// The mystery writer's fair-play rule. For every embargoed fact, the disclosures that give the reader any
    /// of it in scenes told before the embargo lifts: a hint, an implication, a question posed, narration through
    /// someone, a claim, something made inferable. A reveal with none came from nowhere, as far as the records say.
    /// </summary>
    public static IReadOnlyList<SeededReveal> Seeds(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Model.Embargoes
            .OrderBy(e => s.Tell(e.UntilScene) ?? int.MaxValue).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new SeededReveal(e, s.Model.DisclosuresOf(e.Prop)
                .Where(d => s.Tell(d.Scene) is int at && (s.Tell(e.UntilScene) is not int until || at < until))
                .OrderBy(d => s.Tell(d.Scene)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>
    /// Someone else took part in or saw the event by which a person came to hold a fact, and has no record on that
    /// fact at all. Either they did not notice, which may be worth saying, or a record is missing. People
    /// the fact is about are left out, since what they know of themselves is another matter.
    /// </summary>
    public static IReadOnlyList<LintNote> CoWitnesses(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var holders = s.Holders.ToHashSet(StringComparer.Ordinal);
        var notes = new List<LintNote>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var b in s.Model.Beliefs.Where(b => b.Stance == Stance.HoldsTrue && b.Via is not null).OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            if (!s.Model.Events.TryGetValue(b.Via!, out var e) || s.Model.Proposition(b.Prop) is not { } prop)
                continue;
            var about = prop.About.Select(s.Model.Base).ToHashSet(StringComparer.Ordinal);
            foreach (var other in e.Participants.Keys.Select(s.Model.Base).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                if (other == b.Holder || !holders.Contains(other) || about.Contains(other) || !seen.Add((other, e.Id, b.Prop)))
                    continue;
                if (s.Model.BeliefsOf(other, b.Prop).Count > 0)
                    continue;
                notes.Add(new LintNote("CO_WITNESS", b.Id,
                    $"{Who(s, other)} was also at {e.Id}, where {Who(s, b.Holder)} came to hold {b.Prop} ({b.Id}), and has no record on {b.Prop} at all. Did they not notice, or is a record missing?"));
            }
        }
        return notes;
    }

    /// <summary>
    /// Someone was told a fact, in words or in writing, and has no record on it at all. A statement that is a lie
    /// is left out, since being told a lie may rightly leave no belief. Narration and thought are told to nobody.
    /// </summary>
    public static IReadOnlyList<LintNote> Addressees(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var notes = new List<LintNote>();
        foreach (var a in s.Model.Assertions.Values.Where(a => a.Type is AssertionKind.Dialogue or AssertionKind.Document)
                     .OrderBy(a => a.StoryKey).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (a.Sincerity == Sincerity.Insincere || s.ClassifyAssertion(a.Id).StartsWith("lie", StringComparison.Ordinal))
                continue;
            var told = a.Addressees.Concat(a.ReadBy is null ? [] : [a.ReadBy]).Select(s.Model.Base).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            foreach (var who in told)
            {
                if (s.Model.Entity(who)?.Type != EntityKind.Character || who == s.Model.Base(a.Speaker))
                    continue;
                if (s.Model.BeliefsOf(who, a.Prop).Count > 0)
                    continue;
                notes.Add(new LintNote("ADDRESSEE", a.Id,
                    $"{Who(s, who)} was told {a.Prop} by {Who(s, a.Speaker)} in {a.Scene} ({a.Id}), and has no record on {a.Prop} at all. Did they believe it, doubt it, or not take it in?"));
            }
        }
        return notes;
    }

    /// <summary>A proposition that says whom it is about can be found from the people in a scene; one that does not, cannot.</summary>
    public static IReadOnlyList<LintNote> EmptyAbout(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Model.Propositions.Where(p => p.About.Count == 0 && p.Nested is null)
            .Select(p => new LintNote("EMPTY_ABOUT", p.Id, $"{p.Id} names nobody and nothing it is about: \"{Plain.OneLine(p.Text)}\". A cut of the context to what a scene touches would never find it"))
            .ToList();
    }

    private static string Who(StoryState s, string id) => $"{Plain.OneLine(s.Model.Entity(id)?.Name ?? id)} ({id})";

    /// <summary>Every lint, in words, as both front ends print them.</summary>
    public static string Text(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"LINT · ledger head {s.Model.Head} · suggestions, not findings: each names a place for a person to look at, and none of it is a fault in itself\n");

        var seeds = Seeds(s);
        string Scene(string id) => s.Tell(id) is int n ? $"scene {n}, {id}" : $"{id} (not in the manuscript yet)";
        sb.Append(CultureInfo.InvariantCulture, $"\nReveals and what prepares the reader for them ({seeds.Count}):\n");
        if (seeds.Count == 0)
            sb.Append("  The author has embargoed nothing.\n");
        foreach (var r in seeds)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {r.Embargo.Id}: {r.Embargo.Prop} is held back until {Scene(r.Embargo.UntilScene)}. ");
            sb.Append(r.Seeds.Count == 0
                ? "NO SEED: nothing of it is given to the reader before then, as far as the records say.\n"
                : "Seeded in " + string.Join("; ", r.Seeds.Select(d => $"{Scene(d.Scene)} [{Names.Of(d.Mode)}] ({d.Id})")) + ".\n");
        }
        Section(sb, "People who were there and have no record on what someone else learned", CoWitnesses(s));
        Section(sb, "People who were told something and have no record on it", Addressees(s));
        Section(sb, "Propositions that say nothing of whom they are about", EmptyAbout(s));
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<LintNote> notes)
    {
        sb.Append(CultureInfo.InvariantCulture, $"\n{title} ({notes.Count}):\n");
        if (notes.Count == 0)
            sb.Append("  none\n");
        foreach (var n in notes)
            sb.Append("  ").Append(Plain.OneLine($"{n.Code} {n.RecordId}: {n.Message}")).Append('\n');
    }
}
