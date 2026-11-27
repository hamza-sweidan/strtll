using System.Globalization;
using System.Text;
using Nse.Ledger;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>One thing a record rests on.</summary>
/// <param name="Id">The record it rests on.</param>
/// <param name="How">What that record is, in a few words: how the view was come by, or who said the words and where.</param>
/// <param name="Went">Null while that record is in force. Otherwise how it left the story: rejected, or retired and why.</param>
public sealed record Ground(string Id, string How, string? Went = null);

/// <summary>A record that no longer stands as it was recorded.</summary>
/// <param name="Code">One of the codes <see cref="Standing"/> names.</param>
/// <param name="Why">The reason, in words, for this record alone.</param>
/// <param name="Through">The records between it and the one the doubt starts at, nearest first. Empty when it starts here.</param>
public sealed record Doubt(string Id, string Code, string Why, IReadOnlyList<string> Through);

/// <summary>
/// What each record of the story rests on, and whether that still holds. A statement rests on what its speaker
/// had on the fact when they said it: the view they held, or failing that the words they had heard. A belief
/// that names a source rests on the source, a move on the event that caused it, and what the reader is given by
/// a character's words on those words.
/// </summary>
/// <remarks>
/// <para>None of it is stored. No record says what it rests on: that is read off the same records every other
/// answer comes from, each time it is asked. So a scene that is rewritten, a record the author rejects and a
/// fact that is moved all show here without anyone having linked the later passage to the earlier one, and a
/// new record that gives a speaker the fact again is found the same way.</para>
/// <para>A record is in doubt in two ways. What it rested on was in the story and has since been rejected,
/// retired or withdrawn, and nothing else on record carries it. Or everything it rests on is itself in doubt.
/// Silence is neither: a statement by someone with no record on the fact never had a recorded footing, and
/// "no record" is not a fault. People guess, lie and repeat gossip.</para>
/// <para>This decides nothing. A passage whose record is in doubt is put in front of the author with the
/// reason; whether to rewrite it, withdraw the record or put back what it rested on is theirs.</para>
/// </remarks>
public sealed class Standing
{
    /// <summary>What it rested on is gone from the story, and nothing else on record carries it.</summary>
    public const string LostSupport = "LOST_SUPPORT";

    /// <summary>Everything it rests on is itself in doubt.</summary>
    public const string InQuestion = "IN_QUESTION";

