using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Text;
using Nse.Validation;

namespace Nse.Cli;

/// <summary>
/// The command line. It parses arguments, calls the core library and prints; it holds no logic of its own.
/// Exit codes: 0 done, 1 the story has findings or the change was refused, 2 the command was not understood.
/// </summary>
public static class Cli
{
    private const string Usage = """
        nse - narrative state engine

          nse init [--dir DIR]                      create manuscript/ and ledger/ for a new story
          nse sync [--dir DIR]                      re-read the manuscript, rebuild the index, report what no longer lines up
          nse verify [--dir DIR]                    check the ledger's chain and everything sync checks
          nse put FILE [--dir DIR]                  check a change set and, if nothing is wrong, append it to the ledger.
                                                    The author's own command: it asks first, and runs only at a terminal
          nse submit-scene FILE [--dir DIR]         an agent's account of a scene it drafted or revised: the records the
                                                    text supports and the manifest id of the context it was written from.
                                                    Checked like put, and also that the state has not moved under the
                                                    draft; applied as provisional; compared with the scene's contract
          nse propose FILE [--dir DIR]              hold a change set for the author to decide on. It is checked as put
                                                    would check it and nothing is applied. For anything a scene
                                                    submission may not carry: rules, embargoes, contracts, retcons,
                                                    retiring a record the author ratified
          nse review [ID] [--brief] [--plain] [--dir DIR]
                                                    what waits on the author: held proposals, and provisional records.
                                                    With an ID, that one proposal or change set alone; with --brief,
                                                    counts in place of each record and operation; with --plain,
                                                    each record as a sentence in plain words
          nse ratify ID ... [--reject ID,ID] [--note TEXT] [--dir DIR]
          nse reject ID ... [--note TEXT] [--dir DIR]
                                                    the author's decision. An ID is a record, a change set (every record
                                                    it brought that still awaits a decision; ratifying leaves the flagged
                                                    ones to be named) or a proposal (accepted: applied with its records
                                                    ratified; rejected: declined and kept). All of it is done or none.
                                                    Shown for confirmation first; runs only at a terminal
          nse state-at (--scene ID | --end) [--holder ID] [--prop ID] [--reader] [--object ID]
                       [--head N] [--json] [--dir DIR]
                                                    the state on entry to a scene, or at the end of the story.
                                                    With --holder and --prop together: what that person has on
                                                    that fact, and the passage behind it
          nse state-at --prop ID --history [--holder ID] [--head N] [--dir DIR]
                                                    every change to one fact: who learned it when and how, and when the reader was told
          nse state-at --object ID --history [--head N] [--dir DIR]
                                                    every move of one object: where it was kept from when, and what moved it
          nse scene-context --scene ID [--budget N] [--head N] [--take | --no-task | --part TITLE] [--dir DIR]
                                                    the context for drafting or revising one scene. It lists
                                                    what is recorded and says the rule for silence once: a
                                                    fact that is not under a person's name is one they have
                                                    no record on. From an
                                                    agent's shell it is taken to work from: that opens a task
                                                    for the scene, or adds to the one open. At a terminal, or
                                                    with --head or --no-task, it is only a look; --take opens
                                                    the task from a terminal too
          nse task-note FILE [--dir DIR]            add to the task open on a scene what only whoever is working
                                                    knows: what was decided, what is open, what comes next,
                                                    how far the draft has got
          nse recap --holder ID (--at SCENE | --end) [--truth] [--head N] [--dir DIR]
                                                    the story as one person knows it, in story-time order: what they
                                                    took part in or saw, what was said to them or by them, what they
                                                    came to believe. In their own terms; --truth adds the labels
          nse ripple ID [--top N] [--head N] [--dir DIR]
                                                    the weave: what a change to one record would disturb, strongest
                                                    first, each with the path it arrives by
          nse pulse [--head N] [--dir DIR]          the tension field: false beliefs, embargoes, secrets and never-
                                                    settle rules spread through the weave, scene by scene
          nse weave (--around ID [--radius N] | --keystones N) [--head N] [--dir DIR]
                                                    the weave as a Mermaid picture around one record, or the records
                                                    whose change would move the most
          nse diagram VIEW [--holder ID] [--head N] [--dir DIR]
                                                    the story at a glance, as Markdown with Mermaid, generated and
                                                    never edited: knowledge (one person, scene by scene; needs
                                                    --holder), secrets, objects or divergence
          nse lint [--head N] [--dir DIR]           suggestions for the author, never findings: reveals with no
                                                    earlier hint, people who were there or were told and have no
                                                    record, people who learn a thing twice and statements that
                                                    tell nobody anything new (how a fact that joins two details
                                                    shows), propositions that name nobody they are about
          nse attention [--scene ID] [--head N] [--dir DIR]
                                                    what no longer stands as it was recorded: a statement whose
                                                    speaker has lost what they had it from, a belief whose source
                                                    is in doubt, an event that needs a fact no longer so, someone
                                                    acting after their part in the story has ended. Each with its
                                                    passage and the reason, and what in the same scenes is still
                                                    borne out. Worked out from the records; nothing is linked by hand
          nse what-if FILE [--dir DIR]              what a change set would do, worked out in memory: whether it
                                                    passes the checks, every answer that would change, scene by
                                                    scene, which passages already written it would leave in doubt
                                                    and which would still stand, and which drafting contexts would
                                                    change. Nothing is written. Exit 1 when it would be refused
          nse scan (--scene ID [--file PATH] | --all) [--dir DIR]
                                                    look for the tripwires of the embargoes in force in a scene: in
                                                    the scene as it stands, in a draft file, or in every scene. A hit
                                                    is a passage to read, not a verdict. Exit 1 when anything matched
          nse audit --scene ID [--completeness] [--dir DIR]
                                                    the brief for an auditor with a fresh context: where the scene's
                                                    text is, what it must not give away, the scan's hits, and the form
                                                    of the report. --completeness asks instead what the scene does of
                                                    weight that no record says. An audit is another model session:
                                                    it is run when the author asks for one, never as a routine step
          nse brief [--head N] [--task SCENE] [--dir DIR]
                                                    the map a session starts from, and any work in progress.
                                                    With --task, that scene's open task in full
          nse dump [--head N] [--dir DIR]           every point-in-time answer as one JSON document
          nse rebuild [--dir DIR]                   throw the index away and build it again; also accepts a ledger
                                                    that is shorter than this folder has seen it, which is asked
                                                    about first and done only at a terminal
          nse protocol [--with-ratify]              print how an agent is told to work with the engine: the text the
                                                    MCP server gives its client
          nse mcp [--with-ratify] [--dir DIR]       serve the story to an agent over MCP on stdio: story_brief,
                                                    scene_context, state_at, review_queue, submit_scene,
                                                    propose_changes and task_note. With --with-ratify also ratify, which carries
                                                    out the author's decision. Pass it only for a client that asks
                                                    the user before every call of a tool marked
                                                    anthropic/requiresUserInteraction, as Claude Code does
          nse setup-claude [--engine PATH] [--dotnet PATH] [--with-auditor] [--force] [--dir DIR]
                                                    write what Claude Code needs into the story folder: the MCP
                                                    server (.mcp.json), the hooks (.claude/settings.local.json),
                                                    the permission rules (.claude/settings.json), the scene-work
                                                    skill and CLAUDE.md. Files already there are kept without --force.
                                                    --with-auditor also writes the scene-auditor agent, for an author
                                                    who wants scenes audited by a fresh session
          nse hook EVENT [--dir DIR]                what Claude Code's hooks run: EVENT is pre-edit, post-edit, stop,
                                                    session-start or pre-compact. Reads the event's JSON on standard
                                                    input and answers as the harness expects. Never exits with 2

        DIR defaults to the current directory. --head N answers as of an older ledger head.

        Labels in square brackets are computed, never stored:
          knows, knows it is false       takes it the right way and is certain
          correctly believes, suspects   takes it the right way, less than certain
          correctly doubts               takes it as false, and it is false
          FALSE BELIEF                   takes it the wrong way
          OUTDATED BELIEF                takes as true what was true once and no longer is
          unaware, undecided             recorded as not knowing, or as unable to decide
          ORDER NOT ESTABLISHED          two records tie in story time and disagree
          no record                      nothing recorded either way; not the same as unaware
        """;

