using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tasks;

namespace Nse.Tests;

/// <summary>
/// Task records. A task is what a session was in the middle of, for one scene. The engine notes it as the work
/// goes by, the agent adds what only it knows, and the brief shows it to whichever session comes next.
/// Everything here is deterministic: what is tested is that what a stopped session leaves is all there in the
/// next one's brief. Whether a model then picks the work up is not something these tests can show.
/// </summary>
public sealed class TaskTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    private Story Story => _tiny.Story;

    private static string Codes(IEnumerable<Validation.Finding> findings) => string.Join(",", findings.Select(f => f.Code));

    /// <summary>What a session that has just started reads, and nothing else.</summary>
    private static string Brief(Story story) => ContextCompiler.Brief(story.State(), story.HeldProposals(), TaskReport.Text(story.Work()));

    /// <summary>A session that knows nothing of the one before it: a new process would open the folder like this.</summary>
    private Story NewSession() => Story.Open(_tiny.Root);

    private string SceneTwoFile => Path.Combine(Story.ManuscriptDir, "02-two.md");

    private string TaskFile(string scene = "s2") => Path.Combine(_tiny.Root, "work", "tasks", scene + ".json");

    private static SceneContract Contract => new()
    {
        Id = "c_s2", Scene = "s2", Outline = "Ben finds the key.",
        Intends = [new Intent { Change = IntentKind.Placement, Object = "key", Place = "pocket" }],
    };

    private static LedgerRecord[] SceneTwoRecords =>
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
    ];

    // ---------------------------------------------------------------- opening a task

    [Fact]
    public void Taking_a_context_opens_a_task_and_a_look_alone_is_not_work()
    {
        Assert.False(File.Exists(TaskFile()));
        var package = Story.TakeContext("s2");
        var task = TaskStore.Open(_tiny.Root, "s2")!;
        Assert.Equal("tk_s2_1", task.Id);
        Assert.Equal("agent", task.Actor);
        var step = Assert.Single(task.Steps);
        Assert.Equal(StepKind.Context, step.Step);
        Assert.Equal(package.ManifestId, step.Manifest);
        Assert.Equal(1, step.AtHead);
        Assert.Equal("ben", step.Pov);
        Assert.Equal("02-two.md", step.File);
        Assert.Equal(Story.ReadManuscript().Find("s2")!.Hash, step.TextHash);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", step.Time);

        // Asked again with nothing moved, there is nothing to add.
        Story.TakeContext("s2");
        Assert.Single(TaskStore.Open(_tiny.Root, "s2")!.Steps);
        // A context that was taken and nothing more is a look, and the brief does not call it work.
        var view = Assert.Single(Story.Tasks());
        Assert.False(view.InProgress);
        Assert.DoesNotContain("Work in progress", Brief(Story));

        // Compiling a context without taking it, or for an older head, opens nothing.
        ContextCompiler.Compile(Story.State(), "s1");
        Assert.False(File.Exists(TaskFile("s1")));
        // A scene that is not there opens nothing either, and says so as before.
        Assert.Throws<KeyNotFoundException>(() => Story.TakeContext("s9"));
        Assert.False(File.Exists(TaskFile("s9")));
        // The ledger is not touched by any of it.
        Assert.Single(Story.ReadLedger());
    }

    [Fact]
    public void A_context_taken_to_work_from_ends_by_asking_for_the_plan_before_any_prose()
    {
        // A session that writes its passage and means to note what it decided afterwards leaves nothing if it
        // is stopped in between. So the last thing a session reads before it drafts says what to do first.
        var plain = ContextCompiler.Compile(Story.State(), "s2");
        var taken = Story.TakeContext("s2");
        Assert.Equal(plain.Text + Story.NoteFirst, taken.Text);
        Assert.EndsWith(
            "it says which state the draft was written from.\n\nA task is open for this scene. Before you write or change any prose, call task_note with your plan: " +
            "what you have decided, the questions still open, and the next step. Call it again each time you decide something more. " +
            "A session can end at any moment without warning, and a later one has nothing but these notes to go on.", taken.Text);
        // A context that is only compiled opens no task, and says nothing of one.
        Assert.DoesNotContain("task_note", plain.Text);
        // The words are no part of what the manifest stands for: the draft is submitted with the same id either way.
        Assert.Equal(plain.ManifestId, taken.ManifestId);
        Assert.True(Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = taken.ManifestId, Records = SceneTwoRecords }).Applied);

        // They are counted like the rest of the text, so the size given is the size of what is read,
        Assert.Equal(ContextCompiler.EstimateTokens(taken.Text), taken.EstimatedTokens);
        Assert.True(taken.EstimatedTokens > plain.EstimatedTokens);
        // and when the budget is too small for everything it is something else that gives way: never these
        // words, and never the budget.
        // (A few facts about someone who is not in the scene, so that there is a part worth more than the line
        // that names it when it is left out. In a story this small no part is, otherwise.)
        Assert.True(_tiny.Put(Enumerable.Range(1, 6).SelectMany(n => new LedgerRecord[]
        {
            new Proposition { Id = $"p_far{n}", Text = $"Ben keeps spare key number {n} where nobody in the house has looked.", About = ["ben"] },
            new FactChange { Id = $"f_far{n}", Prop = $"p_far{n}", Value = true, StoryKey = 1 },
        }).ToArray()).Applied);
        var again = NewSession();
        var full = again.TakeContext("s1");
        Assert.Empty(full.Omitted);
        var tight = again.TakeContext("s1", full.EstimatedTokens - 1);
        Assert.NotEmpty(tight.Omitted);
        Assert.True(tight.EstimatedTokens <= full.EstimatedTokens - 1);
        Assert.EndsWith(Story.NoteFirst, tight.Text);
        // The smallest budget that will do is larger by these words than it is for the context alone.
        var least = Enumerable.Range(1, full.EstimatedTokens).First(b => Record.Exception(() => again.TakeContext("s1", b)) is null);
        Assert.IsType<BudgetException>(Record.Exception(() => again.TakeContext("s1", least - 1)));
        Assert.EndsWith("it says which state the draft was written from.", ContextCompiler.Compile(again.State(), "s1", least - 1).Text);
    }

    // ---------------------------------------------------------------- what a stopped session leaves for the next

    [Fact]
    public void A_session_that_stops_mid_task_leaves_enough_for_the_next_to_pick_it_up()
    {
        Assert.True(_tiny.Put(Contract).Applied);

        // The first session takes the context, writes half the scene and notes where it stands. Then it is gone.
        var first = NewSession();
        var package = first.TakeContext("s2");
        File.AppendAllText(SceneTwoFile, "\nBen stood a while at the door, the key cold in his hand.\n");
        var noted = first.Note(new TaskNote
        {
            Scene = "s2",
            Decided = ["Ben does not look at the label on the key.", "The scene stays inside the hall."],
            OpenQuestions = ["Does Ann come back before he leaves?"],
            Next = "Write the second half, from Ben reaching the door.",
            Draft = "First half written, up to the door.",
        });
        Assert.True(noted.Noted, Codes(noted.Findings));

        // The next session knows nothing. The brief is all it reads.
        var brief = Brief(NewSession());
        Assert.Contains("\nWork in progress (1). Each is a task that a session left open.", brief);
        // Eight things have to be there: the scene and the point of view,
        Assert.Contains("\n  s2 \"Two\" · POV Ben (ben) · task tk_s2_1, opened by agent\n", brief);
        // the manifest,
        Assert.Contains($"    context: manifest {package.ManifestId}, taken at ledger head 2. The ledger has not moved since.\n", brief);
        // the contract,
        Assert.Contains("    contract: c_s2 · Ben finds the key.\n", brief);
        // where the draft is,
        Assert.Contains("    prose: manuscript/02-two.md, lines 2 to 6. The text has changed since the context was taken: a draft is under way.\n", brief);
        Assert.Contains("    draft: First half written, up to the door.\n", brief);
        Assert.Contains("    state: not submitted yet.\n", brief);
        // the decisions, the open questions,
        Assert.Contains("    decided so far (2):\n      1. Ben does not look at the label on the key.\n      2. The scene stays inside the hall.\n", brief);
        Assert.Contains("    open questions (1):\n      1. Does Ann come back before he leaves?\n", brief);
        // and the next step.
        Assert.Contains("    next: Write the second half, from Ben reaching the door.\n", brief);
        // It comes straight after the map of the scenes, ahead of the counts.
        Assert.True(brief.IndexOf("Work in progress", StringComparison.Ordinal) < brief.IndexOf("\nRecords: ", StringComparison.Ordinal));

        // A session killed before it noted anything still leaves the two things the engine saw for itself:
        // which context it took, and that the text is not what it was then. That much is said, and no more:
        // the engine cannot know that it was a session that changed the text, so it does not call it work.
        var bare = new TinyStory();
        try
        {
            var manifest = bare.Story.TakeContext("s2").ManifestId;
            File.AppendAllText(Path.Combine(bare.Story.ManuscriptDir, "02-two.md"), "\nHalf a sentence that\n");
            var left = Brief(Story.Open(bare.Root));
            Assert.DoesNotContain("Work in progress", left);
            Assert.Contains(
                "\nText changed with nothing noted (1). A context was taken for each of these scenes, and the scene's text is not what it was then. " +
                "Nothing was noted, so the engine cannot say whether a session began a draft and stopped, or the text was edited outside any task. " +
                "Read the text, and ask the author before going on with one.\n", left);
            Assert.Contains(
                $"\n  s2 \"Two\" · POV Ben (ben) · task tk_s2_1, opened by agent\n    context: manifest {manifest}, taken at ledger head 1 · prose: manuscript/02-two.md, lines 2 to 6, is not as it was then\n", left);
            Assert.True(left.IndexOf("Text changed with nothing noted", StringComparison.Ordinal) < left.IndexOf("\nRecords: ", StringComparison.Ordinal));
        }
        finally
        {
            bare.Dispose();
        }
    }

    [Fact]
    public void A_task_says_whether_the_context_it_was_working_from_still_stands()
    {
        Story.TakeContext("s2");
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Next = "Write it." }).Noted);
        Assert.Contains("taken at ledger head 1. The ledger has not moved since.\n", Brief(NewSession()));

        // The ledger moves for a reason that has nothing to do with the scene.
        Assert.True(_tiny.Put(new Proposition { Id = "p_other", Text = "Something else entirely." }).Applied);
        Assert.Contains("taken at ledger head 1. The ledger is at 2, and what that context said still stands.\n", Brief(NewSession()));

        // Then for one that has: the author declares that only Ann ever learns it.
        Assert.True(_tiny.Put(new Secret { Id = "se1", Props = ["p_hid"], KnownOnlyTo = ["ann"] }).Applied);
        var brief = Brief(NewSession());
        Assert.Matches(@"taken at ledger head 1\. The ledger is at 3, and what that context said has changed in \d+ line\(s\)\. Take the context again and check the draft against what differs\.\n", brief);
        Assert.NotEmpty(Assert.Single(NewSession().Tasks()).ContextChanges);

        // Taking it again brings the task up to date.
        var again = NewSession().TakeContext("s2");
        Assert.Contains($"    context: manifest {again.ManifestId}, taken at ledger head 3. The ledger has not moved since.\n", Brief(NewSession()));
        Assert.Equal(3, TaskStore.Open(_tiny.Root, "s2")!.Steps.Count);

        // The scenes are told in another order: what the reader has been given on entry may have changed.
        File.Move(Path.Combine(Story.ManuscriptDir, "01-one.md"), Path.Combine(Story.ManuscriptDir, "03-one.md"));
        Assert.Contains("the scenes in the manuscript are not the scenes, in the order, that they were", Assert.Single(NewSession().Tasks()).ContextChanges);
    }

    // ---------------------------------------------------------------- what the engine notes for itself

    [Fact]
    public void A_submission_and_the_authors_decision_are_noted_and_the_decision_closes_the_task()
    {
        var manifest = Story.TakeContext("s2").ManifestId;
        var applied = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Note = "first draft", Records = SceneTwoRecords });
        Assert.True(applied.Applied, Codes(applied.Findings));
        var change = applied.ChangeSet!.ChangeSet.Id;

        var view = Assert.Single(NewSession().Tasks());
        Assert.True(view.InProgress);
        Assert.Equal(change, view.Submitted);
        Assert.Equal(["e_find", "l_pocket", "b_ben"], view.Waiting);
        Assert.False(view.TextChanged);
        var brief = Brief(NewSession());
        Assert.Contains($"    state: submitted as {change}. 3 of its record(s) still await the author: e_find, l_pocket, b_ben.\n", brief);
        Assert.Contains("The text is as it was submitted.\n", brief);
        var submitted = TaskStore.Open(_tiny.Root, "s2")!.Steps[^1];
        Assert.Equal(StepKind.Submitted, submitted.Step);
        Assert.Equal(["NO_CONTRACT", "FLAGGED"], submitted.Codes);

        // A second draft sent with the first one's manifest is refused, and the task says that too.
        var stale = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Records = [_tiny.BenBelief("b_again", 20.3)] });
        Assert.False(stale.Applied);
        Assert.Equal(["STALE_CONTEXT"], TaskStore.Open(_tiny.Root, "s2")!.Steps[^1].Codes);
        Assert.Contains($"    state: submitted as {change}. 3 of its record(s) still await the author: e_find, l_pocket, b_ben. A later submission was refused (STALE_CONTEXT), and nothing of it was written.\n",
            Brief(NewSession()));

        // The author ratifies the change set. The flagged record is left, so the task stays open on it.
        Assert.True(NewSession().Decide(new Decision { Accept = [change] }).Done);
        view = Assert.Single(NewSession().Tasks());
        Assert.Equal(["b_ben"], view.Waiting);
        Assert.Equal(StepKind.Decided, TaskStore.Open(_tiny.Root, "s2")!.Steps[^1].Step);
        // A decision on something else touches no task.
        var steps = TaskStore.Open(_tiny.Root, "s2")!.Steps.Count;
        Assert.True(NewSession().Decide(new Decision { Accept = ["b_ann"] }).Done);
        Assert.Equal(steps, TaskStore.Open(_tiny.Root, "s2")!.Steps.Count);

        // The author names the flagged record. Everything the task submitted is decided. But the second draft
        // was refused and never sent again: that is unfinished, and the task stays open to say so.
        Assert.True(NewSession().Decide(new Decision { Accept = ["b_ben"] }).Done);
        view = Assert.Single(NewSession().Tasks());
        Assert.True(view.InProgress);
        Assert.Empty(view.Waiting);
        Assert.Contains($"    state: submitted as {change}. Nothing of it awaits the author. A later submission was refused (STALE_CONTEXT), and nothing of it was written.\n",
            Brief(NewSession()));
        Assert.Equal(["context", "submitted", "refused", "decided", "decided"], TaskStore.Open(_tiny.Root, "s2")!.Steps.Select(s => Names.Of(s.Step)));
    }

    [Fact]
    public void A_task_is_put_away_when_everything_it_submitted_is_decided_and_nothing_came_after()
    {
        var manifest = Story.TakeContext("s2").ManifestId;
        var applied = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Records = SceneTwoRecords });
        Assert.True(applied.Applied, Codes(applied.Findings));
        Assert.True(NewSession().Decide(new Decision { Accept = [applied.ChangeSet!.ChangeSet.Id] }).Done);
        // The flagged record still waits, so the task is still open.
        Assert.True(File.Exists(TaskFile()));
        Assert.True(NewSession().Decide(new Decision { Accept = ["b_ben"] }).Done);
        Assert.False(File.Exists(TaskFile()));
        Assert.Empty(NewSession().Tasks());
        Assert.DoesNotContain("Work in progress", Brief(NewSession()));
        var closed = JsonNode.Parse(File.ReadAllText(Path.Combine(_tiny.Root, "work", "tasks", "closed", "s2.001.json")))!;
        Assert.Equal("tk_s2_1", closed["id"]!.GetValue<string>());
        Assert.Equal(["context", "submitted", "decided", "decided", "closed"], closed["steps"]!.AsArray().Select(s => s!["step"]!.GetValue<string>()));
        Assert.Equal("everything it submitted has been decided by the author", closed["steps"]![4]!["reason"]!.GetValue<string>());

        // The next context taken for the scene opens a new task.
        NewSession().TakeContext("s2");
        Assert.Equal("tk_s2_2", TaskStore.Open(_tiny.Root, "s2")!.Id);
    }

    [Fact]
    public void A_submission_that_is_applied_opens_a_task_if_none_is_open_and_one_that_is_refused_does_not()
    {
        // The context was only looked at, by a way that opens nothing, and the scene submitted all the same.
        var manifest = ContextCompiler.Compile(Story.State(), "s2").ManifestId;
        Assert.False(File.Exists(TaskFile()));
        // Anything can be sent and refused. A task is not opened on the strength of that.
        var refused = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Records = [_tiny.BenBelief("b_bad", 20.1, quote: "Ben found the keys")] });
        Assert.Equal("QUOTE_NOT_FOUND", Codes(refused.Findings));
        Assert.False(File.Exists(TaskFile()));
        Assert.DoesNotContain("s2 \"Two\"", TaskReport.Text(NewSession().Work()));

        // One that is applied is work, and opens the scene's task. No context was taken in it, and it says so.
        Assert.True(Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = manifest, Records = SceneTwoRecords }).Applied);
        Assert.Equal(["submitted"], TaskStore.Open(_tiny.Root, "s2")!.Steps.Select(s => Names.Of(s.Step)));
        var brief = Brief(NewSession());
        Assert.Contains($"    context: manifest {manifest}, the one last submitted with. A submission changes what its own scene's context says, so take the context again before going on.\n", brief);
        Assert.Contains("The text is as it was submitted.\n", brief);

        // With a task open, a submission that is refused is noted in it.
        var again = Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = ContextCompiler.Compile(Story.State(), "s2").ManifestId, Records = [_tiny.BenBelief("b_bad", 20.3, quote: "Ben found the keys")],
        });
        Assert.Equal("QUOTE_NOT_FOUND", Codes(again.Findings));
        Assert.Equal(["submitted", "refused"], TaskStore.Open(_tiny.Root, "s2")!.Steps.Select(s => Names.Of(s.Step)));
        Assert.Contains("A later submission was refused (QUOTE_NOT_FOUND), and nothing of it was written.\n", Brief(NewSession()));
        // A submission for a scene that is not in the manuscript is refused as ever, and no task is made for it.
        Assert.False(Story.SubmitScene(new SceneReport { Scene = "s9", Manifest = manifest, Records = SceneTwoRecords }).Applied);
        Assert.False(File.Exists(TaskFile("s9")));
    }

    // ---------------------------------------------------------------- what the agent adds

    [Fact]
    public void Decisions_add_up_and_the_rest_says_how_things_stand_now()
    {
        Story.TakeContext("s2");
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Decided = ["One."], OpenQuestions = ["Q one?", "Q two?"], Next = "First.", Draft = "Nothing yet." }).Noted);
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Decided = ["Two.", "Three."], OpenQuestions = ["Q two?"], Next = "Second." }).Noted);
        var view = Assert.Single(NewSession().Tasks());
        Assert.Equal(["One.", "Two.", "Three."], view.Decided);
        Assert.Equal(["Q two?"], view.OpenQuestions);
        Assert.Equal("Second.", view.Next);
        Assert.Equal("Nothing yet.", view.Draft);

        // A note that leaves the questions out keeps them; an empty list says none are left.
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Draft = "Half." }).Noted);
        Assert.Equal(["Q two?"], Assert.Single(NewSession().Tasks()).OpenQuestions);
        Assert.True(Story.Note(new TaskNote { Scene = "s2", OpenQuestions = [] }).Noted);
        view = Assert.Single(NewSession().Tasks());
        Assert.Empty(view.OpenQuestions);
        Assert.Equal("Half.", view.Draft);
        Assert.DoesNotContain("    open questions (", Brief(NewSession()));

        // Only the latest decisions are printed in full once there are many; the count says how many there are.
        for (var i = 4; i <= 14; i++)
            Assert.True(Story.Note(new TaskNote { Scene = "s2", Decided = [$"Decision {i}."] }).Noted);
        var brief = Brief(NewSession());
        Assert.Contains("    decided so far (14), the last 10 of them:\n      5. Decision 5.\n", brief);
        Assert.Contains("      14. Decision 14.\n", brief);
        Assert.DoesNotContain("      4. Decision 4.", brief);

        // Closing it puts it away with the reason, and the scene has no task open.
        var closed = Story.Note(new TaskNote { Scene = "s2", Close = "The author set the scene aside." });
        Assert.True(closed.Noted);
        Assert.Equal("Task tk_s2_1 for s2 is closed: The author set the scene aside.\n", TaskReport.Noted(closed, new TaskNote { Scene = "s2", Close = "The author set the scene aside." }));
        Assert.Empty(NewSession().Tasks());
        Assert.True(File.Exists(Path.Combine(_tiny.Root, "work", "tasks", "closed", "s2.001.json")));
        Assert.Equal("NO_TASK", Codes(Story.Note(new TaskNote { Scene = "s2", Next = "More." }).Findings));
    }

    [Fact]
    public void A_note_made_before_the_text_last_changed_is_marked_as_such()
    {
        Story.TakeContext("s2");
        Assert.True(Story.Note(new TaskNote
        {
            Scene = "s2", Decided = ["Ben keeps the key."], OpenQuestions = ["Who sees him?"], Next = "Write the passage, then submit.", Draft = "not started",
        }).Noted);
        // Noted, and nothing written since: the notes are as new as the text.
        var brief = Brief(NewSession());
        Assert.Contains("    open questions (1):\n      1. Who sees him?\n    draft: not started\n    next: Write the passage, then submit.\n", brief);
        Assert.DoesNotContain(TaskReport.Behind, brief);

        // The passage is written, and the session stops before it says so: a plan, and under it a draft the
        // plan knew nothing of.
        File.AppendAllText(SceneTwoFile, "\nBen stood a while at the door.\n");
        brief = Brief(NewSession());
        Assert.Contains("The text has changed since the context was taken: a draft is under way.\n", brief);
        Assert.Contains(
            "    open questions (1), noted before the text last changed:\n      1. Who sees him?\n" +
            "    draft (noted before the text last changed): not started\n" +
            "    next (noted before the text last changed): Write the passage, then submit.\n", brief);
        // A decision is something that was settled when it was noted. It is not marked.
        Assert.Contains("    decided so far (1):\n      1. Ben keeps the key.\n", brief);
        var view = Assert.Single(NewSession().Tasks());
        Assert.Equal((NoteAge.Behind, NoteAge.Behind, NoteAge.Behind), (view.OpenQuestionsAge, view.NextAge, view.DraftAge));

        // A note made now is as new as the text, for what it speaks of. What it does not speak of stays behind.
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Decided = ["Ben waits at the door."], Draft = "Passage written." }).Noted);
        brief = Brief(NewSession());
        Assert.Contains("    draft: Passage written.\n", brief);
        Assert.Contains("    open questions (1), noted before the text last changed:\n", brief);
        Assert.Contains("    next (noted before the text last changed): Write the passage, then submit.\n", brief);
        // The draft is still measured from the context and not from the note: it is as much under way as it was.
        Assert.Contains("The text has changed since the context was taken: a draft is under way.\n", brief);
        Assert.True(Assert.Single(NewSession().Tasks()).TextChanged);

        Assert.True(Story.Note(new TaskNote { Scene = "s2", Next = "Submit it." }).Noted);
        brief = Brief(NewSession());
        Assert.Contains("    open questions (1), noted before the text last changed:\n      1. Who sees him?\n    draft: Passage written.\n    next: Submit it.\n", brief);
        Assert.True(Story.Note(new TaskNote { Scene = "s2", OpenQuestions = ["Is the door too much?"] }).Noted);
        brief = Brief(NewSession());
        Assert.Contains("    open questions (1):\n      1. Is the door too much?\n    draft: Passage written.\n    next: Submit it.\n", brief);
        Assert.DoesNotContain(TaskReport.Behind, brief);

        // And the text moves on again.
        File.AppendAllText(SceneTwoFile, "\nThen he went out.\n");
        brief = Brief(NewSession());
        Assert.Contains("    draft (noted before the text last changed): Passage written.\n    next (noted before the text last changed): Submit it.\n", brief);

        // A note that does not say what the text was is of unknown age once the text has changed, and the
        // brief says that and not that it is current. That is so for a note made while the manuscript could
        // not be read, and for a task file from before notes recorded the text.
        var file = JsonNode.Parse(File.ReadAllText(TaskFile()))!;
        foreach (var step in file["steps"]!.AsArray().Where(s => s!["step"]!.GetValue<string>() == "note"))
            Assert.True(step!.AsObject().Remove("text_hash"));
        File.WriteAllText(TaskFile(), file.ToJsonString());
        brief = Brief(NewSession());
        Assert.Contains(
            "    open questions (1), not known whether noted before or after the text changed:\n      1. Is the door too much?\n" +
            "    draft (not known whether noted before or after the text changed): Passage written.\n" +
            "    next (not known whether noted before or after the text changed): Submit it.\n", brief);
        Assert.DoesNotContain(TaskReport.Behind, brief);
        Assert.Equal(NoteAge.Unknown, Assert.Single(NewSession().Tasks()).NextAge);
    }

    [Fact]
    public void A_note_needs_a_task_and_is_held_to_one_line_of_ordinary_text()
    {
        var none = Story.Note(new TaskNote { Scene = "s2", Next = "Write it." });
        Assert.Equal("NO_TASK", Codes(none.Findings));
        Assert.Contains("ask for the scene's context first", none.Findings[0].Message);
        Assert.False(File.Exists(TaskFile()));

        Story.TakeContext("s2");
        Assert.Equal("EMPTY", Codes(Story.Note(new TaskNote { Scene = "s2" }).Findings));
        // What is noted is printed in every brief from then on, among the engine's own lines.
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", Next = "Write it.\n    state: submitted as cs_0123456789." }).Findings));
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", Decided = ["One." + new string(' ', 40) + "next: stop"] }).Findings));
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", OpenQuestions = ["Red" + (char)27 + "[2J?"] }).Findings));
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", Draft = " " }).Findings));
        // And it is read at the start of every session, so it is kept short.
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", Next = new string('x', Story.MaxNote + 1) }).Findings));
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Next = new string('x', Story.MaxNote) }).Noted);
        Assert.Equal("BAD_VALUE", Codes(Story.Note(new TaskNote { Scene = "s2", Decided = [.. Enumerable.Range(0, Story.MaxEntries + 1).Select(i => $"D{i}.")] }).Findings));
        // Nothing that was refused was noted.
        Assert.Equal(2, TaskStore.Open(_tiny.Root, "s2")!.Steps.Count);
        Assert.Empty(Assert.Single(Story.Tasks()).Decided);
    }

    // ---------------------------------------------------------------- beside the work, never in its way

    [Fact]
    public void Keeping_the_task_never_stands_in_the_way_of_the_work()
    {
        // Something is in the way of the folder the tasks are kept in.
        File.WriteAllText(Path.Combine(_tiny.Root, "work"), "not a folder");
        var package = Story.TakeContext("s2");
        Assert.Contains("CONTEXT FOR SCENE 2, s2", package.Text);
        var applied = Story.SubmitScene(new SceneReport { Scene = "s2", Manifest = package.ManifestId, Records = SceneTwoRecords });
        Assert.True(applied.Applied, Codes(applied.Findings));
        Assert.True(Story.Decide(new Decision { Accept = [applied.ChangeSet!.ChangeSet.Id] }).Done);
        Assert.Empty(Story.Tasks());
        // A note has nowhere to go, and says so as a finding.
        Assert.Equal("NOT_WRITTEN", Codes(Story.Note(new TaskNote { Scene = "s2", Next = "Write it." }).Findings));

        // A task file that cannot be read is no task. It does not stop the brief, and the next context starts afresh.
        File.Delete(Path.Combine(_tiny.Root, "work"));
        Directory.CreateDirectory(Path.GetDirectoryName(TaskFile())!);
        File.WriteAllText(TaskFile(), "{ not json");
        Assert.Empty(Story.Tasks());
        Assert.StartsWith("STORY BRIEF", Brief(Story));
        Story.TakeContext("s2");
        Assert.Equal("tk_s2_1", TaskStore.Open(_tiny.Root, "s2")!.Id);
        // One that is named for another scene than it holds is not used either.
        File.Copy(TaskFile(), TaskFile("s1"));
        Assert.Null(TaskStore.Open(_tiny.Root, "s1"));
    }

    [Fact]
    public void Tasks_are_working_notes_kept_out_of_git_and_out_of_the_index()
    {
        // A story made by init ignores the folder from the start.
        var ignore = Path.Combine(_tiny.Root, ".gitignore");
        Assert.Contains("work/tasks/", File.ReadAllLines(ignore));
        // One whose ignore file lacks the line gets it when the first task is written, and only then.
        File.WriteAllText(ignore, ".nse/\n");
        Story.State();
        Assert.DoesNotContain("work/tasks/", File.ReadAllLines(ignore));
        Story.TakeContext("s2");
        Assert.Contains("work/tasks/", File.ReadAllLines(ignore));
        Assert.True(Story.Note(new TaskNote { Scene = "s2", Decided = ["Kept."] }).Noted);

        // Clearing the index away loses none of it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Path.Combine(_tiny.Root, ".nse"), recursive: true);
        Assert.Equal(["Kept."], Assert.Single(NewSession().Tasks()).Decided);
        // And nothing of a task is in the ledger or changes an answer.
        Assert.Single(Story.ReadLedger());
        Assert.DoesNotContain("Kept.", Story.Dump());
    }

    [Fact]
    public void Two_sessions_noting_at_once_lose_nothing()
    {
        Story.TakeContext("s2");
        Parallel.For(0, 24, i =>
        {
            var noted = Story.Open(_tiny.Root).Note(new TaskNote { Scene = "s2", Decided = [$"Decision {i:00}."] });
            Assert.True(noted.Noted, Codes(noted.Findings));
        });
        var decided = Assert.Single(NewSession().Tasks()).Decided;
        Assert.Equal(24, decided.Count);
        Assert.Equal(Enumerable.Range(0, 24).Select(i => $"Decision {i:00}."), decided.Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(_tiny.Root, "work", "tasks"), "*.tmp"));
    }
}
