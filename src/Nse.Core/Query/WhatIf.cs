using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Text;
using Nse.Validation;

namespace Nse.Query;

/// <summary>One scene whose drafting context would read differently, and how many of its lines would.</summary>
public sealed record ContextChange(string Scene, int Tell, int Lines);

/// <summary>
/// What a change set would do if it were applied, worked out in memory: whether it would pass the checks, every
/// point-in-time answer that would change, and every scene whose drafting context would. Nothing is written.
/// </summary>
public sealed record WhatIfResult(
    string Actor, int Operations, int Head, IReadOnlyList<Finding> Findings, IReadOnlyList<string> Changes,
    IReadOnlyList<ContextChange> Contexts)
{
    public bool WouldApply => Findings.Count == 0;
}

/// <summary>
/// Terraform's plan, for retcons. Two states are compared through the same
/// document that pins every answer the engine gives (<see cref="StateDump"/>), so nothing a query could show is
/// left out of the comparison, and the differences are put back into words grouped by the point they are about.
/// </summary>
public static class WhatIf
{
    public static IReadOnlyList<string> Differences(StoryState before, StoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        List<(string Key, string? Was, string? Now)> changed;
        try
        {
            changed = Changed(before, after);
        }
        catch (KeyNotFoundException)
        {
            // A state that names a record that is not there cannot be dumped, and the caller reports which record.
            // That must not turn on the order the two documents happened to be compared in: it is the first one
            // met working out the document before, all of it, and then the document after.
            foreach (var state in new[] { before, after })
            {
                StateDump.AcrossTheStory(state);
                foreach (var (_, key, tell) in StateDump.Points(state))
                    StateDump.Point(state, key, tell);
            }
            throw;
        }
        // The points in the order they are told in the state after, then the end; within a point, people, the
        // reader, the world and what is so, each in id order.
        var telling = after.Telling.Select(t => t.Id).Concat(before.Telling.Select(t => t.Id)).Distinct()
            .Select((id, rank) => (id, rank)).ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);
        int Rank(string key)
        {
            var parts = key.Split('/');
            if (parts[0] != "points")
                return -1;
            return parts[1] == "end" ? int.MaxValue : telling.GetValueOrDefault(parts[1], -1);
        }
        var lines = new List<string>();
        string? heading = null;
        foreach (var (key, was, now) in changed.OrderBy(c => Rank(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('/');
            var head = parts[0] switch
            {
                "points" when parts[1] == "end" => "At the end of the story:",
                "points" => $"On entry to {Scene(after, before, parts[1])}:",
                _ => "Across the story:",
            };
            if (head != heading)
            {
                lines.Add(head);
                heading = head;
            }
            lines.Add("  " + Plain.OneLine(Describe(before, after, parts, was, now)));
        }
        return lines;
    }

    /// <summary>Each scene that can have a context, compiled before and after, with how many of its lines differ.</summary>
    public static IReadOnlyList<ContextChange> Contexts(StoryState before, StoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var result = new List<ContextChange>();
        foreach (var t in after.Telling)
        {
            // The parts are all that two contexts are compared by, so no text is put together for either.
            IReadOnlyList<ContextSection>? Compile(StoryState s)
            {
                if (!s.Model.Scenes.ContainsKey(t.Id) || !s.Model.SceneTimes.ContainsKey(t.Id))
                    return null;
                try
                {
                    return ContextCompiler.Sections(s, t.Id);
                }
                catch (KeyNotFoundException)
                {
                    return null;
                }
            }
            var (a, b) = (Compile(before), Compile(after));
            if (a is null && b is null)
                continue;
            var lines = a is null || b is null
                ? (a ?? b)!.Sum(x => x.Lines.Count + 1)
                : Freshness.Differences(a, b).Count;
            if (lines > 0)
                result.Add(new ContextChange(t.Id, t.Tell, lines));
        }
        return result;
    }

    private static string Scene(StoryState after, StoryState before, string id) =>
        (after.Tell(id) ?? before.Tell(id)) is int n ? $"scene {n}, {id}" : id;

    private static string Name(StoryState after, StoryState before, string id) =>
        after.Model.Entity(id)?.Name ?? before.Model.Entity(id)?.Name ?? id;

    private static string Describe(StoryState before, StoryState after, string[] p, string? was, string? now)
    {
        string Change(Func<string?, string> words) => $"{words(was)} -> {words(now)}";
        switch (p[0])
        {
            case "canon":
                return $"whether {p[1]} is so: " + Change(v => v is null ? "not a proposition" : Unquote(v));
            case "reveal_scene":
                return $"{p[1]} is first stated to the reader in: " + Change(v => v is null or "null" ? "no scene" : Unquote(v));
            case "first_holds":
                return $"{Name(after, before, p[1])} ({p[1]}) first takes {p[2]} as true in: " + Change(v => v is null ? "no scene" : Unquote(v));
            case "assertions":
                return $"statement {p[1]}: " + Change(v => v is null ? "not recorded" : Unquote(v));
            case "holders":
                return "the people with belief records: " + Change(v => v ?? "none");
            case "telling":
                return "the order the scenes are told in: " + Change(v => v ?? "none");
            case "points" when p.Length >= 4:
                return p[2] switch
                {
                    "beliefs" when p.Length >= 5 => $"{Name(after, before, p[3])} ({p[3]}) on {p[4]}: " + Change(Belief),
                    "reader" => $"the reader on {p[3]}: " + Change(Reader),
                    "world" => $"{Name(after, before, p[3])} ({p[3]}) is kept at: " + Change(v => Place(before, after, v)),
                    "truth" => $"whether {p[3]} is so: " + Change(Truth),
                    _ => string.Join("/", p) + ": " + Change(v => v ?? "nothing"),
                };
            default:
                return string.Join("/", p) + ": " + Change(v => v ?? "nothing");
        }
    }

    private static string Unquote(string json) => JsonNode.Parse(json) is JsonValue v && v.TryGetValue<string>(out var s) ? s : json;

    private static string Belief(string? json)
    {
        if (json is null)
            return "no record";
        var o = JsonNode.Parse(json)!.AsObject();
        return $"[{(string?)o["label"]}] ({(string?)o["id"]})";
    }

    private static string Reader(string? json)
    {
        if (json is null)
            return "not given it";
        var o = JsonNode.Parse(json)!.AsObject();
        var stance = (string?)o["stance"] switch { "holds_true" => "as true", "holds_false" => "as false", var x => x };
        return $"{(string?)o["mode"]}, takes it {stance}{(o["strength"] is { } st ? ", " + (string?)st : "")} ({(string?)o["id"]})";
    }

    private static string Place(StoryState before, StoryState after, string? json)
    {
        if (json is null or "null")
            return "nowhere recorded";
        var o = JsonNode.Parse(json)!.AsObject();
        var place = (string?)o["place"];
        return place is null ? $"unresolved ({(string?)o["id"]})" : $"{Name(after, before, place)} ({(string?)o["id"]})";
    }

    private static string Truth(string? json) => json switch
    {
        null or "null" => "not decided",
        "true" => "so",
        "false" => "not so",
        _ => Unquote(json),
    };

    /// <summary>
    /// The leaves that differ between the dumps of two states, by path, each with what it was and what it is as
    /// JSON, or null on the side that has no such leaf. A belief, a reader's view and a location are leaves whole:
    /// what changes is the answer, and an answer is all of its fields together. The ledger head is left out: it
    /// differs by design.
    /// </summary>
    /// <remarks>
    /// The two documents are compared a point at a time, and neither is ever held whole. A dump says everything
    /// about everyone at every point, so for a long story it is tens of megabytes, nearly all of it the same in
    /// both states; a point is built for each state, compared, and let go.
    /// </remarks>
    private static List<(string Key, string? Was, string? Now)> Changed(StoryState before, StoryState after)
    {
        var comparison = new Comparison();
        var (was, now) = (StateDump.Points(before).ToList(), StateDump.Points(after).ToList());
        // A point is known by its name alone only if no two share one, and the document's rule for what is a leaf
        // turns on the name "id". A story with a scene called "end" or "id" is compared as two whole documents.
        static bool ByName(List<(string Id, double Key, int Tell)> points) =>
            points.All(p => p.Id != "id") && points.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == points.Count;
        if (!ByName(was) || !ByName(now))
        {
            comparison.Both(StateDump.Build(before), StateDump.Build(after), 0);
            return comparison.Changed;
        }
        comparison.Both(StateDump.AcrossTheStory(before), StateDump.AcrossTheStory(after), 0);
        var inNow = now.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var inWas = was.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        comparison.Under("points", () =>
        {
            foreach (var p in was)
                comparison.Under(p.Id, () =>
                {
                    var point = StateDump.Point(before, p.Key, p.Tell);
                    if (inNow.TryGetValue(p.Id, out var q))
                        comparison.Both(point, StateDump.Point(after, q.Key, q.Tell), 2);
                    else
                        comparison.Alone(point, 2, isWas: true);
                });
            foreach (var q in now.Where(q => !inWas.Contains(q.Id)))
                comparison.Under(q.Id, () => comparison.Alone(StateDump.Point(after, q.Key, q.Tell), 2, isWas: false));
        });
        return comparison.Changed;
    }

    /// <summary>Two documents, or two parts of them, walked side by side, and the leaves found to differ.</summary>
    private sealed class Comparison
    {
        private readonly List<string> _path = [];

        public List<(string Key, string? Was, string? Now)> Changed { get; } = [];

        /// <summary>Compare what lies under one more name in both documents.</summary>
        public void Under(string name, Action compare)
        {
            _path.Add(name);
            compare();
            _path.RemoveAt(_path.Count - 1);
        }

        private bool Leaf(JsonNode? node, int depth) =>
            node is not JsonObject o || (depth >= 5 && _path[0] == "points") || o.ContainsKey("id");

        private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

        private void Differs(string? was, string? now)
        {
            if (_path is not ["head"])
                Changed.Add((string.Join("/", _path), was, now));
        }

        /// <summary>One side of a place in the document where the other has nothing, or has something of another shape.</summary>
        public void Alone(JsonNode? node, int depth, bool isWas)
        {
            if (Leaf(node, depth))
            {
                Differs(isWas ? Json(node) : null, isWas ? null : Json(node));
                return;
            }
            foreach (var (name, value) in (JsonObject)node!)
                Under(name, () => Alone(value, depth + 1, isWas));
        }

        /// <param name="depth">How many names deep the two nodes sit in the document.</param>
        public void Both(JsonNode? a, JsonNode? b, int depth)
        {
            var (leafA, leafB) = (Leaf(a, depth), Leaf(b, depth));
            if (leafA && leafB)
            {
                if (!Same(a, b))
                    Differs(Json(a), Json(b));
                return;
            }
            if (leafA || leafB)
            {
                Alone(a, depth, isWas: true);
                Alone(b, depth, isWas: false);
                return;
            }
            // Two states mostly hold the same names in the same order, and are then read off side by side. From the
            // first name that does not line up, each name is looked for in the other document instead.
            var (x, y) = ((JsonObject)a!, (JsonObject)b!);
            using var others = y.GetEnumerator();
            var aligned = true;
            foreach (var (name, value) in x)
            {
                _path.Add(name);
                if (aligned && others.MoveNext() && others.Current.Key == name)
                    Both(value, others.Current.Value, depth + 1);
                else
                {
                    aligned = false;
                    if (y.TryGetPropertyValue(name, out var other))
                        Both(value, other, depth + 1);
                    else
                        Alone(value, depth + 1, isWas: true);
                }
                _path.RemoveAt(_path.Count - 1);
            }
            if (aligned && !others.MoveNext())
                return;
            foreach (var (name, value) in y)
                if (!x.ContainsKey(name))
                    Under(name, () => Alone(value, depth + 1, isWas: false));
        }
    }

    /// <summary>Whether two leaves are written as the same JSON: the same values under the same names in the same order.</summary>
    private static bool Same(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject x, JsonObject y):
                if (x.Count != y.Count)
                    return false;
                using (var others = y.GetEnumerator())
                    foreach (var (name, value) in x)
                        if (!others.MoveNext() || name != others.Current.Key || !Same(value, others.Current.Value))
                            return false;
                return true;
            case (JsonArray x, JsonArray y):
                if (x.Count != y.Count)
                    return false;
                for (var i = 0; i < x.Count; i++)
                    if (!Same(x[i], y[i]))
                        return false;
                return true;
            case (JsonValue x, JsonValue y):
                var kind = x.GetValueKind();
                if (kind != y.GetValueKind())
                    return false;
                if (kind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                    return true;
                return kind == JsonValueKind.String && x.TryGetValue<string>(out var s) && y.TryGetValue<string>(out var t)
                    ? s == t
                    : x.ToJsonString() == y.ToJsonString();
            default:
                return false;
        }
    }

