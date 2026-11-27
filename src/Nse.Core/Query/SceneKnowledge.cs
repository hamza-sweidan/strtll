using Nse.Model;

namespace Nse.Query;

/// <summary>What a person can be shown knowing in one scene.</summary>
public enum KnowledgeStatus
{
    /// <summary>Right about it on entry.</summary>
    Knows,

    /// <summary>Not right on entry, right by the end of the scene.</summary>
    LearnsHere,

    /// <summary>Holds the opposite by the end of the scene.</summary>
    Wrong,

    /// <summary>The story says outright that they do not know, or a secret shuts them out.</summary>
    Unaware,

    /// <summary>
    /// There is a record, and it does not say they know: they are undecided, their records tie and disagree,
    /// or what they hold is about a fact whose own truth is not settled at this point.
    /// </summary>
    Unsettled,

    /// <summary>Nothing is recorded either way. That is not evidence of ignorance.</summary>
    NoRecord,
}

/// <summary>
/// A guarded fact a person must not say, be told or act on in a scene, and when they learn it, if ever.
/// <see cref="LearnsAt"/> is set whenever they do; <see cref="LearnsIn"/> only when that happens in a scene.
/// </summary>
public sealed record Gap(string Prop, KnowledgeStatus Status, string? LearnsIn, double? LearnsAt = null);

/// <summary>A disclosure that states an embargoed fact to the reader in a scene told before the embargo lifts.</summary>
public sealed record EmbargoBreach(Embargo Embargo, Disclosure Disclosure);

/// <summary>A record that settles a question a rule says is never to be settled.</summary>
public sealed record RuleBreach(Rule Rule, string RecordId, string How);

/// <summary>
/// The questions a context package for one scene rests on.
/// </summary>
public static class SceneKnowledge
{
    /// <summary>The story key from which a proposition is about anything at all: its first fact change.</summary>
    public static double Starts(this StoryState s, string propId)
    {
        var (first, any) = (double.PositiveInfinity, false);
        foreach (var f in s.Model.Facts(propId))
            (first, any) = (Math.Min(first, f.StoryKey ?? double.NegativeInfinity), true);
        return any ? first : double.NegativeInfinity;
    }

    /// <summary>
    /// Just past everything that happens during a scene, and never into the next one in story time. The
    /// margin of five is a convention for how far a scene's own records may run.
    /// </summary>
    public static double SceneEnd(this StoryState s, string sceneId)
    {
        var key = s.Key(sceneId);
        return Math.Min(key + 5, s.NextSceneKey(key));
    }

    /// <summary>
    /// A fact worth guarding: decided by canon and not handed to the reader in the first scene. Beliefs about
    /// beliefs are left out, since they follow from the rest.
    /// </summary>
    public static bool Guarded(this StoryState s, string propId) =>
        s.GuardedFacts.GetOrAdd(propId, static (id, state) => IsGuarded(state, id), s);

    private static bool IsGuarded(StoryState s, string propId)
    {
        var p = s.Model.Proposition(propId);
        if (p is null || p.Resolution != Resolution.Decided || p.Nested is not null)
            return false;
        // A proposition nobody has said is so or not so has nothing to guard yet.
        if (s.Canon(propId) is not ("true" or "false"))
            return false;
        var reveal = s.RevealScene(propId);
        return s.Canon(propId) == "false" || reveal is null || s.Tell(reveal) > 1;
    }

    /// <summary>
    /// Guarded facts that exist by the end of a scene and have not lapsed before it. "Lapsed" is settled:
    /// the fact was true and, just before the scene, is not. A fact that stops being true at the scene's own
    /// story time lapses in the scene, not before it. One whose fact changes tie and disagree has not settled
    /// as lapsed, so it stays in play, whichever of the tied records happened to be entered last.
    /// </summary>
    public static IReadOnlyList<string> InPlay(this StoryState s, string sceneId) =>
        s.FactsInPlay.GetOrAdd(sceneId, static (id, state) => FindInPlay(state, id), s);

    private static List<string> FindInPlay(StoryState s, string sceneId)
    {
        var (key, end) = (s.Key(sceneId), s.SceneEnd(sceneId));
        return s.Model.Propositions.Select(p => p.Id)
            .Where(id => s.Guarded(id) && s.Starts(id) < end && !s.Expired(id, key, before: true))
            .ToList();
    }

