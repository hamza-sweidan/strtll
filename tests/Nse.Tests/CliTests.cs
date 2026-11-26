using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nse.Ledger;
using Nse.Model;
using Nse.Tasks;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>The command line, run in process against the two-scene story.</summary>
public sealed class CliTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    // The author at a terminal: every question put to them is kept, and answered as the test says.
    private readonly List<string> _asked = [];
    private bool _answer = true;

    private (int Code, string Out, string Err) Run(params string[] args) =>
        RunAt(new Cli.Terminal(true, question => { _asked.Add(question); return _answer; }), args);

    /// <summary>The same command with nobody at a terminal, as when an agent's shell starts it.</summary>
    private (int Code, string Out, string Err) Away(params string[] args) =>
        RunAt(new Cli.Terminal(false, _ => throw new InvalidOperationException("nobody is there to be asked")), args);

    private (int Code, string Out, string Err) RunAt(Cli.Terminal terminal, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Cli.Cli.Run([.. args, "--dir", _tiny.Root], output, error, terminal);
        return (code, output.ToString(), error.ToString());
    }

    private string WriteProposal(string name, string note, params Operation[] ops)
    {
        var path = Path.Combine(_tiny.Root, name);
        File.WriteAllText(path, NseJson.Write(new ChangeSetDraft { Actor = "agent", Note = note, Ops = ops }));
        return path;
    }

    private static PutOp HoldBack => new() { Record = new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s2" } };

    private Approval ApprovalOf(string id)
    {
        var projection = Projection.Build(_tiny.Story.ReadLedger().Select(s => s.ChangeSet));
        return projection.Find(id)!.ApprovalAt(projection.Head);
    }

    private string WriteDraft(string name, params LedgerRecord[] records)
    {
        var path = Path.Combine(_tiny.Root, name);
        File.WriteAllText(path, NseJson.Write(new ChangeSetDraft
        {
            Actor = "author", Note = "from the command line",
            Ops = records.Select(r => (Operation)new PutOp { Record = r }).ToList(),
        }));
        return path;
    }

    [Fact]
    public void State_at_shows_what_a_person_holds_and_labels_what_is_not_ratified()
    {
        var (code, text, _) = Run("state-at", "--scene", "s2", "--holder", "ann");
        Assert.Equal(0, code);
        Assert.Contains("STATE ON ENTRY TO s2 (story time 20, number 2 in the telling) · ledger head 1", text);
        Assert.Contains("Ann (ann)", text);
        Assert.Contains("p_hid  [knows] takes it as true, certain: Ann hid the key under the mat.  (b_ann; took part, s1, PROVISIONAL)", text);
        Assert.DoesNotContain("The reader has been given", text);

        // On entry to scene 1 nothing has happened yet.
        Assert.Contains("no record either way for: p_hid", Run("state-at", "--scene", "s1", "--holder", "ann").Out);
    }

    [Fact]
    public void State_at_says_no_record_instead_of_does_not_know()
    {
        var (code, text, _) = Run("state-at", "--end", "--holder", "ben", "--prop", "p_hid");
        Assert.Equal(0, code);
        Assert.Contains("p_hid  no record. That is not the same as \"does not know\"", text);
    }

    [Fact]
    public void A_persona_is_answered_as_the_person_behind_it()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_ben", 20.1)).Applied);
        var (_, text, _) = Run("state-at", "--end", "--holder", "stranger");
        Assert.Contains("Ben (ben)", text);
        Assert.Contains("[correctly believes]", text);
    }

    [Fact]
    public void State_at_as_json_is_the_same_point_the_dump_holds()
    {
        var (code, text, _) = Run("state-at", "--scene", "s2", "--json");
        Assert.Equal(0, code);
        var doc = JsonNode.Parse(text)!;
        Assert.Equal("s2", doc["at"]!.GetValue<string>());
        Assert.Equal("knows", doc["beliefs"]!["ann"]!["p_hid"]!["label"]!.GetValue<string>());
        Assert.Equal("mat", doc["world"]!["key"]!["place"]!.GetValue<string>());
        var dump = JsonNode.Parse(_tiny.Story.Dump())!;
        Assert.Empty(JsonDiff.Differences(dump["points"]!["s2"]!["beliefs"], doc["beliefs"]));

        var onlyObject = JsonNode.Parse(Run("state-at", "--end", "--object", "key", "--json").Out)!.AsObject();
        Assert.False(onlyObject.ContainsKey("beliefs"));
        Assert.Equal("mat", onlyObject["world"]!["key"]!["place"]!.GetValue<string>());
    }

    [Fact]
    public void Put_applies_a_good_change_set_and_refuses_a_bad_one_without_writing()
    {
        var good = WriteDraft("good.json", _tiny.BenBelief("b_ben", 20.1, quote: "Ben found the key under the mat"));
        // Sound, and the author asked says no: nothing is written.
        _answer = false;
        var (code, text, _) = Run("put", good);
        Assert.Equal(1, code);
        Assert.Equal("Not confirmed. Nothing was written.", text.TrimEnd());
        Assert.Equal("This change set from author passed every check: 1 record(s) to add. None of it is held for review. Write it to the ledger?", Assert.Single(_asked));
        Assert.Single(_tiny.Story.ReadLedger());

        _answer = true;
        (code, text, _) = Run("put", good);
        Assert.Equal(0, code);
        Assert.Matches(@"Applied as 000002-cs_[0-9a-f]{10}\.json: 1 operation\(s\), ledger head 2\.", text);
        Assert.Equal(2, _asked.Count);

        var bad = WriteDraft("bad.json", _tiny.BenBelief("b_ben2", 20.2, quote: "Ben found the keys"));
        (code, text, _) = Run("put", bad);
        Assert.Equal(1, code);
        Assert.Contains("Refused. Nothing was written.", text);
        Assert.Contains("QUOTE_NOT_FOUND b_ben2", text);
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
        // What is refused is not asked about.
        Assert.Equal(2, _asked.Count);

        // The question says what is being done with nothing held back.
        var decides = Path.Combine(_tiny.Root, "decides.json");
        File.WriteAllText(decides, NseJson.Write(new ChangeSetDraft
        {
            Actor = "author",
            Ops =
            [
                new PutOp { Record = new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s2" }, Approval = Approval.Ratified },
                new RetireOp { Id = "b_ben", Reason = "correction" }, new RatifyOp { Ids = ["b_ann", "e_hide"] }, new RejectOp { Ids = ["a_deny"] },
            ],
        }));
        Assert.Equal(0, Run("put", decides).Code);
        Assert.Equal("This change set from author passed every check: 1 record(s) to add, 1 of them as ratified, 1 to retire, 2 to ratify, 1 to reject. None of it is held for review. Write it to the ledger?", _asked[^1]);

        File.WriteAllText(bad, File.ReadAllText(bad).Replace("\"actor\"", "\"surprise\": true, \"actor\""));
        var (strictCode, _, strictError) = Run("put", bad);
        Assert.Equal(1, strictCode);
        Assert.Contains("surprise", strictError);
    }

    [Fact]
    public void A_history_of_an_object_lists_every_move_and_what_moved_it()
    {
        var (code, text, _) = Run("state-at", "--object", "key", "--history");
        Assert.Equal(0, code);
        Assert.StartsWith("HISTORY OF key · ledger head 1\nthe key: where it is kept, in story order\n", text);
        Assert.Matches(@"s1 +story time 10\.1 +under the mat  \(l_mat; by Ann; e_hide: Ann hides the key\., PROVISIONAL\)", text);

        // A history is of one thing, across the whole story, and is given as text.
        Assert.Equal(2, Run("state-at", "--history").Code);
        Assert.Equal(2, Run("state-at", "--prop", "p_hid", "--object", "key", "--history").Code);
        Assert.Equal(2, Run("state-at", "--prop", "p_hid", "--history", "--json").Code);
        Assert.Equal(2, Run("state-at", "--prop", "p_hid", "--history", "--scene", "s1").Code);
        Assert.Equal(1, Run("state-at", "--object", "nothing", "--history").Code);
    }

    /// <summary>A null in a required field is not applied, and an operation with no "op" does not crash the command.</summary>
    [Fact]
    public void A_change_set_file_that_is_not_in_shape_is_refused_with_a_message()
    {
        var nulled = WriteDraft("null.json", new Event { Id = "e_null", StoryKey = 10, Summary = "to be nulled" });
        File.WriteAllText(nulled, File.ReadAllText(nulled).Replace("\"to be nulled\"", "null"));
        var (code, _, error) = Run("put", nulled);
        Assert.Equal(1, code);
        Assert.Contains("null is not allowed at $.ops[0].record.summary. Give it a value", error);

        var noOp = WriteDraft("no-op.json", new Event { Id = "e_fine", StoryKey = 10, Summary = "fine" });
        Assert.Contains("\"op\": \"put\",", File.ReadAllText(noOp));
        File.WriteAllText(noOp, File.ReadAllText(noOp).Replace("\"op\": \"put\",", ""));
        (code, _, error) = Run("put", noOp);
        Assert.Equal(1, code);
        Assert.Contains("missing its \"op\"", error);

        // In a file, a null is refused even where the field could have been left out: it is not what was meant.
        var noCast = WriteDraft("no-cast.json", new SceneRecord { Id = "s9", Pov = "ann", Cast = ["ann"] });
        var doc = JsonNode.Parse(File.ReadAllText(noCast))!;
        doc["ops"]![0]!["record"]!["cast"] = null;
        File.WriteAllText(noCast, doc.ToJsonString());
        (code, _, error) = Run("put", noCast);
        Assert.Equal(1, code);
        Assert.Contains("cast", error);

        Assert.Single(_tiny.Story.ReadLedger());
        Assert.Equal(0, Run("verify").Code);
    }

    [Fact]
    public void Dump_and_state_at_answer_as_of_an_older_head()
    {
        var before = _tiny.Story.Dump();
        Assert.True(_tiny.Put(_tiny.BenBelief("b_ben", 20.1)).Applied);
        Assert.Equal(before, Run("dump", "--head", "1").Out);
        Assert.NotEqual(before, Run("dump").Out);
        Assert.Contains("no record", Run("state-at", "--end", "--holder", "ben", "--prop", "p_hid", "--head", "1").Out);
        var (code, _, error) = Run("dump", "--head", "9");
        Assert.Equal(1, code);
        Assert.Contains("there is no head 9", error);
    }

    [Fact]
    public void Verify_passes_a_sound_story_and_sync_reports_evidence_the_text_no_longer_holds()
    {
        var (code, text, _) = Run("verify");
        Assert.Equal(0, code);
        Assert.Contains("Ledger: 1 change set(s), chain intact.", text);
        Assert.Contains("Nothing out of line.", text);

        // The author rewrites the sentence two records quote.
        var file = Path.Combine(_tiny.Story.ManuscriptDir, "01-one.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Ann hid the key under the mat.", "Ann slid the key beneath the mat."));
        (code, text, _) = Run("sync");
        Assert.Equal(1, code);
        Assert.Contains("Evidence that is no longer in the text (1):", text);
        Assert.Contains("e_hide (event) cites scene s1: \"Ann hid the key under the mat.\" [missing]", text);
    }

    [Fact]
    public void Mistyped_commands_and_options_are_errors_not_guesses()
    {
        Assert.Equal(2, Run("stat-at").Code);
        Assert.Equal(2, Run("state-at", "--scene", "s2", "--holdr", "ann").Code);
        Assert.Equal(2, Run("state-at").Code);
        Assert.Equal(2, Run("state-at", "--scene", "s2", "--end").Code);
        Assert.Equal(2, Run("dump", "--head", "two").Code);
        Assert.Equal(2, Run("put").Code);
        Assert.Equal(2, Run("sync", "extra").Code);
        Assert.Equal(1, Run("state-at", "--scene", "s9").Code);
        Assert.Equal(1, Run("state-at", "--end", "--holder", "nobody").Code);
        Assert.Contains("nse init", Run("help").Out);
    }

    // ---------------------------------------------------------------- proposals and the author's decision

    [Fact]
    public void A_change_is_proposed_reviewed_and_decided_from_the_command_line()
    {
        var (code, text, _) = Run("propose", WriteProposal("embargo.json", "Hold it back until scene 2.", HoldBack));
        Assert.Equal(0, code);
        var id = Regex.Match(text, @"^Held as proposal (pr_[0-9a-f]{16}): 1 operation\(s\), checked against ledger head 1\.").Groups[1].Value;
        Assert.NotEqual("", id);
        Assert.Contains("Nothing was applied", text);
        Assert.Single(_tiny.Story.ReadLedger());

        (code, text, _) = Run("review");
        Assert.Equal(0, code);
        Assert.Contains($"  {id} · proposed by agent at ledger head 1 · would apply as things stand", text);
        Assert.Contains("      1. put em_hid: embargo: p_hid is not to be given to the reader before s2", text);
        Assert.Contains("e_hide", text);
        // One item alone, the whole of it in brief, and an id that is not in the queue.
        (code, text, _) = Run("review", id);
        Assert.Equal(0, code);
        Assert.StartsWith($"REVIEW QUEUE · ledger head 1 · {id} alone\n", text);
        Assert.DoesNotContain("e_hide", text);
        (code, text, _) = Run("review", "--brief");
        Assert.Equal(0, code);
        Assert.Contains("      1 operation(s): 1 to add, 0 to retire", text);
        Assert.DoesNotContain("1. put em_hid", text);
        var (missing, _, why) = Run("review", "b_ann");
        Assert.Equal(1, missing);
        Assert.Contains("Nothing in the review queue has the id b_ann", why);
        Assert.Contains("16 provisional record(s). Treat them as unconfirmed. 1 proposal(s) held for the author's decision.", Run("brief").Out);

        // The author is shown what the decision comes to, says no, and nothing happens.
        _answer = false;
        (code, text, _) = Run("ratify", id);
        Assert.Equal(1, code);
        Assert.Equal("Not confirmed. Nothing was decided.\n", text);
        // The question spells out what the id stands for: the author confirms the content, not a name for it.
        Assert.Equal(
            $"This decision:\n  accept proposal {id}, from agent, and ratify what it adds: Hold it back until scene 2.\n"
            + "    the author's to decide because it sets an embargo (em_hid)\n"
            + "      1. put em_hid: embargo: p_hid is not to be given to the reader before s2\n"
            + "Go ahead?", Assert.Single(_asked));
        Assert.Single(_tiny.Story.ReadLedger());

        // The author says yes.
        _answer = true;
        (code, text, _) = Run("ratify", id, "--note", "agreed");
        Assert.Equal(0, code);
        Assert.Matches($@"Proposal {id} accepted and applied as 000002-cs_[0-9a-f]{{10}}\.json: 1 record\(s\) ratified, 0 retired\.", text);
        Assert.Contains("Ledger head 2.", text);
        Assert.Equal("agreed", _tiny.Story.ReadLedger()[1].ChangeSet.Note);
        Assert.Equal("author", _tiny.Story.ReadLedger()[1].ChangeSet.Actor);
        Assert.Contains("Embargo em_hid", Run("brief").Out);
        Assert.DoesNotContain("proposal(s) held", Run("brief").Out);

        // A proposal that is refused says why and holds nothing. So does a decision.
        (code, text, _) = Run("propose", WriteProposal("bad.json", "These are fine.", new RatifyOp { Ids = ["b_ann"] }));
        Assert.Equal(1, code);
        Assert.StartsWith("Refused. Nothing was held and nothing was written. 1 finding(s):\n  AUTHOR_ONLY: ", text);
        (code, text, _) = Run("ratify", "b_nobody");
        Assert.Equal(1, code);
        Assert.Contains("UNKNOWN_REFERENCE b_nobody", text);
        (code, text, _) = Run("reject", id);
        Assert.Equal(1, code);
        Assert.Contains("ALREADY_DECIDED", text);
        Assert.Equal(2, Run("ratify").Code);
        Assert.Equal(2, Run("reject").Code);
        Assert.Equal(2, Run("propose").Code);
        Assert.Equal(2, Run("reject", "b_ann", "--reject", "a_deny").Code);
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
    }

    [Fact]
    public void Records_are_decided_by_their_own_ids_or_by_the_change_set_that_brought_them()
    {
        var first = _tiny.Story.ReadLedger()[0].ChangeSet.Id;
        var (code, text, _) = Run("ratify", first, "--reject", "a_deny", "--note", "she says nothing in this draft");
        Assert.Equal(0, code);
        Assert.Matches(@"15 record\(s\) ratified and 1 rejected, as 000002-cs_[0-9a-f]{10}\.json\.", text);
        Assert.Contains("  ratify 15 record(s):\n            ann       entity: Ann (character)\n            ben       entity: Ben (character)\n", Assert.Single(_asked));
        Assert.Contains("  reject 1 record(s):\n            a_deny    dialogue at 10.2 in s1: Ann denies p_hid · ", _asked[0]);
        Assert.Equal(Approval.Ratified, ApprovalOf("b_ann"));
        Assert.Equal(Approval.Rejected, ApprovalOf("a_deny"));
        Assert.Contains("Nothing is awaiting review.", Run("brief").Out);
        Assert.Contains("Nothing is waiting", Run("review").Out);

        // Nothing is left of it to decide, and a decision is final.
        (code, text, _) = Run("ratify", first);
        Assert.Equal(1, code);
        Assert.Contains("NOTHING_TO_DECIDE", text);
        (code, text, _) = Run("reject", "b_ann", "--note", "changed my mind");
        Assert.Equal(1, code);
        Assert.Contains("A decision is final", text);
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
    }

    [Fact]
    public void The_authors_own_commands_do_not_run_where_nobody_is_at_a_terminal()
    {
        var good = WriteDraft("good.json", _tiny.BenBelief("b_ben", 20.1, quote: "Ben found the key under the mat"));
        var (code, text, error) = Away("put", good);
        Assert.Equal(1, code);
        Assert.Equal("", text);
        Assert.Contains("nse: put writes to the ledger with nothing held back, so it is the author's own command and runs only at a terminal", error);
        Assert.Contains("'nse submit-scene FILE'", error);
        Assert.Contains("'nse propose FILE'", error);
        (code, text, error) = Away("ratify", "b_ann");
        Assert.Equal(1, code);
        Assert.Equal("", text);
        Assert.Contains("nse: ratify decides for the author, so it is the author's own command and runs only at a terminal", error);
        (code, _, error) = Away("reject", "b_ann");
        Assert.Equal(1, code);
        Assert.Contains("nse: reject decides for the author", error);
        Assert.Single(_tiny.Story.ReadLedger());
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));

        // What an agent may do from the command line still works there: read, and propose.
        Assert.Equal(0, Away("propose", WriteProposal("embargo.json", "Hold it back until scene 2.", HoldBack)).Code);
        Assert.Contains("Proposals held for the author's decision (1)", Away("review").Out);
        Assert.Equal(0, Away("brief").Code);
        Assert.Equal(0, Away("state-at", "--end").Code);
        Assert.Equal(0, Away("rebuild").Code);
        Assert.Single(_tiny.Story.ReadLedger());
        // A command that is put wrongly is still told so first.
        Assert.Equal(2, Away("put").Code);
        Assert.Equal(2, Away("ratify").Code);
    }

    [Fact]
    public void Accepting_a_ledger_that_has_lost_its_end_is_asked_about_and_only_at_a_terminal()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_ben", 20.1)).Applied);
        Assert.Equal(2, _tiny.Story.State().SeenHead);
        File.Delete(Directory.GetFiles(_tiny.Story.LedgerDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Last());

        var (code, text, error) = Away("rebuild");
        Assert.Equal(1, code);
        Assert.Contains("nse: rebuild would accept a ledger that has lost its end (the ledger ends at change set 1, but this folder has seen it end at 2", error);
        Assert.Equal(2, _tiny.Story.State().SeenHead);

        _answer = false;
        (code, text, _) = Run("rebuild");
        Assert.Equal(1, code);
        Assert.Equal("Not confirmed. Nothing was changed.\n", text.ReplaceLineEndings("\n"));
        Assert.StartsWith("The ledger ends at change set 1, but this folder has seen it end at 2.", Assert.Single(_asked));
        Assert.EndsWith("Rebuilding accepts the ledger as it stands and forgets that it was longer. Go ahead?", _asked[0]);
        Assert.Equal(2, _tiny.Story.State().SeenHead);

        _answer = true;
        (code, text, _) = Run("rebuild");
        Assert.Equal(0, code);
        Assert.Contains("Index rebuilt at ledger head 1.", text);
        Assert.Equal(1, _tiny.Story.State().SeenHead);

        // With nothing lost there is nothing to ask.
        _asked.Clear();
        Assert.Equal(0, Run("rebuild").Code);
        Assert.Empty(_asked);
    }

    /// <summary>
    /// The real program, started the way an agent's shell starts it: with a pipe where a keyboard would be.
    /// This is the check the in-process tests stand in for.
    /// </summary>
    [Fact]
    public void Started_with_no_keyboard_behind_it_the_real_program_refuses_the_authors_commands()
    {
        (int Code, string Out, string Err) Start(string? unattended, string typed, params string[] args)
        {
            var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var info = new ProcessStartInfo(dotnet)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "nse.dll"));
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            info.ArgumentList.Add("--dir");
            info.ArgumentList.Add(_tiny.Root);
            info.Environment.Remove("NSE_UNATTENDED");
            if (unattended is not null)
                info.Environment["NSE_UNATTENDED"] = unattended;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(typed);
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(60_000), "the program did not finish");
            return (process.ExitCode, stdout.Result, stderr.Result);
        }

        var good = WriteDraft("good.json", _tiny.BenBelief("b_ben", 20.1, quote: "Ben found the key under the mat"));
        var (code, _, error) = Start(null, "", "put", good);
        Assert.Equal(1, code);
        Assert.Contains("runs only at a terminal", error);
        // Answers fed down a pipe are not a person at a keyboard.
        (code, _, error) = Start(null, "y\nyes\n", "ratify", "b_ann");
        Assert.Equal(1, code);
        Assert.Contains("runs only at a terminal", error);
        (code, _, error) = Start("yes", "y\n", "reject", "b_ann");
        Assert.Equal(1, code);
        Assert.Contains("runs only at a terminal", error);
        Assert.Single(_tiny.Story.ReadLedger());
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));
        // Reading is not held back.
        Assert.Contains("REVIEW QUEUE", Start(null, "", "review").Out);

        // The author's own unattended scripts say that they act as the author.
        Assert.Equal(0, Start("author", "", "put", good).Code);
        Assert.Equal(0, Start("author", "", "ratify", "b_ann").Code);
        Assert.Equal(Approval.Ratified, ApprovalOf("b_ann"));
        Assert.Equal(3, _tiny.Story.ReadLedger().Count);
    }

    /// <summary>
    /// From a shell the sender is not whoever the file says: a proposal could otherwise arrive as
    /// "proposed by author" and its records be put down to the author.
    /// </summary>
    [Fact]
    public void Sent_with_no_terminal_a_proposal_or_a_scene_report_cannot_say_it_is_from_the_author()
    {
        string Proposal(string actor)
        {
            var path = Path.Combine(_tiny.Root, "by.json");
            File.WriteAllText(path, NseJson.Write(new ChangeSetDraft { Actor = actor, Note = "Hold it back until scene 2.", Ops = [HoldBack] }));
            return path;
        }
        foreach (var actor in new[] { "author", "Author", "the author", "import", "agentx" })
        {
            var (code, text, _) = Away("propose", Proposal(actor));
            Assert.Equal(1, code);
            Assert.StartsWith("Refused. Nothing was held and nothing was written. 1 finding(s):\n  BAD_VALUE: this proposal was sent with no terminal behind the command", text);
            Assert.Contains($"It says \"{actor}\"", text);
        }
        Assert.Empty(_tiny.Story.Review().Proposals);
        // Saying what sent it is enough.
        Assert.Equal(0, Away("propose", Proposal("agent:some-model")).Code);
        Assert.Equal("agent:some-model", Assert.Single(_tiny.Story.Review().Proposals).Proposal.Actor);

        string Report(string actor)
        {
            var path = Path.Combine(_tiny.Root, "scene.json");
            File.WriteAllText(path, NseJson.Write(new SceneReport
            {
                Scene = "s2", Manifest = Query.ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId, Actor = actor,
                Records = [_tiny.BenBelief("b_ben", 20.1)],
            }));
            return path;
        }
        var (refused, said, _) = Away("submit-scene", Report("author"));
        Assert.Equal(1, refused);
        Assert.StartsWith("Refused. Nothing was written. 1 finding(s):\n  BAD_VALUE: this scene report was sent with no terminal behind the command", said);
        Assert.Single(_tiny.Story.ReadLedger());
        Assert.Equal(0, Away("submit-scene", Report("agent")).Code);
        var submitted = _tiny.Story.ReadLedger()[^1].ChangeSet;
        Assert.Equal("agent", submitted.Actor);
        Assert.Equal("agent", submitted.Ops.OfType<PutOp>().Single().Record.Origin);

        // At a terminal the author may send either under their own name.
        Assert.Equal(0, Run("propose", Proposal("author")).Code);
        Assert.Contains(_tiny.Story.Review().Proposals, p => p.Proposal.Actor == "author");
    }

    // ---------------------------------------------------------------- task records

    [Fact]
    public void A_task_is_opened_noted_and_shown_from_the_command_line()
    {
        // From an agent's shell, taking a context opens a task for the scene, and its last words say what to note first.
        var taken = Away("scene-context", "--scene", "s2");
        Assert.Equal(0, taken.Code);
        Assert.Equal("agent", TaskStore.Open(_tiny.Root, "s2")!.Actor);
        Assert.EndsWith("A session can end at any moment without warning, and a later one has nothing but these notes to go on." + Environment.NewLine, taken.Out);
        Assert.Contains("A task is open for this scene. Before you write or change any prose, call task_note with your plan", taken.Out);
        // A look opens nothing, and says nothing of a task: one that says so, or one at an older head.
        var look = Away("scene-context", "--scene", "s1", "--no-task");
        Assert.Equal(0, look.Code);
        Assert.DoesNotContain("task_note", look.Out);
        var older = Away("scene-context", "--scene", "s1", "--head", "1");
        Assert.Equal(0, older.Code);
        Assert.DoesNotContain("task_note", older.Out);
        Assert.Null(TaskStore.Open(_tiny.Root, "s1"));
        // At a terminal it is the author reading, and reading opens nothing either.
        var read = Run("scene-context", "--scene", "s1");
        Assert.Equal(0, read.Code);
        Assert.DoesNotContain("task_note", read.Out);
        Assert.Null(TaskStore.Open(_tiny.Root, "s1"));
        // The author can take it to work from by saying so, and the task is then theirs.
        Assert.Equal(0, Run("scene-context", "--scene", "s1", "--take").Code);
        Assert.Equal("author", TaskStore.Open(_tiny.Root, "s1")!.Actor);
        // Taking it and only looking at it cannot both be asked for.
        Assert.Equal(2, Away("scene-context", "--scene", "s1", "--take", "--no-task").Code);
        Assert.Equal(2, Away("scene-context", "--scene", "s1", "--take", "--head", "1").Code);

        var note = Path.Combine(_tiny.Root, "note.json");
        File.WriteAllText(note, """{"scene": "s2", "decided": ["Ben keeps the key."], "open_questions": ["Who sees him?"], "next": "Write the ending."}""");
        var (code, text, _) = Away("task-note", note);
        Assert.Equal(0, code);
        Assert.Equal("Noted in task tk_s2_1 for s2: 1 decision(s) added, 1 question(s) now open, the next step set.\nA session that starts after this one reads it in the brief.\n", text);

        // The brief shows the task that has work in it, and not the one that was only a look.
        var brief = Away("brief").Out;
        Assert.Contains("Work in progress (1).", brief);
        Assert.Contains("  s2 \"Two\" · POV Ben (ben) · task tk_s2_1, opened by agent\n", brief);
        Assert.Contains("    open questions (1):\n      1. Who sees him?\n", brief);
        Assert.Contains("    next: Write the ending.\n", brief);
        // Work in progress is how things are now: a brief as of an older head leaves it out.
        Assert.DoesNotContain("Work in progress", Away("brief", "--head", "1").Out);
        // One task can be asked for by its scene, and only as things stand now.
        var one = Away("brief", "--task", "s2").Out;
        Assert.Contains("\nThe task open for s2. What stands under decided", one);
        Assert.Contains("    next: Write the ending.\n", one);
        Assert.DoesNotContain("Work in progress (", one);
        Assert.Contains("\nNo task is open for s9.\n", Away("brief", "--task", "s9").Out);
        Assert.Equal(2, Away("brief", "--task", "s2", "--head", "1").Code);

        File.WriteAllText(note, """{"scene": "s9", "next": "x"}""");
        (code, text, _) = Away("task-note", note);
        Assert.Equal(1, code);
        Assert.StartsWith("Refused. Nothing was noted. 1 finding(s):\n  NO_TASK: no task is open for s9.", text);
        // A note file is read as strictly as any other, and what is wrong with it is said in the file's own words.
        File.WriteAllText(note, """{"scene": "s2", "nxt": "x"}""");
        var wrong = Away("task-note", note);
        Assert.Equal(1, wrong.Code);
        Assert.Equal("nse: \"nxt\" is not a field that belongs here. Check its spelling, or leave it out." + Environment.NewLine, wrong.Err);
        // Who a note is from is not for the note to say.
        File.WriteAllText(note, """{"scene": "s2", "actor": "author", "next": "x"}""");
        Assert.Contains("\"actor\" is not a field that belongs here", Away("task-note", note).Err);
        Assert.Equal(2, Away("task-note").Code);
        Assert.Equal(2, TaskStore.Open(_tiny.Root, "s2")!.Steps.Count);

        // The way of working an agent is given can be printed, for a hook or a session that has no server.
        Assert.Equal(Cli.McpHost.Instructions(ratifyTool: false) + Environment.NewLine, Away("protocol").Out);
        Assert.Contains("call ratify with exactly those ids", Away("protocol", "--with-ratify").Out);
        Assert.Contains("Before you write or change any prose, call task_note with your plan", Away("protocol").Out);
    }

    [Fact]
    public void Init_makes_a_story_folder_and_the_other_commands_need_one()
    {
        var dir = Path.Combine(_tiny.Root, "new-story");
        var output = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["sync", "--dir", dir], output, new StringWriter()));
        Assert.Equal(0, Cli.Cli.Run(["init", "--dir", dir], output, new StringWriter()));
        Assert.True(Directory.Exists(Path.Combine(dir, "manuscript")));
        Assert.True(Directory.Exists(Path.Combine(dir, "ledger")));
        Assert.Equal(0, Cli.Cli.Run(["sync", "--dir", dir], output, new StringWriter()));
        Assert.Contains("Nothing out of line.", output.ToString());
    }
}
