using Nse.Model;
using Nse.Query;
using Nse.Tests.Support;

namespace Nse.Tests;

/// <summary>
/// The weave: the story as a signal graph. A change to one record spreads along weighted strands to what rests on
/// it, weakening with each step and through each hub, and every node keeps its strongest arrival. These tests pin
/// the mathematics (every strength is the product along its path), the determinism, and a few readings of the
/// example story that can be checked against what the story is about.
/// </summary>
public sealed class WeaveTests : IDisposable
{
    private readonly string _root;
    private readonly Story _story;

    public WeaveTests()
    {
        _root = Fixtures.NewTempDir();
        Fixtures.CopyDirectory(Path.Combine(Fixtures.RepoRoot, "examples", "harbour-letter"), _root);
        _story = Story.Open(_root);
    }

    public void Dispose() => Fixtures.DeleteDirectory(_root);

    private Weave Weave => Weave.Build(_story.State());

    [Fact]
    public void Every_strength_is_the_product_along_its_path_and_never_grows_along_it()
    {
        var w = Weave;
        foreach (var seed in new[] { "P10", "E09a", "ch_marta", "sc_07", "L6" })
        {
            foreach (var hit in w.Ripple(seed))
            {
                var strength = 1.0;
                for (var i = 0; i < hit.Path.Count; i++)
                {
                    var e = hit.Path[i];
                    var next = strength * e.Weight * (i == 0 ? 1.0 : Weave.Damping * w.FanOf(e.From));
                    Assert.True(next <= strength + 1e-12, $"{seed} -> {hit.Node.Id}: grew along {e.Kind}");
                    strength = next;
                }
                Assert.Equal(strength, hit.Strength, 12);
                Assert.InRange(hit.Strength, Weave.Floor, 1.0);
                Assert.Equal(hit.Path.Count, hit.Hops);
                Assert.Equal(seed, hit.Path[0].From);
                Assert.Equal(hit.Node.Id, hit.Path[^1].To);
            }
        }
    }

    [Fact]
    public void A_ripple_keeps_the_strongest_arrival_and_no_path_beats_it()
    {
        // Brute force over two and three steps: no path the search did not take arrives stronger.
        var w = Weave;
        var hits = w.Ripple("P10").ToDictionary(h => h.Node.Id, h => h.Strength);
        foreach (var a in w.Out("P10"))
            foreach (var b in w.Out(a.To))
            {
                var two = a.Weight * b.Weight * Weave.Damping * w.FanOf(b.From);
                if (b.To != "P10" && two >= Weave.Floor)
                    Assert.True(hits[b.To] >= two - 1e-12, $"P10 -> {a.To} -> {b.To} arrives at {two}, the ripple says {hits[b.To]}");
            }
    }

