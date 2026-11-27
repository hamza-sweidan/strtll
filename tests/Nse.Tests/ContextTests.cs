using System.Text.Json;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>The context package for a scene, and the brief a session starts from.</summary>
public sealed class ContextTests(HarbourStory harbour) : IClassFixture<HarbourStory>
{
    private readonly StoryState _state = harbour.Story.State();

    /// <summary>
    /// For every scene: what must not be revealed, what the scene reveals, which guarded facts are in play, and
    /// what each cast member can be shown knowing, are what the reference answers say.
    /// </summary>
    [Fact]
    public void A_context_rests_on_what_the_reference_says_for_every_scene()
    {
        var expected = Fixtures.Json("expected-context.json");
        var differences = new List<string>();
        foreach (var scene in expected.EnumerateObject())
        {
            var sid = scene.Name;
            void Same(string what, IEnumerable<string> want, IEnumerable<string> got)
            {
                var (w, g) = (string.Join(" ", want), string.Join(" ", got));
                if (w != g)
                    differences.Add($"{sid} {what}: expected [{w}], the engine gives [{g}]");
            }
            Same("must not reveal", scene.Value.GetProperty("must_not_reveal").EnumerateArray().Select(x => $"{x[0].GetString()}@{x[1].GetString()}"),
                _state.MustNotReveal(sid).Select(x => $"{x.Prop}@{x.RevealScene}"));
            Same("revealed here", Strings(scene.Value.GetProperty("revealed_here")), _state.RevealedHere(sid));
            Same("in play", Strings(scene.Value.GetProperty("in_play")), _state.InPlay(sid));
            var cast = scene.Value.GetProperty("cast");
            Same("cast", cast.EnumerateObject().Select(c => c.Name), _state.CastHolders(sid).Select(c => c.Person));
            foreach (var person in cast.EnumerateObject())
            {
                Same($"{person.Name} status", person.Value.GetProperty("status").EnumerateObject().Select(x => $"{x.Name}={x.Value.GetString()}"),
                    _state.InPlay(sid).Select(p => $"{p}={Names.Of(_state.Status(person.Name, p, sid))}"));
                Same($"{person.Name} gaps",
                    person.Value.GetProperty("gaps").EnumerateArray().Select(x => $"{x[0].GetString()}:{x[1].GetString()}:{(x[2].ValueKind == JsonValueKind.Null ? "-" : x[2].GetString())}"),
                    _state.KnowledgeGaps(person.Name, sid).Select(g => $"{g.Prop}:{Names.Of(g.Status)}:{g.LearnsIn ?? "-"}"));
            }
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(20)));
    }

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!);

    /// <summary>Every scene's context in the example stays under 10,000 tokens by the rough guide.</summary>
    [Fact]
    public void Every_scene_has_a_context_within_the_default_budget()
    {
        foreach (var scene in _state.Telling)
        {
            var package = ContextCompiler.Compile(_state, scene.Id);
            Assert.True(package.EstimatedTokens <= ContextCompiler.DefaultBudget, $"{scene.Id}: about {package.EstimatedTokens} tokens");
            Assert.Empty(package.Omitted);
            Assert.StartsWith($"CONTEXT FOR SCENE {scene.Tell}, {scene.Id}", package.Text);
        }
    }

    [Fact]
    public void A_budget_too_small_for_what_must_be_there_fails_and_nothing_is_cut()
    {
        var e = Assert.Throws<BudgetException>(() => ContextCompiler.Compile(_state, "sc_09", budget: 500));
        Assert.StartsWith("BUDGET_INSUFFICIENT: the parts that must be in the context for sc_09 need about", e.Message);
        Assert.Contains("the budget is 500", e.Message);
        Assert.Contains("Largest:", e.Message);
    }

    [Fact]
    public void When_the_rest_does_not_fit_it_is_left_out_whole_and_named()
    {
        var full = ContextCompiler.Compile(_state, "sc_09");
        var must = ContextCompiler.Compile(_state, "sc_09", budget: full.EstimatedTokens - 400);
        Assert.NotEmpty(must.Omitted);
        Assert.Contains("OMITTED TO FIT THE BUDGET. To read one in full: scene_context with part set to the start of its title\n", must.Text);
        // A part that was left out is read by its title, however it is cased: that part alone, in full, and nothing
        // else. Before this the note said "available through state_at", and the only call that had it gave the
        // whole state on entry.
        var listed = must.Omitted[0];
        var title = listed[..listed.LastIndexOf(" (~", StringComparison.Ordinal)];
        var alone = ContextCompiler.PartText(_state, "sc_09", title.ToLowerInvariant());
        Assert.StartsWith("FROM THE CONTEXT FOR sc_09, in full · the state on entry, as of ledger head ", alone);
        Assert.Contains("\n\n" + title + "\n", alone);
        Assert.DoesNotContain("MUST NOT BE STATED TO THE READER", alone);
        Assert.True(ContextCompiler.EstimateTokens(alone) < full.EstimatedTokens / 2, "one part is a small share of the whole");
        // A title that nothing starts with is refused, and the answer names the parts there are.
        var none = Assert.Throws<KeyNotFoundException>(() => ContextCompiler.PartText(_state, "sc_09", "no such part"));
        Assert.Contains("has no part whose title starts \"no such part\". Its parts: ", none.Message);
        Assert.Contains(title, none.Message);
        Assert.True(must.EstimatedTokens <= must.Budget, "the note about what was left out counts towards the budget too");
        // What must be there is all still there.
        Assert.Contains("MUST NOT BE STATED TO THE READER IN THIS SCENE", must.Text);
        Assert.Contains("NOT theirs on entry. They do not say it or act on it until the scene gives it to them", must.Text);
        // The manifest names the state the context shows, not how much of it fitted: it is the same at any budget.
        Assert.Equal(full.ManifestId, must.ManifestId);
    }

    /// <summary>
    /// The note about what was left out and the manifest line count against the budget too. Added after the
    /// sums, they would let a context come out larger than its budget.
    /// </summary>
    [Fact]
    public void A_context_is_never_larger_than_its_budget_whatever_the_budget()
    {
        var full = ContextCompiler.Compile(_state, "sc_09");
        int? refusedAt = null;
        for (var budget = full.EstimatedTokens + 5; budget > 0 && refusedAt is null; budget -= 23)
        {
            try
            {
                var package = ContextCompiler.Compile(_state, "sc_09", budget);
                Assert.True(package.EstimatedTokens <= budget, $"budget {budget}: the context is about {package.EstimatedTokens} tokens");
                Assert.Equal(package.EstimatedTokens, ContextCompiler.EstimateTokens(package.Text));
            }
            catch (BudgetException)
            {
                refusedAt = budget;
            }
        }
        Assert.NotNull(refusedAt);
        Assert.Throws<BudgetException>(() => ContextCompiler.Compile(_state, "sc_09", refusedAt.Value - 23));
    }

    [Fact]
    public void A_context_names_its_cast_the_embargoes_in_force_and_scenes_by_their_ids()
    {
        var text = ContextCompiler.Compile(_state, "sc_08").Text;
        Assert.Contains("\nCast: Sarah Kessler (ch_sarah, point of view).\n", text);
        Assert.Contains("P07: Jonas Pike is Edmund Kessler's son. (first told in scene 10, sc_10; embargo EM7: not before scene 10, sc_10)", text);

        // Scenes are named by id wherever they appear, as state_at names them.
        var nine = ContextCompiler.Compile(_state, "sc_09").Text;
        Assert.Contains("  [stated] reader takes it as true, certain:\n    P29: Sarah is keeping a sealed envelope in Edmund's handwriting hidden from Daniel. (sc_02, D12)\n    P01; P02; ", nine);
        Assert.DoesNotMatch(@"\(scene \d+[,)]", nine.Split("THE READER ON ENTRY")[1].Split("\n\n")[0]);

        Assert.Contains("Embargo EM1: P05 is not to be stated to the reader before sc_05.", ContextCompiler.Brief(_state));
        // Every embargo in the example story holds at the start, none at the end, and none is broken.
        Assert.Equal(7, _state.Model.Embargoes.Count);
        Assert.Equal(7, _state.EmbargoesInForce("sc_01").Count);
        Assert.Empty(_state.EmbargoesInForce("sc_12"));
        Assert.Empty(_state.EmbargoBreaches());
    }

    [Fact]
    public void A_history_of_an_object_is_every_move_in_story_order_with_what_moved_it()
    {
        var text = StateReport.Text(_state, new StateQuery(Object: "obj_letter", History: true));
        Assert.StartsWith($"HISTORY OF obj_letter · ledger head {_state.Model.Head}\nthe letter: where it is kept, in story order\n", text);
        var order = new[] { "L1", "L2", "L3", "L4", "L5", "L6", "L7" }.Select(id => text.IndexOf($"({id};", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        // The flashback's move comes second: early in story time, though the scene is told eighth.
        Assert.Matches(@"before the story opens +story time -170 +Edmund's desk drawer  \(L1; by Edmund; Eb4:", text);
        Assert.Matches(@"sc_08 +story time -159\.7 +Sarah's coat  \(L2; by Sarah; E08c:", text);
        Assert.Matches(@"off the page +story time -150 ", text);
        Assert.Matches(@"sc_09 +story time 50\.1 +the office safe  \(L6; by Daniel and Sarah; E09a:", text);
        Assert.Throws<ArgumentException>(() => StateReport.Text(_state, new StateQuery(Prop: "P07", Object: "obj_letter", History: true)));
        Assert.Throws<KeyNotFoundException>(() => StateReport.Text(_state, new StateQuery(Object: "obj_nothing", History: true)));

        // A fact's history says when the fact itself holds, so a belief that has been overtaken can be told
        // from one that is still right.
        Assert.Contains("In story time: true from story time 10.3; false from story time 55.1.",
            StateReport.Text(_state, new StateQuery(Prop: "P30", History: true)));
    }

    [Fact]
    public void The_context_for_scene_nine_says_what_a_draft_of_it_needs_to_be_told()
    {
        var text = ContextCompiler.Compile(_state, "sc_09").Text;
        var daniel = text.Split("\nDaniel Kessler: NOT theirs on entry. They do not say it or act on it until the scene gives it to them\n")[1].Split("\n\n")[0];
        // A draft is tempted to have Daniel say how Sarah got the letter. He never learns it: a secret keeps it to
        // her. That is the author's word and not something the story shows of him, so it is kept apart from what
        // the story does show him not to know; the fact itself is worded under Sarah, who has it.
        Assert.Contains("  SHUT OUT BY A SECRET the author keeps to the people named. The scene cannot give it to them:\n    P10 (SE1, kept to Sarah); P35 (SE1, kept to Sarah); P36 (SE1, kept to Sarah)\n", daniel);
        Assert.Contains("    P10: Sarah took the letter from Edmund's desk drawer on the night he died. (Bs08)\n", text);
        Assert.Contains("  UNAWARE: the story shows that they do not know it:\n    P07: Jonas Pike is Edmund Kessler's son. (learns it in scene 10, sc_10)\n", daniel);
        // Holding the wrong view is another thing, and says what is so.
        Assert.Contains("  BELIEVES THE OPPOSITE of what is so, and acts on it:\n    P03: It is NOT true that: Marta has been taking money from the business. (learns it in scene 10, sc_10)", daniel);
        // A wrong view that no later scene corrects is said as what the records hold. Worded as a forecast, "never
        // learns it", it reads as a ruling, and a draft then keeps the person from being told what nothing forbids.
        Assert.Contains("    P12: It is NOT true that: There was nothing in Edmund's desk but invoices. (nothing recorded later sets them right)\n", text);
        Assert.DoesNotContain("never learns it in the story", text);
        // Silence in the records is a third thing. It is not passed off as ignorance, and it is not written out
        // fact by fact: the rule is said once, and a fact Daniel has no record on is simply not under his name.
        Assert.Contains("  Not listed means NO RECORD: where a fact is not under someone's name, nothing has been recorded either way. Do not write them as knowing it, and do not write that they do not.", text);
        Assert.DoesNotContain("P23", daniel);
        Assert.DoesNotContain("P23", text.Split("\nDaniel Kessler: beliefs on entry\n")[1].Split("\n\n")[0]);
        Assert.DoesNotContain("nothing recorded either way; treat as not knowing", text);
        // What he has yet to learn is placed, by id: where it matters to someone here it is worded under their name.
        Assert.Contains("  NO RECORD ON ENTRY, and the story as recorded gives it to them later, in the scene named:\n    P05 (scene 10, sc_10); P06 (scene 10, sc_10); P14 (scene 12, sc_12); ", daniel);
        Assert.Contains("Sarah Kessler (point of view): beliefs on entry\n  [knows] takes it as true, certain:\n    P01: Edmund Kessler died on the night of 30 April. (Bs07)\n", text);
        // A fact is worded where it first comes up and named after that.
        Assert.Contains("Daniel Kessler: beliefs on entry\n  [knows] takes it as true, certain:\n    P01; P02; P08; P09; P32\n", text);
        Assert.Equal(2, text.Split("Edmund Kessler died on the night of 30 April.").Length);
        Assert.Contains("the letter: Sarah's coat (L5)", text);
        Assert.Contains("manuscript/09-the-safe.md lines 2-", text);
        Assert.Contains("manuscript/08-april.md", text);
        // Everything in the example has been ratified, so no id carries the mark of a provisional record.
        Assert.DoesNotMatch(@"\S\*", text);
        Assert.DoesNotContain("[UNCONFIRMED", text);
    }

    /// <summary>
    /// A hint already given to the reader about something still withheld is shown with the fact, in the words that
    /// gave it: a draft must not undo it, and must not turn it into a statement.
    /// </summary>
    [Fact]
    public void A_withheld_fact_comes_with_what_the_reader_has_had_of_it_so_far()
    {
        var text = ContextCompiler.Compile(_state, "sc_09").Text;
        Assert.Contains(
            "  P15: The man in the grey coat is Jonas Pike. (first told in scene 11, sc_11; embargo EM5: not before scene 11, sc_11)\n"
            + "      cast on entry: Sarah: unaware; Daniel: no record; Marta: correctly believes\n"
            + "      the reader so far: [inferable, sc_07] (D30)\n", text);
        // Where nothing has been given there is no such line.
        Assert.Contains(
            "  P07: Jonas Pike is Edmund Kessler's son. (first told in scene 10, sc_10; embargo EM7: not before scene 10, sc_10)\n"
            + "      cast on entry: Sarah: unaware; Daniel: unaware; Marta: knows\n"
            + "  P34: ", text);
    }

    // The next three tests come from a fresh agent's complaints after answering the 40 probes with these tools.

    [Fact]
    public void A_focused_question_says_what_is_absent_and_names_the_secret_that_shuts_someone_out()
    {
        var one = StateReport.Text(_state, new StateQuery(Scene: "sc_09", Holder: "ch_daniel", Prop: "P10"));
        Assert.Contains("never learns P10: secret SE1 keeps it to Sarah", one);
        Assert.DoesNotContain("no record", one);
        var all = StateReport.Text(_state, new StateQuery(End: true, Holder: "ch_daniel"));
        Assert.Contains("never learns P10, P35, P36: secret SE1 keeps them to Sarah", all);
        // A missing row is now said, not left for the reader to notice.
        Assert.Matches(@"no record either way for: .*P23", all);
        Assert.DoesNotContain("never learns", StateReport.Text(_state, new StateQuery(End: true, Holder: "ch_sarah")));
        Assert.Contains("P23  no record.", StateReport.Text(_state, new StateQuery(Scene: "sc_09", Holder: "ch_daniel", Prop: "P23")));
    }

    [Fact]
    public void A_belief_says_how_where_and_from_whom_it_came()
    {
        var text = StateReport.Text(_state, new StateQuery(Scene: "sc_11", Holder: "ch_daniel", Prop: "P07"));
        Assert.Contains("told by Marta, sc_10", text);
        // By scene 9 the reader can infer whose son Jonas is; it is not stated until scene 10.
        var reader = StateReport.Text(_state, new StateQuery(Scene: "sc_09", ReaderOnly: true, Prop: "P34"));
        Assert.Contains("truth not yet stated to the reader", reader);
        Assert.Contains("the letter (obj_letter): the office safe  (L6; since sc_09)", StateReport.Text(_state, new StateQuery(Scene: "sc_10", Object: "obj_letter")));
    }

    /// <summary>
    /// One person and one fact is the question "what do they have to go on", so the answer shows the passage:
    /// the words the record quotes, or those of what it came from, with the line they stand on. Silence at that
    /// point is followed by what the story has them come by later.
    /// </summary>
    [Fact]
    public void One_person_and_one_fact_are_answered_with_the_passage_behind_the_record()
    {
        var told = StateReport.Text(_state, new StateQuery(Scene: "sc_11", Holder: "ch_daniel", Prop: "P07"));
        Assert.Contains("  P07  [correctly believes] takes it as true, believes: Jonas Pike is Edmund Kessler's son.  (Bd17; told by Marta, sc_10)\n"
                        + "      source: A18, dialogue: Marta asserts P07 to Daniel. \"Jonas is your father's son.\" (sc_10, manuscript/10-stubs.md line 38)\n", told);

        var notYet = StateReport.Text(_state, new StateQuery(Scene: "sc_05", Holder: "ch_daniel", Prop: "P07"));
        Assert.Contains("  P07  no record. That is not the same as \"does not know\": nothing has been recorded either way.\n"
                        + "      first recorded later: sc_07, story time 45.1  [unaware] unaware  (Bd28; explicit, sc_07)\n"
                        + "      passage: \"He was sure he had never heard it.\" (sc_07, manuscript/07-the-kitchen.md line 80)\n", notYet);
        // Nothing before and nothing after is silence and no more.
        Assert.DoesNotContain("first recorded later", StateReport.Text(_state, new StateQuery(Scene: "sc_09", Holder: "ch_daniel", Prop: "P23")));

        var history = StateReport.Text(_state, new StateQuery(Prop: "P07", Holder: "ch_daniel", History: true));
        Assert.Contains("      passage: \"He was sure he had never heard it.\" (sc_07, manuscript/07-the-kitchen.md line 80)\n", history);
        Assert.Contains("      source: A26, document: Edmund asserts P14 to Daniel, Sarah, Marta. \"I acknowledge you as my son, and I give you a half share in the harbour office.\" (sc_12, manuscript/12-opened.md line 36)\n", history);

        var reader = StateReport.Text(_state, new StateQuery(Scene: "sc_11", ReaderOnly: true, Prop: "P07"));
        Assert.Contains("      passage: \"Jonas is your father's son.\" (sc_10, manuscript/10-stubs.md line 38)\n", reader);
        Assert.Contains("      passage: none cited\n", StateReport.Text(_state, new StateQuery(Scene: "sc_09", ReaderOnly: true, Prop: "P34")));

        // Everyone at once, or everything one person holds, stays a list: the passages are for the narrow question.
        Assert.DoesNotContain("passage:", StateReport.Text(_state, new StateQuery(Scene: "sc_11", Prop: "P07")));
        Assert.DoesNotContain("source:", StateReport.Text(_state, new StateQuery(Scene: "sc_11", Prop: "P07")));
        Assert.DoesNotContain("source:", StateReport.Text(_state, new StateQuery(Scene: "sc_11", Holder: "ch_daniel")));
        Assert.DoesNotContain("source:", StateReport.Text(_state, new StateQuery(Prop: "P07", History: true)));
    }

    [Fact]
    public void A_passage_whose_words_have_left_the_text_says_so()
    {
        var gone = new Passage("words that were cut", "sc_10", "Bd17", null, null);
        Assert.Equal("\"words that were cut\" (sc_10; the words are not in that scene's text as it stands)", gone.ToString());
        var record = _state.Model.BeliefsOf("ch_daniel", "P07").Single(b => b.Id == "Bd28");
        Assert.Equal(new Passage("He was sure he had never heard it.", "sc_07", "Bd28", "07-the-kitchen.md", 80), Evidence.Of(_state, record));
    }

    [Fact]
    public void A_history_shows_every_change_to_one_fact_without_comparing_snapshots()
    {
        var text = StateReport.Text(_state, new StateQuery(Prop: "P07", History: true));
        Assert.StartsWith($"HISTORY OF P07 · ledger head {_state.Model.Head}\nJonas Pike is Edmund Kessler's son.\nCanon: true.", text);
        var daniel = text.Split("Daniel Kessler (ch_daniel)")[1].Split("\n\n")[0];
        Assert.Matches(@"sc_10 +story time 55\.1 +\[correctly believes\] takes it as true, believes  \(\w+; told by Marta, sc_10\)", daniel);
        Assert.Matches(@"sc_12 +story time 80\.3 +\[knows\] takes it as true, certain", daniel);
        Assert.Contains("<- the truth is first stated here", text.Split("The reader, in the order of the telling")[1].Split('\n').Single(l => l.Contains("sc_10")));

        var secret = StateReport.Text(_state, new StateQuery(Prop: "P10", Holder: "ch_daniel", History: true));
        Assert.Contains("never learns it: secret SE1 keeps it to Sarah", secret);
        Assert.DoesNotContain("The reader", secret);
        var silent = StateReport.Text(_state, new StateQuery(Prop: "P23", Holder: "ch_daniel", History: true));
        Assert.Contains("no record at any point", silent);
        Assert.Throws<ArgumentException>(() => StateReport.Text(_state, new StateQuery(History: true)));
    }

    [Fact]
    public void A_persona_in_the_cast_is_marked_for_the_author_only()
    {
        var text = ContextCompiler.Compile(_state, "sc_04").Text;
        Assert.Contains("the man in the grey coat  [AUTHOR ONLY: this is Jonas Pike. Do not reveal it.]", text);
    }

    [Fact]
    public void The_manifest_names_what_went_in()
    {
        var a = ContextCompiler.Compile(_state, "sc_07");
        var b = ContextCompiler.Compile(harbour.Story.State(), "sc_07");
        Assert.Equal(a.ManifestId, b.ManifestId);
        Assert.Equal(a.Text, b.Text);
        Assert.Contains("Bd05", a.Records);
        Assert.Matches(@"^m_\d+_[0-9a-f]{6}_[0-9a-f]{8}$", a.ManifestId);
        Assert.Equal(a.Head, ContextCompiler.HeadOf(a.ManifestId));
        var older = ContextCompiler.Compile(harbour.Story.State(a.Head - 6), "sc_07");
        Assert.NotEqual(a.ManifestId, older.ManifestId);
    }

    [Fact]
    public void The_brief_maps_the_story_and_flags_the_flashback()
    {
        var brief = ContextCompiler.Brief(_state);
        Assert.StartsWith($"STORY BRIEF · ledger head {_state.Model.Head} · manuscript ", brief);
        Assert.Contains("   1  sc_01  The Valuation · POV Daniel · story time 10 (Monday morning, October)", brief);
        Assert.Contains("   8  sc_08  April · POV Sarah · story time -160", brief);
        Assert.Single(brief.Split('\n'), line => line.Contains("told out of story order"));
        Assert.Contains("Nothing is awaiting review.", brief);
        Assert.Contains("Never settle P19:", brief);
        Assert.Contains("Secret SE1: only Sarah ever learn P10, P35, P36.", brief);
    }

    [Fact]
    public void The_command_line_serves_the_context_and_the_brief()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["scene-context", "--scene", "sc_07", "--dir", harbour.Root], output, error));
        Assert.Contains("CONTEXT FOR SCENE 7, sc_07 \"The Kitchen\"", output.ToString());
        Assert.Equal(1, Cli.Cli.Run(["scene-context", "--scene", "sc_07", "--budget", "300", "--dir", harbour.Root], output, error));
        Assert.Contains("BUDGET_INSUFFICIENT", error.ToString());
        Assert.Equal(2, Cli.Cli.Run(["scene-context", "--dir", harbour.Root], output, error));
        output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["brief", "--dir", harbour.Root], output, error));
        Assert.Contains("STORY BRIEF", output.ToString());
    }
}
