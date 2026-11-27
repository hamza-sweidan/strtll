using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nse.Cli;

/// <summary>
/// <c>nse setup-claude</c>: writes into a story folder what Claude Code needs to work with the engine. That is
/// the MCP server, the hooks, the permission rules, a skill for the scene workflow, and a short CLAUDE.md.
/// With <c>--with-auditor</c> it also writes the agent that audits a scene. That one is asked for, because
/// every audit is another model session: nothing here starts one unless the author wants it.
/// </summary>
/// <remarks>
/// <para>The server and the hooks name this machine's paths to the engine, so they go where machine-specific
/// settings belong: <c>.mcp.json</c> and <c>.claude/settings.local.json</c>, both kept out of git. The permission
/// rules, the skill and CLAUDE.md hold nothing machine-specific and go where a story's repository can share them.</para>
/// <para>A file that is already there is kept unless <c>--force</c> is given, and the command says which.
/// Run it again on each machine, and after the engine moves.</para>
/// </remarks>
public static class ClaudeSetup
{
    /// <summary>The engine's tools an agent may call without being asked each time. Deciding is not among them.</summary>
    public static readonly string[] AllowedTools =
        ["story_brief", "scene_context", "state_at", "review_queue", "task_note", "submit_scene", "propose_changes", "scan_draft", "audit_brief", "what_if", "recap", "lint", "attention", "ripple", "pulse"];

    /// <summary>
    /// The subagent that audits a scene. It reads the scene's text and its brief, and nothing else. It is
    /// written only when asked for, and used only when the author asks for an audit.
    /// </summary>
    public const string AuditorName = "scene-auditor";

    /// <summary>What the hooks are matched on: the tools that edit files.</summary>
    public const string EditTools = "Edit|Write|MultiEdit|NotebookEdit";

    /// <param name="auditor">Also write the agent that audits a scene with a fresh context.</param>
    public static int Run(Story story, string? engine, string? dotnet, bool force, TextWriter output, bool auditor = false)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(output);
        var (command, prefix) = Launcher(engine, dotnet);
        var root = story.Root;
        var files = new List<(string Path, string Text)>
        {
            (Path.Combine(root, ".mcp.json"), McpJson(command, prefix, root)),
            (Path.Combine(root, ".claude", "settings.local.json"), HookSettings(command, prefix, root)),
            (Path.Combine(root, ".claude", "settings.json"), PermissionSettings()),
            (Path.Combine(root, ".claude", "skills", "scene-work", "SKILL.md"), Skill),
            (Path.Combine(root, "CLAUDE.md"), ClaudeMd),
        };
        if (auditor)
            files.Insert(files.Count - 1, (Path.Combine(root, ".claude", "agents", AuditorName + ".md"), Auditor));
        output.WriteLine($"Setting up {root} for Claude Code, with the engine at: {string.Join(" ", new[] { command }.Concat(prefix))}");
        foreach (var (path, text) in files)
        {
            var shown = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (File.Exists(path) && !force)
            {
                output.WriteLine($"  kept      {shown} (it is there already; --force replaces it)");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine($"  written   {shown}");
        }
        if (!auditor)
            output.WriteLine($"  left out  .claude/agents/{AuditorName}.md (an audit is another model session, so it is opt-in: --with-auditor writes the agent)");
        if (KeepOutOfGit(root))
            output.WriteLine("  .gitignore now leaves out .mcp.json and .claude/settings.local.json, which name this machine's paths");
        output.WriteLine("""

            Next: open Claude Code in this folder. It asks once whether to use the nse server from .mcp.json: say yes.
            The hooks then print the story's brief at the start of every session and after every compaction; refuse
            an edit to a scene's prose until its task holds a note; ask for a note after prose is saved; and ask for
            one before the session stops, if prose has moved on since the last. /hooks lists them.
            """);
        return 0;
    }

