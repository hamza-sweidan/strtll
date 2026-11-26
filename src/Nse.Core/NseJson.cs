using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nse;

/// <summary>One way of writing and reading JSON everywhere, so files and dumps are byte-stable.</summary>
public static partial class NseJson
{
    /// <summary>Strict in, deterministic out: unknown fields are refused, output is the same on every platform.</summary>
    public static readonly JsonSerializerOptions Options = Create(indented: true);

    /// <summary>The same rules on one line, for storing a record in the index.</summary>
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // A null is left out on writing. Read back, a field that must be there would then be missing and
            // the file unreadable, so a null where a value is required is refused on the way in as well.
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            AllowOutOfOrderMetadataProperties = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = indented,
            IndentSize = 1,
            NewLine = "\n",
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => Read<T>(System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>Every way a document can fail to be what was asked for comes out as a <see cref="JsonException"/>.</summary>
    public static T Read<T>(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON value is null.");
        }
        catch (NotSupportedException e)
        {
            // What the serializer says when an operation has no "op" or a record no "kind" to tell it what it is.
            throw new JsonException("an operation is missing its \"op\", or a record its \"kind\", so it cannot be told what it is. " + e.Message, e);
        }
        catch (JsonException e) when (e.Path is not null && e.Message.Contains("doesn't allow setting null values", StringComparison.Ordinal))
        {
            // The serializer's own wording advises a programmer to change the model. Say what the writer of the file can do.
            throw new JsonException($"null is not allowed at {e.Path}. Give it a value, or leave the field out if it is optional.", e.Path, e.LineNumber, e.BytePositionInLine, e);
        }
        catch (JsonException e) when (Unmapped().Match(e.Message) is { Success: true } unmapped)
        {
            // The serializer names the program's own type here, which means nothing to whoever wrote the file.
            throw new JsonException($"\"{unmapped.Groups[1].Value}\" is not a field that belongs here. Check its spelling, or leave it out.", e.Path, e.LineNumber, e.BytePositionInLine, e);
        }
        catch (JsonException e) when (e.Path is not null && e.Message.StartsWith("The JSON value could not be converted to ", StringComparison.Ordinal))
        {
            // Likewise: the serializer goes on to name a type of the program's.
            throw new JsonException($"the value at {e.Path} is not of the kind that belongs there: text where a list is wanted, a number where text is, or the like.", e.Path, e.LineNumber, e.BytePositionInLine, e);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\AThe JSON property '([^']{1,80})' could not be mapped to any \.NET member")]
    private static partial System.Text.RegularExpressions.Regex Unmapped();

    /// <summary>
    /// The path of the first null inside a list, or null when there is none. A null property is simply left
    /// out on writing; a null in a list is written, and nothing downstream expects one.
    /// </summary>
    public static string? NullInList(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is null)
                        return $"{path}[{i}]";
                    if (NullInList(array[i], $"{path}[{i}]") is { } inner)
                        return inner;
                }
                break;
            case JsonObject obj:
                foreach (var (name, value) in obj)
                    if (NullInList(value, $"{path}.{name}") is { } inner)
                        return inner;
                break;
        }
        return null;
    }
}
