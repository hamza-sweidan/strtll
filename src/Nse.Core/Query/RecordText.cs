using System.Globalization;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>
/// Any record in one line of words, for the author deciding on it. It names people by the names the state
/// has for them and falls back to ids, so it can describe a record that is proposed and not yet in the ledger.
/// A proposed record has passed no check, so whatever it says is printed as one line: a line break or a
/// terminal code inside it is spelled out, not obeyed.
/// </summary>
public static class RecordText
{
    public static string Line(StoryState s, LedgerRecord record) => Plain.OneLine(Compose(s, record));

    private static string Compose(StoryState s, LedgerRecord record)
    {
        string Who(string id) => s.Model.Entity(id) is not null ? s.Short(id) : id;
        string At(double? key) => key is double k ? string.Create(CultureInfo.InvariantCulture, $"at {k}") : "from the outset";
        string In(string? scene) => scene is null ? "" : $" in {scene}";
        // As the matcher reads it: a quote copied over a line end is shown on one line.
        string Quoted(string? quote) => quote is null ? "" : $" · \"{QuoteMatcher.Normalize(quote)}\"";
        string Taking(Stance stance, Strength? strength) => stance switch
        {
            Stance.HoldsTrue => "as true", Stance.HoldsFalse => "as false", _ => Names.Of(stance),
        } + (strength is { } st ? ", " + Names.Of(st) : "");
        string Scope(RuleScope? scope)
        {
            if (scope is null)
                return "";
            var parts = new List<string>();
            if (scope.Holders.Count > 0)
                parts.Add("for " + string.Join(", ", scope.Holders.Select(Who)));
            if (scope.Scenes.Count > 0)
                parts.Add("in " + string.Join(", ", scope.Scenes));
            if (scope.FromScene is not null)
                parts.Add("from " + scope.FromScene);
            if (scope.UntilScene is not null)
                parts.Add("until " + scope.UntilScene);
            return parts.Count == 0 ? " [scope names nothing]" : " [" + string.Join("; ", parts) + "]";
        }

        switch (record)
        {
            case Entity e:
                return $"entity: {e.Name} ({Names.Of(e.Type)})"
                       + (e.PersonaOf is { } behind ? $", a persona of {Who(behind)}" : "")
                       + (e.Aliases.Count > 0 ? $", also called {string.Join(", ", e.Aliases)}" : "");
            case SceneRecord sc:
                return $"scene: {sc.Title ?? "(untitled)"} · POV {Who(sc.Pov)} · cast {string.Join(", ", sc.Cast.Select(Who))}"
                       + (sc.Place is { } place ? $" · at {Who(place)}" : "");
            case SceneTime t:
                return string.Create(CultureInfo.InvariantCulture, $"scene time: {t.Scene} at story time {t.StoryKey}")
                       + (t.Label is null ? "" : $" ({t.Label})") + (t.Basis == TimeBasis.FollowsTelling ? ", following the telling" : ", asserted");
            case Proposition p:
                return $"proposition: {p.Text}" + (p.Resolution == Resolution.Decided ? "" : $" [{Names.Of(p.Resolution)}]")
                       + (p.Ends is { Count: > 0 } ends ? $" [ends the part of {string.Join(", ", ends.Select(Who))}]" : "");
            case FactChange f:
                return $"fact: {f.Prop} becomes {(f.Value ? "true" : "false")} {At(f.StoryKey)}";
            case Event e:
                return $"event {At(e.StoryKey)}{In(e.Scene)}: {e.Summary}"
                       + (e.Participants.Count == 0 ? "" : " [" + string.Join(", ", e.Participants.Select(x => $"{Who(x.Key)} {Names.Of(x.Value)}")) + "]")
                       + (e.Needs is { Count: > 0 } needs ? $" · needs {string.Join(", ", needs)}" : "")
                       + Quoted(e.Quote);
            case Assertion a:
                return $"{Names.Of(a.Type)} {At(a.StoryKey)} in {a.Scene}: {Who(a.Speaker)} {(a.Polarity ? "asserts" : "denies")} {a.Prop}"
                       + (a.Addressees.Count == 0 ? "" : " to " + string.Join(", ", a.Addressees.Select(Who)))
                       + (a.Overhearers.Count == 0 ? "" : ", overheard by " + string.Join(", ", a.Overhearers.Select(Who)))
                       + (a.ReadBy is { } reader ? $", read by {Who(reader)}" : "")
                       + Quoted(a.Quote);
            case BeliefChange b:
                return $"belief {At(b.StoryKey)}{In(b.Scene)}: {Who(b.Holder)} "
                       + (b.Stance is Stance.HoldsTrue or Stance.HoldsFalse ? $"comes to take {b.Prop} {Taking(b.Stance, b.Strength)}" : $"is {Names.Of(b.Stance)} about {b.Prop}")
                       + $" · {Names.Of(b.Channel)}{(b.Via is null ? "" : " via " + b.Via)} · {Names.Of(b.Directness)}"
                       + Quoted(b.Quote);
            case Disclosure d:
                return $"disclosure in {d.Scene}: the reader is given {d.Prop} [{Names.Of(d.Mode)}], and takes it {Taking(d.ReaderStance, d.Strength)}" + Quoted(d.Quote);
            case Placement p:
                return $"placement {At(p.StoryKey)}{In(p.Scene)}: {Who(p.Object)} moves to {Who(p.Place)}, cause {p.Cause}" + Quoted(p.Quote);
            case Rule r:
                return $"rule ({r.Type}){(r.Prop is null ? "" : " on " + r.Prop)}{Scope(r.Scope)}: {r.Text}";
            case Embargo e:
                return $"embargo: {e.Prop} is not to be given to the reader before {e.UntilScene}";
            case Secret x:
                return $"secret: only {string.Join(", ", x.KnownOnlyTo.Select(Who))} ever learn {string.Join(", ", x.Props)}";
            case SceneContract c:
                return $"contract for {c.Scene}: {c.Intends.Count} intended change(s), {c.Relies.Count} relied on"
                       + (c.Outline is null ? "" : $" · {c.Outline}");
            default:
                return record.KindName;
        }
    }

