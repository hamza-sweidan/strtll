using System.Collections.Concurrent;
using Nse.Manuscript;
using Nse.Model;

namespace Nse.Query;

/// <summary>What one person makes of one fact, or a note that the records tie and the order is not established.</summary>
public sealed record BeliefView(
    string Id, string Holder, string Prop, Stance? Stance, Strength? Strength, double StoryKey, string? Scene,
    Channel? Channel, string? Via, IReadOnlyList<string>? Conflict = null)
{
    public bool Unresolved => Conflict is not null;
}

public sealed record ReaderView(
    string Id, string Scene, string Prop, DisclosureMode Mode, Stance? Stance, Strength? Strength,
    IReadOnlyList<string>? Conflict = null)
{
    public bool Unresolved => Conflict is not null;
}

public sealed record LocationView(
    string Id, string Object, string? Place, double StoryKey, string? Scene, IReadOnlyList<string>? Conflict = null)
{
    public bool Unresolved => Conflict is not null;
}

/// <summary>
/// The point-in-time questions. Characters and objects are asked in story time, the reader in telling order.
/// "On entry to a scene" means strictly before it on the relevant axis. Records that tie are reported as
/// unresolved, never settled by guessing. Nothing here is stored: every answer is computed from the records.
/// </summary>
/// <remarks>
/// A context, an audit or a what-if asks the same few things of one state thousands of times: where a fact is
/// first told, which facts are in play in a scene. Those are worked out from the records the first time they
/// are asked and kept in this object, which is built for one question and never changes. They are not a cache
/// of the story: they go when the object does, and nothing reads them back from anywhere.
/// </remarks>
public sealed class StoryState
{
    /// <summary>Use as the story key or telling rank to ask about the end of the story.</summary>
    public const double End = double.PositiveInfinity;

    public const int EndOfTelling = int.MaxValue;

    public StoryModel Model { get; }

    /// <summary>The hash of the manuscript the telling order was read from.</summary>
    public string ManuscriptHash { get; init; } = "";

    /// <summary>
    /// Whether the records were read from the index. False when they were replayed from the ledger files
    /// because the index could not be used. The answers are the same either way; this is here so that an index
    /// that is never used shows up as that, instead of hiding behind the fallback.
    /// </summary>
    public bool FromIndex { get; init; }

    /// <summary>How many change sets the ledger holds now, whatever head is being asked about.</summary>
    public int LedgerHead { get; init; }

    /// <summary>
    /// The longest this folder has seen the ledger. The index carries it forward from rebuild to rebuild, so it
    /// outlives ordinary reads. Zero when nothing is known, as when the index has just been deleted.
    /// </summary>
    public int SeenHead { get; init; }

    /// <summary>
    /// Set when the ledger is shorter than this folder has seen it. A ledger that has lost its newest change
    /// sets is a whole, shorter chain and loads without complaint, so nothing but this remembers the loss.
    /// </summary>
    public string? LedgerWarning => SeenHead > LedgerHead
        ? $"the ledger ends at change set {LedgerHead}, but this folder has seen it end at {SeenHead}. Either the newest change sets were removed or an older version was checked out. If that was not intended, restore the files under ledger/ from git. If it was, run 'nse rebuild' to accept the ledger as it stands"
        : null;

    /// <summary>Scenes in the order the reader meets them, taken from the manuscript files.</summary>
    public IReadOnlyList<ManuscriptScene> Telling { get; }

    private readonly Dictionary<string, int> _tell;
    private IReadOnlyList<string>? _holders;
    private IReadOnlyList<string>? _trackedObjects;
    private double[]? _sceneKeys;
    private readonly ConcurrentDictionary<string, string> _canon = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _revealScene = new(StringComparer.Ordinal);

    /// <summary>Whether each fact asked about is worth guarding. See <see cref="SceneKnowledge.Guarded"/>.</summary>
    internal ConcurrentDictionary<string, bool> GuardedFacts { get; } = new(StringComparer.Ordinal);

    /// <summary>The guarded facts in play in each scene asked about. See <see cref="SceneKnowledge.InPlay"/>.</summary>
    internal ConcurrentDictionary<string, IReadOnlyList<string>> FactsInPlay { get; } = new(StringComparer.Ordinal);

    public StoryState(StoryModel model, IReadOnlyList<ManuscriptScene> telling)
    {
        Model = model;
        Telling = telling;
        _tell = telling.ToDictionary(s => s.Id, s => s.Tell, StringComparer.Ordinal);
    }

    public double Key(string sceneId) =>
        Model.SceneTimes.TryGetValue(sceneId, out var t)
            ? t.StoryKey
            : throw new KeyNotFoundException($"Scene {sceneId} has no place in story time.");