    /// <summary>
    /// How the harness should start the engine: a .dll is run by dotnet, anything else is run as it is. Left to
    /// itself it uses the engine that is running now.
    /// </summary>
    public static (string Command, string[] Prefix) Launcher(string? engine, string? dotnet)
    {
        engine ??= typeof(Cli).Assembly.Location;
        engine = Path.GetFullPath(engine);
        if (!engine.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return (engine, []);
        var host = dotnet;
        if (host is null && Environment.ProcessPath is { } process &&
            Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            host = process;
        return (host ?? "dotnet", [engine]);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonArray Args(IEnumerable<string> parts) => new([.. parts.Select(p => (JsonNode?)JsonValue.Create(p))]);

    public static string McpJson(string command, string[] prefix, string root) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            // With --with-ratify the server offers the tool that carries out the author's decision. It is marked
            // as needing the user, and Claude Code asks before every call of such a tool; the permission rules
            // ask as well.
            ["nse"] = new JsonObject { ["command"] = command, ["args"] = Args([.. prefix, "mcp", "--with-ratify", "--dir", root]) },
        },
    }.ToJsonString(Indented) + "\n";

    public static string HookSettings(string command, string[] prefix, string root)
    {
        JsonObject Hook(string hookEvent) => new()
        {
            ["type"] = "command", ["command"] = command, ["args"] = Args([.. prefix, "hook", hookEvent, "--dir", root]), ["timeout"] = 30,
        };
        JsonArray Group(string? matcher, string hookEvent)
        {
            var group = new JsonObject();
            if (matcher is not null)
                group["matcher"] = matcher;
            group["hooks"] = new JsonArray(Hook(hookEvent));
            return new JsonArray(group);
        }
        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["SessionStart"] = Group("startup|resume|clear|compact", "session-start"),
                ["PreToolUse"] = Group(EditTools, "pre-edit"),
                ["PostToolUse"] = Group(EditTools, "post-edit"),
                ["Stop"] = Group(null, "stop"),
                ["PreCompact"] = Group(null, "pre-compact"),
            },
        }.ToJsonString(Indented) + "\n";
    }

    public static string PermissionSettings() => new JsonObject
    {
        ["permissions"] = new JsonObject
        {
            ["allow"] = Args(AllowedTools.Select(t => "mcp__nse__" + t)),
            ["ask"] = Args(["mcp__nse__ratify"]),
            // An Edit rule covers every tool that edits files. A leading slash is the story's own folder.
            ["deny"] = Args(["Edit(/ledger/**)", "Edit(/work/**)", "Edit(/.nse/**)"]),
        },
    }.ToJsonString(Indented) + "\n";

    private static bool KeepOutOfGit(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var want = new[] { ".mcp.json", ".claude/settings.local.json" }.Where(l => !lines.Contains(l)).ToList();
        if (want.Count == 0)
            return false;
        var text = new StringBuilder();
        if (lines.Count > 0 && lines[^1].Length > 0)
            text.Append('\n');
        text.Append("# Written by nse setup-claude: they name this machine's paths to the engine.\n");
        foreach (var l in want)
            text.Append(l).Append('\n');
        File.AppendAllText(path, text.ToString());
        return true;
    }

    public const string Skill = """
        ---
        name: scene-work
        description: How to draft, revise or continue a scene of this story with the Narrative State Engine. Take the scene's context, note the plan before any prose, write, submit what the scene does, and say whether more is to come. Use it whenever you are asked to write or change a scene.
        ---

        # Working on a scene

        The story's state lives in the engine, not in this conversation. Work through its tools in this order.

        1. **Start from the brief.** If it lists work in progress, pick that up before starting anything new.
        2. **Take the scene's context** with `scene_context`. It says what must not be stated to the reader, where each person
           present is shown not to know or believes the opposite, and what they believe on entry. A fact that is
           not under a person's name is one they have no record on: do not write them as knowing it, and do not
           write that they do not. That is the state on entry: someone present who has a fact can give it to them in the
           scene, planned or not; anyone may work out a view of their own, right or wrong, written as their reasoning;
           and a hint to the reader is not a statement. `state_at` with `holder` and `prop` shows
           any such pair, who was there when a thing was said, and the passage behind
           any line. A `*` marks a record the author has not reviewed yet: it is the story as it stands, so
           write on from it. `IN QUESTION` lists records whose footing has gone: build nothing on those, and
           tell the author. It opens a task for the scene.
        3. **Note your plan** with `task_note` before you write a word: what you have decided, the questions still
           open, the next step. A hook refuses edits to a scene's prose until its task holds a note.
        4. **Write the prose** in the scene's manuscript file.
        5. **Note again** after each save: what you decided while writing, what is open now, how far the draft has
           got. A hook reminds you when the prose has moved on since your last note.
        6. **Submit** with `submit_scene`: the scene id, the manifest id from the end of the context, and the
           records the text supports, each quoting the text word for word. A fact is one detail: two details are
           separate propositions when someone could learn them at different times. An event that could not
           happen without some fact names it under `needs`, so that a later change to the fact finds the passage.
        7. **After a submission**, note whether more of the scene is still to be written: `more` true or false.
           A hook asks for this before you stop if you have not said.
        8. **Tell the author** what you recorded and every note left for them to settle, a `TRIPWIRE` note with
           the passage it points at. Only they decide.

        **An audit is the author's to ask for.** It is a second reading of one scene by a reviewer with a fresh
        context, against what must not be given away, and it starts another model session. Do not start one
        on your own. When the author asks for an audit, give `audit_brief` for the scene to such a reviewer:
        the `scene-auditor` agent, if this folder has one. Report what it finds, and do not rewrite the scene
        to answer it unless the author asks.

        Never edit files under `ledger/` or `work/`: the engine writes them, and the permission rules refuse it.
        Rules, embargoes, secrets and anything else that is the author's go through `propose_changes`.
        """;

    /// <summary>
    /// The auditor: a fresh context that receives only the scene's text and the
    /// list of what must not be given away, and reports what the text reveals or presupposes. Its tools are the
    /// brief and the file reader, so it cannot draft, submit or decide. It inherits the session's model.
    /// </summary>
    public const string Auditor = """
        ---
        name: scene-auditor
        description: Audits one scene of this story for leaks, reading its text against what the reader must not be given yet and what each person present does not know. Use only when the author asks for an audit of a scene; never as a routine step after a submission. Give it the scene id, and say "completeness" to have it check the records against the text instead.
        tools: Read, mcp__nse__audit_brief
        ---

        You audit one scene of a story. You do not write or change anything.

        1. Call `audit_brief` with the scene id you were given. Set `completeness` only if you were asked to check
           completeness.
        2. Read the scene's text with Read, at the file and lines the brief names. Read no other prose and no other
           file: what you know of the story is the brief, on purpose.
        3. Report exactly in the form the brief's REPORT part gives, and nothing else: one line per finding, each
           quoting the text word for word, then the last line it asks for.

        Judge only what the text gives away, or for completeness what it does that the records do not say. A hint
        the reader may or may not pick up is not a leak. Do not judge style, and do not suggest rewrites.
        """;

    public const string ClaudeMd = """
        # This folder is a story

        Its state is kept by the Narrative State Engine. The `nse` MCP server is how you read and change it.

        - **Begin with `story_brief`.** The session-start hook prints it for you, and prints it again after a compaction.
        - **The engine's answers outrank your memory of the conversation.** They are computed from the files and the ledger each time.
        - **To write or change a scene, follow the `scene-work` skill.** In short: `scene_context`, then `task_note` with your plan, then the prose, then `task_note` again, then `submit_scene`.
        - **An audit of a scene by a fresh reviewer is opt-in.** It is another model session: run one when the author asks, not after every submission.
        - **The engine writes `ledger/` and `work/`.** Never edit them yourself.
        - **The author decides what becomes canon.** Propose; do not assume.
        """;
}
