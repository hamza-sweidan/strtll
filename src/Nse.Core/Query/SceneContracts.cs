using System.Globalization;
using Nse.Model;

namespace Nse.Query;

/// <summary>A change a scene is recorded as making, in the shape a contract plans changes in.</summary>
public sealed record RecordedChange(string RecordId, Intent Shape, string Text);

/// <summary>
/// A way a scene and its contract do not line up. None of these stops a write: the records say what the text
/// does, and the difference from what was meant is the author's to settle, by changing the scene or the contract.
/// </summary>
public sealed record ContractFinding(string Code, string? RecordId, string Message)
{
    public override string ToString() => RecordId is null ? $"{Code}: {Message}" : $"{Code} {RecordId}: {Message}";
}

/// <summary>
/// The scene contract is the desired state; the records attached to the scene are the actual one. This is
/// the comparison between the two. It is deterministic and it sees only what has been recorded: a change the
/// text makes and nobody declared is invisible here, which is what the audit of the prose is for.
/// </summary>
public static class SceneContracts
{
    public static SceneContract? Contract(this StoryState s, string sceneId) => s.Model.Contracts.GetValueOrDefault(sceneId);

    /// <summary>
    /// What a scene is recorded as doing: the belief changes, disclosures and moves attached to it, and the
    /// fact changes that fall within its span of story time.
    /// </summary>
    public static IReadOnlyList<RecordedChange> RecordedChanges(this StoryState s, string sceneId)
    {
        var (key, end) = (s.Key(sceneId), s.SceneEnd(sceneId));
        var result = new List<RecordedChange>();
        foreach (var b in s.Model.BeliefsIn(sceneId).OrderBy(b => b.StoryKey).ThenBy(b => b.Id, StringComparer.Ordinal))
            result.Add(Recorded(s, b.Id, new Intent { Change = IntentKind.Belief, Holder = b.Holder, Prop = b.Prop, Stance = b.Stance, Strength = b.Strength }));
        foreach (var d in s.Model.DisclosuresIn(sceneId).OrderBy(d => d.Id, StringComparer.Ordinal))
            result.Add(Recorded(s, d.Id, new Intent { Change = IntentKind.Disclosure, Prop = d.Prop, Stance = d.ReaderStance, Strength = d.Strength, Mode = d.Mode }));
        foreach (var p in s.Model.PlacementsIn(sceneId).OrderBy(p => p.StoryKey).ThenBy(p => p.Id, StringComparer.Ordinal))
            result.Add(Recorded(s, p.Id, new Intent { Change = IntentKind.Placement, Object = p.Object, Place = p.Place }));
        foreach (var prop in s.Model.Propositions)
        {
            // A fact change names no scene: it belongs to the scene whose span its story time falls in. Few fall
            // in any one scene, so each fact's changes are looked through first and only those found are sorted.
            List<FactChange>? within = null;
            foreach (var f in s.Model.Facts(prop.Id))
                if (f.StoryKey >= key && f.StoryKey < end)
                    (within ??= []).Add(f);
            if (within is null)
                continue;
            foreach (var f in within.OrderBy(f => f.StoryKey).ThenBy(f => f.Id, StringComparer.Ordinal))
                result.Add(Recorded(s, f.Id, new Intent { Change = IntentKind.Fact, Prop = f.Prop, Value = f.Value }));
        }
        return result;
    }

    private static RecordedChange Recorded(StoryState s, string id, Intent shape) => new(id, shape, Describe(s, shape));

