using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>Thrown when even the parts a context must contain do not fit the budget. Nothing is ever truncated.</summary>
public sealed class BudgetException(string message) : Exception(message);

/// <summary>One titled part of a context. <see cref="Must"/> parts are never left out to fit a budget.</summary>
/// <param name="Facts">For each line, the fact it is about, or null when it is about none. Left out when nobody said.</param>
public sealed record ContextSection(string Title, bool Must, IReadOnlyList<string> Lines, IReadOnlyList<string?>? Facts = null);

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
public static partial class ContextCompiler
{
    public const int DefaultBudget = 10_000;

    /// <summary>The start of the title of the part that points at the prose. Drafting the scene is what changes it.</summary>
    public const string ProseTitle = "PROSE TO READ";

    /// <summary>
    /// What marks a record the author has not reviewed, in the lines two contexts are compared by. It is left
    /// out of the comparison, since ratifying a record changes nothing a draft relied on.
    /// </summary>
    public const string ProvisionalMark = ", PROVISIONAL: not yet reviewed";

    /// <summary>
    /// The same mark as it is printed: one character after the record's id, explained once at the head of the
    /// text. A long story's context names hundreds of records, and the words on every one of them were a tenth
    /// of the whole.
    /// </summary>
    public const string ProvisionalSign = "*";

    public const string ProvisionalEmbargoMark = ", provisional but keep to it";

    /// <summary>
    /// The mark of a fact that rests on a record nobody has confirmed, with the records it names. A context
    /// prints the mark without them: state_at names them for any fact that is asked about.
    /// </summary>
    [GeneratedRegex(@" \[UNCONFIRMED: rests on provisional [^\]\n]*\]")]
    private static partial Regex UnconfirmedBy();

    /// <summary>The start of the title of the part that says what the scene itself is. It is the header, kept as a part so that it is compared.</summary>
    public const string SceneTitle = "SCENE";

    /// <summary>The ledger head a manifest id was compiled at, or null if the text is not a manifest id.</summary>
    public static int? HeadOf(string? manifestId) => ManifestId.Parse(manifestId)?.Head;

