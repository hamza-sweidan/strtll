using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// The word scan for tripwires, and the brief an auditor is given. The scan is
/// deterministic and is tested to the letter. The auditor is a model and is not run here; what is tested is that
/// its brief holds what the design says it must, and nothing that would make it a second drafting context.
/// </summary>
public sealed class ScanTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    /// <summary>An embargo on who hid the key, held until a scene that is not written yet, so it holds in every scene there is.</summary>
    private PutResult Embargo(params string[] tripwires) => _tiny.Put(
        new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] },
        new Embargo { Id = "em_hid", Prop = "p_hid", UntilScene = "s3", Tripwires = tripwires });

    [Fact]
    public void A_tripwire_that_cannot_run_or_matches_everything_is_refused()
    {
        foreach (var (pattern, why) in new[]
                 {
                     ("(unclosed", "is not a pattern the engine can run"),
                     ("", "it is empty"),
                     ("  ", "it is empty"),
                     (".*", "matches an empty passage"),
                     ("key|", "matches an empty passage"),
                 })
        {
            var result = Embargo("under the mat", pattern);
            Assert.False(result.Applied, pattern);
            var finding = Assert.Single(result.Findings, f => f.Code == "BAD_VALUE");
            Assert.Equal("em_hid", finding.RecordId);
            Assert.Contains(why, finding.Message);
        }
        Assert.True(Embargo("under the mat").Applied);
    }

    [Fact]
    public void The_scan_finds_tripwires_paragraph_by_paragraph_as_a_reader_sees_the_text()
    {
        Assert.True(Embargo("UNDER THE MAT", "said to the empty", "nowhere in the text").Applied);
        var scan = _tiny.Story.Scan("s1");
        Assert.Equal("manuscript/01-one.md lines 2-7", scan.Source);
        Assert.Equal(2, scan.Paragraphs);
        Assert.Equal(3, scan.Patterns);
        Assert.Empty(scan.Problems);
        // Case does not matter, and a phrase the file wraps over two lines is still found, at its paragraph's first line.
        Assert.Collection(scan.Hits,
            h =>
            {
                Assert.Equal(("em_hid", "p_hid", 4, 1, "under the mat"), (h.Embargo, h.Prop, h.Line, h.Paragraph, h.Matched));
                Assert.Equal("Ann hid the key under the mat.", h.Excerpt);
            },
            h =>
            {
                Assert.Equal((6, 2, "said to the empty"), (h.Line, h.Paragraph, h.Matched));
                Assert.Equal("\"I never touched it,\" Ann said to the empty hall.", h.Excerpt);
            });
        var text = DraftScan.Text(scan);
        Assert.StartsWith("TRIPWIRE SCAN OF s1 · manuscript/01-one.md lines 2-7 · 2 paragraph(s) · 1 embargo(es) in force, 3 pattern(s)\n", text);
        Assert.Contains("Passages to read (2). A hit is not a verdict: a hint the reader may miss is allowed.", text);
        Assert.Contains("  line 4, paragraph 1: \"under the mat\" matches em_hid (p_hid, held back until s3) in: Ann hid the key under the mat.", text);
        Assert.False(scan.Clean);
    }

    [Fact]
    public void An_embargo_that_has_lifted_is_not_looked_for_and_no_hit_is_not_called_a_clearance()
    {
        Assert.True(_tiny.Put(new Embargo { Id = "em_early", Prop = "p_hid", UntilScene = "s2", Tripwires = ["key"] }).Applied);
        var lifted = _tiny.Story.Scan("s2");
        Assert.Empty(lifted.InForce);
        Assert.Contains("No embargo is in force in this scene", DraftScan.Text(lifted));
        Assert.True(lifted.Clean);

        var quiet = _tiny.Story.Scan("s1", text: "Nothing of note happened in the hall.");
        Assert.Single(quiet.InForce);
        Assert.Empty(quiet.Hits);
        Assert.Contains("No tripwire matched. That is not a clearance: a leak can use words no pattern foresaw.", DraftScan.Text(quiet));

        // An embargo with no patterns cannot be scanned for, and the scan says so instead of passing the draft.
        Assert.True(_tiny.Put(
            new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] },
            new Embargo { Id = "em_bare", Prop = "p_hid", UntilScene = "s3" }).Applied);
        Assert.Contains("carry no tripwires, so the scan cannot point at anything", DraftScan.Text(_tiny.Story.Scan("s2")));
    }

    [Fact]
    public void A_draft_file_is_read_for_the_scene_it_holds_with_its_own_line_numbers()
    {
        Assert.True(Embargo("the key").Applied);
        var dir = Fixtures.NewTempDir();
        try
        {
            // A whole manuscript file: only the scene asked for is scanned, and lines are the file's.
            var whole = Path.Combine(dir, "chapter.md");
            File.WriteAllText(whole, "<!-- scene: s1 -->\nNothing here.\n\n<!-- scene: s2 -->\n# Two\n\nBen took the key.\n");
            var scan = _tiny.Story.Scan("s2", file: whole);
            var hit = Assert.Single(scan.Hits);
            Assert.Equal((7, 1, "the key"), (hit.Line, hit.Paragraph, hit.Matched));
            Assert.Empty(_tiny.Story.Scan("s1", file: whole).Hits);

            // A file of draft alone is all draft.
            var bare = Path.Combine(dir, "draft.txt");
            File.WriteAllText(bare, "First.\n\nSecond, with the key in it.\n");
            Assert.Equal(3, Assert.Single(_tiny.Story.Scan("s2", file: bare).Hits).Line);

            // A file with markers and none of them the scene's holds nothing of it, and that is not a clean scan.
            var other = Path.Combine(dir, "other.md");
            File.WriteAllText(other, "<!-- scene: s1 -->\nThe key.\n");
            var e = Assert.Throws<KeyNotFoundException>(() => _tiny.Story.Scan("s2", file: other));
            Assert.Contains("none of them is s2", e.Message);
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
        Assert.Throws<KeyNotFoundException>(() => _tiny.Story.Scan("s3"));
        Assert.Throws<ArgumentException>(() => _tiny.Story.Scan("s1", file: "a", text: "b"));
    }

    [Fact]
    public void A_draft_is_cut_into_paragraphs_exactly_as_the_same_text_is_in_the_manuscript()
    {
        Assert.True(Embargo("the key", "interlude").Applied);
        foreach (var body in new[]
                 {
                     // A heading with text straight under it: the heading is the title, and the rest is paragraph 1.
                     "# The key\nBen took it.\n\nSecond paragraph, with the key in it.\n",
                     // A heading further down is a paragraph like any other, and is counted as one.
                     "# Two\n\nFirst.\n\n# Interlude\n\nThird, with the key.\n",
                 })
        {
            var file = "<!-- scene: s2 -->\n" + body;
            File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"), file);
            var asItStands = _tiny.Story.Scan("s2");
            Assert.NotEmpty(asItStands.Hits);

            var asDraft = _tiny.Story.Scan("s2", text: file);
            Assert.Equal(asItStands.Paragraphs, asDraft.Paragraphs);
            Assert.Equal(asItStands.Hits, asDraft.Hits);

            // The scene's text alone is the same draft, one line shorter at the top.
            var alone = _tiny.Story.Scan("s2", text: body);
            Assert.Equal(asItStands.Paragraphs, alone.Paragraphs);
            Assert.Equal(asItStands.Hits.Select(h => h with { Line = h.Line - 1 }), alone.Hits);
        }
    }

    [Fact]
    public void An_excerpt_is_never_cut_through_the_middle_of_a_character()
    {
        Assert.True(Embargo("the key").Applied);
        // A character outside the basic plane is two units long. Over these lengths one of them lands exactly
        // where the excerpt begins, and then where it ends.
        const string face = "\U0001F600";
        static bool Whole(string s) => Enumerable.Range(0, s.Length).All(i =>
            char.IsHighSurrogate(s[i]) ? i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
            : !char.IsLowSurrogate(s[i]) || (i > 0 && char.IsHighSurrogate(s[i - 1])));
        Assert.False(Whole(face[..1]));
        foreach (var pad in Enumerable.Range(70, 20))
        {
            var text = face + new string('x', pad) + " the key " + new string('y', pad) + face + " and more words after it";
            var hit = Assert.Single(_tiny.Story.Scan("s1", text: text).Hits);
            Assert.True(Whole(hit.Excerpt), $"at {pad}: {hit.Excerpt}");
        }
    }

    [Fact]
    public void A_pattern_that_runs_too_long_is_reported_as_not_checked_and_never_hangs()
    {
        Assert.True(Embargo("(a|aa)+$").Applied);
        var scan = _tiny.Story.Scan("s1", text: new string('a', 60) + "b");
        var problem = Assert.Single(scan.Problems);
        Assert.Contains("took too long on paragraph 1 (line 1) and was not finished there. Read that paragraph yourself", problem);
        Assert.False(scan.Clean);
        Assert.Contains("NOT CHECKED: embargo em_hid", DraftScan.Text(scan));
    }

    [Fact]
    public void A_submission_whose_text_trips_a_wire_is_applied_with_a_note_for_the_author()
    {
        Assert.True(Embargo("his pocket").Applied);
        var manifest = ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId;
        var result = _tiny.Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = manifest,
            Records =
            [
                new Event
                {
                    Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key.",
                    Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
                },
            ],
        });
        Assert.True(result.Applied, string.Join("; ", result.Findings));
        var note = Assert.Single(result.Notes, n => n.Code == "TRIPWIRE");
        Assert.Equal("em_hid", note.RecordId);
        Assert.Contains("line 4 of manuscript/02-two.md: \"his pocket\" matches a tripwire of em_hid, which holds p_hid back until s3", note.Message);
        // The passage is put in front of the author. Starting another session to audit the scene is theirs to ask for.
        Assert.Contains("A hint is allowed. Show the author the passage; an audit of the scene is theirs to ask for", note.Message);
        Assert.DoesNotContain("Have the scene audited", SubmitReport.Text(result));
        Assert.Contains("TRIPWIRE em_hid: line 4", SubmitReport.Text(result));
    }

    // ------------------------------------------------------------------- the example story

    private static Story Example(out string root)
    {
        root = Fixtures.NewTempDir();
        Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), root);
        return Story.Open(root);
    }

    /// <summary>
    /// The example story as it stands trips none of its own wires. A draft of a scene that names what an embargo
    /// still holds back trips that embargo's wire and no other.
    /// </summary>
    [Fact]
    public void The_example_as_it_stands_trips_nothing_and_a_draft_that_names_the_withheld_trips_its_wire()
    {
        var story = Example(out var root);
        try
        {
            Assert.All(story.ScanAll(), r => Assert.True(r.Clean, DraftScan.Text(r)));

            var hit = Assert.Single(story.Scan("sc_06", text: "The tide was out.\n\nShe thought of Jonas, and said nothing.").Hits);
            Assert.Equal(("EM3", 3, 2, "Jonas"), (hit.Embargo, hit.Line, hit.Paragraph, hit.Matched));
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    [Fact]
    public void The_audit_brief_holds_what_must_not_be_given_away_the_hits_and_the_form_of_the_report()
    {
        var story = Example(out var root);
        try
        {
            var brief = story.AuditBrief("sc_06");
            Assert.StartsWith("AUDIT OF SCENE 6, sc_06", brief);
            Assert.Contains("· leaks · ledger head 16\n", brief);
            Assert.Contains("Read the scene's text, and no other prose: manuscript/06-low-water.md lines 2-32.", brief);
            Assert.Contains("WHAT THE READER MUST NOT BE GIVEN YET", brief);
            Assert.Contains("  P09: ", brief);
            Assert.Contains("embargo EM3 until scene 7, sc_07", brief);
            Assert.Contains("WHAT EACH PERSON PRESENT DOES NOT KNOW", brief);
            Assert.Contains("PASSAGES THE WORD SCAN POINTS AT\n  None. That is not a clearance: read all of it.", brief);
            Assert.Contains("  LEAK <proposition id> <reader, or the id of the person> line <N>: \"<the exact words of the text>\" - <why, in a few words>", brief);
            Assert.Contains("CLEAN if you found no leak, or\nLEAKS and the count.", brief);
            // It is a reading task: no prose is copied in, no beliefs on entry, and no manifest to draft from.
            Assert.DoesNotContain("beliefs on entry", brief);
            Assert.DoesNotContain("Manifest", brief);
            Assert.DoesNotContain("Sarah walked", brief);

            var complete = story.AuditBrief("sc_06", AuditMode.Completeness);
            Assert.Contains("· completeness ·", complete);
            Assert.Contains("ALREADY RECORDED AS HAPPENING IN THIS SCENE", complete);
            Assert.Contains("FACTS IN PLAY, BY ID (use these ids; a fact not listed is NEW)", complete);
            Assert.Contains("  MISSING <belief, statement, disclosure, move or event> <proposition id or NEW> line <N>:", complete);
            Assert.DoesNotContain("WHAT THE READER MUST NOT BE GIVEN YET", complete);

            Assert.Throws<KeyNotFoundException>(() => story.AuditBrief("sc_99"));
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    [Fact]
    public void The_audit_brief_guards_a_fact_whose_reveal_is_not_written_yet_and_words_a_false_fact_as_false()
    {
        Assert.True(Embargo("under the mat").Applied);
        var put = _tiny.Put(
            new Proposition { Id = "p_gone", Text = "The key is gone." },
            new FactChange { Id = "f_gone", Prop = "p_gone", Value = false, StoryKey = 10 },
            _tiny.BenBelief("b_wrong", 20.1, Stance.HoldsFalse, Channel.Assumed));
        Assert.True(put.Applied, Codes(put));
        var brief = _tiny.Story.AuditBrief("s2");
        // No disclosure says when the reader is told, since that scene is not written. Only the embargo puts the
        // fact in front of the auditor, and without this line nobody would be looking for it.
        Assert.Contains("  p_hid: Ann hid the key under the mat. (embargo em_hid until s3, not in the manuscript yet)\n", brief);
        // A fact canon has as false is put as false, never as a bare sentence the auditor would take for true.
        Assert.Contains("    p_gone: It is NOT true that: The key is gone.\n", brief);
        // Someone who holds the opposite is marked: what they would say is not what is so.
        Assert.Contains("    p_hid: Ann hid the key under the mat. (they believe the opposite)\n", brief);

        // A scene with text and a record, and no place in story time, cannot be audited, and the refusal says why.
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "03-three.md"), "<!-- scene: s3 -->\n# Three\n\nAnn waited.\n");
        var untimed = Assert.Throws<KeyNotFoundException>(() => _tiny.Story.AuditBrief("s3"));
        Assert.Contains("so what each person knows on entry to it cannot be worked out", untimed.Message);
    }

    [Fact]
    public void A_submission_whose_scan_could_not_finish_says_so_and_is_not_passed_off_as_clean()
    {
        Assert.True(Embargo("(a|aa)+$").Applied);
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "02-two.md"), TinyStory.Scene2 + "\n" + new string('a', 60) + "b\n");
        var manifest = ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId;
        var result = _tiny.Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = manifest,
            Records =
            [
                new Event
                {
                    Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key.",
                    Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
                },
            ],
        });
        Assert.True(result.Applied, string.Join("; ", result.Findings));
        var note = Assert.Single(result.Notes, n => n.Code == "TRIPWIRE_NOT_RUN");
        Assert.Contains("took too long on paragraph 2", note.Message);
    }

    [Fact]
    public void A_tripwire_already_in_the_ledger_that_cannot_run_is_reported_and_never_thrown()
    {
        // The checks refuse such a pattern on the way in. A ledger written before they did may still hold one, and
        // a scan must say so every time: it neither falls over nor passes the draft.
        var state = _tiny.Story.State();
        var old = new Tracked(
            new Embargo { Id = "em_old", Prop = "p_hid", UntilScene = "s9", Tripwires = ["(unclosed", "the key"] },
            state.Model.Records.Count, 1, Approval.Ratified);
        var holding = new StoryState(new StoryModel(state.Model.Records.Append(old), state.Model.Head), state.Telling);
        var scan = DraftScan.Run(holding, "s1", DraftScan.Paragraphs(state.Telling[0]), "the scene as it stands");
        Assert.Contains("embargo em_old: tripwire \"(unclosed\" was not run: it is not a pattern the engine can run", Assert.Single(scan.Problems));
        Assert.Equal("the key", Assert.Single(scan.Hits).Matched);
        Assert.False(scan.Clean);
    }

    [Fact]
    public void A_pattern_that_matches_nothing_at_every_word_break_reports_only_the_words_it_finds()
    {
        // It is not refused: against an empty passage it does not match. Inside a paragraph it matches no text at
        // all at every word break, and none of those is a hit.
        Assert.True(Embargo(@"\b(mat)?").Applied);
        Assert.Equal("mat", Assert.Single(_tiny.Story.Scan("s1").Hits).Matched);
    }

    [Fact]
    public void The_command_line_scans_and_says_by_its_exit_code_whether_anything_matched()
    {
        Assert.True(Embargo("empty hall").Applied);
        var output = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["scan", "--scene", "s1", "--dir", _tiny.Root], output, new StringWriter()));
        Assert.Contains("\"empty hall\" matches em_hid", output.ToString());
        Assert.Equal(0, Cli.Cli.Run(["scan", "--scene", "s2", "--dir", _tiny.Root], new StringWriter(), new StringWriter()));
        output = new StringWriter();
        Assert.Equal(1, Cli.Cli.Run(["scan", "--all", "--dir", _tiny.Root], output, new StringWriter()));
        Assert.Contains("TRIPWIRE SCAN OF s1", output.ToString());
        Assert.Contains("TRIPWIRE SCAN OF s2", output.ToString());

        var error = new StringWriter();
        Assert.Equal(2, Cli.Cli.Run(["scan", "--all", "--file", "x.md", "--dir", _tiny.Root], new StringWriter(), error));
        Assert.Contains("--file is a draft of one scene", error.ToString());
        error = new StringWriter();
        Assert.Equal(2, Cli.Cli.Run(["scan", "--dir", _tiny.Root], new StringWriter(), error));
        Assert.Contains("scan needs exactly one of --scene ID or --all", error.ToString());
        Assert.Equal(2, Cli.Cli.Run(["audit", "--dir", _tiny.Root], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void Setup_leaves_the_auditor_out_unless_it_is_asked_for_and_nothing_tells_a_session_to_start_one()
    {
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--dir", _tiny.Root], output, new StringWriter()));
        // An audit is another model session. Nothing a story folder is given starts one by default.
        var agent = Path.Combine(_tiny.Root, ".claude", "agents", "scene-auditor.md");
        Assert.False(File.Exists(agent));
        Assert.Contains("  left out  .claude/agents/scene-auditor.md (an audit is another model session, so it is opt-in: --with-auditor writes the agent)", output.ToString());
        var settings = File.ReadAllText(Path.Combine(_tiny.Root, ".claude", "settings.json"));
        Assert.Contains("mcp__nse__scan_draft", settings);
        Assert.Contains("mcp__nse__audit_brief", settings);
        var skill = File.ReadAllText(Path.Combine(_tiny.Root, ".claude", "skills", "scene-work", "SKILL.md")).Replace("\r\n", "\n");
        Assert.DoesNotContain("Have the scene audited", skill);
        Assert.Contains("**An audit is the author's to ask for.**", skill);
        Assert.Contains("Do not start one\non your own.", skill);
        var claude = File.ReadAllText(Path.Combine(_tiny.Root, "CLAUDE.md")).Replace("\r\n", "\n");
        Assert.Contains("then `submit_scene`.\n", claude);
        Assert.Contains("- **An audit of a scene by a fresh reviewer is opt-in.**", claude);
        foreach (var ratify in new[] { false, true })
        {
            var told = Cli.McpHost.Instructions(ratify);
            Assert.DoesNotContain("Have the scene audited", told);
            Assert.Contains("An audit (audit_brief, read by a fresh reviewer) is theirs to ask for: do not\n   start one unasked.", told);
        }

        // Asked for, the agent is written: it reads and reports and can do nothing else, and it says when it is to be used.
        Assert.Equal(0, Cli.Cli.Run(["setup-claude", "--engine", "C:/engine/nse.dll", "--with-auditor", "--dir", _tiny.Root], new StringWriter(), new StringWriter()));
        var auditor = File.ReadAllText(agent).Replace("\r\n", "\n");
        Assert.StartsWith("---\nname: scene-auditor\n", auditor);
        Assert.Contains("\ntools: Read, mcp__nse__audit_brief\n", auditor);
        Assert.Contains("Use only when the author asks for an audit of a scene; never as a routine step after a submission.", auditor);
    }
}