    /// <summary>
    /// Where a scene and its contract differ: intended changes no record makes, recorded changes the contract
    /// did not plan, and things the contract relies on that are not so on entry. Empty when the scene has no
    /// contract, which is itself worth knowing and is said by the caller.
    /// </summary>
    public static IReadOnlyList<ContractFinding> CheckContract(this StoryState s, string sceneId)
    {
        if (s.Contract(sceneId) is not { } contract)
            return [];
        var done = s.RecordedChanges(sceneId);
        var findings = new List<ContractFinding>();
        foreach (var want in contract.Intends)
            if (!done.Any(d => Covers(s, want, d.Shape)))
                findings.Add(new ContractFinding("CONTRACT_MISSING", contract.Id,
                    $"the contract for {sceneId} intends that {Describe(s, want)}, and no record in the scene does that. Either the scene does not do it, or it was not declared"));
        foreach (var d in done.Where(d => WorthPlanning(s, d.Shape)))
            if (!contract.Intends.Any(want => Covers(s, want, d.Shape)))
                findings.Add(new ContractFinding("CONTRACT_UNPLANNED", d.RecordId,
                    $"in {sceneId}, {d.Text}, and contract {contract.Id} does not plan it. Change the scene, or have the author add it to the contract"));
        foreach (var (need, status) in Preconditions(s, sceneId, contract))
            if (status.Code is { } code)
                findings.Add(new ContractFinding(code, contract.Id, $"the contract for {sceneId} relies on this on entry: {Describe(s, need, onEntry: true)}. {status.Detail}"));
        return findings;
    }

    /// <summary>Each thing a contract relies on, with whether it is so on entry to the scene.</summary>
    public static IReadOnlyList<(Intent Need, (string? Code, string Detail) Status)> Preconditions(StoryState s, string sceneId, SceneContract contract)
    {
        var key = s.Key(sceneId);
        var tell = s.Tell(sceneId);
        return contract.Relies.Select(need => (need, Holds(s, need, key, tell))).ToList();
    }

    private static (string? Code, string Detail) Holds(StoryState s, Intent need, double key, int? tell)
    {
        const string Failed = "PRECONDITION_FAILED", Unknown = "PRECONDITION_NOT_ESTABLISHED";
        switch (need.Change)
        {
            case IntentKind.Belief:
                var holder = s.Model.Base(need.Holder!);
                var belief = s.BeliefBefore(holder, need.Prop!, key);
                if (belief is null)
                    // A secret that shuts the person out is as good as a record that they do not know.
                    return need.Stance == Stance.Unaware && s.Sealed(holder, need.Prop!) is { } secret
                        ? (null, $"It is so: secret {secret.Id} keeps it from them.")
                        : (Unknown, "Nothing is recorded either way, which is not the same as its being so.");
                if (belief.Unresolved)
                    return (Unknown, $"Their records {string.Join(" and ", belief.Conflict!)} share a story time and disagree.");
                var same = (need.Stance is null || need.Stance == belief.Stance) && (need.Strength is null || need.Strength == belief.Strength);
                return same ? (null, $"It is so ({belief.Id}).") : (Failed, $"The records say otherwise: [{s.Classify(belief, key)}] ({belief.Id}).");
            case IntentKind.Placement:
                var at = s.Location(need.Object!, key);
                if (at is null)
                    return (Unknown, "No location is recorded for it at that point.");
                if (at.Unresolved)
                    return (Unknown, $"Records {string.Join(" and ", at.Conflict!)} share a story time and disagree.");
                return need.Place is null || need.Place == at.Place
                    ? (null, $"It is so ({at.Id}).")
                    : (Failed, $"The records put it at {s.Model.Entity(at.Place!)?.Name ?? at.Place} ({at.Id}).");
            case IntentKind.Fact:
                if (s.FactTie(need.Prop!, key, before: true) is { } tie)
                    return (Unknown, $"Fact changes {string.Join(" and ", tie)} share a story time and disagree.");
                var truth = s.TruthBefore(need.Prop!, key);
                if (truth is null)
                    return (Unknown, $"Canon does not decide it ({s.Canon(need.Prop!)}).");
                return need.Value is null || need.Value == truth ? (null, "It is so.") : (Failed, $"On entry it is {(truth.Value ? "the case" : "not the case")}.");
            default:
                var given = tell is int t ? s.ReaderBefore(t).GetValueOrDefault(need.Prop!) : null;
                if (given is null)
                    return (Failed, "The reader has been given nothing about it by then.");
                if (given.Unresolved)
                    return (Unknown, $"Disclosures {string.Join(" and ", given.Conflict!)} share a scene and disagree.");
                var matches = (need.Stance is null || need.Stance == given.Stance) && (need.Strength is null || need.Strength == given.Strength)
                              && (need.Mode is null || need.Mode == given.Mode);
                return matches ? (null, $"It is so ({given.Id}).") : (Failed, $"The reader's latest is [{Names.Of(given.Mode)}, {given.Scene}] ({given.Id}).");
        }
    }

