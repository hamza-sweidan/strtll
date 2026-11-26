using System.Text.Json;
using Nse.Query;

namespace Nse.Tests.Support;

/// <summary>
/// The 40 probes carry machine-checkable expectations and a binding from every answer option to a state. This
/// asks the engine each question and reports where its answer is not the keyed one.
/// </summary>
public static class ProbeChecker
{
    public static IReadOnlyList<string> Failures(StoryState s, JsonElement probes, out int probeCount)
    {
        var fails = new List<string>();
        probeCount = 0;
        foreach (var p in probes.EnumerateArray())
        {
            probeCount++;
            var id = p.GetProperty("id").GetString()!;
            var answer = p.GetProperty("answer").GetString()!;
            var options = p.GetProperty("options").EnumerateObject().Select(o => o.Name).ToList();
            if (!options.Contains(answer))
                fails.Add($"{id}: answer {answer} is not one of the options");
            var checks = p.GetProperty("checks").EnumerateArray().ToList();
            foreach (var c in checks)
            {
                var (ok, got) = CheckOne(s, c);
                if (!ok)
                    fails.Add($"{id}: expected {(c.TryGetProperty("expect", out var e) ? e.GetRawText() : "nothing")} for {c.GetRawText()}, the engine gives {got}");
            }
            var outcomes = p.GetProperty("outcomes");
            var first = checks[0];
            var actual = Actual(s, first);
            var kind = first.GetProperty("kind").GetString()!;
            var matching = outcomes.EnumerateObject().Where(o => OptionMatches(o.Value, actual, kind))
                .Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (matching.Count != 1 || matching[0] != answer)
                fails.Add($"{id}: the key says {answer}, but the engine's state matches option(s) {(matching.Count == 0 ? "none" : string.Join(",", matching))}");
        }
        return fails;
    }

    private static double KeyOf(StoryState s, JsonElement c) =>
        c.TryGetProperty("at", out var at) && at.GetString() == "end" ? StoryState.End
        : c.TryGetProperty("scene", out var scene) ? s.Key(scene.GetString()!)
        : StoryState.End;

    /// <summary>What the engine says for one check: a belief view, a place, or a label.</summary>
    private static object? Actual(StoryState s, JsonElement c)
    {
        var key = KeyOf(s, c);
        string P(string name) => c.GetProperty(name).GetString()!;
        switch (P("kind"))
        {
            case "belief":
                return s.BeliefsBefore(P("holder"), key).GetValueOrDefault(P("prop"));
            case "nobody":
                var some = c.GetProperty("holders").EnumerateArray().Select(h => h.GetString()!)
                    .Any(h => s.BeliefsBefore(h, key).GetValueOrDefault(P("prop")) is { } b && b.Stance != Model.Stance.Unaware);
                return some ? "somebody" : "nobody";
            case "location":
                return s.Location(P("object"), key)?.Place;
            case "reader_revealed":
                return "reveal:" + (s.RevealScene(P("prop")) ?? "never");
            case "canon":
                return s.Canon(P("prop"));
            case "assertion":
                return s.ClassifyAssertion(P("assertion"));
            case "learns":
                return s.FirstHolds(P("holder"), P("prop")) is { } first ? first.Scene ?? "background" : "never";
            case "order":
                // The engine's own answer for each pair, not a comparison made here.
                var events = c.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToList();
                return events.Skip(1).All(e => s.Order(events[0], e) == "not_established") ? "not_established" : "established";
            default:
                throw new InvalidOperationException($"unknown check kind {P("kind")}");
        }
    }

    private static (bool Ok, string Got) CheckOne(StoryState s, JsonElement c)
    {
        var kind = c.GetProperty("kind").GetString()!;
        var got = Actual(s, c);
        var hasExpect = c.TryGetProperty("expect", out var exp) && exp.ValueKind != JsonValueKind.Null;
        switch (kind)
        {
            case "belief":
                var b = got as BeliefView;
                var shown = b is null ? "no record" : StanceName(b) + (b.Strength is { } st ? "/" + Names.Of(st) : "");
                if (!hasExpect)
                    return (b is null, shown);
                var ok = b is not null && StanceName(b) == exp.GetProperty("stance").GetString()
                         && (!exp.TryGetProperty("strength", out var str) || (b.Strength is { } have && Names.Of(have) == str.GetString()));
                return (ok, shown);
            case "nobody":
                return ((string)got! == "nobody", (string)got!);
            case "reader_revealed":
                var reveal = s.RevealScene(c.GetProperty("prop").GetString()!);
                var revealed = reveal is not null && s.Tell(reveal) < s.Tell(c.GetProperty("scene").GetString()!);
                return (revealed == exp.GetBoolean(), $"revealed in {reveal ?? "no scene"}");
            case "assertion":
                return (((string)got!).StartsWith(exp.GetString()!, StringComparison.Ordinal), (string)got!);
            default:
                var text = got as string;
                return (hasExpect ? text == exp.GetString() : text is null, text ?? "nothing");
        }
    }

    /// <summary>Does an answer option describe what the engine says? "other" is a distractor; "none" means no record.</summary>
    private static bool OptionMatches(JsonElement spec, object? got, string kind)
    {
        if (spec.ValueKind == JsonValueKind.Object)
        {
            if (got is not BeliefView b)
                return false;
            foreach (var field in spec.EnumerateObject())
            {
                var have = field.Name switch
                {
                    "stance" => StanceName(b),
                    "strength" => b.Strength is { } st ? Names.Of(st) : null,
                    "via" => b.Via,
                    _ => throw new InvalidOperationException($"an option binds the field {field.Name}, which the engine does not report"),
                };
                if (have != field.Value.GetString())
                    return false;
            }
            return true;
        }
        var text = spec.GetString()!;
        if (text == "other")
            return false;
        if (text == "none")
            return got is null;
        if (kind == "assertion")
            return got is string label && label.StartsWith(text, StringComparison.Ordinal);
        return got is string value && value == text;
    }

    private static string StanceName(BeliefView b) => b.Stance is { } s ? Names.Of(s) : "unresolved";
}
