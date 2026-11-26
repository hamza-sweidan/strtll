using System.Text.Json.Nodes;

namespace Nse.Tests.Support;

/// <summary>Lists where two JSON documents differ, by path, so a failing comparison says what is wrong.</summary>
public static class JsonDiff
{
    public static List<string> Differences(JsonNode? expected, JsonNode? actual, string path = "", int limit = 25)
    {
        var result = new List<string>();
        Walk(expected, actual, path, result, limit);
        return result;
    }

    private static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> result, int limit)
    {
        if (result.Count >= limit)
            return;
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var key in eo.Select(kv => kv.Key).Union(ao.Select(kv => kv.Key)).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!eo.ContainsKey(key))
                    result.Add($"{path}/{key}: only the engine has it: {Show(ao[key])}");
                else if (!ao.ContainsKey(key))
                    result.Add($"{path}/{key}: the engine lacks it; expected {Show(eo[key])}");
                else
                    Walk(eo[key], ao[key], $"{path}/{key}", result, limit);
                if (result.Count >= limit)
                    return;
            }
            return;
        }
        if (expected is JsonArray ea && actual is JsonArray aa && ea.Count == aa.Count)
        {
            for (var i = 0; i < ea.Count; i++)
                Walk(ea[i], aa[i], $"{path}[{i}]", result, limit);
            return;
        }
        if (!JsonNode.DeepEquals(expected, actual))
            result.Add($"{path}: expected {Show(expected)}, the engine gives {Show(actual)}");
    }

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 160 ? text[..160] + "..." : text;
    }
}
