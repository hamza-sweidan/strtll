using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Cli;

/// <summary>
/// The second front end, and the one an agent uses: the story served over the Model Context Protocol on
/// standard input and output. One process per client session; no port, no background work. What an agent
/// can do to the ledger is exactly what these tools let it do: record what a scene does, as provisional;
/// ask the author for anything else; and, only if the server was started to offer it, carry out the author's
/// decision through a tool the harness will not run without the author's approval.
/// </summary>
public static class McpHost
{
    /// <summary>What the client is told about how to use the server. Kept under 4,096 characters either way.</summary>
    /// <remarks>
    /// The text is a raw string in this file, so it has whatever line ends the checkout gave the file. A Windows
    /// checkout with git's autocrlf on gives it CRLF. The client is sent the same text either way: LF only.
    /// </remarks>
    public static string Instructions(bool ratifyTool) => Text.Replace("\r\n", "\n").Replace("{DECIDING}", ratifyTool
        ? "When the author has said what to\n    accept and what to reject, call ratify with exactly those ids: it shows them an approval\n    prompt. Never call it on your own judgment."
        : "Show it to the author. They decide in\n    their own terminal, with nse ratify or nse reject. You have no tool that decides.");

    private const string Text = """
        This server holds the recorded state of one story: what each character believes, what the reader has
        been told, where tracked objects are, and what must not be given away yet.

        At the start of a session:
        1. Call story_brief. It lists the scenes in the order they are told, and any work in progress that
           an earlier session left. Pick that up before starting anything new.

        Reading:
        2. For one narrow question (one person, one fact, the reader, an object at one point) call state_at
           with holder, prop, reader or object. Holder and prop together also show the passage behind
           what they hold. For "when did she learn it", "who told him" or "when is the reader told", add
           history with prop; for an object's moves, history with object.
        3. "On entry to a scene" means just before it begins: in story time for people and objects, in
           reading order for the reader. A flashback is late in the telling and early in story time.
        4. "no record" means nothing has been recorded either way. It does not mean the person does not
           know. A context lists only what is recorded: a fact not under someone's name is one they have
           no record on. Labels in square brackets (knows, FALSE BELIEF, unaware, heard it said) are
           computed: trust them over your reading of the prose. PROVISIONAL (a * in a context) is not
           yet reviewed by the author, and UNCONFIRMED rests on such a record: write on from both.
           IN QUESTION: what the record rested on is gone, so build nothing on it. Use ids as given.

        Drafting or revising a scene:
        5. Call scene_context for the scene and follow it: what must not be stated to the reader, what
           each cast member lacks or has wrong on entry, what they believe. A scene may give someone a
           fact they lacked, planned or not; a hint to the reader is not a statement. This opens a task.
        6. Before you write or change any prose, call task_note with your plan: what you have decided,
           the questions still open, the next step. Call it again each time you decide something more,
           and say how far the draft has got. A session can end at any moment without warning, and a
           later one has nothing but these notes to go on.
        7. When the text is in its manuscript file, call submit_scene with the scene id, the manifest id
           from the end of the context, and the records the text supports: events, what was said, what
           each person came to believe and how, what the reader was given, where things moved. Each names
           the scene, is dated within the span the context gives, involves only the cast, and quotes the
           text word for word. Declare everything of weight, planned or not. Under withdraws, name any
           record the context lists for this scene that your draft no longer supports. A fact is one
           detail: two details are separate propositions when someone could learn them at different times.
        8. Refused means nothing was written, and each finding says what to change. STALE_CONTEXT means
           the state moved under your draft: take the context again and check the draft against the lines
           listed.
        9. Applied means provisional. Tell the author what you recorded and every note "for the author
           to settle". An audit (audit_brief, read by a fresh reviewer) is theirs to ask for: do not
           start one unasked.
           The scene's task is put away once they have decided. A task_note made after you
           submit must say whether more of the scene is still to be written: more true or false.

        What is the author's:
        10. Rules, embargoes, secrets, contracts, casts, story times, retcons, anything dated outside your
            scene, and retiring what the author ratified: call propose_changes with the records, what to
            retire, and why. It is held. Nothing changes until they accept it, so do not work as though
            they had.
        11. Only the author decides. review_queue lists what waits. {DECIDING}

        Do not edit files under ledger/, and do not run nse put, nse ratify or nse reject: those are the
        author's own.
        """;

