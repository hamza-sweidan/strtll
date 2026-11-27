using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Writing on from what the author has not reviewed yet. Ann tells Ben where the key was hidden, with Cara in
/// the room, in a scene nobody has ratified. The next scene is written straight on: Ben tells Dora, Cara repeats
/// Ann's words, Dora believes Ben. Then the author decides about the first scene. If they keep it, the second is
/// as good as it was. If they reject what Ann said, they are shown the passages of the second that rested on it,
/// each with the reason. If they reject something else from that scene, the second is left alone.
/// </summary>
public sealed class UnreviewedTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    private const string Four =
        "<!-- scene: s4 -->\n# Four\n\nDora came in from the yard.\n\n\"It was under the mat all along,\" Ben told her. \"Ann put it there.\"\n\n"
        + "\"She said so herself,\" said Cara.\n\nDora stared at the pair of them. \"Are those peas for supper?\"\n";

    public UnreviewedTests()
    {
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"),
            "<!-- scene: s3 -->\n# Three\n\n\"I hid it under the mat myself,\" said Ann. Ben looked at her. Cara went on shelling peas.\n");
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "04-four.md"), Four);
        var put = _tiny.Put(
            new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
            new Entity { Id = "dora", Type = EntityKind.Character, Name = "Dora" },
            new SceneRecord { Id = "s3", Pov = "ben", Cast = ["ben", "ann", "cara"] }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 },
            new SceneRecord { Id = "s4", Pov = "ben", Cast = ["ben", "cara", "dora"] }, new SceneTime { Id = "t4", Scene = "s4", StoryKey = 40 },
            new Proposition { Id = "p_peas", Text = "The peas are for supper.", About = ["cara"] },
            new FactChange { Id = "f_peas", Prop = "p_peas", Value = true, StoryKey = 1 });
        if (!put.Applied)
            throw new InvalidOperationException(string.Join("; ", put.Findings));
    }

    public void Dispose() => _tiny.Dispose();

    private string Manifest(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).ManifestId;

    private string Context(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).Text;

    private SubmitResult Submit(string scene, string manifest, params LedgerRecord[] records) =>
        _tiny.Story.SubmitScene(new SceneReport { Scene = scene, Manifest = manifest, Records = records });

    private static string Notes(SubmitResult r) => string.Join(" || ", r.Findings.Concat(r.Notes).Select(f => f.ToString()));

    /// <summary>What Ann says and what Ben makes of it, and one thing in the same scene that has nothing to do with the key.</summary>
    private SubmitResult SubmitThree() => Submit("s3", Manifest("s3"),
        new Assertion
        {
            Id = "a_says", Scene = "s3", StoryKey = 30.2, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben", "cara"], Prop = "p_hid",
            Quote = "I hid it under the mat myself",
        },
        new BeliefChange
        {
            Id = "b_ben_told", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 30.3, Scene = "s3",
            Channel = Channel.Told, Via = "a_says", Quote = "Ben looked at her.",
        },
        new BeliefChange
        {
            Id = "b_ben_peas", Holder = "ben", Prop = "p_peas", Stance = Stance.HoldsTrue, Strength = Strength.Suspects, StoryKey = 30.4, Scene = "s3",
            Channel = Channel.Inferred, Directness = Directness.Inferred, Quote = "Cara went on shelling peas.",
        });

    /// <summary>The next scene, written on from that: Ben passes it on, Cara repeats the words, Dora takes Ben's word.</summary>
    private static LedgerRecord[] Four4 =>
    [
        new Assertion
        {
            Id = "a_ben_tells", Scene = "s4", StoryKey = 40.2, Type = AssertionKind.Dialogue, Speaker = "ben", Addressees = ["dora"],
            Prop = "p_hid", Quote = "It was under the mat all along",
        },
        new Assertion
        {
            Id = "a_cara_repeats", Scene = "s4", StoryKey = 40.3, Type = AssertionKind.Dialogue, Speaker = "cara", Addressees = ["dora"], Prop = "p_hid",
            Quote = "She said so herself",
        },
        new BeliefChange
        {
            Id = "b_dora_told", Holder = "dora", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 40.4, Scene = "s4",
            Channel = Channel.Told, Via = "a_ben_tells", Quote = "Dora stared at the pair of them.",
        },
    ];

    [Fact]
    public void The_next_scene_is_told_to_write_on_from_what_is_not_reviewed_yet_and_still_sees_that_it_is_not()
    {
        Assert.True(SubmitThree().Applied);
        var text = Context("s4");
        // The mark stays on every record nobody has reviewed.
        Assert.Contains("p_hid: Ann hid the key under the mat. [UNCONFIRMED] (b_ben_told*)", text);
        Assert.Contains("a_says*)", text);
        // And it no longer says to keep off them: a session writing the next scene has nothing else to write from.
        Assert.Contains("  * after an id: PROVISIONAL, a record the author has not reviewed yet. UNCONFIRMED: whether the fact is so rests on such a record. Both are the story as it stands: write on from them.", text);
        Assert.DoesNotContain("Do not build on either", text);
        Assert.DoesNotContain("IN QUESTION", text);
        Assert.Contains("provisional record(s). They are the story as it stands until the author decides: write on from them.", ContextCompiler.Brief(_tiny.Story.State()));
        Assert.Contains("UNCONFIRMED rests on such a record: write on from both.", Nse.Cli.McpHost.Instructions(ratifyTool: false));
        Assert.Contains("IN QUESTION: what the record rested on is gone, so build nothing on it.", Nse.Cli.McpHost.Instructions(ratifyTool: true));

        // The scene written from it goes in, provisional on provisional, with nothing said against it.
        var next = Submit("s4", Manifest("s4"), Four4);
        Assert.True(next.Applied, Notes(next));
        Assert.DoesNotContain(next.Notes, n => n.Code is Standing.InQuestion or Standing.LostSupport or "PUT_IN_QUESTION");
        // What each of its records rests on is read off the records: nobody linked them.
        var standing = Standing.Of(_tiny.Story.State());
        Assert.Equal([new Ground("b_ben_told", "told via a_says")], standing.RestsOn("a_ben_tells"));
        Assert.Equal([new Ground("a_says", "said by Ann in s3")], standing.RestsOn("a_cara_repeats"));
        Assert.Equal([new Ground("a_ben_tells", "the source it came from")], standing.RestsOn("b_dora_told"));
        Assert.Empty(standing.All);
        Assert.Contains("Nothing needs attention", Standing.Text(_tiny.Story.State()));
    }

    [Fact]
    public void Accepting_it_later_leaves_the_next_scene_as_it_was_whether_it_is_submitted_or_still_being_written()
    {
        var three = SubmitThree();
        // A session is in the middle of the next scene: it has taken its context and not submitted yet.
        var begun = Manifest("s4");
        var decided = _tiny.Story.Decide(new Decision { Accept = ["a_says", "b_ben_told", "b_ben_peas"] });
        Assert.True(decided.Done, string.Join("; ", decided.Findings));
        Assert.Empty(decided.Plan!.Consequences);
        Assert.DoesNotContain("This decision", ReviewReport.Decided(decided));

        // The draft goes in on the context it was written from. Nothing it was told has changed.
        var next = Submit("s4", begun, Four4);
        Assert.True(next.Applied, Notes(next));
        Assert.True(next.Basis!.Rebased);
        Assert.DoesNotContain(next.Notes, n => n.Code is "CONTEXT_MOVED" or "PUT_IN_QUESTION" or Standing.InQuestion or Standing.LostSupport);
        Assert.Empty(Standing.Of(_tiny.Story.State()).All);
        Assert.Equal(Approval.Ratified, _tiny.Story.State().Model.ById["a_says"].ApprovalAt(_tiny.Story.State().Model.Head));
        Assert.NotNull(three.ChangeSet);
    }

    [Fact]
    public void Rejecting_it_shows_the_later_passages_that_relied_on_her_words_and_why()
    {
        var three = SubmitThree();
        Assert.True(Submit("s4", Manifest("s4"), Four4).Applied);

        // What Ann said cannot be rejected and the belief that names it left standing: that is said, and nothing is done.
        var alone = _tiny.Story.Decide(new Decision { Reject = ["a_says"] });
        Assert.False(alone.Done);
        Assert.Contains(alone.Findings, f => f is { Code: "DANGLING_REFERENCE", RecordId: "b_ben_told" });

        // The whole of what the scene recorded, then. Before anything is written the author is shown what goes with it.
        string? asked = null;
        var decided = _tiny.Story.Decide(new Decision { Reject = [three.ChangeSet!.ChangeSet.Id], Note = "she would not say it yet" },
            plan => { asked = ReviewReport.Plan(plan); return true; });
        Assert.True(decided.Done, string.Join("; ", decided.Findings));
        var head = _tiny.Story.State().Model.Head;
        var expected =
            "    leaves 3 record(s) of what is already written without what they rested on. Each passage needs a look:\n"
            + "      a_ben_tells · s4, manuscript/04-four.md line 6\n"
            + "        dialogue at 40.2 in s4: Ben asserts p_hid to Dora · \"It was under the mat all along\"\n"
            + "        the fact: p_hid: Ann hid the key under the mat.\n"
            + $"        LOST ITS SUPPORT: Ben had p_hid from b_ben_told (told via a_says), rejected in change set {head}. Nothing else on record gives it to Ben by story time 40.2.\n"
            + "      a_cara_repeats · s4, manuscript/04-four.md line 8\n"
            + "        dialogue at 40.3 in s4: Cara asserts p_hid to Dora · \"She said so herself\"\n"
            + "        the fact: p_hid: Ann hid the key under the mat.\n"
            + $"        LOST ITS SUPPORT: Cara had p_hid from a_says (said by Ann in s3), rejected in change set {head}. Nothing else on record gives it to Cara by story time 40.3.\n"
            + "      b_dora_told · s4, manuscript/04-four.md line 10\n"
            + "        belief at 40.4 in s4: Dora comes to take p_hid as true, believes · told via a_ben_tells · stated · \"Dora stared at the pair of them.\"\n"
            + "        the fact: p_hid: Ann hid the key under the mat.\n"
            + $"        IN QUESTION: it rests on a_ben_tells (the source it came from). That traces back through a_ben_tells, where the trouble starts: lost its support: Ben had p_hid from b_ben_told (told via a_says), rejected in change set {head}. Nothing else on record gives it to Ben by story time 40.2.\n";
        Assert.Contains("  and all of it together:\n" + expected, asked);
        // It is said again once it is done, for whoever carried the decision out without seeing the question.
        Assert.Contains("This decision\n" + expected.Replace("\n      ", "\n    ").Replace("\n        ", "\n      ")[2..], ReviewReport.Decided(decided));

        // And it can be asked for at any time afterwards, for the story or for the scene.
        var state = _tiny.Story.State();
        var report = Standing.Text(state);
        Assert.Contains("ATTENTION · ledger head " + head, report);
        Assert.Contains("\nScene 4, s4 (3):\n  a_ben_tells · s4, manuscript/04-four.md line 6\n", report);
        Assert.Equal(report.Replace("ledger head " + head, "ledger head " + head + " · s4 alone"), Standing.Text(state, "s4"));
        Assert.Contains("Nothing in s3 needs attention", Standing.Text(state, "s3"));
        Assert.Contains("In question: 3 record(s) in s4 no longer have what they rested on.", ContextCompiler.Brief(state));
        var (printed, errors) = (new StringWriter(), new StringWriter());
        Assert.Equal(0, Nse.Cli.Cli.Run(["attention", "--scene", "s4", "--dir", _tiny.Root], printed, errors));
        Assert.Equal(Standing.Text(state, "s4"), printed.ToString().Replace("\r\n", "\n"));
        Assert.Equal(1, Nse.Cli.Cli.Run(["attention", "--scene", "s9", "--dir", _tiny.Root], new StringWriter(), errors));
        Assert.Contains("No scene record s9.", errors.ToString());

        // Whoever revises the scene is told in its context, by record and by passage, and told to build on none of it.
        var context = Context("s4");
        Assert.Contains(
            "IN QUESTION (each of these was borne out when it was recorded. What it rested on has since been rejected, retired or withdrawn, or is in doubt itself. Build nothing on them)\n"
            + "  in this scene as it is recorded. Rewrite the passage, or withdraw the record when you submit:\n"
            + "    a_ben_tells · s4, manuscript/04-four.md line 6 · dialogue at 40.2 in s4: Ben asserts p_hid to Dora · \"It was under the mat all along\"\n"
            + $"      LOST ITS SUPPORT: Ben had p_hid from b_ben_told (told via a_says), rejected in change set {head}. Nothing else on record gives it to Ben by story time 40.2.\n", context);
        Assert.Contains("\n  In question, build nothing on: a_ben_tells, a_cara_repeats, b_dora_told\n", context);
        // Ben no longer has it on entry, and the context says that too: the rejected records are gone from it.
        Assert.DoesNotContain("b_ben_told", context.Split("IN QUESTION")[0]);
    }

    [Fact]
    public void An_unrelated_rejection_leaves_the_next_scene_alone()
    {
        Assert.True(SubmitThree().Applied);
        // The next scene is being written, from a context that lists what Ben made of the peas.
        var begun = ContextCompiler.Compile(_tiny.Story.State(), "s4");
        Assert.Contains("p_peas: The peas are for supper. [UNCONFIRMED] (b_ben_peas*)", begun.Text);

        // The author throws out the guess about the peas. Nothing written later rested on it, and nothing is said to.
        var decided = _tiny.Story.Decide(new Decision { Reject = ["b_ben_peas"] });
        Assert.True(decided.Done, string.Join("; ", decided.Findings));
        Assert.Empty(decided.Plan!.Consequences);
        Assert.DoesNotContain("This decision", ReviewReport.Decided(decided));
        Assert.Empty(Standing.Of(_tiny.Story.State()).All);

        // The draft is not sent back over it. The context did change, in a line about a fact the draft records
        // nothing on, and that line comes back as a note so that it is never silent.
        var next = Submit("s4", begun.ManifestId, Four4);
        Assert.True(next.Applied, Notes(next));
        Assert.True(next.Basis!.Rebased);
        var moved = Assert.Single(next.Notes, n => n.Code == "CONTEXT_MOVED");
        Assert.Contains("None is about a fact this submission records, so it was applied on top of them.", moved.Message);
        Assert.Contains("p_peas [correctly suspects] takes it as true, suspects: The peas are for supper. (b_ben_peas)", moved.Message);
        Assert.Empty(Standing.Of(_tiny.Story.State()).All);
    }

    [Fact]
    public void A_draft_that_leaned_on_what_was_rejected_is_still_sent_back()
    {
        Assert.True(SubmitThree().Applied);
        var begun = Manifest("s4");
        Assert.True(_tiny.Story.Decide(new Decision { Reject = ["b_ben_peas"] }).Done);
        // This draft has Dora ask about the peas, so what the context said of them is something it wrote from.
        var peas = new Assertion
        {
            Id = "a_dora_peas", Scene = "s4", StoryKey = 40.5, Type = AssertionKind.Dialogue, Speaker = "dora", Addressees = ["cara"], Prop = "p_peas",
            Quote = "Are those peas for supper?",
        };
        var next = Submit("s4", begun, [.. Four4, peas]);
        Assert.False(next.Applied);
        Assert.Equal("STALE_CONTEXT", next.Findings[0].Code);
        Assert.Contains(next.Findings, f => f.Code == "CHANGED" && f.Message.Contains("b_ben_peas", StringComparison.Ordinal));

        // So is one written before what Ann said was rejected: every record of it is about that fact.
        var again = Manifest("s4");
        Assert.True(_tiny.Story.Decide(new Decision { Reject = ["a_says", "b_ben_told"] }).Done);
        var late = Submit("s4", again, Four4);
        Assert.False(late.Applied);
        Assert.Equal("STALE_CONTEXT", late.Findings[0].Code);
    }

    [Fact]
    public void A_new_draft_of_the_earlier_scene_that_no_longer_says_it_puts_the_later_passages_in_question()
    {
        Assert.True(SubmitThree().Applied);
        Assert.True(Submit("s4", Manifest("s4"), Four4).Applied);
        // The scene is rewritten, and Ann keeps her counsel. The session that rewrote it withdraws what she said.
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"),
            "<!-- scene: s3 -->\n# Three\n\nAnn said nothing. Ben looked at her. Cara went on shelling peas.\n");
        var rewritten = _tiny.Story.SubmitScene(new SceneReport
        {
            Scene = "s3", Manifest = Manifest("s3"), Withdraws = ["a_says", "b_ben_told"],
            Records = [new Event { Id = "e_silent", Scene = "s3", StoryKey = 30.2, Summary = "Ann says nothing.", Quote = "Ann said nothing." }],
        });
        Assert.True(rewritten.Applied, Notes(rewritten));
        // It is told at once which passages of the scene after it now rest on nothing, and so is the author.
        var put = rewritten.Notes.Where(n => n.Code == "PUT_IN_QUESTION").ToList();
        Assert.Equal(["a_ben_tells", "a_cara_repeats", "b_dora_told"], put.Select(n => n.RecordId));
        Assert.Contains("this submission leaves a record of s4 without what it rested on (s4, manuscript/04-four.md line 6)", put[0].Message);
        Assert.Contains($"LOST ITS SUPPORT: Ben had p_hid from b_ben_told (told via a_says), retired in change set {_tiny.Story.State().Model.Head} (superseded by a new draft of the scene).", put[0].Message);
        Assert.Contains("PUT_IN_QUESTION a_ben_tells", SubmitReport.Text(rewritten));
    }

    [Fact]
    public void What_was_in_doubt_stands_again_once_something_else_gives_the_speaker_the_fact()
    {
        var three = SubmitThree();
        Assert.True(Submit("s4", Manifest("s4"), Four4).Applied);
        Assert.True(_tiny.Story.Decide(new Decision { Reject = [three.ChangeSet!.ChangeSet.Id] }).Done);
        Assert.Equal(3, Standing.Of(_tiny.Story.State()).All.Count);

        // Between the two scenes, off the page, Ben finds the mat disturbed. Nobody points this at what he says later.
        var found = _tiny.Put(new BeliefChange
        {
            Id = "b_ben_mat", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 36,
            Channel = Channel.Inferred, Directness = Directness.Inferred,
        });
        Assert.True(found.Applied, string.Join("; ", found.Findings));
        var told = string.Join("\n", found.Consequences);
        Assert.Contains("  gives 2 record(s) that were in doubt something to rest on again:", told);
        Assert.Contains("    a_ben_tells · s4, manuscript/04-four.md line 6\n", told + "\n");
        Assert.Contains("      STANDS AGAIN on b_ben_mat (inferred): belief at 36: Ben comes to take p_hid as true, believes · inferred · inferred", told);
        Assert.Contains("      STANDS AGAIN on a_ben_tells (the source it came from)", told);
        // Cara had only Ann's words, and those are still gone.
        Assert.Equal(["a_cara_repeats"], Standing.Of(_tiny.Story.State()).All.Select(d => d.Id));
    }
}