    /// <param name="terminal">How to reach the person running the command. Left out, it is worked out from the console.</param>
    /// <param name="input">Where a hook's event is read from. Left out, standard input.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, Terminal? terminal = null, TextReader? input = null)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        // A hook answers the harness in the harness's terms, and to the harness exit code 2 means "block". So it
        // keeps clear of the exit codes the other commands use, including 2 for a command not understood.
        if (args[0] == "hook")
            return HookCommand.Run(args[1..], input ?? Console.In, output, error);
        try
        {
            var a = Arguments.Parse(args[1..]);
            var dir = a.Value("dir") ?? Directory.GetCurrentDirectory();
            var at = terminal ?? Terminal.Of(error);
            return args[0] switch
            {
                "init" => Init(dir, a, output),
                "sync" => Sync(Story.Open(dir), a, output, verify: false),
                "verify" => Sync(Story.Open(dir), a, output, verify: true),
                "put" => Put(Story.Open(dir), a, output, error, at),
                "propose" => Propose(Story.Open(dir), a, output, at),
                "review" => Review(Story.Open(dir), a, output),
                "ratify" => Decide(Story.Open(dir), a, output, error, at, accepting: true),
                "reject" => Decide(Story.Open(dir), a, output, error, at, accepting: false),
                "submit-scene" => SubmitScene(Story.Open(dir), a, output, at),
                "state-at" => StateAt(Story.Open(dir), a, output),
                "scene-context" => SceneContext(Story.Open(dir), a, output, at),
                "task-note" => TaskNote(Story.Open(dir), a, output),
                "brief" => Brief(Story.Open(dir), a, output),
                "scan" => Scan(Story.Open(dir), a, output),
                "what-if" => WhatIf(Story.Open(dir), a, output),
                "recap" => RecapOf(Story.Open(dir), a, output),
                "lint" => LintOf(Story.Open(dir), a, output),
                "attention" => Attention(Story.Open(dir), a, output),
                "diagram" => Diagram(Story.Open(dir), a, output),
                "ripple" => RippleOf(Story.Open(dir), a, output),
                "pulse" => PulseOf(Story.Open(dir), a, output),
                "weave" => WeaveOf(Story.Open(dir), a, output),
                "audit" => Audit(Story.Open(dir), a, output),
                "dump" => Dump(Story.Open(dir), a, output),
                "rebuild" => Rebuild(Story.Open(dir), a, output, error, at),
                "protocol" => Protocol(a, output),
                "mcp" => Mcp(dir, a),
                "setup-claude" => SetupClaude(Story.Open(dir), a, output),
                _ => throw new UsageException($"unknown command '{args[0]}'. Run 'nse help'."),
            };
        }
        catch (UsageException e)
        {
            error.WriteLine("nse: " + e.Message);
            return 2;
        }
        catch (Exception e) when (e is LedgerException or BudgetException or DirectoryNotFoundException or FileNotFoundException
                                      or KeyNotFoundException or ArgumentOutOfRangeException or JsonException
                                      or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("nse: " + e.Message);
            return 1;
        }
        catch (ArgumentException e)
        {
            // The core library's way of saying a question was put wrongly.
            error.WriteLine("nse: " + e.Message);
            return 2;
        }
    }

    private static int Init(string dir, Arguments a, TextWriter output)
    {
        a.Done();
        var story = Story.Init(dir);
        output.WriteLine($"Story folder ready at {story.Root}");
        output.WriteLine("  manuscript/   Markdown files. Start each scene with a marker line: <!-- scene: your_scene_id -->");
        output.WriteLine("  ledger/       the story's state history, written only by the engine");
        output.WriteLine("  .nse/         a rebuildable index, ignored by git");
        return 0;
    }

    /// <summary>
    /// What a command that is the author's own says when nobody is at a terminal. It names the ways that are
    /// open to whoever ran it, because the likeliest caller is an agent that reached for the nearest command.
    /// </summary>
    private static int NotAtATerminal(TextWriter error, string command, string because, string instead)
    {
        error.WriteLine($"nse: {command} {because}, so it is the author's own command and runs only at a terminal, where the author is. {instead}");
        return 1;
    }

    private static int Put(Story story, Arguments a, TextWriter output, TextWriter error, Terminal terminal)
    {
        var file = a.Positional(0) ?? throw new UsageException("put needs the path of a change-set file");
        a.Done();
        if (!terminal.Present)
            return NotAtATerminal(error, "put", "writes to the ledger with nothing held back",
                "To record what a drafted scene does, use 'nse submit-scene FILE'. To have any other change considered, use 'nse propose FILE': it is held for the author to accept or decline.");
        var draft = NseJson.Read<ChangeSetDraft>(File.ReadAllText(file));
        var result = story.Put(draft, sound => terminal.Confirm(PutQuestion(sound)));
        if (result.ChangeSet is { } stored)
        {
            output.WriteLine($"Applied as {stored.File}: {draft.Ops.Count} operation(s), ledger head {stored.ChangeSet.Seq}.");
            if (result.Consequences.Count > 0)
            {
                output.WriteLine("This change");
                foreach (var line in result.Consequences)
                    output.WriteLine(line);
            }
            return 0;
        }
        if (result.Findings is [{ Code: Story.NotConfirmed }])
        {
            output.WriteLine("Not confirmed. Nothing was written.");
            return 1;
        }
        output.WriteLine($"Refused. Nothing was written. {result.Findings.Count} finding(s):");
        foreach (var f in result.Findings)
            output.WriteLine("  " + Plain.OneLine(f.ToString()));
        return 1;
    }

    /// <summary>
    /// A proposal or a scene report sent with no terminal behind the command did not come from the author's
    /// keyboard, whatever its file says. Its actor has to say what sent it, so that neither the review queue
    /// nor the ledger puts it down to the author.
    /// </summary>
    private static Finding? NotTheAuthor(Terminal terminal, string actor, string what) =>
        terminal.Present || actor == "agent" || actor.StartsWith("agent:", StringComparison.Ordinal)
            ? null
            : new Finding("BAD_VALUE", null,
                $"this {what} was sent with no terminal behind the command, so it does not come from the author's keyboard. Its actor has to say what sent it: \"agent\", or \"agent:\" and a name such as the model's. It says \"{actor}\"");

    /// <summary>What a change set that passed its checks is about to do, as a question for the author.</summary>
    private static string PutQuestion(ChangeSetDraft draft)
    {
        var parts = new List<string>();
        var puts = draft.Ops.OfType<PutOp>().ToList();
        var ratified = puts.Count(p => p.Approval == Approval.Ratified);
        if (puts.Count > 0)
            parts.Add($"{puts.Count} record(s) to add" + (ratified > 0 ? $", {ratified} of them as ratified" : ""));
        if (draft.Ops.OfType<RetireOp>().Count() is > 0 and var retired)
            parts.Add($"{retired} to retire");
        if (draft.Ops.OfType<RatifyOp>().Sum(o => o.Ids.Count) is > 0 and var accepted)
            parts.Add($"{accepted} to ratify");
        if (draft.Ops.OfType<RejectOp>().Sum(o => o.Ids.Count) is > 0 and var rejected)
            parts.Add($"{rejected} to reject");
        return $"This change set from {Plain.OneLine(draft.Actor)} passed every check: {string.Join(", ", parts)}. None of it is held for review. Write it to the ledger?";
    }

    private static int SubmitScene(Story story, Arguments a, TextWriter output, Terminal terminal)
    {
        var file = a.Positional(0) ?? throw new UsageException("submit-scene needs the path of a scene report file");
        a.Done();
        var report = NseJson.Read<SceneReport>(File.ReadAllText(file));
        var result = NotTheAuthor(terminal, report.Actor, "scene report") is { } not
            ? new SubmitResult(null, [not], [], null)
            : story.SubmitScene(report);
        output.Write(SubmitReport.Text(result));
        return result.Applied ? 0 : 1;
    }

    private static int Propose(Story story, Arguments a, TextWriter output, Terminal terminal)
    {
        var file = a.Positional(0) ?? throw new UsageException("propose needs the path of a change-set file");
        a.Done();
        var draft = NseJson.Read<ChangeSetDraft>(File.ReadAllText(file));
        var result = NotTheAuthor(terminal, draft.Actor, "proposal") is { } not ? new ProposeResult(null, [not]) : story.Propose(draft);
        output.Write(ReviewReport.Proposed(result));
        return result.Held ? 0 : 1;
    }

    private static int Review(Story story, Arguments a, TextWriter output)
    {
        var only = a.Positional(0);
        var brief = a.Flag("brief");
        var plain = a.Flag("plain");
        a.Done();
        var queue = story.Review();
        if (only is not null)
            queue = queue.Only(only) ?? throw new KeyNotFoundException($"Nothing in the review queue has the id {only}: no held proposal, and no change set with records still waiting.");
        output.Write(ReviewReport.Queue(queue, brief: brief, only: only, plain: plain ? story.State() : null));
        return 0;
    }

    private static int Decide(Story story, Arguments a, TextWriter output, TextWriter error, Terminal terminal, bool accepting)
    {
        var verb = accepting ? "ratify" : "reject";
        var ids = a.Positionals();
        var alsoReject = accepting ? a.Value("reject") : null;
        var note = a.Value("note");
        var actor = a.Value("actor") ?? "author";
        a.Done();
        var others = alsoReject?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (ids.Count == 0 && others.Length == 0)
            throw new UsageException($"{verb} needs at least one id: a record, a change set or a proposal. 'nse review' lists what is waiting");
        if (!terminal.Present)
            return NotAtATerminal(error, verb, "decides for the author",
                "An agent shows the author what is waiting ('nse review') and asks. The author then runs this command, or approves the prompt of the ratify tool.");
        var decision = new Decision { Accept = accepting ? ids : [], Reject = accepting ? others : ids, Actor = actor, Note = note };
        // Asked with the lock released. On a yes the decision is worked out again, and written only if it still
        // comes to exactly what was shown.
        var result = story.Decide(decision, plan => terminal.Confirm("This decision:\n" + ReviewReport.Plan(plan) + "Go ahead?"));
        output.Write(ReviewReport.Decided(result));
        return result.Done ? 0 : 1;
    }

    private static int Sync(Story story, Arguments a, TextWriter output, bool verify)
    {
        a.Done();
        var ledger = story.ReadLedger(); // throws if the chain is broken
        var report = story.Sync();
        if (verify)
            output.WriteLine($"Ledger: {ledger.Count} change set(s), chain intact.");
        output.WriteLine($"Manuscript: {report.Scenes} scene(s), hash {report.ManuscriptHash[..12]}. Ledger head {report.Head}. "
                         + (report.IndexRebuilt ? "Index rebuilt." : "Index not rebuilt: another program has it open. Answers are computed from the files meanwhile."));
        Section(output, "Problems with the files", report.Problems);
        Section(output, "Evidence that is no longer in the text",
            report.BrokenEvidence.Select(e => $"{e.RecordId} ({e.Kind}) cites scene {e.Scene}: \"{QuoteMatcher.Normalize(e.Quote)}\" [{e.Status}]"));
        Section(output, "Story time against the telling", report.TimeMismatches);
        Section(output, "Stated to the reader ahead of an embargo", report.EmbargoBreaches);
        Section(output, "Scenes that differ from their contract", report.ContractDifferences);
        Section(output, "Scenes in the manuscript with no scene record", report.ScenesWithoutRecord);
        Section(output, "Scene records with no marker in the manuscript", report.RecordsWithoutScene);
        if (report.Clean)
            output.WriteLine("Nothing out of line.");
        return report.Clean ? 0 : 1;
    }

    private static void Section(TextWriter output, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0)
            return;
        output.WriteLine($"{title} ({list.Count}):");
        // These lines carry ids and quotes from records. Each is printed as one line whatever a record holds.
        foreach (var line in list)
            output.WriteLine("  " + Plain.OneLine(line));
    }

    private static int Rebuild(Story story, Arguments a, TextWriter output, TextWriter error, Terminal terminal)
    {
        a.Done();
        // Building the index again is harmless, with one exception. When the ledger is shorter than this
        // folder has seen it, a rebuild is how the loss is accepted, and accepting it is the author's to do.
        if (story.State().LedgerWarning is { } shortened)
        {
            if (!terminal.Present)
                return NotAtATerminal(error, "rebuild", $"would accept a ledger that has lost its end ({shortened})",
                    "Restore the missing files under ledger/ from git, or tell the author.");
            if (!terminal.Confirm($"{char.ToUpperInvariant(shortened[0])}{shortened[1..]}. Rebuilding accepts the ledger as it stands and forgets that it was longer. Go ahead?"))
            {
                output.WriteLine("Not confirmed. Nothing was changed.");
                return 1;
            }
        }
        var info = story.Rebuild();
        output.WriteLine($"Index rebuilt at ledger head {info.Head}.");
        return 0;
    }

    private static int SceneContext(Story story, Arguments a, TextWriter output, Terminal terminal)
    {
        var scene = a.Value("scene") ?? throw new UsageException("scene-context needs --scene ID");
        var budget = a.Int("budget") ?? ContextCompiler.DefaultBudget;
        var head = a.Int("head");
        var look = a.Flag("no-task");
        var take = a.Flag("take");
        var part = a.Value("part");
        a.Done();
        if (take && (look || head is not null || part is not null))
            throw new UsageException("--take opens the scene's task, and cannot go with --no-task, --head or --part, which make it a look");
        // One part by itself, in full: how a part the budget left out is read. It is a look, and opens nothing.
        if (part is not null)
        {
            output.WriteLine(ContextCompiler.PartText(story.State(head), scene, part));
            return 0;
        }
        // From an agent's shell a context for the story as it stands is taken to work from, and the scene's
        // task notes that it was. At a terminal it is the author reading, and reading is not work: a task
        // opened by every look would later be shown as a draft somebody had left.
        var package = head is null && !look && (take || !terminal.Present)
            ? story.TakeContext(scene, budget, terminal.Present ? "author" : "agent")
            : ContextCompiler.Compile(story.State(head), scene, budget);
        output.WriteLine(package.Text);
        return 0;
    }

    private static int TaskNote(Story story, Arguments a, TextWriter output)
    {
        var file = a.Positional(0) ?? throw new UsageException("task-note needs the path of a note file");
        a.Done();
        var note = NseJson.Read<TaskNote>(File.ReadAllText(file));
        var result = story.Note(note);
        output.Write(TaskReport.Noted(result, note));
        return result.Noted ? 0 : 1;
    }

    private static int Brief(Story story, Arguments a, TextWriter output)
    {
        var head = a.Int("head");
        var task = a.Value("task");
        a.Done();
        if (task is not null && head is not null)
            throw new UsageException("--task shows work as it stands now, and cannot go with --head");
        output.Write(ContextCompiler.Brief(story.State(head), story.HeldProposals(), head is null ? TaskReport.Text(story.Work(), task) : ""));
        return 0;
    }

    private static int RippleOf(Story story, Arguments a, TextWriter output)
    {
        var seed = a.Positional(0) ?? throw new UsageException("ripple needs the id of a record");
        var top = a.Int("top") ?? 40;
        var head = a.Int("head");
        a.Done();
        var weave = Weave.Build(story.State(head));
        output.Write(Weave.RippleText(weave, seed, weave.Ripple(seed), top));
        return 0;
    }

    private static int PulseOf(Story story, Arguments a, TextWriter output)
    {
        var head = a.Int("head");
        a.Done();
        var state = story.State(head);
        output.Write(Weave.PulseText(Weave.Build(state).Pulse(state), state.Model.Head));
        return 0;
    }

    private static int WeaveOf(Story story, Arguments a, TextWriter output)
    {
        var around = a.Value("around");
        var radius = a.Int("radius") ?? 2;
        var keystones = a.Int("keystones");
        var head = a.Int("head");
        a.Done();
        if ((around is null) == (keystones is null))
            throw new UsageException("weave needs exactly one of --around ID or --keystones N");
        var weave = Weave.Build(story.State(head));
        if (around is not null)
        {
            output.Write(Weave.Mermaid(weave, around, radius));
            return 0;
        }
        output.WriteLine($"KEYSTONES · the {keystones} records whose change would reach furthest through the weave");
        foreach (var (node, reach) in weave.Keystones(keystones!.Value))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {reach,8:0.000}  {node.Id} ({node.Kind}): {node.Label}"));
        return 0;
    }

    private static int Diagram(Story story, Arguments a, TextWriter output)
    {
        var view = a.Positional(0) ?? throw new UsageException($"diagram needs a view: {string.Join(", ", Diagrams.Views)}");
        var holder = a.Value("holder");
        var head = a.Int("head");
        a.Done();
        if (holder is not null && view != "knowledge")
            throw new UsageException("--holder goes with the knowledge view only");
        output.Write(Diagrams.Text(story.State(head), view, holder));
        return 0;
    }

    /// <summary>Suggestions are not findings, so the exit code is 0 whatever they say: a story may rightly have some.</summary>
    private static int LintOf(Story story, Arguments a, TextWriter output)
    {
        var head = a.Int("head");
        a.Done();
        output.Write(Lint.Text(story.State(head)));
        return 0;
    }

    /// <summary>What is in doubt is for a person to settle, and a story may rightly have some for a while: the exit code is 0.</summary>
    private static int Attention(Story story, Arguments a, TextWriter output)
    {
        var scene = a.Value("scene");
        var head = a.Int("head");
        a.Done();
        output.Write(Standing.Text(story.State(head), scene));
        return 0;
    }

    private static int RecapOf(Story story, Arguments a, TextWriter output)
    {
        var holder = a.Value("holder") ?? throw new UsageException("recap needs --holder ID");
        var at = a.Value("at");
        var end = a.Flag("end");
        var truth = a.Flag("truth");
        var head = a.Int("head");
        a.Done();
        if ((at is null) == !end)
            throw new UsageException("recap needs exactly one of --at SCENE or --end");
        output.Write(Recap.Text(story.State(head), holder, at, truth));
        return 0;
    }

    private static int WhatIf(Story story, Arguments a, TextWriter output)
    {
        var file = a.Positional(0) ?? throw new UsageException("what-if needs the path of a change-set file");
        a.Done();
        var result = story.WhatIf(NseJson.Read<ChangeSetDraft>(File.ReadAllText(file)));
        output.Write(Query.WhatIf.Text(result));
        return result.WouldApply ? 0 : 1;
    }

    private static int Scan(Story story, Arguments a, TextWriter output)
    {
        var scene = a.Value("scene");
        var file = a.Value("file");
        var all = a.Flag("all");
        a.Done();
        if (all == (scene is not null))
            throw new UsageException("scan needs exactly one of --scene ID or --all");
        if (all && file is not null)
            throw new UsageException("--file is a draft of one scene; it goes with --scene, not --all");
        var results = all ? story.ScanAll() : [story.Scan(scene!, file)];
        for (var i = 0; i < results.Count; i++)
            output.Write((i > 0 ? "\n" : "") + DraftScan.Text(results[i]));
        return results.All(r => r.Clean) ? 0 : 1;
    }

    private static int Audit(Story story, Arguments a, TextWriter output)
    {
        var scene = a.Value("scene") ?? throw new UsageException("audit needs --scene ID");
        var completeness = a.Flag("completeness");
        a.Done();
        output.Write(story.AuditBrief(scene, completeness ? AuditMode.Completeness : AuditMode.Leaks));
        return 0;
    }

    private static int Dump(Story story, Arguments a, TextWriter output)
    {
        var head = a.Int("head");
        a.Done();
        output.Write(story.Dump(head));
        return 0;
    }

    private static int StateAt(Story story, Arguments a, TextWriter output)
    {
        var query = new StateQuery(a.Value("scene"), a.Flag("end"), a.Value("holder"), a.Value("prop"), a.Value("object"), a.Flag("reader"), a.Flag("history"));
        var json = a.Flag("json");
        var head = a.Int("head");
        a.Done();
        if (query.History && (query.Prop is null) == (query.Object is null))
            throw new UsageException("--history needs exactly one of --prop ID or --object ID");
        if (query.History && json)
            throw new UsageException("--history is given as text only; leave out --json");
        if (query.History && (query.Scene is not null || query.End || query.ReaderOnly))
            throw new UsageException("--history covers the whole story; leave out --scene, --end and --reader");
        if (!query.History && (query.Scene is null) == !query.End)
            throw new UsageException("state-at needs exactly one of --scene ID or --end");
        var state = story.State(head);
        output.Write(json ? StateDump.Write(StateReport.Json(state, query)) : StateReport.Text(state, query));
        return 0;
    }

    private static int Protocol(Arguments a, TextWriter output)
    {
        var withRatify = a.Flag("with-ratify");
        a.Done();
        output.WriteLine(McpHost.Instructions(withRatify));
        return 0;
    }

    private static int Mcp(string dir, Arguments a)
    {
        var withRatify = a.Flag("with-ratify");
        a.Done();
        return McpHost.RunAsync(dir, ratifyTool: withRatify).GetAwaiter().GetResult();
    }

    private static int SetupClaude(Story story, Arguments a, TextWriter output)
    {
        var engine = a.Value("engine");
        var dotnet = a.Value("dotnet");
        var force = a.Flag("force");
        var auditor = a.Flag("with-auditor");
        a.Done();
        return ClaudeSetup.Run(story, engine, dotnet, force, output, auditor);
    }
}

