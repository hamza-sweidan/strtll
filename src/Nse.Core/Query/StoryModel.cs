using Nse.Ledger;
using Nse.Model;

namespace Nse.Query;

/// <summary>The records that count as of one ledger head, sorted into kinds. Built fresh for every question.</summary>
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

    private readonly Dictionary<string, Entity> _entity;
    private readonly Dictionary<string, Proposition> _prop;
    private readonly ILookup<string, FactChange> _facts;

    public StoryModel(IEnumerable<Tracked> live, int head)
    {
        Head = head;
        Records = live.OrderBy(t => t.Order).ToList();
        ById = Records.ToDictionary(t => t.Record.Id, StringComparer.Ordinal);
        var all = Records.Select(t => t.Record).ToList();
        Entities = all.OfType<Entity>().ToList();
        _entity = Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        Scenes = all.OfType<SceneRecord>().ToDictionary(s => s.Id, StringComparer.Ordinal);
        // If a scene's time was restated, the later record stands.
        SceneTimes = all.OfType<SceneTime>().GroupBy(t => t.Scene).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        Propositions = all.OfType<Proposition>().ToList();
        _prop = Propositions.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _facts = all.OfType<FactChange>().ToLookup(f => f.Prop, StringComparer.Ordinal);
        Events = all.OfType<Event>().ToDictionary(e => e.Id, StringComparer.Ordinal);
        Assertions = all.OfType<Assertion>().ToDictionary(a => a.Id, StringComparer.Ordinal);
        Beliefs = all.OfType<BeliefChange>().ToList();
        Disclosures = all.OfType<Disclosure>().ToList();
        Placements = all.OfType<Placement>().ToList();
        Rules = all.OfType<Rule>().ToList();
        Embargoes = all.OfType<Embargo>().ToList();
        Secrets = all.OfType<Secret>().ToList();
        // The validator allows one contract in force per scene. If a ledger somehow holds two, the later stands.
        Contracts = all.OfType<SceneContract>().GroupBy(c => c.Scene).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    public Entity? Entity(string id) => _entity.GetValueOrDefault(id);

    public Proposition? Proposition(string id) => _prop.GetValueOrDefault(id);

    public IEnumerable<FactChange> Facts(string propId) => _facts[propId];

    /// <summary>A persona resolves to the person behind it.</summary>
    public string Base(string entityId) => _entity.GetValueOrDefault(entityId)?.PersonaOf ?? entityId;
}
