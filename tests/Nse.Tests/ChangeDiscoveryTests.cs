using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// What a change to the story does to the passages already written. On Monday a firm issues its courier, Oskar,
/// a spare key. On Friday Mara gives him one herself. On Saturday he lets himself into the building and tells the
/// porter "Mara gave me a key herself." Mara dies on Sunday. Then the author moves her death to Wednesday.
/// The delivery can still happen, because of the spare. The line about who gave him the key has lost its
/// support. And when a Tuesday handoff is written in another thread of the story, the line has support again,
/// with nobody having pointed the one at the other. The story is the fixture under Fixtures/courier, put and
/// submitted the way a story is.
/// </summary>
public sealed class ChangeDiscoveryTests : IDisposable
{
    private readonly string _root = Fixtures.NewTempDir();
    private readonly Story _story;

    private static string Courier => Path.Combine(Fixtures.RepoRoot, "tests", "Nse.Tests", "Fixtures", "courier");

    public ChangeDiscoveryTests()
    {
        _story = Story.Init(_root);
        foreach (var file in new[] { "01-monday.md", "03-friday.md", "04-saturday.md" })
            File.Copy(Path.Combine(Courier, "manuscript", file), Path.Combine(_story.ManuscriptDir, file));
    }

    public void Dispose() => Fixtures.DeleteDirectory(_root);

    /// <param name="marked">Whether the fact of her death says that it ends her part in the story.</param>
    private ChangeSetDraft Draft(string name, bool marked = true)
    {
        var text = File.ReadAllText(Path.Combine(Courier, name + ".json"));
        return NseJson.Read<ChangeSetDraft>(marked ? text : text.Replace(", \"ends\": [\"mara\"]", "", StringComparison.Ordinal));
    }

    private PutResult Put(string name, bool marked = true)
    {
        var result = _story.Put(Draft(name, marked));
        Assert.True(result.Applied, string.Join("; ", result.Findings));
        return result;
    }

    /// <summary>The story as first written: she dies on Sunday, and everything stands.</summary>
    private void Written(bool marked = true)
    {
        foreach (var name in new[] { "01-ground", "02-monday", "03-friday", "04-saturday" })
            Put(name, marked);
        Assert.Empty(Standing.Of(_story.State()).All);
    }

    /// <summary>A Tuesday scene in another thread, written and submitted by a session as any scene is.</summary>
    private SubmitResult Tuesday(string report)
    {
        File.Copy(Path.Combine(Courier, "manuscript", "02-tuesday.md"), Path.Combine(_story.ManuscriptDir, "02-tuesday.md"));
        Put("tuesday-ground");
        var manifest = ContextCompiler.Compile(_story.State(), "s_tue").ManifestId;
        var result = _story.SubmitScene(NseJson.Read<SceneReport>(File.ReadAllText(Path.Combine(Courier, report + ".json")).Replace("MANIFEST", manifest, StringComparison.Ordinal)));
        Assert.True(result.Applied, string.Join("; ", result.Findings));
        return result;
    }

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    private const string TheHandoffCannotHappen = """
          leaves 4 record(s) of what is already written in doubt. The trouble starts at 1. Each passage needs a look:
            e_handoff · s_fri, manuscript/03-friday.md line 4
              event at 50.1 in s_fri: Mara gives Oskar a key. [Mara actor, Oskar addressee] · "Mara pressed a key into Oskar's hand"
              CANNOT HAPPEN AS RECORDED: Mara acts in it in s_fri, at story time 50.1, and p_mara_dead ("Mara is dead."), which ends Mara's part in the story, is so from story time 30.
              and resting on it:
                b_gave · s_fri, manuscript/03-friday.md line 4
                  belief at 50.1 in s_fri: Oskar comes to take p_mara_gave_key as true, certain · took_part via e_handoff · stated
                  it rests on e_handoff (the source it came from).
                a_claim · s_sat, manuscript/04-saturday.md line 6
                  dialogue at 60.2 in s_sat: Oskar asserts p_mara_gave_key to the porter · "Mara gave me a key herself"
                  all Oskar has for p_mara_gave_key is b_gave (took part via e_handoff).
                b_porter · s_sat, manuscript/04-saturday.md line 8
                  belief at 60.3 in s_sat: the porter comes to take p_mara_gave_key as true, believes · told via a_claim · stated · "The porter nodded"
                  it rests on a_claim (the source it came from).

        """;

