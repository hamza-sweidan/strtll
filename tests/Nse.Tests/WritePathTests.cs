using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;
using Nse.Text;

namespace Nse.Tests;

/// <summary>A two-scene story small enough to read in a test, for the checks the write path makes.</summary>
public sealed class TinyStory : IDisposable
{
    public Story Story { get; }
    public string Root { get; }

    public const string Scene1 = "<!-- scene: s1 -->\n# One\n\nAnn hid the key under the mat. Ben was not there.\n\n\"I never touched it,\" Ann said\nto the empty hall.\n";
    public const string Scene2 = "<!-- scene: s2 -->\n# Two\n\nBen found the key under the mat and put it in his pocket.\n";

    /// <param name="annInSceneTwo">Put Ann in the cast of scene 2 as well, for tests that need two people in one scene.</param>
    public TinyStory(bool annInSceneTwo = false)
    {
        Root = Fixtures.NewTempDir();
        Story = Story.Init(Root);
        File.WriteAllText(Path.Combine(Story.ManuscriptDir, "01-one.md"), Scene1);
        File.WriteAllText(Path.Combine(Story.ManuscriptDir, "02-two.md"), Scene2);
        var result = Put(
            new Entity { Id = "ann", Type = EntityKind.Character, Name = "Ann" },
            new Entity { Id = "ben", Type = EntityKind.Character, Name = "Ben" },
            new Entity { Id = "stranger", Type = EntityKind.Character, Name = "the stranger", PersonaOf = "ben" },
            new Entity { Id = "key", Type = EntityKind.Object, Name = "the key" },
            new Entity { Id = "mat", Type = EntityKind.Place, Name = "under the mat" },
            new Entity { Id = "pocket", Type = EntityKind.Place, Name = "Ben's pocket" },
            new SceneRecord { Id = "s1", Pov = "ann", Cast = ["ann"] },
            new SceneRecord { Id = "s2", Pov = "ben", Cast = annInSceneTwo ? ["ben", "ann"] : ["ben"] },
            new SceneTime { Id = "t1", Scene = "s1", StoryKey = 10 },
            new SceneTime { Id = "t2", Scene = "s2", StoryKey = 20 },
            new Proposition { Id = "p_hid", Text = "Ann hid the key under the mat." },
            new FactChange { Id = "f_hid", Prop = "p_hid", Value = true, StoryKey = 10.1 },
            new Event
            {
                Id = "e_hide", Scene = "s1", StoryKey = 10.1, Summary = "Ann hides the key.",
                Participants = new Dictionary<string, ParticipantRole> { ["ann"] = ParticipantRole.Actor },
                Quote = "Ann hid the key under the mat.",
            },
            new Assertion
            {
                Id = "a_deny", Scene = "s1", StoryKey = 10.2, Type = AssertionKind.Dialogue, Speaker = "ann",
                Prop = "p_hid", Polarity = false, Quote = "\"I never touched it,\" Ann said to the empty hall.",
            },
            new BeliefChange
            {
                Id = "b_ann", Holder = "ann", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain,
                StoryKey = 10.1, Scene = "s1", Channel = Channel.TookPart, Via = "e_hide",
            },
            new Placement { Id = "l_mat", Object = "key", Place = "mat", StoryKey = 10.1, Scene = "s1", Cause = "e_hide" });
        if (!result.Applied)
            throw new InvalidOperationException("the tiny story should load: " + string.Join("; ", result.Findings));
    }

    public PutResult Put(params LedgerRecord[] records) => Story.Put(new ChangeSetDraft
    {
        Actor = "author", Ops = records.Select(r => (Operation)new PutOp { Record = r }).ToList(),
    });

    public PutResult Apply(params Operation[] ops) => Story.Put(new ChangeSetDraft { Actor = "author", Ops = ops });

