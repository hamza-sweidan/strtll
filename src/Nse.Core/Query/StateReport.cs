using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Nse.Model;

namespace Nse.Query;

/// <summary>
/// A focused question: one point in the story, narrowed to a person, a fact, the reader or an object.
/// With <see cref="History"/> and a fact, the answer is instead every change to that fact across the story;
/// with <see cref="History"/> and an object, every recorded move of that object.
/// </summary>
public sealed record StateQuery(
    string? Scene = null, bool End = false, string? Holder = null, string? Prop = null, string? Object = null,
    bool ReaderOnly = false, bool History = false);

/// <summary>
/// The answer to a focused question, as text for a person or an agent and as JSON for a program. Both front
/// ends, the command line and the MCP server, print exactly this.
/// </summary>
public static class StateReport
{
    private sealed record Plan(double Key, int Tell, string? Holder, bool People, bool Reader, bool World);

    private static void CheckIds(StoryState s, StateQuery q)
    {
        if (q.Holder is not null && s.Model.Entity(q.Holder) is null)
            throw new KeyNotFoundException($"No entity {q.Holder}.");
        if (q.Prop is not null && s.Model.Proposition(q.Prop) is null)
            throw new KeyNotFoundException($"No proposition {q.Prop}.");
        if (q.Object is not null && s.Model.Entity(q.Object) is null)
            throw new KeyNotFoundException($"No entity {q.Object}.");
    }

    private static Plan Resolve(StoryState s, StateQuery q)
    {
        if ((q.Scene is null) == !q.End)
            throw new ArgumentException("Ask about exactly one point: a scene id, or the end of the story.");
        var key = q.End ? StoryState.End : s.Key(q.Scene!);
        var tell = q.End ? StoryState.EndOfTelling : s.Tell(q.Scene!)
            ?? throw new KeyNotFoundException($"Scene {q.Scene} has no marker in the manuscript, so it has no place in the telling.");
        CheckIds(s, q);
        return new Plan(key, tell, q.Holder is null ? null : s.Model.Base(q.Holder),
            People: !q.ReaderOnly && q.Object is null,
            Reader: q.ReaderOnly || (q.Holder is null && q.Object is null),
            World: q.Object is not null || (q.Holder is null && q.Prop is null && !q.ReaderOnly));
    }

