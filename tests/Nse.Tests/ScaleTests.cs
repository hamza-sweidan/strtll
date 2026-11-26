using System.Globalization;
using System.Text.Json.Nodes;
using Nse.Index;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// What an answer costs as a story grows. The example story is laid end to end, each copy with its own ids and
/// its story time moved on, and every answer is asked at two lengths. The cost is a count, not a time: each
/// record a query is handed, each lookup, each paragraph a scan reads and each pattern it runs. A count is the
/// same on a busy machine as on an idle one, so nothing here can fail for being run at a bad moment.
/// </summary>
/// <remarks>
/// An answer may cost as much more as it has more to say. A context that lists every fact a person does not know
/// is twice as long in a story twice the length, and may take twice the work. What it may not do is take four
/// times the work, which is what happens when a pass over all the records sits inside a loop over all the facts.
/// At 240 scenes that difference is one context in seconds, not milliseconds, and one what-if in half an hour.
/// </remarks>
public sealed class ScaleTests : IDisposable
{
    private const int Short = 2, Long = 4;

    /// <summary>How much faster than its own answer a cost may grow before it counts as out of line.</summary>
    private const double Slack = 1.4;

    private readonly string _root = Fixtures.NewTempDir();
    private readonly string _example = Fixtures.NewTempDir();

    public void Dispose()
    {
        Fixtures.DeleteDirectory(_root);
        Fixtures.DeleteDirectory(_example);
    }

    /// <summary>What an answer took and how much it said.</summary>
    private sealed record Cost(long Work, long Size);