    private const string TheDeliveryStillStands = """
          still borne out in s_sat:
            e_entry · s_sat, manuscript/04-saturday.md line 4 · event at 60.1 in s_sat: Oskar lets himself into the Calder building. [Oskar actor, the porter witness] · needs p_has_key · "Oskar let himself in at the side door with a key"
              BORNE OUT: what it needs is so: f_spare makes p_has_key so from story time 10.1, by e_issue: "The firm issued Oskar a spare key" (s_mon, manuscript/01-monday.md line 4).

        """;

    [Fact]
    public void Moving_her_death_shows_the_line_that_lost_its_support_and_that_the_delivery_can_still_happen()
    {
        Written();
        // Before anything is written: what it would do to the passages that are.
        var asked = _story.WhatIf(Draft("edit1-move-death"));
        Assert.True(asked.WouldApply);
        Assert.Equal((TheHandoffCannotHappen + TheDeliveryStillStands).ReplaceLineEndings("\n"), Text(asked.Written));
        Assert.Contains("For what is already written, it\n  leaves 4 record(s)", WhatIf.Text(asked));
        Assert.Empty(Standing.Of(_story.State()).All);

        // Made, it says the same. Nothing was refused: whether the Friday scene goes is the author's to decide.
        var made = Put("edit1-move-death");
        Assert.Equal(Text(asked.Written), Text(made.Consequences));
        var state = _story.State();
        var standing = Standing.Of(state);
        Assert.Equal(["b_gave", "e_handoff", "a_claim", "b_porter"], standing.All.Select(d => d.Id));
        Assert.Equal([Standing.InQuestion, Standing.AfterTheirEnd, Standing.InQuestion, Standing.InQuestion], standing.All.Select(d => d.Code));
        Assert.Equal(["b_gave", "e_handoff"], standing.Of("a_claim")!.Through);
        // The entry is checked, not merely left alone: it says what it needs, and that is so by the spare.
        Assert.Null(standing.Of("e_entry"));
        Assert.Equal([new Ground("f_spare", "makes p_has_key so from story time 10.1")], standing.RestsOn("e_entry"));
        var report = Standing.Text(state);
        Assert.Contains("\nIn doubt (4), by the record the trouble starts at:\n  e_handoff · s_fri, manuscript/03-friday.md line 4\n", report);
        Assert.Contains("\nStill borne out in s_sat:\n  e_entry · s_sat, manuscript/04-saturday.md line 4 · ", report);
        Assert.Contains("In question: 4 record(s) in s_fri, s_sat no longer have what they rested on.", ContextCompiler.Brief(state));

        // Take the spare away as well and the delivery is in doubt too: it stood because of the spare.
        var without = _story.WhatIf(new ChangeSetDraft { Actor = "author", Ops = [new RetireOp { Id = "f_spare", Reason = "retcon: no spare was issued" }] });
        Assert.Contains("    e_entry · s_sat, manuscript/04-saturday.md line 4", without.Written);
        Assert.Contains("      WHAT IT NEEDS IS NOT SO: it happens at story time 60.1 and needs p_has_key (\"Oskar has a key that opens the Calder building.\"), which is not decided (unstated).", without.Written);
    }

