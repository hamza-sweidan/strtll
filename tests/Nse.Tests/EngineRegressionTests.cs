using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nse.Index;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// Regression tests for the engine's read and write paths: one test for each defect once found there. They pin
/// the corrected behaviour.
/// </summary>
public sealed class EngineRegressionTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private static BeliefChange Ben(string id, double key, Stance stance, string prop = "p_hid") => new()
    {
        Id = id, Holder = "ben", Prop = prop, Stance = stance,
        Strength = stance is Stance.HoldsTrue or Stance.HoldsFalse ? Strength.Believes : null,
        StoryKey = key, Channel = Channel.Assumed,
    };

    private void AddThirdScene(params string[] cast)
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"), "<!-- scene: s3 -->\n# Three\n\nCara came in from the yard.\n");
        var put = _tiny.Put(
            new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
            new SceneRecord { Id = "s3", Pov = "ann", Cast = cast },
            new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 });
        Assert.True(put.Applied, Codes(put));
    }

    // ---- 1. A null in a required field was accepted, written without the field, and the ledger could not be read again.

    [Fact]
    public void A_null_where_a_value_is_required_is_refused_and_the_ledger_stays_readable()
    {
        var place = new PutOp { Record = new Entity { Id = "x", Type = EntityKind.Place, Name = "X" } };
        PutResult[] refused =
        [
            _tiny.Put(new Event { Id = "e_null", StoryKey = 10, Summary = null! }),
            _tiny.Put(new Assertion
            {
                Id = "a_null", Scene = "s1", StoryKey = 10.2, Type = AssertionKind.Dialogue, Speaker = "ann", Prop = "p_hid", Quote = null!,
            }),
            _tiny.Put(new Proposition { Id = "p_null", Text = null! }),
            _tiny.Put(new Entity { Id = null!, Type = EntityKind.Character, Name = "Nobody" }),
            _tiny.Put(new SceneRecord { Id = "s9", Pov = "ann", Cast = ["ann", null!] }),
            _tiny.Story.Put(new ChangeSetDraft { Actor = null!, Ops = [place] }),
            _tiny.Story.Put(new ChangeSetDraft { Actor = "author", Ops = [place, null!] }),
            _tiny.Apply(new RatifyOp { Ids = [null!] }),
            _tiny.Apply(new RetireOp { Id = null!, Reason = "correction" }),
        ];
        for (var i = 0; i < refused.Length; i++)
            Assert.True(!refused[i].Applied && Codes(refused[i]) == "MALFORMED", $"case {i}: {string.Join("; ", refused[i].Findings)}");
        Assert.Contains("cast[1]", refused[4].Findings[0].Message);
        Assert.Equal("BAD_VALUE", Codes(_tiny.Story.Put(new ChangeSetDraft { Actor = "  ", Ops = [place] })));
        // A null where a field may simply be left out is read as left out, and the record is judged as it then
        // stands: here a scene with no cast, which its own point-of-view character is therefore not in.
        var noCast = _tiny.Put(new SceneRecord { Id = "s9", Pov = "ann", Cast = null! });
        Assert.Equal("BAD_VALUE", Codes(noCast));
        Assert.Contains("is not in the cast", noCast.Findings[0].Message);

        // Nothing was written, and everything that reads the ledger still can.
        Assert.Single(_tiny.Story.ReadLedger());
        Assert.True(_tiny.Story.Sync().Clean);
        Assert.Contains("p_hid", _tiny.Story.Dump());
    }

    /// <summary>
    /// The second line of defence, tried on its own by going round <see cref="Story.Put"/>: the bytes about to
    /// become a ledger file are read back first, exactly as the next load would read them.
    /// </summary>
    [Fact]
    public void Nothing_that_cannot_be_read_back_is_moved_into_the_ledger()
    {
        var ledger = _tiny.Story.ReadLedger();
        var unreadable = new ChangeSetDraft
        {
            Actor = "author", Ops = [new PutOp { Record = new Event { Id = "e_null", StoryKey = 10, Summary = null! } }],
        };
        var refused = Assert.Throws<LedgerException>(() => LedgerStore.Append(_tiny.Story.LedgerDir, ledger, unreadable, "0"));
        Assert.Contains("not a readable change set", refused.Message);
        Assert.Equal(1, Directory.GetFiles(_tiny.Story.LedgerDir).Count(f => !f.EndsWith(".lock", StringComparison.Ordinal)));
        Assert.Single(_tiny.Story.ReadLedger());
    }

    // ---- 2. Reject did what retire was forbidden to do, and nothing checked it.

    [Fact]
    public void Rejecting_a_record_must_not_leave_another_pointing_at_nothing()
    {
        var alone = _tiny.Apply(new RejectOp { Ids = ["p_hid"] });
        Assert.False(alone.Applied);
        Assert.Equal(["a_deny", "b_ann", "f_hid"], alone.Findings.Where(f => f.Code == "DANGLING_REFERENCE").Select(f => f.RecordId).Order());
        Assert.Contains("rejected by this change set", alone.Findings[0].Message);

        // With everything that points at it, it goes, and every question can still be asked.
        var together = _tiny.Apply(new RejectOp { Ids = ["p_hid", "f_hid", "a_deny", "b_ann"] });
        Assert.True(together.Applied, Codes(together));
        var state = _tiny.Story.State();
        Assert.Null(state.Model.Proposition("p_hid"));
        Assert.DoesNotContain("p_hid", _tiny.Story.Dump());
        Assert.StartsWith("CONTEXT FOR SCENE 1, s1", ContextCompiler.Compile(state, "s1").Text);
        Assert.True(_tiny.Story.Sync().Clean);
    }

    // ---- 3. A second decision overwrote the first, so "as of head N" changed its answer afterwards.

    [Fact]
    public void A_decision_is_final_so_an_older_head_keeps_its_answer()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_guess", 20.1)).Applied);          // head 2
        Assert.True(_tiny.Apply(new RejectOp { Ids = ["b_guess"] }).Applied);       // head 3
        var asOfThree = _tiny.Story.Dump(3);
        Assert.DoesNotContain("b_guess", asOfThree);

        var again = _tiny.Apply(new RatifyOp { Ids = ["b_guess"] });
        Assert.Equal("LEDGER", Codes(again));
        Assert.Contains("was rejected in change set 3. A decision is final", again.Findings[0].Message);
        Assert.True(_tiny.Put(new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" }).Applied);   // head 4
        Assert.Equal(asOfThree, _tiny.Story.Dump(3));

        // Ratified is final too: what was ratified in error is retired, with a reason.
        Assert.True(_tiny.Apply(new RatifyOp { Ids = ["b_ann"] }).Applied);
        Assert.Equal("LEDGER", Codes(_tiny.Apply(new RejectOp { Ids = ["b_ann"] })));
        Assert.Equal("LEDGER", Codes(_tiny.Apply(new RatifyOp { Ids = ["b_ann"] })));
        var ratifiedOnEntry = _tiny.Apply(new PutOp { Record = new Entity { Id = "dora", Type = EntityKind.Character, Name = "Dora" }, Approval = Approval.Ratified });
        Assert.True(ratifiedOnEntry.Applied);
        var notWaiting = _tiny.Apply(new RejectOp { Ids = ["dora"] });
        Assert.Contains("Only a provisional record awaits a decision", notWaiting.Findings[0].Message);
        Assert.True(_tiny.Apply(new RetireOp { Id = "dora", Reason = "withdrawn" }).Applied);
    }

    // ---- 4. Two readers with a stale index both crashed; a put that could not refresh the index looked like a failure.

    [Fact]
    public void Readers_that_find_the_index_stale_at_the_same_moment_all_get_the_answer()
    {
        var expected = _tiny.Story.Dump();
        for (var round = 0; round < 6; round++)
        {
            File.Delete(_tiny.Story.IndexPath);
            var answers = new string[8];
            Parallel.For(0, answers.Length, i => answers[i] = Story.Open(_tiny.Root).Dump());
            Assert.All(answers, answer => Assert.Equal(expected, answer));
        }
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(_tiny.Story.IndexPath)!), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_put_is_applied_even_when_another_program_has_the_index_open()
    {
        _tiny.Story.State();
        using (new FileStream(_tiny.Story.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = _tiny.Put(_tiny.BenBelief("b_held", 20.1, stance: Stance.HoldsFalse));
            Assert.True(result.Applied, Codes(result));
            Assert.Equal(2, _tiny.Story.ReadLedger().Count);
            // The index on disk cannot be replaced on Windows while it is held. The answer comes from the files.
            var held = _tiny.Story.State();
            Assert.Equal("b_held", held.BeliefsBefore("ben", StoryState.End)["p_hid"].Id);
            if (OperatingSystem.IsWindows())
            {
                Assert.False(held.FromIndex);
                Assert.False(_tiny.Story.Sync().IndexRebuilt);
            }
        }
        Assert.True(_tiny.Story.Sync().IndexRebuilt);
        Assert.True(_tiny.Story.State().FromIndex);
        Assert.True(StoryIndex.ReadInfo(_tiny.Story.IndexPath)!.Matches(_tiny.Story.ReadLedger(), _tiny.Story.ReadManuscript()));
    }

    // ---- 5. Embargo records were stored and never read.

    [Fact]
    public void An_embargo_is_in_the_context_and_a_disclosure_that_breaks_it_is_refused()
    {
        Assert.True(_tiny.Put(new Embargo { Id = "em1", Prop = "p_hid", UntilScene = "s2" }).Applied);
        var context = ContextCompiler.Compile(_tiny.Story.State(), "s1").Text;
        // No scene states it yet, so nothing but the embargo protects it. That is the case when drafting ahead.
        Assert.Contains("  p_hid: Ann hid the key under the mat. [UNCONFIRMED] (embargo em1, provisional but keep to it: not before scene 2, s2)", context);
        Assert.DoesNotContain("nothing is being withheld", context);
        Assert.DoesNotContain("embargo em1", ContextCompiler.Compile(_tiny.Story.State(), "s2").Text);
        Assert.Contains("Embargo em1: p_hid is not to be stated to the reader before s2.", ContextCompiler.Brief(_tiny.Story.State()));

        Disclosure Told(string id, string scene, DisclosureMode mode, string? quote = null) => new()
        {
            Id = id, Scene = scene, Prop = "p_hid", Mode = mode, ReaderStance = Stance.HoldsTrue,
            Strength = mode == DisclosureMode.Stated ? Strength.Certain : Strength.Suspects, Quote = quote,
        };
        var early = _tiny.Put(Told("d_early", "s1", DisclosureMode.Stated, "Ann hid the key under the mat."));
        Assert.Equal("EMBARGO_BROKEN", Codes(early));
        Assert.Contains("it states p_hid in s1, and embargo em1 holds that back until s2", early.Findings[0].Message);
        // Whoever says it, a reader left certain has been told.
        var sure = _tiny.Put(Told("d_sure", "s1", DisclosureMode.AssertedByCharacter) with { Strength = Strength.Certain });
        Assert.Equal("EMBARGO_BROKEN", Codes(sure));
        Assert.Contains("leaves the reader certain of p_hid", sure.Findings[0].Message);
        // Narration that states the opposite is misdirection, not a reveal.
        Assert.True(_tiny.Put(Told("d_denied", "s1", DisclosureMode.Stated) with { ReaderStance = Stance.HoldsFalse }).Applied);
        // A hint is not a breach, and neither is telling it where the embargo lifts.
        Assert.True(_tiny.Put(Told("d_hint", "s1", DisclosureMode.Inferable)).Applied);
        Assert.True(_tiny.Put(Told("d_told", "s2", DisclosureMode.Stated, "Ben found the key under the mat")).Applied);
        Assert.Contains("(first told in scene 2, s2; embargo em1, provisional but keep to it: not before scene 2, s2)",
            ContextCompiler.Compile(_tiny.Story.State(), "s1").Text);

        // An embargo laid over something already told is refused too, naming what it runs into.
        AddThirdScene("ann");
        File.Delete(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"));
        var late = _tiny.Put(new Embargo { Id = "em2", Prop = "p_hid", UntilScene = "s3" });
        Assert.Equal("EMBARGO_BROKEN", Codes(late));
        Assert.Equal("d_told", late.Findings[0].RecordId);
        Assert.Contains("which is not in the manuscript yet", late.Findings[0].Message);
    }

    [Fact]
    public void Moving_a_scene_ahead_of_an_embargo_is_reported_by_sync()
    {
        var put = _tiny.Put(
            new Embargo { Id = "em_r", Prop = "p_hid", UntilScene = "s1" },
            new Disclosure
            {
                Id = "d_told", Scene = "s2", Prop = "p_hid", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain,
            });
        Assert.True(put.Applied, Codes(put));
        Assert.Empty(_tiny.Story.Sync().EmbargoBreaches);

        // Scene 2 is now told first. No record changed.
        File.Move(Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"), Path.Combine(_tiny.Story.ManuscriptDir, "00-two.md"));
        var report = _tiny.Story.Sync();
        var breach = Assert.Single(report.EmbargoBreaches);
        Assert.Equal("d_told states p_hid in s2; embargo em_r holds it back until s1", breach);
        Assert.False(report.Clean);

        var output = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["sync", "--dir", _tiny.Root], output, new StringWriter()));
        Assert.Contains("Stated to the reader ahead of an embargo (1):", output.ToString());
    }

    // ---- 6. A fact dated at a scene's own story time counted on entry, while beliefs and moves dated there did not.

    [Fact]
    public void A_fact_that_starts_at_a_scenes_own_story_time_has_not_started_on_entry()
    {
        var put = _tiny.Put(
            new Proposition { Id = "p_pocket", Text = "The key is in Ben's pocket." },
            new FactChange { Id = "f_pocket", Prop = "p_pocket", Value = true, StoryKey = 20 },   // scene 2's own story time
            new BeliefChange
            {
                Id = "b_no", Holder = "ann", Prop = "p_pocket", Stance = Stance.HoldsFalse, Strength = Strength.Certain,
                StoryKey = 12, Channel = Channel.Assumed,
            });
        Assert.True(put.Applied, Codes(put));
        var s = _tiny.Story.State();
        var key = s.Key("s2");
        Assert.False(s.TruthBefore("p_pocket", key));
        Assert.True(s.TruthAt("p_pocket", key));

        // On entry Ann is right that it is not there yet. By the end she is wrong.
        Assert.Equal("knows it is false", s.Classify(s.BeliefsBefore("ann", key)["p_pocket"], key));
        Assert.Equal("FALSE BELIEF", s.Classify(s.BeliefsBefore("ann", StoryState.End)["p_pocket"], StoryState.End));
        var text = StateReport.Text(s, new StateQuery(Scene: "s2", Holder: "ann", Prop: "p_pocket"));
        Assert.Contains("Canon: true; not the case at this point.", text);
        Assert.Contains("[knows it is false]", text);
        Assert.False(StateDump.Build(s)["points"]!["s2"]!["truth"]!["p_pocket"]!.GetValue<bool>());
        Assert.True(StateDump.Build(s)["points"]!["end"]!["truth"]!["p_pocket"]!.GetValue<bool>());
        // It comes about during the scene, so it is in play there even though it is not yet so on entry.
        Assert.Contains("p_pocket", s.InPlay("s2"));
        Assert.DoesNotContain("p_pocket", s.InPlay("s1"));
    }

    // ---- 7. Two fact changes at one story time that disagree were settled by which record id sorted last.

    [Theory]
    [InlineData("fa", "fb")]
    [InlineData("fb", "fa")]
    public void Fact_changes_that_tie_and_disagree_are_reported_not_settled_by_record_id(string makesTrue, string makesFalse)
    {
        var put = _tiny.Put(
            new Proposition { Id = "p_tie", Text = "The door is locked." },
            new FactChange { Id = makesTrue, Prop = "p_tie", Value = true, StoryKey = 15 },
            new FactChange { Id = makesFalse, Prop = "p_tie", Value = false, StoryKey = 15 },
            Ben("b_tie", 16, Stance.HoldsTrue, "p_tie"));
        Assert.True(put.Applied, Codes(put));
        var s = _tiny.Story.State();
        Assert.False(s.TruthAt("p_tie", 14));
        Assert.Null(s.TruthAt("p_tie", 16));
        Assert.Equal(["fa", "fb"], s.FactTie("p_tie", 16, before: false));
        Assert.Equal("ORDER NOT ESTABLISHED", s.Classify(s.BeliefsBefore("ben", StoryState.End)["p_tie"], StoryState.End));
        Assert.Contains("Canon: true; not settled at this point: fact changes fa and fb share a story time and disagree.",
            StateReport.Text(s, new StateQuery(End: true, Prop: "p_tie")));
        Assert.Equal("unresolved", StateDump.Build(s)["points"]!["end"]!["truth"]!["p_tie"]!.GetValue<string>());

        // A later change settles it. Ben is then wrong; whether he was ever right is not known, so "outdated",
        // which would claim that he was, is not said.
        Assert.True(_tiny.Put(new FactChange { Id = "fc", Prop = "p_tie", Value = false, StoryKey = 17 }).Applied);
        s = _tiny.Story.State();
        Assert.False(s.TruthAt("p_tie", 18));
        Assert.Equal("FALSE BELIEF", s.Classify(s.BeliefsBefore("ben", StoryState.End)["p_tie"], StoryState.End));
    }

    // ---- 8. A secret was not broken if the outsider's belief was dated before the fact came about.

    [Fact]
    public void A_secret_is_broken_by_a_belief_that_becomes_right_while_it_is_in_force()
    {
        Assert.True(_tiny.Put(new Secret { Id = "se1", Props = ["p_hid"], KnownOnlyTo = ["ann"] }).Applied);
        // The key is hidden at 10.1. Ben believing it from story time 2 makes him right from 10.1 on.
        var early = _tiny.Put(Ben("b_early", 2, Stance.HoldsTrue));
        Assert.Equal("SECRET_BROKEN", Codes(early));
        Assert.Contains("ben is right about p_hid from story time 10.1, while this belief is still in force", early.Findings[0].Message);

        // Given up before it comes about, the belief is never right. Being right that it has not happened lets nobody in.
        var dropped = _tiny.Put(Ben("b_then", 2, Stance.HoldsTrue), Ben("b_dropped", 5, Stance.Unaware), Ben("b_not", 6, Stance.HoldsFalse));
        Assert.True(dropped.Applied, Codes(dropped));
        Assert.Equal("SECRET_BROKEN", Codes(_tiny.Put(Ben("b_in", 20.1, Stance.HoldsTrue))));
    }

    // ---- 9. The quote check was a substring test. (The matcher itself is tested with the manuscript tests.)

    [Fact]
    public void Evidence_must_be_whole_words_from_one_paragraph_of_the_scenes_body()
    {
        Event Cite(string id, string quote) => new() { Id = id, Scene = "s1", StoryKey = 10.3, Summary = "x", Quote = quote };
        var midWord = _tiny.Put(Cite("q1", "nn hid the key"));
        Assert.Equal("QUOTE_NOT_FOUND", Codes(midWord));
        Assert.Contains("it starts or ends inside a word", midWord.Findings[0].Message);
        Assert.Equal("QUOTE_NOT_FOUND", Codes(_tiny.Put(Cite("q2", "hid the key under the ma"))));
        // When the words are there, the message says what is wrong instead of offering the same words back.
        var across = _tiny.Put(Cite("q3", "Ben was not there. \"I never touched it,\""));
        Assert.Equal("QUOTE_NOT_FOUND", Codes(across));
        Assert.Contains("runs across a paragraph break in scene s1", across.Findings[0].Message);
        Assert.DoesNotContain("Nearest passage", across.Findings[0].Message);
        Assert.Equal("QUOTE_NOT_FOUND", Codes(_tiny.Put(Cite("q4", "One Ann hid the key"))));
        var one = _tiny.Put(Cite("q5", "key"));
        Assert.Equal("QUOTE_TOO_SHORT", Codes(one));
        Assert.Contains("Quote at least two words, or a whole paragraph", one.Findings[0].Message);
        Assert.True(_tiny.Put(Cite("q6", "hid the key")).Applied);

        // Evidence that an edit has cut into is reported the same way by sync.
        var file = Path.Combine(_tiny.Story.ManuscriptDir, "01-one.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Ann hid the key", "Ann had hid the keys"));
        Assert.Equal(["e_hide", "q6"], _tiny.Story.Sync().BrokenEvidence.Select(e => e.RecordId).Order());
    }

    // ---- 10. Line numbers were served from a stale index after an edit that moved lines without changing any scene.

    [Fact]
    public void Line_numbers_follow_the_file_when_lines_move_and_no_scene_changes()
    {
        Assert.Contains("manuscript/02-two.md lines 2-4: this scene as it stands", ContextCompiler.Compile(_tiny.Story.State(), "s2").Text);
        var hash = _tiny.Story.ReadManuscript().Hash;
        var file = Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md");
        File.WriteAllText(file, "\n\n\n" + File.ReadAllText(file));

        Assert.Equal(hash, _tiny.Story.ReadManuscript().Hash);
        Assert.Contains("manuscript/02-two.md lines 5-7: this scene as it stands", ContextCompiler.Compile(_tiny.Story.State(), "s2").Text);
        // And the index itself was rebuilt, for whatever else reads line numbers out of it.
        Assert.Equal(_tiny.Story.ReadManuscript().Layout, StoryIndex.ReadInfo(_tiny.Story.IndexPath)!.Layout);
        Assert.True(_tiny.Story.State().FromIndex);
    }

    // ---- 11. "Never learns it" was said of someone who does, when the later belief had no scene.

    [Fact]
    public void Something_learned_off_the_page_is_not_reported_as_never_learned()
    {
        Assert.True(_tiny.Put(Ben("b_wrong", 12, Stance.HoldsFalse), Ben("b_later", 25, Stance.HoldsTrue)).Applied);
        var s = _tiny.Story.State();
        var gap = Assert.Single(s.KnowledgeGaps("ben", "s2"));
        Assert.Equal(KnowledgeStatus.Wrong, gap.Status);
        Assert.Null(gap.LearnsIn);
        Assert.Equal(25.0, gap.LearnsAt);
        var text = ContextCompiler.Compile(s, "s2").Text;
        Assert.Contains("  BELIEVES THE OPPOSITE of what is so, and acts on it:\n    p_hid: Ann hid the key under the mat. [UNCONFIRMED] (learns it later, off the page, at story time 25)", text);
        Assert.DoesNotContain("never learns it", text);
        // Two contexts are compared by the line in full, as it was always written.
        Assert.Contains("  p_hid: Ann hid the key under the mat. [UNCONFIRMED: rests on provisional p_hid, f_hid] (learns it later, off the page, at story time 25; currently believes the opposite)",
            ContextCompiler.Compile(s, "s2").Sections.Single(x => x.Title.StartsWith("Ben (point of view): does NOT know", StringComparison.Ordinal)).Lines);
    }

    // ---- 12. Whether a statement was true was judged against whether the fact was ever true, not when it was said.

    [Fact]
    public void A_statement_is_judged_against_what_is_the_case_when_it_is_made()
    {
        Assertion Says(string id, string who, double key, string scene, bool polarity, string quote) => new()
        {
            Id = id, Scene = scene, StoryKey = key, Type = AssertionKind.Dialogue, Speaker = who, Prop = "p_mat", Polarity = polarity, Quote = quote,
        };
        var put = _tiny.Put(
            new Proposition { Id = "p_mat", Text = "The key is under the mat." },
            new FactChange { Id = "f_mat_on", Prop = "p_mat", Value = true, StoryKey = 10.1 },
            new FactChange { Id = "f_mat_off", Prop = "p_mat", Value = false, StoryKey = 20.1 },   // Ben pockets it
            new BeliefChange { Id = "b_ann_mat", Holder = "ann", Prop = "p_mat", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 10.1, Channel = Channel.Assumed },
            new BeliefChange { Id = "b_ben_mat", Holder = "ben", Prop = "p_mat", Stance = Stance.HoldsFalse, Strength = Strength.Certain, StoryKey = 20.1, Channel = Channel.Assumed },
            Says("a_then", "ann", 15, "s1", true, "Ann hid the key under the mat."),
            Says("a_ann", "ann", 21, "s2", true, "Ben found the key"),
            Says("a_ben", "ben", 21, "s2", false, "put it in his pocket"));
        Assert.True(put.Applied, Codes(put));
        var s = _tiny.Story.State();
        Assert.Equal("true", s.Canon("p_mat"));
        Assert.Equal("sincere: true", s.ClassifyAssertion("a_then"));
        // Ann still believes it and says so: an honest mistake. Ben, who took the key, denies it: true.
        Assert.Equal("sincere: false", s.ClassifyAssertion("a_ann"));
        Assert.Equal("sincere: true", s.ClassifyAssertion("a_ben"));
        Assert.Equal("OUTDATED BELIEF", s.Classify(s.BeliefsBefore("ann", StoryState.End)["p_mat"], StoryState.End));
    }

    // ---- Smaller things from the same review.

    [Fact]
    public void A_ledger_that_has_lost_its_newest_change_set_is_noticed_through_the_index()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_more", 20.1)).Applied);
        Assert.True(_tiny.Story.Sync().Clean);
        File.Delete(Directory.GetFiles(_tiny.Story.LedgerDir, "*.json").Order(StringComparer.Ordinal).Last());

        // What is left is a whole chain, one shorter. The files alone cannot show that anything is gone.
        Assert.Single(_tiny.Story.ReadLedger());

        // Ordinary reads come first, as they would in a session, and each of them rebuilds the index. The
        // memory of the longer ledger is carried from index to index, so it is still there afterwards.
        Assert.Contains("WARNING: the ledger ends at change set 1, but this folder has seen it end at 2.", ContextCompiler.Brief(_tiny.Story.State()));
        Assert.Equal(2, _tiny.Story.State().SeenHead);
        _tiny.Story.Dump();
        for (var again = 0; again < 2; again++)
        {
            var report = _tiny.Story.Sync();
            Assert.Contains(report.Problems, p => p.StartsWith("the ledger ends at change set 1, but this folder has seen it end at 2.", StringComparison.Ordinal));
            Assert.False(report.Clean);
        }

        // Nothing is added on top of a ledger that may have lost its end.
        var onTop = _tiny.Put(_tiny.BenBelief("b_on_top", 20.2));
        Assert.Equal("LEDGER_SHORTENED", Codes(onTop));
        Assert.Single(_tiny.Story.ReadLedger());

        // An explicit rebuild accepts the ledger as it stands.
        _tiny.Story.Rebuild();
        Assert.True(_tiny.Story.Sync().Clean);
        Assert.DoesNotContain("WARNING", ContextCompiler.Brief(_tiny.Story.State()));
        Assert.True(_tiny.Put(_tiny.BenBelief("b_on_top", 20.2)).Applied);
    }

    // ---- Further cases of the same kinds.

    /// <summary>The embargo check looked only at the records a change set carried, so the same three records got in by another order.</summary>
    [Fact]
    public void An_embargo_cannot_be_got_round_by_entering_the_fact_last()
    {
        var first = _tiny.Put(
            new Proposition { Id = "p_z", Text = "The stranger is Ben." },
            new FactChange { Id = "f_z0", Prop = "p_z", Value = false },
            new Embargo { Id = "em_z", Prop = "p_z", UntilScene = "s2" },
            new Disclosure
            {
                Id = "d_z", Scene = "s1", Prop = "p_z", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain,
            });
        // The proposition is recorded as not so, and telling the reader that it is so is misdirection.
        Assert.True(first.Applied, Codes(first));

        // The fact change carries neither the disclosure nor the embargo, and it is what makes the breach.
        var makesItSo = _tiny.Put(new FactChange { Id = "f_z", Prop = "p_z", Value = true, StoryKey = 15 });
        Assert.Equal("EMBARGO_BROKEN", Codes(makesItSo));
        Assert.Equal("d_z", makesItSo.Findings[0].RecordId);
        Assert.Contains("embargo em_z holds that back until s2", makesItSo.Findings[0].Message);
        Assert.Empty(_tiny.Story.Sync().EmbargoBreaches);

        // A breach that is already there, because scenes were moved, does not block unrelated work.
        Assert.True(_tiny.Put(
            new Embargo { Id = "em_r", Prop = "p_hid", UntilScene = "s1" },
            new Disclosure { Id = "d_r", Scene = "s2", Prop = "p_hid", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain }).Applied);
        File.Move(Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"), Path.Combine(_tiny.Story.ManuscriptDir, "00-two.md"));
        Assert.Single(_tiny.Story.Sync().EmbargoBreaches);
        Assert.True(_tiny.Put(new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" }).Applied);
    }

    /// <summary>Tied fact changes were still settled by entry order in the scene context, and someone with a record was told "nothing recorded".</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scene_context_does_not_depend_on_which_tied_fact_change_was_entered_last(bool trueFirst)
    {
        var makesTrue = new FactChange { Id = "fa", Prop = "p_tie", Value = true, StoryKey = 15 };
        var makesFalse = new FactChange { Id = "fb", Prop = "p_tie", Value = false, StoryKey = 15 };
        var put = _tiny.Put(
            new Proposition { Id = "p_tie", Text = "The door is locked." },
            trueFirst ? makesTrue : makesFalse,
            trueFirst ? makesFalse : makesTrue,
            Ben("b_tie", 16, Stance.HoldsTrue, "p_tie"),
            new BeliefChange { Id = "b_und", Holder = "ben", Prop = "p_hid", Stance = Stance.Undecided, StoryKey = 12, Channel = Channel.Assumed });
        Assert.True(put.Applied, Codes(put));
        var s = _tiny.Story.State();
        Assert.Contains("p_tie", s.InPlay("s2"));
        Assert.Equal(KnowledgeStatus.Unsettled, s.Status("ben", "p_tie", "s2"));
        Assert.Equal(KnowledgeStatus.Unsettled, s.Status("ben", "p_hid", "s2"));

        var text = ContextCompiler.Compile(s, "s2").Text;
        var gaps = text.Split("Ben (point of view): does NOT know, and must not say it, be told it or act on it in this scene\n")[1].Split("\n\n")[0];
        Assert.Equal(
            "  NOT SETTLED what they make of it:\n" +
            "    p_hid: Ann hid the key under the mat. [UNCONFIRMED] (recorded as undecided; treat as not knowing)\n" +
            "    p_tie: The door is locked. [NOT SETTLED whether this is so by the end of the scene: fact changes fa and fb share a story time and disagree] [UNCONFIRMED] (holds a view, but whether the fact is so is not settled, so the view cannot be judged; treat as not knowing)",
            gaps);
        Assert.DoesNotContain("NO RECORD", gaps);
        // Worded above, where it is not settled, so here it is named: the view he holds, under the label it earns.
        Assert.Contains("  [ORDER NOT ESTABLISHED] takes it as true, believes:\n    p_tie*", text);
    }

    /// <summary>An outsider who took the secret fact as true after it had stopped being so was let in.</summary>
    [Fact]
    public void A_secret_is_broken_by_learning_it_late_and_cannot_be_kept_to_a_persona()
    {
        var put = _tiny.Put(
            new Proposition { Id = "p_tin", Text = "The key is in the tea tin." },
            new FactChange { Id = "f_tin_on", Prop = "p_tin", Value = true, StoryKey = 1 },
            new FactChange { Id = "f_tin_off", Prop = "p_tin", Value = false, StoryKey = 12 },
            new Secret { Id = "se_tin", Props = ["p_tin"], KnownOnlyTo = ["ann"] });
        Assert.True(put.Applied, Codes(put));
        var late = _tiny.Put(Ben("b_late", 13, Stance.HoldsTrue, "p_tin"));
        Assert.Equal("SECRET_BROKEN", Codes(late));
        Assert.Contains("ben takes p_tin as true after it has stopped being so. It was so from story time 1", late.Findings[0].Message);
        // Believing it only before it came about, and giving it up, is still nothing.
        Assert.True(_tiny.Put(Ben("b_guess", 0, Stance.HoldsTrue, "p_tin"), Ben("b_gave_up", 0.5, Stance.Unaware, "p_tin")).Applied);

        // "stranger" is a persona of Ben. Beliefs attach to Ben, so a secret kept to the persona could never be held.
        var persona = _tiny.Put(new Secret { Id = "se_p", Props = ["p_hid"], KnownOnlyTo = ["ann", "stranger"] });
        Assert.Equal("PERSONA_HOLDER", Codes(persona));
        Assert.Contains("Use ben", persona.Findings[0].Message);
    }

    /// <summary>Required strings could be empty, a record could enter already rejected, and an infinite story time came out as a raw error.</summary>
    [Fact]
    public void Empty_names_rejected_on_entry_and_story_times_that_are_not_numbers_are_refused()
    {
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Proposition { Id = "p_blank", Text = " " })));
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Entity { Id = "nobody", Type = EntityKind.Character, Name = "" })));
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Event { Id = "e_blank", StoryKey = 10, Summary = "" })));
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Secret { Id = "se_none", Props = [], KnownOnlyTo = ["ann"] })));

        var rejected = _tiny.Apply(new PutOp { Record = new Entity { Id = "dora", Type = EntityKind.Character, Name = "Dora" }, Approval = Approval.Rejected });
        Assert.Equal("BAD_VALUE", Codes(rejected));
        Assert.Contains("cannot enter the ledger already rejected", rejected.Findings[0].Message);
        // Put and rejected in one change set is the way to keep a refused proposal on the record.
        var both = _tiny.Apply(new PutOp { Record = new Entity { Id = "dora", Type = EntityKind.Character, Name = "Dora" } }, new RejectOp { Ids = ["dora"] });
        Assert.True(both.Applied, Codes(both));
        Assert.Null(_tiny.Story.State().Model.Entity("dora"));

        foreach (var key in new[] { double.PositiveInfinity, double.NegativeInfinity, double.NaN })
        {
            var notFinite = _tiny.Put(new SceneTime { Id = "t_inf", Scene = "s1", StoryKey = key });
            Assert.Equal("MALFORMED", Codes(notFinite));
            Assert.StartsWith("a number in the change set is not finite.", notFinite.Findings[0].Message);
            Assert.DoesNotContain("JsonSerializer", notFinite.Findings[0].Message);
        }
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
    }

    /// <summary>A reader waited a second for a swap that could not happen, on every read.</summary>
    [Fact]
    public void A_read_does_not_wait_for_an_index_another_program_is_holding()
    {
        if (!OperatingSystem.IsWindows())
            return; // elsewhere a file that is open can still be replaced, so there is nothing to wait for
        _tiny.Story.State();
        var file = Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md");
        using (new FileStream(_tiny.Story.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            File.WriteAllText(file, "\n\n" + File.ReadAllText(file));
            _tiny.Story.State();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var state = _tiny.Story.State();
            watch.Stop();
            Assert.False(state.FromIndex);
            Assert.Contains("manuscript/02-two.md lines 4-6", ContextCompiler.Compile(state, "s2").Text);
            Assert.True(watch.ElapsedMilliseconds < 700, $"a read took {watch.ElapsedMilliseconds} ms with the index held open");
        }
    }

    [Fact]
    public void A_cast_member_with_no_belief_records_is_still_named_and_comes_under_the_rule_about_silence()
    {
        AddThirdScene("ann", "cara", "mat");
        var s = _tiny.Story.State();
        Assert.Equal(["ann", "cara"], s.CastHolders("s3").Select(c => c.Person));
        var text = ContextCompiler.Compile(s, "s3").Text;
        Assert.Contains("\nCast: Ann (ann, point of view); Cara (cara, no belief records); under the mat (mat, not a person, so nothing is tracked for it).\n", text);
        // She has no record on anything, and the story shows nothing of what she does not know. That is silence,
        // which is said once for everyone, so there is no list of it under her name; she is still there.
        Assert.Contains("  Not listed means NO RECORD: where a fact is not under someone's name, nothing has been recorded either way.", text);
        Assert.DoesNotContain("Cara: does NOT know", text);
        Assert.Contains("Cara: beliefs on entry\n  no recorded beliefs yet", text);
        // The guarded fact is under the name of the one who has it. Nothing in this small story has been ratified,
        // so it is marked as unconfirmed.
        Assert.Contains("Ann (point of view): beliefs on entry\n  [knows] takes it as true, certain:\n    p_hid: Ann hid the key under the mat. [UNCONFIRMED] (b_ann*)", text);
        Assert.Contains("  A fact that is not under someone's name: no record either way.", text);
    }

    [Fact]
    public void A_fact_in_play_that_nobody_present_has_a_record_on_is_said_once_as_the_authors_alone()
    {
        static string ThirdSceneThroughCara(TinyStory tiny, params string[] cast)
        {
            File.WriteAllText(Path.Combine(tiny.Story.ManuscriptDir, "03-three.md"), "<!-- scene: s3 -->\n# Three\n\nCara waited in the hall.\n");
            Assert.True(tiny.Put(
                new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
                new SceneRecord { Id = "s3", Pov = "cara", Cast = cast }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 }).Applied);
            return ContextCompiler.Compile(tiny.Story.State(), "s3").Text;
        }

        // Cara is alone in a scene, and knows nothing of the key. The fact is still so, and a draft has to be
        // shown it to keep from contradicting it.
        var text = ThirdSceneThroughCara(_tiny, "cara");
        Assert.Contains(
            "SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT (the author's alone: nobody here says it or acts on it, and nothing in the scene may contradict it)\n"
            + "  p_hid: Ann hid the key under the mat. [UNCONFIRMED]\n", text);
        Assert.DoesNotContain("Cara (point of view): does NOT know", text);

        // With Ann there too, it is under her name, and Cara, through whom the scene is told, is named as having nothing on it.
        using var two = new TinyStory();
        var both = ThirdSceneThroughCara(two, "cara", "ann");
        Assert.DoesNotContain("SO, AND NOBODY IN THIS SCENE", both);
        Assert.Contains(
            "Cara (point of view): does NOT know, and must not say it, be told it or act on it in this scene\n"
            + "  NO RECORD EITHER WAY, where someone else here holds a view. Nothing says they have it, so they do not say it or act on it:\n"
            + "    p_hid\n", both);
        Assert.Contains("  Not known to Cara, so not said, told or acted on: p_hid\n", both);
    }

    /// <summary>A scene that is still to be written is a marker with nothing under it, and its context says that and not a span of lines that runs backwards.</summary>
    [Fact]
    public void A_scene_with_no_text_yet_is_pointed_at_by_its_marker()
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"), "<!-- scene: s3 -->\n");
        Assert.True(_tiny.Put(new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 }).Applied);
        var text = ContextCompiler.Compile(_tiny.Story.State(), "s3").Text;
        Assert.Contains("  manuscript/03-three.md: this scene has no text yet. It goes under its marker, which is on line 1\n", text);
        Assert.Contains("  manuscript/02-two.md lines 2-4: the scene before it in the telling (s2)", text);
        Assert.DoesNotMatch(@"lines (\d+)-0\b", text);
    }

    /// <summary>
    /// A fact nobody present has a record on, which is about somebody elsewhere, is the least likely to be
    /// contradicted by the scene. It has a part of its own, the last to be taken: in a story of threads that
    /// never meet, such facts are most of the story's, and they are what is left out, by name, when the budget is short.
    /// </summary>
    [Fact]
    public void A_fact_with_no_record_here_that_is_about_someone_elsewhere_is_the_first_thing_left_out()
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"), "<!-- scene: s3 -->\n# Three\n\nCara waited in the hall.\n");
        Assert.True(_tiny.Put(
            new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
            new SceneRecord { Id = "s3", Pov = "cara", Cast = ["cara"], Place = "mat" }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 },
            new Proposition { Id = "p_ann_debt", Text = "Ann owes Ben money.", About = ["ann", "ben"] },
            new FactChange { Id = "f_debt", Prop = "p_ann_debt", Value = true, StoryKey = 5 },
            new Proposition { Id = "p_cara_kin", Text = "Cara is Ann's sister.", About = ["cara", "ann"] },
            new FactChange { Id = "f_kin", Prop = "p_cara_kin", Value = true },
            new Proposition { Id = "p_loose", Text = "The mat hides a loose board.", About = ["mat"] },
            new FactChange { Id = "f_loose", Prop = "p_loose", Value = true }).Applied);
        // A few more about people elsewhere, so that the part is worth more than the line that names it when it is left out.
        Assert.True(_tiny.Put(Enumerable.Range(1, 6).SelectMany(n => new LedgerRecord[]
        {
            new Proposition { Id = $"p_far{n}", Text = $"Ben keeps the spare key number {n} in a place only Ann has seen.", About = ["ben", "ann"] },
            new FactChange { Id = $"f_far{n}", Prop = $"p_far{n}", Value = true },
        }).ToArray()).Applied);
        var whole = ContextCompiler.Compile(_tiny.Story.State(), "s3");
        Assert.Empty(whole.Omitted);
        // About her, about the place she is in, or about nobody in particular: those touch the scene.
        Assert.Contains(
            "SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT (the author's alone: nobody here says it or acts on it, and nothing in the scene may contradict it)\n"
            + "  p_hid: Ann hid the key under the mat. [UNCONFIRMED]\n"
            + "  p_cara_kin: Cara is Ann's sister. [UNCONFIRMED]\n"
            + "  p_loose: The mat hides a loose board. [UNCONFIRMED]\n\n", whole.Text);
        Assert.Contains("ALSO SO, WITH NO RECORD HERE, AND ABOUT NOBODY AND NOTHING IN THIS SCENE (the author's alone)\n  p_ann_debt: Ann owes Ben money. [UNCONFIRMED]\n  p_far1: ", whole.Text);

        var tight = ContextCompiler.Compile(_tiny.Story.State(), "s3", whole.EstimatedTokens - 5);
        Assert.Equal("ALSO SO, WITH NO RECORD HERE, AND ABOUT NOBODY AND NOTHING IN THIS SCENE (the author's alone)", Assert.Single(tight.Omitted).Split(" (~")[0]);
        Assert.Contains("  p_cara_kin: Cara is Ann's sister.", tight.Text);
        Assert.DoesNotContain("Ann owes Ben money.", tight.Text);
        // What is left out of the text is still part of what the context stands for.
        Assert.Equal(whole.ManifestId, tight.ManifestId);

        // Over by a little, the least is given up: the smallest part whose absence is enough, not the last one.
        // Here Ann is in the room and holds a view on each of twenty facts, and the reader has a hint of each. The
        // reader's part comes last, and a smaller one is enough.
        using var crowded = new TinyStory(annInSceneTwo: true);
        Assert.True(crowded.Put(Enumerable.Range(1, 20).SelectMany(n => new LedgerRecord[]
        {
            new Proposition { Id = $"p_n{n}", Text = $"The house has a room numbered {n} that only Ann has ever been inside.", About = ["ann"] },
            new FactChange { Id = $"f_n{n}", Prop = $"p_n{n}", Value = true },
            new BeliefChange { Id = $"b_n{n}", Holder = "ann", Prop = $"p_n{n}", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 1, Channel = Channel.Background },
            new Disclosure { Id = $"d_n{n}", Scene = "s1", Prop = $"p_n{n}", Mode = DisclosureMode.Implied, ReaderStance = Stance.HoldsTrue, Strength = Strength.Suspects },
        }).ToArray()).Applied);
        var all = ContextCompiler.Compile(crowded.Story.State(), "s2");
        Assert.Empty(all.Omitted);
        var less = ContextCompiler.Compile(crowded.Story.State(), "s2", all.EstimatedTokens - 5);
        Assert.StartsWith("SINCE EACH PERSON WAS LAST ON STAGE", Assert.Single(less.Omitted));
        Assert.Contains("THE READER ON ENTRY (in reading order)\n", less.Text);
        Assert.Contains("Ann: beliefs on entry\n", less.Text);
        Assert.Contains("    p_n1: The house has a room numbered 1 that only Ann has ever been inside.", less.Text);
        Assert.True(less.EstimatedTokens <= less.Budget);
    }

    [Fact]
    public void A_change_sets_id_comes_from_the_file_as_written_not_from_the_programs_model_of_it()
    {
        var path = Directory.GetFiles(_tiny.Story.LedgerDir, "*.json").Single();
        var text = File.ReadAllText(path);
        var written = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(written["id"]!.GetValue<string>(), LedgerStore.IdFor(written));
        var loose = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Re-indented, it says the same thing and keeps its id.
        File.WriteAllText(path, written.ToJsonString(loose));
        Assert.Single(_tiny.Story.ReadLedger());

        // A file from a version of the program that did not write a field this one writes: here, "approval".
        // Its id was taken from what it says, so it still loads. Recomputing the id from the model would add the
        // field back and refuse every old file.
        var older = JsonNode.Parse(text)!.AsObject();
        foreach (var op in older["ops"]!.AsArray())
            Assert.True(op!.AsObject().Remove("approval"));
        var id = LedgerStore.IdFor(older);
        Assert.NotEqual(written["id"]!.GetValue<string>(), id);
        older["id"] = id;
        File.Delete(path);
        File.WriteAllText(Path.Combine(_tiny.Story.LedgerDir, LedgerStore.FileName(1, id)), older.ToJsonString(loose));
        Assert.Equal(id, _tiny.Story.ReadLedger().Single().ChangeSet.Id);
        Assert.Equal(Approval.Provisional, _tiny.Story.State().Model.ById["b_ann"].ApprovalAt(1));
        Assert.True(_tiny.Put(_tiny.BenBelief("b_next", 20.1)).Applied);

        // Changing what a file says is still caught, in the newest file too.
        var newest = Directory.GetFiles(_tiny.Story.LedgerDir, "*.json").Order(StringComparer.Ordinal).Last();
        File.WriteAllText(newest, File.ReadAllText(newest).Replace("\"author\"", "\"someone else\""));
        Assert.Contains("edited after it was written", Assert.Throws<LedgerException>(() => _tiny.Story.ReadLedger()).Message);
    }
}
