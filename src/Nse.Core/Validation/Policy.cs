using System.Globalization;
using Nse.Ledger;
using Nse.Model;
using Nse.Query;

namespace Nse.Validation;

/// <summary>
/// Who may do what to the ledger. An agent's scene submission may record what a scene does, as provisional.
/// What the story is allowed or meant to do is the author's: rules, embargoes, secrets, a scene's contract, its
/// cast and its place in story time, who a persona really is, and whether a question is left open. Those reach
/// the ledger only by the author's own hand or by a proposal the author accepts.
/// </summary>
/// <remarks>
/// The table, by the way a change arrives:
/// <list type="bullet">
/// <item>A scene submission: what the scene does, for its own scene, is applied as provisional; a belief that
/// was inferred or guessed is applied and flagged; anything else is refused and belongs in a proposal.</item>
/// <item>A proposal: held for the author, never applied by the agent. It may carry anything except a decision.</item>
/// <item>A decision, to ratify or reject a record or to accept or decline a proposal: the author only.</item>
/// </list>
/// The rows are fixed here for now. Making them a per-story setting waits until a story needs one changed.
/// </remarks>
public static class Policy
{
    /// <summary>Why a record may not come in with a scene submission, or null if it may.</summary>
    public static string? NotInASceneSubmission(LedgerRecord record) => record switch
    {
        Entity { PersonaOf: not null } => "who a persona stands for is the author's to set",
        Proposition { Resolution: not Resolution.Decided } => "whether a question is left open or never settled is the author's to set",
        Event or Assertion or BeliefChange or Disclosure or Placement or FactChange or Proposition or Entity => null,
        SceneContract => "a scene's contract says what the scene is meant to do, and that is the author's to set",
        Embargo => "an embargo says what the reader may not be given yet, and that is the author's to set",
        Secret => "a secret says who may ever learn a thing, and that is the author's to set",
        Rule => "a rule constrains the whole story, and that is the author's to set",
        SceneRecord or SceneTime => "a scene's cast, point of view and place in story time are the author's to set",
        _ => "this kind of record is the author's to set",
    };