    /// <summary>A mark of the order the scenes are told in. Moving, adding or removing a scene changes it; editing a scene's text does not.</summary>
    public static string OrderMark(StoryState s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", s.Telling.Select(t => t.Id)))))[..6];

    /// <summary>A rough size guide, characters over 3.6. Not a token count.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.6);

    /// <summary>
    /// One line of a part. A line about a fact is printed with the fact's wording the first time the fact comes up
    /// in the text, and by its id after that: late in a long story five people and the reader hold views on the
    /// same hundred facts, and saying each fact once is most of what keeps a context to its budget.
    /// </summary>
    /// <param name="Lead">The line itself when it is about no fact; otherwise what is printed before the fact's id.</param>
    /// <param name="Fact">The fact the line is about.</param>
    /// <param name="Wording">The fact in words, as this line would put it.</param>
    /// <param name="Tail">What follows: the record the line rests on, when they learn it.</param>
    /// <param name="Joins">Lines that name their fact by id alone are run together on one line with their neighbours.</param>
    /// <param name="Bare">Never worded here: the line only places a fact that is worded where it matters, or that nobody present has.</param>
    /// <param name="Compared">
    /// The line as two contexts are compared by it: whole, with its label and its wording, however it is printed.
    /// Null for a heading that only arranges the lines under it.
    /// </param>
    /// <param name="Always">Worded every time: a line that says someone has a fact wrong has to say what is so, there.</param>
    /// <param name="ById">
    /// What follows the fact's id when it is run together with its neighbours, if that is less than the tail. A
    /// record's id is worth giving where the fact is worded; in a list of forty ids it is forty more ids, and
    /// state_at shows the record for any pair that is asked about.
    /// </param>
    /// <param name="About">For a line that is printed as it is: the fact it is about, if it is about one.</param>
    private sealed record Item(
        string Lead, string? Fact, string Wording, string Tail, bool Joins, bool Bare, string? Compared, bool Always = false, string? ById = null,
        string? About = null);

    private sealed class Section(string title, bool must)
    {
        public string Title { get; } = title;

        public bool Must { get; } = must;

        public List<Item> Items { get; } = [];

        public List<string> Records { get; } = [];

        private string? _alone;

        /// <summary>A line printed and compared as it is. It may still be about a fact, and then says which.</summary>
        public void Line(string text, string? about = null) => Items.Add(new Item(text, null, "", "", false, false, text, About: about));

        /// <summary>A heading over the lines that follow. It arranges them and says nothing of its own, so it is not compared.</summary>
        public void Heading(string text) => Items.Add(new Item(text, null, "", "", false, false, null));

        /// <summary>
        /// A fact named by id and no more, in a run with its neighbours. What it says follows from lines that are
        /// compared elsewhere, so it is not compared itself.
        /// </summary>
        public void Mention(string lead, string fact, string tail = "") => Items.Add(new Item(lead, fact, "", tail, true, true, null));

        public void Fact(string lead, string fact, string wording, string tail, string compared, bool joins = false, bool bare = false,
            bool always = false, string? byId = null) =>
            Items.Add(new Item(lead, fact, wording, tail, joins, bare, compared, always, byId));

        /// <summary>The part as it reads on its own, every fact worded. This is its size when it is left out, and when the largest parts are named.</summary>
        public string Alone => _alone ??= Print(new StringBuilder(), new HashSet<string>(StringComparer.Ordinal)).ToString();

        /// <summary>The lines two contexts are compared by, each with the fact it is about.</summary>
        public ContextSection Part => new(Title, Must, Items.Where(i => i.Compared is not null).Select(i => i.Compared!).ToList(),
            Items.Where(i => i.Compared is not null).Select(i => i.Fact ?? i.About).ToList());

        /// <summary>
        /// Print the part after whatever has been printed already: a fact that has been worded is named by id.
        /// Within a run of lines that join, the worded ones come first, one to a line, and the rest follow on one.
        /// </summary>
        public StringBuilder Print(StringBuilder sb, HashSet<string> worded)
        {
            sb.Append(Title);
            for (var i = 0; i < Items.Count;)
            {
                var item = Items[i];
                if (item.Fact is null)
                {
                    sb.Append('\n').Append(item.Lead);
                    i++;
                }
                else if (!item.Joins)
                {
                    sb.Append('\n').Append(item.Lead).Append(item.Fact);
                    if (!item.Bare && (worded.Add(item.Fact) || item.Always))
                        sb.Append(": ").Append(item.Wording);
                    sb.Append(item.Tail);
                    i++;
                }
                else
                {
                    var byId = new List<string>();
                    for (; i < Items.Count && Items[i] is { Fact: not null, Joins: true } next && next.Lead == item.Lead; i++)
                    {
                        if (!next.Bare && worded.Add(next.Fact))
                            sb.Append('\n').Append(next.Lead).Append(next.Fact).Append(": ").Append(next.Wording).Append(next.Tail);
                        else
                            byId.Add(next.Fact + (next.ById ?? next.Tail));
                    }
                    if (byId.Count > 0)
                        sb.Append('\n').Append(item.Lead).AppendJoin("; ", byId);
                }
            }
            return sb;
        }
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
        // What a part costs depends on the parts before it, which may already have worded its facts.
        ContextPackage Assemble(bool[] use)
        {
            var omitted = sections.Where((_, i) => !use[i]).Select(x => $"{x.Title} (~{EstimateTokens(x.Alone) + 1} tokens)").ToList();
            var body = new StringBuilder(header);
            var worded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in sections.Where((_, i) => use[i]))
                section.Print(body.Append("\n\n"), worded);
            if (omitted.Count > 0)
                body.Append("\n\nOMITTED TO FIT THE BUDGET. To read one in full: scene_context with part set to the start of its title\n").Append(string.Join("\n", omitted.Select(o => "  " + o)));
            body.Append(recap);
            var records = sections.Where((_, i) => use[i]).SelectMany(x => x.Records).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
            var final = body.Append($"\n\nManifest {manifest} · manuscript {Short(s.ManuscriptHash)}\nGive this manifest id back when you submit the scene: it says which state the draft was written from.")
                .Replace(ProvisionalMark, ProvisionalSign).ToString();
            final = UnconfirmedBy().Replace(final, " [UNCONFIRMED]") + tail;
            return new ContextPackage(sceneId, s.Model.Head, s.ManuscriptHash, manifest, EstimateTokens(final), budget, final, omitted, records, parts);
        }

        var use = sections.Select(_ => true).ToArray();
        var package = Assemble(use);
        if (package.EstimatedTokens <= budget)
            return package;
        // A part is in whole or out whole, and one that is out is named. Often the text is over by a little, and
        // then the least is given up: the smallest single part whose absence is enough.
        foreach (var i in Enumerable.Range(0, sections.Count).Where(i => !sections[i].Must).OrderBy(i => sections[i].Alone.Length).ThenByDescending(i => i))
        {
            use[i] = false;
            var without = Assemble(use);
            if (without.EstimatedTokens <= budget)
                return without;
            use[i] = true;
        }
        // No one part is enough. The parts that can be left out are in the order they matter to a draft, so
        // they go from the end: what is so elsewhere, then the reader, then where things are, and the people
        // present last.
        var given = new List<int>();
        for (var i = sections.Count - 1; i >= 0 && package.EstimatedTokens > budget; i--)
        {
            if (sections[i].Must)
                continue;
            use[i] = false;
            given.Add(i);
            package = Assemble(use);
        }
        if (package.EstimatedTokens > budget)
        {
            var must = sections.Where(x => x.Must).ToList();
            throw new BudgetException(
                $"BUDGET_INSUFFICIENT: the parts that must be in the context for {sceneId} need about {package.EstimatedTokens} tokens and the budget is {budget}. " +
                "Largest: " + string.Join("; ", must.OrderByDescending(x => EstimateTokens(x.Alone)).Take(3).Select(x => $"{x.Title.Split(':')[0]} ~{EstimateTokens(x.Alone)}")) +
                (tail.Length > 0 ? $". About {EstimateTokens(tail)} of that is the closing words on the scene's task, which a context taken to work from always carries" : "") +
                ". Raise the budget; nothing is ever cut to fit.");
        }
        // A small part given up on the way to a large one may fit after all. Each goes back if the finished
        // text with it still fits, the ones that matter more first.
        foreach (var i in given.Order())
        {
            use[i] = true;
            var with = Assemble(use);
            if (with.EstimatedTokens <= budget)
                package = with;
            else
                use[i] = false;
        }
        return package;
    }

    /// <summary>
    /// One part of a scene's context by itself and in full, every fact in it worded, whatever a budget would have
    /// left out. It is asked for by the start of its title, and every part whose title starts so is given. This
    /// is how a part named as omitted is read: the alternative was the whole state on entry, to find a few lines.
    /// </summary>
    public static string PartText(StoryState s, string sceneId, string title)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var sections = Gather(s, sceneId).Sections;
        var found = sections.Where(x => x.Title.StartsWith(title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
            throw new KeyNotFoundException($"The context for {sceneId} has no part whose title starts \"{title.Trim()}\". Its parts: {string.Join("; ", sections.Select(x => x.Title))}");
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"FROM THE CONTEXT FOR {sceneId}, in full · the state on entry, as of ledger head {s.Model.Head}\n\n")
            .AppendJoin("\n\n", found.Select(x => x.Alone)).Replace(ProvisionalMark, ProvisionalSign).ToString();
        return UnconfirmedBy().Replace(text, " [UNCONFIRMED]");
    }

    /// <summary>
    /// What a context for a scene says, part by part: everything in <see cref="ContextPackage.Sections"/>, with
    /// no text put together and no budget applied. Two contexts are compared by these parts alone, and a what-if
    /// compares two for every scene of the story and prints none of them.
    /// </summary>
    internal static IReadOnlyList<ContextSection> Sections(StoryState s, string sceneId) => Gather(s, sceneId).Parts;

