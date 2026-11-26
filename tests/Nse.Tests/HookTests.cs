using System.Text.Json.Nodes;
using Nse.Tasks;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// The hooks. The engine answers the questions Claude Code's hooks ask: may this edit
/// go ahead, is there anything to note after this save, may the session stop, what should a compaction keep.
/// The harness and a model are not needed to test the answers.
/// </summary>
public sealed class HookTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private Story Story => _tiny.Story;

    private string SceneOneFile => Path.Combine(Story.ManuscriptDir, "01-one.md");
    private string SceneTwoFile => Path.Combine(Story.ManuscriptDir, "02-two.md");

    private EditCheck EditSceneTwo(string? path = null) =>
        Story.CheckEdit(path ?? SceneTwoFile, ["Ben found the key under the mat"], false, null);

    private void Note(string scene = "s2", string decided = "Ben keeps the key a secret.") =>
        Assert.True(Story.Note(new TaskNote { Scene = scene, Decided = [decided] }).Noted);

    private void ChangeSceneTwo(string to = "Ben found the key under the mat, turned it over, and put it in his pocket.") =>
        File.WriteAllText(SceneTwoFile, TinyStory.Scene2.Replace("Ben found the key under the mat and put it in his pocket.", to));

    // ---------------------------------------------------------------- before an edit

    [Fact]
    public void An_edit_to_a_scene_with_no_task_is_refused_with_what_to_do()
    {
        var check = EditSceneTwo();
        Assert.True(check.Refused);
        Assert.Equal(["s2"], check.Scenes);
        Assert.Contains("s2: no task is open", check.Reason);
        Assert.Contains("scene_context", check.Reason);
        Assert.Contains("task_note", check.Reason);
        Assert.Contains(Story.WhyNoteFirst, check.Reason);
    }

    [Fact]
    public void A_task_with_only_a_context_is_not_enough_the_plan_comes_first()
    {
        Story.TakeContext("s2");
        var check = EditSceneTwo();
        Assert.True(check.Refused);
        Assert.Contains("its task holds no note yet", check.Reason);
    }

    [Fact]
    public void Once_the_task_holds_a_note_the_edit_goes_ahead()
    {
        Story.TakeContext("s2");
        Note();
        var check = EditSceneTwo();
        Assert.False(check.Refused);
        Assert.Null(check.Reason);
        Assert.Equal(["s2"], check.Scenes);
    }

    [Fact]
    public void The_ledger_and_the_engines_working_files_are_never_to_be_edited()
    {
        Assert.True(Story.CheckEdit(Path.Combine(Story.LedgerDir, "000001-cs_x.json"), ["a"], false, null).Refused);
        Assert.True(Story.CheckEdit(Path.Combine(_tiny.Root, "work", "tasks", "s2.json"), [], false, "{}").Refused);
        Assert.Contains("submit_scene", Story.CheckEdit(Path.Combine(Story.LedgerDir, "x.json"), [], false, "").Reason);
    }

    [Fact]
    public void Files_outside_the_manuscript_are_not_the_engines_to_judge()
    {
        Assert.Same(EditCheck.Fine, Story.CheckEdit(Path.Combine(_tiny.Root, "notes.md"), ["anything"], false, null));
        Assert.Same(EditCheck.Fine, Story.CheckEdit(Path.Combine(Path.GetTempPath(), "elsewhere.md"), ["x"], false, null));
        Assert.Same(EditCheck.Fine, Story.CheckEdit(Path.Combine(Story.ManuscriptDir, "picture.png"), [], false, "x"));
    }

    [Fact]
    public void Text_that_is_not_in_the_file_touches_nothing_since_the_edit_fails_by_itself()
    {
        var check = Story.CheckEdit(SceneTwoFile, ["words that are nowhere in it"], false, null);
        Assert.False(check.Refused);
        Assert.Empty(check.Scenes);
    }

    [Fact]
    public void A_path_written_with_forward_slashes_or_relative_to_the_story_is_the_same_file()
    {
        Assert.True(EditSceneTwo(SceneTwoFile.Replace('\\', '/')).Refused);
        Assert.True(EditSceneTwo("manuscript/02-two.md").Refused);
    }

    [Fact]
    public void A_whole_file_write_is_judged_by_the_scenes_it_changes()
    {
        // The same text again changes no scene.
        Assert.False(Story.CheckEdit(SceneTwoFile, [], false, TinyStory.Scene2).Refused);
        // New text in scene 2 does.
        var check = Story.CheckEdit(SceneTwoFile, [], false, TinyStory.Scene2.Replace("pocket", "coat"));
        Assert.True(check.Refused);
        Assert.Equal(["s2"], check.Scenes);
        // A file written with Windows line endings is the same text.
        Assert.False(Story.CheckEdit(SceneTwoFile, [], false, TinyStory.Scene2.Replace("\n", "\r\n")).Refused);
    }

    [Fact]
    public void A_new_scene_has_no_work_to_guard_until_it_has_a_marker()
    {
        var check = Story.CheckEdit(Path.Combine(Story.ManuscriptDir, "03-three.md"), [], false, "<!-- scene: s3 -->\n# Three\n\nAnn came back.\n");
        Assert.False(check.Refused);
        Assert.Equal(["s3"], check.Scenes);
    }

    [Fact]
    public void In_a_file_with_two_scenes_only_the_scene_whose_lines_are_changed_is_judged()
    {
        File.WriteAllText(SceneOneFile, TinyStory.Scene1 + "\n" + TinyStory.Scene2);
        File.Delete(SceneTwoFile);
        Story.TakeContext("s1");
        Note("s1", "Ann lies to the hall.");

        // Scene 1 has a noted task: an edit inside it goes ahead.
        Assert.False(Story.CheckEdit(SceneOneFile, ["Ann hid the key under the mat."], false, null).Refused);
        // Scene 2, in the same file, has none: an edit inside it is refused, and names only it.
        var two = Story.CheckEdit(SceneOneFile, ["put it in his pocket"], false, null);
        Assert.True(two.Refused);
        Assert.Equal(["s2"], two.Scenes);
        // Its marker line belongs to it.
        Assert.Equal(["s2"], Story.CheckEdit(SceneOneFile, ["<!-- scene: s2 -->"], false, null).Scenes);
        // Text running across the boundary touches both.
        Assert.Equal(["s1", "s2"], Story.CheckEdit(SceneOneFile, ["to the empty hall.\n\n<!-- scene: s2 -->"], false, null).Scenes);
    }

    [Fact]
    public void Replace_all_judges_every_place_the_text_occurs()
    {
        File.WriteAllText(SceneOneFile, TinyStory.Scene1 + "\n" + TinyStory.Scene2);
        File.Delete(SceneTwoFile);
        Story.TakeContext("s1");
        Note("s1", "Ann lies to the hall.");
        Assert.False(Story.CheckEdit(SceneOneFile, ["the key"], false, null).Refused);
        Assert.True(Story.CheckEdit(SceneOneFile, ["the key"], true, null).Refused);
    }

    // ---------------------------------------------------------------- after a save

    [Fact]
    public void After_prose_is_saved_the_engine_asks_for_a_note_until_one_is_made()
    {
        Story.TakeContext("s2");
        Note();
        Assert.Null(Story.AfterEdit(SceneTwoFile));
        ChangeSceneTwo();
        var said = Story.AfterEdit(SceneTwoFile);
        Assert.NotNull(said);
        Assert.Contains("s2", said);
        Assert.Contains("task_note", said);
        Note(decided: "He turns the key over: he knows it is not his.");
        Assert.Null(Story.AfterEdit(SceneTwoFile));
    }

    [Fact]
    public void A_save_outside_the_manuscript_says_nothing()
    {
        Story.TakeContext("s2");
        Note();
        ChangeSceneTwo();
        Assert.Null(Story.AfterEdit(Path.Combine(_tiny.Root, "notes.md")));
        Assert.Null(Story.AfterEdit(SceneOneFile));
    }

    // ---------------------------------------------------------------- before stopping

    [Fact]
    public void A_session_may_not_stop_while_its_prose_is_ahead_of_its_notes()
    {
        Story.TakeContext("s2");
        Note();
        Assert.Null(Story.BeforeStop());
        ChangeSceneTwo();
        var due = Story.BeforeStop();
        Assert.NotNull(due);
        Assert.Contains("s2 (prose changed since the last note)", due);
        Note(decided: "Done with the pocket.");
        Assert.Null(Story.BeforeStop());
    }

    [Fact]
    public void Prose_written_with_nothing_noted_at_all_is_caught_at_the_stop()
    {
        Story.TakeContext("s2");
        ChangeSceneTwo();
        Assert.Contains("s2 (prose written, nothing noted)", Story.BeforeStop());
    }

    [Fact]
    public void A_task_the_author_opened_is_not_held_against_the_session()
    {
        Story.TakeContext("s2", actor: "author");
        Note();
        ChangeSceneTwo();
        Assert.Null(Story.BeforeStop());
    }

    // ---------------------------------------------------------------- compaction

    [Fact]
    public void The_compaction_note_names_each_open_task_and_its_manifest()
    {
        var package = Story.TakeContext("s2");
        Note();
        var note = Story.CompactionNote();
        Assert.Contains("- s2, manifest " + package.ManifestId, note);
        Assert.Contains("task_note", note);
        Assert.True(note.Length < 10_000);
    }

    // ---------------------------------------------------------------- the command the harness runs

    private (int Code, string Out, string Err) Hook(string hookEvent, string json, string? dir = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Cli.Cli.Run(["hook", hookEvent, "--dir", dir ?? _tiny.Root], output, error, null, new StringReader(json));
        return (code, output.ToString(), error.ToString());
    }

    private string EditEvent(string old) => new JsonObject
    {
        ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Edit", ["cwd"] = _tiny.Root,
        ["tool_input"] = new JsonObject { ["file_path"] = SceneTwoFile, ["old_string"] = old, ["new_string"] = "x" },
    }.ToJsonString();

    [Fact]
    public void The_pre_edit_hook_refuses_in_the_harness_json_and_exits_0()
    {
        var (code, output, _) = Hook("pre-edit", EditEvent("Ben found the key"));
        Assert.Equal(0, code);
        var answer = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", (string?)answer["hookEventName"]);
        Assert.Equal("deny", (string?)answer["permissionDecision"]);
        Assert.Contains("task_note", (string?)answer["permissionDecisionReason"]);
    }

    [Fact]
    public void An_edit_that_may_go_ahead_gets_no_output_so_the_harness_keeps_its_own_rules()
    {
        Story.TakeContext("s2");
        Note();
        var (code, output, _) = Hook("pre-edit", EditEvent("Ben found the key"));
        Assert.Equal(0, code);
        Assert.Equal("", output);
    }

    [Fact]
    public void The_write_and_multi_edit_tools_are_read_as_well()
    {
        var write = new JsonObject
        {
            ["tool_name"] = "Write",
            ["tool_input"] = new JsonObject { ["file_path"] = SceneTwoFile, ["content"] = TinyStory.Scene2.Replace("pocket", "coat") },
        }.ToJsonString();
        Assert.Contains("\"deny\"", Hook("pre-edit", write).Out);
        var multi = new JsonObject
        {
            ["tool_name"] = "MultiEdit",
            ["tool_input"] = new JsonObject
            {
                ["file_path"] = SceneTwoFile,
                ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "pocket", ["new_string"] = "coat" }),
            },
        }.ToJsonString();
        Assert.Contains("\"deny\"", Hook("pre-edit", multi).Out);
    }

    [Fact]
    public void The_post_edit_hook_adds_its_reminder_as_context()
    {
        Story.TakeContext("s2");
        Note();
        ChangeSceneTwo();
        var json = new JsonObject { ["tool_name"] = "Edit", ["tool_input"] = new JsonObject { ["file_path"] = SceneTwoFile } }.ToJsonString();
        var (code, output, _) = Hook("post-edit", json);
        Assert.Equal(0, code);
        var answer = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        Assert.Equal("PostToolUse", (string?)answer["hookEventName"]);
        Assert.Contains("s2", (string?)answer["additionalContext"]);
    }

    [Fact]
    public void The_stop_hook_blocks_once_and_not_again_while_the_session_is_already_answering_it()
    {
        Story.TakeContext("s2");
        ChangeSceneTwo();
        var first = JsonNode.Parse(Hook("stop", "{\"stop_hook_active\": false}").Out)!;
        Assert.Equal("block", (string?)first["decision"]);
        Assert.Contains("s2", (string?)first["reason"]);
        Assert.Equal("", Hook("stop", "{\"stop_hook_active\": true}").Out);
    }

    [Fact]
    public void The_session_start_hook_prints_the_brief_and_after_a_compaction_says_what_to_do_first()
    {
        var start = Hook("session-start", "{\"source\": \"startup\"}");
        Assert.Equal(0, start.Code);
        Assert.Contains("STORY BRIEF", start.Out);
        var after = Hook("session-start", "{\"source\": \"compact\"}");
        Assert.StartsWith("The conversation was just compacted.", after.Out);
        Assert.Contains("STORY BRIEF", after.Out);
    }

    [Fact]
    public void The_pre_compact_hook_prints_the_compaction_note()
    {
        Story.TakeContext("s2");
        // A context taken and nothing more is a look, not work, and is not listed.
        Assert.DoesNotContain("- s2", Hook("pre-compact", "{\"trigger\": \"auto\"}").Out);
        Note();
        Assert.Contains("- s2, manifest m_", Hook("pre-compact", "{\"trigger\": \"auto\"}").Out);
    }

    [Fact]
    public void A_hook_never_exits_with_2_which_the_harness_reads_as_block()
    {
        Assert.Equal(1, Hook("no-such-event", "{}").Code);
        Assert.Equal(0, Hook("pre-edit", "this is not json").Code);
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["hook"], output, error, null, new StringReader("")));
        Assert.Equal(1, Cli.Cli.Run(["hook", "stop", "--what"], output, error, null, new StringReader("")));
    }

    [Fact]
    public void Outside_a_story_folder_a_hook_says_nothing()
    {
        var elsewhere = Fixtures.NewTempDir();
        try
        {
            var (code, output, _) = Hook("pre-edit", EditEvent("Ben found the key"), elsewhere);
            Assert.Equal(0, code);
            Assert.Equal("", output);
        }
        finally
        {
            Fixtures.DeleteDirectory(elsewhere);
        }
    }

    // ---------------------------------------------------------------- setting a story folder up

    [Fact]
    public void Setup_writes_the_server_the_hooks_the_rules_the_skill_and_claude_md()
    {
        var output = new StringWriter();
        var code = Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--dotnet", "C:/dotnet/dotnet.exe", "--dir", _tiny.Root],
            output, new StringWriter());
        Assert.Equal(0, code);

        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(_tiny.Root, ".mcp.json")))!["mcpServers"]!["nse"]!;
        Assert.Equal("C:/dotnet/dotnet.exe", (string?)mcp["command"]);
        var mcpArgs = mcp["args"]!.AsArray().Select(a => (string?)a).ToList();
        Assert.Equal(Path.GetFullPath("C:/engine/nse.dll"), mcpArgs[0]);
        Assert.Equal(["mcp", "--with-ratify", "--dir", _tiny.Root], mcpArgs[1..]);

        var hooks = JsonNode.Parse(File.ReadAllText(Path.Combine(_tiny.Root, ".claude", "settings.local.json")))!["hooks"]!;
        foreach (var (hookEvent, command) in new[]
                 {
                     ("SessionStart", "session-start"), ("PreToolUse", "pre-edit"), ("PostToolUse", "post-edit"), ("Stop", "stop"), ("PreCompact", "pre-compact"),
                 })
        {
            var handler = hooks[hookEvent]![0]!["hooks"]![0]!;
            Assert.Equal("command", (string?)handler["type"]);
            Assert.Equal(["hook", command, "--dir", _tiny.Root], handler["args"]!.AsArray().Select(a => (string?)a).Skip(1).ToList());
        }
        Assert.Equal("Edit|Write|MultiEdit|NotebookEdit", (string?)hooks["PreToolUse"]![0]!["matcher"]);

        var permissions = JsonNode.Parse(File.ReadAllText(Path.Combine(_tiny.Root, ".claude", "settings.json")))!["permissions"]!;
        Assert.Contains("Edit(/ledger/**)", permissions["deny"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(["mcp__nse__ratify"], permissions["ask"]!.AsArray().Select(a => (string?)a));
        Assert.DoesNotContain("mcp__nse__ratify", permissions["allow"]!.AsArray().Select(a => (string?)a));

        Assert.StartsWith("---\nname: scene-work\n", File.ReadAllText(Path.Combine(_tiny.Root, ".claude", "skills", "scene-work", "SKILL.md")).Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(_tiny.Root, "CLAUDE.md")));
        var ignored = File.ReadAllLines(Path.Combine(_tiny.Root, ".gitignore"));
        Assert.Contains(".mcp.json", ignored);
        Assert.Contains(".claude/settings.local.json", ignored);
    }

    [Fact]
    public void Setup_keeps_what_is_there_unless_told_to_replace_it()
    {
        var claudeMd = Path.Combine(_tiny.Root, "CLAUDE.md");
        File.WriteAllText(claudeMd, "the author's own notes");
        var output = new StringWriter();
        Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--dir", _tiny.Root], output, new StringWriter());
        Assert.Equal("the author's own notes", File.ReadAllText(claudeMd));
        Assert.Contains("kept      CLAUDE.md", output.ToString());
        Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--dir", _tiny.Root], new StringWriter(), new StringWriter());
        Assert.Single(File.ReadAllLines(Path.Combine(_tiny.Root, ".gitignore")), l => l == ".mcp.json");
        Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--force", "--dir", _tiny.Root], new StringWriter(), new StringWriter());
        Assert.StartsWith("# This folder is a story", File.ReadAllText(claudeMd));
    }

    [Fact]
    public void An_engine_that_is_not_a_dll_is_run_as_it_is()
    {
        var (command, prefix) = Cli.ClaudeSetup.Launcher("C:/engine/nse.exe", "C:/dotnet/dotnet.exe");
        Assert.Equal(Path.GetFullPath("C:/engine/nse.exe"), command);
        Assert.Empty(prefix);
    }
}