    private readonly StoryState _s;
    private readonly Dictionary<string, Doubt?> _judged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _judging = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IReadOnlyList<Ground> Live, IReadOnlyList<Ground> Gone)> _grounds = new(StringComparer.Ordinal);
    private ILookup<string, Tracked>? _goneOn;
    private Dictionary<string, Tracked>? _goneById;
    private IReadOnlyList<Doubt>? _all;

    private Standing(StoryState s) => _s = s;

    /// <summary>The standing of every record in a state. Worked out once for a state, which never changes.</summary>
    public static Standing Of(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.StandingOnce ??= new Standing(s);
    }

    /// <summary>What a record rests on that is in force.</summary>
    public IReadOnlyList<Ground> RestsOn(string id) => Grounds(id).Live;

    /// <summary>What a record rested on that is gone. Looked for only when nothing in force carries the record.</summary>
    public IReadOnlyList<Ground> Lost(string id) => Grounds(id).Gone;

    /// <summary>Why a record does not stand as recorded, or null when it does.</summary>
    public Doubt? Of(string id)
    {
        if (_judged.TryGetValue(id, out var known))
            return known;
        // Records that rest on each other in a ring give a doubt nowhere to start from.
        if (!_judging.Add(id))
            return null;
        var doubt = Judge(id);
        _judging.Remove(id);
        return _judged[id] = doubt;
    }

    /// <summary>Every record in doubt, in the order of the story: by story time, then by id.</summary>
    public IReadOnlyList<Doubt> All => _all ??= _s.Model.Records.Select(t => Of(t.Record.Id)).OfType<Doubt>()
        .OrderBy(d => When(_s, _s.Model.ById[d.Id].Record)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

    /// <summary>The records of one scene that are in doubt.</summary>
    public IReadOnlyList<Doubt> In(string sceneId) => All.Where(d => SceneOf(_s.Model.ById[d.Id].Record) == sceneId).ToList();

    /// <summary>The record a doubt starts at: its own, or the last of those it comes through.</summary>
    public Doubt Root(Doubt doubt)
    {
        ArgumentNullException.ThrowIfNull(doubt);
        return doubt.Through.Count == 0 ? doubt : Of(doubt.Through[^1]) ?? doubt;
    }

    private Doubt? Judge(string id)
    {
        if (!_s.Model.ById.ContainsKey(id))
            return null;
        var (live, gone) = Grounds(id);
        if (live.Count == 0)
            return gone.Count == 0 ? null : new Doubt(id, LostSupport, LostWhy(id, gone), []);
        // It rests on something in force. It is in doubt only if all of that is: a speaker who also heard the
        // thing from someone else still has those words.
        Doubt? first = null;
        foreach (var ground in live)
        {
            if (Of(ground.Id) is not { } doubt)
                return null;
            first ??= doubt;
        }
        return new Doubt(id, InQuestion, RestsWhy(id, live), [first!.Id, .. first.Through]);
    }

    private (IReadOnlyList<Ground> Live, IReadOnlyList<Ground> Gone) Grounds(string id)
    {
        if (_grounds.TryGetValue(id, out var known))
            return known;
        return _grounds[id] = _s.Model.ById.TryGetValue(id, out var t)
            ? t.Record switch
            {
                Assertion a => Footing(a, t.CreatedSeq),
                BeliefChange { Via: { } via } => Named(via, "the source it came from"),
                Placement p => Named(p.Cause, "the event that caused it"),
                Disclosure { Mode: DisclosureMode.AssertedByCharacter } d => Words(d, t.CreatedSeq),
                _ => ([], []),
            }
            : ([], []);
    }

    /// <summary>A record another one names outright. If it is gone, that is how the naming record lost it.</summary>
    private (IReadOnlyList<Ground>, IReadOnlyList<Ground>) Named(string id, string how)
    {
        if (_s.Model.ById.ContainsKey(id))
            return ([new Ground(id, how)], []);
        return (_goneById ??= _s.Gone.ToDictionary(g => g.Record.Id, StringComparer.Ordinal)).TryGetValue(id, out var gone)
            ? ([], [new Ground(id, how, Went(gone))])
            : ([], []);
    }

    /// <summary>
    /// What a speaker had on a fact when they said it. The view they held by then is what they speak from,
    /// whether they say it straight or deny it. With no view on record, the words they had heard: someone who
    /// was in the room can repeat a thing without vouching for it. A document is left out: it was written at
    /// some other time than it is read, and nothing says when.
    /// </summary>
    private (IReadOnlyList<Ground>, IReadOnlyList<Ground>) Footing(Assertion a, int recorded)
    {
        var who = a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker;
        if (a.Type == AssertionKind.Document || _s.Model.Entity(who) is null)
            return ([], []);
        var person = _s.Model.Base(who);
        var live = new List<Ground>();
        if (_s.BeliefBefore(person, a.Prop, a.StoryKey, inclusive: true) is { } held)
            live.AddRange((held.Conflict ?? [held.Id]).Select(id => new Ground(id, Came(_s.Model.ById[id].Record))));
        else
            live.AddRange(_s.Model.HeardBy(person).Where(said => said.Prop == a.Prop && said.StoryKey <= a.StoryKey && said.Id != a.Id)
                .OrderByDescending(said => said.StoryKey).ThenBy(said => said.Id, StringComparer.Ordinal)
                .Select(said => new Ground(said.Id, Came(said))));
        if (live.Count > 0)
            return (live, []);
        // Nothing in force. What was there while this statement has been in the ledger, and is gone?
        var (views, heard) = (new List<(double Key, Ground Ground)>(), new List<(double Key, Ground Ground)>());
        foreach (var t in GoneOn(a.Prop))
        {
            if (Ended(t) is not int ended || ended <= recorded)
                continue;
            switch (t.Record)
            {
                case BeliefChange b when b.Holder == person && b.StoryKey <= a.StoryKey:
                    views.Add((b.StoryKey, new Ground(b.Id, Came(b), Went(t))));
                    break;
                case Assertion said when said.StoryKey <= a.StoryKey && said.Id != a.Id && _s.Model.Hearers(said).Contains(person):
                    heard.Add((said.StoryKey, new Ground(said.Id, Came(said), Went(t))));
                    break;
            }
        }
        // As with what is in force: a view they held is what they spoke from, and words they had only heard count
        // when there was no view.
        return ([], (views.Count > 0 ? views : heard).OrderByDescending(g => g.Key).ThenBy(g => g.Ground.Id, StringComparer.Ordinal).Select(g => g.Ground).ToList());
    }

    /// <summary>The words by which a character gives the reader a fact: what is said of it in the same scene.</summary>
    private (IReadOnlyList<Ground>, IReadOnlyList<Ground>) Words(Disclosure d, int recorded)
    {
        var live = _s.Model.AssertionsIn(d.Scene).Where(a => a.Prop == d.Prop)
            .OrderBy(a => a.StoryKey).ThenBy(a => a.Id, StringComparer.Ordinal).Select(a => new Ground(a.Id, Came(a))).ToList();
        if (live.Count > 0)
            return (live, []);
        var gone = GoneOn(d.Prop).Where(t => t.Record is Assertion a && a.Scene == d.Scene && Ended(t) is int ended && ended > recorded)
            .Select(t => new Ground(t.Record.Id, Came(t.Record), Went(t))).ToList();
        return ([], gone);
    }

    private IEnumerable<Tracked> GoneOn(string prop) =>
        (_goneOn ??= _s.Gone.Where(t => t.Record is BeliefChange or Assertion)
            .ToLookup(t => t.Record is BeliefChange b ? b.Prop : ((Assertion)t.Record).Prop, StringComparer.Ordinal))[prop];

    /// <summary>The change set a record stopped counting at, as of this state's head.</summary>
    private int? Ended(Tracked t)
    {
        var head = _s.Model.Head;
        int? retired = t.RetiredSeq is int r && r <= head ? r : null;
        int? rejected = t.DecidedApproval == Approval.Rejected && t.DecidedSeq is int d && d <= head ? d : null;
        return (retired, rejected) switch
        {
            (int r2, int d2) => Math.Min(r2, d2),
            _ => retired ?? rejected,
        };
    }

    private string Went(Tracked t)
    {
        var ended = Ended(t);
        return t.DecidedApproval == Approval.Rejected && t.DecidedSeq == ended
            ? string.Create(CultureInfo.InvariantCulture, $"rejected in change set {ended}")
            : string.Create(CultureInfo.InvariantCulture, $"retired in change set {ended}{(string.IsNullOrWhiteSpace(t.RetireReason) ? "" : $" ({Plain.OneLine(t.RetireReason)})")}");
    }

    /// <summary>How a view was come by, or who said some words and where: what a ground is, in a few words.</summary>
    private string Came(LedgerRecord record) => record switch
    {
        BeliefChange b => Names.Of(b.Channel).Replace('_', ' ') + (b.Via is null ? "" : " via " + b.Via),
        Assertion a => $"{(a.Polarity ? "said" : "denied")} by {Name(a.Speaker)} in {a.Scene}",
        _ => record.KindName,
    };

    private string Name(string id) => _s.Model.Entity(id) is null ? id : _s.Short(_s.Model.Base(id));

    private static string Listed(IEnumerable<Ground> grounds) =>
        string.Join("; ", grounds.Select(g => $"{g.Id} ({g.How}){(g.Went is null ? "" : ", " + g.Went)}"));

    private string LostWhy(string id, IReadOnlyList<Ground> gone)
    {
        switch (_s.Model.ById[id].Record)
        {
            case Assertion a:
                var name = Name(a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker);
                return string.Create(CultureInfo.InvariantCulture,
                    $"{name} had {a.Prop} from {Listed(gone)}. Nothing else on record gives it to {name} by story time {a.StoryKey}");
            case Disclosure d:
                return $"the reader was given {d.Prop} by words recorded as {Listed(gone)}. No statement of it is recorded in {d.Scene} now";
            default:
                return $"it names {Listed(gone)}";
        }
    }

    private string RestsWhy(string id, IReadOnlyList<Ground> live)
    {
        switch (_s.Model.ById[id].Record)
        {
            case Assertion a:
                var name = Name(a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker);
                return $"all {name} has for {a.Prop} is {Listed(live)}";
            case Disclosure d:
                return $"the reader is given {d.Prop} by {Listed(live)}";
            default:
                return $"it rests on {Listed(live)}";
        }
    }

    /// <summary>The scene a record of what happens belongs to, if it names one.</summary>
    public static string? SceneOf(LedgerRecord record) => record switch
    {
        Event e => e.Scene,
        Assertion a => a.Scene,
        BeliefChange b => b.Scene,
        Disclosure d => d.Scene,
        Placement p => p.Scene,
        _ => null,
    };

    /// <summary>Where a record falls in story time, for putting records in the order of the story.</summary>
    private static double When(StoryState s, LedgerRecord record) => record switch
    {
        Event e => e.StoryKey,
        Assertion a => a.StoryKey,
        BeliefChange b => b.StoryKey,
        Placement p => p.StoryKey,
        Disclosure d when s.Model.SceneTimes.TryGetValue(d.Scene, out var t) => t.StoryKey,
        _ => double.PositiveInfinity,
    };

    // ------------------------------------------------------------------ in words

    /// <summary>What a code means, as it is printed in front of the reason.</summary>
    public static string Label(string code) => code switch
    {
        LostSupport => "LOST ITS SUPPORT",
        InQuestion => "IN QUESTION",
        _ => code,
    };

    /// <summary>
    /// The reason a record is in doubt, on one line: its own, and when the doubt comes from further back, the
    /// records it comes through and the reason at the far end. That reason is the thing to act on.
    /// </summary>
    public string Reason(Doubt doubt)
    {
        ArgumentNullException.ThrowIfNull(doubt);
        if (doubt.Through.Count == 0)
            return $"{Label(doubt.Code)}: {doubt.Why}.";
        var root = Root(doubt);
        return $"{Label(doubt.Code)}: {doubt.Why}. That traces back through {string.Join(" -> ", doubt.Through)}, where the trouble starts: {Label(root.Code).ToLowerInvariant()}: {root.Why}.";
    }

    /// <summary>Where a record's words stand in the manuscript now, in a few words.</summary>
    public static string Where(StoryState s, LedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(s);
        var scene = SceneOf(record);
        return Evidence.Of(s, record) switch
        {
            { File: { } file } passage => string.Create(CultureInfo.InvariantCulture, $"{passage.Scene}, manuscript/{file} line {passage.Line}"),
            { } passage => $"{passage.Scene ?? scene ?? "no scene"}; its words are not in that scene's text as it stands",
            null => scene is null ? "it cites no passage" : $"{scene}; it cites no passage",
        };
    }

    /// <summary>
    /// One record in doubt as the author or a drafter is shown it: the record and where its words stand, the fact
    /// it is about, and the reason.
    /// </summary>
    public static IReadOnlyList<string> Block(StoryState s, Doubt doubt, string indent = "  ")
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(doubt);
        var record = s.Model.ById[doubt.Id].Record;
        var lines = new List<string>
        {
            $"{indent}{doubt.Id} · {Where(s, record)}",
            $"{indent}  {RecordText.Line(s, record)}",
        };
        if (FactOf(record) is { } prop && s.Model.Proposition(prop) is { } fact)
            lines.Add($"{indent}  the fact: {prop}: {Plain.OneLine(fact.Text)}");
        lines.Add($"{indent}  {Plain.OneLine(Of(s).Reason(doubt))}");
        return lines;
    }

    /// <summary>The fact a record is about, if it is about one.</summary>
    public static string? FactOf(LedgerRecord record) => record switch
    {
        Assertion a => a.Prop,
        BeliefChange b => b.Prop,
        Disclosure d => d.Prop,
        FactChange f => f.Prop,
        _ => null,
    };

    /// <summary>Everything in doubt, scene by scene in the order told, or the records of one scene.</summary>
    public static string Text(StoryState s, string? sceneId = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (sceneId is not null && !s.Model.Scenes.ContainsKey(sceneId))
            throw new KeyNotFoundException($"No scene record {sceneId}.");
        var standing = Of(s);
        var doubts = sceneId is null ? standing.All : standing.In(sceneId);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"ATTENTION · ledger head {s.Model.Head}{(sceneId is null ? "" : $" · {sceneId} alone")} · records that no longer stand as they were recorded\n");
        if (doubts.Count == 0)
        {
            sb.Append(sceneId is null
                ? "\nNothing needs attention: every record that rested on another still has it.\n"
                : $"\nNothing in {sceneId} needs attention: every record of it that rested on another still has it.\n");
            return sb.ToString();
        }
        sb.Append("Each was borne out when it was recorded. Since then what it rested on was rejected, retired or withdrawn, or is in doubt itself.\n");
        sb.Append("Nothing is decided here. For each: rewrite the passage and submit the scene again, withdraw the record, or put back what it rested on.\n");
        var groups = doubts.GroupBy(d => SceneOf(s.Model.ById[d.Id].Record))
            .OrderBy(g => g.Key is null ? int.MaxValue : s.Tell(g.Key) ?? int.MaxValue - 1).ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var title = group.Key is null ? "In no scene" : s.Tell(group.Key) is int n ? $"Scene {n}, {group.Key}" : $"{group.Key}, which is not in the manuscript";
            sb.Append(CultureInfo.InvariantCulture, $"\n{title} ({group.Count()}):\n");
            foreach (var doubt in group)
                foreach (var line in Block(s, doubt))
                    sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// What a change does to what was already recorded: the records it leaves in doubt, and the records in doubt
