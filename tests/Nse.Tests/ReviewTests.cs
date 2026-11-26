using System.Text.Json.Nodes;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Proposals, the review queue, and the author's decision. A proposal is a change
/// set held and not applied; only a decision applies it, ratifies or rejects. Everything here is
/// deterministic. That nothing an agent may call decides is tested here at the level of the engine; the
/// command line's and the MCP server's share of it are in their own test classes.
/// </summary>
public sealed class ReviewTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private Story Story => _tiny.Story;

    private static string Codes(IEnumerable<Validation.Finding> findings) => string.Join(",", findings.Select(f => f.Code));

    private ProposeResult Propose(string note, params Operation[] ops) =>
        Story.Propose(new ChangeSetDraft { Actor = "agent", Note = note, Ops = ops });

    private static PutOp Put(LedgerRecord record) => new() { Record = record };

    private static Rule PastTense => new() { Id = "r_tense", Type = "style", Text = "Past tense throughout." };

    private static Embargo HoldBack => new() { Id = "em_hid", Prop = "p_hid", UntilScene = "s2" };

    private Approval ApprovalOf(string id)
    {
        var projection = Projection.Build(Story.ReadLedger().Select(s => s.ChangeSet));
        return projection.Find(id)!.ApprovalAt(projection.Head);
    }

    private bool InForce(string id) => Story.State().Model.ById.ContainsKey(id);

    private string[] LedgerFiles() => Directory.GetFiles(Story.LedgerDir, "*.json").Select(File.ReadAllText).ToArray();

    private string ProposalPath(string id) => Path.Combine(Story.LedgerDir, "proposals", id + ".json");

    // What scene 2's text supports: Ben finds the key and pockets it, and works out who hid it.
    private StoredChangeSet SubmitSceneTwo()
    {
        var result = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = ContextCompiler.Compile(Story.State(), "s2").ManifestId, Note = "first draft",
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

    // ---------------------------------------------------------------- proposals

    [Fact]
    public void A_proposal_is_held_beside_the_ledger_and_changes_no_answer()
    {
        var files = LedgerFiles();
        var dump = Story.Dump();
        var context = ContextCompiler.Compile(Story.State(), "s2").Text;

        var result = Propose("The reader should not be sure of this before scene 2.", Put(HoldBack), Put(PastTense));
        Assert.True(result.Held, Codes(result.Findings));
        Assert.False(result.AlreadyHeld);
        var proposal = result.Proposal!;
        Assert.Matches("^pr_[0-9a-f]{16}$", proposal.Id);
        Assert.Equal(1, proposal.BaseHead);
        Assert.Equal("agent", proposal.Actor);

        // It is a file under ledger/proposals, named by its id, and it is not part of the chain.
        Assert.True(File.Exists(ProposalPath(proposal.Id)));
        Assert.Equal(files, LedgerFiles());
        Assert.Single(Story.ReadLedger());
        // No answer about the story reads it.
        Assert.Equal(dump, Story.Dump());
        Assert.Equal(context, ContextCompiler.Compile(Story.State(), "s2").Text);
        Assert.False(InForce("em_hid"));

        var queue = Story.Review();
        var held = Assert.Single(queue.Proposals);
        Assert.Equal(proposal.Id, held.Proposal.Id);
        Assert.Empty(held.WouldBeRefused);
        Assert.Equal(["it sets an embargo (em_hid)", "it sets a rule (r_tense)"], held.Why);
        Assert.Equal(["put", "put"], held.Ops.Select(o => o.Verb));
        Assert.Equal(1, Story.HeldProposals());
    }

    [Fact]
    public void A_proposal_is_checked_as_the_change_set_it_would_become()
    {
        // Ben was not there when Ann hid the key, so he cannot have witnessed it. Holding this for the author
        // would only hand them something that can never be applied.
        var result = Propose("Ben saw her do it.", Put(_tiny.BenBelief("b_saw", 10.1, channel: Channel.Witnessed, via: "e_hide")));
        Assert.False(result.Held);
        Assert.Contains("NOT_PRESENT", Codes(result.Findings));
        Assert.Empty(Story.Review().Proposals);
        Assert.False(Directory.Exists(Path.Combine(Story.LedgerDir, "proposals")) && Directory.GetFiles(Path.Combine(Story.LedgerDir, "proposals")).Length > 0);

        Assert.Equal("EMPTY", Codes(Propose("nothing").Findings));
        Assert.Equal("LEDGER", Codes(Propose("gone already", new RetireOp { Id = "b_nobody", Reason = "correction" }).Findings));
    }

    [Fact]
    public void A_proposal_cannot_carry_a_decision()
    {
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("these look right to me", new RatifyOp { Ids = ["b_ann"] }).Findings));
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("this one is wrong", new RejectOp { Ids = ["b_ann"] }).Findings));
        var decided = Propose("already agreed", new PutOp { Record = HoldBack, Approval = Approval.Ratified });
        Assert.Equal("AUTHOR_ONLY", Codes(decided.Findings));
        Assert.Equal("em_hid", decided.Findings[0].RecordId);
        // One such operation refuses the whole of it.
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("mixed", Put(HoldBack), new RatifyOp { Ids = ["b_ann"] }).Findings));
        Assert.Empty(Story.Review().Proposals);
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));
    }

    [Fact]
    public void A_proposal_says_why_it_is_made()
    {
        Assert.Equal("REASON_REQUIRED", Codes(Story.Propose(new ChangeSetDraft { Actor = "agent", Ops = [Put(HoldBack)] }).Findings));
        Assert.Equal("REASON_REQUIRED", Codes(Propose("   ", Put(HoldBack)).Findings));
        Assert.Empty(Story.Review().Proposals);
    }

    [Fact]
    public void The_same_proposal_is_held_once()
    {
        var first = Propose("Hold it back.", Put(HoldBack));
        var again = Propose("Hold it back.", Put(HoldBack));
        Assert.True(again.Held);
        Assert.True(again.AlreadyHeld);
        Assert.Equal(first.Proposal!.Id, again.Proposal!.Id);
        Assert.Single(Story.Review().Proposals);
        // A different reason is a different proposal: the id names everything the proposal says.
        var other = Propose("Hold it back, for another reason.", Put(HoldBack));
        Assert.NotEqual(first.Proposal.Id, other.Proposal!.Id);
        Assert.Equal(2, Story.Review().Proposals.Count);
    }

    // ---------------------------------------------------------------- deciding on proposals

    [Fact]
    public void Accepting_a_proposal_applies_it_with_its_records_ratified_and_says_where_it_came_from()
    {
        var proposal = Propose("The reader should not be sure of this before scene 2.", Put(HoldBack), Put(PastTense)).Proposal!;
        var result = Story.Decide(new Decision { Accept = [proposal.Id], Note = "yes, both" });
        Assert.True(result.Done, Codes(result.Findings));

        var applied = Assert.Single(result.Applied).ChangeSet;
        Assert.Equal(2, applied.Seq);
        Assert.Equal("author", applied.Actor);
        Assert.Equal("yes, both", applied.Note);
        Assert.Equal(new AcceptedProposal(proposal.Id, "agent", "The reader should not be sure of this before scene 2.", 1), applied.Proposal);
        Assert.Null(applied.Basis);
        Assert.All(applied.Ops.OfType<PutOp>(), put => Assert.Equal(Approval.Ratified, put.Approval));

        Assert.True(InForce("em_hid"));
        Assert.Equal(Approval.Ratified, ApprovalOf("em_hid"));
        Assert.Equal(Approval.Ratified, ApprovalOf("r_tense"));
        // The file is gone and the queue has nothing of it; the ledger reads back whole.
        Assert.False(File.Exists(ProposalPath(proposal.Id)));
        Assert.Empty(Story.Review().Proposals);
        Assert.Equal(2, Story.ReadLedger().Count);
        Assert.Equal(proposal.Id, Story.ReadLedger()[1].ChangeSet.Proposal!.Id);
        Assert.Contains("\"proposal\"", File.ReadAllText(Path.Combine(Story.LedgerDir, result.Applied[0].File)));
        // A decision is final.
        Assert.Equal("ALREADY_DECIDED", Codes(Story.Decide(new Decision { Accept = [proposal.Id] }).Findings));
        Assert.Equal("ALREADY_DECIDED", Codes(Story.Decide(new Decision { Reject = [proposal.Id] }).Findings));
    }

    [Fact]
    public void Declining_a_proposal_keeps_it_with_the_reason_and_applies_nothing()
    {
        var files = LedgerFiles();
        var proposal = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var result = Story.Decide(new Decision { Reject = [proposal.Id], Note = "the reader may know from the start" });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Empty(result.Applied);
        Assert.Equal([proposal.Id], result.Declined);
        Assert.Equal($"Decided.\n  Proposal {proposal.Id} declined. It is kept under ledger/proposals/declined/ with the decision.\n", ReviewReport.Decided(result));
        Assert.Equal(files, LedgerFiles());
        Assert.False(InForce("em_hid"));
        Assert.Empty(Story.Review().Proposals);
        Assert.False(File.Exists(ProposalPath(proposal.Id)));

        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(Story.LedgerDir, "proposals", "declined", proposal.Id + ".json")))!;
        Assert.Equal("author", kept["by"]!.GetValue<string>());
        Assert.Equal("the reader may know from the start", kept["reason"]!.GetValue<string>());
        Assert.Equal(1, kept["at_head"]!.GetValue<int>());
        Assert.Equal(proposal.Id, kept["proposal"]!["id"]!.GetValue<string>());

        // Declined is decided: it cannot be accepted after all, and the very same proposal is not held again.
        Assert.Equal("ALREADY_DECIDED", Codes(Story.Decide(new Decision { Accept = [proposal.Id] }).Findings));
        var again = Propose("Hold it back.", Put(HoldBack));
        Assert.False(again.Held);
        Assert.Equal("ALREADY_DECLINED", Codes(again.Findings));
        Assert.Contains("the reader may know from the start", again.Findings[0].Message);
        // Said differently, it is a new proposal and the author may look at it again.
        Assert.True(Propose("Hold it back: scene 2 is the reveal now.", Put(HoldBack)).Held);
    }

    [Fact]
    public void A_decision_is_all_or_nothing()
    {
        var files = LedgerFiles();
        var good = Propose("Hold it back.", Put(HoldBack)).Proposal!;

        // One id that names nothing: none of it is done.
        var result = Story.Decide(new Decision { Accept = [good.Id, "b_nobody"] });
        Assert.False(result.Done);
        Assert.Equal("UNKNOWN_REFERENCE", Codes(result.Findings));
        Assert.Equal("b_nobody", result.Findings[0].RecordId);
        Assert.Empty(result.Applied);
        Assert.Equal(files, LedgerFiles());
        Assert.Single(Story.Review().Proposals);

        // Two proposals that each stand alone and cannot both be applied: the same id put twice.
        var one = Propose("A new fact.", Put(new Proposition { Id = "p_new", Text = "Ben kept the key." })).Proposal!;
        var two = Propose("The same fact, worded another way.", Put(new Proposition { Id = "p_new", Text = "The key stayed with Ben." })).Proposal!;
        Assert.All(Story.Review().Proposals, p => Assert.Empty(p.WouldBeRefused));
        var both = Story.Decide(new Decision { Accept = [one.Id, two.Id] });
        Assert.False(both.Done);
        Assert.Equal("LEDGER", Codes(both.Findings));
        Assert.StartsWith($"in proposal {two.Id}: ", both.Findings[0].Message);
        Assert.Empty(both.Applied);
        Assert.Equal(files, LedgerFiles());
        Assert.Equal(3, Story.Review().Proposals.Count);

        // A record decision that cannot be made stops the proposals that came with it too.
        var mixed = Story.Decide(new Decision { Accept = [good.Id], Reject = ["e_hide"] });
        Assert.Equal("DANGLING_REFERENCE,DANGLING_REFERENCE", Codes(mixed.Findings));
        Assert.Equal(files, LedgerFiles());
        Assert.False(InForce("em_hid"));

        Assert.Equal("EMPTY", Codes(Story.Decide(new Decision()).Findings));
        Assert.Equal("MALFORMED", Codes(Story.Decide(new Decision { Accept = [" "] }).Findings));
        Assert.Equal("BAD_VALUE", Codes(Story.Decide(new Decision { Accept = [good.Id], Reject = [good.Id] }).Findings));
        Assert.Equal("BAD_VALUE", Codes(Story.Decide(new Decision { Accept = [good.Id], Actor = " " }).Findings));
        Assert.Equal(files, LedgerFiles());
    }

    [Fact]
    public void A_proposal_is_checked_again_when_it_is_accepted()
    {
        SubmitSceneTwo();
        var proposal = Propose("Ben could not have worked that out from finding it.", new RetireOp { Id = "b_ben", Reason = "correction" }).Proposal!;
        Assert.Equal(["it retires b_ben, a provisional record of a submission for s2"], Assert.Single(Story.Review().Proposals).Why);

        // The story moves on under the proposal: the author retires the record first.
        Assert.True(_tiny.Apply(new RetireOp { Id = "b_ben", Reason = "correction" }).Applied);
        var held = Assert.Single(Story.Review().Proposals);
        Assert.Equal("LEDGER", Codes(held.WouldBeRefused));
        var files = LedgerFiles();
        var result = Story.Decide(new Decision { Accept = [proposal.Id] });
        Assert.False(result.Done);
        Assert.Equal("LEDGER", Codes(result.Findings));
        Assert.Contains("cannot retire b_ben, which was retired", result.Findings[0].Message);
        Assert.Equal(files, LedgerFiles());
        // It still waits, and can be declined.
        Assert.True(Story.Decide(new Decision { Reject = [proposal.Id] }).Done);
    }

    [Fact]
    public void A_proposal_file_changed_after_it_was_held_is_not_applied()
    {
        var proposal = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var path = ProposalPath(proposal.Id);
        var files = LedgerFiles();

        // The id is what the author approves. Change what the file says under the same id, and the id no longer names it.
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("\"until_scene\": \"s2\"", "\"until_scene\": \"s1\""));
        Assert.NotEqual(original, File.ReadAllText(path));
        var queue = Story.Review();
        Assert.Empty(queue.Proposals);
        var broken = Assert.Single(queue.Unreadable);
        Assert.Contains("do not match its id", broken.Why);
        var result = Story.Decide(new Decision { Accept = [proposal.Id] });
        Assert.Equal("BAD_PROPOSAL", Codes(result.Findings));
        Assert.Equal(files, LedgerFiles());
        Assert.False(InForce("em_hid"));

        // Put back as it was, it is the proposal again. Under another name it is not.
        File.WriteAllText(path, original);
        Assert.Single(Story.Review().Proposals);
        File.Move(path, ProposalPath("pr_0000000000000000"));
        Assert.Empty(Story.Review().Proposals);
        Assert.Contains("file name does not match", Assert.Single(Story.Review().Unreadable).Why);
        Assert.Equal("UNKNOWN_REFERENCE", Codes(Story.Decide(new Decision { Accept = [proposal.Id] }).Findings));
        Assert.Equal("BAD_PROPOSAL", Codes(Story.Decide(new Decision { Accept = ["pr_0000000000000000"] }).Findings));
        Assert.Equal(files, LedgerFiles());
    }

    [Fact]
    public void A_proposal_file_put_there_by_hand_cannot_carry_a_decision_either()
    {
        // Whatever wrote the file, with its id worked out properly: a proposal that decides is not applied.
        var draft = new ChangeSetDraft { Actor = "agent", Note = "these are fine", Ops = [new RatifyOp { Ids = ["b_ann"] }] };
        var forged = ProposalStore.Make(draft, 1);
        ProposalStore.Hold(Story.LedgerDir, forged);
        var held = Assert.Single(Story.Review().Proposals);
        Assert.Equal("AUTHOR_ONLY", Codes(held.WouldBeRefused));
        var result = Story.Decide(new Decision { Accept = [forged.Id] });
        Assert.Equal("AUTHOR_ONLY", Codes(result.Findings));
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));
        Assert.Single(Story.ReadLedger());
    }

    [Fact]
    public void A_file_left_behind_after_its_decision_was_recorded_is_not_a_proposal()
    {
        var proposal = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var bytes = File.ReadAllBytes(ProposalPath(proposal.Id));
        Assert.True(Story.Decide(new Decision { Accept = [proposal.Id] }).Done);

        // As if the process had died between appending the change set and removing the file.
        Directory.CreateDirectory(Path.GetDirectoryName(ProposalPath(proposal.Id))!);
        File.WriteAllBytes(ProposalPath(proposal.Id), bytes);
        Assert.Empty(Story.Review().Proposals);
        Assert.Equal(0, Story.HeldProposals());
        Assert.Equal("ALREADY_DECIDED", Codes(Story.Decide(new Decision { Accept = [proposal.Id] }).Findings));
        Assert.Equal(2, Story.ReadLedger().Count);
        // The next writer clears it away.
        Assert.False(File.Exists(ProposalPath(proposal.Id)));

        // The same for a proposal that was declined: the record of the decision is what counts.
        var other = Propose("A rule of style.", Put(PastTense)).Proposal!;
        var otherBytes = File.ReadAllBytes(ProposalPath(other.Id));
        Assert.True(Story.Decide(new Decision { Reject = [other.Id], Note = "no" }).Done);
        File.WriteAllBytes(ProposalPath(other.Id), otherBytes);
        Assert.Empty(Story.Review().Proposals);
        Assert.Equal(0, Story.HeldProposals());
        Assert.Equal("ALREADY_DECIDED", Codes(Story.Decide(new Decision { Accept = [other.Id] }).Findings));
        Assert.False(InForce("r_tense"));
        Assert.False(File.Exists(ProposalPath(other.Id)));
    }

    // ---------------------------------------------------------------- deciding on records

    [Fact]
    public void Ratifying_a_change_set_ratifies_what_it_brought_and_leaves_the_flagged_records()
    {
        var submission = SubmitSceneTwo();
        var queue = Story.Review();
        Assert.Equal(19, queue.Records);
        var group = queue.Provisional.Single(g => g.ChangeSet.ChangeSet.Id == submission.ChangeSet.Id);
        Assert.Equal(["e_find", "l_pocket", "b_ben"], group.Records.Select(r => r.Id));
        Assert.Equal([false, false, true], group.Records.Select(r => r.Flagged));

        var seen = new List<DecisionPlan>();
        var result = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id] }, plan => { seen.Add(plan); return true; });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(["e_find", "l_pocket"], Assert.Single(seen).Ratify);
        Assert.Equal(["b_ben"], result.Plan!.LeftFlagged);
        Assert.Equal(Approval.Ratified, ApprovalOf("e_find"));
        Assert.Equal(Approval.Ratified, ApprovalOf("l_pocket"));
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ben"));
        var applied = Assert.Single(result.Applied).ChangeSet;
        Assert.Null(applied.Proposal);
        Assert.Equal(["e_find", "l_pocket"], Assert.IsType<RatifyOp>(Assert.Single(applied.Ops)).Ids);

        // What is left of it is flagged, and saying the change set again does nothing; the record is named instead.
        var again = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id] });
        Assert.Equal("NOTHING_TO_DECIDE", Codes(again.Findings));
        Assert.Contains("b_ben", again.Findings[0].Message);
        Assert.True(Story.Decide(new Decision { Accept = ["b_ben"] }).Done);
        Assert.Equal(Approval.Ratified, ApprovalOf("b_ben"));
        Assert.Equal("NOTHING_TO_DECIDE", Codes(Story.Decide(new Decision { Accept = [submission.ChangeSet.Id] }).Findings));
        // A decision on a record is final as well.
        Assert.Equal("LEDGER", Codes(Story.Decide(new Decision { Reject = ["b_ben"] }).Findings));
    }

    [Fact]
    public void A_record_that_was_withdrawn_no_longer_waits()
    {
        var submission = SubmitSceneTwo();
        Assert.True(_tiny.Apply(new RetireOp { Id = "l_pocket", Reason = "correction" }).Applied);
        var group = Story.Review().Provisional.Single(g => g.ChangeSet.ChangeSet.Id == submission.ChangeSet.Id);
        Assert.Equal(["e_find", "b_ben"], group.Records.Select(r => r.Id));
        // Ratifying the change set does not trip over the record that is gone.
        var result = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id] });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(["e_find"], result.Plan!.Ratify);
    }

    [Fact]
    public void A_record_named_by_itself_is_decided_as_named_whatever_is_said_of_its_change_set()
    {
        var submission = SubmitSceneTwo();
        // "All of that scene, and the inference too, but not the move."  The move's cause stays, so nothing dangles.
        var result = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id, "b_ben"], Reject = ["l_pocket"] });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(Approval.Ratified, ApprovalOf("e_find"));
        Assert.Equal(Approval.Ratified, ApprovalOf("b_ben"));
        Assert.Equal(Approval.Rejected, ApprovalOf("l_pocket"));
        Assert.Empty(result.Plan!.LeftFlagged);
        // One change set carries both halves of the decision.
        Assert.Equal(["ratify", "reject"], Assert.Single(result.Applied).ChangeSet.Ops.Select(o => o is RatifyOp ? "ratify" : "reject"));
        Assert.False(InForce("l_pocket"));
    }

    [Fact]
    public void Rejecting_a_change_set_rejects_everything_it_brought_that_still_waits()
    {
        var submission = SubmitSceneTwo();
        var result = Story.Decide(new Decision { Reject = [submission.ChangeSet.Id], Note = "the scene is being rewritten" });
        Assert.True(result.Done, Codes(result.Findings));
        Assert.Equal(["e_find", "l_pocket", "b_ben"], result.Plan!.RejectRecords);
        Assert.All(new[] { "e_find", "l_pocket", "b_ben" }, id => Assert.Equal(Approval.Rejected, ApprovalOf(id)));
        Assert.All(new[] { "e_find", "l_pocket", "b_ben" }, id => Assert.False(InForce(id)));
        Assert.DoesNotContain(Story.Review().Provisional, g => g.ChangeSet.ChangeSet.Id == submission.ChangeSet.Id);
    }

    [Fact]
    public void An_id_names_one_thing_or_the_decision_is_refused()
    {
        var first = Story.ReadLedger()[0].ChangeSet.Id;
        // A record given the id of a change set: "ratify cs_..." could then mean either. The write path no
        // longer lets one in, so this is a ledger from before that rule, written the way the engine writes.
        LedgerStore.Append(Story.LedgerDir, Story.ReadLedger(), new ChangeSetDraft
        {
            Actor = "author", Ops = [new PutOp { Record = new Proposition { Id = first, Text = "A fact with a borrowed name." } }],
        }, Story.ReadManuscript().Hash);
        var result = Story.Decide(new Decision { Accept = [first] });
        Assert.Equal("AMBIGUOUS", Codes(result.Findings));
        Assert.Contains("a change set and a record", result.Findings[0].Message);
        Assert.Equal(2, Story.ReadLedger().Count);
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));
    }

    [Fact]
    public void The_author_is_shown_what_a_decision_comes_to_and_may_still_say_no()
    {
        var submission = SubmitSceneTwo();
        var proposal = Propose("Hold it back.", Put(HoldBack)).Proposal!;
        var files = LedgerFiles();
        DecisionPlan? shown = null;
        var result = Story.Decide(new Decision { Accept = [submission.ChangeSet.Id, proposal.Id], Reject = ["b_ben"] }, plan => { shown = plan; return false; });
        Assert.False(result.Done);
        Assert.False(result.Confirmed);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Applied);
        Assert.NotNull(shown);
        Assert.Equal(["e_find", "l_pocket"], shown!.Ratify);
        Assert.Equal(["b_ben"], shown.RejectRecords);
        Assert.Equal([proposal.Id], shown.AcceptProposals.Select(p => p.Id));
        Assert.Empty(shown.LeftFlagged);
        Assert.Equal(files, LedgerFiles());
        Assert.Single(Story.Review().Proposals);
        Assert.Equal("Not confirmed. Nothing was decided.\n", ReviewReport.Decided(result));

        // What is refused is never shown for confirmation.
        var asked = false;
        Assert.Equal("UNKNOWN_REFERENCE", Codes(Story.Decide(new Decision { Accept = ["b_nobody"] }, _ => asked = true).Findings));
        Assert.False(asked);
    }

    [Fact]
    public void Nothing_is_decided_on_a_ledger_that_has_lost_its_end()
    {
        SubmitSceneTwo();
        Story.State();
        var newest = Directory.GetFiles(Story.LedgerDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Last();
        File.Delete(newest);
        var result = Story.Decide(new Decision { Accept = ["b_ann"] });
        Assert.Equal("LEDGER_SHORTENED", Codes(result.Findings));
        Assert.Equal(Approval.Provisional, ApprovalOf("b_ann"));
    }

    // ---------------------------------------------------------------- only the author decides, at the level of the engine

    [Fact]
    public void Nothing_an_agent_may_call_retires_a_ratified_record_or_ratifies()
    {
        // The author ratifies Ann's knowledge of what she did, and the scene-two submission's event.
        SubmitSceneTwo();
        Assert.True(Story.Decide(new Decision { Accept = ["b_ann", "e_find", "l_pocket"] }).Done);
        var files = LedgerFiles();
        var dump = Story.Dump();
        string Manifest() => ContextCompiler.Compile(Story.State(), "s2").ManifestId;

        // An agent has two ways to write: a scene submission and a proposal.
        // 1. A submission cannot withdraw a ratified record, even one its own scene's submission left.
        var withdraw = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest(), Records = [], Withdraws = ["l_pocket"] });
        Assert.False(withdraw.Applied);
        Assert.Equal("PROPOSAL_ONLY", Codes(withdraw.Findings));
        Assert.Equal(files, LedgerFiles());
        Assert.Equal(dump, Story.Dump());
        // 2. A submission has no way to say "ratify": its records are provisional whatever they claim.
        var claims = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = Manifest(), Records = [_tiny.BenBelief("b_later", 20.3, channel: Channel.Inferred)] });
        Assert.True(claims.Applied, Codes(claims.Findings));
        Assert.Equal(Approval.Provisional, ApprovalOf("b_later"));
        files = LedgerFiles();
        dump = Story.Dump();
        // 3. A proposal to retire a ratified record is held. The record stands until the author decides.
        var retire = Propose("Ann did not know what she had done.", new RetireOp { Id = "b_ann", Reason = "retcon" });
        Assert.True(retire.Held, Codes(retire.Findings));
        Assert.Equal(["it retires b_ann, which the author ratified"], Story.Review().Proposals.Single().Why);
        Assert.True(InForce("b_ann"));
        Assert.Equal(Approval.Ratified, ApprovalOf("b_ann"));
        // 4. A proposal cannot ratify, reject, or bring a record in already ratified.
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("ready", new RatifyOp { Ids = ["b_ben"] }).Findings));
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("wrong", new RejectOp { Ids = ["b_ben"] }).Findings));
        Assert.Equal("AUTHOR_ONLY", Codes(Propose("agreed", new PutOp { Record = HoldBack, Approval = Approval.Ratified }).Findings));
        // After all of that the ledger is byte for byte what it was, and so is every answer.
        Assert.Equal(files, LedgerFiles());
        Assert.Equal(dump, Story.Dump());

        // Only a decision does it, and a decision is the author's.
        var decided = Story.Decide(new Decision { Accept = [retire.Proposal!.Id] });
        Assert.True(decided.Done, Codes(decided.Findings));
        Assert.False(InForce("b_ann"));
        Assert.Equal("retire", Assert.Single(decided.Applied).ChangeSet.Ops.Select(o => o is RetireOp ? "retire" : "other").Single());
    }

    // ---------------------------------------------------------------- who made a record

    [Fact]
    public void A_record_an_agent_sends_without_saying_who_made_it_is_not_put_down_to_the_author()
    {
        SubmitSceneTwo();
        Tracked Find(string id) => Projection.Build(Story.ReadLedger().Select(s => s.ChangeSet)).Find(id)!;
        Assert.Equal("agent", Find("e_find").Record.Origin);
        Assert.Equal("agent", Find("b_ben").Record.Origin);
        // What the author entered stays the author's, and so does a scene the author submits.
        Assert.Equal("author", Find("e_hide").Record.Origin);
        var own = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = ContextCompiler.Compile(Story.State(), "s2").ManifestId, Actor = "author",
            Records = [_tiny.BenBelief("b_own", 20.4)],
        });
        Assert.True(own.Applied, Codes(own.Findings));
        Assert.Equal("author", Find("b_own").Record.Origin);

        // The same in a proposal. A record that does say is left as it says.
        var proposal = Propose("Hold it back.", Put(HoldBack), Put(PastTense with { Origin = "agent:some-model" })).Proposal!;
        Assert.Equal(["agent", "agent:some-model"], proposal.Ops.OfType<PutOp>().Select(p => p.Record.Origin));
        Assert.True(Story.Decide(new Decision { Accept = [proposal.Id] }).Done);
        // Ratified by the author; made by the agent.
        Assert.Equal("agent", Find("em_hid").Record.Origin);
        Assert.Equal(Approval.Ratified, ApprovalOf("em_hid"));
    }

    // ---------------------------------------------------------------- what the author reads

    [Fact]
    public void A_long_queue_is_given_to_an_agent_in_brief_and_one_item_at_a_time()
    {
        var submission = SubmitSceneTwo();
        var proposal = Propose("Hold it back.", Put(HoldBack), Put(PastTense)).Proposal!;
        var queue = Story.Review();

        var brief = ReviewReport.Queue(queue, brief: true);
        Assert.StartsWith("REVIEW QUEUE · ledger head 2 · in brief\n", brief);
        Assert.Contains("      2 operation(s): 2 to add, 0 to retire\n", brief);
        Assert.DoesNotContain("1. put em_hid", brief);
        // Counts stand in for the records, except the flagged ones, which have to be named to be ratified.
        Assert.Contains($"  {submission.ChangeSet.Id} · change set 2 · 3 waiting, 1 flagged · agent", brief);
        Assert.Contains("    FLAGGED b_ben", brief);
        Assert.DoesNotContain("event at 20.1", brief);
        Assert.Contains("This is the queue in brief. For every operation of one proposal, or every record of one change set: nse review ID\n", brief);

        // One item alone, in full.
        var one = queue.Only(submission.ChangeSet.Id)!;
        Assert.Empty(one.Proposals);
        Assert.Equal(3, one.Records);
        var text = ReviewReport.Queue(one, only: submission.ChangeSet.Id);
        Assert.StartsWith($"REVIEW QUEUE · ledger head 2 · {submission.ChangeSet.Id} alone\n", text);
        Assert.Contains("event at 20.1", text);
        Assert.DoesNotContain("a_deny", text);
        Assert.DoesNotContain(proposal.Id, text);
        var other = queue.Only(proposal.Id)!;
        Assert.Equal(0, other.Records);
        Assert.Contains("1. put em_hid", ReviewReport.Queue(other, only: proposal.Id));
        Assert.Null(queue.Only("b_ben"));
        Assert.Null(queue.Only("cs_0000000000"));

        // An agent is given the whole queue while that is short enough to read in one answer...
        Assert.Equal(ReviewReport.Queue(queue, forAgent: true), ReviewReport.ForAgent(queue));
        Assert.DoesNotContain("in brief", ReviewReport.ForAgent(queue));
        // ...and the brief one past that, with the way to ask for one item.
        var many = Enumerable.Range(0, 400)
            .Select(i => (LedgerRecord)new Proposition { Id = $"p_many_{i:000}", Text = "One of a great many propositions, each with a sentence of its own to be read." }).ToArray();
        Assert.True(_tiny.Put(many).Applied);
        var big = Story.Review();
        Assert.True(ContextCompiler.EstimateTokens(ReviewReport.Queue(big, forAgent: true)) > ReviewReport.AgentBudget);
        var given = ReviewReport.ForAgent(big);
        Assert.StartsWith("REVIEW QUEUE · ledger head 3 · in brief\n", given);
        Assert.True(ContextCompiler.EstimateTokens(given) < ReviewReport.AgentBudget);
        Assert.Contains("· 400 waiting ·", given);
        Assert.Contains("call review_queue with its id in only", given);
        Assert.DoesNotContain("p_many_007", given);
        // Asked for alone, that change set is still too long to give whole, and says so the same way.
        var bigOne = big.Provisional.Single(g => g.Records.Count == 400).ChangeSet.ChangeSet.Id;
        Assert.Contains("in brief", ReviewReport.ForAgent(big.Only(bigOne)!, bigOne));
    }

    [Fact]
    public void The_review_queue_reads_as_what_waits_and_how_to_decide()
    {
        var empty = new TinyStory();
        try
        {
            Assert.True(empty.Story.Decide(new Decision { Accept = [empty.Story.ReadLedger()[0].ChangeSet.Id] }).Done);
            Assert.True(empty.Story.Review().Empty);
            Assert.Contains("Nothing is waiting", ReviewReport.Queue(empty.Story.Review()));
        }
        finally
        {
            empty.Dispose();
        }

        var submission = SubmitSceneTwo();
        Assert.True(_tiny.Apply(new RatifyOp { Ids = ["b_ann"] }).Applied);
        var retire = Propose("Ann did not know what she had done.", new RetireOp { Id = "b_ann", Reason = "retcon" }, Put(new SceneContract
        {
            Id = "c_s2", Scene = "s2", Outline = "Ben finds the key.",
            Intends = [new Intent { Change = IntentKind.Placement, Object = "key", Place = "pocket" }],
            Relies = [new Intent { Change = IntentKind.Belief, Holder = "ben", Prop = "p_hid", Stance = Stance.Unaware }],
        })).Proposal!;
        var text = ReviewReport.Queue(Story.Review());
        Assert.StartsWith("REVIEW QUEUE · ledger head 3\n", text);
        Assert.Contains("Proposals held for the author's decision (1). None of this is in the ledger:", text);
        Assert.Contains($"  {retire.Id} · proposed by agent at ledger head 3 · would apply as things stand\n", text);
        Assert.Contains("    Why: Ann did not know what she had done.\n", text);
        Assert.Contains("    The author's to decide because it retires b_ann, which the author ratified; it sets the contract for s2.\n", text);
        Assert.Contains("      1. retire b_ann: retcon\n", text);
        Assert.Contains("           takes away, ratified: belief at 10.1 in s1: Ann comes to take p_hid as true, certain · took_part via e_hide · stated\n", text);
        Assert.Contains("      2. put c_s2: contract for s2: 1 intended change(s), 1 relied on · Ben finds the key.\n", text);
        Assert.Contains("           intends: the key (key) moves to Ben's pocket (pocket)\n", text);
        Assert.Contains("           relies on: Ben (ben) is unaware of p_hid\n", text);
        Assert.Contains($"  {submission.ChangeSet.Id} · change set 2 · 3 waiting, 1 flagged · agent · submission for s2 · first draft\n", text);
        // Ids are set in a column as wide as the longest in their change set.
        Assert.Contains("            e_find    event at 20.1 in s2: Ben finds the key under the mat. [Ben actor] · \"Ben found the key under the mat\"\n", text);
        Assert.Contains("    FLAGGED b_ben     belief at 20.1 in s2: Ben comes to take p_hid as true, believes · took_part via e_find · inferred\n", text);
        Assert.Contains("To decide: nse ratify ID ...   or   nse reject ID ... --note WHY\n", text);
        // b_ann is ratified, so it no longer waits; the rest of the first change set does.
        Assert.DoesNotContain("            b_ann  ", text);
        Assert.Contains("            a_deny    dialogue at 10.2 in s1: Ann denies p_hid · \"\"I never touched it,\" Ann said to the empty hall.\"\n", text);

        // To an agent the same queue ends by saying who decides.
        var forAgent = ReviewReport.Queue(Story.Review(), forAgent: true);
        Assert.Contains("Only the author decides.", forAgent);
        Assert.DoesNotContain("nse ratify", forAgent);
    }
}
