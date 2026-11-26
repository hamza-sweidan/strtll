using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// Lints that suggest and do not infer, and the part of a context that says what each person did since they
/// were last on stage. Each lint has a planted case that it flags and a near miss that it leaves alone, and the example
/// story's counts are pinned as a measurement.
/// </summary>
public sealed class LintTests : IDisposable
{
    private readonly TinyStory _tiny = new(annInSceneTwo: true);

    public void Dispose() => _tiny.Dispose();

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private StoryState State => _tiny.Story.State();

    private void Put(params LedgerRecord[] records)
    {
        var result = _tiny.Put(records);
        Assert.True(result.Applied, Codes(result) + " " + string.Join("; ", result.Findings));
    }

    /// <summary>Ben sees a note under the key; Ann is there too.</summary>
    private void BenSeesTheNote(params string[] about) => Put(
        new Proposition { Id = "p_note", Text = "There is a note under the key.", About = about },
        new FactChange { Id = "f_note", Prop = "p_note", Value = true, StoryKey = 10 },
        new Event
        {
            Id = "e_saw", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key, and a note under it, with Ann watching.",
            Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor, ["ann"] = ParticipantRole.Witness },
            Quote = "Ben found the key under the mat",
        },
        new BeliefChange
        {
            Id = "b_note", Holder = "ben", Prop = "p_note", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.1, Scene = "s2",
            Channel = Channel.Witnessed, Via = "e_saw",
        });

    [Fact]
    public void Someone_who_was_there_and_has_no_record_is_named_and_someone_the_fact_is_about_is_not()
    {
        BenSeesTheNote();
        var note = Assert.Single(Lint.CoWitnesses(State));
        Assert.Equal(("CO_WITNESS", "b_note"), (note.Code, note.RecordId));
        Assert.Equal("Ann (ann) was also at e_saw, where Ben (ben) came to hold p_note (b_note), and has no record on p_note at all. Did they not notice, or is a record missing?", note.Message);

        // Any record of hers on it, even "unaware", answers the question.
        Put(new BeliefChange { Id = "b_ann_note", Holder = "ann", Prop = "p_note", Stance = Stance.Unaware, StoryKey = 20.1, Scene = "s2", Channel = Channel.Explicit, Via = "e_saw" });
        Assert.Empty(Lint.CoWitnesses(State));

        using var other = new TinyStory(annInSceneTwo: true);
        var result = other.Put(
            new Proposition { Id = "p_note", Text = "Ann left a note under the key.", About = ["ann"] },
            new FactChange { Id = "f_note", Prop = "p_note", Value = true, StoryKey = 10 },
            new Event
            {
                Id = "e_saw", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the note.",
                Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor, ["ann"] = ParticipantRole.Witness },
            },
            new BeliefChange
            {
                Id = "b_note", Holder = "ben", Prop = "p_note", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.1, Scene = "s2",
                Channel = Channel.Witnessed, Via = "e_saw",
            });
        Assert.True(result.Applied, Codes(result));
        Assert.Empty(Lint.CoWitnesses(other.Story.State()));
    }

    [Fact]
    public void Someone_told_a_fact_with_no_record_on_it_is_named_unless_what_they_were_told_was_a_lie()
    {
        Put(new Assertion
        {
            Id = "a_tell", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben"], Prop = "p_hid",
            Quote = "put it in his pocket",
        });
        var note = Assert.Single(Lint.Addressees(State));
        Assert.Equal(("ADDRESSEE", "a_tell"), (note.Code, note.RecordId));
        Assert.Contains("Ben (ben) was told p_hid by Ann (ann) in s2 (a_tell), and has no record on p_hid at all", note.Message);

        using var lied = new TinyStory(annInSceneTwo: true);
        Assert.True(lied.Put(new Assertion
        {
            Id = "a_lie", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben"], Prop = "p_hid",
            Polarity = false, Quote = "put it in his pocket",
        }).Applied);
        Assert.StartsWith("lie", lied.Story.State().ClassifyAssertion("a_lie"));
        Assert.Empty(Lint.Addressees(lied.Story.State()));
    }

    [Fact]
    public void A_proposition_that_names_nobody_is_named()
    {
        var note = Assert.Single(Lint.EmptyAbout(State));
        Assert.Equal(("EMPTY_ABOUT", "p_hid"), (note.Code, note.RecordId));
        BenSeesTheNote("ben");
        Assert.Single(Lint.EmptyAbout(State));
        // A belief about a belief says whom it is about through what it nests, and is left alone.
        Put(new Proposition { Id = "p_ben_thinks", Text = "Ben believes Ann hid the key.", Nested = new NestedBelief("ben", "p_hid") });
        Assert.Single(Lint.EmptyAbout(State));
    }

    [Fact]
    public void A_reveal_is_listed_with_its_seeds_and_one_with_none_says_so()
    {
        Put(new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] }, new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s3" });
        var bare = Assert.Single(Lint.Seeds(State));
        Assert.Empty(bare.Seeds);
        Assert.Contains("em_hid: p_hid is held back until s3 (not in the manuscript yet). NO SEED: nothing of it is given to the reader before then", Lint.Text(State));

        Put(new Disclosure
        {
            Id = "d_hint", Scene = "s1", Prop = "p_hid", Mode = DisclosureMode.Implied, ReaderStance = Stance.HoldsTrue, Strength = Strength.Suspects,
            Quote = "I never touched it",
        });
        var seeded = Assert.Single(Lint.Seeds(State));
        Assert.Equal("d_hint", Assert.Single(seeded.Seeds).Id);
        Assert.Contains("Seeded in scene 1, s1 [implied] (d_hint).", Lint.Text(State));
    }

    /// <summary>The lints on the example story as it stands, kept as a measurement.</summary>
    [Fact]
    public void Measured_on_the_example_story()
    {
        var root = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), root);
            var s = Story.Open(root).State();
            Assert.Equal(
                ["EM1:", "EM2:", "EM3:", "EM4:", "EM7:", "EM5:D30", "EM6:"],
                Lint.Seeds(s).Select(r => $"{r.Embargo.Id}:{string.Join(",", r.Seeds.Select(d => d.Id))}"));
            Assert.Equal(["Bm09 ch_tom", "Bs29 ch_jonas", "Bs39 ch_jonas", "Bd24 ch_tom"],
                Lint.CoWitnesses(s).Select(n => $"{n.RecordId} {n.Message[(n.Message.IndexOf('(') + 1)..n.Message.IndexOf(')')]}"));
            Assert.Equal(["A27"], Lint.Addressees(s).Select(n => n.RecordId));
            Assert.Empty(Lint.EmptyAbout(s));

            var output = new StringWriter();
            Assert.Equal(0, Cli.Cli.Run(["lint", "--dir", root], output, new StringWriter()));
            Assert.Equal(Lint.Text(s), output.ToString());
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    // ------------------------------------------------------------------- since each person was last on stage

    [Fact]
    public void A_span_of_a_persons_story_counts_its_first_moment_and_not_its_last_and_takes_in_what_they_did_as_a_persona()
    {
        Put(
            new BeliefChange { Id = "b_at_15", Holder = "ben", Prop = "p_hid", Stance = Stance.Undecided, StoryKey = 15, Channel = Channel.Assumed },
            _tiny.BenBelief("b_at_20", 20),
            new Event
            {
                Id = "e_knock", Scene = "s2", StoryKey = 20.2, Summary = "The stranger knocks.",
                Participants = new Dictionary<string, ParticipantRole> { ["stranger"] = ParticipantRole.Actor },
            },
            new Assertion
            {
                Id = "a_told", Scene = "s1", StoryKey = 10.3, Type = AssertionKind.Narration, Speaker = "ann", Focalizer = "ann", Prop = "p_hid",
                Quote = "Ann hid the key under the mat.",
            });
        // From 15, counted, up to 20, not counted: the span a context gives as "since they were last on stage".
        Assert.Equal(["b_at_15"], Recap.Items(State, "ben", 15, 20, truth: false).Select(i => i.Id));
        Assert.Empty(Recap.Items(State, "ben", 15.5, 20, truth: false));
        // What Ben did under another name is his.
        Assert.Contains(Recap.Items(State, "ben", 20, 25, truth: false), i => i.Id == "e_knock" && i.Text == "The stranger knocks. (took part)");
        // Narration through a person is the book speaking, not something they said.
        Assert.DoesNotContain(Recap.Items(State, "ann", 10, 15, truth: false), i => i.Id == "a_told");
    }

    [Fact]
    public void Last_on_stage_is_a_scene_strictly_earlier_in_story_time_and_counts_a_person_seen_as_a_persona()
    {
        // A scene set at the very same story time is not an earlier one.
        Put(new SceneRecord { Id = "s3", Pov = "ben", Cast = ["ben"] }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 20 });
        Assert.Contains("  Ben: on stage in no scene before this one in story time", ContextCompiler.Compile(State, "s2").Text);
        // Ben was on stage before, as the stranger: that was him.
        Put(new SceneRecord { Id = "s0", Pov = "ann", Cast = ["ann", "stranger"] }, new SceneTime { Id = "t0", Scene = "s0", StoryKey = 5 });
        Assert.Contains("  Ben: last on stage in s0, which is not in the manuscript yet, at story time 5. Since then: nothing recorded", ContextCompiler.Compile(State, "s2").Text);
    }

    [Fact]
    public void A_context_says_where_each_person_was_last_and_what_happened_to_them_since()
    {
        var root = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), root);
            var story = Story.Open(root);
            var two = ContextCompiler.Compile(story.State(), "sc_02").Text;
            // Sarah comes into scene 2 from April, told eighth: what she did off the page in between is listed.
            Assert.Contains(
                "SINCE EACH PERSON WAS LAST ON STAGE (in story time: what they did, heard or came to believe in between)\n"
                + "  Sarah Kessler: last on stage in scene 8, sc_08, at story time -160. Since then: 2 thing(s)\n"
                + "    -150, off the page: Sarah goes back to the city with the letter hidden in the lining of her suitcase. (took part) (Eb8)\n"
                + "    -150, off the page: they came to believe that: Edmund had decided not to send the letter. (inferred) (Bs13)\n"
                + "  Daniel Kessler: last on stage in scene 1, sc_01, at story time 10. Since then: nothing recorded\n", two);
            Assert.Contains("  manuscript/08-april.md lines 2-42: where Sarah Kessler was last on stage (sc_08)", two);
            // Scene 1 is the scene before scene 2 in the telling, and is pointed at once.
            Assert.DoesNotContain("where Daniel Kessler was last on stage", two);

            var one = ContextCompiler.Compile(story.State(), "sc_08").Text;
            Assert.Contains("  Sarah Kessler: on stage in no scene before this one in story time", one);

            // Where the earlier scene sits in its file is a pointer to prose. Moving it changes nothing the context
            // says about the story, so a draft written before the move is not stale.
            var before = ContextCompiler.Compile(story.State(), "sc_09");
            var kitchen = Path.Combine(story.ManuscriptDir, "07-the-kitchen.md");
            File.WriteAllText(kitchen, "\n\n\n" + File.ReadAllText(kitchen));
            var after = ContextCompiler.Compile(story.State(), "sc_09");
            Assert.NotEqual(before.Text, after.Text);
            Assert.Empty(Freshness.Differences(before, after));
            Assert.Equal(before.ManifestId, after.ManifestId);
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }
}