    /// <summary>What a record says that takes more than a line: the entries of a contract. Empty for every other kind.</summary>
    public static IReadOnlyList<string> Detail(StoryState s, LedgerRecord record)
    {
        if (record is not SceneContract c)
            return [];
        try
        {
            return
            [
                .. c.Intends.Select(i => Plain.OneLine("intends: " + SceneContracts.Describe(s, i))),
                .. c.Relies.Select(i => Plain.OneLine("relies on: " + SceneContracts.Describe(s, i, onEntry: true))),
            ];
        }
        catch (KeyNotFoundException)
        {
            // It names someone the state does not have. The check that refuses the proposal says so; here there is nothing to add.
            return [];
        }
    }

    /// <summary>
    /// What a proposed record would take the place of, read from the state as it stands: whether a fact is so
    /// just before it changes, the story time a scene has now, what a person holds or where a thing is kept
    /// just before. A retire is shown with the record it takes away. A put can override as much without
    /// retiring anything, and the author should see that as plainly.
    /// </summary>
    public static IReadOnlyList<string> Replaces(StoryState now, LedgerRecord record)
    {
        try
        {
            return Overridden(now, record).Select(Plain.OneLine).ToList();
        }
        catch (KeyNotFoundException)
        {
            // It names something the state does not have. The check that refuses the proposal says so.
            return [];
        }
    }

    private static IEnumerable<string> Overridden(StoryState now, LedgerRecord record)
    {
        string Approved(string id) => now.Model.ById.TryGetValue(id, out var t) ? Names.Of(t.ApprovalAt(now.Model.Head)) : "not in force";
        switch (record)
        {
            case FactChange f when now.Model.Proposition(f.Prop) is { } prop:
                yield return $"{f.Prop} is: {prop.Text}";
                if (f.StoryKey is not double key)
                    break;
                if (now.FactTie(f.Prop, key, before: true) is { } tie)
                    yield return $"before then the records do not settle it: {string.Join(" and ", tie)} tie";
                else if (now.TruthBefore(f.Prop, key) is bool was)
                {
                    var by = now.Model.Facts(f.Prop).Where(x => (x.StoryKey ?? double.NegativeInfinity) < key)
                        .OrderBy(x => x.StoryKey ?? double.NegativeInfinity).Last();
                    yield return $"before then it is {(was ? "true" : "false")} ({by.Id}, {Approved(by.Id)})";
                }
                else
                    yield return $"before then the records do not say whether it is so ({now.Canon(f.Prop)})";
                break;
            case SceneTime t when now.Model.SceneTimes.TryGetValue(t.Scene, out var old):
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"takes the place of story time {old.StoryKey} ({old.Id}, {Approved(old.Id)}). Records dated inside the scene keep their own dates");
                break;
            case BeliefChange b when now.Model.Entity(b.Holder) is not null && now.Model.Proposition(b.Prop) is not null:
                var holder = now.Model.Base(b.Holder);
                var held = now.BeliefsBefore(holder, b.StoryKey).GetValueOrDefault(b.Prop);
                yield return held is null
                    ? $"before then nothing is recorded for {now.Short(holder)} about {b.Prop}"
                    : $"before then {now.Short(holder)}: [{now.Classify(held, b.StoryKey)}] ({held.Id}{(held.Unresolved ? "" : ", " + Approved(held.Id))})";
                break;
            case Placement p when now.Model.Entity(p.Object) is not null:
                var at = now.Location(p.Object, p.StoryKey);
                yield return at is null ? "before then no place is recorded for it"
                    : at.Unresolved ? $"before then its place is not settled: {string.Join(" and ", at.Conflict!)} tie"
                    : $"before then it is kept at {(now.Model.Entity(at.Place!) is null ? at.Place : now.Short(at.Place!))} ({at.Id}, {Approved(at.Id)})";
                break;
        }
    }
}
