using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;
using Nse.Text;

namespace Nse.Tests;

/// <summary>The review queue in plain words: a sentence for each record after a paragraph on what its change set records.</summary>
public sealed class PlainReviewTests : IDisposable
{
    private readonly TinyStory _tiny = new();

    public void Dispose() => _tiny.Dispose();

    [Fact]
    public void A_denial_is_worded_as_a_denial()
    {
        var state = _tiny.Story.State();
        Assert.StartsWith("In s1, Ann says that it is not so that \"Ann hid the key under the mat\" (p_hid).",
            PlainText.Sentence(state, state.Model.Assertions["a_deny"]));
    }

    /// <summary>Every record of the example reads as one sentence, with its quote where it has one.</summary>
    [Fact]
    public void Every_record_of_the_example_reads_as_one_sentence_with_its_quote()
    {
        var root = Fixtures.NewTempDir();
        try
        {
            Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), root);
            var s = Story.Open(root).State();
            var kinds = new[] { typeof(Event), typeof(Assertion), typeof(BeliefChange), typeof(Disclosure), typeof(Placement), typeof(FactChange) };
            var records = s.Model.Records.Select(t => t.Record).Where(r => kinds.Contains(r.GetType())).ToList();
            Assert.Equal(292, records.Count);
            foreach (var record in records)
            {
                var sentence = PlainText.Sentence(s, record);
                Assert.DoesNotContain('\n', sentence);
                Assert.EndsWith(".", sentence);
                var quote = record switch
                {
                    Event e => e.Quote, Assertion a => a.Quote, BeliefChange b => b.Quote, Disclosure d => d.Quote, Placement p => p.Quote, _ => null,
                };
                if (quote is not null)
                    Assert.EndsWith($" The text: \"{QuoteMatcher.Normalize(quote)}\".", sentence);
                // A fact is given in its words as well as by its id.
                var prop = record switch { Assertion a => a.Prop, BeliefChange b => b.Prop, Disclosure d => d.Prop, FactChange f => f.Prop, _ => null };
                if (prop is not null)
                    Assert.Contains($"\"{s.Model.Proposition(prop)!.Text.Trim().TrimEnd('.')}\" ({prop})", sentence);
            }
            Assert.StartsWith(
                "In sc_08, Sarah becomes sure that \"Edmund wrote a private letter, sealed and stamped it, and never posted it\" (P08), by seeing it (E08a).",
                PlainText.Sentence(s, s.Model.Beliefs.First(b => b.Id == "Bs02")));
        }
        finally
        {
            Fixtures.DeleteDirectory(root);
        }
    }

    [Fact]
    public void The_queue_in_plain_words_gives_a_paragraph_and_then_a_sentence_for_each_record()
    {
        var manifest = ContextCompiler.Compile(_tiny.Story.State(), "s2").ManifestId;
        var result = _tiny.Story.SubmitScene(new SceneReport
        {
            Scene = "s2", Manifest = manifest,
            Records =
            [
                new Event
                {
                    Id = "e_find", Scene = "s2", StoryKey = 20.1, Summary = "Ben finds the key under the mat.",
                    Participants = new Dictionary<string, ParticipantRole> { ["ben"] = ParticipantRole.Actor }, Quote = "Ben found the key under the mat",
                },
                new Placement { Id = "l_pocket", Object = "key", Place = "pocket", StoryKey = 20.1, Scene = "s2", Cause = "e_find", Quote = "put it in his pocket" },
                _tiny.BenBelief("b_ben", 20.1, channel: Channel.TookPart, via: "e_find") with { Directness = Directness.Inferred },
            ],
        });
        Assert.True(result.Applied, string.Join("; ", result.Findings));

        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["review", "--plain", "--dir", _tiny.Root], output, new StringWriter()));
        var text = output.ToString();
        Assert.StartsWith("REVIEW QUEUE · ledger head 2 · in plain words\n", text);
        Assert.Contains("    This submission for s2 records 1 thing that happens, 1 change in what someone believes and 1 move of a tracked object. One is flagged: a belief the text does not state, which is ratified only by naming it.\n", text);
        Assert.Contains("    e_find: In s2, Ben finds the key under the mat (Ben takes part). The text: \"Ben found the key under the mat\".\n", text);
        Assert.Contains("    l_pocket: In s2, the key moves to Ben's pocket, because of e_find. The text: \"put it in his pocket\".\n", text);
        Assert.Contains("    FLAGGED b_ben: In s2, Ben comes to believe that \"Ann hid the key under the mat\" (p_hid), by taking part (e_find). It is marked inferred, not stated in the text.\n", text);
        Assert.Contains("To decide: nse ratify ID ...", text);

        // Without the flag, the queue is as it was.
        output = new StringWriter();
        Cli.Cli.Run(["review", "--dir", _tiny.Root], output, new StringWriter());
        Assert.DoesNotContain("in plain words", output.ToString());
        Assert.DoesNotContain("This submission for s2 records", output.ToString());
    }
}