    /// <summary>Whether a recorded change is one of the changes an intent asks for. A field the intent leaves out matches anything.</summary>
    private static bool Covers(StoryState s, Intent want, Intent got) =>
        want.Change == got.Change
        && (want.Holder is null || s.Model.Base(want.Holder) == got.Holder)
        && (want.Prop is null || want.Prop == got.Prop)
        && (want.Stance is null || want.Stance == got.Stance)
        && (want.Strength is null || want.Strength == got.Strength)
        && (want.Mode is null || want.Mode == got.Mode)
        && (want.Object is null || want.Object == got.Object)
        && (want.Place is null || want.Place == got.Place)
        && (want.Value is null || want.Value == got.Value);

    /// <summary>
    /// Whether an unplanned change is worth reporting. A scene makes many small changes nobody plans; the ones
    /// a contract should have named are those that touch a guarded fact or move a tracked object: someone
    /// coming to hold a view of such a fact, the reader being given it outright, the fact itself changing.
    /// </summary>
    private static bool WorthPlanning(StoryState s, Intent got) => got.Change switch
    {
        IntentKind.Belief => got.Stance is Stance.HoldsTrue or Stance.HoldsFalse && s.Guarded(got.Prop!),
        IntentKind.Disclosure => (got.Mode == DisclosureMode.Stated || got.Strength == Strength.Certain) && s.Guarded(got.Prop!),
        IntentKind.Fact => s.Guarded(got.Prop!),
        _ => true,
    };

    /// <summary>An intent in words, as a change the scene makes or, with <paramref name="onEntry"/>, as something already so.</summary>
    public static string Describe(StoryState s, Intent i, bool onEntry = false)
    {
        string Named(string? id) => id is null ? "something" : $"{s.Model.Entity(id)?.Name ?? id} ({id})";
        string Way(Stance? stance) => stance switch
        {
            Stance.HoldsTrue => "as true", Stance.HoldsFalse => "as false", _ => "one way or the other",
        };
        var strength = i.Strength is { } st ? ", " + Names.Of(st) : "";
        switch (i.Change)
        {
            case IntentKind.Belief:
                var who = i.Holder is null ? "someone" : $"{s.Short(s.Model.Base(i.Holder))} ({s.Model.Base(i.Holder)})";
                return i.Stance switch
                {
                    Stance.Unaware => $"{who} is {(onEntry ? "" : "recorded as ")}unaware of {i.Prop}",
                    Stance.Undecided => $"{who} is {(onEntry ? "" : "recorded as ")}undecided about {i.Prop}",
                    _ => $"{who} {(onEntry ? "takes" : "comes to take")} {i.Prop} {Way(i.Stance)}{strength}",
                };
            case IntentKind.Disclosure:
                var mode = i.Mode is { } m ? $" [{Names.Of(m)}]" : "";
                var stance = i.Stance is null ? "" : $", and takes it {Way(i.Stance)}";
                return $"the reader {(onEntry ? "has been" : "is")} given {i.Prop}{mode}{stance}{strength}";
            case IntentKind.Placement:
                return i.Place is null
                    ? $"{Named(i.Object)} {(onEntry ? "has a recorded place" : "is moved")}"
                    : $"{Named(i.Object)} {(onEntry ? "is kept at" : "moves to")} {Named(i.Place)}";
            default:
                var value = i.Value switch { true => "true", false => "false", _ => "true or false" };
                return string.Create(CultureInfo.InvariantCulture, $"{i.Prop} {(onEntry ? "is" : "becomes")} {value}");
        }
    }
}
