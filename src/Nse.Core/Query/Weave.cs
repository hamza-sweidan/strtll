using System.Globalization;
using System.Text;
using Nse.Model;

namespace Nse.Query;

/// <summary>A point in the weave: one live record, with its kind and a short label.</summary>
public sealed record WeaveNode(string Id, string Kind, string Label);

/// <summary>
/// One strand of the weave, directed. <see cref="Weight"/> is how much of a disturbance at <see cref="From"/> reaches
/// <see cref="To"/> along it, between 0 and 1. <see cref="Kind"/> names the relation, for explanations.
/// </summary>
public sealed record WeaveEdge(string From, string To, string Kind, double Weight);

/// <summary>A record a ripple reached, how strongly, by which strongest path, and from how many sides.</summary>
public sealed record RippleHit(WeaveNode Node, double Strength, int Hops, IReadOnlyList<WeaveEdge> Path, int FedBy);

/// <summary>A scene's tension, and the sources that contribute most to it.</summary>
public sealed record PulseReading(string Scene, int Tell, double Energy, IReadOnlyList<(string Source, double Share)> Sources);

/// <summary>
/// The weave: the story as a signal graph. Every live record is a node, and every reference one record makes to
/// another is a pair of strands: a strong one from the thing referred to towards the record that rests on it (a
/// change to a fact disturbs every belief about it), and a weaker one back (a new belief changes the picture of who
/// knows the fact). Two kinds of strand are derived rather than read: each person's successive beliefs about one
/// fact, and each scene to the next in story time.
/// </summary>
/// <remarks>
/// <para>It is not a neural network: there is no training and no randomness. A disturbance spreads from a node along
/// every strand, multiplied by the strand's weight, by a damping factor for each step and by the fan of the node it
/// leaves (a hub passes less down each of its many strands), and each node keeps the
/// strongest arrival, with the path it came by. That is a shortest-path search on the negative logarithm of the
/// weights, so the result is exact, bounded between 0 and 1, the same on every run, and explained by a single path.
/// A node reached strongly from several sides at once is counted as such: those are the places where the story is
/// knotted.</para>
/// <para>Nothing here is stored. The weave is built from the records each time it is asked for, like every other
/// answer the engine gives.</para>
/// </remarks>
public sealed class Weave
{
    /// <summary>How much of a disturbance survives each step, on top of the strand's own weight.</summary>
    public const double Damping = 0.85;

    /// <summary>Below this a disturbance is taken to have died out.</summary>
    public const double Floor = 0.05;

    public IReadOnlyDictionary<string, WeaveNode> Nodes { get; }
    public IReadOnlyList<WeaveEdge> Edges { get; }
    private readonly Dictionary<string, List<WeaveEdge>> _out;
    private readonly Dictionary<string, List<WeaveEdge>> _in;

    private Weave(Dictionary<string, WeaveNode> nodes, List<WeaveEdge> edges)
    {
        Nodes = nodes;
        // One strand per direction, relation and pair: if a record refers to the same thing twice, the stronger counts.
        Edges = edges.GroupBy(e => (e.From, e.To, e.Kind)).Select(g => g.MaxBy(e => e.Weight)!)
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
        _out = Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _in = Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _fan = Nodes.Keys.ToDictionary(id => id, id => Fan(Out(id).Count), StringComparer.Ordinal);
    }

    private readonly Dictionary<string, double> _fan;

    /// <summary>
    /// The fan effect, after ACT-R's spreading activation: a node with many strands passes less down each, so that a
    /// disturbance passing through a hub (a main character, a busy scene) does not reach everything the hub touches
    /// at full strength. Up to <see cref="FanFree"/> strands pass undiminished; past that, by the square root.
    /// </summary>
    public static double Fan(int strands) => strands <= FanFree ? 1.0 : Math.Sqrt(FanFree / (double)strands);

    public const int FanFree = 4;

