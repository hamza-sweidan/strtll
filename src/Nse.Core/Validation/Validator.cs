using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Text;

namespace Nse.Validation;

/// <summary>One reason a change set cannot be applied. The code is stable; the message says what to do.</summary>
public sealed record Finding(string Code, string? RecordId, string Message)
{
    public override string ToString() => RecordId is null ? $"{Code}: {Message}" : $"{Code} {RecordId}: {Message}";
}

/// <summary>
/// Everything a change set must satisfy before it is written. These checks are deterministic: references
/// resolve, a quote is in the scene it cites word for word, nobody learns from something they were not present
/// at, nothing happens before its cause, and no record contradicts a declared secret. Whether a record means
/// what its author says is not checked here, and cannot be.
/// </summary>
public static partial class Validator
{
    /// <summary>The shape of the ids the engine gives change sets and proposals. A record may not take one.</summary>
    [GeneratedRegex(@"\A(cs|pr)_[0-9a-f]{10,}\z")]
    private static partial Regex EngineId();

    /// <summary>
    /// What a record's id may be: one plain word. The author reads ids in the review queue and types or pastes
    /// them after a command, so an id holds nothing a shell would act on (a semicolon, a dollar sign, a
    /// leading hyphen, a comma that splits a list) and nothing the eye cannot tell apart from another id
    /// (a letter of another script, a character that draws nothing). Scene markers in the manuscript are
    /// held to the same letters already.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.:-]{0,63}\z")]
    private static partial Regex PlainId();

    /// <param name="before">The ledger as it stands.</param>
    /// <param name="after">The ledger with the draft applied.</param>
    public static IReadOnlyList<Finding> Check(
        Projection before, Projection after, ChangeSetDraft draft, ManuscriptSnapshot manuscript)
    {
        var findings = new List<Finding>();
        var model = new StoryModel(after.LiveAt(after.Head), after.Head);
        var state = new StoryState(model, manuscript.Scenes);
        void Add(string code, string? id, string message) => findings.Add(new Finding(code, id, message));

        if (string.IsNullOrWhiteSpace(draft.Actor))
            Add("BAD_VALUE", null, "the change set needs an actor: who or what is making the change");
        // Everything a change set says is shown to the author sooner or later, in a queue or a report, among
        // lines the engine writes itself. Text that could start a line of its own could pass for one of those.
        void OneLine(string? text, string? id, string what, bool quote = false)
        {
            if (text is null)
                return;
            // A quote is copied from the manuscript, where a sentence may run over a line end and the next
            // line be indented. The matcher folds line breaks, tabs and runs of spaces into one space, so
            // those are let through in a quote, and nothing else of the kind is.
            var held = quote ? new string(text.Where(c => c is not ('\n' or '\r' or '\t')).ToArray()) : text;
            if (Plain.Odd(held) is { } odd)
                Add("BAD_VALUE", id, $"{what} holds a line break or another character that is not ordinary text ({odd}). Text in the ledger is one line of ordinary characters: write a line break as a space. Only a quote may keep the line breaks of the passage it was copied from");
            else if (!quote && Plain.Padded(text))
                Add("BAD_VALUE", id, $"{what} holds a run of three or more spaces. One is enough: a long run can push what follows onto a row of its own on the author's screen");
        }
        OneLine(draft.Actor, null, "the change set's actor");
        OneLine(draft.Note, null, "the change set's note");
        foreach (var op in draft.Ops)
        {
            if (op is RetireOp retiring)
                OneLine(retiring.Reason, retiring.Id, "the reason for retiring it");
            if (op is PutOp put)
            {
                CheckText(put.Record, OneLine);
                CheckRecord(put.Record, model, state, manuscript, Add);
                // A record that enters rejected never counts, can never be decided again and uses up its id.
                if (put.Approval == Approval.Rejected)
                    Add("BAD_VALUE", put.Record.Id, "a record cannot enter the ledger already rejected. Put it as provisional and reject it, in the same change set if that is what is wanted");
            }
            if (op is RetireOp retire && string.IsNullOrWhiteSpace(retire.Reason))
                Add("REASON_REQUIRED", retire.Id, "say why the record is retired: retcon, correction, duplicate or withdrawn");
        }

        // A record that is retired or rejected no longer counts. Either way it must not leave another record
        // pointing at nothing: rejecting is not a way round what retiring refuses.
        var gone = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in draft.Ops)
        {
            if (op is RetireOp retire)
                gone[retire.Id] = "retired";
            if (op is RejectOp reject)
                foreach (var id in reject.Ids)
                    gone[id] = "rejected";
        }
        if (gone.Count > 0)
            foreach (var t in model.Records)
                foreach (var (id, role) in References(t.Record))
                    if (gone.TryGetValue(id, out var how))
                        Add("DANGLING_REFERENCE", t.Record.Id, $"its {role} {id} is {how} by this change set. Retire, reject or replace this record too");