    [Fact]
    public void An_answer_costs_no_more_as_the_story_grows_than_it_has_more_to_say()
    {
        var story = Story.Init(_root);
        Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), _example);
        var source = Story.Open(_example).State();

        Repeat(story, source, from: 0, to: Short);
        var few = Measure(story);
        Repeat(story, source, from: Short, to: Long);
        var many = Measure(story);
        Assert.Equal(source.Telling.Count * Long, story.State().Telling.Count);

        // Each of these says more in a longer story, and may cost that much more.
        foreach (var answer in new[] { "one scene's context", "one scene's audit brief", "the state dump", "every scene's context" })
        {
            var (work, size) = ((double)many[answer].Work / few[answer].Work, (double)many[answer].Size / few[answer].Size);
            Assert.True(work <= Slack * size, string.Create(CultureInfo.InvariantCulture,
                $"{answer}: the story doubled, the answer grew {size:0.00} times ({few[answer].Size} to {many[answer].Size} characters), and the work to give it grew {work:0.00} times ({few[answer].Work} to {many[answer].Work} records looked at). A pass over the whole story has got inside a loop over the story"));
        }

        // The scan has one pass to make over each scene, whatever the scene has to say: twice the scenes, twice the work.
        var scan = (double)many["the scan of every scene"].Work / few["the scan of every scene"].Work;
        Assert.True(scan <= Slack * Long / Short, string.Create(CultureInfo.InvariantCulture,
            $"the scan of every scene: the story doubled and the work grew {scan:0.00} times ({few["the scan of every scene"].Work} to {many["the scan of every scene"].Work}). A paragraph is read, or a pattern made ready, more than once"));

        // A what-if is the dump and every context, twice over, compared. It may cost that and no more.
        var parts = 2 * (many["the state dump"].Work + many["every scene's context"].Work);
        var whatIf = WhatIfOfOneNote(story);
        Assert.True(whatIf <= 1.1 * parts, $"a what-if of one note took {whatIf}, and the two dumps and two sets of contexts it compares take {parts}");
    }

    private static Dictionary<string, Cost> Measure(Story story)
    {
        var costs = new Dictionary<string, Cost>(StringComparer.Ordinal);
        // Each answer is asked of a state of its own, so that none is helped by what another has already worked out.
        void Ask(string answer, Func<StoryState, long> size)
        {
            var state = story.State();
            var said = size(state);
            costs[answer] = new Cost(state.Model.Effort.Spent, said);
        }
        Ask("one scene's context", s => ContextCompiler.Compile(s, s.Telling[^1].Id, int.MaxValue).Text.Length);
        Ask("one scene's audit brief", s =>
        {
            var last = s.Telling[^1];
            return AuditBrief.Text(s, last.Id, AuditMode.Leaks, DraftScan.Run(s, last.Id, DraftScan.Paragraphs(last), "the scene")).Length;
        });
        Ask("the state dump", s => StateDump.Write(StateDump.Build(s)).Length);
        Ask("every scene's context", s => s.Telling.Sum(t => (long)ContextCompiler.Compile(s, t.Id, int.MaxValue).Text.Length));
        Ask("the scan of every scene", s => DraftScan.All(s).Count);
        return costs;
    }

    /// <summary>
    /// What it takes to work out what one note for every scene would change: nothing any answer about the story
    /// says, and one line of every scene's context.
    /// </summary>
    private static long WhatIfOfOneNote(Story story)
    {
        var manuscript = story.ReadManuscript();
        var before = Projection.Build(story.ReadLedger().Select(x => x.ChangeSet));
        var after = before.With(new ChangeSet
        {
            Seq = before.Head + 1, Id = "cs_scale", Actor = "author", Manuscript = manuscript.Hash,
            Ops = [new PutOp { Record = new Rule { Id = "g_scale", Type = "guidance", Text = "A note for every scene." } }],
        });
        var (was, now) = (StoryIndex.FromFiles(before, null, manuscript), StoryIndex.FromFiles(after, null, manuscript));
        Assert.Empty(WhatIf.Differences(was, now));
        var contexts = WhatIf.Contexts(was, now);
        Assert.Equal(manuscript.Scenes.Count, contexts.Count);
        Assert.All(contexts, change => Assert.Equal(1, change.Lines));
        return was.Model.Effort.Spent + now.Model.Effort.Spent;
    }

    /// <summary>Lay copies of the example on the end of a story, through the write path, each as one change set.</summary>
    private static void Repeat(Story story, StoryState source, int from, int to)
    {
        var ids = source.Model.ById.Keys.ToHashSet(StringComparer.Ordinal);
        for (var copy = from; copy < to; copy++)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"r{copy:00}_");
            foreach (var scene in source.Telling.GroupBy(t => t.File))
            {
                var text = File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter", "manuscript", scene.Key));
                foreach (var t in scene)
                    text = text.Replace($"<!-- scene: {t.Id} -->", $"<!-- scene: {prefix}{t.Id} -->", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(story.ManuscriptDir, string.Create(CultureInfo.InvariantCulture, $"r{copy:00}-{scene.Key}")), text);
            }
            var ops = source.Model.Records
                .Select(t => Renamed(JsonNode.Parse(NseJson.Write(t.Record)), ids, prefix, copy * 1000.0)!.ToJsonString())
                .Select(json => (Operation)new PutOp { Record = NseJson.Read<LedgerRecord>(json), Approval = Approval.Ratified })
                .ToList();
            var put = story.Put(new ChangeSetDraft { Actor = "import:scale", Note = "copy " + copy.ToString(CultureInfo.InvariantCulture), Ops = ops });
            Assert.True(put.Applied, string.Join("; ", put.Findings.Take(5)));
        }
    }

    /// <summary>A record with every id in it given a copy's prefix, and every story time moved on by the copy's shift.</summary>
    private static JsonNode? Renamed(JsonNode? node, HashSet<string> ids, string prefix, double shift, string? name = null)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                    copy[ids.Contains(k) ? prefix + k : k] = Renamed(v, ids, prefix, shift, k);
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(x => Renamed(x, ids, prefix, shift)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var s):
                return ids.Contains(s) ? prefix + s : s;
            case JsonValue v when name == "story_key" && v.TryGetValue<double>(out var d):
                return d + shift;
            default:
                return node?.DeepClone();
        }
    }
}