    /// <summary>
    /// Why a record is not a record of the given scene, or null if it is. A new person, place, thing or
    /// proposition belongs to no scene and may be introduced by any. Everything else must name the scene and,
    /// if it has a story time, fall within the scene's span of it.
    /// </summary>
    /// <remarks>
    /// The span is the test that matters. Every answer the engine gives is by story time, so a belief or a move
    /// labelled with this scene and dated before it would change what is so on entry to earlier scenes, which
    /// nobody drafting this scene was asked to touch. A change to the story outside the scene's own time, such
    /// as something a character turns out to have known all along, is a proposal for the author.
    /// </remarks>
    public static string? NotOfScene(StoryState s, LedgerRecord record, string scene)
    {
        var (from, to) = (s.Key(scene), s.SceneEnd(scene));
        string? Check(string? named, double? time, bool needsScene = true)
        {
            if (needsScene && named != scene)
                return named is null ? "it names no scene" : $"it names scene {named}";
            if (time is not double t)
                return "it has no story time";
            return t >= from && t < to
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"its story time {t} is outside the scene's span, which runs from {from} up to but not including {to}");
        }
        return record switch
        {
            Entity or Proposition => null,
            FactChange f => Check(null, f.StoryKey, needsScene: false),
            Event e => Check(e.Scene, e.StoryKey),
            Assertion a => Check(a.Scene, a.StoryKey),
            BeliefChange b => Check(b.Scene, b.StoryKey),
            Placement p => Check(p.Scene, p.StoryKey),
            Disclosure d => d.Scene == scene ? null : $"it names scene {d.Scene}",
            _ => "it is not a kind of record a scene makes",
        };
    }

    /// <summary>
    /// The people a record has present in the scene who are not in its cast: whoever comes to believe something,
    /// whoever is spoken to, overhears or reads, whoever takes part in an event, and whoever speaks. The writer
    /// of a document that is read in the scene need not be there, and narration belongs to whoever it is told
    /// through, not to the narrator. The cast is the author's to set; a record that has someone else in the
    /// room would change it by the back door.
    /// </summary>
    public static IReadOnlyList<string> OutsideCast(StoryState s, LedgerRecord record, string scene)
    {
        var entry = s.Model.Scenes[scene];
        var cast = entry.Cast.Append(entry.Pov).Select(s.Model.Base).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> present = record switch
        {
            BeliefChange b => [b.Holder],
            Event e => e.Participants.Keys,
            Assertion a => a.Addressees.Concat(a.Overhearers).Concat(a.ReadBy is null ? [] : [a.ReadBy]).Concat(a.Type switch
            {
                AssertionKind.Document => [],
                AssertionKind.Narration => a.Focalizer is null ? [] : [a.Focalizer],
                _ => [a.Speaker],
            }),
            _ => [],
        };
        return present.Where(p => !cast.Contains(s.Model.Base(p))).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Why an operation cannot be part of a proposal at all, or null if it can. A proposal asks the author to
    /// decide; it cannot carry the decision. Everything else may be proposed.
    /// </summary>
    public static string? NotInAProposal(Operation op) => op switch
    {
        RatifyOp or RejectOp => "deciding on a record is the author's own act and cannot be proposed. Provisional records are in the review queue already; say which ones you think are ready",
        PutOp { Approval: not Approval.Provisional } => "a proposal cannot say how the author will decide. Leave the approval out: if the author accepts the proposal, its records are ratified",
        _ => null,
    };

    /// <summary>
    /// What an operation does that makes it the author's to decide, in a few words. This is what the review
    /// queue shows beside a held proposal.
    /// </summary>
    /// <param name="now">The state as it stands, so that a record which takes the place of something says what.</param>
    public static string WhyHeld(Operation op, Projection before, IReadOnlyList<StoredChangeSet> ledger, StoryState now)
    {
        switch (op)
        {
            case PutOp put:
                return put.Record switch
                {
                    Rule => $"it sets a rule ({put.Record.Id})",
                    Embargo => $"it sets an embargo ({put.Record.Id})",
                    Secret => $"it sets a secret ({put.Record.Id})",
                    SceneContract c => $"it sets the contract for {c.Scene}",
                    SceneRecord => $"it sets the cast and point of view of {put.Record.Id}",
                    // A scene's time is restated by a later record, with nothing retired. Say that it moves, and from where.
                    SceneTime t when now.Model.SceneTimes.TryGetValue(t.Scene, out var had) =>
                        string.Create(CultureInfo.InvariantCulture, $"it moves {t.Scene} in story time, from {had.StoryKey} to {t.StoryKey}"),
                    SceneTime t => $"it sets the place of {t.Scene} in story time",
                    FactChange f => FactReason(f, now),
                    Entity { PersonaOf: not null } => $"it says who the persona {put.Record.Id} is",
                    Proposition { Resolution: not Resolution.Decided } => $"it leaves the question {put.Record.Id} open",
                    _ => $"it adds {put.Record.Id} outside a scene submission",
                };
            case RetireOp retire:
                var old = before.Find(retire.Id);
                if (old is null)
                    return $"it retires {retire.Id}";
                if (old.ApprovalAt(before.Head) == Approval.Ratified)
                    return $"it retires {retire.Id}, which the author ratified";
                return ledger[old.CreatedSeq - 1].ChangeSet.Basis is { } basis
                    ? $"it retires {retire.Id}, a provisional record of a submission for {basis.Scene}"
                    : $"it retires {retire.Id}, a record that did not come from a scene submission";
            default:
                return "it decides on records";
        }
    }

    private static string FactReason(FactChange f, StoryState now)
    {
        var to = f.Value ? "true" : "false";
        var when = f.StoryKey is double k ? string.Create(CultureInfo.InvariantCulture, $"from story time {k}") : "from the outset";
        // What the records have just before then, where they settle it.
        var was = f.StoryKey is double key && now.Model.Proposition(f.Prop) is not null ? now.TruthBefore(f.Prop, key) : null;
        return was is bool before && before != f.Value
            ? $"it changes what is so: {f.Prop} is {(before ? "true" : "false")} in the records, and this makes it {to} {when}"
            : $"it makes {f.Prop} {to} {when}, outside a scene submission";
    }

    /// <summary>
    /// Whether a record is one the author should look at by itself before it is confirmed: a belief the text
    /// does not state or entail, which someone inferred or guessed. Ratifying a whole change set leaves these
    /// for the author to name one by one.
    /// </summary>
    public static bool Flagged(LedgerRecord record) =>
        record is BeliefChange { Directness: Directness.Inferred or Directness.Speculative };
}