    public BeliefChange BenBelief(string id, double key, Stance stance = Stance.HoldsTrue, Channel channel = Channel.Inferred,
        string? via = null, string? quote = null) => new()
    {
        Id = id, Holder = "ben", Prop = "p_hid", Stance = stance,
        Strength = stance is Stance.HoldsTrue or Stance.HoldsFalse ? Strength.Believes : null,
        StoryKey = key, Scene = "s2", Channel = channel, Via = via, Quote = quote,
    };

    public void Dispose() => Fixtures.DeleteDirectory(Root);
}

public sealed class WritePathTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private static string Codes(PutResult r) => string.Join(",", r.Findings.Select(f => f.Code));

    /// <summary>A quote that is not in the cited scene word for word is refused, and the nearest passage is named.</summary>
    [Fact]
    public void A_quote_that_is_not_in_the_scene_is_refused_with_the_nearest_passage()
    {
        var before = _tiny.Story.ReadLedger().Count;
        var result = _tiny.Put(_tiny.BenBelief("b_ben", 20.1, quote: "Ben found a key beneath the mat"));
        Assert.False(result.Applied);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("QUOTE_NOT_FOUND", finding.Code);
        Assert.Equal("b_ben", finding.RecordId);
        Assert.Contains("Nearest passage: \"Ben found the key under the mat\"", finding.Message);
        Assert.Equal(before, _tiny.Story.ReadLedger().Count);
        Assert.False(_tiny.Story.State().BeliefsBefore("ben", StoryState.End).ContainsKey("p_hid"));
    }

    [Fact]
    public void A_quote_survives_rewrapped_lines_but_not_changed_words()
    {
        // In the file the sentence breaks after "said"; the quote has it on one line.
        Assert.True(_tiny.Put(_tiny.BenBelief("b1", 20.1, quote: "Ben found the key under the mat")).Applied);
        var other = _tiny.Put(new Event
        {
            Id = "e2", Scene = "s1", StoryKey = 10.3, Summary = "Ann denies it.", Quote = "Ann said\n   to the empty hall.",
        });
        Assert.True(other.Applied, Codes(other));
        var wrong = _tiny.Put(new Event { Id = "e3", Scene = "s1", StoryKey = 10.4, Summary = "x", Quote = "Ann said to the empty room." });
        Assert.Equal("QUOTE_NOT_FOUND", Codes(wrong));
    }

    [Fact]
    public void A_quote_must_cite_a_scene_that_is_in_the_manuscript()
    {
        Assert.True(_tiny.Put(new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann"] }).Applied);
        var planned = _tiny.Put(new Event { Id = "e9", Scene = "s3", StoryKey = 30, Summary = "x", Quote = "anything" });
        Assert.Equal("SCENE_NOT_IN_MANUSCRIPT", Codes(planned));
        var nowhere = _tiny.Put(new BeliefChange
        {
            Id = "b9", Holder = "ben", Prop = "p_hid", Stance = Stance.Unaware, StoryKey = 5, Channel = Channel.Background, Quote = "anything",
        });
        Assert.Equal("QUOTE_NEEDS_SCENE", Codes(nowhere));
    }

    [Fact]
    public void Nobody_learns_from_something_they_were_not_present_at_whatever_the_channel_is_called()
    {
        foreach (var channel in new[] { Channel.Witnessed, Channel.TookPart, Channel.Told, Channel.Inferred })
        {
            var result = _tiny.Put(_tiny.BenBelief("b_" + channel, 20.1, channel: channel, via: "e_hide"));
            Assert.False(result.Applied);
            Assert.Contains(result.Findings, f => f.Code == "NOT_PRESENT");
        }
        Assert.Equal("SOURCE_REQUIRED", Codes(_tiny.Put(_tiny.BenBelief("b_w", 20.1, channel: Channel.Witnessed))));
        Assert.True(_tiny.Put(_tiny.BenBelief("b_ok", 20.1, channel: Channel.Inferred)).Applied);
    }

    [Fact]
    public void A_belief_cannot_be_dated_before_its_source()
    {
        var early = _tiny.Put(new BeliefChange
        {
            Id = "b_early", Holder = "ann", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Certain,
            StoryKey = 9, Channel = Channel.TookPart, Via = "e_hide",
        });
        Assert.Equal("BEFORE_SOURCE", Codes(early));
    }

    [Fact]
    public void Being_told_needs_an_assertion_addressed_to_the_hearer()
    {
        Assert.True(_tiny.Put(new Assertion
        {
            Id = "a_tell", Scene = "s2", StoryKey = 20.2, Type = AssertionKind.Dialogue, Speaker = "ann",
            Addressees = ["ann"], Overhearers = ["ben"], Prop = "p_hid", Quote = "Ben found the key",
        }).Applied);
        var told = _tiny.Put(_tiny.BenBelief("b_told", 20.3, channel: Channel.Told, via: "a_tell"));
        Assert.Equal("CHANNEL_MISMATCH", Codes(told));
        Assert.True(_tiny.Put(_tiny.BenBelief("b_heard", 20.3, channel: Channel.Overheard, via: "a_tell")).Applied);
    }

    [Fact]
    public void References_must_resolve_and_ids_are_never_reused()
    {
        var unknown = _tiny.Put(new BeliefChange
        {
            Id = "b_x", Holder = "nobody", Prop = "p_nope", Stance = Stance.Unaware, StoryKey = 1, Channel = Channel.Background,
        });
        Assert.Equal(2, unknown.Findings.Count(f => f.Code == "UNKNOWN_REFERENCE"));
        var again = _tiny.Put(new Entity { Id = "ann", Type = EntityKind.Character, Name = "Another Ann" });
        Assert.Equal("LEDGER", Codes(again));
        Assert.Contains("already in the ledger", again.Findings[0].Message);
        Assert.Equal("EMPTY", Codes(_tiny.Apply()));
    }

    [Fact]
    public void Beliefs_attach_to_the_person_not_the_persona()
    {
        var result = _tiny.Put(new BeliefChange
        {
            Id = "b_p", Holder = "stranger", Prop = "p_hid", Stance = Stance.Unaware, StoryKey = 20.1, Channel = Channel.Explicit,
        });
        Assert.Equal("PERSONA_HOLDER", Codes(result));
    }

    [Fact]
    public void A_record_that_lets_an_outsider_in_on_a_secret_is_refused()
    {
        Assert.True(_tiny.Put(new Secret { Id = "se1", Props = ["p_hid"], KnownOnlyTo = ["ann"] }).Applied);
        Assert.Equal("SECRET_BROKEN", Codes(_tiny.Put(_tiny.BenBelief("b_in", 20.1))));
        // Being wrong about it, or unaware of it, breaks nothing.
        Assert.True(_tiny.Put(_tiny.BenBelief("b_wrong", 20.1, stance: Stance.HoldsFalse)).Applied);
    }

    [Fact]
    public void An_object_cannot_move_before_its_cause()
    {
        Assert.True(_tiny.Put(new Event
        {
            Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key.",
            Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor },
        }).Applied);
        var early = _tiny.Put(new Placement { Id = "l_early", Object = "key", Place = "pocket", StoryKey = 15, Cause = "e_find" });
        Assert.Equal("BEFORE_CAUSE", Codes(early));
        Assert.True(_tiny.Put(new Placement
        {
            Id = "l_pocket", Object = "key", Place = "pocket", StoryKey = 20.1, Scene = "s2", Cause = "e_find",
            Quote = "put it in his pocket",
        }).Applied);
        var s = _tiny.Story.State();
        Assert.Null(s.Location("key", s.Key("s1")));
        Assert.Equal("mat", s.Location("key", s.Key("s2"))!.Place);
        Assert.Equal("pocket", s.Location("key", StoryState.End)!.Place);
    }

    [Fact]
    public void Records_that_tie_in_story_time_are_reported_as_unresolved_not_guessed()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_a", 20.1), _tiny.BenBelief("b_b", 20.1, stance: Stance.HoldsFalse)).Applied);
        var s = _tiny.Story.State();
        var view = s.BeliefsBefore("ben", StoryState.End)["p_hid"];
        Assert.True(view.Unresolved);
        Assert.Equal(["b_a", "b_b"], view.Conflict);
        Assert.Equal("ORDER NOT ESTABLISHED", s.Classify(view, StoryState.End));
        // A later record settles it.
        Assert.True(_tiny.Put(_tiny.BenBelief("b_c", 20.2)).Applied);
        Assert.Equal("correctly believes", _tiny.Story.State().Classify(_tiny.Story.State().BeliefsBefore("ben", StoryState.End)["p_hid"], StoryState.End));
    }

    [Fact]
    public void An_edit_is_a_retire_and_a_put_and_the_old_head_keeps_its_answer()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_old", 20.1, stance: Stance.HoldsFalse)).Applied);
        var oldHead = _tiny.Story.ReadLedger().Count;
        var oldDump = _tiny.Story.Dump();

        Assert.Equal("REASON_REQUIRED", Codes(_tiny.Apply(new RetireOp { Id = "b_old", Reason = " " })));
        var edit = _tiny.Apply(new RetireOp { Id = "b_old", Reason = "correction" },
            new PutOp { Record = _tiny.BenBelief("b_new", 20.1) });
        Assert.True(edit.Applied, Codes(edit));

        var now = _tiny.Story.State();
        Assert.Equal("b_new", now.BeliefsBefore("ben", StoryState.End)["p_hid"].Id);
        Assert.Equal("b_old", _tiny.Story.State(oldHead).BeliefsBefore("ben", StoryState.End)["p_hid"].Id);
        Assert.Equal(oldDump, _tiny.Story.Dump(oldHead));
        Assert.Equal("LEDGER", Codes(_tiny.Apply(new RetireOp { Id = "b_old", Reason = "duplicate" })));
    }

    [Fact]
    public void Retiring_a_record_must_not_leave_another_pointing_at_nothing()
    {
        var result = _tiny.Apply(new RetireOp { Id = "e_hide", Reason = "retcon" });
        Assert.False(result.Applied);
        Assert.Equal(["b_ann", "l_mat"], result.Findings.Where(f => f.Code == "DANGLING_REFERENCE").Select(f => f.RecordId).Order());
    }

    [Fact]
    public void A_rejected_record_is_kept_for_the_trail_and_never_served()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_guess", 20.1)).Applied);
        var head = _tiny.Story.ReadLedger().Count;
        Assert.Equal(Approval.Provisional, _tiny.Story.State().Model.ById["b_guess"].ApprovalAt(head));
        Assert.True(_tiny.Apply(new RejectOp { Ids = ["b_guess"] }).Applied);
        Assert.False(_tiny.Story.State().BeliefsBefore("ben", StoryState.End).ContainsKey("p_hid"));
        Assert.True(_tiny.Story.State(head).BeliefsBefore("ben", StoryState.End).ContainsKey("p_hid"));

        Assert.True(_tiny.Apply(new RatifyOp { Ids = ["b_ann"] }).Applied);
        var state = _tiny.Story.State();
        Assert.Equal(Approval.Ratified, state.Model.ById["b_ann"].ApprovalAt(state.Model.Head));
    }

    [Fact]
    public void Only_one_writer_at_a_time_may_append_to_the_ledger()
    {
        var lockFile = _tiny.Story.WriteLockPath;
        using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = _tiny.Put(_tiny.BenBelief("b_wait", 20.1));
            Assert.Equal("LOCKED", Codes(blocked));
            Assert.Single(_tiny.Story.ReadLedger());
        }
        Assert.True(_tiny.Put(_tiny.BenBelief("b_wait", 20.1)).Applied);
        // The lock file is left in place between writers: deleting it on release opens a race on Unix. It sits
        // beside the ledger it guards, not under .nse/, which anyone may clear out at any time, and the story's
        // .gitignore keeps it out of git.
        Assert.True(File.Exists(lockFile));
        Assert.Equal(Path.Combine(_tiny.Story.LedgerDir, ".lock"), lockFile);
        Assert.Equal([".lock"], Directory.GetFiles(_tiny.Story.LedgerDir).Select(Path.GetFileName).Where(f => !f!.EndsWith(".json", StringComparison.Ordinal)));
        Assert.Contains("ledger/.lock", File.ReadAllLines(Path.Combine(_tiny.Root, ".gitignore")));
        Directory.Delete(Path.Combine(_tiny.Root, ".nse"), recursive: true);
        Assert.True(File.Exists(lockFile));
        Assert.True(_tiny.Put(_tiny.BenBelief("b_next", 20.2)).Applied);
        Assert.Equal(3, _tiny.Story.ReadLedger().Count);
    }

    [Fact]
    public void Init_on_an_existing_story_adds_what_its_gitignore_lacks_and_keeps_the_rest()
    {
        var ignore = Path.Combine(_tiny.Root, ".gitignore");
        File.WriteAllText(ignore, "# mine\nnotes/\n.nse/");
        Story.Init(_tiny.Root);
        Assert.Equal(
            [
                "# mine", "notes/", ".nse/",
                "# Held while a change set is being appended. It stays in place and is not part of the story.", "ledger/.lock",
                "# What a session was in the middle of: working notes, local to this machine and not part of the story.", "work/tasks/",
            ],
            File.ReadAllLines(ignore));
        Story.Init(_tiny.Root);
        Assert.Equal(7, File.ReadAllLines(ignore).Length);
    }

    [Fact]
    public void A_lie_is_derived_from_the_claim_and_the_speakers_belief()
    {
        var s = _tiny.Story.State();
        // Ann denies hiding the key, having hidden it.
        Assert.Equal("lie: false", s.ClassifyAssertion("a_deny"));
        Assert.Equal("knows", s.Classify(s.BeliefsBefore("ann", s.Key("s2"))["p_hid"], s.Key("s2")));
        Assert.Equal("true", s.Canon("p_hid"));
        Assert.False(s.TruthAt("p_hid", 10));
        Assert.True(s.TruthAt("p_hid", 10.1));
    }

    [Fact]
    public void A_change_set_file_is_stable_and_strictly_read()
    {
        var path = Directory.GetFiles(_tiny.Story.LedgerDir, "*.json").Single();
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\"kind\": \"belief_change\"", text);
        Assert.Contains("\"story_key\": 10.1", text);
        Assert.Contains("\"stance\": \"holds_true\"", text);
        Assert.Matches(@"^000001-cs_[0-9a-f]{10}\.json$", Path.GetFileName(path));

        File.WriteAllText(path, text.Replace("\"actor\":", "\"surprise\": 1,\n \"actor\":"));
        var unknown = Assert.Throws<LedgerException>(() => _tiny.Story.ReadLedger());
        Assert.Contains("not a readable change set", unknown.Message);
    }

    [Fact]
    public void A_new_story_ignores_its_index_and_protects_its_ledger_from_line_ending_changes()
    {
        Assert.Contains(".nse/", File.ReadAllText(Path.Combine(_tiny.Root, ".gitignore")));
        Assert.Contains("ledger/** -text", File.ReadAllText(Path.Combine(_tiny.Root, ".gitattributes")));
        Assert.Throws<DirectoryNotFoundException>(() => Story.Open(Path.Combine(_tiny.Root, "nowhere")));
    }
}