    /// <summary>What a node passes on per strand, as a share of what reached it.</summary>
    public double FanOf(string id) => _fan.GetValueOrDefault(id, 1.0);

    public IReadOnlyList<WeaveEdge> Out(string id) => _out.GetValueOrDefault(id) ?? [];

    public IReadOnlyList<WeaveEdge> In(string id) => _in.GetValueOrDefault(id) ?? [];

    /// <summary>
    /// The strand weights: for a record of one kind referring to a thing in one role, how strongly a change to the
    /// thing disturbs the record (down), and how strongly a change to the record tells on the thing (up). They are
    /// fixed here, in one table, so that every answer can be traced to them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double Down, double Up)> Weights = new Dictionary<string, (double, double)>(StringComparer.Ordinal)
    {
        ["fact_change.prop"] = (0.95, 0.9),
        ["belief_change.prop"] = (0.9, 0.5),
        ["belief_change.via"] = (0.9, 0.3),
        ["belief_change.holder"] = (0.5, 0.4),
        ["belief_change.scene"] = (0.6, 0.3),
        ["assertion.prop"] = (0.8, 0.5),
        ["assertion.speaker"] = (0.5, 0.3),
        ["assertion.hearer"] = (0.6, 0.3),
        ["assertion.focalizer"] = (0.4, 0.2),
        ["assertion.scene"] = (0.6, 0.3),
        ["event.participant"] = (0.5, 0.4),
        ["event.need"] = (0.9, 0.3),
        ["event.scene"] = (0.7, 0.4),
        ["disclosure.prop"] = (0.9, 0.5),
        ["disclosure.scene"] = (0.7, 0.4),
        ["placement.object"] = (0.8, 0.5),
        ["placement.place"] = (0.4, 0.2),
        ["placement.cause"] = (0.9, 0.3),
        ["placement.scene"] = (0.6, 0.3),
        ["proposition.about"] = (0.4, 0.3),
        ["proposition.nested_holder"] = (0.6, 0.3),
        ["proposition.nested_prop"] = (0.7, 0.4),
        ["proposition.ends"] = (0.4, 0.9),
        ["embargo.prop"] = (0.8, 0.6),
        ["embargo.until"] = (0.6, 0.4),
        ["secret.prop"] = (0.8, 0.6),
        ["secret.person"] = (0.5, 0.3),
        ["rule.prop"] = (0.7, 0.4),
        ["rule.holder"] = (0.5, 0.2),
        ["rule.scene"] = (0.6, 0.2),
        ["scene_contract.scene"] = (0.8, 0.6),
        ["scene_contract.intent"] = (0.6, 0.3),
        ["scene.pov"] = (0.5, 0.4),
        ["scene.cast"] = (0.4, 0.3),
        ["scene.place"] = (0.3, 0.2),
        ["scene_time.scene"] = (0.9, 0.9),
        ["entity.persona"] = (0.6, 0.6),
        // Derived: one person's next belief on the same fact, and the next scene in story time.
        ["then"] = (0.8, 0.2),
        ["next_scene"] = (0.35, 0.1),
    };