    public static string Text(StoryState s, StateQuery q)
    {
        if (q.History)
        {
            if ((q.Prop is null) == (q.Object is null))
                throw new ArgumentException("A history is of one fact or one object: give a proposition id or an object id, not both.");
            CheckIds(s, q);
            return q.Prop is not null ? History(s, q) : Moves(s, q);
        }
        var plan = Resolve(s, q);
        var (key, tell) = (plan.Key, plan.Tell);
        var sb = new StringBuilder();
        var where = q.End ? "AT THE END OF THE STORY" : $"ON ENTRY TO {q.Scene}";
        var position = q.End ? "" : string.Create(CultureInfo.InvariantCulture, $" (story time {key}, number {tell} in the telling)");
        sb.Append(CultureInfo.InvariantCulture, $"STATE {where}{position} · ledger head {s.Model.Head}\n");
        if (!q.End && s.Telling.FirstOrDefault(t => t.Tell == tell - 1) is { } before
            && s.Model.SceneTimes.TryGetValue(before.Id, out var earlier) && key < earlier.StoryKey)
            sb.Append("This scene is told out of story order. People and objects are shown as of its story time; the reader as of its place in the telling.\n");
        if (q.Prop is not null)
        {
            // Say the truth outright, so it does not have to be read off the labels.
            // "At this point" is on entry: a fact change dated at the scene's own story time has not happened yet.
            var canon = s.Canon(q.Prop);
            var now = canon is not ("true" or "false") ? ""
                : s.FactTie(q.Prop, key, before: true) is { } tie ? $"; not settled at this point: fact changes {string.Join(" and ", tie)} share a story time and disagree"
                : s.TruthBefore(q.Prop, key) == true ? "; the case at this point" : "; not the case at this point";
            sb.Append(CultureInfo.InvariantCulture, $"{q.Prop}: {s.Model.Proposition(q.Prop)!.Text} Canon: {canon}{now}.{s.UnconfirmedMark(q.Prop, key)}\n");
        }
        if (plan.People)
            foreach (var h in plan.Holder is null ? s.Holders : [plan.Holder])
            {
                sb.Append(CultureInfo.InvariantCulture, $"\n{s.Model.Entity(h)!.Name} ({h})\n");
                var beliefs = s.BeliefsBefore(h, key);
                var shown = beliefs.Where(kv => q.Prop is null || kv.Key == q.Prop).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                foreach (var (p, b) in shown)
                    // A label is a judgement against the truth; where the truth is unconfirmed, so is the label.
                    sb.Append(CultureInfo.InvariantCulture, $"  {p}  [{s.Classify(b, key)}] {Stand(b.Stance, b.Strength)}: {s.Model.Proposition(p)!.Text}  ({b.Id}{Source(s, b)}{Approved(s, b.Id)}){(b.Stance is Stance.HoldsTrue or Stance.HoldsFalse ? s.UnconfirmedMark(p, key) : "")}\n");

                // A secret is the author's declaration that someone never learns a thing. It is not a belief
                // record, so it is said here; otherwise the silence would read as "no record".
                var secret = new HashSet<string>(StringComparer.Ordinal);
                foreach (var x in s.Model.Secrets.Where(x => !x.KnownOnlyTo.Contains(h)))
                {
                    var kept = x.Props.Where(p => (q.Prop is null || p == q.Prop) && !beliefs.ContainsKey(p)).ToList();
                    secret.UnionWith(kept);
                    if (kept.Count > 0)
                        sb.Append(CultureInfo.InvariantCulture, $"  never learns {string.Join(", ", kept)}: secret {x.Id} keeps {(kept.Count == 1 ? "it" : "them")} to {string.Join(", ", x.KnownOnlyTo.Select(s.Short))}\n");
                }

                // Say what is absent. A missing row and an [unaware] row mean different things.
                var silent = s.Model.Propositions.Select(p => p.Id)
                    .Where(p => (q.Prop is null || p == q.Prop) && !beliefs.ContainsKey(p) && !secret.Contains(p)).ToList();
                if (silent.Count > 0)
                    sb.Append(q.Prop is not null
                        ? $"  {q.Prop}  no record. That is not the same as \"does not know\": nothing has been recorded either way.\n"
                        : $"  no record either way for: {string.Join(", ", silent)}\n");
            }
        if (plan.Reader)
        {
            sb.Append("\nThe reader has been given\n");
            var reader = s.ReaderBefore(tell).Where(kv => q.Prop is null || kv.Key == q.Prop)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (reader.Count == 0)
                sb.Append(q.Prop is null ? "  nothing yet\n" : $"  {q.Prop}  nothing yet\n");
            foreach (var (p, d) in reader)
            {
                // The row shows the latest disclosure. Where the truth was first stated may be an earlier scene.
                var first = s.RevealScene(p);
                var stated = first is null || s.Tell(first) >= tell ? "; truth not yet stated to the reader"
                    : first == d.Scene ? "" : $"; truth first stated in {first}";
                sb.Append(CultureInfo.InvariantCulture, $"  {p}  [{Names.Of(d.Mode)}, {d.Scene}] {Stand(d.Stance, d.Strength)}: {s.Model.Proposition(p)!.Text}  ({d.Id}{stated}{Approved(s, d.Id)})\n");
            }
        }
        if (plan.World)
        {
            sb.Append("\nWhere things are kept\n");
            foreach (var o in q.Object is null ? s.TrackedObjects : [q.Object])
            {
                var l = s.Location(o, key);
                var name = s.Model.Entity(o)?.Name ?? o;
                name += $" ({o})";
                sb.Append(l is null ? $"  {name}: no location recorded at this point\n"
                    : l.Unresolved ? $"  {name}: unresolved; records {string.Join(" and ", l.Conflict!)} share a story time and disagree\n"
                    : $"  {name}: {s.Model.Entity(l.Place!)?.Name ?? l.Place}  ({l.Id}; since {Where(s, l.Scene, l.StoryKey)}{Approved(s, l.Id)})\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every change to one fact across the whole story: for each person in story order, and for the reader in
    /// the order of the telling. This answers "when did she first learn it" without comparing snapshots.
    /// </summary>
    private static string History(StoryState s, StateQuery q)
    {
        var prop = q.Prop!;
        var holder = q.Holder is null ? null : s.Model.Base(q.Holder);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HISTORY OF {prop} · ledger head {s.Model.Head}\n{s.Model.Proposition(prop)!.Text}\nCanon: {s.Canon(prop)}.{s.UnconfirmedMark(prop)}\n");

        // When the fact itself holds. Without this a belief that was right and has been overtaken cannot be told
        // from one that is still right: each row below is judged against the truth at the time it was acquired.
        var facts = s.Model.Facts(prop).OrderBy(f => f.StoryKey ?? double.NegativeInfinity).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        if (facts.Count > 0 && s.Canon(prop) is "true" or "false")
            sb.Append(CultureInfo.InvariantCulture, $"In story time: {string.Join("; ", facts.Select(f => string.Create(CultureInfo.InvariantCulture, $"{(f.Value ? "true" : "false")} from {(f.StoryKey is { } k ? $"story time {k}" : "the outset")}")))}. Each row is judged against the truth when it was acquired.\n");
        foreach (var h in holder is null ? s.Holders : [holder])
        {
            var changes = s.Model.BeliefsOf(h, prop)
                .OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"\n{s.Model.Entity(h)!.Name} ({h})\n");
            foreach (var b in changes)
            {
                var view = new BeliefView(b.Id, b.Holder, b.Prop, b.Stance, b.Strength, b.StoryKey, b.Scene, b.Channel, b.Via);
                sb.Append(CultureInfo.InvariantCulture, $"  {Where(s, b.Scene, b.StoryKey),-22}  story time {b.StoryKey,-7}  [{s.Classify(view)}] {Stand(b.Stance, b.Strength)}  ({b.Id}{Source(s, view)}{Approved(s, b.Id)})\n");
            }
            if (changes.Count == 0)
                sb.Append(s.Sealed(h, prop) is { } secret
                    ? $"  never learns it: secret {secret.Id} keeps it to {string.Join(", ", secret.KnownOnlyTo.Select(s.Short))}\n"
                    : "  no record at any point. That is not the same as \"never learns it\": nothing has been recorded either way.\n");
        }
        if (holder is null)
        {
            sb.Append("\nThe reader, in the order of the telling\n");
            var told = s.Model.DisclosuresOf(prop).Where(d => s.Tell(d.Scene) is not null)
                .OrderBy(d => s.Tell(d.Scene)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
            var first = s.RevealScene(prop);
            foreach (var d in told)
                sb.Append(CultureInfo.InvariantCulture, $"  {d.Scene,-22}  number {s.Tell(d.Scene),-2} in the telling  [{Names.Of(d.Mode)}] {Stand(d.ReaderStance, d.Strength)}  ({d.Id}{Approved(s, d.Id)}){(d.Scene == first && d.Mode == DisclosureMode.Stated ? "  <- the truth is first stated here" : "")}\n");
            if (told.Count == 0)
                sb.Append("  the reader is never given anything about it\n");
            else if (first is null && s.Canon(prop) is "true" or "false")
                sb.Append("  the truth is never stated to the reader\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every recorded move of one object in story order, with the event that moved it and who acted in it.
    /// This answers "when did it get there, and who put it there" without comparing snapshots.
    /// </summary>
    private static string Moves(StoryState s, StateQuery q)
    {
        var id = q.Object!;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HISTORY OF {id} · ledger head {s.Model.Head}\n{s.Model.Entity(id)!.Name}: where it is kept, in story order\n\n");
        var moves = s.Model.PlacementsOf(id)
            .OrderBy(p => p.StoryKey).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        foreach (var p in moves)
        {
            var cause = p.Cause;
            if (s.Model.Events.TryGetValue(p.Cause, out var e))
            {
                // The actors are named as the event records them: a persona stays a persona.
                var actors = e.Participants.Where(kv => kv.Value == ParticipantRole.Actor).Select(kv => s.Short(kv.Key)).ToList();
                cause = $"{(actors.Count > 0 ? $"by {string.Join(" and ", actors)}; " : "")}{p.Cause}: {e.Summary}";
            }
            var ties = moves.Where(o => o.StoryKey == p.StoryKey && o.Place != p.Place).Select(o => o.Id).ToList();
            var tie = ties.Count > 0 ? $"  ORDER NOT ESTABLISHED: shares its story time with {string.Join(", ", ties)} and disagrees" : "";
            sb.Append(CultureInfo.InvariantCulture, $"  {Where(s, p.Scene, p.StoryKey),-22}  story time {p.StoryKey,-7}  {s.Model.Entity(p.Place)?.Name ?? p.Place}  ({p.Id}; {cause}{Approved(s, p.Id)}){tie}\n");
        }
        if (moves.Count == 0)
            sb.Append("  no location recorded at any point\n");
        return sb.ToString();
    }

    /// <summary>
    /// Where a record happens: its scene, or a plain statement that no scene shows it. "Before the story opens"
    /// is said only of what precedes every scene in story time.
    /// </summary>
    private static string Where(StoryState s, string? scene, double key) =>
        scene ?? (s.Model.SceneTimes.Count == 0 || key < s.Model.SceneTimes.Values.Min(t => t.StoryKey) ? "before the story opens" : "off the page");

    public static JsonObject Json(StoryState s, StateQuery q)
    {
        var plan = Resolve(s, q);
        var point = StateDump.Point(s, plan.Key, plan.Tell);
        var doc = new JsonObject { ["head"] = s.Model.Head, ["at"] = q.End ? "end" : q.Scene };
        if (plan.People)
        {
            var beliefs = new JsonObject();
            foreach (var (h, value) in point["beliefs"]!.AsObject())
                if (plan.Holder is null || h == plan.Holder)
                    beliefs[h] = Only(value!.AsObject(), q.Prop);
            doc["beliefs"] = beliefs;
        }
        if (plan.Reader)
            doc["reader"] = Only(point["reader"]!.AsObject(), q.Prop);
        if (plan.World)
            doc["world"] = Only(point["world"]!.AsObject(), q.Object);
        return doc;
    }

    private static JsonObject Only(JsonObject items, string? key)
    {
        var result = new JsonObject();
        foreach (var (k, value) in items)
            if (key is null || k == key)
                result[k] = value?.DeepClone();
        return result;
    }

    /// <summary>How and where a belief was acquired, and from whom when it came from something said or written.</summary>
    private static string Source(StoryState s, BeliefView b)
    {
        if (b.Unresolved || b.Channel is not { } channel)
            return "";
        var how = Names.Of(channel).Replace('_', ' ');
        if (b.Via is { } via && s.Model.Assertions.TryGetValue(via, out var said))
        {
            var who = s.Short(s.Model.Base(said.Speaker));
            how = (channel, said.Type) switch
            {
                (Channel.Read, _) => $"read, written by {who}",
                // A letter read aloud: the writer is not the one speaking.
                (_, AssertionKind.Document) => $"heard it read out, written by {who}",
                (Channel.Told, _) => $"told by {who}",
                (Channel.Overheard, _) => $"overheard from {who}",
                _ => $"{how} from what {who} said",
            };
        }
        return $"; {how}, {Where(s, b.Scene, b.StoryKey)}";
    }

    private static string Stand(Stance? stance, Strength? strength) =>
        stance is null ? "records tie and disagree"
        : stance.Value switch
        {
            // "holds false, believes" was read as "believes it". Say which way the person takes it.
            Stance.HoldsTrue => "takes it as true",
            Stance.HoldsFalse => "takes it as false",
            _ => Names.Of(stance.Value),
        } + (strength is { } st ? ", " + Names.Of(st) : "");

    /// <summary>Anything not yet ratified is labelled, so nobody builds on it by accident.</summary>
    private static string Approved(StoryState s, string id) =>
        s.Model.ById.TryGetValue(id, out var t) && t.ApprovalAt(s.Model.Head) == Approval.Provisional ? ", PROVISIONAL" : "";
}
