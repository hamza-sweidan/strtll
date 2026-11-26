using Nse.Ledger;
using Nse.Model;

namespace Nse.Query;

/// <summary>
/// The records that count as of one ledger head, sorted into kinds and into the groups questions are asked by:
/// one person's belief changes, the disclosures of one fact, the moves of one object. Built fresh for every
/// question, so none of it is stored anywhere and none of it can fall behind the ledger.
/// </summary>
public sealed class StoryModel
{
    public int Head { get; }
    public IReadOnlyList<Tracked> Records { get; }
    public IReadOnlyDictionary<string, Tracked> ById { get; }

    public IReadOnlyList<Entity> Entities { get; }
    public IReadOnlyDictionary<string, SceneRecord> Scenes { get; }
    public IReadOnlyDictionary<string, SceneTime> SceneTimes { get; }
    public IReadOnlyList<Proposition> Propositions { get; }
    public IReadOnlyDictionary<string, Event> Events { get; }
    public IReadOnlyDictionary<string, Assertion> Assertions { get; }
    public IReadOnlyList<BeliefChange> Beliefs { get; }
    public IReadOnlyList<Disclosure> Disclosures { get; }
    public IReadOnlyList<Placement> Placements { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public IReadOnlyList<Embargo> Embargoes { get; }
    public IReadOnlyList<Secret> Secrets { get; }

    /// <summary>The contract in force for each scene that has one.</summary>
    public IReadOnlyDictionary<string, SceneContract> Contracts { get; }

    /// <summary>What answering from this model has taken so far. See <see cref="Query.Effort"/>.</summary>
    internal Effort Effort { get; } = new();

    private readonly Dictionary<string, Entity> _entity;
    private readonly Dictionary<string, Proposition> _prop;

    // Each group holds its records in the order they entered the ledger, as the list of the whole kind does. A
    // question about one person, fact or object reads its group. Reading the whole kind for it costs as many
    // steps as the story has records, and a context asks such a question for every fact and everyone present.
    private readonly Dictionary<string, Counted<FactChange>> _facts;
    private readonly Dictionary<string, Counted<BeliefChange>> _beliefsOfHolder;
    private readonly Dictionary<(string Holder, string Prop), Counted<BeliefChange>> _beliefsOfHolderOnProp;
    private readonly Dictionary<string, Counted<Disclosure>> _disclosures;
    private readonly Dictionary<string, Counted<Placement>> _placements;
    private readonly Dictionary<string, Counted<Secret>> _secrets;
    private readonly Dictionary<string, Counted<Event>> _eventsIn;
    private readonly Dictionary<string, Counted<Assertion>> _assertionsIn;
    private readonly Dictionary<string, Counted<BeliefChange>> _beliefsIn;
    private readonly Dictionary<string, Counted<Disclosure>> _disclosuresIn;
    private readonly Dictionary<string, Counted<Placement>> _placementsIn;

    public StoryModel(IEnumerable<Tracked> live, int head)
    {
        Head = head;
        var records = live.OrderBy(t => t.Order).ToList();
        Records = Counting(records);
        ById = Counting(records.ToDictionary(t => t.Record.Id, StringComparer.Ordinal));
        var all = records.Select(t => t.Record).ToList();
        var entities = all.OfType<Entity>().ToList();
        Entities = Counting(entities);
        _entity = entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        Scenes = Counting(all.OfType<SceneRecord>().ToDictionary(s => s.Id, StringComparer.Ordinal));
        // If a scene's time was restated, the later record stands.
        SceneTimes = Counting(all.OfType<SceneTime>().GroupBy(t => t.Scene).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal));
        var propositions = all.OfType<Proposition>().ToList();
        Propositions = Counting(propositions);
        _prop = propositions.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _facts = Grouped(all.OfType<FactChange>(), f => f.Prop, StringComparer.Ordinal);
        Events = Counting(all.OfType<Event>().ToDictionary(e => e.Id, StringComparer.Ordinal));
        _eventsIn = Grouped(all.OfType<Event>().Where(e => e.Scene is not null), e => e.Scene!, StringComparer.Ordinal);
        Assertions = Counting(all.OfType<Assertion>().ToDictionary(a => a.Id, StringComparer.Ordinal));
        _assertionsIn = Grouped(all.OfType<Assertion>(), a => a.Scene, StringComparer.Ordinal);
        var beliefs = all.OfType<BeliefChange>().ToList();
        Beliefs = Counting(beliefs);
        _beliefsOfHolder = Grouped(beliefs, b => b.Holder, StringComparer.Ordinal);
        _beliefsOfHolderOnProp = Grouped(beliefs, b => (b.Holder, b.Prop), null);
        _beliefsIn = Grouped(beliefs.Where(b => b.Scene is not null), b => b.Scene!, StringComparer.Ordinal);
        var disclosures = all.OfType<Disclosure>().ToList();
        Disclosures = Counting(disclosures);
        _disclosures = Grouped(disclosures, d => d.Prop, StringComparer.Ordinal);
        _disclosuresIn = Grouped(disclosures, d => d.Scene, StringComparer.Ordinal);
        var placements = all.OfType<Placement>().ToList();
        Placements = Counting(placements);
        _placements = Grouped(placements, p => p.Object, StringComparer.Ordinal);
        _placementsIn = Grouped(placements.Where(p => p.Scene is not null), p => p.Scene!, StringComparer.Ordinal);
        Rules = Counting(all.OfType<Rule>().ToList());
        Embargoes = Counting(all.OfType<Embargo>().ToList());
        var secrets = all.OfType<Secret>().ToList();
        Secrets = Counting(secrets);
        // A secret that names a fact twice is still one secret about it.
        _secrets = secrets.SelectMany(x => x.Props.Distinct(StringComparer.Ordinal).Select(p => (Prop: p, Secret: x)))
            .GroupBy(x => x.Prop, x => x.Secret, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Counting(g.ToList()), StringComparer.Ordinal);
        // The validator allows one contract in force per scene. If a ledger somehow holds two, the later stands.
        Contracts = Counting(all.OfType<SceneContract>().GroupBy(c => c.Scene).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal));
    }

    private Counted<T> Counting<T>(List<T> items) => new(items, Effort);

    private CountedMap<T> Counting<T>(Dictionary<string, T> items) => new(items, Effort);

    private Dictionary<TKey, Counted<T>> Grouped<TKey, T>(IEnumerable<T> items, Func<T, TKey> key, IEqualityComparer<TKey>? comparer)
        where TKey : notnull =>
        items.GroupBy(key, comparer).ToDictionary(g => g.Key, g => Counting(g.ToList()), comparer);

    private IReadOnlyList<T> Group<TKey, T>(Dictionary<TKey, Counted<T>> groups, TKey key) where TKey : notnull
    {
        Effort.Add();
        return groups.TryGetValue(key, out var found) ? found : [];
    }

    public Entity? Entity(string id)
    {
        Effort.Add();
        return _entity.GetValueOrDefault(id);
    }

    public Proposition? Proposition(string id)
    {
        Effort.Add();
        return _prop.GetValueOrDefault(id);
    }

    public IEnumerable<FactChange> Facts(string propId) => Group(_facts, propId);

    /// <summary>One person's belief changes, about every fact.</summary>
    public IReadOnlyList<BeliefChange> BeliefsOf(string holder) => Group(_beliefsOfHolder, holder);

    /// <summary>One person's belief changes about one fact.</summary>
    public IReadOnlyList<BeliefChange> BeliefsOf(string holder, string propId) => Group(_beliefsOfHolderOnProp, (holder, propId));

    /// <summary>What the reader is given about one fact, in any scene.</summary>
    public IReadOnlyList<Disclosure> DisclosuresOf(string propId) => Group(_disclosures, propId);

    /// <summary>The moves of one object.</summary>
    public IReadOnlyList<Placement> PlacementsOf(string objectId) => Group(_placements, objectId);

    /// <summary>The secrets that cover one fact.</summary>
    public IReadOnlyList<Secret> SecretsOf(string propId) => Group(_secrets, propId);

    /// <summary>The events recorded as happening in one scene.</summary>
    public IReadOnlyList<Event> EventsIn(string sceneId) => Group(_eventsIn, sceneId);

    /// <summary>What is recorded as said, written or thought in one scene.</summary>
    public IReadOnlyList<Assertion> AssertionsIn(string sceneId) => Group(_assertionsIn, sceneId);

    /// <summary>The belief changes recorded as happening in one scene.</summary>
    public IReadOnlyList<BeliefChange> BeliefsIn(string sceneId) => Group(_beliefsIn, sceneId);

    /// <summary>What the reader is given in one scene.</summary>
    public IReadOnlyList<Disclosure> DisclosuresIn(string sceneId) => Group(_disclosuresIn, sceneId);

    /// <summary>The moves recorded as happening in one scene.</summary>
    public IReadOnlyList<Placement> PlacementsIn(string sceneId) => Group(_placementsIn, sceneId);

    /// <summary>A persona resolves to the person behind it.</summary>
    public string Base(string entityId)
    {
        Effort.Add();
        return _entity.GetValueOrDefault(entityId)?.PersonaOf ?? entityId;
    }
}
