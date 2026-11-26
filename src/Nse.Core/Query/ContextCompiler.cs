using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nse.Model;

namespace Nse.Query;

/// <summary>Thrown when even the parts a context must contain do not fit the budget. Nothing is ever truncated.</summary>
public sealed class BudgetException(string message) : Exception(message);

/// <summary>One titled part of a context. <see cref="Must"/> parts are never left out to fit a budget.</summary>
public sealed record ContextSection(string Title, bool Must, IReadOnlyList<string> Lines);

/// <summary>
/// The id a context is known by, in three parts the engine can check when it is handed back: the ledger head
/// the context was compiled at; a mark of the order the scenes were in; and a mark of everything the context
/// said about the story's state. Given the same ledger head and the same order of scenes the engine can compile
/// the context again and must get the same marks, so an id that was made up, or that belongs to another scene,
/// does not pass.
/// </summary>
public sealed record ManifestId(int Head, string Order, string Basis)
{
    public static ManifestId? Parse(string? text)
    {
        if (text is null)
            return null;
        var parts = text.Split('_');
        if (!(parts.Length == 4 && parts[0] == "m"
              && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var head)
              && parts[2].Length == 6 && parts[2].All(char.IsAsciiHexDigitLower) && parts[3].Length == 8 && parts[3].All(char.IsAsciiHexDigitLower)))
            return null;
        // Only the text the engine prints is an id. "m_0012_..." or one in capitals names the same context, but
        // what is handed back is written into the ledger as given, so it has to be the id and nothing else.
        var id = new ManifestId(head, parts[2], parts[3]);
        return id.ToString() == text ? id : null;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"m_{Head}_{Order}_{Basis}");
}

/// <summary>
/// The context for drafting or revising one scene. A later call hands the manifest id back, and the engine can
/// tell whether the state has moved since.
/// </summary>
/// <param name="Sections">
/// Every part that was compiled, including any the budget left out of <see cref="Text"/>. This is what a
/// context rests on, and what two contexts are compared by.
/// </param>
public sealed record ContextPackage(
    string Scene, int Head, string ManuscriptHash, string ManifestId, int EstimatedTokens, int Budget, string Text,
    IReadOnlyList<string> Omitted, IReadOnlyList<string> Records, IReadOnlyList<ContextSection> Sections);

/// <summary>
/// Compiles the state on entry to a scene into one readable package. Three levels: what must be there
/// (what may not be revealed, open questions, what each cast member does not know, the point-of-view
/// character's beliefs), what is relevant (the rest of the cast, the reader, where things are, the prose
/// to read), and everything else, which is named but left for state_at. If the first level does not fit the
/// budget the call fails; the second level is dropped section by section and each omission is listed.
/// </summary>
public static class ContextCompiler
{
    public const int DefaultBudget = 10_000;

    /// <summary>The start of the title of the part that points at the prose. Drafting the scene is what changes it.</summary>
    public const string ProseTitle = "PROSE TO READ";

    public const string ProvisionalMark = ", PROVISIONAL: do not build on this";

    public const string ProvisionalEmbargoMark = ", provisional but keep to it";

    /// <summary>The start of the title of the part that says what the scene itself is. It is the header, kept as a part so that it is compared.</summary>
    public const string SceneTitle = "SCENE";

    /// <summary>The ledger head a manifest id was compiled at, or null if the text is not a manifest id.</summary>
    public static int? HeadOf(string? manifestId) => ManifestId.Parse(manifestId)?.Head;

