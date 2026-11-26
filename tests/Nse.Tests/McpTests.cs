using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Cli;

namespace Nse.Tests;

/// <summary>
/// The MCP server, driven the way a client drives it: JSON-RPC messages, one per line, over a pair of
/// streams. This checks the wiring and the three tools at protocol level. It does not involve a model.
/// </summary>
public sealed class McpTests(HarbourStory harbour) : IClassFixture<HarbourStory>
{
    private sealed class Client : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _fromServer = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
        private readonly Stream _write;
        private readonly StreamReader _read;
        private readonly Task _server;
        private int _id;

        public Client(string storyRoot, bool ratifyTool = false)
        {
            _write = _toServer.Writer.AsStream();
            _read = new StreamReader(_fromServer.Reader.AsStream(), Encoding.UTF8);
            _server = McpHost.RunAsync(storyRoot, _toServer.Reader.AsStream(), _fromServer.Writer.AsStream(), _stop.Token, ratifyTool);
        }

        private async Task Send(JsonObject message)
        {
            await _write.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), _stop.Token);
            await _write.FlushAsync(_stop.Token);
        }

        public Task Notify(string method) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

        /// <summary>Send a request and return the response with the same id, skipping anything else the server says.</summary>
        public async Task<JsonNode> Request(string method, JsonObject? parameters = null)
        {
            var id = ++_id;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
                message["params"] = parameters;
            await Send(message);
            while (true)
            {
                var line = await _read.ReadLineAsync(_stop.Token) ?? throw new EndOfStreamException("the server closed the stream");
                var node = JsonNode.Parse(line)!;
                if (node["id"] is { } got && got.GetValueKind() == JsonValueKind.Number && got.GetValue<int>() == id)
                    return node;
            }
        }

        public async Task<JsonNode> Call(string tool, JsonObject arguments) =>
            (await Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }))["result"]!;

        public static string TextOf(JsonNode result) =>
            string.Join("\n", result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>() ?? ""));

        public async ValueTask DisposeAsync()
        {
            await _toServer.Writer.CompleteAsync();
            await _stop.CancelAsync();
            try
            {
                await _server;
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Dispose();
        }
    }

    private Task<Client> Connect() => ConnectTo(harbour.Root);

    private static async Task<Client> ConnectTo(string root, bool ratifyTool = false)
    {
        var client = new Client(root, ratifyTool);
        var init = await client.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "nse-tests", ["version"] = "0" },
        });
        Assert.Equal("nse", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        await client.Notify("notifications/initialized");
        return client;
    }

    [Fact]
    public async Task The_server_introduces_itself_with_its_operating_instructions()
    {
        await using var client = new Client(harbour.Root);
        var init = await client.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "nse-tests", ["version"] = "0" },
        });
        var result = init["result"]!;
        Assert.Equal("nse", result["serverInfo"]!["name"]!.GetValue<string>());
        var instructions = result["instructions"]!.GetValue<string>();
        Assert.Contains("\"no record\" means nothing has been recorded either way", instructions);
        Assert.Contains("PROVISIONAL", instructions);
        Assert.True(instructions.Length < 4096, "Claude Code reads at most 4,096 characters of server instructions");
        Assert.NotNull(result["capabilities"]!["tools"]);

        // Started as it is by default, the server has no tool that decides, and says so.
        Assert.Equal(McpHost.Instructions(ratifyTool: false), instructions);
        Assert.Contains("You have no tool that decides.", instructions);
        Assert.DoesNotContain("call ratify", instructions);
        // Started to offer it, the instructions say when to call it. Nothing is left of the placeholders either way.
        var offering = McpHost.Instructions(ratifyTool: true);
        Assert.Contains("call ratify with exactly those ids: it shows them an approval\n    prompt. Never call it on your own judgment.", offering);
        // Either way it says how a session leaves its work for the next one, and where the next one finds it.
        Assert.Contains("any work in progress that\n   an earlier session left. Pick that up before starting anything new.", instructions);
        // The note comes before the prose, and the instructions say so in that order.
        Assert.Contains("6. Before you write or change any prose, call task_note with your plan: what you have decided,\n   the questions still open, the next step. Call it again each time you decide something more,", instructions);
        Assert.Contains("A session can end at any moment without warning, and a\n   later one has nothing but these notes to go on.", instructions);
        // And when a task ends: by the author's decision, unless the session says that more is to come.
        Assert.Contains(
            "The scene's task is put away once they have decided. A task_note made after you\n   submit must say whether more of the scene is still to be written: more true or false.", instructions);
        Assert.True(offering.Length < 4096, "Claude Code reads at most 4,096 characters of server instructions");
        Assert.DoesNotContain("{", instructions + offering);
    }

    [Fact]
    public async Task It_offers_ten_tools_that_only_read_and_four_that_write_something()
    {
        await using var client = await Connect();
        var tools = (await client.Request("tools/list"))["result"]!["tools"]!.AsArray();
        // No tool that decides: the server offers that one only when it is started to.
        Assert.Equal(["audit_brief", "lint", "propose_changes", "pulse", "recap", "review_queue", "ripple", "scan_draft", "scene_context", "state_at", "story_brief", "submit_scene", "task_note", "what_if"],
            tools.Select(t => t!["name"]!.GetValue<string>()).Order());
        // scene_context is among those that write: taken to work from, it writes the scene's task. It is not
        // marked as read-only, because a client is entitled to believe the mark.
        string[] writing = ["submit_scene", "propose_changes", "task_note", "scene_context"];
        foreach (var tool in tools)
        {
            var writes = writing.Contains(tool!["name"]!.GetValue<string>());
            Assert.True(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>() != writes, $"{tool["name"]}: only {string.Join(", ", writing)} may write");
            Assert.False(tool["annotations"]!["destructiveHint"]?.GetValue<bool>() ?? false, $"{tool["name"]} destroys nothing");
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
        }
        var submit = tools.Single(t => t!["name"]!.GetValue<string>() == "submit_scene")!;
        Assert.Equal(["manifest", "records", "scene"], submit["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        Assert.Equal("array", submit["inputSchema"]!["properties"]!["records"]!["type"]!.GetValue<string>());
        var stateAt = tools.Single(t => t!["name"]!.GetValue<string>() == "state_at")!;
        var properties = stateAt["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order();
        Assert.Equal(["end", "head", "history", "holder", "object", "prop", "reader", "scene"], properties);
        var context = tools.Single(t => t!["name"]!.GetValue<string>() == "scene_context")!;
        Assert.Equal(["scene"], context["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task The_tools_answer_with_what_the_command_line_prints()
    {
        await using var client = await Connect();
        var state = await client.Call("state_at", new JsonObject { ["scene"] = "sc_07", ["holder"] = "ch_daniel" });
        Assert.NotEqual(true, state["isError"]?.GetValue<bool>());
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["state-at", "--scene", "sc_07", "--holder", "ch_daniel", "--dir", harbour.Root], output, new StringWriter()));
        Assert.Equal(output.ToString().TrimEnd(), Client.TextOf(state).TrimEnd());
        Assert.Contains("P03  [FALSE BELIEF] takes it as true, believes", Client.TextOf(state));

        var context = Client.TextOf(await client.Call("scene_context", new JsonObject { ["scene"] = "sc_09" }));
        Assert.Contains("CONTEXT FOR SCENE 9, sc_09 \"The Safe\"", context);
        Assert.Contains("never learns it: SE1 keeps it to Sarah", context);

        var brief = Client.TextOf(await client.Call("story_brief", new JsonObject()));
        Assert.StartsWith("STORY BRIEF · ledger head ", brief);
        Assert.Contains("told out of story order", brief);

        var end = Client.TextOf(await client.Call("state_at", new JsonObject { ["end"] = true, ["object"] = "obj_letter" }));
        Assert.Contains("the letter (obj_letter): Jonas Pike's hands", end);
        var older = Client.TextOf(await client.Call("state_at", new JsonObject { ["end"] = true, ["object"] = "obj_letter", ["head"] = 5 }));
        Assert.DoesNotContain("Jonas Pike's hands", older);

        var moves = Client.TextOf(await client.Call("state_at", new JsonObject { ["object"] = "obj_letter", ["history"] = true }));
        Assert.StartsWith("HISTORY OF obj_letter", moves);
        Assert.Contains("the office safe  (L6; by Daniel and Sarah; E09a:", moves);
    }

    /// <summary>Over the protocol, the scan and the audit brief answer as the command line does, and write nothing.</summary>
    [Fact]
    public async Task The_scan_and_the_audit_brief_answer_as_the_command_line_does()
    {
        await using var client = await Connect();
        var before = Directory.GetFiles(harbour.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.Combine(harbour.Root, ".nse"))).Order().ToList();

        var scan = Client.TextOf(await client.Call("scan_draft", new JsonObject { ["scene"] = "sc_06" }));
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["scan", "--scene", "sc_06", "--dir", harbour.Root], output, new StringWriter()));
        Assert.Equal(output.ToString().TrimEnd(), scan.TrimEnd());
        Assert.Contains("No tripwire matched. That is not a clearance", scan);

        // A draft given as text: the name on the envelope a scene early.
        var draft = Client.TextOf(await client.Call("scan_draft", new JsonObject
        {
            ["scene"] = "sc_06", ["text"] = "Sarah looked at the tide.\n\nShe thought of the envelope, and the name on it: Jonas Pike.",
        }));
        Assert.Contains("line 3, paragraph 2: \"Jonas\" matches EM3 (P09, held back until sc_07)", draft);

        var brief = Client.TextOf(await client.Call("audit_brief", new JsonObject { ["scene"] = "sc_06" }));
        output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["audit", "--scene", "sc_06", "--dir", harbour.Root], output, new StringWriter()));
        Assert.Equal(output.ToString().TrimEnd(), brief.TrimEnd());
        Assert.StartsWith("AUDIT OF SCENE 6, sc_06", brief);

        var complete = Client.TextOf(await client.Call("audit_brief", new JsonObject { ["scene"] = "sc_06", ["completeness"] = true }));
        Assert.Contains("ALREADY RECORDED AS HAPPENING IN THIS SCENE", complete);

        var unknown = await client.Call("scan_draft", new JsonObject { ["scene"] = "sc_99" });
        Assert.True(unknown["isError"]!.GetValue<bool>());

        // Reading opens no task and writes no file.
        var after = Directory.GetFiles(harbour.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.Combine(harbour.Root, ".nse"))).Order().ToList();
        Assert.Equal(before, after);
    }

    /// <summary>The one tool that writes, end to end over the protocol: context, draft, submission, and a stale one refused.</summary>
    [Fact]
    public async Task A_scene_is_submitted_with_the_manifest_its_context_gave()
    {
        using var tiny = new TinyStory();
        await using var client = new Client(tiny.Root);
        await client.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "nse-tests", ["version"] = "0" },
        });
        await client.Notify("notifications/initialized");

        var context = Client.TextOf(await client.Call("scene_context", new JsonObject { ["scene"] = "s2" }));
        var manifest = System.Text.RegularExpressions.Regex.Match(context, @"Manifest (m_\d+_[0-9a-f]{6}_[0-9a-f]{8})").Groups[1].Value;
        Assert.Equal("m_1_", manifest[..4]);
        Assert.Contains("Give this manifest id back when you submit the scene", context);

        JsonArray Records(string id) => [new JsonObject
        {
            ["kind"] = "event", ["id"] = id, ["scene"] = "s2", ["story_key"] = 20.1, ["summary"] = "Ben finds the key under the mat.",
            ["participants"] = new JsonObject { ["ben"] = "actor" }, ["quote"] = "Ben found the key under the mat",
        }];
        var applied = await client.Call("submit_scene", new JsonObject { ["scene"] = "s2", ["manifest"] = manifest, ["records"] = Records("e_find"), ["note"] = "first draft" });
        Assert.NotEqual(true, applied["isError"]?.GetValue<bool>());
        Assert.StartsWith("Applied as 000002-cs_", Client.TextOf(applied));
        Assert.Contains("NO_CONTRACT", Client.TextOf(applied));
        Assert.Equal("first draft", tiny.Story.ReadLedger()[^1].ChangeSet.Note);

        // The author changes something the context showed. The old manifest no longer stands.
        Assert.True(tiny.Put(new Nse.Model.Secret { Id = "se1", Props = ["p_hid"], KnownOnlyTo = ["ann"] }).Applied);
        var stale = await client.Call("submit_scene", new JsonObject { ["scene"] = "s2", ["manifest"] = manifest, ["records"] = Records("e_again") });
        Assert.True(stale["isError"]!.GetValue<bool>());
        Assert.Contains("STALE_CONTEXT", Client.TextOf(stale));
        Assert.Contains("never learns it: se1 keeps it to Ann", Client.TextOf(stale));

        // A record the ledger cannot read, and one that is the author's to set, are refused with the reason.
        var shape = await client.Call("submit_scene", new JsonObject { ["scene"] = "s2", ["manifest"] = manifest, ["records"] = new JsonArray(new JsonObject { ["id"] = "x" }) });
        Assert.True(shape["isError"]!.GetValue<bool>());
        Assert.Contains("Record 1 of 1, id \"x\" is not in the shape the ledger uses", Client.TextOf(shape));
        var authors = await client.Call("submit_scene", new JsonObject
        {
            ["scene"] = "s2", ["manifest"] = manifest,
            ["records"] = new JsonArray(new JsonObject { ["kind"] = "embargo", ["id"] = "em", ["prop"] = "p_hid", ["until_scene"] = "s2" }),
        });
        Assert.Contains("PROPOSAL_ONLY em", Client.TextOf(authors));
        Assert.Equal(3, tiny.Story.ReadLedger().Count);
    }

    /// <summary>
    /// The server's share of keeping decisions with the author. Claude Code shows its approval prompt on every call of a tool
    /// whose listing carries this mark, in every permission mode and whatever allow rule matches, and denies
    /// the call where nobody can answer. So the test is that the mark is there, exactly as documented, on the
    /// one tool that decides, and that no other tool is a way to a decision.
    /// </summary>
    [Fact]
    public async Task Only_ratify_decides_and_its_listing_demands_the_user()
    {
        // Unless it is started to offer the tool, the server has none that decides, and calling it by name gets nothing done.
        using var tiny = new TinyStory();
        await using (var plain = await ConnectTo(tiny.Root))
        {
            var listed = (await plain.Request("tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
            Assert.DoesNotContain("ratify", listed);
            var called = await plain.Request("tools/call", new JsonObject { ["name"] = "ratify", ["arguments"] = new JsonObject { ["accept"] = new JsonArray("b_ann") } });
            Assert.True(called["error"] is not null || called["result"]!["isError"]!.GetValue<bool>());
            Assert.Single(tiny.Story.ReadLedger());
            Assert.Contains("you have no tool that does", Client.TextOf(await plain.Call("review_queue", new JsonObject())));
        }

        await using var client = await ConnectTo(harbour.Root, ratifyTool: true);
        var tools = (await client.Request("tools/list"))["result"]!["tools"]!.AsArray();
        Assert.Equal(["audit_brief", "lint", "propose_changes", "pulse", "ratify", "recap", "review_queue", "ripple", "scan_draft", "scene_context", "state_at", "story_brief", "submit_scene", "task_note", "what_if"],
            tools.Select(t => t!["name"]!.GetValue<string>()).Order());
        var ratify = tools.Single(t => t!["name"]!.GetValue<string>() == "ratify")!;
        Assert.False(ratify["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        var mark = ratify["_meta"]!["anthropic/requiresUserInteraction"]!;
        Assert.Equal(JsonValueKind.True, mark.GetValueKind());
        Assert.True(ratify["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.Equal(["accept", "note", "reject"], ratify["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order());

        foreach (var tool in tools.Where(t => t!["name"]!.GetValue<string>() != "ratify"))
        {
            // No other tool is given a change set to apply as it stands, an approval, or a decision.
            var takes = tool!["inputSchema"]!["properties"]?.AsObject().Select(p => p.Key).ToList() ?? [];
            Assert.DoesNotContain("ops", takes);
            Assert.DoesNotContain("approval", takes);
            Assert.DoesNotContain("accept", takes);
            Assert.DoesNotContain("reject", takes);
        }
        Assert.Equal(["note", "records", "retires"], tools.Single(t => t!["name"]!.GetValue<string>() == "propose_changes")!["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["note"], tools.Single(t => t!["name"]!.GetValue<string>() == "propose_changes")!["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task A_change_is_proposed_shown_and_decided_over_the_protocol()
    {
        using var tiny = new TinyStory();
        Assert.True(tiny.Apply(new Nse.Ledger.RatifyOp { Ids = ["b_ann"] }).Applied);
        await using var client = await ConnectTo(tiny.Root, ratifyTool: true);
        bool InForce(string id) => tiny.Story.State().Model.ById.ContainsKey(id);
        JsonObject Embargo(string id) => new() { ["kind"] = "embargo", ["id"] = id, ["prop"] = "p_hid", ["until_scene"] = "s2" };

        // The agent wants a record the author ratified gone, and an embargo set. It can only ask.
        var held = await client.Call("propose_changes", new JsonObject
        {
            ["note"] = "Ann did not know what she had done.",
            ["retires"] = new JsonArray(new JsonObject { ["id"] = "b_ann", ["reason"] = "retcon" }),
            ["records"] = new JsonArray(Embargo("em_hid")),
        });
        Assert.NotEqual(true, held["isError"]?.GetValue<bool>());
        var id = System.Text.RegularExpressions.Regex.Match(Client.TextOf(held), @"^Held as proposal (pr_[0-9a-f]{16}): 2 operation\(s\)").Groups[1].Value;
        Assert.NotEqual("", id);
        Assert.Contains("Nothing was applied", Client.TextOf(held));
        Assert.Equal(2, tiny.Story.ReadLedger().Count);
        Assert.True(InForce("b_ann"));
        Assert.False(InForce("em_hid"));

        Assert.Contains("1 proposal(s) held for the author's decision", Client.TextOf(await client.Call("story_brief", new JsonObject())));
        var queue = Client.TextOf(await client.Call("review_queue", new JsonObject()));
        Assert.Contains($"  {id} · proposed by agent at ledger head 2 · would apply as things stand", queue);
        Assert.Contains("it retires b_ann, which the author ratified; it sets an embargo (em_hid)", queue);
        Assert.Contains("Only the author decides. Show them what is here and ask.", queue);
        Assert.Contains("call ratify with exactly those ids", queue);
        Assert.Contains("event at 10.1 in s1", queue);
        var alone = Client.TextOf(await client.Call("review_queue", new JsonObject { ["only"] = id }));
        Assert.StartsWith($"REVIEW QUEUE · ledger head 2 · {id} alone\n", alone);
        Assert.DoesNotContain("event at 10.1 in s1", alone);
        var nothing = await client.Call("review_queue", new JsonObject { ["only"] = "b_ann" });
        Assert.True(nothing["isError"]!.GetValue<bool>());
        Assert.Contains("Nothing in the review queue has the id b_ann", Client.TextOf(nothing));

        // What these tools have no way to say.
        var approval = Embargo("em2");
        approval["approval"] = "ratified";
        var decided = await client.Call("propose_changes", new JsonObject { ["note"] = "agreed already", ["records"] = new JsonArray(approval) });
        Assert.True(decided["isError"]!.GetValue<bool>());
        Assert.Contains("Record 1 of 1, id \"em2\" is not in the shape the ledger uses", Client.TextOf(decided));
        var smuggled = await client.Call("propose_changes", new JsonObject
        {
            ["note"] = "these are fine", ["retires"] = new JsonArray(new JsonObject { ["id"] = "b_ann", ["reason"] = "retcon", ["op"] = "ratify" }),
        });
        Assert.True(smuggled["isError"]!.GetValue<bool>());
        Assert.Contains("Retire 1 of 1 is not in shape", Client.TextOf(smuggled));
        var noWhy = await client.Call("propose_changes", new JsonObject { ["note"] = " ", ["records"] = new JsonArray(Embargo("em3")) });
        Assert.True(noWhy["isError"]!.GetValue<bool>());
        Assert.Contains("REASON_REQUIRED", Client.TextOf(noWhy));
        Assert.Equal(2, tiny.Story.ReadLedger().Count);
        Assert.Single(tiny.Story.Review().Proposals);

        // The decision. In a session the harness lets this call through only on the author's approval.
        var refused = await client.Call("ratify", new JsonObject { ["accept"] = new JsonArray(id, "b_nobody") });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Refused. Nothing was decided", Client.TextOf(refused));
        Assert.Contains("UNKNOWN_REFERENCE b_nobody", Client.TextOf(refused));
        Assert.Equal(2, tiny.Story.ReadLedger().Count);

        var done = await client.Call("ratify", new JsonObject { ["accept"] = new JsonArray(id), ["reject"] = new JsonArray("a_deny"), ["note"] = "yes" });
        Assert.NotEqual(true, done["isError"]?.GetValue<bool>());
        Assert.Contains("0 record(s) ratified and 1 rejected, as 000003-cs_", Client.TextOf(done));
        Assert.Contains($"Proposal {id} accepted and applied as 000004-cs_", Client.TextOf(done));
        var last = tiny.Story.ReadLedger()[^1].ChangeSet;
        // The embargo was made by the agent and ratified by the author, and the ledger says both.
        Assert.Equal("agent", last.Ops.OfType<Nse.Ledger.PutOp>().Single().Record.Origin);
        Assert.Equal(Nse.Model.Approval.Ratified, last.Ops.OfType<Nse.Ledger.PutOp>().Single().Approval);
        Assert.Equal("author, by the ratify tool", last.Actor);
        Assert.Equal("yes", last.Note);
        Assert.Equal("agent", last.Proposal!.Actor);
        Assert.False(InForce("b_ann"));
        Assert.True(InForce("em_hid"));
        Assert.False(InForce("a_deny"));
        Assert.DoesNotContain("Proposals held", Client.TextOf(await client.Call("review_queue", new JsonObject())));
    }

    /// <summary>One session's work is there for the next, with nothing passed between them but the story folder.</summary>
    [Fact]
    public async Task A_session_leaves_its_work_for_the_next_over_the_protocol()
    {
        using var tiny = new TinyStory();
        string manifest;
        await using (var first = await ConnectTo(tiny.Root))
        {
            var context = Client.TextOf(await first.Call("scene_context", new JsonObject { ["scene"] = "s2" }));
            manifest = System.Text.RegularExpressions.Regex.Match(context, @"Manifest (m_\d+_[0-9a-f]{6}_[0-9a-f]{8})").Groups[1].Value;
            Assert.NotEqual("", manifest);
            // The context ends by saying what to note before any prose is written.
            Assert.EndsWith(Story.NoteFirst, context);
            var noted = await first.Call("task_note", new JsonObject
            {
                ["scene"] = "s2", ["decided"] = new JsonArray("Ben keeps the key."), ["open"] = new JsonArray("Who sees him?"), ["next"] = "Write the ending.",
            });
            Assert.NotEqual(true, noted["isError"]?.GetValue<bool>());
            Assert.StartsWith("Noted in task tk_s2_1 for s2: 1 decision(s) added, 1 question(s) now open, the next step set.", Client.TextOf(noted));

            // A context for an older head is a look at the past, and a note needs a task.
            var past = Client.TextOf(await first.Call("scene_context", new JsonObject { ["scene"] = "s1", ["head"] = 1 }));
            Assert.StartsWith("CONTEXT FOR SCENE 1, s1", past);
            Assert.DoesNotContain("task_note", past);
            // So is one that is asked for as a look: a session answering a question opens no task by reading.
            var looked = Client.TextOf(await first.Call("scene_context", new JsonObject { ["scene"] = "s1", ["look"] = true }));
            Assert.StartsWith("CONTEXT FOR SCENE 1, s1", looked);
            Assert.DoesNotContain("task_note", looked);
            Assert.Null(Nse.Tasks.TaskStore.Open(tiny.Root, "s1"));
            var none = await first.Call("task_note", new JsonObject { ["scene"] = "s1", ["next"] = "x" });
            Assert.True(none["isError"]!.GetValue<bool>());
            Assert.Contains("NO_TASK", Client.TextOf(none));
            // Whether more is to come is for a note made after a submission, and reaches the engine as given.
            var early = await first.Call("task_note", new JsonObject { ["scene"] = "s2", ["next"] = "x", ["more"] = true });
            Assert.True(early["isError"]!.GetValue<bool>());
            Assert.Contains("nothing has been submitted in this task yet", Client.TextOf(early));
            var twoLines = await first.Call("task_note", new JsonObject { ["scene"] = "s2", ["next"] = "one\ntwo" });
            Assert.True(twoLines["isError"]!.GetValue<bool>());
            Assert.Contains("BAD_VALUE", Client.TextOf(twoLines));
        }

        // The first session is gone. A second one starts, and all it does is ask for the brief.
        await using var second = await ConnectTo(tiny.Root);
        var brief = Client.TextOf(await second.Call("story_brief", new JsonObject()));
        Assert.Contains("Work in progress (1).", brief);
        Assert.Contains("  s2 \"Two\" · POV Ben (ben) · task tk_s2_1, opened by agent\n", brief);
        Assert.Contains($"    context: manifest {manifest}, taken at ledger head 1. The ledger has not moved since.\n", brief);
        Assert.Contains("    decided so far (1):\n      1. Ben keeps the key.\n", brief);
        Assert.Contains("    open questions (1):\n      1. Who sees him?\n", brief);
        Assert.Contains("    next: Write the ending.\n", brief);
        Assert.Null(Nse.Tasks.TaskStore.Open(tiny.Root, "s1"));
        // A brief as of an older head is the past, and leaves the work out.
        Assert.DoesNotContain("Work in progress", Client.TextOf(await second.Call("story_brief", new JsonObject { ["head"] = 1 })));
        // One task can be asked for by its scene, for when the brief names it without its notes.
        var one = Client.TextOf(await second.Call("story_brief", new JsonObject { ["task"] = "s2" }));
        Assert.Contains("\nThe task open for s2. What stands under decided", one);
        Assert.Contains("    next: Write the ending.\n", one);
        Assert.Contains("\nNo task is open for s1.\n", Client.TextOf(await second.Call("story_brief", new JsonObject { ["task"] = "s1" })));
        var both = await second.Call("story_brief", new JsonObject { ["task"] = "s2", ["head"] = 1 });
        Assert.True(both["isError"]!.GetValue<bool>());
        Assert.Contains("cannot go with head", Client.TextOf(both));

        var tool = (await second.Request("tools/list"))["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "task_note")!;
        Assert.Equal(["close", "decided", "draft", "more", "next", "open", "scene"], tool["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["scene"], tool["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains("Call this before you write or change any prose, with your plan, and again each time you decide something more", tool["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bad_question_comes_back_as_an_error_that_says_what_was_wrong()
    {
        await using var client = await Connect();
        var unknown = await client.Call("state_at", new JsonObject { ["scene"] = "sc_99" });
        Assert.True(unknown["isError"]!.GetValue<bool>());
        Assert.Contains("sc_99", Client.TextOf(unknown));

        var both = await client.Call("state_at", new JsonObject { ["scene"] = "sc_07", ["end"] = true });
        Assert.True(both["isError"]!.GetValue<bool>());
        Assert.Contains("exactly one point", Client.TextOf(both));

        var twoThings = await client.Call("state_at", new JsonObject { ["prop"] = "P07", ["object"] = "obj_letter", ["history"] = true });
        Assert.True(twoThings["isError"]!.GetValue<bool>());
        Assert.Contains("one fact or one object", Client.TextOf(twoThings));

        var tight = await client.Call("scene_context", new JsonObject { ["scene"] = "sc_09", ["budget"] = 200 });
        Assert.True(tight["isError"]!.GetValue<bool>());
        Assert.Contains("BUDGET_INSUFFICIENT", Client.TextOf(tight));

        // The server is still serving after the errors.
        Assert.StartsWith("STORY BRIEF", Client.TextOf(await client.Call("story_brief", new JsonObject())));
    }
}