    [Fact]
    public void A_session_revising_the_scene_is_shown_which_line_to_change_and_what_it_can_keep()
    {
        Written();
        Put("edit1-move-death");
        var context = ContextCompiler.Compile(_story.State(), "s_sat").Text;
        // The reason is said once, at the record the trouble starts at, and each record that traces back to it says by what way.
        Assert.Contains(
            "IN QUESTION (each of these was borne out when it was recorded. What it rested on has since been rejected, retired or withdrawn, or is in doubt itself. Build nothing on them)\n"
            + "  where the trouble starts. This is for the author to settle:\n"
            + "    e_handoff · s_fri, manuscript/03-friday.md line 4 · event at 50.1 in s_fri: Mara gives Oskar a key. [Mara actor, Oskar addressee] · \"Mara pressed a key into Oskar's hand\"\n"
            + "      CANNOT HAPPEN AS RECORDED: Mara acts in it in s_fri, at story time 50.1, and p_mara_dead (\"Mara is dead.\"), which ends Mara's part in the story, is so from story time 30.\n"
            + "  in this scene as it is recorded. Rewrite the passage, or withdraw the record when you submit:\n"
            + "    a_claim · s_sat, manuscript/04-saturday.md line 6 · dialogue at 60.2 in s_sat: Oskar asserts p_mara_gave_key to the porter · \"Mara gave me a key herself\"\n"
            + "      IN QUESTION: all Oskar has for p_mara_gave_key is b_gave (took part via e_handoff). It traces back through b_gave -> e_handoff.\n"
            + "    b_porter · s_sat, manuscript/04-saturday.md line 8 · belief at 60.3 in s_sat: the porter comes to take p_mara_gave_key as true, believes · told via a_claim · stated · \"The porter nodded\"\n"
            + "      IN QUESTION: it rests on a_claim (the source it came from). It traces back through a_claim -> b_gave -> e_handoff.\n"
            + "  among the records the lines of this context rest on. They are still listed where they were; tell the author:\n"
            + "    b_gave · s_fri, manuscript/03-friday.md line 4 · belief at 50.1 in s_fri: Oskar comes to take p_mara_gave_key as true, certain · took_part via e_handoff · stated\n"
            + "      IN QUESTION: it rests on e_handoff (the source it came from). It traces back through e_handoff.\n"
            + "  still borne out in this scene. A new draft can keep these:\n"
            + "    e_entry · s_sat, manuscript/04-saturday.md line 4 · event at 60.1 in s_sat: Oskar lets himself into the Calder building. [Oskar actor, the porter witness] · needs p_has_key · \"Oskar let himself in at the side door with a key\"\n"
            + "      BORNE OUT: what it needs is so: f_spare makes p_has_key so from story time 10.1, by e_issue: \"The firm issued Oskar a spare key\" (s_mon, manuscript/01-monday.md line 4).\n",
            context);
        const string trace = "where the trouble starts: cannot happen as recorded: Mara acts in it in s_fri, at story time 50.1, and p_mara_dead (\"Mara is dead.\"), which ends Mara's part in the story, is so from story time 30.";
        // What he holds on entry is marked where it is listed, and what the scene is recorded as doing says what the entry needs.
        Assert.Contains("Oskar (point of view): beliefs on entry\n  [knows] takes it as true, certain:\n    p_has_key; p_mara_gave_key [IN QUESTION]\n", context);
        Assert.Contains("  e_entry at 60.1, event: Oskar lets himself into the Calder building. [Oskar actor, the porter witness]; needs p_has_key\n", context);
        Assert.Contains("\n  In question, build nothing on: a_claim, b_porter, b_gave\n", context);
        // Asked about the one pair, the answer carries the same warning.
        var pair = StateReport.Text(_story.State(), new StateQuery(Scene: "s_sat", Holder: "oskar", Prop: "p_mara_gave_key"));
        Assert.Contains($"      IN QUESTION: it rests on e_handoff (the source it came from). That traces back through e_handoff, {trace} Build nothing on it.\n", pair);
        // The scene the trouble starts in is told its own part.
        var friday = ContextCompiler.Compile(_story.State(), "s_fri").Text;
        Assert.Contains("  in this scene as it is recorded. Rewrite the passage, or withdraw the record when you submit:\n    b_gave · s_fri, manuscript/03-friday.md line 4 · ", friday);
        Assert.Contains("    e_handoff · s_fri, manuscript/03-friday.md line 4 · event at 50.1 in s_fri: Mara gives Oskar a key. [Mara actor, Oskar addressee] · \"Mara pressed a key into Oskar's hand\"\n      CANNOT HAPPEN AS RECORDED: Mara acts in it in s_fri", friday);
        Assert.DoesNotContain("where the trouble starts. This is for the author to settle", friday);
    }