    /// <summary>The result in words, as both front ends print it.</summary>
    public static string Text(WhatIfResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"WHAT IF · a change set from {Plain.OneLine(r.Actor)} of {r.Operations} operation(s), on ledger head {r.Head} · worked out in memory; nothing was written\n");
        if (r.WouldApply)
            sb.Append("It passes every check: it could be applied as it stands.\n");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"It would be refused as it stands ({r.Findings.Count} finding(s)). What follows is what it would change if it were let through:\n");
            foreach (var f in r.Findings)
                sb.Append("  ").Append(Plain.OneLine(f.ToString())).Append('\n');
        }
        if (r.Changes.Count == 0)
            sb.Append("No answer about the story would change.\n");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"Answers that would change ({r.Changes.Count(l => l.StartsWith("  ", StringComparison.Ordinal))}):\n");
            foreach (var line in r.Changes)
                sb.Append("  ").Append(line).Append('\n');
        }
        if (r.Contexts.Count == 0)
            sb.Append("No scene's drafting context would change.\n");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"Drafting contexts that would change ({r.Contexts.Count}); a draft written from the old one would be refused as stale:\n");
            foreach (var c in r.Contexts)
                sb.Append(CultureInfo.InvariantCulture, $"  scene {c.Tell}, {c.Scene}: {c.Lines} line(s)\n");
        }
        return sb.ToString();
    }
}