public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// How the command line reaches the person running it. A few commands are the author's own: they decide for
/// the author, or write to the ledger with nothing held back. Those run only where a person is, which means
/// at a terminal. A command an agent starts has no terminal behind it: its input is not a keyboard. That is
/// what is tested, because it does not depend on how the command was spelled or what rules the harness was given.
/// </summary>
/// <remarks>
/// This is a guard against the easy mistake, not against an agent set on getting round it. Whatever can type
/// into the author's terminal, or write files as the author, can act as the author. The harness is what
/// limits that.
/// </remarks>
public sealed class Terminal(bool present, Func<string, bool> confirm)
{
    /// <summary>Whether a person is there to be asked.</summary>
    public bool Present { get; } = present;

    /// <summary>Put a yes-or-no question to the person and wait for the answer. Anything but yes is no.</summary>
    public bool Confirm(string question) => Present && confirm(question);

    /// <summary>
    /// The terminal this process has, if it has one. The author's own unattended scripts say so by setting
    /// NSE_UNATTENDED to "author"; whatever sets it is acting as the author and is answered yes.
    /// </summary>
    public static Terminal Of(TextWriter prompt)
    {
        if (Environment.GetEnvironmentVariable("NSE_UNATTENDED") == "author")
            return new Terminal(true, _ => true);
        return new Terminal(!Console.IsInputRedirected, question =>
        {
            prompt.Write(question + " [y/N] ");
            prompt.Flush();
            var answer = Console.ReadLine()?.Trim();
            return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
        });
    }
}

