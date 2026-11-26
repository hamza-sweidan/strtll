using System.Text.Json;
using Nse.Ledger;
using Nse.Model;

namespace Nse.Tests.Support;

/// <summary>
/// Turns the example story's hand-written state (story.json) into change sets for the engine. The records then go in
/// through <see cref="Story.Put"/> like any others, so the gold has to satisfy every check the write path makes.
/// </summary>
public static class GoldImporter
{
    private const string Actor = "import:harbour-letter";

    /// <summary>
    /// The change sets in the order they are applied: the registry, the propositions with their truth, what
    /// happens and is said, what was the case before the story opens, and then one per scene in telling order.
    /// </summary>
    public static IReadOnlyList<ChangeSetDraft> Drafts(JsonElement gold)
    {
        var drafts = new List<ChangeSetDraft>();
        var scenes = gold.GetProperty("scenes").EnumerateArray().OrderBy(s => s.GetProperty("tell").GetInt32()).ToList();

        var registry = new List<LedgerRecord>();
        foreach (var e in gold.GetProperty("entities").EnumerateArray())
            registry.Add(new Entity
            {
                Id = Str(e, "id"), Type = Parse<EntityKind>(Str(e, "kind")), Name = Str(e, "name"),
                Aliases = List(e, "aliases"), PersonaOf = Opt(e, "persona_of"), Note = Opt(e, "note"), Origin = "import",
            });
        foreach (var s in scenes)
        {
            var id = Str(s, "id");
            registry.Add(new SceneRecord
            {
                Id = id, Title = Opt(s, "title"), Pov = Str(s, "pov"), Cast = List(s, "cast"), Place = Opt(s, "place"),
                Origin = "import",
            });
            registry.Add(new SceneTime
            {
                Id = "st_" + id, Scene = id, StoryKey = s.GetProperty("story_key").GetDouble(), Label = Opt(s, "story_label"),
                // The flashback is the one scene whose place in story time was asserted against the telling.
                Basis = id == "sc_08" ? TimeBasis.Asserted : TimeBasis.FollowsTelling, Origin = "import",
            });
        }
        drafts.Add(Draft("People, places, objects and scenes", registry));

        var canon = new List<LedgerRecord>();
        foreach (var p in gold.GetProperty("propositions").EnumerateArray())
        {
            var id = Str(p, "id");
            var value = Str(p, "canon");
            canon.Add(new Proposition
            {
                Id = id, Text = Str(p, "text"), About = List(p, "about"), Note = Opt(p, "note"), Origin = "import",
                Resolution = value switch { "open" => Resolution.Open, "ambiguous" => Resolution.Ambiguous, _ => Resolution.Decided },
                Nested = p.TryGetProperty("nested", out var n)
                    ? new NestedBelief(Str(n, "holder"), Str(n, "prop"), Opt(n, "stance") is { } st ? Parse<Stance>(st) : null)
                    : null,
            });
            if (value == "true")
            {
                canon.Add(new FactChange { Id = "f_" + id, Prop = id, Value = true, StoryKey = Num(p, "true_from"), Origin = "import" });
                if (Num(p, "true_until") is { } until)
                    canon.Add(new FactChange { Id = "f_" + id + "_end", Prop = id, Value = false, StoryKey = until, Origin = "import" });
            }
            else if (value == "false")
            {
                canon.Add(new FactChange { Id = "f_" + id, Prop = id, Value = false, StoryKey = Num(p, "exists_from"), Origin = "import" });
            }
        }
        foreach (var r in gold.GetProperty("rules").EnumerateArray())
            canon.Add(new Rule { Id = Str(r, "id"), Type = Str(r, "kind"), Prop = Opt(r, "prop"), Text = Str(r, "text"), Origin = "import" });
        foreach (var e in gold.GetProperty("embargoes").EnumerateArray())
            canon.Add(new Embargo
            {
                Id = Str(e, "id"), Prop = Str(e, "prop"), UntilScene = Str(e, "until_scene"), Tripwires = List(e, "tripwires"),
                Note = Opt(e, "note"), Origin = "import",
            });
        foreach (var s in gold.GetProperty("secrets").EnumerateArray())
            canon.Add(new Secret
            {
                Id = Str(s, "id"), Props = List(s, "props"), KnownOnlyTo = List(s, "known_only_to"), Note = Opt(s, "note"),
                Origin = "import",
            });
        drafts.Add(Draft("Propositions, their truth, rules, embargoes and secrets", canon));

        var happenings = new List<LedgerRecord>();
        foreach (var e in gold.GetProperty("events").EnumerateArray())
            happenings.Add(new Event
            {
                Id = Str(e, "id"), Scene = Opt(e, "scene"), StoryKey = e.GetProperty("story_key").GetDouble(),
                Summary = Str(e, "summary"), Quote = Opt(e, "quote"), Origin = "import",
                Participants = e.GetProperty("participants").EnumerateObject()
                    .ToDictionary(x => x.Name, x => Parse<ParticipantRole>(x.Value.GetString()!)),
            });
        foreach (var a in gold.GetProperty("assertions").EnumerateArray())
            happenings.Add(new Assertion
            {
                Id = Str(a, "id"), Scene = Str(a, "scene"), StoryKey = a.GetProperty("story_key").GetDouble(),
                Type = Parse<AssertionKind>(Str(a, "kind")), Speaker = Str(a, "speaker"), Focalizer = Opt(a, "focalizer"),
                Addressees = List(a, "addressees"), Overhearers = List(a, "overhearers"), ReadBy = Opt(a, "read_by"),
                Prop = Str(a, "prop"), Polarity = !a.TryGetProperty("polarity", out var pol) || pol.GetBoolean(),
                Sincerity = Opt(a, "sincerity") switch
                {
                    "sincere" => Sincerity.Sincere, "insincere" => Sincerity.Insincere, "n/a" => Sincerity.NotApplicable, _ => null,
                },
                Quote = Str(a, "quote"), Origin = "import",
            });
        drafts.Add(Draft("What happens and what is said", happenings));

        drafts.Add(Draft("Before the story opens", SceneRecords(gold, null)));
        foreach (var s in scenes)
        {
            var records = SceneRecords(gold, Str(s, "id"));
            if (records.Count > 0)
                drafts.Add(Draft($"Scene {s.GetProperty("tell").GetInt32()}, {Str(s, "title")}", records));
        }
        return drafts;
    }

