using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// Taking a manuscript in, scene by scene, and being cut short. Which revision of each scene has been submitted
/// is written into the ledger with the submission, so a session that starts again, with its context gone, its
/// tasks cleared and the index deleted, is told from the ledger alone what is done and where to pick up. It is
/// not made to read and process the same chapters twice.
/// </summary>
public sealed class FeedTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public FeedTests()
    {
        // Six scenes. The first was recorded by the author's own hand. The fifth has a marker and no text yet,
        // and the sixth has text and no scene record.
        Write("03-three.md", "s3", "Three", "Cara came in with the post. There was a letter for Ann.");
        Write("04-four.md", "s4", "Four", "Ann burned the letter in the grate.");
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, "05-five.md"), "<!-- scene: s5 -->\n# Five\n");
        Write("06-six.md", "s6", "Six", "Dora watched the smoke from the yard.");
        var put = _tiny.Put(
            new Entity { Id = "cara", Type = EntityKind.Character, Name = "Cara" },
            new SceneRecord { Id = "s3", Pov = "ann", Cast = ["ann", "cara"] }, new SceneTime { Id = "t3", Scene = "s3", StoryKey = 30 },
            new SceneRecord { Id = "s4", Pov = "ann", Cast = ["ann"] }, new SceneTime { Id = "t4", Scene = "s4", StoryKey = 40 },
            new SceneRecord { Id = "s5", Pov = "ann", Cast = ["ann"] }, new SceneTime { Id = "t5", Scene = "s5", StoryKey = 50 });
        if (!put.Applied)
            throw new InvalidOperationException(string.Join("; ", put.Findings));
    }

    public void Dispose() => _tiny.Dispose();

    private void Write(string file, string scene, string title, string text) =>
        File.WriteAllText(Path.Combine(_tiny.Story.ManuscriptDir, file), $"<!-- scene: {scene} -->\n# {title}\n\n{text}\n");

    private static Event Happens(string id, string scene, double key, string summary, string quote) =>
        new() { Id = id, Scene = scene, StoryKey = key, Summary = summary, Quote = quote };

    private static SubmitResult Submit(Story story, string scene, LedgerRecord record, params string[] withdraws) =>
        story.SubmitScene(new SceneReport
        {
            Scene = scene, Manifest = ContextCompiler.Compile(story.State(), scene).ManifestId, Records = [record], Withdraws = withdraws,
        });

    private static string States(FeedProgress feed) => string.Join(" ", feed.Scenes.Select(s => $"{s.Scene}:{s.State}"));

    [Fact]
    public void A_feed_that_is_cut_short_picks_up_at_the_first_unfinished_scene_from_the_ledger_alone()
    {
        var story = _tiny.Story;
        var feed = story.Feed();
        Assert.Equal("s1:Recorded s2:NotFed s3:NotFed s4:NotFed s5:Empty s6:NotReady", States(feed));
        Assert.Equal("s2", feed.Next!.Scene);
        Assert.Equal(1, feed.Done);

        // Two scenes are taken in. Each submission carries the hash of the text it was made with.
        var two = Submit(story, "s2", Happens("e_find", "s2", 20.1, "Ben finds the key.", "Ben found the key under the mat"));
        Assert.True(two.Applied, string.Join("; ", two.Findings));
        Assert.Equal(story.ReadManuscript().Find("s2")!.Hash, two.Basis!.Text);
        Assert.Equal(two.Basis.Text, story.ReadLedger()[^1].ChangeSet.Basis!.Text);
        Assert.True(Submit(story, "s3", Happens("e_post", "s3", 30.1, "Cara brings the post.", "Cara came in with the post.")).Applied);

        // The session is cut short. Nothing of it is left but the ledger and the manuscript: its tasks are
        // cleared, the index is deleted, and whatever comes next opens the folder afresh.
        Fixtures.DeleteDirectory(Path.Combine(_tiny.Root, "work"));
        Fixtures.DeleteDirectory(Path.Combine(_tiny.Root, ".nse"));
        var resumed = Story.Open(_tiny.Root);
        feed = resumed.Feed();
        Assert.Equal("s1:Recorded s2:Fed s3:Fed s4:NotFed s5:Empty s6:NotReady", States(feed));
        Assert.Equal("s4", feed.Next!.Scene);
        const string told =
            "\nFeeding the manuscript\n"
            + "  3 of 6 scene(s) stand recorded at their present text. Do not read or submit those again: the ledger has them.\n"
            + "  Pick up at scene 4, s4 (manuscript/04-four.md lines 2-4): nothing has been submitted for it.\n"
            + "  Not ready, and the author's to set up before they can be submitted: s6.\n";
        Assert.Equal(told, FeedReport.Brief(feed));
        // It is in the brief, which is what a session reads first and reads again after its context is cut down.
        var (brief, errors) = (new StringWriter(), new StringWriter());
        Assert.Equal(0, Cli.Cli.Run(["brief", "--dir", _tiny.Root], brief, errors));
        Assert.Contains(told, brief.ToString().Replace("\r\n", "\n"));
        var table = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["feed", "--dir", _tiny.Root], table, errors));
        Assert.Contains("    2  s2  fed        submitted at this text in change set 3; 1 of the 1 record(s) it brought are in force\n", table.ToString().Replace("\r\n", "\n"));
        Assert.Contains("    4  s4  NOT FED    nothing has been submitted for it\n", table.ToString().Replace("\r\n", "\n"));
        Assert.Contains("\nPick up at scene 4, s4 (manuscript/04-four.md lines 2-4): nothing has been submitted for it.\n", table.ToString().Replace("\r\n", "\n"));

        // A submission that is refused wrote nothing, so the scene is where it was.
        Assert.False(Submit(resumed, "s4", Happens("e_burn", "s4", 40.1, "Ann burns the letter.", "Ann tore the letter up")).Applied);
        Assert.Equal("s4", resumed.Feed().Next!.Scene);
    }

    [Fact]
    public void A_scene_whose_text_was_revised_after_it_was_submitted_is_the_first_unfinished_change()
    {
        var story = _tiny.Story;
        Assert.True(Submit(story, "s2", Happens("e_find", "s2", 20.1, "Ben finds the key.", "Ben found the key under the mat")).Applied);
        Assert.True(Submit(story, "s3", Happens("e_post", "s3", 30.1, "Cara brings the post.", "Cara came in with the post.")).Applied);
        Assert.Equal("s4", story.Feed().Next!.Scene);

        // The author rewrites the third scene. It was taken in, and what was taken in is no longer its text.
        Write("03-three.md", "s3", "Three", "Cara came in with the post. There were two letters for Ann.");
        var feed = story.Feed();
        Assert.Equal("s1:Recorded s2:Fed s3:Changed s4:NotFed s5:Empty s6:NotReady", States(feed));
        Assert.Equal(
            "\nFeeding the manuscript\n"
            + "  2 of 6 scene(s) stand recorded at their present text. Do not read or submit those again: the ledger has them.\n"
            + "  Pick up at scene 3, s3 (manuscript/03-three.md lines 2-4): its text has changed since change set 4, where it was last submitted.\n"
            + "  After it, 1 more: s4.\n"
            + "  Not ready, and the author's to set up before they can be submitted: s6.\n", FeedReport.Brief(feed));

        // Submitted again at the new text, with what the old text supported withdrawn, it is done, and the next is the fourth.
        Assert.True(Submit(story, "s3", Happens("e_post2", "s3", 30.1, "Cara brings two letters.", "There were two letters for Ann."), "e_post").Applied);
        Assert.Equal("s1:Recorded s2:Fed s3:Fed s4:NotFed s5:Empty s6:NotReady", States(story.Feed()));
        var four = Submit(story, "s4", Happens("e_burn", "s4", 40.1, "Ann burns the letter.", "Ann burned the letter in the grate."));
        Assert.True(four.Applied);
        Assert.Null(story.Feed().Next);
        Assert.Contains("  Nothing is left to submit.\n", FeedReport.Brief(story.Feed()));

        // If the author throws out all that a submission brought, the scene is to be taken in again.
        Assert.True(story.Decide(new Decision { Reject = [four.ChangeSet!.ChangeSet.Id] }).Done);
        var after = story.Feed();
        Assert.Equal("s4", after.Next!.Scene);
        Assert.Equal($"what was submitted for it in change set {four.ChangeSet.ChangeSet.Seq} was rejected or withdrawn in full, and nothing else records it", after.Next.Detail);
    }

    [Fact]
    public void A_ledger_written_before_the_revision_was_kept_still_reads_and_says_what_it_does_not_know()
    {
        // A change set as an earlier engine wrote it: a submission's basis with no text hash in it.
        var old = NseJson.Read<ChangeBasis>("{\"scene\":\"s2\",\"manifest\":\"m_1_abcdef_0123abcd\",\"base_head\":1,\"rebased\":false}");
        Assert.Null(old.Text);
        Assert.DoesNotContain("text", NseJson.Write(old));
        var story = _tiny.Story;
        var ledger = story.ReadLedger();
        LedgerStore.Append(story.LedgerDir, ledger, new ChangeSetDraft
        {
            Actor = "agent", Ops = [new PutOp { Record = Happens("e_find", "s2", 20.1, "Ben finds the key.", "Ben found the key under the mat") }],
        }, story.ReadManuscript().Hash, old with { Manifest = ContextCompiler.Compile(story.State(), "s2").ManifestId, BaseHead = ledger.Count });
        var feed = story.Feed();
        Assert.Equal(FedState.Unknown, feed.Scenes.Single(s => s.Scene == "s2").State);
        // It is not sent back to be read again on a guess, and it is not called done either.
        Assert.Equal("s3", feed.Next!.Scene);
        Assert.Contains("  Submitted before the engine kept the text they were submitted with, so not known to be current: s2.\n", FeedReport.Brief(feed));
    }

    [Fact]
    public void A_story_with_nothing_left_to_take_in_says_nothing_about_feeding()
    {
        // The example story was recorded whole by the author's hand. Its brief has no use for the part.
        var harbour = Story.Open(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"));
        var feed = harbour.Feed();
        Assert.All(feed.Scenes, s => Assert.Equal(FedState.Recorded, s.State));
        Assert.Equal("", FeedReport.Brief(feed));
    }
}
