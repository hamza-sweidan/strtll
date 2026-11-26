using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Regression tests for proposals and decisions, one defect at a time, the most serious first. None of the
/// defects let an agent decide. They were ways the author
/// could be misled, a decision blocked or left half done, or the ledger made to say something untrue.
/// </summary>
public sealed class ProposalRegressionTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private Story Story => _tiny.Story;

    private static string Codes(IEnumerable<Validation.Finding> findings) => string.Join(",", findings.Select(f => f.Code));

    private ProposeResult Propose(string note, params Operation[] ops) =>
        Story.Propose(new ChangeSetDraft { Actor = "agent", Note = note, Ops = ops });

    private static PutOp Put(LedgerRecord record) => new() { Record = record };

    private static Embargo HoldBack => new() { Id = "em_hid", Prop = "p_hid", UntilScene = "s2" };

    private static Rule PastTense => new() { Id = "r_tense", Type = "style", Text = "Past tense throughout." };

    private Tracked Find(string id) => Projection.Build(Story.ReadLedger().Select(s => s.ChangeSet)).Find(id)!;

    private Approval ApprovalOf(string id) => Find(id).ApprovalAt(Story.ReadLedger().Count);

    private bool InForce(string id) => Story.State().Model.ById.ContainsKey(id);

    private string[] LedgerFiles() => Directory.GetFiles(Story.LedgerDir, "*.json").Select(File.ReadAllText).ToArray();

    private string ProposalPath(string id) => Path.Combine(Story.LedgerDir, "proposals", id + ".json");

    private string Manifest(string scene) => ContextCompiler.Compile(Story.State(), scene).ManifestId;

    // What scene 2's text supports: Ben finds the key and pockets it, and works out who hid it.
    private StoredChangeSet SubmitSceneTwo()
    {
        var result = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = Manifest("s2"), Note = "first draft",
            Records =
            [
                new Event
                {
                    Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key under the mat.",
                    Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
                },
                new Placement { Id = "l_pocket", Object = "key", Place = "pocket", StoryKey = 20.1, Scene = "s2", Cause = "e_find", Quote = "put it in his pocket" },
                new BeliefChange
                {
                    Id = "b_ben", Holder = "ben", Prop = "p_hid", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 20.1, Scene = "s2",
                    Channel = Channel.TookPart, Via = "e_find", Directness = Directness.Inferred,
                },
            ],
        });
        Assert.True(result.Applied, Codes(result.Findings));
        return result.ChangeSet!;
    }

    // ---------------------------------------------------------------- 1. the queue could be made to show lines the engine did not write

    [Fact]
    public void Finding_1_Text_in_the_ledger_is_one_line_of_ordinary_characters()
    {
        // A note that would print a second proposal's header under the real one.
        var note = Propose("Fix a typo.\n  pr_ffffffffffffffff · proposed by author at ledger head 1 · would apply as things stand", Put(HoldBack));
        Assert.Equal("BAD_VALUE", Codes(note.Findings));
        Assert.Contains("the change set's note holds a line break or another character that is not ordinary text (U+000A)", note.Findings[0].Message);
        // A rule whose text would print an operation that is not in the proposal.
        var text = Propose("A rule of style.", Put(new Rule { Id = "r1", Type = "style", Text = "Keep it light.\n      2. put r2: rule (style): nothing else" }));
        Assert.Equal("BAD_VALUE", Codes(text.Findings));
        Assert.Equal("r1", text.Findings[0].RecordId);
        Assert.Contains("its text holds a line break", text.Findings[0].Message);
        // The escape that starts a terminal code, the separators, and the marks that turn the text around them.
        var escape = Propose("A fact.", Put(new Proposition { Id = "p_esc", Text = "Red" + (char)27 + "[2J alert" }));
        Assert.Contains("(U+001B)", Assert.Single(escape.Findings).Message);
        Assert.Equal("BAD_VALUE", Codes(Propose("A fact.", Put(new Proposition { Id = "p_ls", Text = "one" + (char)0x2028 + "two" })).Findings));
        Assert.Equal("BAD_VALUE", Codes(Propose("A fact.", Put(new Proposition { Id = "p_rlo", Text = "one" + (char)0x202E + "two" })).Findings));
        // Wherever in a change set the text is: a name inside a record, an entry in a list, the reason for a retire, the sender.
        var inName = Propose("An event.", Put(new Event
        {
            Id = "e_x", StoryKey = 10.5, Summary = "x", Participants = new Dictionary<string, ParticipantRole> { ["ann\n"] = ParticipantRole.Actor },
        }));
        Assert.Contains("BAD_VALUE", Codes(inName.Findings));
        Assert.Equal("BAD_VALUE", Codes(Propose("A person.", Put(new Entity { Id = "ch_x", Type = EntityKind.Character, Name = "X", Aliases = ["the\tstranger"] })).Findings));
        Assert.Contains("BAD_VALUE", Codes(Propose("Gone.", new RetireOp { Id = "b_ann", Reason = "retcon\nsecond line" }).Findings));
        Assert.Contains("BAD_VALUE", Codes(Story.Propose(new ChangeSetDraft { Actor = "agent\nauthor", Note = "x", Ops = [Put(HoldBack)] }).Findings));
        // It is a rule of the ledger, not of proposals: the author's own put is held to it, and so is a submission.
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Proposition { Id = "p_two", Text = "two\nlines" }).Findings));
        var submitted = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = Manifest("s2"), Records = [_tiny.BenBelief("b_note", 20.1) with { Note = "line one\nline two" }],
        });
        Assert.Equal("BAD_VALUE", Codes(submitted.Findings));
        // An id is one word.
        Assert.Contains("BAD_VALUE", Codes(_tiny.Put(new Proposition { Id = "p two", Text = "An id with a space in it." }).Findings));
        Assert.Empty(Story.Review().Proposals);
        Assert.Single(Story.ReadLedger());

        // A quote is the one exception, and only for the line breaks and tabs of the passage it was copied from.
        Assert.True(_tiny.Put(new Event { Id = "e_wrapped", Scene = "s1", StoryKey = 10.3, Summary = "Ann denies it.", Quote = "Ann said\n\tto the empty hall." }).Applied);
        var bell = _tiny.Put(new Event { Id = "e_bell", Scene = "s1", StoryKey = 10.3, Summary = "x", Quote = "Ann said" + (char)7 + " to the empty hall." });
        Assert.Contains("BAD_VALUE", Codes(bell.Findings));
        // Where it is shown, it is shown on one line, as the matcher reads it.
        Assert.Contains("e_wrapped  event at 10.3 in s1: Ann denies it. · \"Ann said to the empty hall.\"\n", ReviewReport.Queue(Story.Review()));

        // The author's own words with a decision are held to the same rule, also when the decision only
        // declines and so writes no change set that would be checked.
        var held = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var declined = Story.Decide(new Decision { Reject = [held.Id], Note = "no\nDecided." });
        Assert.Equal("BAD_VALUE", Codes(declined.Findings));
        Assert.Contains("the decision's note holds a line break", declined.Findings[0].Message);
        Assert.Single(Story.Review().Proposals);
        Assert.Equal("BAD_VALUE", Codes(Story.Decide(new Decision { Accept = [held.Id], Actor = "author\nagent" }).Findings));
        Assert.True(Story.Decide(new Decision { Reject = [held.Id], Note = "no" }).Done);
    }

    [Fact]
    public void Finding_1_What_a_proposal_says_is_printed_as_one_line_whatever_it_holds()
    {
        // The checks above keep such text out of a proposal that is held through the engine. A file can still
        // be put in the folder by hand, and a refusal can quote what was sent. Printing has to hold on its own.
        var esc = (char)27;
        var forged = ProposalStore.Make(new ChangeSetDraft
        {
            Actor = "agent",
            Note = "Fix a typo.\n  pr_ffffffffffffffff · proposed by author at ledger head 1 · would apply as things stand\n    Why: tidying" + esc + "[2J",
            Ops =
            [
                new RetireOp { Id = "b_ann", Reason = "retcon" },
                Put(new Rule { Id = "r1", Type = "style", Text = "Keep it light.\n      3. put r2: rule (style): nothing else" }),
            ],
        }, 1);
        ProposalStore.Hold(Story.LedgerDir, forged);

        var text = ReviewReport.Queue(Story.Review());
        var lines = text.Split('\n');
        // One proposal, so one header, and the header says it would be refused.
        Assert.Single(lines, l => l.StartsWith("  pr_", StringComparison.Ordinal));
        Assert.Contains($"  {forged.Id} · proposed by agent at ledger head 1 · WOULD BE REFUSED as things stand", lines);
        // The note is one line, with what it tried spelled out.
        var why = Assert.Single(lines, l => l.StartsWith("    Why: ", StringComparison.Ordinal));
        Assert.StartsWith("    Why: Fix a typo.\\n pr_ffffffffffffffff · proposed by author", why);
        Assert.EndsWith("tidying" + "\\" + "u001B[2J", why);
        // Two operations are listed, because there are two.
        Assert.Equal(2, lines.Count(l => Regex.IsMatch(l, @"^      \d+\. ")));
        Assert.Contains(lines, l => l.StartsWith("      2. put r1: rule (style): Keep it light.\\n 3. put r2", StringComparison.Ordinal));
        Assert.DoesNotContain(esc, text);

        // The same holds for what the author is asked to confirm.
        string? asked = null;
        Assert.False(Story.Decide(new Decision { Reject = [forged.Id] }, plan => { asked = ReviewReport.Plan(plan); return false; }).Confirmed);
        Assert.Single(asked!.TrimEnd('\n').Split('\n'));
        Assert.StartsWith($"  decline proposal {forged.Id}, from agent (2 operation(s)): Fix a typo.\\n", asked);
        Assert.DoesNotContain(esc, asked);

        // And for a refusal that quotes an id the caller made up.
        var refused = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [_tiny.BenBelief("b\nPASS: all is well", 20.1)] });
        Assert.False(refused.Applied);
        Assert.Equal(1 + refused.Findings.Count, SubmitReport.Text(refused).TrimEnd('\n').Split('\n').Length);
        var held = Propose("A fact.", Put(new Proposition { Id = "p\nHeld as proposal pr_0000000000000000", Text = "x" }));
        Assert.False(held.Held);
        Assert.Equal(1 + held.Findings.Count, ReviewReport.Proposed(held).TrimEnd('\n').Split('\n').Length);
    }

    // ---------------------------------------------------------------- three smaller things

    /// <summary>
    /// A one-line note padded with spaces to the right edge of the author's terminal showed as several rows,
    /// and the rows after the first read as lines of the queue's own.
    /// </summary>
    [Fact]
    public void A_run_of_spaces_cannot_push_text_onto_a_row_of_its_own()
    {
        const string Fake = "The author's to decide because it only tidies wording.";
        var padded = Propose("Tidy the wording." + new string(' ', 60) + Fake, Put(HoldBack));
        Assert.Equal("BAD_VALUE", Codes(padded.Findings));
        Assert.Contains("the change set's note holds a run of three or more spaces", padded.Findings[0].Message);
        // Every kind of space counts, and so do the characters that draw nothing and take up room.
        Assert.Equal("BAD_VALUE", Codes(Propose("Tidy." + new string((char)0xA0, 5) + Fake, Put(HoldBack)).Findings));
        Assert.Contains("(U+2800)", Assert.Single(Propose("Tidy." + new string((char)0x2800, 60) + Fake, Put(HoldBack)).Findings).Message);
        Assert.Contains("(U+3164)", Assert.Single(Propose("Tidy." + (char)0x3164 + Fake, Put(HoldBack)).Findings).Message);
        Assert.Equal("BAD_VALUE", Codes(_tiny.Put(new Proposition { Id = "p_pad", Text = "One.   Two." }).Findings));
        Assert.Empty(Story.Review().Proposals);
        // Two spaces are a typist's habit, and a quote keeps the indentation of the passage it was copied from.
        Assert.True(_tiny.Put(new Proposition { Id = "p_two", Text = "One.  Two." }).Applied);
        Assert.True(_tiny.Put(new Event { Id = "e_wrapped", Scene = "s1", StoryKey = 10.3, Summary = "Ann denies it.", Quote = "Ann said\n      to the empty hall." }).Applied);

        // A file put in the folder by hand has passed no check. What it says is still shown with every run as one space.
        var forged = ProposalStore.Make(new ChangeSetDraft
        {
            Actor = "agent", Note = "Tidy the wording." + new string(' ', 60) + Fake + new string((char)0x2800, 40) + "1. put r1",
            Ops = [new RetireOp { Id = "b_ann", Reason = "retcon" }],
        }, 3);
        ProposalStore.Hold(Story.LedgerDir, forged);
        var text = ReviewReport.Queue(Story.Review());
        var why = Assert.Single(text.Split('\n'), l => l.StartsWith("    Why: ", StringComparison.Ordinal));
        Assert.StartsWith("    Why: Tidy the wording. The author's to decide because it only tidies wording." + "\\" + "u2800", why);
        Assert.DoesNotContain("   ", why.TrimStart());
        Assert.DoesNotContain((char)0x2800, text);
    }

    /// <summary>
    /// A record id could be anything without a space. The author pastes ids after "nse ratify", so an id
    /// could carry a second command, an option or a redirection, or look exactly like another id.
    /// </summary>
    [Fact]
    public void An_id_is_one_plain_word()
    {
        var zeroWidth = ((char)0x200B).ToString();
        string[] refused =
        [
            "Ea;calc", "$(whoami)", "`id`", "a|b", "a&b", "--note", "-x", "a,b", "'q'", ">out.txt", ".hidden", "a b", "a/b", "a\\b", "caf" + (char)0xE9,
            "Ex" + zeroWidth, "E" + (char)0xAD + "x", "Ex" + (char)0x2800, "cs_0123456789" + zeroWidth, (char)0x0441 + "s_0123456789",
            new string('a', 65),
        ];
        foreach (var id in refused)
        {
            var put = _tiny.Put(new Proposition { Id = id, Text = "A fact." });
            Assert.False(put.Applied, id);
            Assert.Contains(put.Findings, f => f.Code == "BAD_VALUE" && f.Message.StartsWith("an id is one plain word", StringComparison.Ordinal));
        }
        // By a submission and by a proposal as by the author's own put.
        var submitted = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [_tiny.BenBelief("Ea;calc", 20.1)] });
        Assert.Equal("BAD_VALUE", Codes(submitted.Findings));
        Assert.Equal("BAD_VALUE", Codes(Propose("A fact.", Put(new Proposition { Id = "$(whoami)", Text = "x" })).Findings));
        Assert.Single(Story.ReadLedger());

        // What an id may be.
        string[] accepted = ["Ex", "sc_07", "f_P03_end", "p.1", "ch:ann", "b-2", "_x", "7", new string('a', 64)];
        Assert.True(_tiny.Put([.. accepted.Select(id => (LedgerRecord)new Proposition { Id = id, Text = "A fact." })]).Applied);
    }

    /// <summary>
    /// Agents write the manuscript, and the engine prints headings and file names from it. A heading with a
    /// terminal code in it reached the author's screen as it was, through the brief and the scene context.
    /// </summary>
    [Fact]
    public void What_is_printed_from_the_manuscript_is_held_to_the_same_rule()
    {
        var esc = (char)27;
        File.WriteAllText(Path.Combine(Story.ManuscriptDir, "02-two.md"),
            "<!-- scene: s2 -->\n# Two" + esc + "[2J and more\n\nBen found the key under the mat and put it in his pocket.\n\n\tAn indented line, which is ordinary.\n");
        // A file whose name turns the text after it round is not read at all.
        var turned = "03-" + (char)0x202E + "three.md";
        File.WriteAllText(Path.Combine(Story.ManuscriptDir, turned), "<!-- scene: s9 -->\n# Nine\n\nNothing happens.\n");
        File.WriteAllText(Path.Combine(Story.ManuscriptDir, "04-four.md"), "<!-- scene: -dash -->\n# Four\n\nNothing happens here either.\n");

        var manuscript = Story.ReadManuscript();
        Assert.Equal("Two" + "\\" + "u001B[2J and more", manuscript.Find("s2")!.Title);
        Assert.Null(manuscript.Find("s9"));
        Assert.Contains(manuscript.Problems, p => p == "02-two.md, line 2: a character that is not ordinary text (U+001B). Prose has no use for it; where the engine prints from this line it is shown spelled out");
        Assert.DoesNotContain(manuscript.Problems, p => p.StartsWith("02-two.md, line 6", StringComparison.Ordinal));
        Assert.Contains(manuscript.Problems, p => p.StartsWith("03-" + "\\" + "u202Ethree.md: the file's name holds a character that is not ordinary text (U+202E). The file is not read", StringComparison.Ordinal));
        Assert.Contains(manuscript.Problems, p => p.StartsWith("04-four.md: scene id -dash begins with a hyphen", StringComparison.Ordinal));

        // Nothing the engine prints from it carries the code, and sync says what is wrong with the files.
        var brief = ContextCompiler.Brief(Story.State());
        Assert.Contains("s2  Two" + "\\" + "u001B[2J and more", brief);
        Assert.DoesNotContain(esc, brief);
        Assert.DoesNotContain(esc, ContextCompiler.Compile(Story.State(), "s2").Text);
        var report = Story.Sync();
        Assert.False(report.Clean);
        Assert.Equal(3, report.Problems.Count);
    }

    // ---------------------------------------------------------------- 2. a hand-made file could leave a decision half done and never be declined

    [Fact]
    public void Finding_2_A_proposal_that_could_not_be_written_out_again_is_not_a_proposal()
    {
        var good = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        // A file put in the folder by hand, with the id its contents hash to, holding a story time no file can hold.
        var draft = new ChangeSetDraft { Actor = "agent", Note = "Move the scene.", Ops = [Put(new SceneTime { Id = "t2b", Scene = "s2", StoryKey = 30 })] };
        var written = NseJson.Write(ProposalStore.Make(draft, 1)).Replace("\"story_key\": 30", "\"story_key\": 1e999");
        Assert.Contains("1e999", written);
        var node = JsonNode.Parse(written)!;
        var id = LedgerStore.HashId(node, ProposalStore.Prefix, ProposalStore.IdDigits);
        node["id"] = id;
        File.WriteAllText(ProposalPath(id), node.ToJsonString());

        var files = LedgerFiles();
        var queue = Story.Review();
        Assert.Equal([good.Id], queue.Proposals.Select(p => p.Proposal.Id));
        Assert.Contains("is not finite", Assert.Single(queue.Unreadable).Why);
        Assert.Contains("they cannot be decided on: delete them to clear them away", ReviewReport.Queue(queue));

        // Named in a decision, it stops the whole of it before anything is written...
        var both = Story.Decide(new Decision { Accept = [good.Id], Reject = [id] });
        Assert.Equal("BAD_PROPOSAL", Codes(both.Findings));
        Assert.Contains($"delete ledger/proposals/{id}.json", both.Findings[0].Message);
        Assert.Empty(both.Applied);
        Assert.Equal(files, LedgerFiles());
        Assert.Single(Story.Review().Proposals);
        // ...and by itself it is refused the same way every time, with a finding and not a crash.
        Assert.Equal("BAD_PROPOSAL", Codes(Story.Decide(new Decision { Reject = [id] }).Findings));
        Assert.Equal("BAD_PROPOSAL", Codes(Story.Decide(new Decision { Accept = [id] }).Findings));
        Assert.True(Story.Decide(new Decision { Accept = [good.Id] }).Done);
    }

    [Fact]
    public void Finding_2_If_the_writing_stops_part_way_what_was_done_is_reported()
    {
        var good = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var other = Propose("A rule of style.", Put(PastTense)).Proposal!;
        // The folder where declined proposals are kept cannot be made: a file is in its place.
        File.WriteAllText(Path.Combine(Story.LedgerDir, "proposals", "declined"), "in the way");

        var result = Story.Decide(new Decision { Accept = [good.Id], Reject = [other.Id] });
        Assert.False(result.Done);
        Assert.Equal("LEDGER", Codes(result.Findings));
        Assert.Single(result.Applied);
        Assert.Empty(result.Declined);
        var text = ReviewReport.Decided(result);
        Assert.StartsWith("Decided in part, then stopped. This much was done:\n", text);
        Assert.Contains($"  Proposal {good.Id} accepted and applied as 000002-cs_", text);
        Assert.DoesNotContain("declined. It is kept", text);
        Assert.Contains("the writing stopped part-way", text);
        // The ledger is whole, what was applied stands, and the other proposal still waits.
        Assert.Equal(2, Story.ReadLedger().Count);
        Assert.True(InForce("em_hid"));
        Assert.Equal([other.Id], Story.Review().Proposals.Select(p => p.Proposal.Id));
    }

    // ---------------------------------------------------------------- 3. a record named after a proposal made the proposal undecidable

    [Fact]
    public void Finding_3_A_record_cannot_take_the_name_of_a_change_set_or_a_proposal()
    {
        var first = Story.ReadLedger()[0].ChangeSet.Id;
        var held = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        Assert.Matches("^pr_[0-9a-f]{16}$", held.Id);
        // Whether or not anything has that id now, and by any way in.
        foreach (var taken in new[] { first, held.Id, "cs_0123456789", "pr_0123456789abcdef", "pr_0123456789" })
        {
            var put = _tiny.Put(new Proposition { Id = taken, Text = "A fact with a borrowed name." });
            Assert.Equal("BAD_VALUE", Codes(put.Findings));
            Assert.Contains("an id of this shape is one the engine gives", put.Findings[0].Message);
        }
        var submitted = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [_tiny.BenBelief(held.Id, 20.1)] });
        Assert.Equal("BAD_VALUE", Codes(submitted.Findings));
        Assert.Equal("BAD_VALUE", Codes(Propose("A fact.", Put(new Proposition { Id = first, Text = "x" })).Findings));
        // Ids that only begin the same way are ordinary ids.
        Assert.True(_tiny.Put(new Proposition { Id = "pr_office", Text = "The office is locked." }, new Proposition { Id = "cs_cafe", Text = "The cafe is open." }).Applied);
        // The proposal is as decidable as it was.
        Assert.True(Story.Decide(new Decision { Accept = [held.Id] }).Done);
    }

    [Fact]
    public void Finding_3_A_record_past_deciding_does_not_stand_in_the_way_of_its_namesake()
    {
        var held = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        // A ledger from before that rule, written as the engine writes: a record named after the held proposal.
        LedgerStore.Append(Story.LedgerDir, Story.ReadLedger(), new ChangeSetDraft
        {
            Actor = "agent", Ops = [Put(new Proposition { Id = held.Id, Text = "A fact with a borrowed name." })],
        }, Story.ReadManuscript().Hash);
        Assert.Equal("AMBIGUOUS", Codes(Story.Decide(new Decision { Accept = [held.Id] }).Findings));
        var declining = Story.Decide(new Decision { Reject = [held.Id] });
        Assert.Equal("AMBIGUOUS", Codes(declining.Findings));
        Assert.Contains("Retire the record", declining.Findings[0].Message);

        // The author takes the twin out of force. It was rejected, and a rejected record still has its id;
        // before, that kept the proposal ambiguous for good.
        Assert.True(_tiny.Apply(new RejectOp { Ids = [held.Id] }).Applied);
        var result = Story.Decide(new Decision { Accept = [held.Id] });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(held.Id, Assert.Single(result.Applied).ChangeSet.Proposal!.Id);
        Assert.True(InForce("em_hid"));
    }

    // ---------------------------------------------------------------- 8 (and 4). the ledger put a sender's record down to the author

    [Fact]
    public void Finding_8_A_record_is_made_by_whoever_sent_it_whatever_it_says()
    {
        var result = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = Manifest("s2"), Actor = "agent:review",
            Records =
            [
                _tiny.BenBelief("b_none", 20.1),
                _tiny.BenBelief("b_author", 20.2) with { Origin = "author" },
                _tiny.BenBelief("b_capital", 20.3) with { Origin = "Author" },
                _tiny.BenBelief("b_space", 20.4) with { Origin = "author " },
                _tiny.BenBelief("b_import", 20.5) with { Origin = "import" },
                _tiny.BenBelief("b_model", 20.6) with { Origin = "agent:review:some-model" },
            ],
        });
        Assert.True(result.Applied, Codes(result.Findings));
        Assert.All(new[] { "b_none", "b_author", "b_capital", "b_space", "b_import" }, id => Assert.Equal("agent:review", Find(id).Record.Origin));
        // It may say more about itself, after the sender's own name.
        Assert.Equal("agent:review:some-model", Find("b_model").Record.Origin);
        // The same in a proposal.
        var proposal = Propose("Hold it back.", Put(HoldBack with { Origin = "Author" })).Proposal!;
        Assert.Equal("agent", Assert.Single(proposal.Ops.OfType<PutOp>()).Record.Origin);
    }

    // ---------------------------------------------------------------- 5. a put that overrides was described as one that only adds

    [Fact]
    public void Finding_5_A_put_that_takes_the_place_of_something_says_what()
    {
        Assert.True(Story.Decide(new Decision { Accept = ["f_hid", "t2"] }).Done);
        var proposal = Propose("Ann put it back before Ben came.",
            Put(new FactChange { Id = "f_not", Prop = "p_hid", Value = false, StoryKey = 15 }),
            Put(new SceneTime { Id = "t2b", Scene = "s2", StoryKey = 30 }),
            Put(new BeliefChange
            {
                Id = "b_ann2", Holder = "ann", Prop = "p_hid", Stance = Stance.HoldsFalse, Strength = Strength.Certain, StoryKey = 15, Channel = Channel.Assumed,
            }),
            Put(new Placement { Id = "l_back", Object = "key", Place = "pocket", StoryKey = 15, Cause = "e_hide" }));
        Assert.True(proposal.Held, Codes(proposal.Findings));

        var text = ReviewReport.Queue(Story.Review());
        // The reasons say that a fact is turned round and a scene moved, not that records are "added".
        Assert.Contains("    The author's to decide because it changes what is so: p_hid is true in the records, and this makes it false from story time 15; "
                        + "it moves s2 in story time, from 20 to 30; it adds b_ann2 outside a scene submission; it adds l_back outside a scene submission.\n", text);
        // Each put is shown with what it would take the place of, as a retire is shown with what it takes away.
        Assert.Contains("      1. put f_not: fact: p_hid becomes false at 15\n"
                        + "           p_hid is: Ann hid the key under the mat.\n"
                        + "           before then it is true (f_hid, ratified)\n", text);
        Assert.Contains("      2. put t2b: scene time: s2 at story time 30, asserted\n"
                        + "           takes the place of story time 20 (t2, ratified). Records dated inside the scene keep their own dates\n", text);
        Assert.Contains("      3. put b_ann2: belief at 15: Ann comes to take p_hid as false, certain · assumed · stated\n"
                        + "           before then Ann: [knows] (b_ann, provisional)\n", text);
        Assert.Contains("      4. put l_back: placement at 15: the key moves to Ben's pocket, cause e_hide\n"
                        + "           before then it is kept at under the mat (l_mat, provisional)\n", text);

        // Where nothing is overridden, it says that too, and a fact set for the first time is not called a change.
        var fresh = Propose("A new fact.",
            Put(new Proposition { Id = "p_new", Text = "Ben kept the key." }),
            Put(new FactChange { Id = "f_new", Prop = "p_new", Value = true, StoryKey = 21 }),
            Put(new BeliefChange { Id = "b_ben0", Holder = "ben", Prop = "p_hid", Stance = Stance.Unaware, StoryKey = 5, Channel = Channel.Background })).Proposal!;
        var alone = ReviewReport.Queue(Story.Review().Only(fresh.Id)!, only: fresh.Id);
        Assert.Contains("it makes p_new true from story time 21, outside a scene submission", alone);
        Assert.Contains("           before then nothing is recorded for Ben about p_hid\n", alone);
    }

    // ---------------------------------------------------------------- 6. deleting the index forgot that the ledger had been longer

    [Fact]
    public void Finding_6_Clearing_the_index_away_does_not_forget_a_longer_ledger()
    {
        Assert.True(_tiny.Put(_tiny.BenBelief("b_ben", 20.1)).Applied);
        // The length is written in the lock file: beside the ledger, and never deleted.
        Assert.Equal("2", File.ReadAllText(Story.WriteLockPath).Trim());
        File.Delete(Directory.GetFiles(Story.LedgerDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Last());
        // The folder the readme says may be deleted at any time. Each of the three ways the engine can come
        // to the story is tried first after deleting it, because whichever comes first builds the index again
        // and the others would then be reading its copy of the number.
        void ClearTheIndexAway()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path.Combine(_tiny.Root, ".nse")))
                Directory.Delete(Path.Combine(_tiny.Root, ".nse"), recursive: true);
        }
        const string Shorter = "the ledger ends at change set 1, but this folder has seen it end at 2";

        // A writer first: nothing is written on top of it, by any way in.
        ClearTheIndexAway();
        Assert.Equal("LEDGER_SHORTENED", Codes(_tiny.Put(_tiny.BenBelief("b_on_top", 20.2)).Findings));
        ClearTheIndexAway();
        Assert.Equal("LEDGER_SHORTENED", Codes(Story.Decide(new Decision { Accept = ["b_ann"] }).Findings));
        Assert.Single(Story.ReadLedger());
        Assert.False(File.Exists(Story.IndexPath));
        // A sync first.
        ClearTheIndexAway();
        var report = Story.Sync();
        Assert.False(report.Clean);
        Assert.Contains(report.Problems, p => p.StartsWith(Shorter, StringComparison.Ordinal));
        // A reader first.
        ClearTheIndexAway();
        Assert.Contains(Shorter, Story.State().LedgerWarning);
        Assert.Contains("WARNING", ContextCompiler.Brief(Story.State()));
        var submitted = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest("s2"), Records = [_tiny.BenBelief("b_sub", 20.2)] });
        Assert.Equal("LEDGER_SHORTENED", Codes(submitted.Findings));
        Assert.Single(Story.ReadLedger());

        // Only accepting the loss clears it, and then the mark says so.
        Story.Rebuild();
        Assert.Equal("1", File.ReadAllText(Story.WriteLockPath).Trim());
        Assert.Null(Story.State().LedgerWarning);
        Assert.True(_tiny.Put(_tiny.BenBelief("b_after", 20.2)).Applied);
        Assert.Equal("2", File.ReadAllText(Story.WriteLockPath).Trim());
    }

    // ---------------------------------------------------------------- 7. the write lock was held while the author read the prompt

    [Fact]
    public void Finding_7_The_author_is_asked_with_the_lock_released_and_the_decision_is_checked_again()
    {
        var submission = SubmitSceneTwo();
        string? asked = null;
        // While the author is reading, another writer gets on with its work...
        var result = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id] }, plan =>
        {
            asked = ReviewReport.Plan(plan);
            Assert.True(_tiny.Put(new Proposition { Id = "p_other", Text = "Something else entirely." }).Applied);
            return true;
        });
        // ...and the decision, which that did not touch, is written on top of it.
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(4, Story.ReadLedger().Count);
        Assert.Equal(Approval.Ratified, ApprovalOf("e_find"));
        // What the author was asked about was the records themselves, not their names.
        Assert.Equal(
            "  ratify 2 record(s):\n"
            + "            e_find    event at 20.1 in s2: Ben finds the key under the mat. [Ben actor] · \"Ben found the key under the mat\"\n"
            + "            l_pocket  placement at 20.1 in s2: the key moves to Ben's pocket, cause e_find · \"put it in his pocket\"\n"
            + "  left alone because they are flagged: b_ben\n", asked);

        // If a record the decision names is gone by the time the author says yes, nothing is written.
        var gone = Story.Decide(new Decision { Accept = ["b_ben", "p_other"] }, _ =>
        {
            Assert.True(_tiny.Apply(new RetireOp { Id = "p_other", Reason = "duplicate" }).Applied);
            return true;
        });
        Assert.False(gone.Done);
        Assert.Equal("CHANGED,LEDGER", Codes(gone.Findings));
        Assert.Empty(gone.Applied);
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ben"));

        // If it still can be made and comes to something else than was shown, nothing is written either.
        var first = Story.ReadLedger()[0].ChangeSet.Id;
        var files = LedgerFiles().Length;
        var less = Story.Decide(new Decision { Accept = [first] }, _ =>
        {
            Assert.True(_tiny.Apply(new RetireOp { Id = "l_mat", Reason = "correction" }).Applied);
            return true;
        });
        Assert.Equal("CHANGED", Codes(less.Findings));
        Assert.Contains("Run it again to see it as it now stands", less.Findings[0].Message);
        Assert.Equal(files + 1, LedgerFiles().Length);
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));

        // That holds even when the very same change sets would be written, and only their meaning has moved:
        // the proposal was shown as taking away a provisional record, and by the yes the author has ratified it.
        var retire = Propose("Ann did not know what she had done.", new RetireOp { Id = "b_ann", Reason = "retcon" }).Proposal!;
        string? first_time = null;
        var harder = Story.Decide(new Decision { Accept = [retire.Id] }, plan =>
        {
            first_time = ReviewReport.Plan(plan);
            Assert.True(_tiny.Apply(new RatifyOp { Ids = ["b_ann"] }).Applied);
            return true;
        });
        Assert.Contains("takes away, provisional: belief at 10.1 in s1: Ann comes to take p_hid as true", first_time);
        Assert.Equal("CHANGED", Codes(harder.Findings));
        Assert.True(InForce("b_ann"));
        // Run again, it is shown as it now stands, and goes through.
        string? second_time = null;
        Assert.True(Story.Decide(new Decision { Accept = [retire.Id] }, plan => { second_time = ReviewReport.Plan(plan); return true; }).Done);
        Assert.Contains("takes away, ratified: belief at 10.1 in s1", second_time);
        Assert.Contains("because it retires b_ann, which the author ratified", second_time);
        Assert.False(InForce("b_ann"));
        Assert.True(Story.Decide(new Decision { Accept = [first] }, _ => true).Done);
        Assert.Equal(Approval.Ratified, ApprovalOf("e_hide"));
    }

    [Fact]
    public void Finding_7_A_put_is_asked_about_with_the_lock_released_and_checked_again_as_it_is_written()
    {
        var draft = new ChangeSetDraft { Actor = "author", Ops = [Put(new Proposition { Id = "p_late", Text = "A late fact." })] };
        // Another writer takes the id while the author is reading the question.
        var late = Story.Put(draft, _ =>
        {
            Assert.True(_tiny.Put(new Proposition { Id = "p_late", Text = "The same id, sooner." }).Applied);
            return true;
        });
        Assert.Equal("LEDGER", Codes(late.Findings));
        Assert.Equal(2, Story.ReadLedger().Count);

        // What would be refused is not asked about, and a no writes nothing.
        var asked = 0;
        Assert.Equal("LEDGER", Codes(Story.Put(draft, _ => { asked++; return true; }).Findings));
        Assert.Equal(0, asked);
        var fresh = new ChangeSetDraft { Actor = "author", Ops = [Put(new Proposition { Id = "p_fresh", Text = "A fresh fact." })] };
        Assert.Equal(Story.NotConfirmed, Codes(Story.Put(fresh, _ => { asked++; return false; }).Findings));
        Assert.Equal(1, asked);
        Assert.Equal(2, Story.ReadLedger().Count);
        Assert.True(Story.Put(fresh, _ => true).Applied);
    }
}