    /// <summary>Serve until the client closes the streams. With no streams given, uses standard input and output.</summary>
    /// <param name="ratifyTool">
    /// Offer the tool that decides. It is marked as needing the user, and a client that honours the mark asks
    /// the author before every call. A client that does not would run it unasked, and the server cannot tell
    /// the two apart. So it is left out unless whoever starts the server says the client is one that asks.
    /// </param>
    public static async Task<int> RunAsync(
        string dir, Stream? input = null, Stream? output = null, CancellationToken cancel = default, bool ratifyTool = false)
    {
        var story = Story.Open(dir);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        // Standard output carries the protocol, so nothing else may write to it.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(story);
        builder.Services.AddSingleton(new ServerOptions(ratifyTool));
        var server = builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation { Name = "nse", Title = "Narrative State Engine", Version = "0.1.0" };
            o.ServerInstructions = Instructions(ratifyTool);
        });
        if (input is not null && output is not null)
            server.WithStreamServerTransport(input, output);
        else
            server.WithStdioServerTransport();
        server.WithTools<NseTools>();
        if (ratifyTool)
            server.WithTools<DecisionTools>();
        using var host = builder.Build();
        await host.RunAsync(cancel);
        return 0;
    }
}

/// <summary>The tools. Each call reads the story afresh, so an answer always reflects the files as they are.</summary>
/// <remarks>
/// There is no tool here that puts a change set as given, and none that takes an approval: an agent's records
/// are provisional or held, and the only way to anything else is <see cref="DecisionTools.Ratify"/>.
/// </remarks>
[McpServerToolType]
public sealed class NseTools(Story story, ServerOptions options)
{
    [McpServerTool(Name = "story_brief", Title = "Story brief", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The map of the story: scenes in the order they are told with their ids, points of view and story times, any work in progress that an earlier session left, how much state is recorded, what awaits review, and standing rules. Call once at the start of a session, and again after your context has been cut down.")]
    public string StoryBrief(
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null,
        [Description("A scene id, to show that scene's open task in full. Use it when the brief names a task without its notes.")] string? task = null) =>
        Guarded(() => task is not null && head is not null
            ? throw new ArgumentException("task shows work as it stands now, and cannot go with head")
            : ContextCompiler.Brief(story.State(head), story.HeldProposals(), head is null ? TaskReport.Text(story.Work(), task) : ""));

    // Not marked read-only: taken to work from, it writes the scene's task under work/tasks. It changes nothing
    // of the story, and asked twice with nothing moved it writes once.
    [McpServerTool(Name = "scene_context", Title = "Context for one scene", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Everything needed to draft or revise one scene, as the state on entry to it: what must not be stated to the reader, with the hints already given (a hint is allowed); open questions that must stay open; what is so that nobody present has any record on; where each cast member is shown not to know, believes the opposite, or was not there for what the teller last came by; what each believes, and what each has only heard said; what the reader has been told; where tracked objects are; and which manuscript passages to read. Silence is not listed: a fact that is not under a person's name is one they have no record on, and state_at with holder and prop shows any such pair. All of it is the state on entry: a scene may give someone a fact they lacked, planned or not. Taking it opens a task for the scene, or adds to the one already open, so that a later session can see work had begun and from what; its last lines say what to note before you write. If you only want to read it, to answer a question, set look. Fails with BUDGET_INSUFFICIENT rather than cutting anything that must be there.")]
    public string SceneContext(
        [Description("Scene id, for example sc_07. story_brief lists them.")] string scene,
        [Description("Size limit in rough tokens. Default 10000.")] int? budget = null,
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null,
        [Description("True to read the context without opening a task: you are answering a question and will not draft or revise from it.")] bool look = false,
        [Description("The start of one part's title, to read that part by itself and in full. This is how a part the context lists as omitted is read. It opens no task.")] string? part = null) =>
        // A context for the story as it stands is taken to work from, and the scene's task notes that it was.
        // One for an older head is a look at the past, and one asked for as a look is a look: they open nothing.
        Guarded(() => part is not null ? ContextCompiler.PartText(story.State(head), scene, part)
            : head is null && !look
            ? story.TakeContext(scene, budget ?? ContextCompiler.DefaultBudget).Text
            : ContextCompiler.Compile(story.State(head), scene, budget ?? ContextCompiler.DefaultBudget).Text);

    [McpServerTool(Name = "state_at", Title = "State at one point", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A focused question about one point in the story: on entry to a scene, or at the end. Narrow it to one person (holder), one fact (prop), the reader, or one object. With holder and prop together the answer shows the passage behind what that person has on that fact, the words quoted and the line they stand on, and when they have nothing, what they come by later. With no narrowing it returns everyone's beliefs, the reader's state and where things are. Give exactly one of scene or end, unless history is true.")]
    public string StateAt(
        [Description("Scene id: the state on entry to this scene.")] string? scene = null,
        [Description("True for the state at the end of the story. Do not combine with scene.")] bool end = false,
        [Description("Entity id of one person, for example ch_daniel. A persona is answered as the person behind it.")] string? holder = null,
        [Description("Proposition id, for example P12, to show only that fact.")] string? prop = null,
        [Description("Entity id of a tracked object, for example obj_letter, to show only where it is kept.")] string? @object = null,
        [Description("True to show only what the reader has been given.")] bool reader = false,
        [Description("True, with prop, for the whole history of that fact instead of one point: every change for each person in story order with how and from whom they learned it, and every time the reader was given it. Use this for 'when did X first learn P' and 'who told whom'. True, with object instead of prop, for every move of that object and what moved it. Needs no scene.")] bool history = false,
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() => StateReport.Text(story.State(head), new StateQuery(scene, end, holder, prop, @object, reader, history)));

    [McpServerTool(Name = "recap", Title = "The story as one person knows it", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The story as one person knows it, in story-time order up to a point: what they took part in or saw, what was said to them, by them or in their hearing, what they read, and what they came to believe. In their own terms by default, with no word on what is really so: material for writing in their voice, or for a scene where they look back. Give exactly one of at or end.")]
    public string RecapOf(
        [Description("Entity id of the person, for example ch_sarah. A persona is answered as the person behind it.")] string holder,
        [Description("Scene id: everything before entry to this scene, in story time.")] string? at = null,
        [Description("True for everything up to the end of the story. Do not combine with at.")] bool end = false,
        [Description("True to add the author's labels: whether each belief was right, and whether each statement was sincere.")] bool truth = false,
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() => (at is null) == !end
            ? throw new ArgumentException("give exactly one of at or end")
            : Recap.Text(story.State(head), holder, at, truth));

    [McpServerTool(Name = "ripple", Title = "What a change would disturb", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The weave: the story as a signal graph in which every record is a node and every reference a pair of weighted strands. Give a record id and get everything a change to it would disturb, strongest first, each with the path the disturbance arrives by and how many sides it arrives from. Deterministic. Use it before revising a scene or proposing a retcon, to see what else to check.")]
    public string RippleOf(
        [Description("Id of any live record: a proposition (P10), an event, a belief change, a scene, a person.")] string id,
        [Description("How many records to list. Default 40.")] int? top = null,
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() =>
        {
            var weave = Weave.Build(story.State(head));
            return Weave.RippleText(weave, id, weave.Ripple(id), top ?? 40);
        });

    [McpServerTool(Name = "pulse", Title = "Tension scene by scene", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The tension field: false and outdated beliefs, embargoes, secrets and never-settle rules, injected at the facts they concern and spread through the weave to every scene, with the sources that charge each scene most. Deterministic.")]
    public string PulseOf(
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() =>
        {
            var state = story.State(head);
            return Weave.PulseText(Weave.Build(state).Pulse(state), state.Model.Head);
        });

    [McpServerTool(Name = "lint", Title = "Suggestions for the author", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Suggestions, never findings: each embargoed reveal with the earlier disclosures that prepare the reader for it, or NO SEED; people who were at an event where someone else learned a fact, or were told a fact, and have no record on it; a person who learns the same thing twice, and a statement that tells nobody anything new, each of which is how a fact that joins two details shows; propositions that name nobody they are about. Show them to the author when asked what the records may be missing. Do not record a belief because a lint suggests one: ask.")]
    public string LintOf(
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() => Lint.Text(story.State(head)));

    [McpServerTool(Name = "attention", Title = "What no longer stands as recorded", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The records that no longer stand as they were recorded, each with the passage it quotes and the reason: a statement whose speaker has lost what they had the fact from, a belief whose source is in doubt, an event that needs a fact which is no longer so, someone acting after a fact that ends their part in the story. With them, what in the same scenes is still borne out and by what, and anything else on record that a statement in doubt might rest on, marked as a suggestion. It comes of the author rejecting a record, of a scene being rewritten, of a fact being moved. Worked out from the records each time; nobody links a later passage to an earlier one. Show it to the author after a decision or a change, and read it for a scene before you revise that scene. It decides nothing.")]
    public string Attention(
        [Description("A scene id, to list only that scene's records. Leave out for the whole story.")] string? scene = null,
        [Description("Answer as of an older ledger head. Leave out for the current state.")] int? head = null) =>
        Guarded(() => Standing.Text(story.State(head), scene));

    [McpServerTool(Name = "scan_draft", Title = "Scan a draft for tripwires", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Look through a draft of one scene for the tripwires of every embargo in force in it: patterns of words the author expects a passage giving a withheld fact away to use. With no text, the scene is scanned as it stands in the manuscript. A hit is a passage to read again, not a verdict: a hint the reader may miss is allowed. No hit is not a clearance either.")]
    public string ScanDraft(
        [Description("Scene id, for example sc_07.")] string scene,
        [Description("A draft to scan instead of the manuscript's text: the scene's prose, or a whole manuscript file from which the scene's part is taken. Leave out to scan the scene as it stands.")] string? text = null) =>
        Guarded(() => DraftScan.Text(story.Scan(scene, text: text)));

    [McpServerTool(Name = "audit_brief", Title = "Brief for auditing one scene", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What an auditor of one scene needs and nothing more: where its text is, what it must not give away to the reader, what each person present does not know, the passages the word scan points at, and the exact form of the report. Give it to a reviewer with a fresh context when the author asks for an audit of a scene. That is another model session, so it is never a routine step after a submission. With completeness true it asks instead what the scene does of weight that no record says.")]
    public string AuditBrief(
        [Description("Scene id, for example sc_07.")] string scene,
        [Description("True to check the records against the text, taking the text as right, instead of checking the text for leaks.")] bool completeness = false) =>
        Guarded(() => story.AuditBrief(scene, completeness ? AuditMode.Completeness : AuditMode.Leaks));

    [McpServerTool(Name = "submit_scene", Title = "Submit a drafted or revised scene", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Record what a scene you drafted or revised does. Give the scene id, the manifest id from the end of the scene_context you worked from, and the records the scene's text supports. Every record must belong to this scene and quote its text word for word. The engine checks everything it can check, refuses with findings that say what to fix, and otherwise applies the records as provisional and compares the scene with its contract. It never ratifies.")]
    public string SubmitScene(
        [Description("Scene id, for example sc_07.")] string scene,
        [Description("The manifest id printed at the end of the scene_context this draft was written from, for example m_16_a1b2c3_0123abcd.")] string manifest,
        [Description("The records, each an object with a \"kind\": event, assertion, belief_change, disclosure, placement, fact_change, proposition or entity. An event that could not happen without some fact lists it under \"needs\", as proposition ids: an entry that takes a key needs the fact that he has one. Use the field names and values the ledger uses, for example {\"kind\":\"belief_change\",\"id\":\"Bd40\",\"holder\":\"ch_daniel\",\"prop\":\"P07\",\"stance\":\"holds_true\",\"strength\":\"suspects\",\"story_key\":40.2,\"scene\":\"sc_07\",\"channel\":\"overheard\",\"via\":\"A31\",\"quote\":\"words from the scene\"}. Ids must be new.")] JsonElement[] records,
        [Description("One line on what this submission is: first draft, revision after the author's note, and so on.")] string? note = null,
        [Description("Ids of records listed in the scene_context under 'already recorded as happening in this scene' that this draft no longer supports. Only provisional records an earlier submission for this scene left can be withdrawn.")] string[]? withdraws = null) =>
        Guarded(() =>
        {
            var parsed = ParseRecords(records);
            var result = story.SubmitScene(new SceneReport { Scene = scene, Manifest = manifest, Note = note, Records = parsed, Withdraws = withdraws ?? [] });
            var text = SubmitReport.Text(result);
            // A refusal is an error to the caller: nothing was written, and the text says what to change.
            return result.Applied ? text : throw new McpException(text);
        });

    [McpServerTool(Name = "task_note", Title = "Note where the work on a scene stands", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add to the task open on a scene what only you know: what you decided, what is still open, what comes next, how far the draft has got. A task opens when you take the scene's context. Call this before you write or change any prose, with your plan, and again each time you decide something more: a session can end at any moment without warning. A session that starts later reads the notes in story_brief, and it has nothing else to go on.")]
    public string TaskNote(
        [Description("Scene id, for example sc_07.")] string scene,
        [Description("Decisions made since your last note, one line each. They are kept, in order.")] string[]? decided = null,
        [Description("The questions that are open now. This list takes the place of the last one: give an empty list when none are left, and leave it out to keep the list as it was.")] string[]? open = null,
        [Description("What to do next, said so that a session that knows nothing else could do it.")] string? next = null,
        [Description("Where the draft is and how far it has got.")] string? draft = null,
        [Description("Only in a note made after the scene was submitted, and required then: true if more of the scene is still to be written, false if not. With false the task is put away once the author has decided on what was submitted. With true it stays open.")] bool? more = null,
        [Description("Set only to close the task without submitting the scene, with the reason.")] string? close = null) =>
        Guarded(() =>
        {
            var note = new TaskNote { Scene = scene, Decided = decided ?? [], OpenQuestions = open, Next = next, Draft = draft, More = more, Close = close };
            var result = story.Note(note);
            var text = TaskReport.Noted(result, note);
            return result.Noted ? text : throw new McpException(text);
        });

    [McpServerTool(Name = "review_queue", Title = "What waits on the author", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What waits on the author's decision: each proposal held for them, with what it would do and why it is theirs to decide, and every provisional record not yet ratified, grouped by the change set that brought it. Show it to the author when they ask what is waiting, and before they decide. A long queue is given in brief, with counts; ask for one item at a time with only.")]
    public string ReviewQueue(
        [Description("The id of one held proposal (pr_...) or one change set (cs_...), to show that item alone and in full.")] string? only = null,
        [Description("True to give each provisional record as a sentence in plain words, after a paragraph on what its change set records. Use it when showing the queue to the author.")] bool plain = false) =>
        Guarded(() =>
        {
            var queue = story.Review();
            if (only is not null)
                queue = queue.Only(only) ?? throw new KeyNotFoundException($"Nothing in the review queue has the id {only}: no held proposal, and no change set with records still waiting.");
            return ReviewReport.ForAgent(queue, only, options.RatifyTool, plain ? story.State() : null);
        });

    [McpServerTool(Name = "propose_changes", Title = "Propose a change to the author", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ask the author for a change you may not make yourself: a rule, an embargo, a secret, a scene's contract, cast or story time, a correction, a retcon, a change dated outside the scene you drafted, or retiring a record the author ratified. Give the records to add, the records to retire, and why. It is checked as if it were applied and refused with findings if it could not be; otherwise it is held for the author. Nothing is applied and no answer about the story changes until the author accepts it.")]
    public string ProposeChanges(
        [Description("Why this is proposed, in a sentence or two. The author decides from it, and it stays on record with their decision.")] string note,
        [Description("Records to add, each an object with a \"kind\", in the shape submit_scene takes. Any kind may be proposed, also rule, embargo, secret, scene_contract, scene and scene_time. A proposition that ends someone's part in the story, a death or a departure for good, names them under \"ends\". Ids must be new. Do not set an approval.")] JsonElement[]? records = null,
        [Description("Records to retire, each {\"id\": \"the record's id\", \"reason\": \"retcon, correction, duplicate or withdrawn, and a few words\"}. To change a record, retire it and add its replacement under a new id.")] JsonElement[]? retires = null) =>
        Guarded(() =>
        {
            var result = story.Propose(new ChangeSetDraft { Actor = "agent", Note = note, Ops = ParseOps(records, retires) });
            var text = ReviewReport.Proposed(result);
            return result.Held ? text : throw new McpException(text);
        });

    [McpServerTool(Name = "what_if", Title = "What a change would do", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Work out, without writing anything, what a change to the story would do: whether it would pass the engine's checks, every answer about who knows what, what the reader has been given, what is so and where things are that would change, scene by scene; which passages already written it would leave in doubt, each with the file, the line and the reason, what in those scenes would still be borne out, and what it may do that the records cannot say, marked as a suggestion; and which scenes' drafting contexts would change. Use it before proposing a retcon or a correction, and to show the author its consequences. It takes the records and retirements propose_changes takes.")]
    public string WhatIf(
        [Description("Records to add, each an object with a \"kind\", in the shape propose_changes takes.")] JsonElement[]? records = null,
        [Description("Records to retire, each {\"id\": \"...\", \"reason\": \"...\"}.")] JsonElement[]? retires = null) =>
        Guarded(() => Nse.Query.WhatIf.Text(story.WhatIf(new ChangeSetDraft { Actor = "agent", Ops = ParseOps(records, retires) })));

    /// <summary>A change set's operations as a tool call gives them: retirements first, then records.</summary>
    private static List<Operation> ParseOps(JsonElement[]? records, JsonElement[]? retires)
    {
        var ops = new List<Operation>();
        var gone = retires ?? [];
        for (var i = 0; i < gone.Length; i++)
        {
            if (gone[i].ValueKind != JsonValueKind.Object
                || !gone[i].TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !gone[i].TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
                || gone[i].EnumerateObject().Count() != 2)
                throw new McpException($"Retire {i + 1} of {gone.Length} is not in shape. Each is {{\"id\": \"...\", \"reason\": \"...\"}} and nothing else.");
            ops.Add(new RetireOp { Id = id.GetString()!, Reason = reason.GetString()! });
        }
        ops.AddRange(ParseRecords(records ?? []).Select(r => new PutOp { Record = r }));
        return ops;
    }

    /// <summary>Records as a tool call gives them, read strictly, with an error that says which one is wrong.</summary>
    private static List<LedgerRecord> ParseRecords(JsonElement[] records)
    {
        var parsed = new List<LedgerRecord>();
        for (var i = 0; i < records.Length; i++)
        {
            try
            {
                parsed.Add(NseJson.Read<LedgerRecord>(records[i].GetRawText()));
            }
            catch (JsonException e)
            {
                // Say which one, by position and by id if it has one: the path in the message is inside the record.
                var id = records[i].ValueKind == JsonValueKind.Object && records[i].TryGetProperty("id", out var given) && given.ValueKind == JsonValueKind.String
                    ? $", id \"{given.GetString()}\"" : "";
                throw new McpException($"Record {i + 1} of {records.Length}{id} is not in the shape the ledger uses: {e.Message}");
            }
        }
        return parsed;
    }

    /// <summary>
    /// Turn the engine's own errors into messages the client is shown. Anything else is a fault and is left
    /// to the SDK, which reports it without details.
    /// </summary>
    internal static string Guarded(Func<string> answer)
    {
        try
        {
            return answer();
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or BudgetException or LedgerException
                                      or IOException or UnauthorizedAccessException)
        {
            // IOException covers a story folder that has gone, and a file another program has open mid-save.
            throw new McpException(e.Message);
        }
    }
}

/// <summary>What the server was started to offer.</summary>
public sealed record ServerOptions(bool RatifyTool);

/// <summary>The tool that decides, kept apart so that the server offers it only when started to.</summary>
[McpServerToolType]
public sealed class DecisionTools(Story story)
{
    /// <summary>
    /// The one tool that decides. It is marked as needing the user: the harness shows its approval prompt on
    /// every call, whatever permission mode or allow rule is in force, and denies the call where nobody can
    /// be asked. That mark is the whole of the gate on this side, so it is tested for.
    /// </summary>
    [McpServerTool(Name = "ratify", Title = "Carry out the author's decision", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("Carry out the author's decision on what waits in the review queue. Call it only when the author has said in this conversation exactly what to accept and what to reject, and pass exactly those ids. It shows the author an approval prompt, and nothing is decided unless they approve. All of it is done or none of it. Never call it on your own judgment.")]
    public string Ratify(
        [Description("Ids to accept. A record is ratified. A change set (cs_...) ratifies every record it brought that still awaits a decision, except flagged ones, which are accepted by naming them. A proposal (pr_...) is applied with its records ratified.")] string[]? accept = null,
        [Description("Ids to reject. A record, or every waiting record of a change set, is rejected: kept on record and no longer part of the story. A proposal is declined and kept.")] string[]? reject = null,
        [Description("The author's reason, in their words, if they gave one.")] string? note = null) =>
        NseTools.Guarded(() =>
        {
            // The ledger says how the decision arrived. It cannot say the prompt was shown: that is the harness's part.
            var result = story.Decide(new Decision { Accept = accept ?? [], Reject = reject ?? [], Actor = "author, by the ratify tool", Note = note });
            var text = ReviewReport.Decided(result);
            return result.Done ? text : throw new McpException(text);
        });
}