/// <summary>Options (--name value, --flag) and positionals. Anything left unread is an error, so typos are not ignored.</summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private int _positionalRead;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "end", "reader", "json", "history", "brief", "with-ratify", "with-auditor", "no-task", "take", "force", "all", "completeness", "truth", "plain" };

    public static Arguments Parse(string[] args)
    {
        var a = new Arguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                a._positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (Flags.Contains(name))
                a._options[name] = null;
            else if (i + 1 < args.Length)
                a._options[name] = args[++i];
            else
                throw new UsageException($"--{name} needs a value");
        }
        return a;
    }

    public string? Value(string name)
    {
        _read.Add(name);
        return _options.GetValueOrDefault(name);
    }

    public bool Flag(string name)
    {
        _read.Add(name);
        return _options.ContainsKey(name);
    }

    public int? Int(string name) =>
        Value(name) is { } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException($"--{name} must be a whole number")
            : null;

    /// <summary>Every positional, for a command that takes as many as it is given.</summary>
    public IReadOnlyList<string> Positionals()
    {
        _positionalRead = _positional.Count;
        return _positional;
    }

    public string? Positional(int index)
    {
        _positionalRead = Math.Max(_positionalRead, index + 1);
        return index < _positional.Count ? _positional[index] : null;
    }

    public void Done()
    {
        var unknown = _options.Keys.Where(k => !_read.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"unknown option --{unknown[0]}");
        if (_positional.Count > _positionalRead)
            throw new UsageException($"unexpected argument '{_positional[_positionalRead]}'");
    }
}
