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

    /// <summary>
    /// Someone comes to hold a fact they already held, the same way and as firmly, on the strength of another
    /// passage. A record that leaves a person exactly where they were says the passage taught them something
    /// the fact as worded has no room for: it joins details that are learned at different times, and the first
    /// passage gave them only one. Two details are separate facts when someone could learn one without the
    /// other. Both passages are shown side by side, since that is how the join is seen.
    /// </summary>
    /// <remarks>
    /// Left alone: a fact that began or stopped being so in between, where taking it up again is news; records
    /// that share a story time, which tie and are reported as that; and a record with no passage or source of
    /// its own, which claims to teach nothing.
    /// </remarks>
    public static IReadOnlyList<LintNote> LearnedTwice(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var found = new List<(BeliefChange Again, LintNote Note)>();
        foreach (var holder in s.Holders)
            foreach (var records in s.Model.BeliefsOf(holder).GroupBy(b => b.Prop, StringComparer.Ordinal))
            {
                BeliefChange? last = null;
                foreach (var b in records.OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal))
                {
                    if (last is not null && LearnedAgain(s, last, b) is { } note)
                        found.Add((b, note));
                    last = b;
                }
            }
        return found.OrderBy(x => x.Again.StoryKey).ThenBy(x => x.Again.Id, StringComparer.Ordinal).Select(x => x.Note).ToList();
    }

    /// <summary>
    /// The note for a belief change that leaves its holder where an earlier one put them, or null when it does
    /// not. Asked of one pair of records, so that a scene submission can be checked as it comes in.
    /// </summary>
    public static LintNote? LearnedAgain(StoryState s, BeliefChange before, BeliefChange again)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(again);
        if (again.Stance is not (Stance.HoldsTrue or Stance.HoldsFalse) || again.Stance != before.Stance || again.Strength != before.Strength
            || !(again.StoryKey > before.StoryKey) || (again.Via is null && again.Quote is null)
            || (again.Via == before.Via && again.Quote == before.Quote)
            || s.Model.Proposition(again.Prop) is not { } prop || s.TruthAt(again.Prop, before.StoryKey) != s.TruthAt(again.Prop, again.StoryKey))
            return null;
        string Passage(BeliefChange b) => Evidence.Of(s, b) is { } p ? p.ToString() : "no passage cited";
        var way = $"{(again.Stance == Stance.HoldsTrue ? "as true" : "as false")}{(again.Strength is { } st ? ", " + Names.Of(st) : "")}";
        return new LintNote("LEARNED_TWICE", again.Id,
            $"{Who(s, again.Holder)} already takes {again.Prop} {way} ({before.Id}: {Passage(before)}), and {again.Id} has them come to it again from {Passage(again)}. The second record changes nothing. "
            + $"If the later passage gives them something the earlier one did not, {again.Prop} joins more than one detail: \"{Plain.OneLine(prop.Text)}\". "
            + "Two details are separate facts when someone could learn them at different times: give the new detail a proposition of its own");
    }

    /// <summary>
    /// A statement made in earnest that tells nobody anything: everyone it is said to was already certain of
    /// the fact. People do repeat what is known. But a passage in which someone is told a thing again usually
    /// tells them more than the fact as worded holds, and whoever recorded the statement filed the new detail
    /// under the old fact. Each hearer is shown with the passage they were certain from, so the two can be read
    /// side by side.
    /// </summary>
    /// <remarks>
    /// Left alone: a lie; narration and thought, which are told to nobody; a statement that reaches anyone who
    /// was less than certain, since for them it does something; and a hearer with a record of their own on the
    /// fact from that moment to the end of the scene, which is the case <see cref="LearnedTwice"/> looks at.
    /// </remarks>
    public static IReadOnlyList<LintNote> ToldAgain(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Model.Assertions.Values.OrderBy(a => a.StoryKey).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => ToldAgain(s, a)).OfType<LintNote>().ToList();
    }

    /// <summary>The note for one statement that tells nobody anything, or null. Asked of one record, so that a scene submission can be checked as it comes in.</summary>
    public static LintNote? ToldAgain(StoryState s, Assertion a)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(a);
        if (a.Type is not (AssertionKind.Dialogue or AssertionKind.Document) || a.Sincerity == Sincerity.Insincere
            || s.Model.Proposition(a.Prop) is not { } prop || s.ClassifyAssertion(a.Id).StartsWith("lie", StringComparison.Ordinal))
            return null;
        var told = a.Addressees.Concat(a.ReadBy is null ? [] : [a.ReadBy]).Select(s.Model.Base).Distinct()
            .Where(who => who != s.Model.Base(a.Speaker) && s.Model.Entity(who)?.Type == EntityKind.Character)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (told.Count == 0)
            return null;
        var end = s.Model.SceneTimes.ContainsKey(a.Scene) ? s.SceneEnd(a.Scene) : double.PositiveInfinity;
        var already = new List<string>();
        foreach (var who in told)
        {
            var theirs = s.Model.BeliefsOf(who, a.Prop);
            if (StoryState.Latest(theirs, a.StoryKey) is not { Unresolved: false, Strength: Strength.Certain } held
                || held.Stance != (a.Polarity ? Stance.HoldsTrue : Stance.HoldsFalse)
                || theirs.Any(b => b.StoryKey >= a.StoryKey && b.StoryKey < end))
                return null;
            already.Add($"{Who(s, who)} ({held.Id}: {(Evidence.Of(s, theirs.First(b => b.Id == held.Id)) is { } p ? p.ToString() : "no passage cited")})");
        }
        return new LintNote("TOLD_AGAIN", a.Id,
            $"{Who(s, a.Speaker)} {(a.Polarity ? "asserts" : "denies")} {a.Prop} in {a.Scene}, from {Evidence.OfSource(s, a.Id)}, and everyone it is said to was already certain of that: {string.Join("; ", already)}. "
            + $"If this passage gives them something the earlier one did not, {a.Prop} joins more than one detail: \"{Plain.OneLine(prop.Text)}\". "
            + "Two details are separate facts when someone could learn them at different times: give the new detail a proposition of its own");
    }

    /// <summary>
    /// A fact an embargo holds back is said on the page before the embargo lifts, and nothing records what the
    /// reader is given by it. A scene submission that would bring this about is refused; what is listed here was
    /// in the ledger already, or came about when an embargo was added or a scene was moved.
    /// </summary>
    public static IReadOnlyList<LintNote> SaidUnderEmbargo(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.SaidUnderEmbargo().Select(x => new LintNote("EMBARGO_UNDECLARED", x.Said.Id, Undeclared(s, x.Embargo, x.Said))).ToList();
    }

    /// <summary>What is wrong with a statement of an embargoed fact that no disclosure goes with, and what to do about it.</summary>
    public static string Undeclared(StoryState s, Embargo embargo, Assertion said)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(embargo);
        ArgumentNullException.ThrowIfNull(said);
        var until = embargo.UntilScene + (s.Tell(embargo.UntilScene) is null ? ", which is not in the manuscript yet" : "");
        return $"{Who(s, said.Speaker)} {(said.Polarity ? "asserts" : "denies")} {said.Prop} on the page in {said.Scene} (\"{Plain.OneLine(said.Quote)}\"), embargo {embargo.Id} holds that back from the reader until {until}, "
            + $"and no record says what the reader is given by those words. Add a disclosure for {said.Prop} in {said.Scene} that says how the reader takes them, so that the embargo is checked against it. "
            + "A hint the reader may miss is allowed; if the words leave the reader sure, change the text";
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
        Section(sb, "People who learn the same thing twice, which is how a fact that joins two details shows", LearnedTwice(s));
        Section(sb, "Statements that tell nobody anything new, where the passage may hold a detail the fact does not", ToldAgain(s));
        Section(sb, "Embargoed facts said on the page with no record of what the reader was given", SaidUnderEmbargo(s));
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
