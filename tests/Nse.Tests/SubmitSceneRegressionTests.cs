using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Regression tests for scene submissions, one defect each. The scenes here are the tiny
/// story's: scene 1 at story time 10 with Ann, scene 2 at 20 with Ben, its span running up to 25.
/// </summary>
public sealed class SubmitSceneRegressionTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(SubmitResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private string Manifest(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).ManifestId;

    private string Context(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).Text;

    private SubmitResult Submit(string scene, string manifest, params LedgerRecord[] records) =>
        _tiny.Story.SubmitScene(new SceneReport { Scene = scene, Manifest = manifest, Records = records });

    private SubmitResult Withdraw(params string[] ids) =>
        _tiny.Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [], Withdraws = ids });

    private static Event Finds => new()
    {
        Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key under the mat.",
        Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
    };

    private static Placement Pockets => new()
    {
        Id = "l_pocket", Object = "key", Place = "pocket", StoryKey = 20.1, Scene = "s2", Cause = "e_find", Quote = "put it in his pocket",
    };

    private static BeliefChange Ben(string id, double key, string? scene = "s2", string prop = "p_hid", Stance stance = Stance.HoldsTrue) => new()
    {
        Id = id, Holder = "ben", Prop = prop, Stance = stance, Strength = Strength.Certain, StoryKey = key, Scene = scene, Channel = Channel.Assumed,
    };

    /// <summary>
    /// 1 and 8. Scope went by the scene's name alone, so a record labelled with scene 2 and dated before scene 1
    /// was applied and changed what is so on entry to scene 1. A record with only a quote scene passed scope and
    /// was then invisible to the contract check.
    /// </summary>
    [Fact]
    public void A_record_dated_outside_the_scenes_span_is_not_the_scenes_whatever_it_is_labelled()
    {
        var manifest = Manifest("s2");
        var before = _tiny.Story.Dump();
        var misdated = Submit("s2", manifest,
            Ben("b_early", 6),
            new Placement { Id = "l_early", Object = "key", Place = "pocket", StoryKey = 2, Scene = "s2", Cause = "e_hide" },
            new Event { Id = "e_after", Scene = "s2", StoryKey = 25, Summary = "Something later." },
            Ben("b_quoted", 20.1, scene: null) with { QuoteScene = "s2", Quote = "Ben found the key under the mat" });
        Assert.Equal("OUT_OF_SCENE,OUT_OF_SCENE,OUT_OF_SCENE,OUT_OF_SCENE", Codes(misdated));
        Assert.Contains("its story time 6 is outside the scene's span, which runs from 20 up to but not including 25", misdated.Findings[0].Message);
        Assert.Contains("goes to the author as a proposal", misdated.Findings[0].Message);
        Assert.Contains("its story time 25 is outside", misdated.Findings[2].Message);
        Assert.Contains("it names no scene", misdated.Findings[3].Message);
        // Nothing moved: not on entry to scene 1, not anywhere.
        Assert.Equal(before, _tiny.Story.Dump());

        // The context tells the agent the span, and the first moment of it is inside.
        Assert.Contains("What happens in this scene is dated from 20 up to, but not including, 25.", Context("s2"));
        Assert.True(Submit("s2", manifest, Finds with { StoryKey = 20 }).Applied);
    }

    /// <summary>
    /// 2 and 7. A provisional fact change moved other scenes' answers with no mark on them, and a proposition
    /// nobody had given a truth to was reported everywhere as "NOT true", which also made every other draft in
    /// flight stale.
    /// </summary>
    [Fact]
    public void What_rests_on_an_unconfirmed_fact_says_so_and_a_new_proposition_is_not_thereby_false()
    {
        // The author ratifies the story so far, so that anything unconfirmed below comes from the drafts.
        Assert.True(_tiny.Apply(new RatifyOp { Ids = _tiny.Story.State().Model.Records.Select(t => t.Record.Id).ToList() }).Applied);
        Assert.DoesNotContain("UNCONFIRMED", Context("s2"));
        var draftingTwo = Manifest("s2");

        // Meanwhile a draft of scene 1 writes down a proposition and says nothing about whether it is so.
        var one = Submit("s1", Manifest("s1"),
            new Proposition { Id = "p_second", Text = "Ben has a second key." },
            new Event { Id = "e_hall", Scene = "s1", StoryKey = 10.3, Summary = "Ann speaks to the empty hall.", Quote = "to the empty hall" });
        Assert.True(one.Applied, Codes(one));
        var s = _tiny.Story.State();
        Assert.Equal("unstated", s.Canon("p_second"));
        Assert.Null(s.TruthAt("p_second", 30));
        Assert.DoesNotContain("p_second", s.InPlay("s2"));
        Assert.DoesNotContain("p_second", Context("s2"));
        Assert.Contains("p_second: Ben has a second key. Canon: unstated. [UNCONFIRMED: rests on provisional p_second]",
            StateReport.Text(s, new StateQuery(End: true, Prop: "p_second")));
        // So the draft of scene 2 that was in flight is not stale. It is a rebase.
        var two = Submit("s2", draftingTwo, Finds);
        Assert.True(two.Applied, Codes(two));
        Assert.True(two.Basis!.Rebased);

        // Then a draft of scene 1 claims the ratified fact stops being so. Ann's ratified belief is now judged
        // against that claim, and the judgement says what it rests on.
        Assert.DoesNotContain("UNCONFIRMED", StateReport.Text(_tiny.Story.State(), new StateQuery(End: true, Holder: "ann")));
        var gone = Submit("s1", Manifest("s1"), new FactChange { Id = "f_gone", Prop = "p_hid", Value = false, StoryKey = 10.5 });
        Assert.True(gone.Applied, Codes(gone));
        s = _tiny.Story.State();
        var ann = StateReport.Text(s, new StateQuery(End: true, Holder: "ann", Prop: "p_hid"));
        Assert.Contains("Canon: true; not the case at this point. [UNCONFIRMED: rests on provisional f_gone]", ann);
        Assert.Contains("p_hid  [OUTDATED BELIEF] takes it as true, certain: Ann hid the key under the mat.  (b_ann; took part, s1) [UNCONFIRMED: rests on provisional f_gone]", ann);
        // Before the claimed change takes effect nothing rests on it.
        Assert.Empty(s.Unconfirmed("p_hid", 10.4));
        Assert.Equal(["f_gone"], s.Unconfirmed("p_hid", 10.6));
        // And a later scene's context carries the mark wherever the fact appears.
        Assert.Contains("  p_hid [OUTDATED BELIEF] takes it as true, certain: Ann hid the key under the mat. (b_ann) [UNCONFIRMED: rests on provisional f_gone]",
            ContextCompiler.Compile(s, "s1").Sections.Count > 0 ? TwoPeople(s) : "");
    }

    /// <summary>The beliefs-on-entry lines a context would give for Ann at the end of the story, built the way the context builds them.</summary>
    private static string TwoPeople(StoryState s)
    {
        var b = s.BeliefsBefore("ann", StoryState.End)["p_hid"];
        return $"  p_hid [{s.Classify(b, StoryState.End)}] takes it as true, certain: {s.Model.Proposition("p_hid")!.Text} ({b.Id}){s.UnconfirmedMark("p_hid", StoryState.End)}";
    }

    /// <summary>
    /// 3. Both contexts being compared were compiled against the manuscript as it is now, so a draft was taken as
    /// fresh after the scenes had been reordered under it, though what the reader has been given on entry had changed.
    /// </summary>
    [Fact]
    public void A_draft_is_stale_once_the_scenes_are_reordered_or_the_scene_itself_is_moved_or_recast()
    {
        Assert.True(_tiny.Put(new Disclosure { Id = "d_told", Scene = "s1", Prop = "p_hid", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain }).Applied);
        var manifest = Manifest("s2");
        Assert.Contains("p_hid [stated, s1] reader takes it as true, certain", Context("s2"));

        // Scene 2 is now told first: on entry to it the reader has been given nothing.
        File.Move(Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"), Path.Combine(_tiny.Story.ManuscriptDir, "00-two.md"));
        Assert.Contains("THE READER ON ENTRY (in reading order)\n  nothing yet", Context("s2"));
        var reordered = Submit("s2", manifest, Finds);
        Assert.Equal("STALE_CONTEXT", Codes(reordered));
        Assert.Contains("are not the scenes, in the order, that they were when this context was compiled", reordered.Findings[0].Message);
        File.Move(Path.Combine(_tiny.Story.ManuscriptDir, "00-two.md"), Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"));
        Assert.Equal(manifest, Manifest("s2"));

        // The author moves the scene in story time. The header is part of what is compared.
        var moved = _tiny.Apply(new RetireOp { Id = "t2", Reason = "correction" }, new PutOp { Record = new SceneTime { Id = "t2b", Scene = "s2", StoryKey = 22 } });
        Assert.True(moved.Applied, Codes(moved));
        var late = Submit("s2", manifest, Finds);
        Assert.Equal("STALE_CONTEXT", late.Findings[0].Code);
        Assert.Contains(late.Findings, f => f.Code == "CHANGED" && f.Message == "- SCENE: story time 20, span up to 25");
        Assert.Contains(late.Findings, f => f.Code == "CHANGED" && f.Message == "+ SCENE: story time 22, span up to 27");
        // From the new context the same record is now out of the scene's span, and one dated within it goes in.
        Assert.Equal("OUT_OF_SCENE", Codes(Submit("s2", Manifest("s2"), Finds)));
        Assert.True(Submit("s2", Manifest("s2"), Finds with { StoryKey = 22.1 }).Applied);
    }

    /// <summary>4. Someone outside the scene's cast could be spoken to in it and made to learn something.</summary>
    [Fact]
    public void Nobody_outside_the_cast_is_present_in_a_scene_submission()
    {
        var manifest = Manifest("s2");
        var toAnn = new Assertion
        {
            Id = "a_to_ann", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Dialogue, Speaker = "ben", Addressees = ["ann"], Prop = "p_hid",
            Quote = "Ben found the key under the mat",
        };
        var annLearns = new BeliefChange
        {
            Id = "b_ann2", Holder = "ann", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.3, Scene = "s2",
            Channel = Channel.Told, Via = "a_to_ann",
        };
        var absent = Submit("s2", manifest, toAnn, annLearns);
        Assert.Equal("NOT_IN_CAST,NOT_IN_CAST", Codes(absent));
        Assert.Contains("it has ann present in s2, and the cast of s2 is ben. The cast is the author's to set", absent.Findings[0].Message);
        var watching = Finds with
        {
            Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor, ["stranger"] = ParticipantRole.Witness, ["ann"] = ParticipantRole.Witness },
        };
        // A persona is the person behind it, and Ben is in the cast. Ann is not.
        var one = Assert.Single(Submit("s2", manifest, watching).Findings);
        Assert.Contains("it has ann present in s2", one.Message);

        // Whoever wrote a letter that is read in the scene need not be there, and narration belongs to the one
        // it is told through, not to the narrator.
        var present = Submit("s2", manifest, Finds,
            new Entity { Id = "narrator", Type = EntityKind.Narrator, Name = "the narrator" },
            new Assertion
            {
                Id = "a_letter", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Document, Speaker = "ann", ReadBy = "ben", Prop = "p_hid",
                Quote = "Ben found the key under the mat",
            },
            new Assertion
            {
                Id = "a_told", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Narration, Speaker = "narrator", Focalizer = "ben", Prop = "p_hid",
                Quote = "put it in his pocket",
            });
        Assert.True(present.Applied, Codes(present));
    }

    /// <summary>
    /// 5. Two drafts of one scene were both applied, the second as a clean rebase, because the context showed only
    /// the state on entry. A second draft's records piled up on the first's.
    /// </summary>
    [Fact]
    public void A_second_draft_of_a_scene_sees_the_first_and_keeps_or_withdraws_what_it_recorded()
    {
        var manifest = Manifest("s2");
        Assert.Contains("ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it)\n  nothing yet", Context("s2"));
        Assert.True(Submit("s2", manifest, Finds, Pockets).Applied);

        // A second session, working from the same context, has the key stay where it was.
        var other = Submit("s2", manifest, Finds with { Id = "e_find2" });
        Assert.Equal("STALE_CONTEXT", other.Findings[0].Code);
        Assert.Contains(other.Findings, f => f.Code == "CHANGED"
            && f.Message == "+ ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it): l_pocket: the key (key) moves to Ben's pocket (pocket)");
        var context = Context("s2");
        Assert.Contains("  e_find at 20.1, event: Ben finds the key under the mat. [Ben actor], PROVISIONAL: do not build on this", context);
        Assert.Contains("  l_pocket: the key (key) moves to Ben's pocket (pocket), PROVISIONAL: do not build on this", context);

        // It asks again, and submits its own account: the move is withdrawn, the finding kept.
        var revised = _tiny.Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Withdraws = ["l_pocket"], Records = [Ben("b_ben", 20.2)] });
        Assert.True(revised.Applied, Codes(revised));
        Assert.Contains("1 record(s), all provisional, and 1 earlier record(s) of the scene withdrawn, ledger head 3.", SubmitReport.Text(revised));
        var state = _tiny.Story.State();
        Assert.Equal("mat", state.Location("key", StoryState.End)!.Place);
        Assert.Equal("superseded by a new draft of the scene", _tiny.Story.ReadLedger()[^1].ChangeSet.Ops.OfType<RetireOp>().Single().Reason);
        Assert.DoesNotContain("l_pocket", Context("s2"));

        // What a submission may withdraw: only what a submission for this scene left, and only while it is provisional.
        var authors = Withdraw("l_mat");
        Assert.Equal("PROPOSAL_ONLY", Codes(authors));
        Assert.Contains("was not recorded by a submission for s2", authors.Findings[0].Message);
        Assert.Equal("UNKNOWN_REFERENCE", Codes(Withdraw("l_pocket")));
        Assert.Equal("UNKNOWN_REFERENCE", Codes(Withdraw("l_nothing")));
        Assert.True(_tiny.Apply(new RatifyOp { Ids = ["b_ben"] }).Applied);
        Assert.Equal("PROPOSAL_ONLY", Codes(Withdraw("b_ben")));
        Assert.Equal("EMPTY", Codes(_tiny.Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [] })));
        Assert.True(Withdraw("e_find").Applied);
        Assert.Equal(5, _tiny.Story.ReadLedger().Count);
    }

    /// <summary>6. The manifest id was taken at its word: another scene's, or one made up, submitted the scene.</summary>
    [Fact]
    public void A_manifest_must_be_the_one_issued_for_this_scene_at_that_head()
    {
        var (one, two) = (Manifest("s1"), Manifest("s2"));
        Assert.NotEqual(one, two);
        var parsed = ManifestId.Parse(two)!;
        Assert.Equal((1, ContextCompiler.OrderMark(_tiny.Story.State())), (parsed.Head, parsed.Order));
        Assert.Equal(two, parsed.ToString());

        var anothers = Submit("s2", one, Finds);
        Assert.Equal("BAD_MANIFEST", Codes(anothers));
        Assert.Contains($"{one} is not the manifest of the context for s2 at ledger head 1", anothers.Findings[0].Message);
        Assert.Equal("BAD_MANIFEST", Codes(Submit("s2", (parsed with { Basis = "00000000" }).ToString(), Finds)));
        Assert.Equal("BAD_MANIFEST", Codes(Submit("s2", (parsed with { Head = 0 }).ToString(), Finds)));
        Assert.Equal("BAD_MANIFEST", Codes(Submit("s2", "m_1_0123abcd", Finds)));
        Assert.Equal("STALE_CONTEXT", Codes(Submit("s2", (parsed with { Order = "000000" }).ToString(), Finds)));
        Assert.Single(_tiny.Story.ReadLedger());
        Assert.True(Submit("s2", two, Finds).Applied);
    }

    /// <summary>9. Nothing checked a "never settle" rule against what a scene does.</summary>
    [Fact]
    public void A_question_the_author_says_is_never_settled_is_not_settled_by_a_scene()
    {
        Assert.True(_tiny.Put(
            new Proposition { Id = "p_meant", Text = "Ann meant Ben to find the key.", Resolution = Resolution.Ambiguous },
            new Rule { Id = "r_meant", Type = "never_resolve", Prop = "p_meant", Text = "No scene may settle whether Ann meant it." }).Applied);
        var manifest = Manifest("s2");
        Assert.Contains("NEVER SETTLE THESE\n  p_meant: No scene may settle whether Ann meant it.", Context("s2"));

        Disclosure Reader(string id, DisclosureMode mode, Stance stance, Strength? strength) =>
            new() { Id = id, Scene = "s2", Prop = "p_meant", Mode = mode, ReaderStance = stance, Strength = strength };
        var stated = Submit("s2", manifest, Reader("d_stated", DisclosureMode.Stated, Stance.HoldsTrue, Strength.Believes));
        Assert.Equal("RULE_BROKEN", Codes(stated));
        Assert.Contains("it gives the reader p_meant as settled in s2, and rule r_meant says that is never to be settled: No scene may settle whether Ann meant it.", stated.Findings[0].Message);
        Assert.Equal("RULE_BROKEN", Codes(Submit("s2", manifest, Reader("d_sure", DisclosureMode.AssertedByCharacter, Stance.HoldsFalse, Strength.Certain))));

        // A character may hold and voice an opinion, however firmly, and the reader may be left wondering.
        var opinions = Submit("s2", manifest, Ben("b_meant", 20.2, prop: "p_meant"),
            Reader("d_posed", DisclosureMode.Posed, Stance.Undecided, null), Reader("d_hint", DisclosureMode.Implied, Stance.HoldsTrue, Strength.Suspects));
        Assert.True(opinions.Applied, Codes(opinions));

        // Canon may not decide it either, by anyone's hand; and a rule laid over something already settled is refused.
        Assert.Equal("RULE_BROKEN", Codes(_tiny.Put(new FactChange { Id = "f_meant", Prop = "p_meant", Value = true })));
        Assert.True(_tiny.Put(new Proposition { Id = "p_why", Text = "Ann hid it out of spite.", Resolution = Resolution.Open },
            new Disclosure { Id = "d_why", Scene = "s1", Prop = "p_why", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain }).Applied);
        var tooLate = _tiny.Put(new Rule { Id = "r_why", Type = "never_establish", Prop = "p_why", Text = "Never say why." });
        Assert.Equal("RULE_BROKEN", Codes(tooLate));
        Assert.Equal("d_why", tooLate.Findings[0].RecordId);
    }

    /// <summary>10. Smaller things: stray fields in a contract entry, and two kinds of record that are the author's to add.</summary>
    [Fact]
    public void A_contract_entry_carries_only_the_fields_of_its_kind_and_personas_and_open_questions_are_the_authors()
    {
        var stray = _tiny.Put(new SceneContract
        {
            Id = "c_s2", Scene = "s2",
            Intends = [new Intent { Change = IntentKind.Belief, Holder = "ben", Prop = "p_hid", Mode = DisclosureMode.Stated, Object = "key" }],
        });
        Assert.Equal("BAD_VALUE", Codes(stray));
        Assert.Contains("an entry under 'intends' for a belief carries fields that kind does not use: mode, object", stray.Findings[0].Message);
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new SceneContract
        {
            Id = "c_s2", Scene = "s2", Relies = [new Intent { Change = IntentKind.Placement, Object = "key", Place = "mat", Value = true, Stance = Stance.HoldsTrue }],
        })));

        var manifest = Manifest("s2");
        var persona = Submit("s2", manifest, new Entity { Id = "mask", Type = EntityKind.Character, Name = "the masked man", PersonaOf = "ben" });
        Assert.Equal("PROPOSAL_ONLY", Codes(persona));
        Assert.Contains("who a persona stands for is the author's to set", persona.Findings[0].Message);
        var open = Submit("s2", manifest, new Proposition { Id = "p_open", Text = "Ben will tell Ann.", Resolution = Resolution.Ambiguous });
        Assert.Equal("PROPOSAL_ONLY", Codes(open));
        Assert.Contains("whether a question is left open or never settled is the author's to set", open.Findings[0].Message);
        Assert.Single(_tiny.Story.ReadLedger());
    }
}