    [Fact]
    public void A_handoff_written_in_another_thread_gives_the_line_its_support_back_without_anyone_linking_them()
    {
        Written();
        Put("edit1-move-death");
        var tuesday = Tuesday("tuesday-scene");
        // The session that wrote Tuesday is told what its scene did for a passage it never read.
        var again = tuesday.Notes.Where(n => n.Code == "STANDS_AGAIN").ToList();
        Assert.Equal(["a_claim", "b_porter"], again.Select(n => n.RecordId));
        Assert.Equal(
            "a record of s_sat that was in doubt has something to rest on again (s_sat, manuscript/04-saturday.md line 6): dialogue at 60.2 in s_sat: Oskar asserts p_mara_gave_key to the porter · \"Mara gave me a key herself\". It now rests on b_tue (took part via e_tue)",
            again[0].Message);
        var state = _story.State();
        var standing = Standing.Of(state);
        // He has it twice now, and one of the two stands. That is enough.
        Assert.Equal(["b_gave", "b_tue"], standing.RestsOn("a_claim").Select(g => g.Id));
        Assert.NotNull(standing.Of("b_gave"));
        Assert.Null(standing.Of("b_tue"));
        Assert.Null(standing.Of("a_claim"));
        // The Friday scene is still the author's to deal with. The line on Saturday no longer is.
        Assert.Equal(["b_gave", "e_handoff"], standing.All.Select(d => d.Id));
        var context = ContextCompiler.Compile(state, "s_sat").Text;
        Assert.DoesNotContain("in this scene as it is recorded", context);
        Assert.DoesNotContain("p_mara_gave_key [IN QUESTION]", context);
    }

    [Fact]
    public void A_handoff_recorded_as_another_fact_is_offered_as_a_suggestion_for_the_author_to_judge()
    {
        Written();
        Put("edit1-move-death");
        // The session that wrote Tuesday worded the fact for itself. The engine cannot know the two are one.
        var tuesday = Tuesday("tuesday-scene-other");
        Assert.DoesNotContain(tuesday.Notes, n => n.Code == "STANDS_AGAIN");
        const string suggestion =
            "POSSIBLE SUPPORT, for you to judge: Oskar holds p_tue_key (\"Mara gave Oskar the side-door key off her own ring.\") from story time 20.1: b_tue (took part via e_tue), \"gave it to Oskar\" (s_tue, manuscript/02-tuesday.md line 4). It is about Mara and Oskar, as p_mara_gave_key is. If the two are one fact, record them under one proposition and this stands.";
        var offered = Assert.Single(tuesday.Notes, n => n.Code == "POSSIBLE_SUPPORT");
        Assert.Equal("a_claim", offered.RecordId);
        Assert.EndsWith("is in doubt, and this scene may give it something to rest on. " + suggestion, offered.Message);
        // It decides nothing: the line is still in doubt, and the suggestion sits under it wherever it is listed.
        var state = _story.State();
        Assert.NotNull(Standing.Of(state).Of("a_claim"));
        Assert.Contains(
            "        all Oskar has for p_mara_gave_key is b_gave (took part via e_handoff).\n        " + suggestion + "\n", Standing.Text(state));
    }

    [Fact]
    public void Where_nothing_says_that_death_ends_her_part_the_same_move_is_a_suggestion_and_cutting_the_scene_makes_it_certain()
    {
        Written(marked: false);
        var asked = _story.WhatIf(Draft("edit1-move-death"));
        Assert.Equal(
            """
              may have done more, which the records cannot say. A suggestion, for you to judge:
                p_mara_dead ("Mara is dead.") was not so on entry to what follows and is now so. It is about Mara, who is in it:
                  e_handoff · s_fri, manuscript/03-friday.md line 4
                    event at 50.1 in s_fri: Mara gives Oskar a key. [Mara actor, Oskar addressee] · "Mara pressed a key into Oskar's hand"
                  If that cannot stand, these are left without what they rest on: b_gave (s_fri, manuscript/03-friday.md line 4); a_claim (s_sat, manuscript/04-saturday.md line 6); b_porter (s_sat, manuscript/04-saturday.md line 8)
                  The records do not say whether p_mara_dead stops Mara being there. If it ends their part in the story, put the proposition again with ends naming them, and this is followed through from then on.

            """.ReplaceLineEndings("\n"), Text(asked.Written));
        Put("edit1-move-death");
        // Nothing is certain, so nothing is listed as in doubt.
        Assert.Empty(Standing.Of(_story.State()).All);

        // The author judges that it cannot stand, and cuts the Friday handoff. Now it is certain, and said as that.
        var cut = _story.Put(new ChangeSetDraft
        {
            Actor = "author", Note = "she is dead by Friday",
            Ops =
            [
                new RetireOp { Id = "b_gave", Reason = "cut: the Friday handoff cannot happen" },
                new RetireOp { Id = "f_gave", Reason = "cut: the Friday handoff cannot happen" },
                new RetireOp { Id = "e_handoff", Reason = "cut: the Friday handoff cannot happen" },
            ],
        });
        Assert.True(cut.Applied, string.Join("; ", cut.Findings));
        var head = _story.State().Model.Head;
        Assert.Equal(
            $$"""
              leaves 2 record(s) of what is already written in doubt. The trouble starts at 1. Each passage needs a look:
                a_claim · s_sat, manuscript/04-saturday.md line 6
                  dialogue at 60.2 in s_sat: Oskar asserts p_mara_gave_key to the porter · "Mara gave me a key herself"
                  the fact: p_mara_gave_key: Mara gave Oskar a key herself.
                  LOST ITS SUPPORT: Oskar had p_mara_gave_key from b_gave (took part via e_handoff), retired in change set {{head}} (cut: the Friday handoff cannot happen). Nothing else on record gives it to Oskar by story time 60.2.
                  and resting on it:
                    b_porter · s_sat, manuscript/04-saturday.md line 8
                      belief at 60.3 in s_sat: the porter comes to take p_mara_gave_key as true, believes · told via a_claim · stated · "The porter nodded"
                      it rests on a_claim (the source it came from).

            """.ReplaceLineEndings("\n") + TheDeliveryStillStands.ReplaceLineEndings("\n"), Text(cut.Consequences));
    }