    public static Weave Build(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nodes = new Dictionary<string, WeaveNode>(StringComparer.Ordinal);
        foreach (var t in s.Model.Records)
            nodes[t.Record.Id] = new WeaveNode(t.Record.Id, t.Record.KindName, Label(s, t.Record));
        var edges = new List<WeaveEdge>();
        void Link(string record, string target, string relation)
        {
            if (!nodes.ContainsKey(record) || !nodes.ContainsKey(target) || record == target)
                return;
            var (down, up) = Weights[relation];
            edges.Add(new WeaveEdge(target, record, relation, down));
            edges.Add(new WeaveEdge(record, target, relation + ".up", up));
        }
        foreach (var t in s.Model.Records)
        {
            var r = t.Record;
            switch (r)
            {
                case FactChange f:
                    Link(r.Id, f.Prop, "fact_change.prop");
                    break;
                case BeliefChange b:
                    Link(r.Id, b.Prop, "belief_change.prop");
                    Link(r.Id, b.Holder, "belief_change.holder");
                    if (b.Via is { } via) Link(r.Id, via, "belief_change.via");
                    if (b.Scene is { } bs) Link(r.Id, bs, "belief_change.scene");
                    break;
                case Assertion a:
                    Link(r.Id, a.Prop, "assertion.prop");
                    Link(r.Id, a.Speaker, "assertion.speaker");
                    foreach (var h in a.Addressees.Concat(a.Overhearers).Concat(a.ReadBy is null ? [] : [a.ReadBy]))
                        Link(r.Id, h, "assertion.hearer");
                    if (a.Focalizer is { } fz) Link(r.Id, fz, "assertion.focalizer");
                    Link(r.Id, a.Scene, "assertion.scene");
                    break;
                case Event e:
                    foreach (var p in e.Participants.Keys)
                        Link(r.Id, p, "event.participant");
                    foreach (var need in e.Needs ?? [])
                        Link(r.Id, need, "event.need");
                    if (e.Scene is { } es) Link(r.Id, es, "event.scene");
                    break;
                case Disclosure d:
                    Link(r.Id, d.Prop, "disclosure.prop");
                    Link(r.Id, d.Scene, "disclosure.scene");
                    break;
                case Placement p:
                    Link(r.Id, p.Object, "placement.object");
                    Link(r.Id, p.Place, "placement.place");
                    Link(r.Id, p.Cause, "placement.cause");
                    if (p.Scene is { } ps) Link(r.Id, ps, "placement.scene");
                    break;
                case Proposition p:
                    foreach (var about in p.About)
                        Link(r.Id, about, "proposition.about");
                    if (p.Nested is { } n)
                    {
                        Link(r.Id, n.Holder, "proposition.nested_holder");
                        Link(r.Id, n.Prop, "proposition.nested_prop");
                    }
                    foreach (var who in p.Ends ?? [])
                        Link(r.Id, who, "proposition.ends");
                    break;
                case Embargo em:
                    Link(r.Id, em.Prop, "embargo.prop");
                    Link(r.Id, em.UntilScene, "embargo.until");
                    break;
                case Secret x:
                    foreach (var p in x.Props) Link(r.Id, p, "secret.prop");
                    foreach (var who in x.KnownOnlyTo) Link(r.Id, who, "secret.person");
                    break;
                case Rule rule:
                    if (rule.Prop is { } rp) Link(r.Id, rp, "rule.prop");
                    if (rule.Scope is { } scope)
                    {
                        foreach (var h in scope.Holders) Link(r.Id, h, "rule.holder");
                        foreach (var sc in scope.Scenes.Concat(new[] { scope.FromScene, scope.UntilScene }.OfType<string>()))
                            Link(r.Id, sc, "rule.scene");
                    }
                    break;
                case SceneContract c:
                    Link(r.Id, c.Scene, "scene_contract.scene");
                    foreach (var i in c.Intends.Concat(c.Relies))
                        foreach (var target in new[] { i.Holder, i.Prop, i.Object, i.Place }.OfType<string>())
                            Link(r.Id, target, "scene_contract.intent");
                    break;
                case SceneRecord sc:
                    Link(r.Id, sc.Pov, "scene.pov");
                    foreach (var c in sc.Cast.Where(c => c != sc.Pov)) Link(r.Id, c, "scene.cast");
                    if (sc.Place is { } place) Link(r.Id, place, "scene.place");
                    break;
                case SceneTime st:
                    Link(r.Id, st.Scene, "scene_time.scene");
                    break;
                case Entity en:
                    if (en.PersonaOf is { } person) Link(r.Id, person, "entity.persona");
                    break;
            }
        }
        // Each person's beliefs about one fact, in story order: a change to an earlier one disturbs the next.
        foreach (var chain in s.Model.Beliefs.GroupBy(b => (b.Holder, b.Prop)))
        {
            var ordered = chain.OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Link(ordered[i].Id, ordered[i - 1].Id, "then");
        }
        // Scenes in story time: what happens in one is the ground the next one stands on.
        var timed = s.Model.SceneTimes.Values.Where(t => s.Model.Scenes.ContainsKey(t.Scene))
            .OrderBy(t => t.StoryKey).ThenBy(t => t.Scene, StringComparer.Ordinal).Select(t => t.Scene).ToList();
        for (var i = 1; i < timed.Count; i++)
            Link(timed[i], timed[i - 1], "next_scene");
        return new Weave(nodes, edges);
    }

