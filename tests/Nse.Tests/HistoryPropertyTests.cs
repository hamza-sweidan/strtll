using Nse.Index;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Tests;

/// <summary>
/// Older heads keep their answers, on histories the example story does not have. Its ledger is sixteen change
/// sets that only put records, which leaves untested one way for an older head to change its answer: a second
/// decision on one record. Here the ledger grows by random puts, ratifications, rejections and retirements,
/// many of them refused, and after every step the heads before it must still say what they said. The seeds are
/// fixed, so a failure can be run again.
/// </summary>
public sealed class HistoryPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Whatever_is_done_to_the_ledger_every_older_head_keeps_its_answer(int seed)
    {
        using var tiny = new TinyStory();
        var random = new Random(seed);
        var saved = new Dictionary<int, string> { [1] = tiny.Story.Dump() };
        var beliefs = new List<string>();
        var (applied, refused) = (0, 0);

        for (var step = 0; step < 70; step++)
        {
            Operation op;
            var roll = random.Next(10);
            if (roll < 5 || beliefs.Count == 0)
            {
                // A coarse grid of story times, so that records tie and disagree now and then.
                var stance = (Stance)random.Next(4);
                var id = $"b{step}";
                op = new PutOp
                {
                    Record = new BeliefChange
                    {
                        Id = id, Holder = random.Next(2) == 0 ? "ann" : "ben", Prop = "p_hid", Stance = stance,
                        Strength = stance is Stance.HoldsTrue or Stance.HoldsFalse ? (Strength)random.Next(3) : null,
                        StoryKey = 11 + random.Next(12) + random.Next(3) / 10.0, Channel = Channel.Assumed,
                    },
                    Approval = random.Next(4) == 0 ? Approval.Ratified : Approval.Provisional,
                };
                beliefs.Add(id);
            }
            else
            {
                var target = beliefs[random.Next(beliefs.Count)];
                op = roll switch
                {
                    < 7 => new RatifyOp { Ids = [target] },
                    < 9 => new RejectOp { Ids = [target] },
                    _ => new RetireOp { Id = target, Reason = "correction" },
                };
            }

            var result = tiny.Apply(op);
            if (!result.Applied)
            {
                // A second decision, a decision on a retired record, retiring twice: refused, and nothing moves.
                refused++;
                Assert.Equal("LEDGER", result.Findings[0].Code);
                continue;
            }
            applied++;
            var head = result.ChangeSet!.ChangeSet.Seq;
            saved[head] = tiny.Story.Dump();
            foreach (var older in new[] { head - 1, 1 + random.Next(head - 1) })
                Assert.True(saved[older] == tiny.Story.Dump(older), $"seed {seed}, step {step}: head {older} changed its answer once head {head} existed");
        }

        Assert.True(applied >= 25 && refused >= 5, $"seed {seed}: {applied} applied and {refused} refused is too thin a history to mean much");
        // At the end, every head, read through the index and replayed from the files.
        var ledger = tiny.Story.ReadLedger();
        var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
        var manuscript = tiny.Story.ReadManuscript();
        Assert.Equal(saved.Keys.Order(), Enumerable.Range(1, ledger.Count));
        foreach (var (head, dump) in saved)
        {
            var state = tiny.Story.State(head);
            Assert.True(state.FromIndex);
            Assert.True(dump == StateDump.Write(StateDump.Build(state)), $"seed {seed}: head {head} no longer gives its answer");
            Assert.True(dump == StateDump.Write(StateDump.Build(StoryIndex.FromFiles(projection, head, manuscript))),
                $"seed {seed}: head {head} replayed from the files differs from the index");
        }
    }
}
