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
/// a character's words on those words. An event rests on the facts it says it needs.
/// </summary>
/// <remarks>
/// <para>None of it is stored. No record says what it rests on: that is read off the same records every other
/// answer comes from, each time it is asked. So a scene that is rewritten, a record the author rejects and a
/// fact that is moved all show here without anyone having linked the later passage to the earlier one, and a
/// new record that gives a speaker the fact again is found the same way.</para>
/// <para>A record is in doubt for one of four reasons. What it rested on was in the story and has since been
/// rejected, retired or withdrawn, and nothing else on record carries it. Someone is in it after a fact that
/// ends their part in the story. It needs a fact that is not so when it happens. Or everything it rests on is
/// itself in doubt. Silence is none of these: a statement by someone with no record on the fact never had a
/// recorded footing, and "no record" is not a fault. People guess, lie and repeat gossip.</para>
/// <para>This decides nothing. A passage whose record is in doubt is put in front of the author with the
/// reason; whether to rewrite it, withdraw the record or put back what it rested on is theirs.</para>
/// </remarks>
public sealed class Standing
{
    /// <summary>What it rested on is gone from the story, and nothing else on record carries it.</summary>
    public const string LostSupport = "LOST_SUPPORT";

    /// <summary>Everything it rests on is itself in doubt.</summary>
    public const string InQuestion = "IN_QUESTION";

    /// <summary>Someone is in it after a fact that ends their part in the story.</summary>
    public const string AfterTheirEnd = "AFTER_THEIR_END";

    /// <summary>A fact it could not happen without is not so when it happens.</summary>
    public const string NeedNotMet = "NEED_NOT_MET";

    /// <summary>Not a finding: the record is taken not to stand, to see what would follow. See <see cref="Supposing"/>.</summary>
    public const string Supposed = "SUPPOSED";