    /// <summary>
    /// How the text is to be read, said once at its head. The rule about silence used to be written out on a line
    /// of its own for every person present and every fact they had no record on, which late in a long story was
    /// most of the text. It is one rule, and the lines under each person are the exceptions to it.
    /// </summary>
    public const string Reading = """
        HOW TO READ THIS
          Whose knowledge it is: a fact is the author's until a line gives it to someone. On entry a person can say or act on a fact only as the lines under their own name allow. The point-of-view character tells the scene, so the reader is given nothing that character does not have by that moment of it.
          Not listed means NO RECORD: where a fact is not under someone's name, nothing has been recorded either way. Do not write them as knowing it, and do not write that they do not. If the scene needs it settled, look the pair up; if nothing is there, leave it open and tell the author.
          Three things that are listed are different from that. UNAWARE: the story shows that they do not know. BELIEVES THE OPPOSITE: they hold the wrong view, and act on it. HEARD IT SAID: they were there for the words and can repeat them, and nothing records what they made of them. Each makes a different scene.
          What the scene may change: someone here who has a fact can tell it or show it, and whoever takes it in has it from then on and can answer it. That needs no plan. When the scene is submitted, record who said what to whom and what each hearer made of it; it goes to the author to keep or reject.
          What someone works out is theirs: anyone here may reason from what they have to a view of something they lack, and may be wrong. Write it as their reasoning, on the grounds they have. The narration does not confirm it. Record it as inferred or assumed.
          What it may not: give a fact to someone a secret shuts out of it, or state to the reader a fact listed as not to be stated. A hint is not a statement: the reader may be left suspecting, and neither the narration nor anyone speaking may leave them sure.
          A fact is worded the first time it comes up, and again where it must not be stated and wherever someone has it wrong or is shown not to know it. Elsewhere it is named by its id.
          * after an id: PROVISIONAL, a record the author has not reviewed yet. UNCONFIRMED: whether the fact is so rests on such a record. Both are the story as it stands: write on from them. What a later scene takes from one is tracked, and if the author rejects it they are shown the passages that rested on it.
          To see the passage behind a line, or what anyone has on any fact: state_at with the scene, the holder and the prop.
        """;

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
        // The same fact is phrased for each person it is listed under, so the words are kept for the scene.
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
        string InScene(string id) => s.Tell(id) is int n ? $"scene {n}, {id}" : $"{id}, which is not in the manuscript yet";
        string CastOnEntry(string prop) => "      cast on entry: " + string.Join("; ",
            cast.Select(c => $"{s.Short(c.Person)}: {s.Classify(s.BeliefBefore(c.Person, prop, key), key)}"));

        // ---- must: what the scene is for. The contract is the author's statement of what it should do and
        // what it relies on; each thing relied on is shown with whether the records bear it out on entry.
        var planned = new HashSet<string>(StringComparer.Ordinal);
        if (s.Contract(sceneId) is not { } contract)
        {
            // A scene nobody has planned is still one to write. What it turns out to do is found in the writing,
            // and the author sees it afterwards: saying so keeps a drafter from reading the lists below as a fence.
            var unplanned = new Section("NO CONTRACT FOR THIS SCENE", true);
            unplanned.Line("  Nothing is planned for it. Whatever it changes in what people know, in what the reader is given or in where things are is recorded when the scene is submitted, and goes to the author to keep or reject.");
            sections.Add(unplanned);
        }
        else
        {
            var agreed = new Section($"CONTRACT FOR THIS SCENE ({contract.Id}{Provisional(contract.Id)}): what it is meant to do", true);
            agreed.Records.Add(contract.Id);
            if (!string.IsNullOrWhiteSpace(contract.Outline))
                agreed.Line("  outline: " + contract.Outline.Trim());
            foreach (var intent in contract.Intends)
                agreed.Line("  intends: " + SceneContracts.Describe(s, intent));
            foreach (var (need, status) in SceneContracts.Preconditions(s, sceneId, contract))
                agreed.Line($"  relies on: {SceneContracts.Describe(s, need, onEntry: true)}. {(status.Code is null ? "" : "NOT BORNE OUT ON ENTRY. ")}{status.Detail}");
            if (agreed.Items.Count == 0)
                agreed.Line("  nothing is planned or relied on");
            // The plan is written in ids. The facts it names are what the scene is for, so they are worded
            // here, with what is so, and not left to be found wherever else they come up.
            var named = contract.Intends.Concat(contract.Relies).Select(i => i.Prop).OfType<string>()
                .Distinct(StringComparer.Ordinal).Where(prop => s.Model.Proposition(prop) is not null).ToList();
            if (named.Count > 0)
                agreed.Heading("  the facts it names:");
            foreach (var prop in named)
                agreed.Fact("    ", prop, TruthPhrase(prop), "", $"    {prop}: {TruthPhrase(prop)}");
            planned.UnionWith(named);
            agreed.Line("  Anything else of weight that the scene does to what people know, to what the reader is told or to where things are is not forbidden. It is reported to the author as unplanned.");
            sections.Add(agreed);
        }

