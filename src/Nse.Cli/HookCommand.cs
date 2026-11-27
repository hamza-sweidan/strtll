using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Query;

namespace Nse.Cli;

/// <summary>
/// <c>nse hook EVENT</c>: what Claude Code runs at the hook points the story folder's settings name. It reads
/// the event's JSON on standard input and answers on standard output in the form the harness reads.
/// </summary>
/// <remarks>
/// <para>Exit codes matter more here than anywhere else. To the harness, 2 means "block", so this command never
/// returns it: a refusal is said in JSON with exit 0, and anything that goes wrong is reported with exit 1,
/// which the harness treats as a hook error and does not act on. A broken hook must not lock the author out
/// of their own files.</para>
/// <para>An edit that may go ahead gets no output at all. Saying "allow" would tell the harness to skip its own
/// permission rules, and those are not the engine's to waive.</para>
/// </remarks>
public static class HookCommand
{
    public const string Events = "pre-edit, post-edit, stop, session-start, pre-compact";

#pragma warning disable CA1031 // Whatever goes wrong, the hook says so and gets out of the way.
    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var (hook, dir) = Parse(args);
            var json = ReadJson(input);
            dir ??= Str(json, "cwd") ?? Directory.GetCurrentDirectory();
            // Outside a story folder there is nothing for the engine to say.
            if (!Directory.Exists(Path.Combine(dir, "ledger")) || !Directory.Exists(Path.Combine(dir, "manuscript")))
                return 0;
            var story = Story.Open(dir);
            switch (hook)
            {
                case "pre-edit":
                    if (PreEdit(story, json) is { } reason)
                        Write(output, new JsonObject
                        {
                            ["hookSpecificOutput"] = new JsonObject
                            {
                                ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = reason,
                            },
                        });
                    return 0;
                case "post-edit":
                    if (story.AfterEdit(Str(json?["tool_input"], "file_path") ?? "") is { } said)
                        Write(output, new JsonObject
                        {
                            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PostToolUse", ["additionalContext"] = said },
                        });
                    return 0;
                case "stop":
                    // Already going on because of this hook: the session has been asked once, and asking again
                    // would only loop.
                    if (json?["stop_hook_active"] is JsonValue active && active.TryGetValue<bool>(out var yes) && yes)
                        return 0;
                    if (story.BeforeStop() is { } due)
                        Write(output, new JsonObject { ["decision"] = "block", ["reason"] = due });
                    return 0;
                case "session-start":
                    output.Write(SessionStart(story, Str(json, "source") ?? "startup"));
                    return 0;
                case "pre-compact":
                    output.Write(story.CompactionNote());
                    return 0;
                default:
                    error.WriteLine($"nse hook: unknown event '{hook}'. The events are {Events}.");
                    return 1;
            }
        }
        catch (Exception e)
        {
            error.WriteLine("nse hook: " + e.Message);
            return 1;
        }
    }
#pragma warning restore CA1031

    private static (string Hook, string? Dir) Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException($"which event? The events are {Events}.");
        string? dir = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length)
                dir = args[++i];
            else
                throw new ArgumentException($"unexpected argument '{args[i]}'");
        }
        return (args[0], dir);
    }

    private static JsonNode? ReadJson(TextReader input)
    {
        var text = input.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void Write(TextWriter output, JsonObject answer) => output.WriteLine(answer.ToJsonString());

    /// <summary>The refusal for an edit, or null if it may go ahead.</summary>
    private static string? PreEdit(Story story, JsonNode? json)
    {
        var tool = Str(json, "tool_name") ?? "";
        var input = json?["tool_input"];
        var file = Str(input, "file_path") ?? Str(input, "notebook_path");
        if (file is null)
            return null;
        EditCheck check;
        switch (tool)
        {
            case "Write":
                check = story.CheckEdit(file, [], false, Str(input, "content") ?? "");
                break;
            case "Edit":
                check = story.CheckEdit(file, [Str(input, "old_string") ?? ""], Bool(input, "replace_all"), null);
                break;
            case "MultiEdit":
                var edits = input?["edits"] as JsonArray ?? new JsonArray();
                check = story.CheckEdit(file, edits.Select(e => Str(e, "old_string") ?? "").ToList(), edits.Any(e => Bool(e, "replace_all")), null);
                break;
            default:
                check = story.CheckEdit(file, [], false, null);
                break;
        }
        return check.Refused ? check.Reason : null;
    }

    private static bool Bool(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>What a session starts from: the brief, with a line on why it is there, and after a compaction what to do first.</summary>
    public static string SessionStart(Story story, string source)
    {
        ArgumentNullException.ThrowIfNull(story);
        var head = source == "compact"
            ? "The conversation was just compacted. What survives of the work is in the files and in the tasks below. Before going on with a " +
              "scene, take its context again with scene_context, and call task_note with anything decided or still open that its task does not hold yet.\n\n"
            : "This folder is a story kept by the Narrative State Engine. Its brief follows; use the nse tools as the server's instructions say.\n\n";
        return head + ContextCompiler.Brief(story.State(), story.HeldProposals(), TaskReport.Text(story.Work()) + FeedReport.Brief(story.Feed()));
    }
}
