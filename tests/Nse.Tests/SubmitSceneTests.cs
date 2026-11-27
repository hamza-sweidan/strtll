using System.Text.Json.Nodes;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// The scene contract, and the way an agent's account of a drafted scene enters the ledger. Everything here is
/// deterministic; no model is involved.
/// </summary>
public sealed class SubmitSceneTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(SubmitResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private static string Manifest(TinyStory tiny, string scene) => ContextCompiler.Compile(tiny.Story.State(), scene).ManifestId;

    private static SubmitResult Submit(TinyStory tiny, string manifest, params LedgerRecord[] records) =>
        tiny.Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Note = "first draft", Records = records });

    // What scene 2's text supports: Ben finds the key and pockets it.
    private static Event Finds => new()
    {
        Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key under the mat.",
        Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
    };

    private static Placement Pockets => new()
    {
        Id = "l_pocket", Object = "key", Place = "pocket", StoryKey = 20.1, Scene = "s2", Cause = "e_find", Quote = "put it in his pocket",
    };

    private static BeliefChange BenWorksItOut => new()
    {
        Id = "b_ben", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 20.1, Scene = "s2",
        Channel = Channel.TookPart, Via = "e_find", Directness = Directness.Inferred,
    };

    private static SceneContract ContractFor(params Intent[] intends) => new()
    {
        Id = "c_s2", Scene = "s2", Outline = "Ben finds the key.", Intends = intends,
        Relies =
        [
            new Intent { Change = IntentKind.Placement, Object = "key", Place = "mat" },
            new Intent { Change = IntentKind.Belief, Holder = "ben", Prop = "p_hid", Stance = Stance.Unaware },
        ],
    };

    private static readonly Intent BenLearns = new() { Change = IntentKind.Belief, Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue };
    private static readonly Intent KeyToPocket = new() { Change = IntentKind.Placement, Object = "key", Place = "pocket" };

    [Fact]
    public void A_submission_is_applied_as_provisional_and_says_what_it_rested_on()
    {
        var head = _tiny.Story.ReadLedger().Count;
        var manifest = Manifest(_tiny, "s2");
        Assert.Matches(@"^m_1_[0-9a-f]{6}_[0-9a-f]{8}$", manifest);
        Assert.Equal(head, ContextCompiler.HeadOf(manifest));

        var result = Submit(_tiny, manifest, Finds, Pockets, BenWorksItOut);
        Assert.True(result.Applied, Codes(result));
        Assert.Equal(new ChangeBasis("s2", manifest, head, Rebased: false, Text: _tiny.Story.ReadManuscript().Find("s2")!.Hash), result.Basis);

        // The basis is in the file, written by the engine, and the records are provisional and the agent's.
        var stored = _tiny.Story.ReadLedger()[^1].ChangeSet;
        Assert.Equal(result.Basis, stored.Basis);
        Assert.Equal("agent", stored.Actor);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_tiny.Story.LedgerDir, result.ChangeSet!.File)))!;
        Assert.Equal(manifest, file["basis"]!["manifest"]!.GetValue<string>());
        Assert.False(file["basis"]!["rebased"]!.GetValue<bool>());
        var state = _tiny.Story.State();
        Assert.All(new[] { "e_find", "l_pocket", "b_ben" }, id => Assert.Equal(Approval.Provisional, state.Model.ById[id].ApprovalAt(state.Model.Head)));
        Assert.Equal("pocket", state.Location("key", StoryState.End)!.Place);

        // With no contract there is nothing to compare with, and that is said. An inference is flagged.
        Assert.Equal(["NO_CONTRACT", "FLAGGED"], result.Notes.Select(n => n.Code));
        var text = SubmitReport.Text(result);
        Assert.StartsWith("Applied as 000002-cs_", text);
        Assert.Contains("3 record(s), all provisional, ledger head 2.", text);
        Assert.Contains("For the author to settle (2):", text);
        Assert.DoesNotContain("Rebased", text);

        // A change set that did not come through a submission has no basis, and a caller cannot give it one.
        Assert.Null(_tiny.Story.ReadLedger()[0].ChangeSet.Basis);
        Assert.DoesNotContain(typeof(ChangeSetDraft).GetProperties(), p => p.Name == "Basis");
    }

    /// <summary>
    /// The author's records already have Ben sure of where the key was hidden. A scene that has him "come to" the
    /// same view from another passage taught him something the fact as worded has no room for, and whoever
    /// recorded it is told so on the spot, with both passages.
    /// </summary>
    [Fact]
    public void A_belief_that_changes_nothing_comes_back_as_a_sign_of_a_fact_that_joins_two_details()
    {
        Assert.True(_tiny.Put(new BeliefChange
        {
            Id = "b_before", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 15,
            Channel = Channel.Background,
        }).Applied);
        var again = new BeliefChange
        {
            Id = "b_again", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.1, Scene = "s2",
            Channel = Channel.TookPart, Via = "e_find",
        };
        var result = Submit(_tiny, Manifest(_tiny, "s2"), Finds, again);
        Assert.True(result.Applied, Codes(result));
        var note = Assert.Single(result.Notes, n => n.Code == "LEARNED_TWICE");
        Assert.Equal("b_again", note.RecordId);
        Assert.Contains("Ben (ben) already takes p_hid as true, certain (b_before: no passage cited), and b_again has them come to it again from \"Ben found the key under the mat\" (s2, manuscript/02-two.md line 4).", note.Message);
        Assert.Contains("Two details are separate facts when someone could learn them at different times", note.Message);
        Assert.Contains("LEARNED_TWICE b_again", SubmitReport.Text(result));
    }

    /// <summary>The same join seen from the statement: Ann tells Ben, in scene 2, what he was already certain of, and nobody records that he learned anything.</summary>
    [Fact]
    public void A_statement_that_tells_nobody_anything_new_comes_back_with_the_submission_that_records_it()
    {
        using var tiny = new TinyStory(annInSceneTwo: true);
        Assert.True(tiny.Put(new BeliefChange
        {
            Id = "b_before", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 15,
            Channel = Channel.Background,
        }).Applied);
        var tells = new Assertion
        {
            Id = "a_tell", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben"], Prop = "p_hid",
            Quote = "put it in his pocket",
        };
        var result = Submit(tiny, Manifest(tiny, "s2"), tells);
        Assert.True(result.Applied, Codes(result));
        var note = Assert.Single(result.Notes, n => n.Code == "TOLD_AGAIN");
        Assert.Equal("a_tell", note.RecordId);
        Assert.Contains("everyone it is said to was already certain of that: Ben (ben) (b_before: no passage cited)", note.Message);
    }

    /// <summary>A draft missing an intended belief change, and a draft containing an unplanned one, are both reported.</summary>
    [Fact]
    public void A_missing_intended_change_and_an_unplanned_one_are_both_reported()
    {
        // The author plans that Ben finds the key, pockets it, and works out who hid it.
        Assert.True(_tiny.Put(ContractFor(BenLearns, KeyToPocket)).Applied);
        var context = ContextCompiler.Compile(_tiny.Story.State(), "s2").Text;
        Assert.Contains("CONTRACT FOR THIS SCENE (c_s2*): what it is meant to do", context);
        Assert.Contains("  outline: Ben finds the key.", context);
        Assert.Contains("  intends: Ben (ben) comes to take p_hid as true", context);
        Assert.Contains("  intends: the key (key) moves to Ben's pocket (pocket)", context);
        Assert.Contains("  relies on: the key (key) is kept at under the mat (mat). It is so (l_mat).", context);
        Assert.Contains("  relies on: Ben (ben) is unaware of p_hid. NOT BORNE OUT ON ENTRY. Nothing is recorded either way", context);
        // The plan is written in ids, so the fact it names is put in words with it, once.
        Assert.Contains("  the facts it names:\n    p_hid: Ann hid the key under the mat. [UNCONFIRMED]\n  Anything else of weight", context);
        Assert.Equal(2, context.Split("Ann hid the key under the mat.").Length);

        // The draft's report leaves the belief change out.
        var missing = Submit(_tiny, Manifest(_tiny, "s2"), Finds, Pockets);
        Assert.True(missing.Applied, Codes(missing));
        var note = Assert.Single(missing.Notes, n => n.Code == "CONTRACT_MISSING");
        Assert.Equal("c_s2", note.RecordId);
        Assert.Contains("intends that Ben (ben) comes to take p_hid as true, and no record in the scene does that", note.Message);
        Assert.DoesNotContain(missing.Notes, n => n.Code == "CONTRACT_UNPLANNED");
        Assert.Contains(missing.Notes, n => n.Code == "PRECONDITION_NOT_ESTABLISHED" && n.Message.Contains("Ben (ben) is unaware of p_hid"));
        Assert.Contains("For the author to settle (2):", SubmitReport.Text(missing));

        // Another story, where the author planned only the move. The draft has Ben work out the secret as well.
        using var other = new TinyStory();
        Assert.True(other.Put(ContractFor(KeyToPocket)).Applied);
        var unplanned = Submit(other, Manifest(other, "s2"), Finds, Pockets, BenWorksItOut);
        Assert.True(unplanned.Applied, Codes(unplanned));
        var extra = Assert.Single(unplanned.Notes, n => n.Code == "CONTRACT_UNPLANNED");
        Assert.Equal("b_ben", extra.RecordId);
        Assert.Contains("in s2, Ben (ben) comes to take p_hid as true, believes, and contract c_s2 does not plan it", extra.Message);
        Assert.DoesNotContain(unplanned.Notes, n => n.Code == "CONTRACT_MISSING");

        // When the scene does what was planned and nothing else of weight, nothing is reported about the plan.
        // A scene also makes small changes nobody plans. Those are not reported, or the ones that matter would be
        // buried: here a view on a question the author has not decided, and a hint the reader may or may not take.
        using var third = new TinyStory();
        Assert.True(third.Put(ContractFor(BenLearns, KeyToPocket)).Applied);
        Assert.True(third.Put(new Proposition { Id = "p_rain", Text = "It will rain before night.", Resolution = Resolution.Open }).Applied);
        var asPlanned = Submit(third, Manifest(third, "s2"), Finds, Pockets, BenWorksItOut,
            new BeliefChange
            {
                Id = "b_rain", Holder = "ben", Prop = "p_rain", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 20.2, Scene = "s2",
                Channel = Channel.Assumed,
            },
            new Disclosure { Id = "d_hint", Scene = "s2", Prop = "p_hid", Mode = DisclosureMode.Inferable, ReaderStance = Stance.HoldsTrue, Strength = Strength.Suspects });
        Assert.True(asPlanned.Applied, Codes(asPlanned));
        Assert.Equal(["b_ben", "b_rain", "d_hint", "l_pocket"], third.Story.State().RecordedChanges("s2").Select(c => c.RecordId).Order());
        Assert.DoesNotContain(asPlanned.Notes, n => n.Code.StartsWith("CONTRACT_", StringComparison.Ordinal));
        // The differences are also what sync reports, for whoever looks later.
        Assert.Equal(2, other.Story.Sync().ContractDifferences.Count);
        Assert.Contains(other.Story.Sync().ContractDifferences, d => d.StartsWith("CONTRACT_UNPLANNED b_ben:", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Told by X" where X's assertion is later in story time is rejected, and "witnessed E" where
    /// the holder is not a participant is rejected.
    /// </summary>
    [Fact]
    public void Told_before_it_was_said_and_witnessed_without_being_there_are_rejected()
    {
        using var two = new TinyStory(annInSceneTwo: true);
        var manifest = Manifest(two, "s2");
        var says = new Assertion
        {
            Id = "a_says", Scene = "s2", StoryKey = 20.5, Type = AssertionKind.Dialogue, Speaker = "ann", Addressees = ["ben"], Prop = "p_hid",
            Quote = "Ben found the key under the mat",
        };
        var toldTooSoon = new BeliefChange
        {
            Id = "b_told", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.2, Scene = "s2",
            Channel = Channel.Told, Via = "a_says",
        };
        var early = Submit(two, manifest, says, toldTooSoon);
        Assert.Equal("BEFORE_SOURCE", Codes(early));
        Assert.Equal("b_told", early.Findings[0].RecordId);
        Assert.Contains("the belief is dated 20.2 but its source a_says is at 20.5", early.Findings[0].Message);

        var sawWithoutBeingThere = new BeliefChange
        {
            Id = "b_saw", Holder = "ann", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain, StoryKey = 20.2, Scene = "s2",
            Channel = Channel.Witnessed, Via = "e_find",
        };
        var absent = Submit(two, manifest, Finds, sawWithoutBeingThere);
        Assert.Equal("NOT_PRESENT", Codes(absent));
        Assert.Contains("ann was not present at e_find", absent.Findings[0].Message);

        // Nothing was written by either, and the refusal reads as one.
        Assert.Single(two.Story.ReadLedger());
        Assert.StartsWith("Refused. Nothing was written. 1 finding(s):\n  NOT_PRESENT b_saw:", SubmitReport.Text(absent));
    }

    /// <summary>One way the ledger can move between the context and the submission, and what it should lead to.</summary>
    private sealed record Trial(string Name, bool Related, string Expect, Action<TinyStory> Move);

    private static Trial[] Trials()
    {
        static BeliefChange Belief(string id, string who, double key, Stance stance, string prop = "p_hid") => new()
        {
            Id = id, Holder = who, Prop = prop, Stance = stance, Strength = stance is Stance.HoldsTrue or Stance.HoldsFalse ? Strength.Believes : null,
            StoryKey = key, Channel = Channel.Assumed,
        };
        static void Ok(PutResult r) => Assert.True(r.Applied, string.Join("; ", r.Findings));
        return
        [
            // Related: each changes a line of the context for scene 2.
            new("Ben, in the cast, holds a view of the hidden key before the scene", true, "+ Ben (point of view): beliefs on entry: p_hid [FALSE BELIEF]",
                t => Ok(t.Put(Belief("x1", "ben", 12, Stance.HoldsFalse)))),
            new("Ben learns it later, off the page", true, "learns it later, off the page, at story time 30",
                t => Ok(t.Put(Belief("x2", "ben", 30, Stance.HoldsTrue)))),
            new("the key is moved before the scene", true, "+ WORLD ON ENTRY: the key: Ben's pocket",
                t => Ok(t.Put(
                    new Event { Id = "x3e", StoryKey = 15, Summary = "Ann moves the key." },
                    new Placement { Id = "x3", Object = "key", Place = "pocket", StoryKey = 15, Cause = "x3e" }))),
            new("a secret now shuts Ben out", true, "never learns it: x4 keeps it to Ann",
                t => Ok(t.Put(new Secret { Id = "x4", Props = ["p_hid"], KnownOnlyTo = ["ann"] }))),
            new("the reader is told in scene 1", true, "+ THE READER ON ENTRY (in reading order): p_hid [stated, s1]",
                t => Ok(t.Put(new Disclosure { Id = "x5", Scene = "s1", Prop = "p_hid", Mode = DisclosureMode.Stated, ReaderStance = Stance.HoldsTrue, Strength = Strength.Certain }))),
            new("a new guarded fact comes into play", true, "x6: The mat is new.",
                t => Ok(t.Put(new Proposition { Id = "x6", Text = "The mat is new." }, new FactChange { Id = "x6f", Prop = "x6", Value = true, StoryKey = 5 }))),
            new("a question is declared never to be settled", true, "+ NEVER SETTLE THESE: x7p: Do not settle it.",
                t => Ok(t.Put(
                    new Proposition { Id = "x7p", Text = "Ann meant Ben to find it.", Resolution = Resolution.Ambiguous },
                    new Rule { Id = "x7", Type = "never_resolve", Prop = "x7p", Text = "Do not settle it." }))),
            new("the author gives the scene a contract", true, "intends: the key (key) moves to Ben's pocket (pocket)",
                t => Ok(t.Put(new SceneContract { Id = "x8", Scene = "s2", Intends = [KeyToPocket] }))),
            new("the record of where the key is kept is retired", true, "- WORLD ON ENTRY: the key: under the mat (l_mat)",
                t => Ok(t.Apply(new RetireOp { Id = "l_mat", Reason = "correction" }))),
            new("the hidden-key fact lapses before the scene", true, "- SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT",
                t => Ok(t.Put(new FactChange { Id = "x10", Prop = "p_hid", Value = false, StoryKey = 15 }))),

            // Unrelated: the ledger moves and the context for scene 2 says what it said.
            new("a new place is registered", false, "", t => Ok(t.Put(new Entity { Id = "y1", Type = EntityKind.Place, Name = "the yard" }))),
            new("a question the author has not decided is added", false, "",
                t => Ok(t.Put(new Proposition { Id = "y2", Text = "Ann will come back.", Resolution = Resolution.Open }))),
            new("Ann, who is not in the scene, changes her mind later", false, "", t => Ok(t.Put(Belief("y3", "ann", 30, Stance.HoldsFalse)))),
            new("an event with no effects is recorded in scene 1", false, "",
                t => Ok(t.Put(new Event { Id = "y4", Scene = "s1", StoryKey = 10.3, Summary = "Ann speaks to the empty hall.", Quote = "to the empty hall" }))),
            new("something Ann says in scene 1 is recorded", false, "",
                t => Ok(t.Put(new Assertion
                {
                    Id = "y5", Scene = "s1", StoryKey = 10.4, Type = AssertionKind.Dialogue, Speaker = "ann", Prop = "p_hid", Polarity = false,
                    Quote = "Ann said to the empty hall",
                }))),
            new("the author ratifies a record the context showed as provisional", false, "", t => Ok(t.Apply(new RatifyOp { Ids = ["l_mat"] }))),
            new("a later scene is given a record, with no place in the telling yet", false, "",
                t => Ok(t.Put(new SceneRecord { Id = "y7", Pov = "ann", Cast = ["ann"] }))),
            new("Ann forms a view on the undecided question", false, "",
                t =>
                {
                    Ok(t.Put(new Proposition { Id = "y8p", Text = "It will rain.", Resolution = Resolution.Open }));
                    Ok(t.Put(Belief("y8", "ann", 12, Stance.HoldsTrue, "y8p")));
                }),
            new("a record is put and rejected", false, "",
                t =>
                {
                    Ok(t.Put(new Entity { Id = "y9", Type = EntityKind.Place, Name = "the shed" }));
                    Ok(t.Apply(new RejectOp { Ids = ["y9"] }));
                }),
            new("a record is put and retired", false, "",
                t =>
                {
                    Ok(t.Put(new Entity { Id = "y10", Type = EntityKind.Place, Name = "the loft" }));
                    Ok(t.Apply(new RetireOp { Id = "y10", Reason = "duplicate" }));
                }),
        ];
    }

    /// <summary>
    /// Twenty stale writes. The ledger moves between the context and the submission. If what
    /// moved changes what the context says, the submission is refused with the exact difference. If it does not,
    /// the submission is applied and the rebase is recorded. Twenty of twenty.
    /// </summary>
    [Fact]
    public void Twenty_stale_writes_are_refused_with_the_difference_or_applied_with_a_recorded_rebase()
    {
        var trials = Trials();
        Assert.Equal((10, 10), (trials.Count(t => t.Related), trials.Count(t => !t.Related)));
        var wrong = new List<string>();
        foreach (var trial in trials)
        {
            using var tiny = new TinyStory();
            var manifest = Manifest(tiny, "s2");
            trial.Move(tiny);
            var moved = tiny.Story.ReadLedger().Count;
            Assert.True(moved > 1, trial.Name);

            var result = Submit(tiny, manifest, Finds, Pockets);
            if (trial.Related)
            {
                var difference = string.Join("\n", result.Findings.Where(f => f.Code == "CHANGED").Select(f => f.Message));
                if (result.Applied || result.Findings[0].Code != "STALE_CONTEXT" || !difference.Contains(trial.Expect, StringComparison.Ordinal)
                    || tiny.Story.ReadLedger().Count != moved)
                    wrong.Add($"{trial.Name}: expected a refusal naming \"{trial.Expect}\"; applied={result.Applied}; findings: {string.Join(" | ", result.Findings)}");
                // The agent does what the refusal says: asks again, and submits from the new context.
                else if (!Submit(tiny, Manifest(tiny, "s2"), Finds, Pockets).Applied)
                    wrong.Add($"{trial.Name}: a submission from the fresh context was refused as well");
            }
            else
            {
                var stored = result.Applied ? tiny.Story.ReadLedger()[^1].ChangeSet : null;
                if (stored?.Basis is not { Rebased: true, BaseHead: 1, Scene: "s2" } || stored.Basis.Manifest != manifest || stored.Seq != moved + 1
                    || !SubmitReport.Text(result).Contains($"Rebased: the draft was written from ledger head 1 and the ledger had moved to {moved}.", StringComparison.Ordinal))
                    wrong.Add($"{trial.Name}: expected to be applied with a recorded rebase; findings: {string.Join(" | ", result.Findings)}");
            }
        }
        Assert.True(wrong.Count == 0, $"{trials.Length - wrong.Count} of {trials.Length} trials went as they should.\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void A_scene_submission_carries_only_what_a_scene_does_and_only_for_its_own_scene()
    {
        var manifest = Manifest(_tiny, "s2");
        // What the story may or must do is the author's to set. It does not come in with a scene.
        LedgerRecord[] authorsOwn =
        [
            new Embargo { Id = "em", Prop = "p_hid", UntilScene = "s2" },
            new Secret { Id = "se", Props = ["p_hid"], KnownOnlyTo = ["ann"] },
            new Rule { Id = "ru", Type = "never_resolve", Prop = "p_hid", Text = "Leave it open." },
            new SceneContract { Id = "co", Scene = "s2" },
            new SceneRecord { Id = "s9", Pov = "ben", Cast = ["ben"] },
            new SceneTime { Id = "t9", Scene = "s2", StoryKey = 5 },
        ];
        foreach (var record in authorsOwn)
        {
            var barred = Submit(_tiny, manifest, Finds, record);
            Assert.Equal("PROPOSAL_ONLY", Codes(barred));
            Assert.Equal(record.Id, barred.Findings[0].RecordId);
            Assert.Contains("the author's to set", barred.Findings[0].Message);
        }

        // A record of another scene, of no scene, or of a story time outside the scene is not this scene's.
        var elsewhere = Submit(_tiny, manifest,
            Finds with { Id = "e_other", Scene = "s1", Quote = "Ben was not there." },
            new BeliefChange { Id = "b_nowhere", Holder = "ben", Prop = "p_hid", Stance = Stance.Unaware, StoryKey = 20.1, Channel = Channel.Assumed },
            new FactChange { Id = "f_then", Prop = "p_hid", Value = false, StoryKey = 12 });
        Assert.Equal("OUT_OF_SCENE,OUT_OF_SCENE,OUT_OF_SCENE", Codes(elsewhere));
        // A new person and a new fact belong to no scene and may come in with any; a fact change within the scene's span is the scene's.
        var introduced = Submit(_tiny, manifest, Finds,
            new Entity { Id = "dog", Type = EntityKind.Character, Name = "the dog" },
            new Proposition { Id = "p_took", Text = "Ben took the key." },
            new FactChange { Id = "f_took", Prop = "p_took", Value = true, StoryKey = 20.1 });
        Assert.True(introduced.Applied, Codes(introduced));

        // A manifest that is not one, one from beyond the ledger, and a scene with no place to stand.
        Assert.Equal("BAD_MANIFEST", Codes(Submit(_tiny, "m_latest", Finds)));
        Assert.Equal("BAD_MANIFEST", Codes(Submit(_tiny, "", Finds)));
        Assert.Equal("BAD_MANIFEST", Codes(Submit(_tiny, "m_2_0123abcd", Finds with { Id = "e_again" })));
        Assert.Equal("STALE_CONTEXT", Codes(Submit(_tiny, "m_99_abcdef_0123abcd", Finds with { Id = "e_again" })));
        var nowhere = _tiny.Story.SubmitScene(new SceneReport { Scene = "s7", Manifest = Manifest(_tiny, "s2"), Records = [Finds with { Id = "e_s7", Scene = "s7" }] });
        Assert.Equal("UNKNOWN_SCENE", Codes(nowhere));
        Assert.Equal("MALFORMED", Codes(_tiny.Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest(_tiny, "s2"), Records = [Finds with { Id = "e_n" }, null!] })));
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
    }

    [Fact]
    public void A_scene_has_one_contract_in_force_and_its_entries_are_checked()
    {
        Assert.True(_tiny.Put(ContractFor(KeyToPocket)).Applied);
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(ContractFor(BenLearns) with { Id = "c_again" })));
        // An edit is a retire and a put.
        var edit = _tiny.Apply(new RetireOp { Id = "c_s2", Reason = "correction" }, new PutOp { Record = ContractFor(BenLearns) with { Id = "c_v2" } });
        Assert.True(edit.Applied, Codes(edit));
        Assert.Equal("c_v2", _tiny.Story.State().Contract("s2")!.Id);

        SceneContract With(Intent i) => new() { Id = "c_bad", Scene = "s1", Intends = [i] };
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(With(new Intent { Change = IntentKind.Belief, Prop = "p_hid" }))));
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(With(new Intent { Change = IntentKind.Placement, Place = "mat" }))));
        Assert.Equal("UNKNOWN_REFERENCE", Codes(_tiny.Put(With(new Intent { Change = IntentKind.Fact, Prop = "p_nope" }))));
        Assert.Equal("PERSONA_HOLDER", Codes(_tiny.Put(With(new Intent { Change = IntentKind.Belief, Holder = "stranger", Prop = "p_hid" }))));
        Assert.Equal("UNKNOWN_REFERENCE", Codes(_tiny.Put(new SceneContract { Id = "c_s9", Scene = "s9" })));

        // What a contract relies on is checked against the state on entry, kind by kind.
        var relies = new SceneContract
        {
            Id = "c_s1", Scene = "s1",
            Relies =
            [
                new Intent { Change = IntentKind.Fact, Prop = "p_hid", Value = false },
                new Intent { Change = IntentKind.Fact, Prop = "p_hid", Value = true },
                new Intent { Change = IntentKind.Placement, Object = "key", Place = "mat" },
                new Intent { Change = IntentKind.Disclosure, Prop = "p_hid" },
            ],
        };
        Assert.True(_tiny.Put(relies).Applied);
        var all = _tiny.Story.State().CheckContract("s1");
        // This contract plans nothing, so what scene 1 is recorded as doing is all unplanned: Ann comes to know
        // she hid the key, the key goes under the mat, and the fact itself comes about.
        Assert.Equal(["b_ann", "f_hid", "l_mat"], all.Where(f => f.Code == "CONTRACT_UNPLANNED").Select(f => f.RecordId).Order());
        var findings = all.Where(f => f.Code.StartsWith("PRECONDITION_", StringComparison.Ordinal)).Select(f => f.Code + ": " + f.Message).ToList();
        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.StartsWith("PRECONDITION_FAILED", StringComparison.Ordinal) && f.Contains("p_hid is true. On entry it is not the case."));
        Assert.Contains(findings, f => f.StartsWith("PRECONDITION_NOT_ESTABLISHED", StringComparison.Ordinal) && f.Contains("No location is recorded for it at that point."));
        Assert.Contains(findings, f => f.StartsWith("PRECONDITION_FAILED", StringComparison.Ordinal) && f.Contains("The reader has been given nothing about it by then."));
    }

    [Fact]
    public void The_command_line_takes_a_scene_report_from_a_file()
    {
        var path = Path.Combine(_tiny.Root, "report.json");
        File.WriteAllText(path, NseJson.Write(new SceneReport { Scene = "s2", Manifest = Manifest(_tiny, "s2"), Records = [Finds, Pockets] }));
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["submit-scene", path, "--dir", _tiny.Root], output, new StringWriter()));
        Assert.StartsWith("Applied as 000002-cs_", output.ToString());

        // The same report again: the ids are taken. A refusal is exit code 1 and writes nothing.
        output = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["submit-scene", path, "--dir", _tiny.Root], output, new StringWriter()));
        Assert.StartsWith("Refused. Nothing was written.", output.ToString());
        Assert.Equal(2, _tiny.Story.ReadLedger().Count);
        Assert.Equal(2, Cli.Cli.Run(["submit-scene", "--dir", _tiny.Root], output, new StringWriter()));
    }
}