        // ---- must: what the scene is already recorded as doing. A new draft of a scene that has been
        // submitted before has to know this: each of these it keeps, by leaving it alone, or withdraws. It is
        // also what makes a second draft stale once a first has been applied.
        var recorded = new Section("ALREADY RECORDED AS HAPPENING IN THIS SCENE (a new draft keeps each of these, or withdraws it)", true);
        var done = new List<string>();
        foreach (var happened in s.Model.EventsIn(sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var who = happened.Participants.Count == 0 ? "" : " [" + string.Join(", ", happened.Participants.Select(x => $"{s.Short(x.Key)} {Names.Of(x.Value)}")) + "]";
            recorded.Line(string.Create(CultureInfo.InvariantCulture, $"  {happened.Id} at {happened.StoryKey}, event: {happened.Summary}{who}{Provisional(happened.Id)}"));
            recorded.Records.Add(happened.Id);
        }
        foreach (var said in s.Model.AssertionsIn(sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var to = said.Addressees.Count == 0 ? "" : " to " + string.Join(", ", said.Addressees.Select(s.Short));
            recorded.Line(string.Create(CultureInfo.InvariantCulture,
                $"  {said.Id} at {said.StoryKey}, {Names.Of(said.Type)}: {s.Short(said.Speaker)} {(said.Polarity ? "asserts" : "denies")} {said.Prop}{to} [{s.ClassifyAssertion(said.Id)}]{Provisional(said.Id)}"));
            recorded.Records.Add(said.Id);
            done.Add(said.Prop);
        }
        foreach (var change in s.RecordedChanges(sceneId))
        {
            // A fact that begins or stops being so in the scene's span is dated: whether it is already so when
            // someone speaks is exactly what a draft needs, and "in this scene" does not say.
            var when = s.Model.ById.TryGetValue(change.RecordId, out var row) && row.Record is FactChange dated
                ? string.Create(CultureInfo.InvariantCulture, $" at {dated.StoryKey}") : "";
            recorded.Line($"  {change.RecordId}{when}: {change.Text}{Provisional(change.RecordId)}");
            recorded.Records.Add(change.RecordId);
            if (change.Shape.Prop is { } changed)
                done.Add(changed);
        }
        if (recorded.Items.Count == 0)
            recorded.Line("  nothing yet");
        // These lines are written in ids, like the plan. The facts they name are this scene's own, so they are
        // worded here and not left to be found wherever else they come up, or in a part the budget leaves out.
        var own = done.Distinct(StringComparer.Ordinal).Where(prop => s.Model.Proposition(prop) is not null).ToList();
        if (own.Count > 0)
            recorded.Heading("  the facts these name:");
        foreach (var prop in own)
            recorded.Fact("    ", prop, TruthPhrase(prop), "", $"    {prop}: {TruthPhrase(prop)}");
        planned.UnionWith(own);
        sections.Add(recorded);
        // Where the part on what is in question goes, once the parts after this one have said which records they list.
        var doubtAt = sections.Count;

        // ---- must: the author's word on this scene. A note is their understanding of a person or a stretch of
        // the story, in prose, scoped to where it holds. Notes like these, with their stages marked, are what a
        // drafter needs to write a person rather than a set of facts.
        var rules = s.RulesInScope(sceneId);
        if (rules.Count > 0)
        {
            var word = new Section("THE AUTHOR'S RULES AND NOTES FOR THIS SCENE (a note is the author's understanding, to write from; a rule is to keep to)", true);
            foreach (var (rule, caveat) in rules)
            {
                var kind = rule.Type == "guidance" ? "note" : $"rule ({rule.Type})";
                word.Line($"  {rule.Id}{Provisional(rule.Id)}, {kind}{(rule.Prop is null ? "" : " on " + rule.Prop)}{Where(rule.Scope)}: {rule.Text}"
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
        var withheld = new Section("MUST NOT BE STATED TO THE READER IN THIS SCENE (the reader is first told later). A hint is allowed: the reader may be left suspecting it. Neither the narration nor anyone speaking may leave them sure of it", true);
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
        // What the reader has had of a withheld fact so far, in the words that gave it: the hints a draft must
        // neither undo nor turn into a statement. Without them a drafter knows what to keep back and not what
        // has already been let slip on purpose.
        void SoFar(string prop)
        {
            var seeds = s.Model.DisclosuresOf(prop).Where(d => s.Tell(d.Scene) is int at && at < tell)
                .OrderBy(d => s.Tell(d.Scene)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
            if (seeds.Count == 0)
                return;
            withheld.Line("      the reader so far: " + string.Join("; ", seeds.Select(d =>
                $"[{Names.Of(d.Mode)}, {d.Scene}]{(d.Quote is { } quote ? $" \"{Plain.OneLine(QuoteMatcher.Normalize(quote))}\"" : "")} ({d.Id}{Provisional(d.Id)})")));
            withheld.Records.AddRange(seeds.Select(d => d.Id));
        }
        void Withhold(string prop, string why)
        {
            // Worded here whatever has worded it before. This is the list a draft may least afford to misread,
            // and a bare id in it sends the reader looking for the fact it is meant to keep them off.
            withheld.Fact("  ", prop, TruthPhrase(prop), why, $"  {prop}: {TruthPhrase(prop)}{why}", always: true);
            withheld.Line(CastOnEntry(prop));
            SoFar(prop);
        }
        var told = s.MustNotReveal(sceneId);
        var toldLater = told.Select(t => t.Prop).ToHashSet(StringComparer.Ordinal);
        foreach (var (prop, reveal) in told)
        {
            var embargo = Embargoed(prop);
            Withhold(prop, $" (first told in {InScene(reveal)}{(embargo.Length > 0 ? "; " + embargo : "")})");
        }
        foreach (var prop in embargoes.Select(e => e.Prop).Distinct().Where(p => !toldLater.Contains(p)))
            Withhold(prop, $" ({Embargoed(prop)})");
        if (withheld.Items.Count == 0)
            withheld.Line("  nothing is being withheld from the reader at this point");
        sections.Add(withheld);

        var open = s.Model.Rules.Where(r => r.Type is "never_resolve" or "never_establish").ToList();
        if (open.Count > 0)
        {
            var never = new Section("NEVER SETTLE THESE", true);
            foreach (var r in open)
                never.Line($"  {r.Prop}: {r.Text}");
            never.Records.AddRange(open.Select(r => r.Id));
            sections.Add(never);
        }

        // Each person present is asked once what they make of the facts in play, and what they hold on entry.
        var minds = cast.Select(c => (Entry: c.CastEntry, c.Person, Know: s.Knowledge(c.Person, sceneId),
            Beliefs: s.BeliefsBefore(c.Person, key).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())).ToList();

        // ---- must: what is so and is nobody's here. A fact in play that no one present has any record on is
        // under nobody's name, and the rule about silence covers every one of them. It is still so: a scene that
        // contradicted it would be wrong, and a draft can keep only to what it is shown. Such a fact used to be
        // said once for each person present. It is said once.
        var notToReader = told.Select(t => t.Prop).Concat(embargoes.Select(e => e.Prop)).Distinct(StringComparer.Ordinal).ToList();
        var withheldFacts = notToReader.ToHashSet(StringComparer.Ordinal);
        // Someone a secret shuts out, with no record of their own, has nothing on the fact either: the author's
        // word that they never learn it is not something the story shows of them.
        bool ShutOut(string person, Gap g) => g.Status == KnowledgeStatus.Unaware && s.BeliefBefore(person, g.Prop, end) is null;
        var silences = minds.Select(m => m.Know.Gaps.Where(g => g.Status == KnowledgeStatus.NoRecord || ShutOut(m.Person, g))
            .Select(g => g.Prop).ToHashSet(StringComparer.Ordinal)).ToList();
        var nobodys = s.InPlay(sceneId).Where(prop => !withheldFacts.Contains(prop) && silences.All(x => x.Contains(prop))).ToList();
        // Which of them touch the scene: a fact about someone in it, its place or a thing that is there, or one that
        // does not say whom it is about. The rest are about people and things elsewhere. A draft is least likely to
        // contradict those, and in a story of threads that never meet they are most of the story's facts, so they
        // go in a part of their own that is the last to be taken and the first to be left out, by name.
        var here = scene.Cast.Concat([scene.Pov]).SelectMany(c => new[] { c, s.Model.Base(c) }).ToHashSet(StringComparer.Ordinal);
        if (scene.Place is { } at)
            here.Add(at);
        foreach (var thing in s.TrackedObjects)
            if (s.Location(thing, key) is { Unresolved: false, Place: { } kept } && here.Contains(kept))
                here.Add(thing);
        // A fact that began or stopped being so since the teller was last on stage, or does within this scene, is
        // news the scene stands on, whoever it is about: the people in it are likely to be asking about exactly
        // that. It is kept with what must be there, and says from when.
        var window = s.LastOnStage(s.Model.Base(scene.Pov), sceneId) is { } lately ? s.Key(lately.Id) : key;
        string Lately(string prop) => s.Model.Facts(prop).Where(f => f.StoryKey >= window && f.StoryKey < end).OrderBy(f => f.StoryKey).LastOrDefault() is { } turned
            ? string.Create(CultureInfo.InvariantCulture, $" ({(turned.Value ? "so" : "no longer so")} from story time {turned.StoryKey}{(turned.StoryKey >= key ? ", which is within this scene" : "")})")
            : "";
        bool Touches(string prop) =>
            planned.Contains(prop) || Lately(prop).Length > 0 || s.Model.Proposition(prop) is not { } fact
            || (fact.About.Count == 0 ? fact.Nested is not { } nested || here.Contains(s.Model.Base(nested.Holder))
                : fact.About.Any(a => here.Contains(a) || here.Contains(s.Model.Base(a))));
        var elsewhere = new Section("ALSO SO, WITH NO RECORD HERE, AND ABOUT NOBODY AND NOTHING IN THIS SCENE (the author's alone)", false);
        if (nobodys.Count > 0)
        {
            var authors = new Section("SO, AND NOBODY IN THIS SCENE HAS ANY RECORD ON IT (the author's alone. Nobody here speaks or acts from knowing it; a guess at it, right or wrong, is the guesser's own. Nothing the narration gives as so may contradict it)", true);
            foreach (var prop in nobodys)
                (Touches(prop) ? authors : elsewhere).Fact("  ", prop, TruthPhrase(prop), Lately(prop), $"  {prop}: {TruthPhrase(prop)}{Lately(prop)}", always: s.Canon(prop) == "false");
            if (authors.Items.Count > 0)
                sections.Add(authors);
        }

        // ---- must: where each cast member stands apart from what is so; and the point-of-view character's beliefs.
        // All of it is the state on entry. None of it says what the scene may not do: a person who does not have
        // a fact when the scene opens is exactly the person a scene can give it to.
        var unknownTo = new List<(string Name, List<string> Wrong, List<string> Open, List<string> NotTheirs)>();
        // The scene the teller was last in, and what they came by there. Someone here who was not in that scene
        // has had no chance to come by it with them, and it is the freshest thing a draft will reach for.
        var teller = s.Model.Base(scene.Pov);
        var before = s.LastOnStage(teller, sceneId);
        var thereBefore = before?.Cast.Select(s.Model.Base).ToHashSet(StringComparer.Ordinal) ?? [];
        var cameBy = before is null ? [] : s.Model.BeliefsIn(before.Id)
            .Where(b => b.Holder == teller && b.Stance is Stance.HoldsTrue or Stance.HoldsFalse).Select(b => b.Prop).Distinct(StringComparer.Ordinal).ToList();
        foreach (var (entry, person, (personGaps, learns), beliefs) in minds)
        {
            var head = s.Model.Entity(entry)!.Name + (entry == scene.Pov ? " (point of view)" : "");
            if (entry != person)
                head += $"  [AUTHOR ONLY: this is {s.Model.Entity(person)!.Name}. Do not reveal it.]";
            var gaps = new Section($"{head}: NOT theirs on entry. They do not say it or act on it until the scene gives it to them", true);
            var listed = new List<string>();
            var heard = s.HeardOnly(person, key);
            string When(Gap gap) =>
                gap.LearnsIn is { } later ? $"learns it in {InScene(later)}"
                // Learned in no scene is still learned. Saying "never" here would be wrong.
                : gap.LearnsAt is double at ? string.Create(CultureInfo.InvariantCulture, $"learns it later, off the page, at story time {at}")
                // Someone recorded as undecided has the question in mind and has not answered it. "Treat as not
                // knowing" kept a draft from letting them come down on a side, which is what a scene is for.
                : gap.Status == KnowledgeStatus.Unsettled
                    ? s.BeliefBefore(person, gap.Prop, end) is { Unresolved: false, Stance: Stance.Undecided } ? "they have not made up their mind, and the scene may move them" : "treat as not knowing"
                : s.Sealed(person, gap.Prop) is { } secret ? $"never learns it: {secret.Id} keeps it to {string.Join(", ", secret.KnownOnlyTo.Select(s.Short))}"
                // What follows is what the records hold, not a ruling on the scene: said as a forecast ("never
                // learns it") it reads as one, and a drafter keeps a person from being told what nothing forbids.
                : gap.Status == KnowledgeStatus.Wrong ? "nothing recorded later sets them right"
                : "nothing recorded later gives it to them";
            // The kinds are kept apart because each makes a different scene: someone who holds the wrong view
            // acts on it; someone the story shows not to know can be shown not knowing; someone who learns it
            // later must simply not have it yet. Silence that never ends is none of these and is not listed:
            // most facts in play late in a long story are ones a person present has no record on and never
            // comes to know, and the rule for them is said once, at the head of the text.
            void Kind(string heading, Func<Gap, bool> which, Func<Gap, string> why, Func<Gap, string> compared, bool bare = false)
            {
                var these = personGaps.Where(which).ToList();
                if (these.Count == 0)
                    return;
                gaps.Heading(heading);
                foreach (var gap in these)
                {
                    gaps.Fact("    ", gap.Prop, TruthPhrase(gap.Prop), $" ({why(gap)})", $"  {gap.Prop}: {TruthPhrase(gap.Prop)} ({compared(gap)})", joins: bare, bare: bare, always: !bare);
                    listed.Add(gap.Prop);
                }
            }
            Kind("  BELIEVES THE OPPOSITE of what is so, and acts on it:", g => g.Status == KnowledgeStatus.Wrong,
                When, g => When(g) + "; currently believes the opposite");
            Kind("  UNAWARE: the story shows that they do not know it:", g => g.Status == KnowledgeStatus.Unaware && !ShutOut(person, g), When, When);
            Kind("  NOT SETTLED what they make of it:", g => g.Status == KnowledgeStatus.Unsettled,
                g => Unsettled(person, g.Prop) + "; " + When(g), g => Unsettled(person, g.Prop) + "; " + When(g));
            // A secret is the author's word, not something the text shows of this person, and it shuts out
            // everyone it does not name. It is one line for each of them, and the facts are worded once: under
            // whoever holds them, or as the author's alone.
            Kind("  SHUT OUT BY A SECRET the author keeps to the people named. The scene cannot give it to them:", g => ShutOut(person, g),
                g => s.Sealed(person, g.Prop) is { } secret ? $"{secret.Id}, kept to {string.Join(", ", secret.KnownOnlyTo.Select(s.Short))}" : When(g), When, bare: true);
            Kind("  NO RECORD ON ENTRY, and the story as recorded gives it to them later, in the scene named:",
                g => g is { Status: KnowledgeStatus.NoRecord, LearnsAt: not null },
                g => g.LearnsIn is { } later ? InScene(later) : string.Create(CultureInfo.InvariantCulture, $"off the page, at story time {g.LearnsAt}"), When, bare: true);
            // The scene is told through one person, so that person's silences are the ones a draft trips on:
            // a fact someone else in the room holds, worded under their name, is one sentence away from being
            // narrated. Those are named here, each with who has it: that is who can give it to them in the
            // scene. For everyone else the rule at the head of the text is enough, and naming theirs would bring
            // back a line for every person and every fact.
            if (entry == scene.Pov)
            {
                var holders = minds.Where(m => m.Person != person)
                    .SelectMany(m => m.Beliefs.Where(kv => kv.Value.Stance is Stance.HoldsTrue or Stance.HoldsFalse).Select(kv => (Prop: kv.Key, m.Person)))
                    .ToLookup(x => x.Prop, x => s.Short(x.Person), StringComparer.Ordinal);
                var blind = personGaps.Where(g => g is { Status: KnowledgeStatus.NoRecord, LearnsAt: null } && holders.Contains(g.Prop) && heard.All(said => said.Prop != g.Prop))
                    .Select(g => g.Prop).ToList();
                if (blind.Count > 0)
                {
                    gaps.Heading("  NO RECORD ON ENTRY, and someone else here has it:");
                    foreach (var prop in blind)
                        gaps.Mention("    ", prop, $" ({string.Join(", ", holders[prop])})");
                    listed.AddRange(blind);
                }
            }
            // Someone here who was not in the scene the teller was last in, set against what the teller came by
            // there. They were not there for it, so they need it from someone before they can speak of it.
            else if (before is not null && !thereBefore.Contains(person))
            {
                var have = beliefs.Select(kv => kv.Key).Concat(heard.Select(a => a.Prop)).Concat(listed).ToHashSet(StringComparer.Ordinal);
                var missed = cameBy.Where(prop => !have.Contains(prop)).ToList();
                if (missed.Count > 0)
                {
                    gaps.Heading($"  WAS NOT THERE in {InScene(before.Id)}, where {s.Short(teller)} came by these, and has no record on them:");
                    foreach (var prop in missed)
                        gaps.Mention("    ", prop);
                    listed.AddRange(missed);
                }
            }
            if (gaps.Items.Count > 0)
            {
                sections.Add(gaps);
                var wrong = personGaps.Where(g => g.Status == KnowledgeStatus.Wrong).Select(g => g.Prop).ToList();
                var unsettled = personGaps.Where(g => g.Status == KnowledgeStatus.Unsettled).Select(g => g.Prop).ToList();
                unknownTo.Add((s.Model.Entity(entry)!.Name, wrong, unsettled, listed.Except(wrong, StringComparer.Ordinal).Except(unsettled, StringComparer.Ordinal).ToList()));
            }

            // What they hold on entry, under the label each view earns against what is so. A label such as "knows"
            // or "FALSE BELIEF" is a judgement against the truth, and is no firmer than it.
            var held = new Section($"{head}: beliefs on entry", entry == scene.Pov);
            held.Records.AddRange(beliefs.Select(kv => kv.Value.Id));
            if (beliefs.Count == 0 && heard.Count == 0)
                held.Line("  no recorded beliefs yet");
            foreach (var group in beliefs.GroupBy(kv => (Label: s.Classify(kv.Value, key), Way: Stand(kv.Value.Stance, kv.Value.Strength)))
                         .OrderBy(g => LabelOrder(g.Key.Label)).ThenBy(g => g.Key.Label, StringComparer.Ordinal).ThenBy(g => g.Key.Way, StringComparer.Ordinal))
            {
                var (earned, way) = group.Key;
                held.Heading($"  [{earned}]{(way == earned ? "" : " " + way)}:");
                foreach (var (prop, b) in group)
                {
                    var unconfirmed = b.Stance is Stance.HoldsTrue or Stance.HoldsFalse ? s.UnconfirmedMark(prop, key) : "";
                    held.Fact("    ", prop, Prop(prop) + unconfirmed, $" ({b.Id}{Provisional(b.Id)})",
                        $"  {prop} [{earned}] {way}: {Prop(prop)} ({b.Id}{Provisional(b.Id)}){unconfirmed}", joins: true, byId: Provisional(b.Id));
                }
            }
            // Being in the room when a thing is said is not believing it, and it is not nothing either. Someone who
            // heard the words can repeat them and say whose they were; what they made of them, no record says.
            if (heard.Count > 0)
            {
                held.Heading("  [heard it said] with no record of what they made of it. They can repeat the words, not vouch for them:");
                foreach (var said in heard)
                {
                    var how = $"{(said.Polarity ? "said" : "denied")} by {s.Short(said.Speaker)} in {said.Scene}";
                    held.Fact("    ", said.Prop, TruthPhrase(said.Prop),
                        $" ({how}: \"{Plain.OneLine(QuoteMatcher.Normalize(said.Quote))}\", {said.Id}{Provisional(said.Id)})",
                        $"  {said.Prop} [heard it said] {how} ({said.Id}{Provisional(said.Id)})");
                    held.Records.Add(said.Id);
                }
            }
            if (learns.Count > 0)
            {
                held.Line("  learns during this scene, as recorded:");
                foreach (var learned in learns)
                    held.Fact("    ", learned, TruthPhrase(learned), "", $"    {learned}: {TruthPhrase(learned)}", joins: true);
            }
            sections.Add(held);
        }

        // ---- relevant: since each person was last on stage. The last scene they
        // were in before this one in story time, and what they did, heard or came to believe in between, which is
        // mostly what happened off the page. Where that scene's prose is goes with the other pointers to prose,
        // which the freshness check leaves out, so that editing an earlier scene does not make this one stale.
        var since = new Section("SINCE EACH PERSON WAS LAST ON STAGE (in story time: what they did, heard or came to believe in between)", false);
        var lastSeen = new List<(string Name, string Scene)>();
        foreach (var (entry, person) in cast)
        {
            var name = s.Model.Entity(entry)!.Name;
            var last = s.LastOnStage(person, sceneId);
            if (last is null)
            {
                since.Line($"  {name}: on stage in no scene before this one in story time");
                continue;
            }
            var items = Recap.Items(s, person, s.SceneEnd(last.Id), key, truth: false);
            since.Line(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: last on stage in {InScene(last.Id)}, at story time {s.Key(last.Id)}. Since then: {(items.Count == 0 ? "nothing recorded" : $"{items.Count} thing(s)")}"));
            foreach (var i in items)
                since.Line($"    {Recap.At(i.Key)}, {(i.Scene is null ? "off the page" : InScene(i.Scene))}: {i.Text} ({i.Id}{Provisional(i.Id)})");
            since.Records.AddRange(items.Select(i => i.Id));
            lastSeen.Add((name, last.Id));
        }
        if (since.Items.Count > 0)
            sections.Add(since);

        // ---- relevant
        var revealed = s.RevealedHere(sceneId);
        if (revealed.Count > 0)
        {
            var shown = new Section("REVEALED TO THE READER IN THIS SCENE, AS RECORDED", false);
            foreach (var fact in revealed)
                shown.Fact("  ", fact, TruthPhrase(fact), "", $"  {fact}: {TruthPhrase(fact)}", joins: true);
            sections.Add(shown);
        }

        // What the reader has been given, under how it was given and how they take it. A fact someone present
        // holds has been worded under their name by now, so most of this is ids.
        var reader = s.ReaderBefore(tell).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var given = new Section("THE READER ON ENTRY (in reading order)", false);
        given.Records.AddRange(reader.Select(kv => kv.Value.Id));
        if (reader.Count == 0)
            given.Line("  nothing yet");
        foreach (var group in reader.GroupBy(kv => (kv.Value.Mode, Way: Stand(kv.Value.Stance, kv.Value.Strength)))
                     .OrderBy(g => (int)g.Key.Mode).ThenBy(g => g.Key.Way, StringComparer.Ordinal))
        {
            given.Heading($"  [{Names.Of(group.Key.Mode)}] reader {group.Key.Way}:");
            foreach (var (prop, d) in group)
                given.Fact("    ", prop, Prop(prop), $" ({d.Scene}, {d.Id}{Provisional(d.Id)})",
                    $"  {prop} [{Names.Of(d.Mode)}, {d.Scene}] reader {group.Key.Way}: {Prop(prop)} ({d.Id}{Provisional(d.Id)})", joins: true, byId: Provisional(d.Id));
        }

        var world = new Section("WORLD ON ENTRY", false);
        foreach (var obj in s.TrackedObjects)
        {
            var l = s.Location(obj, key);
            var name = s.Model.Entity(obj)?.Name ?? obj;
            world.Line(l is null ? $"  {name}: no location recorded at this point"
                : l.Unresolved ? $"  {name}: unresolved; records {string.Join(" and ", l.Conflict!)} share a story time and disagree"
                : $"  {name}: {s.Model.Entity(l.Place!)?.Name ?? l.Place} ({l.Id}{Provisional(l.Id)})");
            if (l is { Unresolved: false })
                world.Records.Add(l.Id);
        }
        if (world.Items.Count > 0)
            sections.Add(world);
        // The reader's state comes after where things are: when the budget is short the parts go from the end,
        // and a draft that keeps to what must not be revealed can do without the list of what has been.
        sections.Add(given);

        // Last of the parts that can be left out, so that it is the first to go.
        if (elsewhere.Items.Count > 0)
            sections.Add(elsewhere);

        // ---- must: what no longer stands. A record of this scene, or one that the state above lists, which was
        // borne out when it was recorded and whose footing has gone since: the author rejected it, an earlier
        // scene was rewritten and no longer says it, or it rests on something that is in doubt itself. A draft
        // that builds on one builds on nothing, and a new draft of this scene has to deal with its own.
        var standing = Standing.Of(s);
        var mine = standing.In(sceneId);
        var listedHere = sections.SelectMany(x => x.Records).ToHashSet(StringComparer.Ordinal);
        var theirs = standing.All.Where(d => listedHere.Contains(d.Id) && Standing.SceneOf(s.Model.ById[d.Id].Record) != sceneId).ToList();
        if (mine.Count + theirs.Count > 0)
        {
            var doubted = new Section("IN QUESTION (each of these was borne out when it was recorded. What it rested on has since been rejected, retired or withdrawn, or is in doubt itself. Build nothing on them)", true);
            void Show(Doubt doubt)
            {
                var record = s.Model.ById[doubt.Id].Record;
                var about = Standing.FactOf(record);
                doubted.Line($"    {doubt.Id} · {Standing.Where(s, record)} · {RecordText.Line(s, record)}", about);
                doubted.Line($"      {Plain.OneLine(standing.Reason(doubt))}", about);
                doubted.Records.Add(doubt.Id);
            }
            if (mine.Count > 0)
            {
                doubted.Heading("  in this scene as it is recorded. Rewrite the passage, or withdraw the record when you submit:");
                foreach (var doubt in mine)
                    Show(doubt);
            }
            if (theirs.Count > 0)
            {
                doubted.Heading("  among the records the lines of this context rest on. They are still listed where they were; tell the author:");
                foreach (var doubt in theirs)
                    Show(doubt);
            }
            var inDoubt = mine.Concat(theirs).Select(d => Standing.FactOf(s.Model.ById[d.Id].Record)).OfType<string>()
                .Distinct(StringComparer.Ordinal).Where(prop => s.Model.Proposition(prop) is not null).ToList();
            if (inDoubt.Count > 0)
                doubted.Heading("  the facts these name:");
            foreach (var prop in inDoubt)
                doubted.Fact("    ", prop, TruthPhrase(prop), "", $"    {prop}: {TruthPhrase(prop)}");
            sections.Insert(doubtAt, doubted);
        }
        var questioned = mine.Concat(theirs).Select(d => d.Id).ToList();

        var prose = new Section(ProseTitle + " (open these files; the text is not copied here)", true);
        // A scene that is still to be written has a marker and nothing under it.
        prose.Line(text.LineEnd < text.LineStart
            ? $"  manuscript/{text.File}: this scene has no text yet. It goes under its marker, which is on line {text.LineStart - 1}"
            : $"  manuscript/{text.File} lines {text.LineStart}-{text.LineEnd}: this scene as it stands");
        if (s.Telling.FirstOrDefault(t => t.Tell == tell - 1) is { } previous)
            prose.Line($"  manuscript/{previous.File} lines {previous.LineStart}-{previous.LineEnd}: the scene before it in the telling ({previous.Id})");
        foreach (var (who, where) in lastSeen.GroupBy(l => l.Scene).Select(g => (string.Join(" and ", g.Select(l => l.Name)), g.Key)))
            if (s.Telling.FirstOrDefault(t => t.Id == where) is { } seen && seen.Tell != tell - 1)
                prose.Line($"  manuscript/{seen.File} lines {seen.LineStart}-{seen.LineEnd}: where {who} {(who.Contains(" and ", StringComparison.Ordinal) ? "were" : "was")} last on stage ({where})");
        sections.Add(prose);

        // ---- the hard constraints once more, by id, as the last thing before the manifest. What a model reads
        // last it keeps best, so the strongest instructions go there. That it helps here is not measured. It is
        // drawn from the parts above and adds nothing to them, so it is no part of what the manifest id stands for.
        var recap = new StringBuilder("\n\nKEEP TO THESE (the parts above, by id)");
        recap.Append("\n  Not to be stated to the reader, though it may be hinted: ").Append(notToReader.Count == 0 ? "nothing at this point" : string.Join(", ", notToReader));
        foreach (var (name, wrong, unsettled, notTheirs) in unknownTo)
        {
            // Holding the wrong view and not having the fact are kept apart here too: the first is acted on.
            if (wrong.Count > 0)
                recap.Append(CultureInfo.InvariantCulture, $"\n  {name} has these wrong, and acts on that: {string.Join(", ", wrong)}");
            // So is a question someone has in mind and has not settled: that is not a thing they lack.
            if (unsettled.Count > 0)
                recap.Append(CultureInfo.InvariantCulture, $"\n  {name} has not settled: {string.Join(", ", unsettled)}");
            if (notTheirs.Count > 0)
                recap.Append(CultureInfo.InvariantCulture, $"\n  {name} lacks on entry: {string.Join(", ", notTheirs)}");
        }
        if (questioned.Count > 0)
            recap.Append("\n  In question, build nothing on: ").Append(string.Join(", ", questioned));
        recap.Append("\n  A fact that is not under someone's name: no record either way. They do not say it or act on it until the scene gives it to them, and the text does not say they do not know it.");
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
            $"CONTEXT FOR SCENE {tell}, {sceneId}{(scene.Title ?? text.Title) switch { null => "", var title => $" \"{title}\"" }}\nPoint of view: {pov.Name}.{place} Story time {key}{label}. What happens in this scene is dated from {key} up to, but not including, {end}.\nCast: {castLine}.\nEverything below is the state ON ENTRY to the scene, as of ledger head {s.Model.Head}.\n\n{Reading.ReplaceLineEndings("\n")}");

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
        parts.AddRange(sections.Select(x => x.Part));
        return (header, sections, recap.ToString(), parts);
    }

    /// <summary>The order the labels of a person's views are listed in: what they have right, then what they have wrong, then what is open.</summary>
    private static int LabelOrder(string label) => label switch
    {
        "knows" => 0,
        "knows it is false" => 1,
        _ when label.StartsWith("correctly", StringComparison.Ordinal) => 2,
        "FALSE BELIEF" => 3,
        "OUTDATED BELIEF" => 4,
        "unaware" => 5,
        "undecided" => 6,
        "ORDER NOT ESTABLISHED" => 7,
        _ => 8,
    };

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
                sb.Append(CultureInfo.InvariantCulture, $" {provisional} provisional record(s). They are the story as it stands until the author decides: write on from them.");
            if (heldProposals > 0)
                sb.Append(CultureInfo.InvariantCulture, $" {heldProposals} proposal(s) held for the author's decision. They are not part of the story unless the author accepts them.");
            sb.Append(" The review queue lists them. Only the author decides.\n");
        }
        // A change to the story can leave a passage written earlier resting on nothing. A session has to hear of
        // that before it builds on the passage, and the author before they forget what they changed.
        var doubts = Standing.Of(s).All;
        if (doubts.Count > 0)
        {
            var where = doubts.Select(d => Standing.SceneOf(s.Model.ById[d.Id].Record)).OfType<string>().Distinct(StringComparer.Ordinal)
                .OrderBy(id => s.Tell(id) ?? int.MaxValue).ThenBy(id => id, StringComparer.Ordinal).ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"In question: {doubts.Count} record(s){(where.Count == 0 ? "" : $" in {string.Join(", ", where)}")} no longer have what they rested on. The attention report lists each with its passage and the reason.\n");
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