    /// <summary>A mark of the order the scenes are told in. Moving, adding or removing a scene changes it; editing a scene's text does not.</summary>
    public static string OrderMark(StoryState s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", s.Telling.Select(t => t.Id)))))[..6];

    /// <summary>A rough size guide, characters over 3.6. Not a token count.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.6);

    private sealed record Section(string Title, bool Must, List<string> Lines, List<string> Records)
    {
        private string? _text;

        /// <summary>The part as it is printed. It is asked for only once every line is in, and fitting the budget reads it several times.</summary>
        public string Text => _text ??= Title + "\n" + string.Join("\n", Lines);
    }

    /// <param name="tail">
    /// Words to end the text with, after the manifest line. They are counted against the budget like the rest,
    /// and they are no part of what the manifest id stands for.
    /// </param>
    public static ContextPackage Compile(StoryState s, string sceneId, int budget = DefaultBudget, string tail = "")
    {
        var (header, sections, recap, parts) = Gather(s, sceneId);
        var manifest = new ManifestId(s.Model.Head, OrderMark(s), Freshness.Mark(sceneId, s.Model.Head, parts)).ToString();

        // The size that counts is the size of the finished text: header, sections, the note about what was left
        // out and the manifest line. Assemble it and measure it, so the budget is a promise and not an estimate.
        ContextPackage Assemble(bool[] use)
        {
            var included = sections.Where((_, i) => use[i]).ToList();
            var omitted = sections.Where((_, i) => !use[i]).Select(x => $"{x.Title} (~{EstimateTokens(x.Text) + 1} tokens)").ToList();
            var body = new StringBuilder(header);
            foreach (var section in included)
                body.Append("\n\n").Append(section.Text);
            if (omitted.Count > 0)
                body.Append("\n\nOMITTED TO FIT THE BUDGET, available through state_at:\n").Append(string.Join("\n", omitted.Select(o => "  " + o)));
            body.Append(recap);
            var records = included.SelectMany(x => x.Records).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
            var final = body.Append($"\n\nManifest {manifest} · manuscript {Short(s.ManuscriptHash)}\nGive this manifest id back when you submit the scene: it says which state the draft was written from.").Append(tail).ToString();
            return new ContextPackage(sceneId, s.Model.Head, s.ManuscriptHash, manifest, EstimateTokens(final), budget, final, omitted, records, parts);
        }

        var use = sections.Select(x => x.Must).ToArray();
        var least = Assemble(use);
        if (least.EstimatedTokens > budget)
        {
            var must = sections.Where(x => x.Must).ToList();
            throw new BudgetException(
                $"BUDGET_INSUFFICIENT: the parts that must be in the context for {sceneId} need about {least.EstimatedTokens} tokens and the budget is {budget}. " +
                "Largest: " + string.Join("; ", must.OrderByDescending(x => EstimateTokens(x.Text)).Take(3).Select(x => $"{x.Title.Split(':')[0]} ~{EstimateTokens(x.Text)}")) +
                (tail.Length > 0 ? $". About {EstimateTokens(tail)} of that is the closing words on the scene's task, which a context taken to work from always carries" : "") +
                ". Raise the budget; nothing is ever cut to fit.");
        }
        // Take the rest in order while it fits by the sum of its parts, then check the finished text and give
        // up the largest optional section until it does. With none left this is the text measured above.
        var used = EstimateTokens(header) + sections.Where(x => x.Must).Sum(x => EstimateTokens(x.Text) + 1);
        for (var i = 0; i < sections.Count; i++)
        {
            var size = EstimateTokens(sections[i].Text) + 1;
            if (use[i] || used + size > budget)
                continue;
            use[i] = true;
            used += size;
        }
        while (true)
        {
            var package = Assemble(use);
            if (package.EstimatedTokens <= budget)
                return package;
            var drop = Enumerable.Range(0, sections.Count).Where(i => use[i] && !sections[i].Must)
                .OrderByDescending(i => EstimateTokens(sections[i].Text)).ThenByDescending(i => i).First();
            use[drop] = false;
        }
    }

    /// <summary>
    /// What a context for a scene says, part by part: everything in <see cref="ContextPackage.Sections"/>, with
    /// no text put together and no budget applied. Two contexts are compared by these parts alone, and a what-if
    /// compares two for every scene of the story and prints none of them.
    /// </summary>
    internal static IReadOnlyList<ContextSection> Sections(StoryState s, string sceneId) => Gather(s, sceneId).Parts;

    /// <summary>
    /// Everything a context is made of, before it is fitted to a budget: the header, the sections with the records
    /// each rests on, the closing list of constraints, and the parts as they are compared.
    /// </summary>
    private static (string Header, List<Section> Sections, string Recap, List<ContextSection> Parts) Gather(StoryState s, string sceneId)
    {
        if (!s.Model.Scenes.TryGetValue(sceneId, out var scene))
            throw new KeyNotFoundException($"No scene record {sceneId}.");
        var key = s.Key(sceneId);
        var tell = s.Tell(sceneId) ?? throw new KeyNotFoundException($"Scene {sceneId} has no marker in the manuscript, so it has no place in the telling.");
        var text = s.Telling.First(t => t.Id == sceneId);
        var cast = s.CastHolders(sceneId);
        var sections = new List<Section>();

        var end = s.SceneEnd(sceneId);
        // A state the checks would refuse can name a record that is not there, and what-if compiles such states on
        // purpose, to show what a refused change would do. A missing record is said by name, as everywhere else
        // in the queries, so that the caller can report it; it must never surface as a null reference.
        var pov = s.Model.Entity(scene.Pov) ?? throw new KeyNotFoundException($"No entity {scene.Pov}.");
        string Prop(string id) => (s.Model.Proposition(id) ?? throw new KeyNotFoundException($"No proposition {id}.")).Text;
        // A fact whose changes tie and disagree is not printed as plain truth: whether it is so is not known.
        // The same fact is phrased once for each person who does not know it, so the words are kept for the scene.
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        string TruthPhrase(string id)
        {
            if (phrases.TryGetValue(id, out var phrase))
                return phrase;
            return phrases[id] =
                (s.Canon(id) == "false" ? "It is NOT true that: " + Prop(id) : Prop(id))
                + (s.FactTie(id, end, before: true) is { } tie
                    ? $" [NOT SETTLED whether this is so by the end of the scene: fact changes {string.Join(" and ", tie)} share a story time and disagree]"
                    : "")
                // Whether it is so at all may rest on a fact change or a proposition nobody has confirmed.
                + s.UnconfirmedMark(id);
        }
        // A record that settles nothing: say which kind, never "nothing recorded".
        string Unsettled(string person, string id) =>
            s.BeliefBefore(person, id, end) switch
            {
                { Unresolved: true } b => $"their records {string.Join(" and ", b.Conflict!)} share a story time and disagree",
                { Stance: Stance.Undecided } => "recorded as undecided",
                _ => "holds a view, but whether the fact is so is not settled, so the view cannot be judged",
            };
        string Provisional(string id) => s.Model.ById.TryGetValue(id, out var t) && t.ApprovalAt(s.Model.Head) == Approval.Provisional ? ProvisionalMark : "";
        // A label such as "knows" or "FALSE BELIEF" is a judgement against the truth, and is no firmer than it.
        string Line(string prop, BeliefView b) =>
            $"  {prop} [{s.Classify(b, key)}] {Stand(b.Stance, b.Strength)}: {Prop(prop)} ({b.Id}{Provisional(b.Id)})"
            + (b.Stance is Stance.HoldsTrue or Stance.HoldsFalse ? s.UnconfirmedMark(prop, key) : "");
        string InScene(string id) => s.Tell(id) is int n ? $"scene {n}, {id}" : $"{id}, which is not in the manuscript yet";
        string CastOnEntry(string prop) => "      cast on entry: " + string.Join("; ",
            cast.Select(c => $"{s.Short(c.Person)}: {s.Classify(s.BeliefBefore(c.Person, prop, key), key)}"));

        // ---- must: what the scene is for. The contract is the author's statement of what it should do and
        // what it relies on; each thing relied on is shown with whether the records bear it out on entry.
        if (s.Contract(sceneId) is { } contract)
        {
            var agreed = new Section($"CONTRACT FOR THIS SCENE ({contract.Id}{Provisional(contract.Id)}): what it is meant to do", true, [], [contract.Id]);
            if (!string.IsNullOrWhiteSpace(contract.Outline))
                agreed.Lines.Add("  outline: " + contract.Outline.Trim());
            agreed.Lines.AddRange(contract.Intends.Select(i => "  intends: " + SceneContracts.Describe(s, i)));
            foreach (var (need, status) in SceneContracts.Preconditions(s, sceneId, contract))
                agreed.Lines.Add($"  relies on: {SceneContracts.Describe(s, need, onEntry: true)}. {(status.Code is null ? "" : "NOT BORNE OUT ON ENTRY. ")}{status.Detail}");
            if (agreed.Lines.Count == 0)
                agreed.Lines.Add("  nothing is planned or relied on");
            agreed.Lines.Add("  Anything else of weight that the scene does to what people know, to what the reader is told or to where things are will be reported as unplanned.");
            sections.Add(agreed);
        }

        // ---- must: what the scene is already recorded as doing. A new draft of a scene that has been
        // submitted before has to know this: each of these it keeps, by leaving it alone, or withdraws. It is
        // also what makes a second draft stale once a first has been applied.
        var recorded = new Section("ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it)", true, [], []);
        foreach (var happened in s.Model.EventsIn(sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var who = happened.Participants.Count == 0 ? "" : " [" + string.Join(", ", happened.Participants.Select(x => $"{s.Short(x.Key)} {Names.Of(x.Value)}")) + "]";
            recorded.Lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {happened.Id} at {happened.StoryKey}, event: {happened.Summary}{who}{Provisional(happened.Id)}"));
            recorded.Records.Add(happened.Id);
        }
        foreach (var said in s.Model.AssertionsIn(sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var to = said.Addressees.Count == 0 ? "" : " to " + string.Join(", ", said.Addressees.Select(s.Short));
            recorded.Lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {said.Id} at {said.StoryKey}, {Names.Of(said.Type)}: {s.Short(said.Speaker)} {(said.Polarity ? "asserts" : "denies")} {said.Prop}{to} [{s.ClassifyAssertion(said.Id)}]{Provisional(said.Id)}"));
            recorded.Records.Add(said.Id);
        }
        foreach (var change in s.RecordedChanges(sceneId))
        {
            recorded.Lines.Add($"  {change.RecordId}: {change.Text}{Provisional(change.RecordId)}");
            recorded.Records.Add(change.RecordId);
        }
        if (recorded.Lines.Count == 0)
            recorded.Lines.Add("  nothing yet");
        sections.Add(recorded);

        // ---- must: the author's word on this scene. A note is their understanding of a person or a stretch of
        // the story, in prose, scoped to where it holds. Notes like these, with their stages marked, are what a
        // drafter needs to write a person rather than a set of facts.
        var rules = s.RulesInScope(sceneId);
        if (rules.Count > 0)
        {
            var word = new Section("THE AUTHOR'S RULES AND NOTES FOR THIS SCENE (a note is the author's understanding, to write from; a rule is to keep to)", true, [], []);
            foreach (var (rule, caveat) in rules)
            {
                var kind = rule.Type == "guidance" ? "note" : $"rule ({rule.Type})";
                word.Lines.Add($"  {rule.Id}{Provisional(rule.Id)}, {kind}{(rule.Prop is null ? "" : " on " + rule.Prop)}{Where(rule.Scope)}: {rule.Text}"
                               + (caveat is null ? "" : $" [{caveat}]"));
                word.Records.Add(rule.Id);
            }
            sections.Add(word);
        }
        string Where(RuleScope? scope)
        {
            if (scope is null)
                return "";
            var parts = new List<string>();
            if (scope.Holders.Count > 0)
                parts.Add("for " + string.Join(", ", scope.Holders.Select(h => s.Model.Entity(h)?.Name ?? h)));
            if (scope.Scenes.Count > 0)
                parts.Add("in " + string.Join(", ", scope.Scenes));
            if (scope.FromScene is not null)
                parts.Add("from " + scope.FromScene);
            if (scope.UntilScene is not null)
                parts.Add("until " + scope.UntilScene);
            return " [" + string.Join("; ", parts) + (scope.FromScene ?? scope.UntilScene) switch { null => "", _ => ", in story time" } + "]";
        }

        // ---- must: what may not be revealed. Two sources: what the records show the reader is first told
        // later, and what the author has embargoed, which holds even before the revealing scene is written.
        var withheld = new Section("MUST NOT BE REVEALED TO THE READER IN THIS SCENE (the reader is first told later)", true, [], []);
        var embargoes = s.EmbargoesInForce(sceneId);
        var embargoesOn = embargoes.ToLookup(e => e.Prop, StringComparer.Ordinal);
        string Embargoed(string prop)
        {
            var mine = embargoesOn[prop].ToList();
            withheld.Records.AddRange(mine.Select(e => e.Id));
            // A provisional embargo is still kept to: the cautious reading of an undecided constraint is to honour it.
            return string.Join("; ", mine.Select(e =>
                $"embargo {e.Id}{(Provisional(e.Id).Length > 0 ? ProvisionalEmbargoMark : "")}: not before {InScene(e.UntilScene)}"));
        }
        var told = s.MustNotReveal(sceneId);
        var toldLater = told.Select(t => t.Prop).ToHashSet(StringComparer.Ordinal);
        foreach (var (prop, reveal) in told)
        {
            var embargo = Embargoed(prop);
            withheld.Lines.Add($"  {prop}: {TruthPhrase(prop)} (first told in {InScene(reveal)}{(embargo.Length > 0 ? "; " + embargo : "")})");
            withheld.Lines.Add(CastOnEntry(prop));
        }
        foreach (var prop in embargoes.Select(e => e.Prop).Distinct().Where(p => !toldLater.Contains(p)))
        {
            withheld.Lines.Add($"  {prop}: {TruthPhrase(prop)} ({Embargoed(prop)})");
            withheld.Lines.Add(CastOnEntry(prop));
        }
        if (withheld.Lines.Count == 0)
            withheld.Lines.Add("  nothing is being withheld from the reader at this point");
        sections.Add(withheld);

        var open = s.Model.Rules.Where(r => r.Type is "never_resolve" or "never_establish").ToList();
        if (open.Count > 0)
            sections.Add(new Section("NEVER SETTLE THESE", true, open.Select(r => $"  {r.Prop}: {r.Text}").ToList(), open.Select(r => r.Id).ToList()));

        // ---- must: what each cast member does not know; and the point-of-view character's beliefs
        var unknownTo = new List<(string Name, List<string> Props)>();
        var noRecord = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, person) in cast)
        {
            var head = s.Model.Entity(entry)!.Name + (entry == scene.Pov ? " (point of view)" : "");
            if (entry != person)
                head += $"  [AUTHOR ONLY: this is {s.Model.Entity(person)!.Name}. Do not reveal it.]";
            var gaps = new Section($"{head}: does NOT know, and must not say it, be told it or act on it in this scene", true, [], []);
            var (personGaps, learns) = s.Knowledge(person, sceneId);
            foreach (var gap in personGaps)
            {
                // Late in a long story most facts in play are ones a person present has no record on and never
                // comes to know. The line that says so is the same for each of them, and is written once.
                if (gap is { Status: KnowledgeStatus.NoRecord, LearnsIn: null, LearnsAt: null })
                {
                    if (!noRecord.TryGetValue(gap.Prop, out var said))
                        noRecord[gap.Prop] = said = $"  {gap.Prop}: {TruthPhrase(gap.Prop)} (nothing recorded either way; treat as not knowing)";
                    gaps.Lines.Add(said);
                    continue;
                }
                var why = gap.LearnsIn is { } later ? $"learns it in {InScene(later)}"
                    // Learned in no scene is still learned. Saying "never" here would be wrong.
                    : gap.LearnsAt is double at ? string.Create(CultureInfo.InvariantCulture, $"learns it later, off the page, at story time {at}")
                    : gap.Status == KnowledgeStatus.NoRecord ? "nothing recorded either way; treat as not knowing"
                    : gap.Status == KnowledgeStatus.Unsettled ? "treat as not knowing"
                    : s.Sealed(person, gap.Prop) is { } secret ? $"never learns it: {secret.Id} keeps it to {string.Join(", ", secret.KnownOnlyTo.Select(s.Short))}"
                    : "never learns it in the story as recorded";
                if (gap.Status == KnowledgeStatus.Wrong)
                    why += "; currently believes the opposite";
                if (gap.Status == KnowledgeStatus.Unsettled)
                    why = Unsettled(person, gap.Prop) + "; " + why;
                gaps.Lines.Add($"  {gap.Prop}: {TruthPhrase(gap.Prop)} ({why})");
            }
            if (gaps.Lines.Count > 0)
            {
                sections.Add(gaps);
                unknownTo.Add((s.Model.Entity(entry)!.Name, personGaps.Select(g => g.Prop).ToList()));
            }

            var beliefs = s.BeliefsBefore(person, key).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var held = new Section($"{head}: beliefs on entry", entry == scene.Pov, [], beliefs.Select(kv => kv.Value.Id).ToList());
            held.Lines.AddRange(beliefs.Count == 0 ? ["  no recorded beliefs yet"] : beliefs.Select(kv => Line(kv.Key, kv.Value)));
            if (learns.Count > 0)
            {
                held.Lines.Add("  learns during this scene, as recorded:");
                held.Lines.AddRange(learns.Select(p => $"    {p}: {TruthPhrase(p)}"));
            }
            sections.Add(held);
        }

        // ---- relevant: since each person was last on stage. The last scene they
        // were in before this one in story time, and what they did, heard or came to believe in between, which is
        // mostly what happened off the page. Where that scene's prose is goes with the other pointers to prose,
        // which the freshness check leaves out, so that editing an earlier scene does not make this one stale.
        var since = new Section("SINCE EACH PERSON WAS LAST ON STAGE (in story time: what they did, heard or came to believe in between)", false, [], []);
        var lastSeen = new List<(string Name, string Scene)>();
        foreach (var (entry, person) in cast)
        {
            var name = s.Model.Entity(entry)!.Name;
            var last = s.Model.Scenes.Values
                .Where(x => x.Id != sceneId && s.Model.SceneTimes.TryGetValue(x.Id, out var t) && t.StoryKey < key
                            && x.Cast.Select(s.Model.Base).Contains(person))
                .OrderByDescending(x => s.Model.SceneTimes[x.Id].StoryKey).ThenByDescending(x => s.Tell(x.Id) ?? -1).ThenBy(x => x.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (last is null)
            {
                since.Lines.Add($"  {name}: on stage in no scene before this one in story time");
                continue;
            }
            var items = Recap.Items(s, person, s.SceneEnd(last.Id), key, truth: false);
            since.Lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: last on stage in {InScene(last.Id)}, at story time {s.Key(last.Id)}. Since then: {(items.Count == 0 ? "nothing recorded" : $"{items.Count} thing(s)")}"));
            since.Lines.AddRange(items.Select(i => $"    {Recap.At(i.Key)}, {(i.Scene is null ? "off the page" : InScene(i.Scene))}: {i.Text} ({i.Id}{Provisional(i.Id)})"));
            since.Records.AddRange(items.Select(i => i.Id));
            lastSeen.Add((name, last.Id));
        }
        if (since.Lines.Count > 0)
            sections.Add(since);

        // ---- relevant
        var revealed = s.RevealedHere(sceneId);
        if (revealed.Count > 0)
            sections.Add(new Section("REVEALED TO THE READER IN THIS SCENE, AS RECORDED", false, revealed.Select(p => $"  {p}: {TruthPhrase(p)}").ToList(), []));

        var reader = s.ReaderBefore(tell).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        sections.Add(new Section("THE READER ON ENTRY (in reading order)", false,
            reader.Count == 0 ? ["  nothing yet"] : reader.Select(kv =>
                $"  {kv.Key} [{Names.Of(kv.Value.Mode)}, {kv.Value.Scene}] reader {Stand(kv.Value.Stance, kv.Value.Strength)}: {Prop(kv.Key)} ({kv.Value.Id}{Provisional(kv.Value.Id)})").ToList(),
            reader.Select(kv => kv.Value.Id).ToList()));

        var world = new Section("WORLD ON ENTRY", false, [], []);
        foreach (var obj in s.TrackedObjects)
        {
            var l = s.Location(obj, key);
            var name = s.Model.Entity(obj)?.Name ?? obj;
            world.Lines.Add(l is null ? $"  {name}: no location recorded at this point"
                : l.Unresolved ? $"  {name}: unresolved; records {string.Join(" and ", l.Conflict!)} share a story time and disagree"
                : $"  {name}: {s.Model.Entity(l.Place!)?.Name ?? l.Place} ({l.Id}{Provisional(l.Id)})");
            if (l is { Unresolved: false })
                world.Records.Add(l.Id);
        }
        if (world.Lines.Count > 0)
            sections.Add(world);

        var prose = new Section(ProseTitle + " (open these files; the text is not copied here)", false, [], []);
        prose.Lines.Add($"  manuscript/{text.File} lines {text.LineStart}-{text.LineEnd}: this scene as it stands");
        if (s.Telling.FirstOrDefault(t => t.Tell == tell - 1) is { } previous)
            prose.Lines.Add($"  manuscript/{previous.File} lines {previous.LineStart}-{previous.LineEnd}: the scene before it in the telling ({previous.Id})");
        foreach (var (who, where) in lastSeen.GroupBy(l => l.Scene).Select(g => (string.Join(" and ", g.Select(l => l.Name)), g.Key)))
            if (s.Telling.FirstOrDefault(t => t.Id == where) is { } seen && seen.Tell != tell - 1)
                prose.Lines.Add($"  manuscript/{seen.File} lines {seen.LineStart}-{seen.LineEnd}: where {who} {(who.Contains(" and ", StringComparison.Ordinal) ? "were" : "was")} last on stage ({where})");
        sections.Add(prose);

        // ---- the hard constraints once more, by id, as the last thing before the manifest. What a model reads
        // last it keeps best, so the strongest instructions go there. That it helps here is not measured. It is
        // drawn from the parts above and adds nothing to them, so it is no part of what the manifest id stands for.
        var recap = new StringBuilder("\n\nKEEP TO THESE (the parts above, by id)");
        var notToReader = told.Select(t => t.Prop).Concat(embargoes.Select(e => e.Prop)).Distinct(StringComparer.Ordinal).ToList();
        recap.Append("\n  Not to be given to the reader: ").Append(notToReader.Count == 0 ? "nothing at this point" : string.Join(", ", notToReader));
        foreach (var (name, props) in unknownTo)
            recap.Append(CultureInfo.InvariantCulture, $"\n  Not known to {name}, so not said, told or acted on: {string.Join(", ", props)}");
        if (open.Count > 0)
            recap.Append("\n  Never settle: ").Append(string.Join(", ", open.Select(r => r.Prop ?? r.Id)));
        if (rules.Count > 0)
            recap.Append("\n  The author's rules and notes: ").Append(string.Join(", ", rules.Select(r => r.Rule.Id)));

        // ---- the header, and the parts as they are compared
        var place = scene.Place is { } p && s.Model.Entity(p) is { } e ? $" Place: {e.Name}." : "";
        var label = s.Model.SceneTimes[sceneId].Label is { } l2 ? $" ({l2})" : "";
        var people = cast.Select(c => c.CastEntry).ToHashSet(StringComparer.Ordinal);
        var tracked = s.Holders.ToHashSet(StringComparer.Ordinal);
        string CastEntry(string c)
        {
            var notes = new List<string> { c };
            if (c == scene.Pov)
                notes.Add("point of view");
            if (!people.Contains(c))
                notes.Add("not a person, so nothing is tracked for it");
            else if (!tracked.Contains(s.Model.Base(c)))
                notes.Add("no belief records");
            return $"{s.Model.Entity(c)?.Name ?? c} ({string.Join(", ", notes)})";
        }
        var castLine = string.Join("; ", new[] { scene.Pov }.Concat(scene.Cast.Where(c => c != scene.Pov)).Select(CastEntry));
        var header = string.Create(CultureInfo.InvariantCulture,
            $"CONTEXT FOR SCENE {tell}, {sceneId}{(scene.Title ?? text.Title) switch { null => "", var title => $" \"{title}\"" }}\nPoint of view: {pov.Name}.{place} Story time {key}{label}. What happens in this scene is dated from {key} up to, but not including, {end}.\nCast: {castLine}.\nEverything below is the state ON ENTRY to the scene, as of ledger head {s.Model.Head}. \"No record\" means nothing has been recorded either way; it does not mean \"does not know\".");

        // What the header says about the scene itself, kept as a part of its own so that it is compared with
        // the rest: a scene moved in story time, or given another cast, is not the scene the draft was for.
        var parts = new List<ContextSection>
        {
            new(SceneTitle, true,
            [
                string.Create(CultureInfo.InvariantCulture, $"story time {key}, span up to {end}"),
                $"point of view {scene.Pov}", $"place {scene.Place ?? "not given"}", $"cast {castLine}",
            ]),
        };
        parts.AddRange(sections.Select(x => new ContextSection(x.Title, x.Must, x.Lines)));
        return (header, sections, recap.ToString(), parts);
    }

