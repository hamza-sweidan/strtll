using System.Text.Json.Nodes;
using Nse.Index;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// The example story, loaded once through the engine's write path. Every change set is applied by
/// <see cref="Story.Put"/>, and the answers after each one are kept so an older head can be checked later.
/// </summary>
public sealed class HarbourStory : IDisposable
{
    public Story Story { get; }
    public string Root { get; }
    public IReadOnlyList<PutResult> Results { get; }

    /// <summary>The full dump as it stood when each change set had just been applied, by head.</summary>
    public IReadOnlyDictionary<int, string> DumpAtHead { get; }

    public HarbourStory()
    {
        Story = Fixtures.NewHarbourStory(out var root);
        Root = root;
        var results = new List<PutResult>();
        var dumps = new Dictionary<int, string>();
        foreach (var draft in GoldImporter.Drafts(Fixtures.Json("story.json")))
        {
            var result = Story.Put(draft);
            results.Add(result);
            if (result.ChangeSet is { } stored)
                dumps[stored.ChangeSet.Seq] = Story.Dump();
        }
        Results = results;
        DumpAtHead = dumps;
    }

    public void Dispose() => Fixtures.DeleteDirectory(Root);
}

/// <summary>The read path, on the example story: every answer checked against reference answers kept as fixtures.</summary>
public sealed class HarbourLetterTests(HarbourStory harbour) : IClassFixture<HarbourStory>
{
    private readonly Story _story = harbour.Story;

    [Fact]
    public void The_gold_state_goes_in_through_the_validated_write_path()
    {
        var refused = harbour.Results.Where(r => !r.Applied).SelectMany(r => r.Findings).Select(f => f.ToString()).ToList();
        Assert.True(refused.Count == 0, "the write path refused:\n" + string.Join("\n", refused.Take(30)));
        var ledger = _story.ReadLedger();
        Assert.Equal(harbour.Results.Count, ledger.Count);
        Assert.Equal(Enumerable.Range(1, ledger.Count), ledger.Select(s => s.ChangeSet.Seq));
        var gold = Fixtures.Json("story.json");
        var state = _story.State();
        Assert.Equal(gold.GetProperty("belief_changes").GetArrayLength(), state.Model.Beliefs.Count);
        Assert.Equal(gold.GetProperty("disclosures").GetArrayLength(), state.Model.Disclosures.Count);
        Assert.Equal(gold.GetProperty("locations").GetArrayLength(), state.Model.Placements.Count);
        Assert.Equal(12, state.Telling.Count);
    }

    [Fact]
    public void The_fixtures_were_made_from_the_gold_state_they_sit_beside()
    {
        var recorded = Fixtures.Node("expected-state.json")["source"]!["story_sha256"]!.GetValue<string>();
        Assert.True(recorded == Fixtures.Sha256OfFile(Path.Combine(Fixtures.HarbourLetter, "story.json")),
            "story.json changed after expected-state.json was made from it.");
    }

    /// <summary>
    /// For every scene and the end, every tracked person, the reader, the letter and every proposition's truth,
    /// the engine gives what the reference answers give.
    /// </summary>
    [Fact]
    public void The_engine_gives_every_answer_the_reference_gives()
    {
        var expected = Fixtures.Node("expected-state.json").AsObject();
        expected.Remove("source");
        var actual = StateDump.Build(_story.State());
        actual.Remove("head");
        var differences = JsonDiff.Differences(expected, actual);
        Assert.True(differences.Count == 0, "the engine and the reference disagree:\n" + string.Join("\n", differences));
    }