public sealed class ManuscriptTests
{
    [Fact]
    public void Scenes_are_read_in_file_order_with_titles_blocks_and_line_numbers()
    {
        var dir = Fixtures.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "02-b.md"), "<!-- scene: later -->\n# Later\n\nOne.\n\nTwo\nlines.\n");
            File.WriteAllText(Path.Combine(dir, "01-a.md"), "<!-- scene: first -->\n\nNo title here.\n<!--scene:second-->\n# Second\n\nText.\n\n\n");
            var m = ManuscriptReader.Read(dir);
            Assert.Empty(m.Problems);
            Assert.Equal(["first", "second", "later"], m.Scenes.Select(s => s.Id));
            Assert.Equal([1, 2, 3], m.Scenes.Select(s => s.Tell));
            Assert.Null(m.Find("first")!.Title);
            Assert.Equal("Second", m.Find("second")!.Title);
            var later = m.Find("later")!;
            Assert.Equal(["One.", "Two\nlines."], later.Blocks.Select(b => b.Text));
            Assert.Equal((6, 7), (later.Blocks[1].LineStart, later.Blocks[1].LineEnd));
            Assert.Equal("02-b.md", later.File);
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Stray_text_and_repeated_ids_are_reported_and_line_endings_do_not_change_the_hash()
    {
        var dir = Fixtures.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "01.md"), "Stray opening.\n<!-- scene: a -->\nText.\n<!-- scene: a -->\nAgain.\n");
            var m = ManuscriptReader.Read(dir);
            Assert.Equal(2, m.Problems.Count);
            Assert.Single(m.Scenes);

            File.WriteAllText(Path.Combine(dir, "01.md"), "<!-- scene: a -->\nText one.\n\nText two.\n");
            var lf = ManuscriptReader.Read(dir);
            File.WriteAllText(Path.Combine(dir, "01.md"), "<!-- scene: a -->\r\nText one.\r\n\r\nText two.\r\n");
            var crlf = ManuscriptReader.Read(dir);
            Assert.Equal(lf.Hash, crlf.Hash);
            File.WriteAllText(Path.Combine(dir, "01.md"), "<!-- scene: a -->\nText one.\n\nText 2.\n");
            Assert.NotEqual(lf.Hash, ManuscriptReader.Read(dir).Hash);
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void The_nearest_passage_is_found_only_when_something_is_close()
    {
        const string text = "She put the envelope inside her coat. Then she picked up the glasses and the folder.";
        Assert.True(QuoteMatcher.Contains([text], "envelope   inside\nher coat"));
        Assert.False(QuoteMatcher.Contains([text], "envelope in her coat"));
        Assert.False(QuoteMatcher.Contains([text], "   "));
        Assert.Equal("She put the envelope inside her coat.", QuoteMatcher.Nearest(text, "She put the letter inside her coat."));
        Assert.Null(QuoteMatcher.Nearest(text, "The tide was out across the whole harbour."));
    }

    /// <summary>
    /// A substring test is not enough for a quote: it could start inside a word ("he had
    /// taken" inside "She had taken", naming the wrong person), be a single letter, or run across paragraphs.
    /// </summary>
    [Fact]
    public void A_quote_is_whole_words_within_one_paragraph_and_long_enough_to_point_at_something()
    {
        string[] scene = ["She had taken the letter from the drawer.", "Anna said nothing.", "\"No.\"", "It was Sarah's coat, and she'd kept it."];
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(scene, "She had taken the letter"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(scene, "he had taken the letter"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(scene, "en the letter from the dra"));
        Assert.Equal(QuoteMatch.SpansParagraphs, QuoteMatcher.Match(scene, "from the drawer. Anna said nothing."));
        Assert.Equal(QuoteMatch.TooShort, QuoteMatcher.Match(scene, "letter"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(scene, "a"));
        Assert.Equal(QuoteMatch.NotFound, QuoteMatcher.Match(scene, "She had taken the parcel"));
        Assert.False(QuoteMatcher.Contains(scene, "he had taken the letter"));
        // One word is enough when it is the whole paragraph: a line of dialogue.
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(scene, "\"No.\""));
        Assert.Equal(QuoteMatch.TooShort, QuoteMatcher.Match(scene, "No."));
        // An apostrophe inside a word is part of the word; a quotation mark beside one is not.
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(scene, "Sarah's coat"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(scene, "was Sarah"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(scene, "d kept it"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(["'I took it,' she said."], "I took it,' she said"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(["‘I took it,’ she said."], "I took it,’ she said"));
    }

    /// <summary>
    /// A quote is matched against the text as a reader sees it, and when the
    /// words are there but cannot be quoted that way, the answer says why.
    /// </summary>
    [Fact]
    public void A_quote_is_matched_against_what_a_reader_sees_and_a_refusal_says_why()
    {
        // A letter quoted in the story: every line carries a mark that is not part of the letter.
        string[] letter = ["> He wrote that he was sorry\n> and that he would come.", "*Mr Jonas Pike.*", "It began *Dear Jonas*, and said _nothing_ of the sale."];
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(letter, "he was sorry and that he would come"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(letter, "> he was sorry\n> and that he would come"));
        // Emphasis is markup. The quote may have it or leave it out.
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(letter, "It began Dear Jonas, and said nothing of the sale"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(letter, "It began *Dear Jonas*, and said"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(letter, "Mr Jonas Pike."));
        Assert.Equal("Mr Jonas Pike.", QuoteMatcher.Readable("*Mr Jonas Pike.*"));
        Assert.Equal("a snake_case word", QuoteMatcher.Readable("a _snake_case_ word"));

        // An accent typed as its own character belongs to the letter before it: a quote cannot stop between
        // them. The two are built here from their code points, because they look alike on the page.
        var combining = (char)0x0301;
        var composed = (char)0x00E9;
        string[] cafe = [$"The door was shut. The cafe{combining} was dark.", "A grey-coated man's letter lay there."];
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(cafe, "shut. The cafe"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(cafe, $"The cafe{combining} was dark"));
        // The same letter typed as one character is a different spelling to this engine, which runs without
        // the Unicode tables that would fold the two. A known limit, pinned here so that it is not a surprise.
        Assert.Equal(QuoteMatch.NotFound, QuoteMatcher.Match(cafe, $"The caf{composed} was dark"));
        // A hyphen between letters is inside a word.
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(cafe, "coated man's letter"));
        Assert.Equal(QuoteMatch.InsideWord, QuoteMatcher.Match(cafe, "A grey"));
        Assert.Equal(QuoteMatch.Exact, QuoteMatcher.Match(cafe, "A grey-coated man's letter"));
        Assert.Equal(QuoteMatch.SpansParagraphs, QuoteMatcher.Match(cafe, "was dark. A grey-coated man's"));
        Assert.Equal(QuoteMatch.NotFound, QuoteMatcher.Match(cafe, "The cafe was light"));
    }

    [Fact]
    public void A_heading_is_the_title_alone_and_text_run_on_under_it_is_still_a_paragraph()
    {
        var dir = Fixtures.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "01.md"), "<!-- scene: a -->\n# One\nShe had taken the letter.\n\nAnna said nothing.\n");
            var scene = ManuscriptReader.Read(dir).Find("a")!;
            Assert.Equal("One", scene.Title);
            Assert.Equal(["She had taken the letter.", "Anna said nothing."], scene.Blocks.Select(b => b.Text));
            Assert.Equal((3, 3), (scene.Blocks[0].LineStart, scene.Blocks[0].LineEnd));
            // The title is not evidence of what happens in the scene.
            Assert.Equal(QuoteMatch.NotFound, QuoteMatcher.Match(scene.Blocks.Select(b => b.Text), "One She had taken"));
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Blank lines added between scenes change no scene's text, so the manuscript hash stays the same; line
    /// numbers must not then be served from a stale index.
    /// </summary>
    [Fact]
    public void Moving_lines_without_changing_a_scene_changes_the_layout_but_not_the_content_hash()
    {
        var dir = Fixtures.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "01.md"), "<!-- scene: a -->\nText one.\n<!-- scene: b -->\nText two.\n");
            var before = ManuscriptReader.Read(dir);
            File.WriteAllText(Path.Combine(dir, "01.md"), "\n\n<!-- scene: a -->\nText one.\n\n\n\n<!-- scene: b -->\nText two.\n");
            var after = ManuscriptReader.Read(dir);
            Assert.Equal(before.Hash, after.Hash);
            Assert.NotEqual(before.Layout, after.Layout);
            Assert.Equal((4, 9), (after.Find("a")!.LineStart, after.Find("b")!.LineStart));
            Assert.Equal(after.Layout, ManuscriptReader.Read(dir).Layout);
        }
        finally
        {
            Fixtures.DeleteDirectory(dir);
        }
    }
}