    /// <summary>The map a session starts from: the scenes, how much is recorded, and what is awaiting review.</summary>
    /// <param name="heldProposals">How many proposals wait on the author now. They are not in the state, so the caller counts them.</param>
    /// <param name="work">Work in progress, already in words. Tasks are not in the state either.</param>
    public static string Brief(StoryState s, int heldProposals = 0, string work = "")
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"STORY BRIEF · ledger head {s.Model.Head} · manuscript {Short(s.ManuscriptHash)}\n");
        // A session reads the brief far more often than anyone runs verify, so this is where a lost end shows.
        if (s.LedgerWarning is { } warning)
            sb.Append("WARNING: ").Append(warning).Append(". State may be missing. Tell the author before relying on it.\n");
        sb.Append("\nScenes in the order they are told\n");
        double? previous = null;
        foreach (var t in s.Telling)
        {
            var record = s.Model.Scenes.GetValueOrDefault(t.Id);
            var time = s.Model.SceneTimes.GetValueOrDefault(t.Id);
            sb.Append(CultureInfo.InvariantCulture, $"  {t.Tell,2}  {t.Id}  {record?.Title ?? t.Title ?? "(untitled)"}");
            if (record is null)
                sb.Append(" · NO SCENE RECORD");
            else
                sb.Append(CultureInfo.InvariantCulture, $" · POV {s.Short(record.Pov)}");
            if (time is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" · story time {time.StoryKey}{(time.Label is null ? "" : $" ({time.Label})")}");
                if (previous is double before && time.StoryKey < before)
                    sb.Append(" · told out of story order");
                previous = time.StoryKey;
            }
            sb.Append('\n');
        }
        // What a session was in the middle of comes straight after the map: it is the first thing to act on.
        sb.Append(work);
        var live = s.Model.Records;
        var provisional = live.Count(t => t.ApprovalAt(s.Model.Head) == Approval.Provisional);
        sb.Append(CultureInfo.InvariantCulture, $"\nRecords: {live.Count} in force: {s.Model.Propositions.Count} propositions, {s.Model.Beliefs.Count} belief changes, {s.Model.Disclosures.Count} disclosures, {s.Model.Events.Count} events, {s.Model.Assertions.Count} assertions.\n");
        if (provisional == 0 && heldProposals == 0)
            sb.Append("Nothing is awaiting review.\n");
        else
        {
            sb.Append("Awaiting review:");
            if (provisional > 0)
                sb.Append(CultureInfo.InvariantCulture, $" {provisional} provisional record(s). Treat them as unconfirmed.");
            if (heldProposals > 0)
                sb.Append(CultureInfo.InvariantCulture, $" {heldProposals} proposal(s) held for the author's decision. They are not part of the story unless the author accepts them.");
            sb.Append(" The review queue lists them. Only the author decides.\n");
        }
        foreach (var r in s.Model.Rules.Where(r => r.Type is "never_resolve" or "never_establish"))
            sb.Append(CultureInfo.InvariantCulture, $"Never settle {r.Prop}: {r.Text}\n");
        var word = s.Model.Rules.Where(r => !SceneKnowledge.NeverSettle(r)).ToList();
        if (word.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"The author's rules and notes: {word.Count(r => r.Type != "guidance")} rule(s) and {word.Count(r => r.Type == "guidance")} note(s), each shown in the context of every scene it holds in.\n");
        foreach (var x in s.Model.Secrets)
            sb.Append(CultureInfo.InvariantCulture, $"Secret {x.Id}: only {string.Join(", ", x.KnownOnlyTo.Select(s.Short))} ever learn {string.Join(", ", x.Props)}.\n");
        foreach (var x in s.Model.Embargoes.OrderBy(x => s.Tell(x.UntilScene) ?? int.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"Embargo {x.Id}: {x.Prop} is not to be stated to the reader before {x.UntilScene}{(s.Tell(x.UntilScene) is null ? ", which is not in the manuscript yet" : "")}.\n");
        sb.Append("\nAsk scene_context for a scene before drafting or revising it, and state_at for one person, fact or object at a point.\n");
        return sb.ToString();
    }

    private static string Stand(Stance? stance, Strength? strength) =>
        stance is null ? "records tie and disagree"
        : stance.Value switch
        {
            Stance.HoldsTrue => "takes it as true",
            Stance.HoldsFalse => "takes it as false",
            _ => Names.Of(stance.Value),
        } + (strength is { } st ? ", " + Names.Of(st) : "");

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
