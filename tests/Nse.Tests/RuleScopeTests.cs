using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Rules that hold where they are scoped to, the author's notes as a rule type, and a closing recap of a
/// context's hard constraints by id. Notes on a person, with their stages marked, are what a drafter needs to
/// write that person; these tests pin that a note reaches exactly
/// the scenes it is scoped to and no others.
/// </summary>
public sealed class RuleScopeTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    private string Context(string scene) => ContextCompiler.Compile(_tiny.Story.State(), scene).Text;

    private static Rule Note(string id, string text, RuleScope? scope) => new() { Id = id, Type = "guidance", Text = text, Scope = scope };

    [Fact]
    public void A_note_scoped_to_a_person_reaches_only_the_scenes_they_are_in()
    {
        Assert.True(_tiny.Put(Note("g_ben", "Ben distrusts anything that comes too easily.", new RuleScope { Holders = ["ben"] })).Applied);
        Assert.DoesNotContain("g_ben", Context("s1"));
        var two = Context("s2");
        Assert.Contains("THE AUTHOR'S RULES AND NOTES FOR THIS SCENE (a note is the author's understanding, to write from; a rule is to keep to)", two);
        Assert.Contains("  g_ben, PROVISIONAL: do not build on this, note [for Ben]: Ben distrusts anything that comes too easily.", two);

        // A persona stands for the person behind it, in the scope as in the cast.
        using var other = new TinyStory();
        Assert.True(other.Put(Note("g_stranger", "Seen from outside, he is all hands.", new RuleScope { Holders = ["stranger"] })).Applied);
        Assert.Contains("g_stranger", ContextCompiler.Compile(other.Story.State(), "s2").Text);
        Assert.DoesNotContain("g_stranger", ContextCompiler.Compile(other.Story.State(), "s1").Text);

        // And the other way round: a note for the person holds where they are in the cast under another name.
        var third = other.Put(
            new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann", "stranger"] },
            new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 },
            Note("g_ben", "Ben never knocks.", new RuleScope { Holders = ["ben"] }));
        Assert.True(third.Applied, Codes(third));
        Assert.Contains(other.Story.State().RulesInScope("s3"), r => r.Rule.Id == "g_ben");
        Assert.DoesNotContain(other.Story.State().RulesInScope("s1"), r => r.Rule.Id == "g_ben");
    }

    [Fact]
    public void A_span_is_read_in_story_time_from_its_first_scene_up_to_but_not_including_its_last()
    {
        Assert.True(_tiny.Put(
            Note("g_from", "From here on the house feels watched.", new RuleScope { FromScene = "s2" }),
            Note("g_until", "Until the key is found, nobody looks under the mat.", new RuleScope { UntilScene = "s2" }),
            Note("g_only", "Only in the first scene.", new RuleScope { Scenes = ["s1"] })).Applied);
        var one = Context("s1");
        Assert.DoesNotContain("g_from", one);
        Assert.Contains("g_until", one);
        Assert.Contains("[until s2, in story time]", one);
        Assert.Contains("g_only", one);
        var two = Context("s2");
        Assert.Contains("[from s2, in story time]", two);
        Assert.DoesNotContain("g_until", two);
        Assert.DoesNotContain("g_only", two);
    }

    [Fact]
    public void A_rule_with_no_scope_holds_everywhere_and_a_never_settle_rule_keeps_its_own_section()
    {
        var put = _tiny.Put(
            new Proposition { Id = "p_why", Text = "Ann hid the key to keep Ben out.", Resolution = Resolution.Ambiguous },
            new Rule { Id = "r_style", Type = "style", Text = "Past tense throughout." },
            new Rule { Id = "r_open", Type = "never_resolve", Prop = "p_why", Text = "Why Ann hid it stays open." });
        Assert.True(put.Applied, Codes(put));
        foreach (var scene in new[] { "s1", "s2" })
        {
            var text = Context(scene);
            Assert.Contains("  r_style, PROVISIONAL: do not build on this, rule (style): Past tense throughout.", text);
            Assert.Contains("NEVER SETTLE THESE\n  p_why: Why Ann hid it stays open.", text);
            Assert.DoesNotContain("r_open, PROVISIONAL", text);
        }
        var brief = ContextCompiler.Brief(_tiny.Story.State());
        Assert.Contains("The author's rules and notes: 1 rule(s) and 0 note(s), each shown in the context of every scene it holds in.", brief);
    }

    [Fact]
    public void A_rule_in_scope_is_never_left_out_to_fit_a_budget()
    {
        Assert.True(_tiny.Put(Note("g_ben", "Ben distrusts anything that comes too easily.", new RuleScope { Holders = ["ben"] })).Applied);
        var state = _tiny.Story.State();
        // The tightest budget the context compiles at: everything that may be left out has been.
        var budget = Enumerable.Range(1, ContextCompiler.Compile(state, "s2", int.MaxValue).EstimatedTokens).First(b =>
        {
            try
            {
                ContextCompiler.Compile(state, "s2", b);
                return true;
            }
            catch (BudgetException)
            {
                return false;
            }
        });
        var tight = ContextCompiler.Compile(state, "s2", budget);
        Assert.NotEmpty(tight.Omitted);
        Assert.Contains("  g_ben, PROVISIONAL: do not build on this, note [for Ben]: Ben distrusts anything that comes too easily.", tight.Text);
    }

    [Fact]
    public void A_scope_that_holds_nowhere_or_points_at_nothing_is_refused()
    {
        foreach (var (rule, code, words) in new (Rule, string, string)[]
                 {
                     (new Rule { Id = "r1", Type = "never_establish", Text = "Open.", Scope = new RuleScope { Holders = ["ben"] } }, "BAD_VALUE", "takes no scope"),
                     (Note("r2", "Empty.", new RuleScope()), "BAD_VALUE", "the scope names nothing"),
                     (Note("r3", "Backwards.", new RuleScope { FromScene = "s2", UntilScene = "s1" }), "BAD_VALUE", "holds in no scene"),
                     (Note("r4", "Same.", new RuleScope { FromScene = "s1", UntilScene = "s1" }), "BAD_VALUE", "holds in no scene"),
                     (Note("r5", "Nobody.", new RuleScope { Holders = ["nobody"] }), "UNKNOWN_REFERENCE", "its entity nobody is not in the ledger"),
                     (Note("r6", "Nowhere.", new RuleScope { Scenes = ["s9"] }), "UNKNOWN_REFERENCE", "its scene s9 is not in the ledger"),
                     (Note("r8", "From nowhere.", new RuleScope { FromScene = "s9" }), "UNKNOWN_REFERENCE", "its scene s9 is not in the ledger"),
                     (Note("r9", "Until nowhere.", new RuleScope { UntilScene = "s9" }), "UNKNOWN_REFERENCE", "its scene s9 is not in the ledger"),
                 })
        {
            var result = _tiny.Put(rule);
            Assert.False(result.Applied, rule.Id);
            Assert.Contains(result.Findings, f => f.Code == code && f.RecordId == rule.Id && f.Message.Contains(words));
        }

        // A span needs both its ends in story time.
        var untimed = _tiny.Put(
            new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] },
            Note("r7", "Untimed.", new RuleScope { UntilScene = "s3" }));
        Assert.Contains(untimed.Findings, f => f.Code == "BAD_VALUE" && f.Message.Contains("s3, which has no place in story time"));

        // Retiring a scene a scope names would leave the rule pointing at nothing.
        Assert.True(_tiny.Put(Note("g_s2", "In two.", new RuleScope { Scenes = ["s2"] })).Applied);
        var gone = _tiny.Apply(new RetireOp { Id = "s2", Reason = "correction: cut" });
        Assert.Contains(gone.Findings, f => f.Code == "DANGLING_REFERENCE" && f.RecordId == "g_s2");
    }

    [Fact]
    public void A_span_whose_end_lost_its_story_time_is_shown_with_a_caveat_and_not_dropped()
    {
        Assert.True(_tiny.Put(Note("g_late", "Late on, Ann regrets it.", new RuleScope { FromScene = "s2" })).Applied);
        Assert.DoesNotContain("g_late", Context("s1"));
        // The scene's time is restated as a new record and the old one retired: in between, nothing says where s2 is.
        var restated = _tiny.Apply(new RetireOp { Id = "t2", Reason = "correction: to be restated" });
        Assert.True(restated.Applied, Codes(restated));
        Assert.Contains("[where it holds cannot be worked out: s2 has no place in story time now, so it is shown in case it holds]", Context("s1"));
    }

    [Fact]
    public void A_note_in_scope_is_part_of_the_context_so_a_draft_written_before_it_is_stale()
    {
        var manifest = ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId;
        // A note for the other scene changes nothing this draft rests on.
        Assert.True(_tiny.Put(Note("g_s1", "Ann is tired.", new RuleScope { Scenes = ["s1"] })).Applied);
        var fine = Submit(manifest, "e1");
        Assert.True(fine.Applied, string.Join("; ", fine.Findings));
        Assert.True(fine.Basis!.Rebased);

        // A note for this scene does: the draft was written without it.
        manifest = ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId;
        Assert.True(_tiny.Put(Note("g_s2", "Ben is in a hurry.", new RuleScope { Scenes = ["s2"] })).Applied);
        var stale = Submit(manifest, "e2");
        Assert.False(stale.Applied);
        Assert.Equal("STALE_CONTEXT", stale.Findings[0].Code);
        Assert.Contains(stale.Findings, f => f.Code == "CHANGED" && f.Message.Contains("g_s2"));
    }

    private SubmitResult Submit(string manifest, string id) => _tiny.Story.SubmitScene(new SceneReport
    {
        Scene = "s2", Manifest = manifest,
        Records =
        [
            new Event
            {
                Id = id, Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key.",
                Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
            },
        ],
    });

    [Fact]
    public void The_context_ends_its_parts_with_a_recap_of_the_hard_constraints_by_id()
    {
        Assert.True(_tiny.Put(
            new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] },
            new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s3" },
            Note("g_ben", "Ben distrusts luck.", new RuleScope { Holders = ["ben"] })).Applied);
        var text = Context("s2");
        var recap = text[text.IndexOf("\n\nKEEP TO THESE (the parts above, by id)", StringComparison.Ordinal)..text.IndexOf("\n\nManifest ", StringComparison.Ordinal)];
        Assert.Equal(
            "\n\nKEEP TO THESE (the parts above, by id)\n  Not to be given to the reader: p_hid\n  Not known to Ben, so not said, told or acted on: p_hid\n  The author's rules and notes: g_ben", recap);
        // It comes after every part and before the manifest, and it is no part of what the manifest stands for.
        Assert.True(text.IndexOf("KEEP TO THESE", StringComparison.Ordinal) > text.IndexOf(ContextCompiler.ProseTitle, StringComparison.Ordinal));
        Assert.DoesNotContain(ContextCompiler.Compile(_tiny.Story.State(), "s2").Sections, p => p.Title.StartsWith("KEEP TO THESE", StringComparison.Ordinal));

        // With nothing withheld, it says so rather than leaving the line out.
        using var plain = new TinyStory();
        Assert.Contains("\n  Not to be given to the reader: nothing at this point", ContextCompiler.Compile(plain.Story.State(), "s1").Text);
    }

    [Fact]
    public void A_rule_without_a_scope_is_written_as_before_so_old_ledgers_and_their_hashes_are_untouched()
    {
        var json = NseJson.Write<LedgerRecord>(new Rule { Id = "r", Type = "style", Text = "Past tense." });
        Assert.DoesNotContain("scope", json);
        var scoped = NseJson.Write<LedgerRecord>(Note("g", "Note.", new RuleScope { Holders = ["ben"], UntilScene = "s2" }));
        Assert.Contains("\"scope\": {", scoped);
        Assert.Contains("\"until_scene\": \"s2\"", scoped);
        var back = Assert.IsType<Rule>(NseJson.Read<LedgerRecord>(scoped));
        Assert.Equal(["ben"], back.Scope!.Holders);
    }
}
