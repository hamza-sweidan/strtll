using System.Globalization;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// On the example story: what a change set would do, worked out in memory and written nowhere; and the story as
/// one person knows it.
/// </summary>
public sealed class WhatIfRecapTests : IDisposable
{
    private readonly string _root;
    private readonly Story _story;

    public WhatIfRecapTests()
    {
        _root = Fixtures.NewTempDir();
        Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), _root);
        _story = Story.Open(_root);
    }

    public void Dispose() => Fixtures.DeleteDirectory(_root);

    private static ChangeSetDraft Draft(params Operation[] ops) => new() { Actor = "author", Ops = ops };

    /// <summary>Daniel works out in the kitchen how Sarah came by the letter: a retcon against secret SE1.</summary>
    private static BeliefChange DanielLearnsP10 => new()
    {
        Id = "B_whatif", Holder = "ch_daniel", Prop = "P10", Stance = Stance.HoldsTrue, Strength = Strength.Believes,
        StoryKey = 45.5, Scene = "sc_07", Channel = Channel.Inferred, Directness = Directness.Inferred,
    };

    /// <summary>A retcon of P10 to Daniel in sc_07 reports the changed scenes and the SE1 breach; nothing is written.</summary>
    [Fact]
    public void A_retcon_reports_the_breach_every_changed_answer_and_the_stale_contexts_and_writes_nothing()
    {
        var files = Directory.GetFiles(_story.LedgerDir).Order().ToList();
        var dump = _story.Dump();

        var result = _story.WhatIf(Draft(new PutOp { Record = DanielLearnsP10 }));
        Assert.False(result.WouldApply);
        var breach = Assert.Single(result.Findings);
        Assert.Equal(("SECRET_BROKEN", "B_whatif"), (breach.Code, breach.RecordId));
        Assert.Contains("secret SE1", breach.Message);

        // Every point after the kitchen in story time, and none before it.
        Assert.Equal(
        [
            "Across the story:",
            "  Daniel Kessler (ch_daniel) first takes P10 as true in: no scene -> sc_07",
            "On entry to scene 9, sc_09:",
            "  Daniel Kessler (ch_daniel) on P10: no record -> [correctly believes] (B_whatif)",
            "On entry to scene 10, sc_10:",
            "  Daniel Kessler (ch_daniel) on P10: no record -> [correctly believes] (B_whatif)",
            "On entry to scene 11, sc_11:",
            "  Daniel Kessler (ch_daniel) on P10: no record -> [correctly believes] (B_whatif)",
            "On entry to scene 12, sc_12:",
            "  Daniel Kessler (ch_daniel) on P10: no record -> [correctly believes] (B_whatif)",
            "At the end of the story:",
            "  Daniel Kessler (ch_daniel) on P10: no record -> [correctly believes] (B_whatif)",
        ], result.Changes);
        // Earlier scenes' contexts change too: in them Daniel no longer "never learns it", he learns it in scene 7.
        Assert.Equal(["sc_01", "sc_02", "sc_03", "sc_07", "sc_09", "sc_10", "sc_12"], result.Contexts.Select(c => c.Scene));

        var text = WhatIf.Text(result);
        Assert.StartsWith("WHAT IF · a change set from author of 1 operation(s), on ledger head 16 · worked out in memory; nothing was written\n", text);
        Assert.Contains("It would be refused as it stands (1 finding(s)). What follows is what it would change if it were let through:", text);
        Assert.Contains("Answers that would change (6):", text);
        Assert.Contains("Drafting contexts that would change (7); a draft written from the old one would be refused as stale:", text);

        Assert.Equal(files, Directory.GetFiles(_story.LedgerDir).Order().ToList());
        Assert.Equal(dump, _story.Dump());
    }

    [Fact]
    public void A_change_that_moves_no_answer_says_so_and_names_only_the_contexts_it_reaches()
    {
        var note = new Rule
        {
            Id = "G_marta", Type = "guidance", Text = "Marta keeps her own counsel; she answers questions with questions.",
            Scope = new RuleScope { Holders = ["ch_marta"], Scenes = ["sc_09"] },
        };
        var result = _story.WhatIf(Draft(new PutOp { Record = note }));
        Assert.True(result.WouldApply, string.Join("; ", result.Findings));
        Assert.Empty(result.Changes);
        Assert.Equal("sc_09", Assert.Single(result.Contexts).Scene);
        var text = WhatIf.Text(result);
        Assert.Contains("It passes every check: it could be applied as it stands.", text);
        Assert.Contains("No answer about the story would change.", text);
    }

    [Fact]
    public void A_retirement_shows_what_the_story_loses()
    {
        // Retiring the record by which the reader is told who R.P. was, with everything that rests on it.
        var reveal = _story.State().Model.Disclosures.First(d => d.Prop == "P05" && d.Mode == DisclosureMode.Stated);
        var result = _story.WhatIf(Draft(new RetireOp { Id = reveal.Id, Reason = "retcon: the reveal moves later" }));
        Assert.Contains(result.Changes, l => l.Contains("P05 is first stated to the reader in: " + reveal.Scene + " -> no scene"));
        Assert.Contains(result.Changes, l => l.StartsWith("  the reader on P05: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A retcon that leaves other records pointing at nothing is the one whose consequences most need showing. It
    /// is refused, and the answer still comes back: what-if must not fall over on the very state the checks refuse.
    /// </summary>
    [Fact]
    public void Retiring_a_record_that_others_rest_on_is_answered_with_its_findings_and_never_fails()
    {
        var gone = _story.WhatIf(Draft(new RetireOp { Id = "P10", Reason = "retcon: it never happened" }));
        Assert.False(gone.WouldApply);
        Assert.Contains(gone.Findings, f => f.Code == "DANGLING_REFERENCE");
        // The answers cannot be compared, and that is said, with what is missing; it is not passed off as no change.
        Assert.Equal("the answers could not be worked out with this change set laid on: No proposition P10.", Assert.Single(gone.Changes));
        Assert.NotEmpty(gone.Contexts);
        Assert.Contains("It would be refused as it stands", WhatIf.Text(gone));

        // A few of every kind of record that others refer to: each is answered, none throws.
        var records = _story.State().Model.Records.Select(t => t.Record).ToList();
        foreach (var kind in new[] { "entity", "scene", "scene_time", "proposition", "event", "assertion", "fact_change" })
            foreach (var record in records.Where(r => r.KindName == kind).Take(3))
                Assert.NotEmpty(WhatIf.Text(_story.WhatIf(Draft(new RetireOp { Id = record.Id, Reason = "retcon: asked about" }))));
    }

    [Fact]
    public void A_change_set_that_cannot_be_read_or_laid_on_is_refused_without_a_comparison()
    {
        var empty = _story.WhatIf(Draft());
        Assert.Equal("EMPTY", Assert.Single(empty.Findings).Code);
        Assert.Empty(empty.Changes);

        var twice = _story.WhatIf(Draft(new PutOp { Record = new Proposition { Id = "P01", Text = "Again." } }));
        Assert.False(twice.WouldApply);
    }

    [Fact]
    public void The_command_line_and_the_server_say_the_same_and_the_exit_code_says_whether_it_would_apply()
    {
        var dir = Fixtures.NewTempDir();
        try
        {
            var file = Path.Combine(dir, "retcon.json");
            File.WriteAllText(file, NseJson.Write(Draft(new PutOp { Record = DanielLearnsP10 })));
            var output = new StringWriter();
            Assert.Equal(1, Cli.Cli.Run(["what-if", file, "--dir", _root], output, new StringWriter()));
            Assert.Equal(WhatIf.Text(_story.WhatIf(Draft(new PutOp { Record = DanielLearnsP10 }))), output.ToString());
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
    }

    // ---------------------------------------------------------------------------------------------------- recap

    /// <summary>
    /// Sarah's recap to scene 9 agrees with her belief timeline as it is written out by hand in the fixtures. Every
    /// row of it dated before scene 9 is in the recap, in the same order, with the same story time and the same
    /// proposition, and nothing dated later is.
    /// </summary>
    [Fact]
    public void Sarahs_recap_to_scene_9_agrees_with_her_belief_timeline()
    {
        var report = File.ReadAllLines(Path.Combine(Fixtures.HarbourLetter, "sarah-belief-timeline.md"));
        var start = Array.IndexOf(report, "## Sarah Kessler: belief timeline");
        var rows = report.Skip(start + 4).TakeWhile(l => l.StartsWith('|'))
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Select(c => (Key: double.Parse(c[1], CultureInfo.InvariantCulture), Text: c[3][(c[3].IndexOf(' ') + 1)..]))
            .ToList();
        var nine = _story.State().Key("sc_09");
        var expected = rows.Where(r => r.Key < nine).ToList();
        Assert.Equal(27, expected.Count);

        var recap = Recap.Text(_story.State(), "ch_sarah", "sc_09", truth: false);
        var beliefs = recap.Split('\n')
            .Where(l => l.Contains("  they came to ") || l.Contains("  they did not know that") || l.Contains("  they could not decide whether"))
            .Select(l =>
            {
                var t = l.Trim();
                var key = double.Parse(t[..t.IndexOf(' ')], CultureInfo.InvariantCulture);
                var text = t[(t.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                return (Key: key, Text: text[..text.LastIndexOf(" (", text.LastIndexOf(" (", StringComparison.Ordinal) - 1, StringComparison.Ordinal)]);
            })
            .ToList();
        Assert.Equal(expected, beliefs);
    }

    [Fact]
    public void A_recap_is_in_the_persons_terms_and_in_story_time_order_with_labels_only_on_request()
    {
        var recap = Recap.Text(_story.State(), "ch_sarah", "sc_09", truth: false);
        Assert.StartsWith("RECAP FOR Sarah Kessler (ch_sarah), up to entry to scene 9, sc_09 (story time 50) · ledger head 16\n", recap);
        // The flashback is told eighth and happened first.
        Assert.True(recap.IndexOf("In scene 8, sc_08:", StringComparison.Ordinal) < recap.IndexOf("In scene 2, sc_02:", StringComparison.Ordinal));
        Assert.Contains("Daniel Kessler told them that: Marta has been taking money from the business. (A07)", recap);
        Assert.Contains("they said to Daniel Kessler that it is not so that: Marta has been taking money from the business. (A08)", recap);
        Assert.DoesNotContain("[knows]", recap);
        Assert.DoesNotContain("sc_09:", recap.Replace("scene 9, sc_09 (", ""));

        var labelled = Recap.Text(_story.State(), "ch_sarah", "sc_09", truth: true);
        Assert.Contains("[knows]", labelled);
        Assert.Contains("With the author's labels in square brackets", labelled);

        // Daniel's story to the end never has him learn how Sarah came by the letter: SE1 keeps it to her.
        var daniel = Recap.Text(_story.State(), "ch_daniel", null, truth: false);
        Assert.Contains("RECAP FOR Daniel Kessler (ch_daniel), to the end of the story", daniel);
        Assert.DoesNotContain("Sarah took the letter from Edmund's desk drawer on the night he died", daniel);

        Assert.Throws<ArgumentException>(() => Recap.Text(_story.State(), "obj_letter", null, truth: false));
        Assert.Throws<KeyNotFoundException>(() => Recap.Text(_story.State(), "ch_nobody", null, truth: false));
    }

    [Fact]
    public void A_persona_is_recapped_as_the_person_behind_it()
    {
        var persona = _story.State().Model.Entities.First(e => e.PersonaOf is not null);
        var recap = Recap.Text(_story.State(), persona.Id, null, truth: false);
        Assert.Contains($"{persona.Id} is a persona of {persona.PersonaOf}; the recap is the person's.", recap);
        Assert.Equal(Recap.Text(_story.State(), persona.PersonaOf!, null, truth: false), recap.Replace($"{persona.Id} is a persona of {persona.PersonaOf}; the recap is the person's.\n", ""));
    }

    [Fact]
    public void The_command_line_needs_exactly_one_point()
    {
        Assert.Equal(2, Cli.Cli.Run(["recap", "--holder", "ch_sarah", "--dir", _root], new StringWriter(), new StringWriter()));
        Assert.Equal(2, Cli.Cli.Run(["recap", "--holder", "ch_sarah", "--at", "sc_09", "--end", "--dir", _root], new StringWriter(), new StringWriter()));
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["recap", "--holder", "ch_sarah", "--at", "sc_09", "--dir", _root], output, new StringWriter()));
        Assert.Equal(Recap.Text(_story.State(), "ch_sarah", "sc_09", truth: false), output.ToString());
    }
}