    /// <summary>
    /// Truths the reader is given only in a later scene. "Later" is reading order, but a proposition that is
    /// not the case by the end of this scene in story time is not a withheld truth and is left out. One that
    /// comes about during the scene is withheld: it happens here and the reader is told later.
    /// </summary>
    public static IReadOnlyList<(string Prop, string RevealScene)> MustNotReveal(this StoryState s, string sceneId)
    {
        var tell = s.Tell(sceneId) ?? throw new KeyNotFoundException($"Scene {sceneId} has no marker in the manuscript.");
        var end = s.SceneEnd(sceneId);
        return s.Model.Propositions
            .Select(p => (p.Id, Reveal: s.RevealScene(p.Id)))
            .Where(x => x.Reveal is not null && s.Tell(x.Reveal) > tell && s.Starts(x.Id) < end)
            .OrderBy(x => s.Tell(x.Reveal!)).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => (x.Id, x.Reveal!))
            .ToList();
    }

    /// <summary>
    /// The author's embargoes that hold in a scene: every one whose lifting scene is told later, or is not in
    /// the manuscript yet. Unlike <see cref="MustNotReveal"/> this does not depend on the revealing scene having
    /// been written and recorded, so it protects a fact while the scenes before its reveal are being drafted.
    /// </summary>
    public static IReadOnlyList<Embargo> EmbargoesInForce(this StoryState s, string sceneId)
    {
        var tell = s.Tell(sceneId) ?? throw new KeyNotFoundException($"Scene {sceneId} has no marker in the manuscript.");
        return s.Model.Embargoes.Where(e => s.Tell(e.UntilScene) is not int until || until > tell)
            .OrderBy(e => s.Tell(e.UntilScene) ?? int.MaxValue).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Records that settle what a "never settle" rule covers: a disclosure that states it to the reader or
    /// leaves the reader certain of it, and any fact change of it. What characters believe is left alone: the
    /// rule is about the book, not about the people in it.
    /// </summary>
    public static IReadOnlyList<RuleBreach> RuleBreaches(this StoryState s)
    {
        var result = new List<RuleBreach>();
        foreach (var rule in s.Model.Rules.Where(r => r.Type is "never_resolve" or "never_establish" && r.Prop is not null))
        {
            result.AddRange(s.Model.Disclosures
                .Where(d => d.Prop == rule.Prop && d.ReaderStance is Stance.HoldsTrue or Stance.HoldsFalse
                            && (d.Mode == DisclosureMode.Stated || d.Strength == Strength.Certain))
                .Select(d => new RuleBreach(rule, d.Id, $"gives the reader {rule.Prop} as settled in {d.Scene}")));
            result.AddRange(s.Model.Facts(rule.Prop!).Select(f => new RuleBreach(rule, f.Id, $"has canon decide {rule.Prop}")));
        }
        return result;
    }

    /// <summary>
    /// Disclosures that give an embargoed fact away in a scene told before the embargo lifts. A hint the reader
    /// may or may not pick up is not a breach. Stating it is, and so is anything else that leaves the reader
    /// certain of it, whoever says it. For a decided fact, "it" is the fact the way canon has it: narration
    /// that states the opposite is misdirection, not a reveal. The order is the telling as the manuscript now
    /// stands, so moving a scene can create a breach without any record changing.
    /// </summary>
    public static IReadOnlyList<EmbargoBreach> EmbargoBreaches(this StoryState s)
    {
        var result = new List<EmbargoBreach>();
        foreach (var e in s.Model.Embargoes)
        {
            var until = s.Tell(e.UntilScene);
            var canon = s.Model.Proposition(e.Prop) is null ? null : s.Canon(e.Prop);
            Stance? way = canon switch { "true" => Stance.HoldsTrue, "false" => Stance.HoldsFalse, _ => null };
            foreach (var d in s.Model.DisclosuresOf(e.Prop))
            {
                var takesIt = way is null ? d.ReaderStance is Stance.HoldsTrue or Stance.HoldsFalse : d.ReaderStance == way;
                var given = d.Mode == DisclosureMode.Stated || d.Strength == Strength.Certain;
                if (takesIt && given && s.Tell(d.Scene) is int at && (until is null || at < until))
                    result.Add(new EmbargoBreach(e, d));
            }
        }
        return result;
    }

    /// <summary>
    /// Statements on the page of a fact an embargo holds back, in a scene told before the embargo lifts, with no
    /// record in that scene of what the reader is given. The words are in the text, so the reader has read
    /// them. What the reader takes from them is a judgement, and nobody has made it, so the embargo has nothing
    /// to be checked against: <see cref="EmbargoBreaches"/> looks at disclosures and there is none. A statement
    /// that goes the other way from canon is misdirection, and is not listed.
    /// </summary>
    public static IReadOnlyList<(Embargo Embargo, Assertion Said)> SaidUnderEmbargo(this StoryState s)
    {
        var result = new List<(Embargo, Assertion)>();
        foreach (var e in s.Model.Embargoes)
        {
            var until = s.Tell(e.UntilScene);
            var canon = s.Model.Proposition(e.Prop) is null ? null : s.Canon(e.Prop);
            bool? way = canon switch { "true" => true, "false" => false, _ => null };
            foreach (var said in s.Model.AssertionsOf(e.Prop))
                if ((way is null || said.Polarity == way) && s.Tell(said.Scene) is int at && (until is null || at < until)
                    && !s.Model.DisclosuresIn(said.Scene).Any(d => d.Prop == e.Prop))
                    result.Add((e, said));
        }
        return result.OrderBy(x => x.Item2.StoryKey).ThenBy(x => x.Item2.Id, StringComparer.Ordinal).ThenBy(x => x.Item1.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a rule is one that keeps a question open for the whole book, which is checked and shown on its own.</summary>
    public static bool NeverSettle(Rule r) => r.Type is "never_resolve" or "never_establish";

    /// <summary>
    /// The author's rules and notes that hold in a scene, never-settle rules apart, in the order they entered the
    /// ledger. A rule with no scope holds everywhere. A span whose end has lost its place in story time cannot be
    /// worked out; such a rule is shown, with a caveat that says so, rather than dropped in silence.
    /// </summary>
    public static IReadOnlyList<(Rule Rule, string? Caveat)> RulesInScope(this StoryState s, string sceneId)
    {
        var scene = s.Model.Scenes.TryGetValue(sceneId, out var found) ? found : throw new KeyNotFoundException($"No scene record {sceneId}.");
        var present = scene.Cast.Select(s.Model.Base).ToHashSet(StringComparer.Ordinal);
        var key = s.Key(sceneId);
        var result = new List<(Rule, string?)>();
        foreach (var rule in s.Model.Rules.Where(r => !NeverSettle(r)))
        {
            if (rule.Scope is not { } scope)
            {
                result.Add((rule, null));
                continue;
            }
            if (scope.Scenes.Count > 0 && !scope.Scenes.Contains(sceneId))
                continue;
            if (scope.Holders.Count > 0 && !scope.Holders.Any(h => present.Contains(s.Model.Base(h))))
                continue;
            var unknown = new List<string>();
            bool Outside(string? end, Func<double, bool> outside)
            {
                if (end is null)
                    return false;
                if (s.Model.SceneTimes.TryGetValue(end, out var t))
                    return outside(t.StoryKey);
                unknown.Add(end);
                return false;
            }
            if (Outside(scope.FromScene, from => key < from) || Outside(scope.UntilScene, until => key >= until))
                continue;
            result.Add((rule, unknown.Count == 0 ? null
                : $"where it holds cannot be worked out: {string.Join(" and ", unknown)} has no place in story time now, so it is shown in case it holds"));
        }
        return result;
    }

    public static IReadOnlyList<string> RevealedHere(this StoryState s, string sceneId) =>
        s.Model.Propositions.Select(p => p.Id).Where(id => s.RevealScene(id) == sceneId).ToList();

    /// <summary>The secret that shuts a person out of a fact, if one does.</summary>
    public static Secret? Sealed(this StoryState s, string holder, string propId)
    {
        foreach (var secret in s.Model.SecretsOf(propId))
            if (!secret.KnownOnlyTo.Contains(holder))
                return secret;
        return null;
    }

    public static KnowledgeStatus Status(this StoryState s, string holder, string propId, string sceneId)
    {
        var (key, end) = (s.Key(sceneId), s.SceneEnd(sceneId));
        return Status(s, holder, propId, s.Model.BeliefsOf(holder, propId), key, end);
    }

    /// <param name="records">The person's belief changes about the fact.</param>
    /// <param name="key">The scene's story time.</param>
    /// <param name="end">Where the scene ends in story time.</param>
    private static KnowledgeStatus Status(StoryState s, string holder, string propId, IReadOnlyList<BeliefChange> records, double key, double end)
    {
        var onEntry = StoryState.Latest(records, key);
        var atEnd = StoryState.Latest(records, end);
        if (onEntry is not null && s.IsRight(onEntry, key) == true)
            return KnowledgeStatus.Knows;
        if (atEnd is not null && s.IsRight(atEnd, end) == true)
            return KnowledgeStatus.LearnsHere;
        if (atEnd is not null && s.IsRight(atEnd, end) == false)
            return KnowledgeStatus.Wrong;
        if (atEnd is { Stance: Stance.Unaware })
            return KnowledgeStatus.Unaware;
        if (atEnd is null && s.Sealed(holder, propId) is not null)
            return KnowledgeStatus.Unaware;
        // Someone with a record is never reported as having none, even when the record settles nothing.
        return atEnd is null ? KnowledgeStatus.NoRecord : KnowledgeStatus.Unsettled;
    }

    /// <summary>
    /// The belief change by which a person first gets it right after a scene, if they ever do. It may have no
    /// scene of its own: something learned off the page is still learned.
    /// </summary>
    public static BeliefChange? FirstRightAfter(this StoryState s, string holder, string propId, string sceneId)
    {
        var end = s.SceneEnd(sceneId);
        return FirstRightAfter(s, s.Model.BeliefsOf(holder, propId), end);
    }

    /// <param name="records">The person's belief changes about the fact.</param>
    /// <param name="end">Where the scene ends in story time.</param>
    private static BeliefChange? FirstRightAfter(StoryState s, IReadOnlyList<BeliefChange> records, double end)
    {
        if (records.Count == 0)
            return null;
        var later = records.Where(b => b.StoryKey >= end)
            .OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal);
        foreach (var b in later)
        {
            var view = new BeliefView(b.Id, b.Holder, b.Prop, b.Stance, b.Strength, b.StoryKey, b.Scene, b.Channel, b.Via);
            if (s.IsRight(view) == true)
                return b;
        }
        return null;
    }

    /// <summary>The scene in which a person first gets it right after this one. Null when they never do, and also when they do off the page.</summary>
    public static string? LearnsLater(this StoryState s, string holder, string propId, string sceneId) =>
        s.FirstRightAfter(holder, propId, sceneId)?.Scene;

    /// <summary>Guarded facts in play that a person must not say, be told or act on in a scene, including what they never learn.</summary>
    public static IReadOnlyList<Gap> KnowledgeGaps(this StoryState s, string holder, string sceneId) => s.Knowledge(holder, sceneId).Gaps;

    /// <summary>
    /// One person against the guarded facts in play in a scene: the gaps, and the facts they come to know during
    /// it. Both are read off in one pass, each fact's records for the person fetched once. In a long story the
    /// facts in play are most of the story's facts, and a person has no record on most of them.
    /// </summary>
    public static (IReadOnlyList<Gap> Gaps, IReadOnlyList<string> LearnsHere) Knowledge(this StoryState s, string holder, string sceneId)
    {
        var (key, end) = (s.Key(sceneId), s.SceneEnd(sceneId));
        var (gaps, learns) = (new List<Gap>(), new List<string>());
        foreach (var prop in s.InPlay(sceneId))
        {
            var records = s.Model.BeliefsOf(holder, prop);
            var status = Status(s, holder, prop, records, key, end);
            if (status == KnowledgeStatus.LearnsHere)
                learns.Add(prop);
            else if (status != KnowledgeStatus.Knows)
            {
                var later = FirstRightAfter(s, records, end);
                gaps.Add(new Gap(prop, status, later?.Scene, later?.StoryKey));
            }
        }
        return (gaps, learns);
    }

    /// <summary>
    /// What a person has heard said and has no record of their own on: for each such fact, the latest statement
    /// of it they were there for before a story time. Hearing is not believing. Someone who was in the room can
    /// repeat the words and say who spoke them; whether they took them as so is for a belief record to say, and
    /// here there is none.
    /// </summary>
    public static IReadOnlyList<Assertion> HeardOnly(this StoryState s, string person, double key)
    {
        var latest = new Dictionary<string, Assertion>(StringComparer.Ordinal);
        foreach (var said in s.Model.HeardBy(person))
        {
            if (!(said.StoryKey < key))
                continue;
            if (latest.TryGetValue(said.Prop, out var seen)
                && (seen.StoryKey > said.StoryKey || (seen.StoryKey == said.StoryKey && string.CompareOrdinal(seen.Id, said.Id) >= 0)))
                continue;
            latest[said.Prop] = said;
        }
        return latest.Values.Where(said => s.BeliefBefore(person, said.Prop, key) is null)
            .OrderBy(said => said.Prop, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The last scene a person was in before a scene, in story time. A scene set at the very same story time is
    /// not an earlier one. Null when they were on stage in none.
    /// </summary>
    public static SceneRecord? LastOnStage(this StoryState s, string person, string sceneId)
    {
        var key = s.Key(sceneId);
        return s.Model.Scenes.Values
            .Where(x => x.Id != sceneId && s.Model.SceneTimes.TryGetValue(x.Id, out var t) && t.StoryKey < key
                        && x.Cast.Select(s.Model.Base).Contains(person))
            .OrderByDescending(x => s.Model.SceneTimes[x.Id].StoryKey).ThenByDescending(x => s.Tell(x.Id) ?? -1).ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The people in the cast with the point-of-view character first, each with the person behind the cast
    /// entry. Someone with no belief records at all is still here: least is known about them, so they are the
    /// one whose ignorance most needs spelling out.
    /// </summary>
    public static IReadOnlyList<(string CastEntry, string Person)> CastHolders(this StoryState s, string sceneId)
    {
        var scene = s.Model.Scenes[sceneId];
        return new[] { scene.Pov }.Concat(scene.Cast.Where(c => c != scene.Pov))
            .Select(c => (c, s.Model.Base(c)))
            .Where(x => s.Model.Entity(x.Item2)?.Type == EntityKind.Character)
            .ToList();
    }
}