    /// <summary>
    /// Every path of at most so many strands from a seed, walked one at a time, with the strongest arrival at each
    /// node any of them reaches. Slow and plain, and nothing like the way the weave searches, which is the point.
    /// </summary>
    private static Dictionary<string, double> EveryPath(Weave w, string seed, int strands)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal) { seed };
        void Walk(string at, double strength, int left)
        {
            foreach (var e in w.Out(at))
            {
                if (onPath.Contains(e.To))
                    continue;
                var next = strength * e.Weight * (at == seed ? 1.0 : Weave.Damping * w.FanOf(at));
                if (next < Weave.Floor)
                    continue;
                if (!best.TryGetValue(e.To, out var had) || next > had)
                    best[e.To] = next;
                if (left > 1)
                {
                    onPath.Add(e.To);
                    Walk(e.To, next, left - 1);
                    onPath.Remove(e.To);
                }
            }
        }
        Walk(seed, 1.0, strands);
        return best;
    }

    [Fact]
    public void A_ripple_kept_to_a_radius_reaches_everything_within_it_at_its_strongest()
    {
        var w = Weave;
        foreach (var radius in new[] { 1, 2, 3 })
            foreach (var seed in w.Nodes.Keys)
            {
                var expected = EveryPath(w, seed, radius);
                var hits = w.Ripple(seed, maxHops: radius);
                Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), hits.Select(h => h.Node.Id).Order(StringComparer.Ordinal));
                Assert.All(hits, h =>
                {
                    Assert.Equal(expected[h.Node.Id], h.Strength, 12);
                    Assert.InRange(h.Hops, 1, radius);
                });
            }

        // The case that stopping the search early gets wrong. Two steps from A06 lie four other statements, each
        // through a node whose own strongest arrival is already two steps long, so a search that goes on only
        // from strongest arrivals never takes the second step.
        Assert.Superset(new HashSet<string> { "A09", "A10", "A24", "A24b" }, w.Ripple("A06", maxHops: 2).Select(h => h.Node.Id).ToHashSet());
        Assert.Empty(w.Ripple("A06", maxHops: 0));
    }

    [Fact]
    public void A_belief_that_was_out_of_date_when_it_was_formed_charges_the_pulse_like_a_false_one()
    {
        using var tiny = new TinyStory();
        // The key was under the mat and is not any more. Ben comes to think it still is.
        Assert.True(tiny.Put(
            new Proposition { Id = "p_there", Text = "The key is under the mat." },
            new FactChange { Id = "f_there", Prop = "p_there", Value = true, StoryKey = 10.1 },
            new FactChange { Id = "f_moved", Prop = "p_there", Value = false, StoryKey = 15 },
            new BeliefChange
            {
                Id = "b_stale", Holder = "ben", Prop = "p_there", Stance = Stance.HoldsTrue, Strength = Strength.Believes, StoryKey = 20.1, Scene = "s2",
                Channel = Channel.Assumed,
            }).Applied);
        var state = tiny.Story.State();
        Assert.Equal("OUTDATED BELIEF", state.Classify(state.BeliefsBefore("ben", StoryState.End)["p_there"]));
        var two = Weave.Build(state).Pulse(state).Single(p => p.Scene == "s2");
        Assert.Contains(two.Sources, x => x.Source == "p_there" && x.Share > 0);
    }

    [Fact]
    public void A_label_is_never_cut_through_the_middle_of_a_character()
    {
        using var tiny = new TinyStory();
        // A character outside the basic plane is two units long, and this one sits right where a label is cut.
        var text = new string('z', 66) + "\U0001F600 and then enough more words to need cutting";
        Assert.True(tiny.Put(new Proposition { Id = "p_long", Text = text }).Applied);
        Assert.Equal(new string('z', 66) + "...", Weave.Build(tiny.Story.State()).Nodes["p_long"].Label);
    }

    [Fact]
    public void A_ripple_from_a_fact_reaches_what_rests_on_it_first()
    {
        var hits = Weave.Ripple("P10");
        Assert.Equal(47, hits.Count);
        Assert.Equal(
            ["f_P10 0.950", "Bs08 0.900", "D32 0.900", "EM4 0.800", "SE1 0.800"],
            hits.Take(5).Select(h => $"{h.Node.Id} {h.Strength:0.000}"));
        // The secret that keeps P10 to Sarah also keeps P35 and P36, so a change to one reaches the others through it.
        var p35 = hits.Single(h => h.Node.Id == "P35");
        Assert.Equal(["secret.prop", "secret.prop.up"], p35.Path.Select(e => e.Kind));
        Assert.True(p35.FedBy >= 2);

        var text = Weave.RippleText(Weave, "P10", hits, 5);
        Assert.StartsWith("RIPPLE FROM P10 (proposition): Sarah took the letter from Edmund's desk drawer on the night he died.\nA change here would disturb 47 record(s)", text);
        Assert.Contains("        path: P10 -[fact_change.prop]-> f_P10\n", text);
        Assert.Contains("42 weaker record(s) not listed; raise --top to see them.", text);
    }

    [Fact]
    public void A_hub_passes_less_down_each_of_its_strands()
    {
        Assert.Equal(1.0, Weave.Fan(1));
        Assert.Equal(1.0, Weave.Fan(Weave.FanFree));
        Assert.Equal(0.5, Weave.Fan(16), 12);
        var w = Weave;
        Assert.True(w.Out("ch_sarah").Count > 50);
        Assert.True(w.FanOf("ch_sarah") < 0.3);
        // Without the fan, a fact would reach nearly the whole story through the people it involves.
        Assert.True(w.Ripple("P10").Count < w.Nodes.Count / 5);
    }

    [Fact]
    public void The_keystones_of_the_example_are_its_central_secret_and_the_two_discoveries()
    {
        var keystones = Weave.Keystones(3).Select(k => k.Node.Id).ToList();
        // Jonas is Edmund's son; the stranger comes asking for Edmund; Sarah finds the letter in April.
        Assert.Equal(["P07", "E04a", "E08a"], keystones);
    }

    [Fact]
    public void The_pulse_is_strongest_in_the_flashback_and_names_what_charges_each_scene()
    {
        var state = _story.State();
        var pulse = Weave.Build(state).Pulse(state);
        Assert.Equal(12, pulse.Count);
        Assert.Equal(["sc_08", "sc_05", "sc_11"], pulse.OrderByDescending(p => p.Energy).Take(3).Select(p => p.Scene));
        var april = pulse.Single(p => p.Scene == "sc_08");
        Assert.Equal("P12", april.Sources[0].Source);
        // Nothing charged reaches scene 2: its records are about facts no one holds wrongly and nothing withholds.
        Assert.Equal(0.0, pulse.Single(p => p.Scene == "sc_02").Energy);
        var text = Weave.PulseText(pulse, state.Model.Head);
        Assert.Contains("   8 sc_08       2.156  ####################  P12 0.612, P10 0.306, P18 0.306\n", text);
    }

    [Fact]
    public void Everything_is_the_same_on_every_run_and_from_a_rebuilt_index()
    {
        var first = (Weave.Edges.Count, Weave.RippleText(Weave, "E08a", Weave.Ripple("E08a"), 400), Weave.Mermaid(Weave, "P10", 2));
        Directory.Delete(Path.Combine(_root, ".nse"), recursive: true);
        var w = Weave;
        Assert.Equal(first, (w.Edges.Count, Weave.RippleText(w, "E08a", w.Ripple("E08a"), 400), Weave.Mermaid(w, "P10", 2)));
    }

    [Fact]
    public void The_picture_is_the_tree_the_ripple_grew()
    {
        var w = Weave;
        var text = Weave.Mermaid(w, "P10", 1);
        var hits = w.Ripple("P10", maxHops: 1);
        Assert.StartsWith("# The weave around P10\n", text);
        Assert.Contains("```mermaid\nflowchart LR\n", text);
        Assert.Equal(hits.Count, text.Split('\n').Count(l => l.Contains(" -->|", StringComparison.Ordinal)));
        Assert.Contains("[\"P10 1.000<br/>Sarah took the letter", text);
        Assert.All(hits, h => Assert.Equal(1, h.Hops));
    }

    [Fact]
    public void The_command_line_and_the_server_answer_and_refuse_what_they_cannot()
    {
        var output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["ripple", "P10", "--top", "5", "--dir", _root], output, new StringWriter()));
        Assert.Equal(Weave.RippleText(Weave, "P10", Weave.Ripple("P10"), 5), output.ToString());
        output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["pulse", "--dir", _root], output, new StringWriter()));
        Assert.StartsWith("PULSE · ledger head 16", output.ToString());
        output = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["weave", "--keystones", "3", "--dir", _root], output, new StringWriter()));
        Assert.Contains("P07 (proposition)", output.ToString());
        Assert.Equal(1, Cli.Cli.Run(["ripple", "P99", "--dir", _root], new StringWriter(), new StringWriter()));
        Assert.Equal(2, Cli.Cli.Run(["weave", "--dir", _root], new StringWriter(), new StringWriter()));
        Assert.Equal(2, Cli.Cli.Run(["weave", "--around", "P10", "--keystones", "3", "--dir", _root], new StringWriter(), new StringWriter()));
    }
}
