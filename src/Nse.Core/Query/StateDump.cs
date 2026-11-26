using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Model;

namespace Nse.Query;

/// <summary>
/// Every point-in-time answer the engine can give, in one deterministic document: on entry to each scene and
/// at the end, for every tracked person, the reader and every tracked object. Two states are the same exactly
/// when their dumps are byte-identical, which is how a rebuilt index and an older ledger head are checked.
/// </summary>
public static class StateDump
{
    public static JsonObject Build(StoryState s)
    {
        var props = s.Model.Propositions.Select(p => p.Id).ToList();
        var holders = s.Holders;
        var root = new JsonObject
        {
            ["head"] = s.Model.Head,
            ["holders"] = new JsonArray(holders.Select(h => (JsonNode)h).ToArray()),
            ["telling"] = new JsonArray(s.Telling.Select(t => (JsonNode)t.Id).ToArray()),
        };
        var canon = new JsonObject();
        var reveal = new JsonObject();
        foreach (var p in props)
        {
            canon[p] = s.Canon(p);
            reveal[p] = s.RevealScene(p);
        }
        root["canon"] = canon;
        root["reveal_scene"] = reveal;

        var first = new JsonObject();
        foreach (var h in holders)
        {
            var mine = new JsonObject();
            foreach (var p in props)
                if (s.FirstHolds(h, p) is { } b)
                    mine[p] = b.Scene ?? "background";
            first[h] = mine;
        }
        root["first_holds"] = first;

        var assertions = new JsonObject();
        foreach (var a in s.Model.Assertions.Keys.OrderBy(k => k, StringComparer.Ordinal))
            assertions[a] = s.ClassifyAssertion(a);
        root["assertions"] = assertions;

        var points = new JsonObject();
        foreach (var scene in s.Telling)
            if (s.Model.SceneTimes.ContainsKey(scene.Id))
                points[scene.Id] = Point(s, s.Key(scene.Id), scene.Tell);
        points["end"] = Point(s, StoryState.End, StoryState.EndOfTelling);
        root["points"] = points;
        return root;
    }

    /// <summary>The state strictly before a story key (people, objects, facts) and before a rank in the telling (reader).</summary>
    public static JsonObject Point(StoryState s, double key, int tell)
    {
        var beliefs = new JsonObject();
        foreach (var h in s.Holders)
        {
            var mine = new JsonObject();
            foreach (var (prop, b) in s.BeliefsBefore(h, key).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                mine[prop] = View(s, b, key);
            beliefs[h] = mine;
        }
        var reader = new JsonObject();
        foreach (var (prop, d) in s.ReaderBefore(tell).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            reader[prop] = View(d);
        var world = new JsonObject();
        foreach (var obj in s.TrackedObjects)
            world[obj] = s.Location(obj, key) is { } l ? new JsonObject { ["id"] = l.Id, ["place"] = l.Place } : null;
        var truth = new JsonObject();
        foreach (var p in s.Model.Propositions)
            truth[p.Id] = s.FactTie(p.Id, key, before: true) is not null ? "unresolved"
                : s.TruthBefore(p.Id, key) is { } t ? JsonValue.Create(t) : null;
        return new JsonObject { ["beliefs"] = beliefs, ["reader"] = reader, ["world"] = world, ["truth"] = truth };
    }

    public static JsonObject View(StoryState s, BeliefView b, double? key)
    {
        var o = new JsonObject { ["id"] = b.Id, ["stance"] = b.Stance is { } st ? Names.Of(st) : "unresolved" };
        if (b.Strength is { } strength)
            o["strength"] = Names.Of(strength);
        o["label"] = s.Classify(b, key);
        return o;
    }

    public static JsonObject View(ReaderView d)
    {
        var o = new JsonObject
        {
            ["id"] = d.Id, ["mode"] = Names.Of(d.Mode), ["stance"] = d.Stance is { } st ? Names.Of(st) : "unresolved",
        };
        if (d.Strength is { } strength)
            o["strength"] = Names.Of(strength);
        return o;
    }

    public static string Write(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions(NseJson.Options)) + "\n";
}