    /// <summary>Belief changes, disclosures and movements that belong to one scene, or to no scene.</summary>
    private static List<LedgerRecord> SceneRecords(JsonElement gold, string? scene)
    {
        var records = new List<LedgerRecord>();
        foreach (var b in gold.GetProperty("belief_changes").EnumerateArray().Where(b => Opt(b, "scene") == scene))
            records.Add(new BeliefChange
            {
                Id = Str(b, "id"), Holder = Str(b, "holder"), Prop = Str(b, "prop"), Stance = Parse<Stance>(Str(b, "stance")),
                Strength = Opt(b, "strength") is { } s ? Parse<Strength>(s) : null,
                StoryKey = b.GetProperty("story_key").GetDouble(), Scene = scene, Channel = Parse<Channel>(Str(b, "channel")),
                Via = Opt(b, "via"), Quote = Opt(b, "quote"), QuoteScene = Opt(b, "quote_scene"), Note = Opt(b, "note"),
                // The gold marks every in-scene change. An unmarked background record is the author's summary.
                Directness = Opt(b, "directness") is { } d ? Parse<Directness>(d) : Directness.Entailed, Origin = "import",
            });
        if (scene is not null)
            foreach (var d in gold.GetProperty("disclosures").EnumerateArray().Where(d => Str(d, "scene") == scene))
                records.Add(new Disclosure
                {
                    Id = Str(d, "id"), Scene = scene, Prop = Str(d, "prop"), Mode = Parse<DisclosureMode>(Str(d, "mode")),
                    ReaderStance = Parse<Stance>(Str(d, "reader_stance")),
                    Strength = Opt(d, "strength") is { } s ? Parse<Strength>(s) : null, Quote = Opt(d, "quote"),
                    Note = Opt(d, "note"), Origin = "import",
                });
        foreach (var l in gold.GetProperty("locations").EnumerateArray().Where(l => Opt(l, "scene") == scene))
            records.Add(new Placement
            {
                Id = Str(l, "id"), Object = Str(l, "object"), Place = Str(l, "place"),
                StoryKey = l.GetProperty("story_key").GetDouble(), Scene = scene, Cause = Str(l, "cause"),
                Quote = Opt(l, "quote"), QuoteScene = Opt(l, "quote_scene"), Origin = "import",
            });
        return records;
    }

    private static ChangeSetDraft Draft(string note, IEnumerable<LedgerRecord> records) => new()
    {
        Actor = Actor, Note = note,
        Ops = records.Select(r => (Operation)new PutOp { Record = r, Approval = Approval.Ratified }).ToList(),
    };

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IReadOnlyList<string> List(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

    private static T Parse<T>(string name) where T : struct, System.Enum =>
        System.Enum.GetValues<T>().First(v => Query.Names.Of(v) == name);
}