    [Fact]
    public void The_scene_in_which_someone_dies_still_has_them_in_it_and_the_two_fields_are_held_to_what_they_mean()
    {
        Written();
        Put("edit1-move-death");
        // She dies in a scene set on Wednesday. What she does in that scene is before her end, whatever its exact time.
        Assert.True(_story.Put(new ChangeSetDraft
        {
            Actor = "author",
            Ops =
            [
                new PutOp { Record = new SceneRecord { Id = "s_wed", Pov = "mara", Cast = ["mara"] } },
                new PutOp { Record = new SceneTime { Id = "t_wed", Scene = "s_wed", StoryKey = 30 } },
                new PutOp
                {
                    Record = new Event
                    {
                        Id = "e_last", Scene = "s_wed", StoryKey = 30.4, Summary = "Mara locks the shop for the last time.",
                        Participants = new Dictionary<string, ParticipantRole> { ["mara"] = ParticipantRole.Actor },
                    },
                },
            ],
        }).Applied);
        Assert.Null(Standing.Of(_story.State()).Of("e_last"));

        // A fact ends the part of a person, and an event needs facts that exist.
        string Codes(params LedgerRecord[] records) => string.Join(",", _story.Put(new ChangeSetDraft
        {
            Actor = "author", Ops = records.Select(r => (Operation)new PutOp { Record = r }).ToList(),
        }).Findings.Select(f => f.Code));
        Assert.Equal("BAD_VALUE", Codes(
            new Entity { Id = "ring", Type = EntityKind.Object, Name = "the key ring" },
            new Proposition { Id = "p_lost", Text = "The key ring is lost.", Ends = ["ring"] }));
        Assert.Equal("UNKNOWN_REFERENCE", Codes(new Proposition { Id = "p_gone", Text = "The clerk has emigrated.", Ends = ["clerk"] }));
        Assert.Equal("UNKNOWN_REFERENCE", Codes(new Event { Id = "e_none", StoryKey = 61, Summary = "Oskar signs the book.", Needs = ["p_no_such"] }));
        // A record that uses neither is written exactly as it always was: an old ledger reads and writes unchanged.
        Assert.DoesNotContain("ends", NseJson.Write<LedgerRecord>(new Proposition { Id = "p", Text = "It rained." }));
        Assert.DoesNotContain("needs", NseJson.Write<LedgerRecord>(new Event { Id = "e", StoryKey = 1, Summary = "It rains." }));
        Assert.Equal("proposition: Mara is dead. [ends the part of Mara]", RecordText.Line(_story.State(), _story.State().Model.Proposition("p_mara_dead")!));
        // The weave follows both, so a ripple from the fact reaches what needs it.
        var weave = Weave.Build(_story.State());
        Assert.Contains(weave.Ripple("p_has_key"), h => h.Node.Id == "e_entry" && h.Path[^1].Kind == "event.need");
        Assert.Contains(weave.Ripple("p_mara_dead"), h => h.Node.Id == "mara" && h.Path[^1].Kind == "proposition.ends.up");
    }
}
