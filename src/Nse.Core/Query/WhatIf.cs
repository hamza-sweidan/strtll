using System.Globalization;
using System.Text;
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
        var was = Flatten(StateDump.Build(before));
        var now = Flatten(StateDump.Build(after));
        var keys = was.Keys.Union(now.Keys).Where(k => k != "head").Where(k => was.GetValueOrDefault(k) != now.GetValueOrDefault(k)).ToList();
        // The points in the order they are told in the state after, then the end; within a point, people, the
        // reader, the world and what is so, each in id order.
        var telling = after.Telling.Select(t => t.Id).Concat(before.Telling.Select(t => t.Id)).Distinct().ToList();
        int Rank(string key)
        {
            var parts = key.Split('/');
            if (parts[0] != "points")
                return -1;
            return parts[1] == "end" ? int.MaxValue : telling.IndexOf(parts[1]);
        }
        var lines = new List<string>();
        string? heading = null;
        foreach (var key in keys.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal))
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
            lines.Add("  " + Plain.OneLine(Describe(before, after, parts, was.GetValueOrDefault(key), now.GetValueOrDefault(key))));
        }
        return lines;
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
    /// The dump as paths to leaves. A belief, a reader's view and a location are leaves whole: what changes is the
    /// answer, and an answer is all of its fields together.
    /// </summary>
    private static Dictionary<string, string> Flatten(JsonNode root)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string path, int depth)
        {
            var leaf = node is not JsonObject o
                       || o.ContainsKey("id")
                       || (path.StartsWith("points/", StringComparison.Ordinal) && depth >= 5);
            if (leaf)
            {
                flat[path] = node?.ToJsonString() ?? "null";
                return;
            }
            foreach (var (name, value) in (JsonObject)node!)
                Walk(value, path.Length == 0 ? name : path + "/" + name, depth + 1);
        }
        Walk(root, "", 0);
        return flat;
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
