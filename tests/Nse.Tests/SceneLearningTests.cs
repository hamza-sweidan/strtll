using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// What a scene may teach, and what the next one is told about it. Ann knows where the key was hidden. In a
/// third scene she says so, to Ben, with Cara in the room; Dora is not there. Nobody planned it: the scene has
/// no contract. The scene has to be writable, what it did has to come back to the author, and the scene after
/// it has to be told three different things: Ben took it in, Cara heard the words, Dora was not there.
/// </summary>
public sealed class SceneLearningTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public SceneLearningTests()
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"),
            "<!-- scene: s3 -->\n# Three\n\n\"I hid it under the mat myself,\" said Ann. Ben looked at her. Cara went on shelling peas.\n");
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "04-four.md"), "<!-- scene: s4 -->\n# Four\n\nDora came in from the yard.\n");
        var put = _tiny.Put(
            new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
            new Entity { Id = "dora", Type = EntityKind.Character, Name = "Dora" },
            new SceneRecord { Id = "s3", Pov = "ben", Cast = ["ben", "ann", "cara"] }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 },
            new SceneRecord { Id = "s4", Pov = "ben", Cast = ["ben", "cara", "dora"] }, new SceneTime { Id = "t4", Scene = "s4", StoryKey = 40 });
        if (!put.Applied)
            throw new InvalidOperationException(string.Join("; ", put.Findings));
    }

    public void Dispose() => _tiny.Dispose();

    private static string Codes(SubmitResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private string Context(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).Text;

    private string Part(string scene, string title) => Context(scene).Split("\n" + title)[1].Split("\n\n")[0];

    private SubmitResult Submit(string scene, params LedgerRecord[] records) => _tiny.Story.SubmitScene(new SceneReport
    {
        Scene = scene, Manifest = ContextCompiler.Compile(_tiny.Story.State(), scene).ManifestId, Records = records,
    });

    private static Assertion AnnSaysSo => new()
    {
        Id = "a_says", Scene = "s3", StoryKey = 30.2, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben", "cara"], Prop = "p_hid",
        Quote = "I hid it under the mat myself",
    };

    private static BeliefChange BenTakesItIn => new()
    {
        Id = "b_ben_told", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 30.3, Scene = "s3",
        Channel = Channel.Told, Via = "a_says", Quote = "Ben looked at her.",
    };

    [Fact]
    public void Before_she_speaks_the_context_says_who_has_it_and_that_the_scene_may_give_it()
    {
        var text = Context("s3");
        // He does not have it on entry, she does, and the line says so.
        Assert.Contains(
            "Ben (point of view): NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n"
            + "  NO RECORD ON ENTRY, and someone else here has it:\n"
            + "    p_hid (Ann)\n", text);
        Assert.Contains("Ann: beliefs on entry\n  [knows] takes it as true, certain:\n    p_hid: Ann hid the key under the mat. [UNCONFIRMED] (b_ann*)\n", text);
        // Nothing forbids his being told: not having a thing on entry is where a scene starts from.
        Assert.DoesNotContain("be told it", text);
        Assert.DoesNotContain("must not say it", text);
        Assert.Contains("  What the scene may change: someone here who has a fact can tell it or show it, and whoever takes it in has it from then on and can answer it. That needs no plan.", text);
        // With no contract, that is said: what the scene does is recorded and goes to the author, it is not held to a plan.
        Assert.Contains("NO CONTRACT FOR THIS SCENE\n  Nothing is planned for it. Whatever it changes in what people know, in what the reader is given or in where things are is recorded when the scene is submitted, and goes to the author to keep or reject.\n", text);
        Assert.Contains("  Ben lacks on entry: p_hid\n", text);
    }

    [Fact]
    public void An_exchange_nobody_planned_is_recorded_and_comes_back_to_the_author()
    {
        var result = Submit("s3", AnnSaysSo, BenTakesItIn);
        Assert.True(result.Applied, Codes(result) + " " + string.Join("; ", result.Findings));
        // It stops nothing that there was no plan. It is said, and the records wait for the author.
        Assert.Contains(result.Notes, n => n.Code == "NO_CONTRACT");
        Assert.DoesNotContain(result.Notes, n => n.Code.StartsWith("CONTRACT_", StringComparison.Ordinal));
        var state = _tiny.Story.State();
        Assert.All(new[] { "a_says", "b_ben_told" }, id => Assert.Equal(Approval.Provisional, state.Model.ById[id].ApprovalAt(state.Model.Head)));
        var queue = ReviewReport.Queue(_tiny.Story.Review(), plain: state);
        Assert.Contains("a_says", queue);
        Assert.Contains("b_ben_told", queue);
    }

    [Fact]
    public void The_next_scene_is_told_who_took_it_in_who_only_heard_it_and_who_was_not_there()
    {
        Assert.True(Submit("s3", AnnSaysSo, BenTakesItIn).Applied);
        // A later draft of the scene is told what it is already recorded as doing, and the fact those lines name is
        // put in words there, where the ids are.
        Assert.Contains("  the facts these name:\n    p_hid: Ann hid the key under the mat.", Part("s3", "ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it)\n"));
        var text = Context("s4");

        // Ben took it in: it is his, from her words.
        Assert.Contains("Ben (point of view): beliefs on entry\n  [correctly believes] takes it as true, believes:\n    p_hid: Ann hid the key under the mat. [UNCONFIRMED] (b_ben_told*)\n", text);
        // Cara was in the room. She heard it said, and nothing records what she made of it: she can repeat Ann's
        // words, and the text cannot have her know it.
        Assert.Contains(
            "Cara: beliefs on entry\n"
            + "  [heard it said] with no record of what they made of it. They can repeat the words, not vouch for them:\n"
            + "    p_hid (said by Ann in s3: \"I hid it under the mat myself\", a_says*)\n", text);
        // Dora was not there. She has nothing, and needs someone who has it to tell her.
        Assert.Contains(
            "Dora: NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n"
            + "  WAS NOT THERE in scene 3, s3, where Ben came by these, and has no record on them:\n"
            + "    p_hid\n", text);
        Assert.Contains("Dora: beliefs on entry\n  no recorded beliefs yet", text);

        // Asked about one of them and the fact, the answer says the same, with the passage.
        var ben = StateReport.Text(_tiny.Story.State(), new StateQuery(Scene: "s4", Holder: "ben", Prop: "p_hid"));
        Assert.Contains("[correctly believes] takes it as true, believes", ben);
        Assert.Contains("      source: a_says, dialogue: Ann asserts p_hid to Ben, Cara. \"I hid it under the mat myself\" (s3, manuscript/03-three.md line 4)\n", ben);
        var cara = StateReport.Text(_tiny.Story.State(), new StateQuery(Scene: "s4", Holder: "cara", Prop: "p_hid"));
        Assert.Contains("  p_hid  no record. That is not the same as \"does not know\": nothing has been recorded either way.\n"
                        + "      heard it said: a_says, dialogue: Ann asserts p_hid to Ben, Cara. \"I hid it under the mat myself\" (s3, manuscript/03-three.md line 4)\n", cara);
        var dora = StateReport.Text(_tiny.Story.State(), new StateQuery(Scene: "s4", Holder: "dora", Prop: "p_hid"));
        Assert.Contains("  p_hid  no record. That is not the same as \"does not know\": nothing has been recorded either way.\n"
                        + "      was not there when it was said: a_says, in s3, by Ann to Ben, Cara\n", dora);
        Assert.DoesNotContain("heard it said", dora);
        // What Ann said to an empty hall in the first scene reached nobody, so it is no one's source and is not listed.
        Assert.DoesNotContain("a_deny", dora);
    }

    [Fact]
    public void A_plan_that_did_not_foresee_the_telling_does_not_stop_it_and_one_that_did_has_nothing_to_report()
    {
        // The author planned the scene, and not what Ann would say in it.
        Assert.True(_tiny.Put(new SceneContract { Id = "c_s3", Scene = "s3", Outline = "Ann and Ben in the kitchen, with Cara at the table." }).Applied);
        var text = Context("s3");
        Assert.DoesNotContain("NO CONTRACT FOR THIS SCENE", text);
        Assert.Contains("  Anything else of weight that the scene does to what people know, to what the reader is told or to where things are is not forbidden. It is reported to the author as unplanned.", text);
        Assert.Contains("  NO RECORD ON ENTRY, and someone else here has it:\n    p_hid (Ann)\n", text);

        var unforeseen = Submit("s3", AnnSaysSo, BenTakesItIn);
        Assert.True(unforeseen.Applied, Codes(unforeseen) + " " + string.Join("; ", unforeseen.Findings));
        var note = Assert.Single(unforeseen.Notes, n => n.Code.StartsWith("CONTRACT_", StringComparison.Ordinal));
        Assert.Equal("CONTRACT_UNPLANNED", note.Code);
        Assert.Equal("b_ben_told", note.RecordId);
        Assert.Contains("in s3, Ben (ben) comes to take p_hid as true, believes, and contract c_s3 does not plan it", note.Message);
        Assert.DoesNotContain(unforeseen.Notes, n => n.Code == "NO_CONTRACT");
        var state = _tiny.Story.State();
        Assert.Equal(Approval.Provisional, state.Model.ById["b_ben_told"].ApprovalAt(state.Model.Head));

        // The same exchange in a story whose author did plan it. It goes in the same way, and there is nothing to report.
        using var planned = new SceneLearningTests();
        Assert.True(planned._tiny.Put(new SceneContract
        {
            Id = "c_s3", Scene = "s3", Outline = "Ann tells Ben where the key was.",
            Intends = [new Intent { Change = IntentKind.Belief, Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue }],
        }).Applied);
        var foreseen = planned.Submit("s3", AnnSaysSo, BenTakesItIn);
        Assert.True(foreseen.Applied, Codes(foreseen) + " " + string.Join("; ", foreseen.Findings));
        Assert.DoesNotContain(foreseen.Notes, n => n.Code == "NO_CONTRACT" || n.Code.StartsWith("CONTRACT_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_view_someone_reasons_to_is_writable_is_recorded_as_theirs_and_the_next_scene_knows_it_is_wrong()
    {
        // Dora saw Ben with the mat up, and nothing else. In a scene nobody planned she works out, wrongly, that he hid the key.
        TheAccusation();

        // Nobody in the scene has what is so. The drafter is told that this stops nobody guessing, and how a guess is to be written.
        var text = Context("s5");
        Assert.Contains("  What someone works out is theirs: anyone here may reason from what they have to a view of something they lack, and may be wrong. Write it as their reasoning, on the grounds they have. The narration does not confirm it. Record it as inferred or assumed.\n", text);
        var alone = Part("s5", "SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT (the author's alone. Nobody here speaks or acts from knowing it; a guess at it, right or wrong, is the guesser's own. Nothing the narration gives as so may contradict it)\n");
        Assert.Contains("  p_ben_hid: It is NOT true that: Ben hid the key under the mat.", alone);

        var guess = new Assertion
        {
            Id = "a_guess", Scene = "s5", StoryKey = 50.1, Type = AssertionKind.Dialogue, Speaker = "dora", Addressees = ["ben", "cara"], Prop = "p_ben_hid",
            Quote = "It was Ben who hid it, then",
        };
        var view = new BeliefChange
        {
            Id = "b_dora", Holder = "dora", Prop = "p_ben_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 50.1, Scene = "s5",
            Channel = Channel.Inferred, Quote = "He had the mat up this morning.",
        };
        var result = Submit("s5", guess, view);
        Assert.True(result.Applied, Codes(result) + " " + string.Join("; ", result.Findings));
        Assert.Contains(result.Notes, n => n.Code == "NO_CONTRACT");

        // Her saying it has made nothing so.
        Assert.Equal("false", _tiny.Story.State().Canon("p_ben_hid"));
        // The scene after it is told that she has it wrong and acts on that, with what is so beside it.
        var next = Context("s6");
        Assert.Contains(
            "Dora: NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n"
            + "  BELIEVES THE OPPOSITE of what is so, and acts on it:\n"
            + "    p_ben_hid: It is NOT true that: Ben hid the key under the mat.", next);
        Assert.Contains("  [FALSE BELIEF] takes it as true, believes:\n    p_ben_hid", Part("s6", "Dora: beliefs on entry\n"));
        Assert.Contains("  Dora has these wrong, and acts on that: p_ben_hid\n", next);
        // Ben was accused to his face. He heard it, and nothing records what he made of it.
        var heard = Part("s6", "Ben (point of view): beliefs on entry\n").Split("  [heard it said] with no record of what they made of it. They can repeat the words, not vouch for them:\n")[1];
        Assert.StartsWith("    p_ben_hid: It is NOT true that: Ben hid the key under the mat.", heard);
        // Having heard it is where it is listed for him. It is not listed a second time as something he lacks.
        Assert.DoesNotContain("someone else here has it", next);
        Assert.DoesNotContain("Ben lacks on entry", next);
        Assert.Contains(" (said by Dora in s5: \"It was Ben who hid it, then\", a_guess*)", heard);
    }

    /// <summary>A fifth scene in which Dora says, wrongly, that Ben hid the key, and a sixth after it. It is not so, and nobody in either scene has what is so.</summary>
    private void TheAccusation()
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "05-five.md"),
            "<!-- scene: s5 -->\n# Five\n\n\"It was Ben who hid it, then,\" said Dora. \"He had the mat up this morning.\" Cara said nothing.\n");
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "06-six.md"), "<!-- scene: s6 -->\n# Six\n\nDora watched Ben all through supper.\n");
        Assert.True(_tiny.Put(
            new Proposition { Id = "p_ben_hid", Text = "Ben hid the key under the mat." },
            new FactChange { Id = "f_ben_hid", Prop = "p_ben_hid", Value = false, StoryKey = 10.1 },
            new SceneRecord { Id = "s5", Pov = "ben", Cast = ["ben", "cara", "dora"] }, new SceneTime { Id = "t5", Scene = "s5", StoryKey = 50 },
            new SceneRecord { Id = "s6", Pov = "ben", Cast = ["ben", "dora"] }, new SceneTime { Id = "t6", Scene = "s6", StoryKey = 60 }).Applied);
    }

    [Fact]
    public void A_planned_mistake_reads_as_a_mistake_wherever_the_fact_is_listed_and_an_open_mind_is_not_a_lack()
    {
        // The author plans that Dora comes to believe it. The plan words the fact first, as not so.
        TheAccusation();
        Assert.True(_tiny.Put(
            new SceneContract
            {
                Id = "c_s5", Scene = "s5", Outline = "Dora accuses Ben.",
                Intends = [new Intent { Change = IntentKind.Belief, Holder = "dora", Prop = "p_ben_hid", Stance = Stance.HoldsTrue }],
            },
            // Cara has heard the question put and has not made up her mind.
            new BeliefChange { Id = "b_cara", Holder = "cara", Prop = "p_ben_hid", Stance = Stance.Undecided, StoryKey = 45, Channel = Channel.Background }).Applied);
        var text = Context("s5");
        Assert.Contains("  intends: Dora (dora) comes to take p_ben_hid as true\n", text);
        Assert.Contains("  the facts it names:\n    p_ben_hid: It is NOT true that: Ben hid the key under the mat.", text);
        // Under Cara's name it is a question she has in mind, which the scene may settle for her: not a thing to be
        // kept from her, and not something she "lacks".
        var cara = Part("s5", "Cara: NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n");
        Assert.Contains("  NOT SETTLED what they make of it:\n    p_ben_hid: It is NOT true that: Ben hid the key under the mat.", cara);
        Assert.Contains("(recorded as undecided; they have not made up their mind, and the scene may move them)", cara);
        Assert.Contains("\n  Cara has not settled: p_ben_hid\n", text);
        Assert.DoesNotContain("Cara lacks on entry", text);
        Assert.DoesNotContain("treat as not knowing", text);
    }

    [Fact]
    public void What_has_just_become_so_stays_in_the_context_whoever_it_is_about_and_says_from_when()
    {
        // Three facts about Dora, who is not in the third scene. One has been so all along. One became so after the
        // scene Ben was last in. One becomes so while the third scene is going on.
        Assert.True(_tiny.Put(
            new Proposition { Id = "p_shop", Text = "Dora keeps the shop.", About = ["dora"] },
            new FactChange { Id = "f_shop", Prop = "p_shop", Value = true, StoryKey = 5 },
            new Proposition { Id = "p_left", Text = "Dora has left for the coast.", About = ["dora"] },
            new FactChange { Id = "f_left", Prop = "p_left", Value = true, StoryKey = 25 },
            new Proposition { Id = "p_back", Text = "Dora is back in the village.", About = ["dora"] },
            new FactChange { Id = "f_back", Prop = "p_back", Value = true, StoryKey = 30.5 }).Applied);

        // What is news stays with what must be there: the people in the scene are likely to be asking about it.
        var must = Part("s3", "SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT (the author's alone. Nobody here speaks or acts from knowing it; a guess at it, right or wrong, is the guesser's own. Nothing the narration gives as so may contradict it)\n");
        var left = Assert.Single(must.Split('\n'), l => l.StartsWith("  p_left: Dora has left for the coast.", StringComparison.Ordinal));
        Assert.EndsWith(" (so from story time 25)", left);
        Assert.Contains("  p_back (so from story time 30.5, which is within this scene)", must);
        Assert.DoesNotContain("p_shop", must);
        // What has long been so about someone who is not here is in the part that can be left out.
        Assert.Contains("  p_shop: Dora keeps the shop.", Part("s3", "ALSO SO, WITH NO RECORD HERE, AND ABOUT NOBODY AND NOTHING IN THIS SCENE (the author's alone)\n"));

        // A fact that turns during the scene is one of the things the scene is recorded as doing. It is dated there, and worded.
        var recorded = Part("s3", "ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it)\n");
        Assert.Contains("  f_back at 30.5: p_back becomes true", recorded);
        Assert.Contains("  the facts these name:\n    p_back: Dora is back in the village.", recorded);

        // Asked about on entry it is not so yet, and the answer says when it turns: whether it is so when someone speaks is the question.
        var state = _tiny.Story.State();
        Assert.Contains("Canon: true; not the case at this point: it becomes so at story time 30.5, which is within this scene.", StateReport.Text(state, new StateQuery(Scene: "s3", Prop: "p_back")));
        Assert.Contains("Canon: true; the case at this point.", StateReport.Text(state, new StateQuery(Scene: "s3", Prop: "p_left")));
        Assert.Contains("Canon: true; not the case at this point: it becomes so at story time 25.", StateReport.Text(state, new StateQuery(Scene: "s2", Prop: "p_left")));
    }

    [Fact]
    public void An_embargo_holds_in_a_scene_with_no_plan_and_a_hint_is_still_allowed()
    {
        Assert.True(_tiny.Put(new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s4" }).Applied);
        var text = Context("s3");
        Assert.Contains(
            "MUST NOT BE STATED TO THE READER IN THIS SCENE (the reader is first told later). A hint is allowed: the reader may be left suspecting it. Neither the narration nor anyone speaking may leave them sure of it\n"
            + "  p_hid: Ann hid the key under the mat. [UNCONFIRMED] (embargo em_hid, provisional but keep to it: not before scene 4, s4)\n", text);
        Assert.Contains("  Not to be stated to the reader, though it may be hinted: p_hid\n", text);
        Assert.DoesNotContain("Not to be given to the reader", text);

        Disclosure Reader(string id, DisclosureMode mode, Strength strength) => new()
        {
            Id = id, Scene = "s3", Prop = "p_hid", Mode = mode, ReaderStance = Stance.HoldsTrue, Strength = strength, Quote = "I hid it under the mat myself",
        };
        // Said aloud so that the reader is left sure: that is the reader being told, whoever says it.
        var stated = Submit("s3", AnnSaysSo, BenTakesItIn, Reader("d_sure", DisclosureMode.AssertedByCharacter, Strength.Certain));
        Assert.False(stated.Applied);
        Assert.Equal("EMBARGO_BROKEN", Codes(stated));
        // The words recorded as said, and nothing about the reader. The embargo is checked against what the reader
        // is recorded as given, so this account would pass it and give the fact away. It is sent back for that.
        var silent = Submit("s3", AnnSaysSo, BenTakesItIn);
        Assert.False(silent.Applied);
        Assert.Equal("EMBARGO_UNDECLARED", Codes(silent));
        Assert.Equal("a_says", silent.Findings[0].RecordId);
        Assert.Contains(
            "Ann (ann) asserts p_hid on the page in s3 (\"I hid it under the mat myself\"), embargo em_hid holds that back from the reader until s4, and no record says what the reader is given by those words. "
            + "Add a disclosure for p_hid in s3 that says how the reader takes them, so that the embargo is checked against it.", silent.Findings[0].Message);
        // The same exchange, with the reader left only suspecting, goes in.
        var hinted = Submit("s3", AnnSaysSo, BenTakesItIn, Reader("d_hint", DisclosureMode.AssertedByCharacter, Strength.Suspects));
        Assert.True(hinted.Applied, Codes(hinted) + " " + string.Join("; ", hinted.Findings));
        // What Ann said to the empty hall in the first scene denies it. That is misdirection, and nobody is asked about it.
        Assert.Empty(Lint.SaidUnderEmbargo(_tiny.Story.State()));

        // The author's own writes are not sent back. Where one leaves such a statement standing, lint names it.
        using var other = new SceneLearningTests();
        Assert.True(other._tiny.Put(AnnSaysSo, new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s4" }).Applied);
        var listed = Assert.Single(Lint.SaidUnderEmbargo(other._tiny.Story.State()));
        Assert.Equal("a_says", listed.RecordId);
        Assert.Contains("  EMBARGO_UNDECLARED a_says: Ann (ann) asserts p_hid on the page in s3", Lint.Text(other._tiny.Story.State()));
    }

    [Fact]
    public void A_secret_holds_in_a_scene_with_no_plan()
    {
        Assert.True(_tiny.Put(new Secret { Id = "se_hid", Props = ["p_hid"], KnownOnlyTo = ["ann"] }).Applied);
        Assert.Contains(
            "  SHUT OUT BY A SECRET the author keeps to the people named. The scene cannot give it to them:\n    p_hid (se_hid, kept to Ann)",
            Part("s3", "Ben (point of view): NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n"));
        var told = Submit("s3", AnnSaysSo, BenTakesItIn);
        Assert.False(told.Applied);
        Assert.Equal("SECRET_BROKEN", Codes(told));
    }
}