/// that it settles. Two states are compared, each read whole, so it does not matter what kind of change lies
/// between them: a decision, a retirement, a new draft of a scene, or records that were only added.
/// </summary>
/// <param name="Raised">In doubt after the change, and not before. Each is a record of the state after.</param>
/// <param name="Settled">In doubt before the change, still in the story after it, and no longer in doubt.</param>
public sealed record Consequences(IReadOnlyList<Doubt> Raised, IReadOnlyList<Doubt> Settled)
{
    public bool None => Raised.Count == 0 && Settled.Count == 0;

    public static Consequences Between(StoryState before, StoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var (was, now) = (Standing.Of(before), Standing.Of(after));
        var raised = now.All.Where(d => was.Of(d.Id) is null).ToList();
        var settled = was.All.Where(d => after.Model.ById.ContainsKey(d.Id) && now.Of(d.Id) is null).ToList();
        return new Consequences(raised, settled);
    }

    /// <summary>The consequences in words, each record with where its passage is and why. Empty when there are none.</summary>
    public IReadOnlyList<string> Lines(StoryState after, string indent = "  ")
    {
        ArgumentNullException.ThrowIfNull(after);
        var lines = new List<string>();
        if (Raised.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{indent}leaves {Raised.Count} record(s) of what is already written without what they rested on. Each passage needs a look:"));
            foreach (var doubt in Raised)
                lines.AddRange(Standing.Block(after, doubt, indent + "  "));
        }
        if (Settled.Count > 0)
        {
            var standing = Standing.Of(after);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{indent}gives {Settled.Count} record(s) that were in doubt something to rest on again:"));
            foreach (var doubt in Settled)
            {
                var record = after.Model.ById[doubt.Id].Record;
                lines.Add($"{indent}  {doubt.Id} · {Standing.Where(after, record)}");
                lines.Add($"{indent}    {RecordText.Line(after, record)}");
                var grounds = standing.RestsOn(doubt.Id);
                lines.Add(grounds.Count == 0
                    ? $"{indent}    STANDS AGAIN: what put it in doubt is gone"
                    : $"{indent}    STANDS AGAIN on {string.Join("; ", grounds.Select(g => $"{g.Id} ({g.How})"))}: {RecordText.Line(after, after.Model.ById[grounds[0].Id].Record)}");
            }
        }
        return lines;
    }
}