    private static string Label(StoryState s, LedgerRecord r)
    {
        static string Cut(string text, int max = 70)
        {
            var one = Nse.Text.Plain.OneLine(text).Trim();
            if (one.Length <= max)
                return one;
            var keep = Nse.Text.Plain.SplitsCharacter(one, max - 3) ? max - 4 : max - 3;
            return one[..keep].TrimEnd() + "...";
        }
        return r switch
        {
            Entity e => Cut(e.Name),
            SceneRecord sc => Cut(sc.Title ?? s.Telling.FirstOrDefault(t => t.Id == sc.Id)?.Title ?? sc.Id),
            Proposition p => Cut(p.Text),
            Event e => Cut(e.Summary),
            _ => Cut(RecordText.Line(s, r)),
        };
    }

    /// <summary>How much of a disturbance gets to a node, and the path it takes.</summary>
    private readonly record struct Arrival(double Strength, IReadOnlyList<WeaveEdge> Path);

    /// <summary>
    /// Spread a disturbance from one node. Each node reached keeps the strongest arrival: the product of the strand
    /// weights along the path, damped once for every step after the first. Ties in strength go to the path found
    /// through the node with the smaller id, so the answer is the same on every run. With
    /// <paramref name="maxHops"/>, only paths of at most that many strands count, and each node keeps the strongest
    /// of those.
    /// </summary>
    public IReadOnlyList<RippleHit> Ripple(string seed, double floor = Floor, int? maxHops = null)
    {
        if (!Nodes.ContainsKey(seed))
            throw new KeyNotFoundException($"No live record {seed} to start a ripple from.");
        var arrivals = maxHops is int limit ? Within(seed, floor, limit) : Strongest(seed, floor);
        double? Reached(string id) => id == seed ? 1.0 : arrivals.TryGetValue(id, out var a) ? a.Strength : null;
        var hits = new List<RippleHit>();
        foreach (var (id, (strength, path)) in arrivals)
        {
            // Fed from several sides: strands into this node from other reached nodes that alone would bring at
            // least half its strength. One is the path itself.
            var fed = In(id).Count(e => Reached(e.From) is double from && Carried(from, e, seed) >= strength / 2);
            hits.Add(new RippleHit(Nodes[id], strength, path.Count, path, fed));
        }
        return hits.OrderByDescending(h => h.Strength).ThenBy(h => h.Node.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What a strand carries on of the strength that reached the node it leaves. The seed passes its whole
    /// disturbance down every strand: it is the record asked about. Every other node damps it and shares it out
    /// by its fan.
    /// </summary>
    private double Carried(double strength, WeaveEdge e, string seed) =>
        strength * e.Weight * (e.From == seed ? 1.0 : Damping * FanOf(e.From));

    /// <summary>The strongest arrival at every node the disturbance reaches, by a path of any length.</summary>
    private Dictionary<string, Arrival> Strongest(string seed, double floor)
    {
        var best = Spread(seed, floor);
        var arrivals = new Dictionary<string, Arrival>(StringComparer.Ordinal);
        foreach (var (id, (strength, via)) in best)
        {
            if (via is null)
                continue;
            var path = new List<WeaveEdge>();
            for (var e = via; e is not null; e = best[e.From].Via)
                path.Add(e);
            path.Reverse();
            arrivals[id] = new Arrival(strength, path);
        }
        return arrivals;
    }

    /// <summary>
    /// How strongly a disturbance arrives at every node it reaches, the seed included at full strength, and the
    /// strand each strongest arrival came by. The path to a node is read back along those strands.
    /// </summary>
    private Dictionary<string, (double Strength, WeaveEdge? Via)> Spread(string seed, double floor)
    {
        var best = new Dictionary<string, (double Strength, WeaveEdge? Via)>(StringComparer.Ordinal) { [seed] = (1.0, null) };
        var queue = new SortedSet<(double Neg, string Id)>(Comparer<(double Neg, string Id)>.Create((a, b) =>
        {
            var c = a.Neg.CompareTo(b.Neg);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        })) { (-1.0, seed) };
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var id = queue.Min.Id;
            queue.Remove(queue.Min);
            if (!done.Add(id))
                continue;
            var strength = best[id].Strength;
            foreach (var e in Out(id))
            {
                var next = Carried(strength, e, seed);
                if (next < floor || done.Contains(e.To))
                    continue;
                if (!best.TryGetValue(e.To, out var had) || next > had.Strength
                    || (next == had.Strength && string.CompareOrdinal(e.From, had.Via!.From) < 0))
                {
                    if (best.TryGetValue(e.To, out var old))
                        queue.Remove((-old.Strength, e.To));
                    best[e.To] = (next, e);
                    queue.Add((-next, e.To));
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The strongest arrival at every node by a path of at most <paramref name="limit"/> strands. Cutting
    /// <see cref="Strongest"/> short does not give this. A node's strongest arrival may come by a long path that
    /// uses up the steps, while a weaker and shorter arrival still has one to go on with: stop at the strongest,
    /// and what lies a step past that node is missed though it is within the limit. So this goes one strand at a
    /// time. Each round holds the strongest arrival at each node by a path of exactly that many strands, and a
    /// node's answer is its strongest over the rounds, the shorter path on a tie.
    /// </summary>
    private Dictionary<string, Arrival> Within(string seed, double floor, int limit)
    {
        var best = new Dictionary<string, Arrival>(StringComparer.Ordinal);
        var round = new Dictionary<string, Arrival>(StringComparer.Ordinal) { [seed] = new Arrival(1.0, []) };
        for (var strands = 1; strands <= limit && round.Count > 0; strands++)
        {
            var next = new Dictionary<string, Arrival>(StringComparer.Ordinal);
            foreach (var (id, arrival) in round)
                foreach (var e in Out(id))
                {
                    var strength = Carried(arrival.Strength, e, seed);
                    if (strength < floor || e.To == seed)
                        continue;
                    if (!next.TryGetValue(e.To, out var had) || strength > had.Strength
                        || (strength == had.Strength && Before(e, had.Path[^1])))
                        next[e.To] = new Arrival(strength, [.. arrival.Path, e]);
                }
            foreach (var (id, arrival) in next)
                if (!best.TryGetValue(id, out var had) || arrival.Strength > had.Strength)
                    best[id] = arrival;
            round = next;
        }
        return best;
    }

    /// <summary>Which of two strands into one node wins a tie: the one from the node with the smaller id, then the relation with the smaller name.</summary>
    private static bool Before(WeaveEdge a, WeaveEdge b)
    {
        var from = string.CompareOrdinal(a.From, b.From);
        return from != 0 ? from < 0 : string.CompareOrdinal(a.Kind, b.Kind) < 0;
    }

    /// <summary>How much of the story a change to a record would reach: the sum of the strengths of everything its ripple reaches.</summary>
    /// <remarks>
    /// Only the strengths are worked out, not the paths or the sides each node is fed from: the keystones ask this
    /// of every record. They are added up strongest first and then by id, the order a ripple lists them in, because
    /// a sum of fractions depends on its order in the last digits and this one is printed and compared.
    /// </remarks>
    public double Reach(string seed)
    {
        if (!Nodes.ContainsKey(seed))
            throw new KeyNotFoundException($"No live record {seed} to start a ripple from.");
        var reach = 0.0;
        foreach (var (_, strength) in Spread(seed, Floor).Where(arrival => arrival.Value.Via is not null)
                     .Select(arrival => (arrival.Key, arrival.Value.Strength))
                     .OrderByDescending(arrival => arrival.Strength).ThenBy(arrival => arrival.Key, StringComparer.Ordinal))
            reach += strength;
        return reach;
    }

    /// <summary>
    /// The records whose change would move the most, by reach, among the kinds an author changes: facts and what
    /// happens, who learns what, what the reader is given, and where things are. Scenes and people are left out:
    /// they touch everything by being where things happen.
    /// </summary>
    public IReadOnlyList<(WeaveNode Node, double Reach)> Keystones(int top)
    {
        string[] kinds = ["proposition", "fact_change", "event", "assertion", "belief_change", "disclosure", "placement"];
        return Nodes.Values.Where(n => kinds.Contains(n.Kind))
            .Select(n => (Node: n, Reach: Reach(n.Id)))
            .OrderByDescending(x => x.Reach).ThenBy(x => x.Node.Id, StringComparer.Ordinal)
            .Take(top).ToList();
    }

    /// <summary>
    /// The tension field. Sources of tension are injected at the facts they concern, and each spreads through the
    /// weave; a scene's pulse is the sum of what arrives from every source. One source cannot count twice: it
    /// arrives by its strongest path only. Sources, each with its charge:
    /// someone holds a fact the wrong way (a false or outdated belief), 1 for each such person;
    /// the fact is embargoed, 0.5; it is kept by a secret, 0.5; a rule says it is never to be settled, 0.5.
    /// </summary>
    public IReadOnlyList<PulseReading> Pulse(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var charge = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string prop, double amount)
        {
            if (Nodes.ContainsKey(prop))
                charge[prop] = charge.GetValueOrDefault(prop) + amount;
        }
        foreach (var group in s.Model.Beliefs.GroupBy(b => (b.Holder, b.Prop)))
            if (group.Any(b => s.Classify(new BeliefView(b.Id, b.Holder, b.Prop, b.Stance, b.Strength, b.StoryKey, b.Scene, b.Channel, b.Via)) is "FALSE BELIEF" or "OUTDATED BELIEF"))
                Add(group.Key.Prop, 1.0);
        foreach (var e in s.Model.Embargoes) Add(e.Prop, 0.5);
        foreach (var x in s.Model.Secrets) foreach (var p in x.Props) Add(p, 0.5);
        foreach (var r in s.Model.Rules.Where(SceneKnowledge.NeverSettle)) if (r.Prop is { } p) Add(p, 0.5);

        var scenes = s.Telling.Where(t => Nodes.ContainsKey(t.Id)).ToList();
        var energy = scenes.ToDictionary(t => t.Id, _ => 0.0, StringComparer.Ordinal);
        var shares = scenes.ToDictionary(t => t.Id, _ => new List<(string, double)>(), StringComparer.Ordinal);
        foreach (var (prop, amount) in charge.OrderBy(c => c.Key, StringComparer.Ordinal))
            foreach (var hit in Ripple(prop).Where(h => energy.ContainsKey(h.Node.Id)))
            {
                var share = amount * hit.Strength;
                energy[hit.Node.Id] += share;
                shares[hit.Node.Id].Add((prop, share));
            }
        return scenes.Select(t => new PulseReading(t.Id, t.Tell, energy[t.Id],
            shares[t.Id].OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.Ordinal).Take(3).ToList())).ToList();
    }

    private static string N(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>A ripple in words: what a change to one record would disturb, strongest first, each with its path.</summary>
    public static string RippleText(Weave w, string seed, IReadOnlyList<RippleHit> hits, int top)
    {
        var node = w.Nodes[seed];
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"RIPPLE FROM {seed} ({node.Kind}): {node.Label}\nA change here would disturb {hits.Count} record(s) before dying out below {N(Floor)}; reach {N(hits.Sum(h => h.Strength))}. Strength is the share of the disturbance that arrives, along the strongest path; 'fed by' counts the sides it arrives from at half strength or more.\n");
        foreach (var group in hits.Take(top).GroupBy(h => h.Node.Kind).OrderByDescending(g => g.Max(h => h.Strength)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n{group.Key} ({group.Count()}):\n");
            foreach (var h in group)
                sb.Append(CultureInfo.InvariantCulture,
                    $"  {N(h.Strength)}  {h.Node.Id}: {h.Node.Label}{(h.FedBy > 1 ? $" [fed by {h.FedBy}]" : "")}\n        path: {Path(seed, h.Path)}\n");
        }
        if (hits.Count > top)
            sb.Append(CultureInfo.InvariantCulture, $"\n{hits.Count - top} weaker record(s) not listed; raise --top to see them.\n");
        return sb.ToString();
    }

    private static string Path(string seed, IReadOnlyList<WeaveEdge> path) =>
        seed + string.Concat(path.Select(e => $" -[{e.Kind}]-> {e.To}"));

    /// <summary>The pulse in words: each scene in the order told, its tension, a bar, and what charges it most.</summary>
    public static string PulseText(IReadOnlyList<PulseReading> readings, int head)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"PULSE · ledger head {head} · the tension field: false and outdated beliefs, embargoes, secrets and never-settle rules, spread through the weave to each scene\n\n");
        var max = readings.Count == 0 ? 0 : readings.Max(r => r.Energy);
        foreach (var r in readings)
        {
            var bar = max <= 0 ? "" : new string('#', (int)Math.Round(20 * r.Energy / max, MidpointRounding.AwayFromZero));
            sb.Append(CultureInfo.InvariantCulture,
                $"  {r.Tell,2} {r.Scene,-8} {N(r.Energy),8}  {bar,-20}  {string.Join(", ", r.Sources.Select(x => $"{x.Source} {N(x.Share)}"))}\n");
        }
        return sb.ToString();
    }

    /// <summary>The neighbourhood of a record as a Mermaid flowchart: everything its ripple reaches within some steps, with the strands between them.</summary>
    public static string Mermaid(Weave w, string seed, int radius)
    {
        var hits = w.Ripple(seed, maxHops: radius);
        var keep = new HashSet<string>(hits.Select(h => h.Node.Id).Append(seed), StringComparer.Ordinal);
        var ids = keep.OrderBy(x => x, StringComparer.Ordinal).Select((id, i) => (id, i)).ToDictionary(x => x.id, x => $"n{x.i}", StringComparer.Ordinal);
        var strength = hits.ToDictionary(h => h.Node.Id, h => h.Strength, StringComparer.Ordinal);
        strength[seed] = 1.0;
        static string Label(string text) => text.Replace("\"", "#quot;", StringComparison.Ordinal).Replace("|", "#124;", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# The weave around {seed}\n\nEverything a change to {seed} reaches within {radius} step(s), with the strength that arrives. Generated by `nse weave`; do not edit.\n\n```mermaid\nflowchart LR\n");
        foreach (var id in keep.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"  {ids[id]}[\"{Label(id)} {N(strength[id])}<br/>{Label(w.Nodes[id].Label)}\"]\n");
        // The strands that carried each strongest arrival, so the picture is the tree the ripple grew.
        foreach (var h in hits.OrderBy(h => h.Node.Id, StringComparer.Ordinal))
        {
            var last = h.Path[^1];
            sb.Append(CultureInfo.InvariantCulture, $"  {ids[last.From]} -->|{N(last.Weight)}| {ids[last.To]}\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"  style {ids[seed]} stroke-width:3px\n```\n");
        return sb.ToString();
    }
}