    private readonly StoryState _s;
    private readonly IReadOnlySet<string>? _supposed;
    private readonly Dictionary<string, Doubt?> _judged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _judging = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IReadOnlyList<Ground> Live, IReadOnlyList<Ground> Gone)> _grounds = new(StringComparer.Ordinal);
    private ILookup<string, Tracked>? _goneOn;
    private Dictionary<string, Tracked>? _goneById;
    private Dictionary<string, List<Proposition>>? _enders;
    private IReadOnlyList<Doubt>? _all;

    private Standing(StoryState s, IReadOnlySet<string>? supposed = null) => (_s, _supposed) = (s, supposed);

    /// <summary>The standing of every record in a state. Worked out once for a state, which never changes.</summary>
    public static Standing Of(StoryState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.StandingOnce ??= new Standing(s);
    }

    /// <summary>
    /// The same, supposing some records cannot stand: what would be left in doubt if they went. It is how a
    /// possible consequence is followed through before anyone has decided that it is one.
    /// </summary>
    public static Standing Supposing(StoryState s, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Standing(s, ids.ToHashSet(StringComparer.Ordinal));
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

    /// <summary>
    /// The doubt over the view a person holds of a fact just before a point in story time. Null when they hold
    /// none, and when any record they hold it by stands: someone who learned a thing twice still has it when
    /// one of the two is in doubt.
    /// </summary>
    public Doubt? Held(string person, string prop, double before)
    {
        if (_s.BeliefBefore(person, prop, before) is not { Unresolved: false } held)
            return null;
        Doubt? first = null;
        foreach (var view in _s.Model.BeliefsOf(person, prop).Where(b => b.StoryKey < before)
                     .OrderByDescending(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            if (view.Stance != held.Stance)
                break;
            if (Of(view.Id) is not { } doubt)
                return null;
            first ??= doubt;
        }
        return first;
    }

    /// <summary>The doubt a doubt starts at: its own, or that of the last record it comes through.</summary>
    public Doubt Root(Doubt doubt)
    {
        ArgumentNullException.ThrowIfNull(doubt);
        return doubt.Through.Count == 0 ? doubt : Of(doubt.Through[^1]) ?? doubt;
    }

    private Doubt? Judge(string id)
    {
        if (!_s.Model.ById.TryGetValue(id, out var t))
            return null;
        if (_supposed?.Contains(id) == true)
            return new Doubt(id, Supposed, "taken not to stand, to see what would follow", []);
        if (Direct(t.Record) is { } own)
            return own;
        var (live, gone) = Grounds(id);
        if (live.Count == 0)
            return gone.Count == 0 ? null : new Doubt(id, LostSupport, LostWhy(id, gone), []);
        // It rests on something in force. It is in doubt only if all of that is: a speaker who also heard the
        // thing from someone else, or learned it twice, still has the other.
        Doubt? first = null;
        foreach (var ground in live)
        {
            if (Of(ground.Id) is not { } doubt)
                return null;
            first ??= doubt;
        }
        return new Doubt(id, InQuestion, RestsWhy(id, live), [first!.Id, .. first.Through]);
    }

    /// <summary>What is wrong with a record in itself, whatever it rests on.</summary>
    private Doubt? Direct(LedgerRecord record)
    {
        if (Enders.Count > 0 && Entry(_s, record) is double entry)
            foreach (var (who, does) in Present(record))
            {
                var person = _s.Model.Base(who);
                if (!Enders.TryGetValue(person, out var facts))
                    continue;
                // On entry to the scene it is recorded in: the scene in which someone dies still has them in it.
                foreach (var fact in facts.Where(f => _s.TruthBefore(f.Id, entry) == true))
                    return new Doubt(record.Id, AfterTheirEnd, string.Create(CultureInfo.InvariantCulture,
                        $"{Name(person)} {does}{(SceneOf(record) is { } scene ? $" in {scene}" : "")}, at story time {When(_s, record)}, and {fact.Id} (\"{Plain.OneLine(fact.Text)}\"), which ends {Name(person)}'s part in the story, is so {Since(fact.Id, entry)}"), []);
            }
        if (record is Event { Needs: { Count: > 0 } needs } happened)
            foreach (var need in needs)
            {
                if (_s.Model.Proposition(need) is not { } fact)
                    continue;
                var (value, tie) = _s.Truth(need, happened.StoryKey, before: false);
                if (value == true)
                    continue;
                var how = tie is not null ? $"not settled then: fact changes {string.Join(" and ", tie)} share a story time and disagree"
                    : value is null ? $"not decided ({_s.Canon(need)})"
                    : _s.Expired(need, happened.StoryKey) ? "no longer so by then" : "not so by then";
                return new Doubt(record.Id, NeedNotMet, string.Create(CultureInfo.InvariantCulture,
                    $"it happens at story time {happened.StoryKey} and needs {need} (\"{Plain.OneLine(fact.Text)}\"), which is {how}"), []);
            }
        return null;
    }

    /// <summary>The facts that end someone's part in the story, by the person.</summary>
    private Dictionary<string, List<Proposition>> Enders
    {
        get
        {
            if (_enders is not null)
                return _enders;
            _enders = new Dictionary<string, List<Proposition>>(StringComparer.Ordinal);
            foreach (var fact in _s.Model.Propositions.Where(p => p.Ends is { Count: > 0 }))
                foreach (var who in fact.Ends!.Select(_s.Model.Base).Distinct(StringComparer.Ordinal))
                {
                    if (!_enders.TryGetValue(who, out var facts))
                        _enders[who] = facts = [];
                    facts.Add(fact);
                }
            return _enders;
        }
    }

    /// <summary>From when a fact has been so, as of a point in story time: the latest change before it that made it so.</summary>
    private string Since(string prop, double before)
    {
        var from = _s.Model.Facts(prop).Where(f => f.Value && (f.StoryKey ?? double.NegativeInfinity) < before)
            .Select(f => f.StoryKey ?? double.NegativeInfinity).DefaultIfEmpty(double.NegativeInfinity).Max();
        return double.IsNegativeInfinity(from) ? "from the outset" : string.Create(CultureInfo.InvariantCulture, $"from story time {from}");
    }

    /// <summary>
    /// The people a record has there, each with what they do in it. The writer of a document need not be there
    /// when it is read, and narration belongs to whoever it is told through.
    /// </summary>
    internal static IEnumerable<(string Who, string Does)> Present(LedgerRecord record)
    {
        switch (record)
        {
            case Event e:
                foreach (var (who, role) in e.Participants)
                    yield return (who, role switch
                    {
                        ParticipantRole.Actor => "acts in it", ParticipantRole.Witness => "is there to see it", _ => "is spoken to in it",
                    });
                break;
            case Assertion a:
                if (a.Type is AssertionKind.Dialogue or AssertionKind.Thought)
                    yield return (a.Speaker, "says it");
                else if (a is { Type: AssertionKind.Narration, Focalizer: { } through })
                    yield return (through, "is the one it is told through");
                foreach (var who in a.Addressees)
                    yield return (who, "is told it");
                foreach (var who in a.Overhearers)
                    yield return (who, "overhears it");
                if (a.ReadBy is { } reader)
                    yield return (reader, "reads it");
                break;
            case BeliefChange b:
                yield return (b.Holder, "comes to a view");
                break;
        }
    }

    /// <summary>The point in story time a record is judged on entry to: its scene's, or failing a scene its own.</summary>
    internal static double? Entry(StoryState s, LedgerRecord record)
    {
        if (SceneOf(record) is { } scene && s.Model.SceneTimes.TryGetValue(scene, out var time))
            return time.StoryKey;
        return record switch
        {
            Event e => e.StoryKey, Assertion a => a.StoryKey, BeliefChange b => b.StoryKey, Placement p => p.StoryKey, _ => null,
        };
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
                Event { Needs: { Count: > 0 } } e => (Needed(e), []),
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

    /// <summary>What makes each fact an event needs so at the moment it happens: the change that last made it so.</summary>
    private List<Ground> Needed(Event e)
    {
        var grounds = new List<Ground>();
        foreach (var need in e.Needs!)
        {
            if (_s.Model.Proposition(need) is null || _s.TruthAt(need, e.StoryKey) != true)
                continue;
            var by = _s.Model.Facts(need).Where(f => f.Value && (f.StoryKey ?? double.NegativeInfinity) <= e.StoryKey)
                .OrderBy(f => f.StoryKey ?? double.NegativeInfinity).ThenBy(f => f.Id, StringComparer.Ordinal).LastOrDefault();
            if (by is not null)
                grounds.Add(new Ground(by.Id, string.Create(CultureInfo.InvariantCulture,
                    $"makes {need} so {(by.StoryKey is double k ? $"from story time {k}" : "from the outset")}")));
        }
        return grounds;
    }

    /// <summary>
    /// What a speaker had on a fact when they said it. The view they held by then is what they speak from,
    /// whether they say it straight or deny it: every record since they last held otherwise that says the same,
    /// so that someone who learned a thing twice still has it when one of the two is gone. With no view on
    /// record, the words they had heard: someone who was in the room can repeat a thing without vouching for
    /// it. A document is left out: it was written at some other time than it is read, and nothing says when.
    /// </summary>
    private (IReadOnlyList<Ground>, IReadOnlyList<Ground>) Footing(Assertion a, int recorded)
    {
        var who = a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker;
        if (a.Type == AssertionKind.Document || _s.Model.Entity(who) is null)
            return ([], []);
        var person = _s.Model.Base(who);
        var live = new List<Ground>();
        if (_s.BeliefBefore(person, a.Prop, a.StoryKey, inclusive: true) is { } held)
        {
            if (held.Unresolved)
                live.AddRange(held.Conflict!.Select(id => new Ground(id, Came(_s.Model.ById[id].Record))));
            else
                foreach (var view in _s.Model.BeliefsOf(person, a.Prop).Where(b => b.StoryKey <= a.StoryKey)
                             .OrderByDescending(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal))
                {
                    if (view.Stance != held.Stance)
                        break;
                    live.Add(new Ground(view.Id, Came(view)));
                }
        }
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

    private string Speaker(Assertion a) => Name(a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker);

    private string LostWhy(string id, IReadOnlyList<Ground> gone)
    {
        switch (_s.Model.ById[id].Record)
        {
            case Assertion a:
                return string.Create(CultureInfo.InvariantCulture,
                    $"{Speaker(a)} had {a.Prop} from {Listed(gone)}. Nothing else on record gives it to {Speaker(a)} by story time {a.StoryKey}");
            case Disclosure d:
                return $"the reader was given {d.Prop} by words recorded as {Listed(gone)}. No statement of it is recorded in {d.Scene} now";
            default:
                return $"it names {Listed(gone)}";
        }
    }

    private string RestsWhy(string id, IReadOnlyList<Ground> live) => _s.Model.ById[id].Record switch
    {
        Assertion a => $"all {Speaker(a)} has for {a.Prop} is {Listed(live)}",
        Disclosure d => $"the reader is given {d.Prop} by {Listed(live)}",
        _ => $"it rests on {Listed(live)}",
    };

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

    /// <summary>The fact a record is about, if it is about one.</summary>
    public static string? FactOf(LedgerRecord record) => record switch
    {
        Assertion a => a.Prop,
        BeliefChange b => b.Prop,
        Disclosure d => d.Prop,
        FactChange f => f.Prop,
        _ => null,
    };

    // ------------------------------------------------------------------ in words

    /// <summary>What a code means, as it is printed in front of the reason.</summary>
    public static string Label(string code) => code switch
    {
        LostSupport => "LOST ITS SUPPORT",
        InQuestion => "IN QUESTION",
        AfterTheirEnd => "CANNOT HAPPEN AS RECORDED",
        NeedNotMet => "WHAT IT NEEDS IS NOT SO",
        Supposed => "SUPPOSING IT CANNOT STAND",
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

    /// <summary>
    /// The same reason where the far end is said elsewhere on the page: the record's own, and the records the
    /// doubt comes through. A list of many records that all trace back to one says that one's reason once.
    /// </summary>
    public static string Short(Doubt doubt)
    {
        ArgumentNullException.ThrowIfNull(doubt);
        return doubt.Through.Count == 0
            ? $"{Label(doubt.Code)}: {doubt.Why}."
            : $"{Label(doubt.Code)}: {doubt.Why}. It traces back through {string.Join(" -> ", doubt.Through)}.";
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
    /// Records in doubt as the author is shown them, gathered by where each doubt starts. The record the trouble
    /// starts at comes first, in full, because that is the one to act on. What rests on it follows, each with
    /// its passage. A statement in doubt is followed by anything else on record that might be what it rests on.
    /// </summary>
    public IReadOnlyList<string> Grouped(IReadOnlyList<Doubt> doubts, string indent = "  ")
    {
        ArgumentNullException.ThrowIfNull(doubts);
        var lines = new List<string>();
        LedgerRecord Record(string id) => _s.Model.ById[id].Record;
        foreach (var group in doubts.GroupBy(d => Root(d).Id, StringComparer.Ordinal)
                     .OrderBy(g => When(_s, Record(g.Key))).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var root = Of(group.Key) ?? group.First();
            var record = Record(root.Id);
            lines.Add($"{indent}{root.Id} · {Where(_s, record)}");
            lines.Add($"{indent}  {RecordText.Line(_s, record)}");
            if (FactOf(record) is { } prop && _s.Model.Proposition(prop) is { } fact)
                lines.Add($"{indent}  the fact: {prop}: {Plain.OneLine(fact.Text)}");
            lines.Add($"{indent}  {Label(root.Code)}: {Plain.OneLine(root.Why)}.");
            lines.AddRange(Possible(record).Select(x => $"{indent}  {x.Text}"));
            var rest = group.Where(d => d.Id != root.Id).ToList();
            if (rest.Count == 0)
                continue;
            lines.Add($"{indent}  and resting on it:");
            foreach (var doubt in rest)
            {
                var resting = Record(doubt.Id);
                lines.Add($"{indent}    {doubt.Id} · {Where(_s, resting)}");
                lines.Add($"{indent}      {RecordText.Line(_s, resting)}");
                lines.Add($"{indent}      {Plain.OneLine(doubt.Why)}.");
                lines.AddRange(Possible(resting).Select(x => $"{indent}      {x.Text}"));
            }
        }
        return lines;
    }

    /// <summary>
    /// What else on record a statement in doubt might rest on: another fact its speaker holds by then, about
    /// the same people and things. Two propositions can be one fact written down twice, by two sessions that
    /// never saw each other's wording, and only the author can say whether these are. It is a suggestion.
    /// </summary>
    /// <returns>Each suggestion in words, with the record of the speaker's view that it points at.</returns>
    public IReadOnlyList<(string View, string Text)> Possible(LedgerRecord record)
    {
        if (record is not Assertion { Polarity: true, Type: not AssertionKind.Document } a || _s.Model.Proposition(a.Prop) is not { } claimed)
            return [];
        var about = claimed.About.Select(_s.Model.Base).ToHashSet(StringComparer.Ordinal);
        if (about.Count < 2)
            return [];
        var person = _s.Model.Base(a.Type == AssertionKind.Narration ? a.Focalizer ?? a.Speaker : a.Speaker);
        var found = new List<(int Shared, BeliefChange View, Proposition Fact)>();
        foreach (var views in _s.Model.BeliefsOf(person).Where(b => b.Prop != a.Prop && b.StoryKey <= a.StoryKey).GroupBy(b => b.Prop, StringComparer.Ordinal))
        {
            var latest = views.OrderByDescending(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal).First();
            if (latest.Stance != Stance.HoldsTrue || Of(latest.Id) is not null || _s.Model.Proposition(latest.Prop) is not { } other)
                continue;
            var shared = other.About.Select(_s.Model.Base).Count(about.Contains);
            if (shared >= 2)
                found.Add((shared, latest, other));
        }
        return found.OrderByDescending(x => x.Shared).ThenByDescending(x => x.View.StoryKey).ThenBy(x => x.View.Id, StringComparer.Ordinal).Take(3)
            .Select(x => (x.View.Id, string.Create(CultureInfo.InvariantCulture,
                $"POSSIBLE SUPPORT, for you to judge: {Name(person)} holds {x.Fact.Id} (\"{Plain.OneLine(x.Fact.Text)}\") from story time {x.View.StoryKey}: {x.View.Id} ({Came(x.View)}){(Evidence.Of(_s, x.View) is { } passage ? ", " + passage : "")}. It is about {string.Join(" and ", x.Fact.About.Select(_s.Model.Base).Where(about.Contains).Select(Name))}, as {a.Prop} is. If the two are one fact, record them under one proposition and this stands.")))
            .ToList();
    }

    /// <summary>
    /// What in a scene is borne out, and by what: each event and statement of the scene that rests on something
    /// and is not in doubt. It is the other half of what a revision needs, and what lets the engine say that a
    /// scene can still happen though one of its lines cannot stand.
    /// </summary>
    public IReadOnlyList<string> BorneOut(string sceneId)
    {
        var lines = new List<string>();
        var records = _s.Model.EventsIn(sceneId).OrderBy(e => e.StoryKey).ThenBy(e => e.Id, StringComparer.Ordinal).Cast<LedgerRecord>()
            .Concat(_s.Model.AssertionsIn(sceneId).OrderBy(a => a.StoryKey).ThenBy(a => a.Id, StringComparer.Ordinal));
        foreach (var record in records)
        {
            if (Of(record.Id) is not null)
                continue;
            var sound = RestsOn(record.Id).Where(g => Of(g.Id) is null).ToList();
            if (sound.Count == 0)
                continue;
            lines.Add($"{record.Id} · {Where(_s, record)} · {RecordText.Line(_s, record)}");
            lines.Add("  BORNE OUT: " + Plain.OneLine(record switch
            {
                Event => "what it needs is so: " + string.Join("; ", sound.Select(g => $"{g.Id} {g.How}{Made(g.Id)}")),
                Assertion a => $"{Speaker(a)} has {a.Prop} from {Listed(sound)}",
                _ => $"it rests on {Listed(sound)}",
            }) + ".");
        }
        return lines;
    }

    /// <summary>
    /// The passage that made a fact so, where one can be found: the event recorded at the same story time, in the
    /// scene whose span that time falls in. A fact change quotes nothing itself.
    /// </summary>
    private string Made(string factChange)
    {
        if (_s.Model.ById[factChange].Record is not FactChange { StoryKey: double key })
            return "";
        var scene = _s.Model.SceneTimes.Values.Where(t => _s.Model.Scenes.ContainsKey(t.Scene) && key >= t.StoryKey && key < _s.SceneEnd(t.Scene))
            .OrderBy(t => t.StoryKey).Select(t => t.Scene).FirstOrDefault();
        if (scene is null)
            return "";
        var by = _s.Model.EventsIn(scene).Where(e => e.StoryKey == key && e.Quote is not null).OrderBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
        return by is not null && Evidence.Of(_s, by) is { } passage ? $", by {by.Id}: {passage}" : $", which falls in {scene}";
    }

    /// <summary>Everything in doubt, by where each doubt starts, or what is in doubt in one scene.</summary>
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
        sb.Append("Each was borne out when it was recorded. Since then the story has changed under it: what it rested on was rejected, retired or withdrawn, a fact it needs is not so, or someone in it has left the story by then.\n");
        sb.Append("Nothing is decided here. For each: rewrite the passage and submit the scene again, withdraw the record, or put back what it rested on.\n");
        sb.Append(CultureInfo.InvariantCulture, $"\nIn doubt ({doubts.Count}), by the record the trouble starts at:\n");
        foreach (var line in standing.Grouped(doubts))
            sb.Append(line).Append('\n');
        var scenes = doubts.Select(d => SceneOf(s.Model.ById[d.Id].Record)).OfType<string>().Distinct(StringComparer.Ordinal)
            .OrderBy(id => s.Tell(id) ?? int.MaxValue).ThenBy(id => id, StringComparer.Ordinal).ToList();
        foreach (var scene in scenes)
        {
            var sound = standing.BorneOut(scene);
            if (sound.Count == 0)
                continue;
            sb.Append(CultureInfo.InvariantCulture, $"\nStill borne out in {scene}:\n");
            foreach (var line in sound)
                sb.Append("  ").Append(line).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// What a change does to what was already recorded: the records it leaves in doubt, the records in doubt that it
/// settles, and what it may have done that the records cannot say. Two states are compared, each read whole, so
/// it does not matter what kind of change lies between them: a decision, a retirement, a fact moved in time, a
/// new draft of a scene, or records that were only added.
/// </summary>
/// <param name="Raised">In doubt after the change, and not before. Each is a record of the state after.</param>
/// <param name="Settled">In doubt before the change, still in the story after it, and no longer in doubt.</param>
/// <param name="Possible">What may follow and is not certain, in words, for the author to judge.</param>
public sealed record Consequences(IReadOnlyList<Doubt> Raised, IReadOnlyList<Doubt> Settled, IReadOnlyList<string> Possible)
{
    public bool None => Raised.Count == 0 && Settled.Count == 0 && Possible.Count == 0;

    public static Consequences Between(StoryState before, StoryState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var (was, now) = (Standing.Of(before), Standing.Of(after));
        var raised = now.All.Where(d => was.Of(d.Id) is null).ToList();
        var settled = was.All.Where(d => after.Model.ById.ContainsKey(d.Id) && now.Of(d.Id) is null).ToList();
        return new Consequences(raised, settled, Presence(before, after, now));
    }

    /// <summary>
    /// A fact about a person that is so at another time than it was, set against what that person does in the
    /// stretch where it changed. Whether being dead, abroad or in prison stops someone handing over a key is not
    /// something a record says unless the fact is marked as ending their part in the story. So this is offered,
    /// with what would follow if it did, and decided by nobody but the author.
    /// </summary>
    private static List<string> Presence(StoryState before, StoryState after, Standing now)
    {
        var lines = new List<string>();
        static string Truth(bool? so) => so switch { true => "so", false => "not so", _ => "not settled" };
        foreach (var fact in after.Model.Propositions.Where(p => p.About.Count > 0 && p.Resolution == Resolution.Decided))
        {
            var changesNow = after.Model.Facts(fact.Id).Select(f => (Key: f.StoryKey ?? double.NegativeInfinity, f.Value)).Order().ToList();
            var changesWere = before.Model.Proposition(fact.Id) is null ? []
                : before.Model.Facts(fact.Id).Select(f => (Key: f.StoryKey ?? double.NegativeInfinity, f.Value)).Order().ToList();
            if (changesNow.SequenceEqual(changesWere))
                continue;
            // A fact about one person is a state of theirs: dead, abroad, blind, in prison. A fact about two is
            // something between them, and says nothing of whether either can be anywhere. Only the first is
            // worth setting against everything that person does.
            var ends = (fact.Ends ?? []).Select(after.Model.Base).ToHashSet(StringComparer.Ordinal);
            var people = fact.About.Select(after.Model.Base).Where(id => after.Model.Entity(id)?.Type == EntityKind.Character)
                .ToHashSet(StringComparer.Ordinal);
            if (people.Count != 1 || ends.Overlaps(people))
                continue;
            // What passages there are to read: events and statements. A view of theirs follows from one of those.
            var hits = new List<(LedgerRecord Record, string Who, bool? Was, bool? IsNow)>();
            foreach (var t in after.Model.Records.Where(t => t.Record is Event or Assertion))
            {
                if (Standing.Entry(after, t.Record) is not double entry || now.Of(t.Record.Id) is not null)
                    continue;
                var who = Standing.Present(t.Record).Select(x => after.Model.Base(x.Who)).FirstOrDefault(people.Contains);
                if (who is null)
                    continue;
                var (wasSo, isSo) = (before.Model.Proposition(fact.Id) is null ? null : before.TruthBefore(fact.Id, entry), after.TruthBefore(fact.Id, entry));
                if (wasSo != isSo)
                    hits.Add((t.Record, who, wasSo, isSo));
            }
            if (hits.Count == 0)
                continue;
            const int shown = 6;
            var names = string.Join(", ", hits.Select(h => after.Short(h.Who)).Distinct(StringComparer.Ordinal));
            lines.Add($"{fact.Id} (\"{Plain.OneLine(fact.Text)}\") was {Truth(hits[0].Was)} on entry to what follows and is now {Truth(hits[0].IsNow)}. It is about {names}, who is in {(hits.Count == 1 ? "it" : "each of them")}:");
            foreach (var hit in hits.Take(shown))
            {
                lines.Add($"  {hit.Record.Id} · {Standing.Where(after, hit.Record)}");
                lines.Add($"    {RecordText.Line(after, hit.Record)}");
            }
            if (hits.Count > shown)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"  and {hits.Count - shown} more"));
            var supposing = Standing.Supposing(after, hits.Select(h => h.Record.Id));
            var follow = supposing.All.Where(d => d.Code != Standing.Supposed && now.Of(d.Id) is null).ToList();
            if (follow.Count > 0)
                lines.Add($"  If {(hits.Count == 1 ? "that" : "those")} cannot stand, these are left without what they rest on: "
                          + string.Join("; ", follow.Select(d => $"{d.Id} ({Standing.Where(after, after.Model.ById[d.Id].Record)})")));
            lines.Add($"  The records do not say whether {fact.Id} stops {names} being there. If it ends their part in the story, put the proposition again with ends naming them, and this is followed through from then on.");
        }
        return lines;
    }

    /// <summary>The consequences in words, each record with where its passage is and why. Empty when there are none.</summary>
    public IReadOnlyList<string> Lines(StoryState after, string indent = "  ")
    {
        ArgumentNullException.ThrowIfNull(after);
        var lines = new List<string>();
        var standing = Standing.Of(after);
        if (Raised.Count > 0)
        {
            var starts = Raised.Select(d => standing.Root(d).Id).Distinct(StringComparer.Ordinal).Count();
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{indent}leaves {Raised.Count} record(s) of what is already written in doubt. The trouble starts at {starts}. Each passage needs a look:"));
            lines.AddRange(standing.Grouped(Raised, indent + "  "));
            foreach (var scene in Raised.Select(d => Standing.SceneOf(after.Model.ById[d.Id].Record)).OfType<string>().Distinct(StringComparer.Ordinal)
                         .OrderBy(id => after.Tell(id) ?? int.MaxValue).ThenBy(id => id, StringComparer.Ordinal))
            {
                var sound = standing.BorneOut(scene);
                if (sound.Count == 0)
                    continue;
                lines.Add($"{indent}still borne out in {scene}:");
                lines.AddRange(sound.Select(line => $"{indent}  {line}"));
            }
        }
        if (Settled.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{indent}gives {Settled.Count} record(s) that were in doubt something to rest on again:"));
            foreach (var doubt in Settled)
            {
                var record = after.Model.ById[doubt.Id].Record;
                lines.Add($"{indent}  {doubt.Id} · {Standing.Where(after, record)}");
                lines.Add($"{indent}    {RecordText.Line(after, record)}");
                var grounds = standing.RestsOn(doubt.Id).Where(g => standing.Of(g.Id) is null).ToList();
                lines.Add(grounds.Count == 0
                    ? $"{indent}    STANDS AGAIN: what put it in doubt is gone"
                    : $"{indent}    STANDS AGAIN on {string.Join("; ", grounds.Select(g => $"{g.Id} ({g.How})"))}: {RecordText.Line(after, after.Model.ById[grounds[0].Id].Record)}");
            }
        }
        if (Possible.Count > 0)
        {
            lines.Add($"{indent}may have done more, which the records cannot say. A suggestion, for you to judge:");
            lines.AddRange(Possible.Select(line => $"{indent}  {line}"));
        }
        return lines;
    }
}