    /// <summary>The story time of the first scene set after a story time, or infinity when none is.</summary>
    internal double NextSceneKey(double key)
    {
        var keys = _sceneKeys ??= [.. Model.SceneTimes.Values.Select(t => t.StoryKey).Order()];
        var (low, high) = (0, keys.Length);
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (keys[middle] > key)
                high = middle;
            else
                low = middle + 1;
        }
        return low < keys.Length ? keys[low] : double.PositiveInfinity;
    }

    /// <summary>The scene's rank in the telling, or null when it is not in the manuscript.</summary>
    public int? Tell(string sceneId) => _tell.TryGetValue(sceneId, out var t) ? t : null;

    /// <summary>People who have at least one belief record, in the order they entered the ledger.</summary>
    public IReadOnlyList<string> Holders =>
        _holders ??= Model.Entities.Select(e => e.Id).Where(id => Model.BeliefsOf(id).Count > 0).ToList();

    /// <summary>The short name a character goes by.</summary>
    public string Short(string entityId)
    {
        var e = Model.Entity(entityId) ?? throw new KeyNotFoundException($"No entity {entityId}.");
        return e.Type == EntityKind.Character && e.PersonaOf is null && e.Aliases.Count > 0 ? e.Aliases[0] : e.Name;
    }

    // ------------------------------------------------------------------ facts

    /// <summary>
    /// "true", "false", "open" (not decided yet), "ambiguous" (left open on purpose) or "unstated": marked as
    /// decided, with no fact change to say which way. The last is a gap in the records, not a "no": a
    /// proposition someone has only just written down is not thereby false.
    /// </summary>
    public string Canon(string propId) => _canon.GetOrAdd(propId, static (id, state) => state.FindCanon(id), this);

    private string FindCanon(string propId)
    {
        var p = Model.Proposition(propId) ?? throw new KeyNotFoundException($"No proposition {propId}.");
        return p.Resolution switch
        {
            Resolution.Open => "open",
            Resolution.Ambiguous => "ambiguous",
            _ when !Model.Facts(propId).Any() => "unstated",
            _ => Model.Facts(propId).Any(f => f.Value) ? "true" : "false",
        };
    }

    /// <summary>
    /// The records behind an answer about a fact that the author has not confirmed: the proposition itself if
    /// it is still provisional, and any provisional fact change of it that is in effect at the point asked
    /// about. Empty when the answer rests only on what has been ratified. Whatever is derived from these, a
    /// label or a "NOT true", is no firmer than they are and has to say so.
    /// </summary>
    public IReadOnlyList<string> Unconfirmed(string propId, double key = End, bool before = true)
    {
        bool Provisional(string id) => Model.ById.TryGetValue(id, out var t) && t.ApprovalAt(Model.Head) == Approval.Provisional;
        // Nearly every fact rests on ratified records alone, and this is asked for every line that names one.
        List<string>? changes = null;
        foreach (var f in Model.Facts(propId))
            if (InEffect(f, key, before) && Provisional(f.Id))
                (changes ??= []).Add(f.Id);
        changes?.Sort(StringComparer.Ordinal);
        return (Provisional(propId), changes) switch
        {
            (false, null) => [],
            (true, null) => [propId],
            (false, _) => changes,
            _ => [propId, .. changes],
        };
    }

    /// <summary>The words that mark an answer as resting on unconfirmed records, or nothing when it does not.</summary>
    public string UnconfirmedMark(string propId, double key = End, bool before = true) =>
        Unconfirmed(propId, key, before) is { Count: > 0 } ids ? $" [UNCONFIRMED: rests on provisional {string.Join(", ", ids)}]" : "";

    /// <summary>
    /// Whether a proposition is the case at a moment in story time, counting a fact change dated at that very
    /// moment. This is the question for a belief at the time it is acquired, or a statement at the time it is
    /// made. A decided proposition is false until something makes it true. Null when canon does not decide,
    /// or when fact changes tie and disagree (see <see cref="FactTie"/>).
    /// </summary>
    public bool? TruthAt(string propId, double key) => Truth(propId, key, before: false).Value;

    /// <summary>
    /// Whether a proposition is the case just before a story key. This is the question on entry to a scene:
    /// like a belief or a move dated at the scene's own key, a fact change dated there happens in the scene
    /// and has not happened yet.
    /// </summary>
    public bool? TruthBefore(string propId, double key) => Truth(propId, key, before: true).Value;

    /// <summary>The fact changes that share the latest story time and disagree, so that truth is not settled. Null when it is.</summary>
    public IReadOnlyList<string>? FactTie(string propId, double key, bool before) => Truth(propId, key, before).Conflict;

    /// <summary>Whether a proposition is the case at or just before a story key, and the fact changes that tie if that is not settled.</summary>
    internal (bool? Value, IReadOnlyList<string>? Conflict) Truth(string propId, double key, bool before)
    {
        var p = Model.Proposition(propId) ?? throw new KeyNotFoundException($"No proposition {propId}.");
        if (p.Resolution != Resolution.Decided)
            return (null, null);
        // The fact changes in effect that share the latest story time: what the first of them says, and whether
        // the rest agree. This is asked for every fact at every point, so it is one pass and keeps no list.
        var (recorded, found, latestKey, value, agreed) = (false, false, double.NegativeInfinity, false, true);
        foreach (var f in Model.Facts(propId))
        {
            recorded = true;
            if (!InEffect(f, key, before))
                continue;
            var k = f.StoryKey ?? double.NegativeInfinity;
            if (!found || k > latestKey)
                (found, latestKey, value, agreed) = (true, k, f.Value, true);
            else if (k == latestKey && f.Value != value)
                agreed = false;
        }
        // Decided with nothing recorded to say which way: canon gives no answer.
        if (!recorded)
            return (null, null);
        if (!found)
            return (false, null);
        // Two changes at one story time that disagree are a tie. It is reported, never settled by guessing.
        return agreed
            ? (value, null)
            : (null, LatestFacts(propId, key, before).Select(f => f.Id).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Was true once and is no longer: the difference between an outdated belief and a false one. "Was true"
    /// means settled as true. A change to true that ties with a change to false never settled anything, and
    /// calling a belief outdated on the strength of it would be a guess.
    /// </summary>
    public bool Expired(string propId, double key, bool before = false) =>
        Truth(propId, key, before).Value == false &&
        Model.Facts(propId).Any(f => f.Value && InEffect(f, key, before)
                                     && Truth(propId, f.StoryKey ?? double.NegativeInfinity, before: false).Value == true);

    private static bool InEffect(FactChange f, double key, bool before)
    {
        var k = f.StoryKey ?? double.NegativeInfinity;
        return before ? k < key || double.IsNegativeInfinity(k) : k <= key;
    }

    /// <summary>The fact changes in effect that share the latest story time.</summary>
    private List<FactChange> LatestFacts(string propId, double key, bool before)
    {
        var latest = new List<FactChange>();
        var latestKey = double.NegativeInfinity;
        foreach (var f in Model.Facts(propId))
        {
            if (!InEffect(f, key, before))
                continue;
            var k = f.StoryKey ?? double.NegativeInfinity;
            if (latest.Count == 0 || k > latestKey)
            {
                latest.Clear();
                latest.Add(f);
                latestKey = k;
            }
            else if (k == latestKey)
                latest.Add(f);
        }
        return latest;
    }

    // ------------------------------------------------------------- characters

    /// <summary>The latest belief change per proposition for one person, strictly before a story key.</summary>
    public IReadOnlyDictionary<string, BeliefView> BeliefsBefore(string holder, double key, bool inclusive = false)
    {
        var groups = new Dictionary<string, (double Key, List<BeliefChange> Records)>(StringComparer.Ordinal);
        foreach (var b in Model.BeliefsOf(holder))
        {
            if (!(b.StoryKey < key || (inclusive && b.StoryKey == key)))
                continue;
            if (!groups.TryGetValue(b.Prop, out var cur) || b.StoryKey > cur.Key)
                groups[b.Prop] = (b.StoryKey, [b]);
            else if (b.StoryKey == cur.Key)
                cur.Records.Add(b);
        }
        return groups.ToDictionary(g => g.Key, g => Settle(g.Value.Records), StringComparer.Ordinal);
    }

    /// <summary>
    /// What one person makes of one fact strictly before a story key, or null when nothing is recorded by then.
    /// It is the entry <see cref="BeliefsBefore"/> has for that fact, read from that person's records about that
    /// fact alone: the question to ask when one fact is wanted, however many the person holds views on.
    /// </summary>
    public BeliefView? BeliefBefore(string holder, string propId, double key, bool inclusive = false) =>
        Latest(Model.BeliefsOf(holder, propId), key, inclusive);

    /// <summary>What one person's records about one fact come to strictly before a story key, or null when none is that early.</summary>
    internal static BeliefView? Latest(IReadOnlyList<BeliefChange> records, double key, bool inclusive = false)
    {
        List<BeliefChange>? latest = null;
        var latestKey = double.NegativeInfinity;
        for (var i = 0; i < records.Count; i++)
        {
            var b = records[i];
            if (!(b.StoryKey < key || (inclusive && b.StoryKey == key)))
                continue;
            if (latest is null || b.StoryKey > latestKey)
                (latest, latestKey) = ([b], b.StoryKey);
            else if (b.StoryKey == latestKey)
                latest.Add(b);
        }
        return latest is null ? null : Settle(latest);
    }

    /// <summary>One answer from records that share the latest story key, or an explicit conflict.</summary>
    private static BeliefView Settle(List<BeliefChange> records)
    {
        var ordered = records.Count == 1 ? records : records.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var first = ordered[0];
        if (ordered.All(r => r.Stance == first.Stance && r.Strength == first.Strength))
            return new BeliefView(first.Id, first.Holder, first.Prop, first.Stance, first.Strength, first.StoryKey,
                first.Scene, first.Channel, first.Via);
        var ids = ordered.Select(r => r.Id).ToList();
        return new BeliefView("unresolved:" + string.Join("+", ids), first.Holder, first.Prop, null, null,
            first.StoryKey, first.Scene, null, null, ids);
    }

    /// <summary>
    /// Whether a held stance matches the truth. With a story key, the truth just before that key, which is the
    /// question on entry to a scene. Without one, the truth at the moment the belief was acquired.
    /// </summary>
    public bool? IsRight(BeliefView b, double? key = null)
    {
        if (b.Stance is not (Stance.HoldsTrue or Stance.HoldsFalse))
            return null;
        var truth = key is double k ? TruthBefore(b.Prop, k) : TruthAt(b.Prop, b.StoryKey);
        return truth is null ? null : (b.Stance == Stance.HoldsTrue) == truth;
    }

    /// <summary>
    /// The derived label: knowledge, a false belief, an outdated one and the rest. A belief is judged against
    /// what is true at the time asked about, so one that was right once and has been overtaken is not knowledge.
    /// </summary>
    public string Classify(BeliefView? b, double? key = null)
    {
        if (b is null)
            return "no record";
        if (b.Unresolved)
            return "ORDER NOT ESTABLISHED";
        if (b.Stance is Stance.Unaware or Stance.Undecided)
            return Names.Of(b.Stance.Value);
        var (at, before) = (key ?? b.StoryKey, key is not null);
        var strength = b.Strength is { } s ? Names.Of(s) : "none";
        var right = IsRight(b, key);
        if (right is null)
            return FactTie(b.Prop, at, before) is null ? $"{strength}; canon {Canon(b.Prop)}" : "ORDER NOT ESTABLISHED";
        if (right == false)
            return b.Stance == Stance.HoldsTrue && Expired(b.Prop, at, before) ? "OUTDATED BELIEF" : "FALSE BELIEF";
        if (b.Strength == Strength.Certain)
            return b.Stance == Stance.HoldsTrue ? "knows" : "knows it is false";
        return b.Stance == Stance.HoldsTrue ? $"correctly {strength}" : "correctly doubts";
    }

    /// <summary>The first record in which a person takes a proposition to be true.</summary>
    public BeliefChange? FirstHolds(string holder, string propId) =>
        Model.BeliefsOf(holder, propId) is { Count: > 0 } records
            ? records.Where(b => b.Stance == Stance.HoldsTrue).OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault()
            : null;

    // ----------------------------------------------------------------- reader

    private static int Rank(DisclosureMode mode) => mode switch
    {
        // Within one scene a plain statement settles what narration through a character left open.
        DisclosureMode.Stated => 0,
        DisclosureMode.AssertedByCharacter => 1,
        DisclosureMode.Focalized => 2,
        DisclosureMode.Implied => 3,
        DisclosureMode.Inferable => 4,
        _ => 5,
    };

    /// <summary>What the reader has been given before a rank in the telling. Disclosures in scenes not in the manuscript have no rank and are left out.</summary>
    public IReadOnlyDictionary<string, ReaderView> ReaderBefore(int tell)
    {
        var groups = new Dictionary<string, (int Tell, List<Disclosure> Records)>(StringComparer.Ordinal);
        foreach (var d in Model.Disclosures)
        {
            if (Tell(d.Scene) is not int dt || dt >= tell)
                continue;
            if (!groups.TryGetValue(d.Prop, out var cur) || dt > cur.Tell)
                groups[d.Prop] = (dt, [d]);
            else if (dt == cur.Tell)
                cur.Records.Add(d);
        }
        return groups.ToDictionary(g => g.Key, g => Settle(g.Value.Records), StringComparer.Ordinal);
    }

    private static ReaderView Settle(List<Disclosure> records)
    {
        var best = records.Min(d => Rank(d.Mode));
        var top = records.Where(d => Rank(d.Mode) == best).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        var first = top[0];
        if (top.All(d => d.ReaderStance == first.ReaderStance && d.Strength == first.Strength))
            return new ReaderView(first.Id, first.Scene, first.Prop, first.Mode, first.ReaderStance, first.Strength);
        var ids = top.Select(d => d.Id).ToList();
        return new ReaderView("unresolved:" + string.Join("+", ids), first.Scene, first.Prop, first.Mode, null, null, ids);
    }

    /// <summary>The first scene in the telling that states the truth of a proposition to the reader, if any does.</summary>
    public string? RevealScene(string propId) => _revealScene.GetOrAdd(propId, static (id, state) => state.FindRevealScene(id), this);

    private string? FindRevealScene(string propId)
    {
        var canon = Canon(propId);
        if (canon is not ("true" or "false"))
            return null;
        var want = canon == "true" ? Stance.HoldsTrue : Stance.HoldsFalse;
        return Model.DisclosuresOf(propId)
            .Where(d => d.Mode == DisclosureMode.Stated && d.ReaderStance == want && Tell(d.Scene) is not null)
            .OrderBy(d => Tell(d.Scene)).ThenBy(d => d.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Scene;
    }

    // ------------------------------------------------------------------ world

    /// <summary>Where an object is kept just before a story key. Null when nothing is recorded yet.</summary>
    public LocationView? Location(string objectId, double key)
    {
        var before = Model.PlacementsOf(objectId).Where(p => p.StoryKey < key).ToList();
        if (before.Count == 0)
            return null;
        var latest = before.Max(p => p.StoryKey);
        var recs = before.Where(p => p.StoryKey == latest).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var first = recs[0];
        if (recs.All(p => p.Place == first.Place))
            return new LocationView(first.Id, objectId, first.Place, first.StoryKey, first.Scene);
        var ids = recs.Select(p => p.Id).ToList();
        return new LocationView("unresolved:" + string.Join("+", ids), objectId, null, latest, null, ids);
    }

    public IReadOnlyList<string> TrackedObjects =>
        _trackedObjects ??= Model.Placements.Select(p => p.Object).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();

    // ----------------------------------------------------------------- events

    /// <summary>
    /// Whether one event comes "before" or "after" another in story time, or "not_established" when they share
    /// a story time. Which of two such events came first is something the records do not say, and it is not
    /// settled by the order they were entered in or by their ids.
    /// </summary>
    public string Order(string eventId, string otherEventId)
    {
        Event Find(string id) => Model.Events.GetValueOrDefault(id) ?? throw new KeyNotFoundException($"No event {id}.");
        var (a, b) = (Find(eventId).StoryKey, Find(otherEventId).StoryKey);
        return a < b ? "before" : a > b ? "after" : "not_established";
    }

    // -------------------------------------------------------------- assertions

    /// <summary>
    /// Lie, sincere statement and unreliable narration fall out of the claim, the speaker's belief and what is
    /// the case at the moment it is said. A fact that has since changed makes an honest statement a mistake.
    /// </summary>
    public string ClassifyAssertion(string assertionId)
    {
        if (!Model.Assertions.TryGetValue(assertionId, out var a))
            throw new KeyNotFoundException($"No assertion {assertionId}.");
        var canon = Canon(a.Prop);
        var (now, tie) = Truth(a.Prop, a.StoryKey, before: false);
        var truth = canon is not ("true" or "false") ? $"canon {canon}"
            : tie is not null ? "order not established"
            : now == a.Polarity ? "true" : "false";
        if (a.Type == AssertionKind.Narration)
            return $"narration through {Short(a.Focalizer ?? a.Speaker)}: {truth}";
        var held = BeliefsBefore(Model.Base(a.Speaker), a.StoryKey, inclusive: true).GetValueOrDefault(a.Prop);
        if (held is null || held.Unresolved || held.Stance is Stance.Unaware or Stance.Undecided)
            return $"sincerity not established: {truth}";
        return (held.Stance == Stance.HoldsTrue) != a.Polarity ? $"lie: {truth}" : $"sincere: {truth}";
    }
}

/// <summary>The names enum values have in ledger files and in output.</summary>
public static class Names
{
    public static string Of<T>(T value) where T : struct, Enum =>
        Known<T>.Names.TryGetValue(value, out var name) ? name : Written(value);

    private static string Written<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>The name of every value an enum declares, worked out once: a name is printed for nearly every line of every answer.</summary>
    private static class Known<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Names = Enum.GetValues<T>().Distinct().ToDictionary(v => v, Written);
    }
}