    /// <summary>The engine alone answers every point-in-time probe.</summary>
    [Fact]
    public void The_engine_answers_all_forty_probes()
    {
        var failures = ProbeChecker.Failures(_story.State(), Fixtures.Json("probes.json"), out var count);
        Assert.Equal(40, count);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_flashback_is_before_scene_one_for_people_and_after_scene_seven_for_the_reader()
    {
        var s = _story.State();
        Assert.True(s.Key("sc_08") < s.Key("sc_01"));
        Assert.Equal(8, s.Tell("sc_08"));
        // Sarah took the letter in the flashback, so on entry to scene 1 she already knows it...
        Assert.Equal("knows", s.Classify(s.BeliefsBefore("ch_sarah", s.Key("sc_01")).GetValueOrDefault("P10"), s.Key("sc_01")));
        // ...while the reader is first told in scene 8, and has not been told on entry to it.
        Assert.Equal("sc_08", s.RevealScene("P10"));
        Assert.False(s.ReaderBefore(8).ContainsKey("P10"));
        Assert.True(s.ReaderBefore(9).ContainsKey("P10"));
    }

    [Fact]
    public void No_record_is_not_the_same_as_does_not_know()
    {
        var s = _story.State();
        var key = s.Key("sc_09");
        // Nothing records what Daniel makes of the watcher on the quay: there is no record, and it is reported as such.
        Assert.False(s.BeliefsBefore("ch_daniel", key).ContainsKey("P23"));
        Assert.Equal("no record", s.Classify(null));
        // Sarah is recorded as not knowing who the man in the grey coat is: that is a record.
        Assert.Equal("unaware", s.Classify(s.BeliefsBefore("ch_sarah", key)["P15"], key));
    }

    [Fact]
    public void Events_that_share_a_story_time_are_reported_as_not_ordered()
    {
        var probe = Fixtures.Json("probes.json").EnumerateArray()
            .First(p => p.GetProperty("checks")[0].GetProperty("kind").GetString() == "order");
        var events = probe.GetProperty("checks")[0].GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToList();
        var s = _story.State();
        // The two events share a story time, and the engine says their order is not established rather than
        // picking one. That is its own answer, from the events; it does not lean on P20 being marked open.
        Assert.Equal(2, events.Count);
        Assert.Equal("not_established", s.Order(events[0], events[1]));
        Assert.Equal("not_established", s.Order(events[1], events[0]));
        var later = s.Model.Events.Values.First(e => e.StoryKey > s.Model.Events[events[0]].StoryKey).Id;
        Assert.Equal("before", s.Order(events[0], later));
        Assert.Equal("after", s.Order(later, events[0]));
        // The proposition about which came first is separately marked as not decided, so canon does not say either.
        Assert.Equal("open", s.Canon("P20"));
        Assert.Null(s.TruthAt("P20", StoryState.End));
        Assert.Null(s.FactTie("P20", StoryState.End, before: true));
    }

    /// <summary>The index holds nothing that cannot be rebuilt.</summary>
    [Fact]
    public void Deleting_the_index_and_rebuilding_gives_byte_identical_answers()
    {
        var before = _story.Dump();
        File.Delete(_story.IndexPath);
        Assert.False(File.Exists(_story.IndexPath));
        var after = _story.Dump();
        Assert.True(File.Exists(_story.IndexPath));
        Assert.Equal(before, after);
        _story.Rebuild();
        Assert.Equal(before, _story.Dump());

        // The answers above really came out of the index. Without this check the test would also pass if every
        // read quietly fell back to replaying the ledger files, which gives the same answers.
        Assert.True(_story.State().FromIndex);
        Assert.True(_story.State(3).FromIndex);
        Assert.Equal(before, StateDump.Write(StateDump.Build(
            StoryIndex.FromFiles(Projection.Build(_story.ReadLedger().Select(s => s.ChangeSet)), null, _story.ReadManuscript()))));
    }

    /// <summary>An older ledger head still gives the answers it gave when it was the latest.</summary>
    [Fact]
    public void Every_older_ledger_head_still_gives_its_old_answers()
    {
        Assert.True(harbour.DumpAtHead.Count >= 10);
        foreach (var (head, saved) in harbour.DumpAtHead)
            Assert.True(saved == _story.Dump(head), $"the answers as of head {head} are not the ones saved when it was the latest");
        Assert.NotEqual(harbour.DumpAtHead[5], harbour.DumpAtHead[harbour.DumpAtHead.Keys.Max()]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _story.State(harbour.DumpAtHead.Count + 1));
    }

    /// <summary>
    /// Move a scene in the telling and only reader-side answers change, with no ledger edit, and
    /// the scenes whose story time merely followed the telling are flagged.
    /// </summary>
    [Fact]
    public void Swapping_two_scenes_changes_what_the_reader_knows_and_nothing_else()
    {
        var root = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(harbour.Root, root);
            var story = Story.Open(root);
            var ledgerBefore = Directory.GetFiles(story.LedgerDir).OrderBy(f => f, StringComparer.Ordinal)
                .Select(Fixtures.Sha256OfFile).ToList();
            Assert.True(story.Sync().Clean, "the unedited story should sync clean");

            // Scene 4's file now sorts before scene 3's. The scene markers travel with the text.
            File.Move(Path.Combine(story.ManuscriptDir, "03-nothing-but-invoices.md"), Path.Combine(story.ManuscriptDir, "04a-nothing-but-invoices.md"));
            File.Move(Path.Combine(story.ManuscriptDir, "04-the-man-in-the-grey-coat.md"), Path.Combine(story.ManuscriptDir, "03-the-man-in-the-grey-coat.md"));
            var report = story.Sync();

            var expected = Fixtures.Node("expected-swapped.json");
            var state = story.State();
            Assert.Equal(expected["telling"]!.AsArray().Select(n => n!.GetValue<string>()), state.Telling.Select(t => t.Id));
            Assert.Equal(ledgerBefore, Directory.GetFiles(story.LedgerDir).OrderBy(f => f, StringComparer.Ordinal).Select(Fixtures.Sha256OfFile));

            var dump = StateDump.Build(state);
            var reader = new JsonObject();
            foreach (var (point, value) in dump["points"]!.AsObject())
                reader[point] = value!["reader"]!.DeepClone();
            Assert.Empty(JsonDiff.Differences(expected["reader"], reader));
            Assert.Empty(JsonDiff.Differences(expected["reveal_scene"], dump["reveal_scene"]));

            // Story time did not move: every person, object and fact is where it was.
            var original = Fixtures.Node("expected-state.json");
            foreach (var (point, value) in dump["points"]!.AsObject())
                foreach (var part in new[] { "beliefs", "world", "truth" })
                    Assert.Empty(JsonDiff.Differences(original["points"]![point]![part], value![part], $"{point}/{part}"));

            var mismatch = Assert.Single(report.TimeMismatches);
            Assert.Contains("sc_03", mismatch);
            Assert.Contains("sc_04", mismatch);
            Assert.Empty(report.BrokenEvidence);
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    [Fact]
    public void The_ledger_refuses_to_load_once_a_change_set_is_edited_or_removed()
    {
        var root = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(harbour.Root, root);
            var story = Story.Open(root);
            var files = Directory.GetFiles(story.LedgerDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            // Change one stance in a middle file, then in the newest file, which nothing follows.
            foreach (var file in new[] { files[5], files[^1] })
            {
                var original = File.ReadAllText(file);
                Assert.Contains("\"holds_true\"", original);
                var at = original.IndexOf("\"holds_true\"", StringComparison.Ordinal);
                File.WriteAllText(file, original[..at] + "\"holds_false\"" + original[(at + "\"holds_true\"".Length)..]);
                var edited = Assert.Throws<LedgerException>(() => story.State());
                Assert.Contains("edited after it was written", edited.Message);
                File.WriteAllText(file, original);
                Assert.Equal(harbour.DumpAtHead[harbour.DumpAtHead.Keys.Max()], story.Dump());
            }

            File.Delete(files[4]);
            var removed = Assert.Throws<LedgerException>(() => story.State());
            Assert.Contains("missing or out of order", removed.Message);
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    /// <summary>
    /// examples/harbour-letter is this story as the engine writes it, kept in the repository so there is
    /// something real to run the command line against. It must be exactly what the importer produces. To
    /// regenerate it after the fixtures change, run the tests once with NSE_UPDATE_EXAMPLE=1.
    /// </summary>
    [Fact]
    public void The_example_story_in_the_repository_is_what_the_engine_writes()
    {
        var example = Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter");
        if (Environment.GetEnvironmentVariable("NSE_UPDATE_EXAMPLE") == "1")
        {
            foreach (var part in new[] { "manuscript", "ledger" })
            {
                Fixtures.DeleteDirectory(Path.Combine(example, part));
                Fixtures.CopyDirectory(Path.Combine(harbour.Root, part), Path.Combine(example, part));
            }
            foreach (var file in new[] { ".gitignore", ".gitattributes" })
                File.Copy(Path.Combine(harbour.Root, file), Path.Combine(example, file), overwrite: true);
        }
        Assert.True(Directory.Exists(example), "examples/harbour-letter is missing. Run the tests once with NSE_UPDATE_EXAMPLE=1.");
        foreach (var part in new[] { "manuscript", "ledger" })
        {
            // Only the story's own files: the write lock beside the ledger is not part of it and is not in git.
            string[] Names(string root) => Directory.GetFiles(Path.Combine(root, part), part == "ledger" ? "*.json" : "*.md")
                .Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(Names(harbour.Root), Names(example));
            foreach (var name in Names(example))
                Assert.True(Fixtures.Sha256OfFile(Path.Combine(harbour.Root, part, name)) == Fixtures.Sha256OfFile(Path.Combine(example, part, name)),
                    $"examples/harbour-letter/{part}/{name} is not what the engine writes from the fixtures.");
        }
        // Read from a copy. The engine keeps a cache beside any story it reads, and the tests write nothing into the
        // repository: a dozen other tests copy this folder while this one runs.
        var copy = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(example, copy);
            Assert.True(Story.Open(copy).Sync().Clean);
        }
        finally
        {
            Fixtures.DeleteDirectory(copy);
        }
    }

    [Fact]
    public void A_copy_of_a_story_leaves_the_cache_behind()
    {
        var (from, to) = (Fixtures.NewTempDir(), Fixtures.NewTempDir());
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "ledger"));
            Directory.CreateDirectory(Path.Combine(from, ".nse"));
            File.WriteAllText(Path.Combine(from, "ledger", "000001-cs_x.json"), "{}");
            File.WriteAllText(Path.Combine(from, ".nse", "index.sqlite.123-abc.tmp"), "half written");
            Fixtures.CopyDirectory(from, to);
            Assert.True(File.Exists(Path.Combine(to, "ledger", "000001-cs_x.json")));
            Assert.False(Directory.Exists(Path.Combine(to, ".nse")));
        }
        finally
        {
            Fixtures.DeleteDirectory(from);
            Fixtures.DeleteDirectory(to);
        }
    }

    [Fact]
    public void Derived_labels_follow_from_the_records()
    {
        var s = _story.State();
        // Sarah says there was nothing in the desk but invoices, and believes otherwise: a lie, with no "lie" stored anywhere.
        var lie = s.Model.Assertions.Values.Single(a => a.Speaker == "ch_sarah" && a.Prop == "P12");
        Assert.StartsWith("lie", s.ClassifyAssertion(lie.Id));
        // Daniel believes Marta took the money until scene 10: a false belief, and afterwards knowledge that she did not.
        Assert.Equal("FALSE BELIEF", s.Classify(s.BeliefsBefore("ch_daniel", s.Key("sc_07"))["P03"], s.Key("sc_07")));
        Assert.Equal("knows it is false", s.Classify(s.BeliefsBefore("ch_daniel", s.Key("sc_11"))["P03"], s.Key("sc_11")));
        // The narration in scene 1 gives Daniel's conviction as fact.
        var narration = s.Model.Assertions.Values.Single(a => a.Type == AssertionKind.Narration);
        Assert.Equal("narration through Daniel: false", s.ClassifyAssertion(narration.Id));
    }
}