        CheckSecrets(model, state, Add);

        // A scene has one contract in force. A second would leave it open which of them the scene is held to.
        foreach (var contract in draft.Ops.OfType<PutOp>().Select(p => p.Record).OfType<SceneContract>())
            if (model.Records.Select(t => t.Record).OfType<SceneContract>().FirstOrDefault(c => c.Scene == contract.Scene && c.Id != contract.Id) is { } other)
                Add("BAD_VALUE", contract.Id, $"scene {contract.Scene} already has contract {other.Id} in force. Retire that one in the same change set: an edit is a retire and a put");

        // A disclosure that gives an embargoed fact away before the embargo lifts. Every breach that exists
        // after this change set and did not exist before it is refused, whatever brought it about: the
        // disclosure, the embargo, or a fact change that turns something already said into the truth. Looking
        // only at the records a change set carries would let the same three records in by another order.
        // A breach that was there already, because scenes were moved about, is left for sync to report.
        var already = new StoryState(new StoryModel(before.LiveAt(before.Head), before.Head), manuscript.Scenes)
            .EmbargoBreaches().Select(b => (b.Embargo.Id, b.Disclosure.Id)).ToHashSet();
        foreach (var breach in state.EmbargoBreaches())
            if (!already.Contains((breach.Embargo.Id, breach.Disclosure.Id)))
                Add("EMBARGO_BROKEN", breach.Disclosure.Id,
                    $"it {(breach.Disclosure.Mode == DisclosureMode.Stated ? "states" : "leaves the reader certain of")} {breach.Disclosure.Prop} in {breach.Disclosure.Scene}, and embargo {breach.Embargo.Id} holds that back until {breach.Embargo.UntilScene}"
                    + (state.Tell(breach.Embargo.UntilScene) is null ? ", which is not in the manuscript yet" : "")
                    + ". Record it as a hint the reader may miss, move the disclosure, or change the embargo");
        // A question the author has said is never to be settled. Characters may hold and voice opinions; what
        // may not happen is the reader being given it as settled, or canon deciding it. As with embargoes,
        // only what this change set brings about is refused.
        var settledAlready = new StoryState(new StoryModel(before.LiveAt(before.Head), before.Head), manuscript.Scenes)
            .RuleBreaches().Select(b => (b.Rule.Id, b.RecordId)).ToHashSet();
        foreach (var breach in state.RuleBreaches())
            if (!settledAlready.Contains((breach.Rule.Id, breach.RecordId)))
                Add("RULE_BROKEN", breach.RecordId, $"it {breach.How}, and rule {breach.Rule.Id} says that is never to be settled: {breach.Rule.Text}");
        return findings;
    }

    /// <summary>
    /// A secret names the only people who ever learn something. Any belief that lets someone else in breaks it.
    /// "Lets in" means taking the fact the way canon has it, about a fact that has come about by the time the
    /// belief is given up. That covers three cases: right at once; right later, because the belief was held
    /// before the fact came about and still is when it does; and behind the times, taking as true something
    /// that was true and has since stopped being so. The last is still finding out what was kept from them.
    /// Being right that it has not happened yet lets nobody in.
    /// </summary>
    private static void CheckSecrets(StoryModel model, StoryState state, Action<string, string?, string> add)
    {
        foreach (var secret in model.Secrets)
            foreach (var prop in secret.Props)
            {
                if (model.Proposition(prop) is null || state.Canon(prop) is not ("true" or "false"))
                    continue;
                var so = state.Canon(prop) == "true";
                var way = so ? Stance.HoldsTrue : Stance.HoldsFalse;
                var kept = $"secret {secret.Id} keeps to {string.Join(", ", secret.KnownOnlyTo)}";
                var outsiders = model.Beliefs.Where(b => b.Prop == prop && !secret.KnownOnlyTo.Contains(b.Holder)).GroupBy(b => b.Holder);
                foreach (var held in outsiders)
                    foreach (var b in held.Where(b => b.Stance == way))
                    {
                        if (state.TruthAt(prop, b.StoryKey) == so)
                        {
                            add("SECRET_BROKEN", b.Id, $"{b.Holder} is right about {prop}, which {kept}");
                            continue;
                        }
                        if (!so)
                            continue;
                        // In force from its own story time until the holder's next change of mind about it.
                        var until = held.Where(o => o.StoryKey > b.StoryKey).Select(o => o.StoryKey).DefaultIfEmpty(double.PositiveInfinity).Min();
                        var comesAbout = model.Facts(prop).Where(f => f.Value).Select(f => f.StoryKey ?? double.NegativeInfinity)
                            .Where(k => k < until).OrderBy(k => k).ToList();
                        if (comesAbout.Count == 0)
                            continue;
                        string At(double k) => double.IsNegativeInfinity(k) ? "the outset" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"story time {k}");
                        var later = comesAbout.Where(k => k > b.StoryKey).ToList();
                        add("SECRET_BROKEN", b.Id, later.Count > 0
                            ? $"{b.Holder} is right about {prop} from {At(later[0])}, while this belief is still in force, and that is what {kept}"
                            : $"{b.Holder} takes {prop} as true after it has stopped being so. It was so from {At(comesAbout[0])}, and that is what {kept}");
                    }
            }
    }

    private static void CheckRecord(
        LedgerRecord record, StoryModel model, StoryState state, ManuscriptSnapshot manuscript,
        Action<string, string?, string> add)
    {
        var id = record.Id;
        if (string.IsNullOrWhiteSpace(id))
            add("BAD_VALUE", null, "a record needs an id");
        else if (!PlainId().IsMatch(id))
            add("BAD_VALUE", id, "an id is one plain word: the letters a to z, digits, and after the first character also _ . : and -, sixty-four characters at most. The author types ids to decide on records, so an id holds nothing a shell or the eye could take for something else");
        else if (EngineId().IsMatch(id))
            // Otherwise "ratify cs_..." could mean the record or the change set, and whoever names a record
            // after a held proposal could keep the author from ever deciding on the proposal.
            add("BAD_VALUE", id, "an id of this shape is one the engine gives: cs_ and hex digits names a change set, pr_ and hex digits a proposal. Give the record another id");

        foreach (var (target, role) in References(record))
            if (string.IsNullOrWhiteSpace(target))
                add("BAD_VALUE", id, $"it names an empty {role} id");
            else if (!Exists(model, target, role))
                add("UNKNOWN_REFERENCE", id, $"its {role} {target} is not in the ledger");

        // A field that must be there must also say something.
        void NotBlank(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value))
                add("BAD_VALUE", id, $"its {what} is empty");
        }

        switch (record)
        {
            case Entity e:
                NotBlank(e.Name, "name");
                if (e.PersonaOf is { } person && model.Entity(person)?.PersonaOf is not null)
                    add("BAD_VALUE", id, "a persona must stand for a person, not for another persona");
                break;

            case SceneRecord s:
                if (!s.Cast.Contains(s.Pov))
                    add("BAD_VALUE", id, $"the point-of-view character {s.Pov} is not in the cast");
                break;

            case Proposition p:
                NotBlank(p.Text, "text");
                // A fact ends the part of people. A persona is a way of presenting one, and a place or a thing has no part to end.
                foreach (var who in p.Ends ?? [])
                    if (model.Entity(who) is { PersonaOf: { } behind })
                        add("PERSONA_HOLDER", id, $"it ends the part of the persona {who}. A fact ends a person's part: use {behind}");
                    else if (model.Entity(who) is { Type: not EntityKind.Character })
                        add("BAD_VALUE", id, $"it ends the part of {who}, which is not a person. Only a person takes part in what happens");
                break;

            case Rule r:
                NotBlank(r.Type, "type");
                NotBlank(r.Text, "text");
                if (r.Scope is { } scope)
                {
                    if (r.Type is "never_resolve" or "never_establish")
                        add("BAD_VALUE", id, "a never-settle rule keeps a question open for the whole book, and takes no scope. Leave the scope out");
                    if (scope.Holders.Count == 0 && scope.Scenes.Count == 0 && scope.FromScene is null && scope.UntilScene is null)
                        add("BAD_VALUE", id, "the scope names nothing, so it holds everywhere. Leave it out, or say whom or where the rule is for");
                    // A span is in story time, so its ends need places in story time, and the start must come first.
                    double? Key(string? scene) => scene is not null && model.SceneTimes.TryGetValue(scene, out var t) ? t.StoryKey : null;
                    foreach (var end in new[] { scope.FromScene, scope.UntilScene })
                        if (end is not null && model.Scenes.ContainsKey(end) && Key(end) is null)
                            add("BAD_VALUE", id, $"the scope's span ends at {end}, which has no place in story time. Give the scene a story time first");
                    if (Key(scope.FromScene) is double from && Key(scope.UntilScene) is double until && from >= until)
                        add("BAD_VALUE", id, $"the scope runs from {scope.FromScene} until {scope.UntilScene}, and {scope.UntilScene} is not later in story time, so it holds in no scene");
                }
                break;

            case SceneContract c:
                foreach (var (intent, list) in c.Intends.Select(i => (i, "intends")).Concat(c.Relies.Select(i => (i, "relies"))))
                {
                    var lacks = intent.Change switch
                    {
                        IntentKind.Belief when intent.Holder is null || intent.Prop is null => "a belief names a holder and a proposition",
                        IntentKind.Disclosure when intent.Prop is null => "a disclosure names a proposition",
                        IntentKind.Placement when intent.Object is null => "a placement names an object",
                        IntentKind.Fact when intent.Prop is null => "a fact names a proposition",
                        _ => null,
                    };
                    if (lacks is not null)
                        add("BAD_VALUE", id, $"an entry under '{list}' is incomplete: {lacks}");
                    // A field its kind does not use would make the entry match nothing, and the same change
                    // would then be reported both as missing and as unplanned.
                    var stray = new (string Name, bool Set, bool Used)[]
                    {
                        ("holder", intent.Holder is not null, intent.Change == IntentKind.Belief),
                        ("prop", intent.Prop is not null, intent.Change != IntentKind.Placement),
                        ("stance", intent.Stance is not null, intent.Change is IntentKind.Belief or IntentKind.Disclosure),
                        ("strength", intent.Strength is not null, intent.Change is IntentKind.Belief or IntentKind.Disclosure),
                        ("mode", intent.Mode is not null, intent.Change == IntentKind.Disclosure),
                        ("object", intent.Object is not null, intent.Change == IntentKind.Placement),
                        ("place", intent.Place is not null, intent.Change == IntentKind.Placement),
                        ("value", intent.Value is not null, intent.Change == IntentKind.Fact),
                    }.Where(f => f.Set && !f.Used).Select(f => f.Name).ToList();
                    if (stray.Count > 0)
                        add("BAD_VALUE", id, $"an entry under '{list}' for a {Names.Of(intent.Change)} carries fields that kind does not use: {string.Join(", ", stray)}");
                    if (intent.Holder is { } h && model.Entity(h) is { PersonaOf: { } really })
                        add("PERSONA_HOLDER", id, $"an entry under '{list}' names the persona {h}. Beliefs attach to the person: use {really}");
                }
                break;

            case Embargo em:
                // A tripwire is run against every draft of every scene the embargo holds in. One that cannot run,
                // or that matches an empty passage and so every paragraph, would make the scan say nothing true.
                foreach (var pattern in em.Tripwires)
                    if (DraftScan.Unusable(pattern) is { } unusable)
                        add("BAD_VALUE", id, $"the tripwire \"{pattern}\" cannot be used: {unusable}. A tripwire is a pattern of words a passage giving the fact away is likely to use, matched without regard to case");
                break;

            case Secret x:
                // Beliefs attach to the person, so a persona named here could never be recorded as knowing.
                foreach (var who in x.KnownOnlyTo)
                    if (model.Entity(who) is { PersonaOf: { } behind })
                        add("PERSONA_HOLDER", id, $"a secret is kept to people, not personas, and {who} is a persona. Use {behind}");
                if (x.Props.Count == 0)
                    add("BAD_VALUE", id, "a secret names at least one fact");
                break;

            case Event e:
                NotBlank(e.Summary, "summary");
                CheckQuote(e.Quote, e.QuoteScene ?? e.Scene, id, manuscript, add);
                break;

            case Assertion a:
                CheckQuote(a.Quote, a.Scene, id, manuscript, add);
                if (a.Type == AssertionKind.Narration && a.Focalizer is null)
                    add("BAD_VALUE", id, "narration needs a focalizer: whose perception the narration gives");
                break;

            case BeliefChange b:
                CheckBelief(b, model, add);
                CheckQuote(b.Quote, b.QuoteScene ?? b.Scene, id, manuscript, add);
                break;

            case Disclosure d:
                CheckQuote(d.Quote, d.Scene, id, manuscript, add);
                if (d.ReaderStance is Stance.HoldsTrue or Stance.HoldsFalse && d.Strength is null)
                    add("BAD_VALUE", id, "a view the reader holds needs a strength");
                break;

            case Placement p:
                if (model.Events.TryGetValue(p.Cause, out var cause) && p.StoryKey < cause.StoryKey)
                    add("BEFORE_CAUSE", id, $"the object moves at {p.StoryKey} but its cause {p.Cause} happens at {cause.StoryKey}");
                CheckQuote(p.Quote, p.QuoteScene ?? p.Scene, id, manuscript, add);
                break;
        }
    }

    /// <summary>Every piece of text a record holds, wherever in it, is one line of ordinary characters.</summary>
    private static void CheckText(LedgerRecord record, Action<string?, string?, string, bool> oneLine)
    {
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, value) in o)
                    {
                        // A name is fixed by the kind of record, except where it is itself an id, as in who took part.
                        oneLine(name, record.Id, $"a name under its {(path.Length == 0 ? "top level" : path)}", false);
                        Walk(value, path.Length == 0 ? name : path + "." + name);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        Walk(item, path);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var text):
                    oneLine(text, record.Id, $"its {path}", path == "quote" || path.EndsWith(".quote", StringComparison.Ordinal));
                    break;
            }
        }
        Walk(JsonNode.Parse(NseJson.Write(record)), "");
    }

    private static void CheckBelief(BeliefChange b, StoryModel model, Action<string, string?, string> add)
    {
        var id = b.Id;
        var holder = model.Entity(b.Holder);
        if (holder is { PersonaOf: not null })
            add("PERSONA_HOLDER", id, $"beliefs attach to the person, not the persona. Use {holder.PersonaOf}");
        if (b.Stance is Stance.HoldsTrue or Stance.HoldsFalse && b.Strength is null)
            add("BAD_VALUE", id, "a held belief needs a strength: certain, believes or suspects");

        var needsSource = b.Channel is Channel.Told or Channel.Overheard or Channel.Witnessed or Channel.TookPart or Channel.Read;
        if (b.Via is null)
        {
            if (needsSource)
                add("SOURCE_REQUIRED", id, $"channel '{Names.Of(b.Channel)}' needs a source: the event or assertion it came from");
            return;
        }
        model.Assertions.TryGetValue(b.Via, out var said);
        model.Events.TryGetValue(b.Via, out var happened);
        if (said is null && happened is null)
            return; // reported as an unknown reference already

        var sourceKey = said?.StoryKey ?? happened!.StoryKey;
        if (b.StoryKey < sourceKey)
            add("BEFORE_SOURCE", id, $"the belief is dated {b.StoryKey} but its source {b.Via} is at {sourceKey}");

        // Whatever the channel is called, nobody learns from a source they were not present at. Relabelling
        // "told" as "witnessed" must not get an absent character past this check.
        var present = said is not null
            ? new[] { said.Speaker }.Concat(said.Addressees).Concat(said.Overhearers).Concat(said.ReadBy is null ? [] : [said.ReadBy])
            : happened!.Participants.Keys;
        if (!present.Select(model.Base).Contains(b.Holder))
            add("NOT_PRESENT", id, $"{b.Holder} was not present at {b.Via}, so cannot have learned from it by any channel");
        if (said is null)
            return;
        if (b.Channel == Channel.Told && !said.Addressees.Select(model.Base).Contains(b.Holder))
            add("CHANNEL_MISMATCH", id, $"{b.Holder} was not addressed by {b.Via}");
        if (b.Channel == Channel.Overheard && !said.Overhearers.Select(model.Base).Contains(b.Holder))
            add("CHANNEL_MISMATCH", id, $"{b.Holder} did not overhear {b.Via}");
        if (b.Channel == Channel.Read && (said.ReadBy is null || model.Base(said.ReadBy) != b.Holder))
            add("CHANNEL_MISMATCH", id, $"{b.Holder} did not read {b.Via}");
        if (b.Channel is Channel.Told or Channel.Overheard or Channel.Read && said.Prop != b.Prop && b.Directness == Directness.Stated)
            add("CHANNEL_MISMATCH", id, $"marked 'stated', but {b.Via} asserts {said.Prop}, not {b.Prop}. Mark it entailed or cite the right assertion");
    }

    /// <summary>A quote is evidence only if it is in the cited scene word for word.</summary>
    private static void CheckQuote(
        string? quote, string? sceneId, string recordId, ManuscriptSnapshot manuscript, Action<string, string?, string> add)
    {
        if (quote is null)
            return;
        if (sceneId is null)
        {
            add("QUOTE_NEEDS_SCENE", recordId, "a quote needs a scene to be checked against");
            return;
        }
        var scene = manuscript.Find(sceneId);
        if (scene is null)
        {
            add("SCENE_NOT_IN_MANUSCRIPT", recordId, $"the quote cites scene {sceneId}, which has no marker in the manuscript");
            return;
        }
        var paragraphs = scene.Blocks.Select(b => b.Text).ToList();
        switch (QuoteMatcher.Match(paragraphs, quote))
        {
            case QuoteMatch.Exact:
                return;
            case QuoteMatch.TooShort:
                add("QUOTE_TOO_SHORT", recordId,
                    $"\"{quote}\" is in scene {sceneId}, but one word points at nothing. Quote at least two words, or a whole paragraph");
                return;
            // The words are there, so naming the nearest passage would only repeat them. Say what is wrong instead.
            case QuoteMatch.SpansParagraphs:
                add("QUOTE_NOT_FOUND", recordId,
                    $"the quote runs across a paragraph break in scene {sceneId}: \"{quote}\". Quote from one paragraph");
                return;
            case QuoteMatch.InsideWord:
                add("QUOTE_NOT_FOUND", recordId,
                    $"the quote is in scene {sceneId} only as part of longer words; it starts or ends inside a word: \"{quote}\". Quote whole words");
                return;
        }
        var nearest = QuoteMatcher.Nearest(string.Join("\n", paragraphs.Select(QuoteMatcher.Readable)), QuoteMatcher.Readable(quote));
        add("QUOTE_NOT_FOUND", recordId,
            $"the quote is not in scene {sceneId} word for word, as whole words within one paragraph: \"{quote}\"."
            + (nearest is null ? " Nothing close to it is there either." : $" Nearest passage: \"{nearest}\""));
    }

    /// <summary>Every id a record points at, with the role it plays, in a form an error message can use.</summary>
    public static IEnumerable<(string Id, string Role)> References(LedgerRecord record)
    {
        switch (record)
        {
            case Entity e:
                if (e.PersonaOf is not null) yield return (e.PersonaOf, "entity");
                break;
            case SceneRecord s:
                yield return (s.Pov, "entity");
                foreach (var c in s.Cast) yield return (c, "entity");
                if (s.Place is not null) yield return (s.Place, "entity");
                break;
            case SceneTime t:
                yield return (t.Scene, "scene");
                break;
            case Proposition p:
                foreach (var a in p.About) yield return (a, "entity");
                if (p.Nested is not null)
                {
                    yield return (p.Nested.Holder, "entity");
                    yield return (p.Nested.Prop, "proposition");
                }
                foreach (var who in p.Ends ?? []) yield return (who, "entity");
                break;
            case FactChange f:
                yield return (f.Prop, "proposition");
                break;
            case Event e:
                if (e.Scene is not null) yield return (e.Scene, "scene");
                if (e.QuoteScene is not null) yield return (e.QuoteScene, "scene");
                foreach (var who in e.Participants.Keys) yield return (who, "entity");
                foreach (var need in e.Needs ?? []) yield return (need, "proposition");
                break;
            case Assertion a:
                yield return (a.Scene, "scene");
                yield return (a.Prop, "proposition");
                yield return (a.Speaker, "entity");
                if (a.Focalizer is not null) yield return (a.Focalizer, "entity");
                if (a.ReadBy is not null) yield return (a.ReadBy, "entity");
                foreach (var who in a.Addressees.Concat(a.Overhearers)) yield return (who, "entity");
                break;
            case BeliefChange b:
                yield return (b.Holder, "entity");
                yield return (b.Prop, "proposition");
                if (b.Scene is not null) yield return (b.Scene, "scene");
                if (b.QuoteScene is not null) yield return (b.QuoteScene, "scene");
                if (b.Via is not null) yield return (b.Via, "source");
                break;
            case Disclosure d:
                yield return (d.Scene, "scene");
                yield return (d.Prop, "proposition");
                break;
            case Placement p:
                yield return (p.Object, "entity");
                yield return (p.Place, "entity");
                yield return (p.Cause, "event");
                if (p.Scene is not null) yield return (p.Scene, "scene");
                if (p.QuoteScene is not null) yield return (p.QuoteScene, "scene");
                break;
            case Rule r:
                if (r.Prop is not null) yield return (r.Prop, "proposition");
                if (r.Scope is { } scope)
                {
                    foreach (var who in scope.Holders) yield return (who, "entity");
                    foreach (var scene in scope.Scenes) yield return (scene, "scene");
                    if (scope.FromScene is not null) yield return (scope.FromScene, "scene");
                    if (scope.UntilScene is not null) yield return (scope.UntilScene, "scene");
                }
                break;
            case Embargo e:
                yield return (e.Prop, "proposition");
                yield return (e.UntilScene, "scene");
                break;
            case Secret s:
                foreach (var p in s.Props) yield return (p, "proposition");
                foreach (var who in s.KnownOnlyTo) yield return (who, "entity");
                break;
            case SceneContract c:
                yield return (c.Scene, "scene");
                foreach (var i in c.Intends.Concat(c.Relies))
                {
                    if (i.Holder is not null) yield return (i.Holder, "entity");
                    if (i.Prop is not null) yield return (i.Prop, "proposition");
                    if (i.Object is not null) yield return (i.Object, "entity");
                    if (i.Place is not null) yield return (i.Place, "entity");
                }
                break;
        }
    }

    private static bool Exists(StoryModel model, string id, string role) => role switch
    {
        "entity" => model.Entity(id) is not null,
        "scene" => model.Scenes.ContainsKey(id),
        "proposition" => model.Proposition(id) is not null,
        "event" => model.Events.ContainsKey(id),
        "source" => model.Events.ContainsKey(id) || model.Assertions.ContainsKey(id),
        _ => model.ById.ContainsKey(id),
    };
}
