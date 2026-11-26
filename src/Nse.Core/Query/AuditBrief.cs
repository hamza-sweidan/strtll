using System.Globalization;
using System.Text;
using Nse.Model;
using Nse.Text;

namespace Nse.Query;

/// <summary>What an audit of one scene looks for.</summary>
public enum AuditMode
{
    /// <summary>What the text gives away that it must not: to the reader ahead of time, or by a person who does not know it.</summary>
    Leaks,

    /// <summary>
    /// The text is taken as right, and the records are checked against it: what the scene does of weight that no
    /// record says. Run before the author ratifies a scene, it measures how complete the declared records are.
    /// </summary>
    Completeness,
}

/// <summary>
/// The brief an auditor with a fresh context is given for one scene: where its text is, and the short list of
/// what to check it against. Nothing else of the story goes in, so that the audit is a narrow reading task and
/// not a second drafting context. Its last lines fix the form of the report, so that it can be read by a person
/// or a script alike.
/// </summary>
public static class AuditBrief
{
    /// <summary>The words that open every line of a leak report, and the last line of a clean one.</summary>
    public const string Leak = "LEAK", Clean = "CLEAN", Missing = "MISSING", Complete = "COMPLETE";

    public static string Text(StoryState s, string sceneId, AuditMode mode, ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(scan);
        if (!s.Model.Scenes.TryGetValue(sceneId, out var scene))
            throw new KeyNotFoundException($"No scene record {sceneId}.");
        var tell = s.Tell(sceneId) ?? throw new KeyNotFoundException($"Scene {sceneId} has no marker in the manuscript, so there is no text to audit.");
        if (!s.Model.SceneTimes.ContainsKey(sceneId))
            throw new KeyNotFoundException($"Scene {sceneId} has no place in story time, so what each person knows on entry to it cannot be worked out.");
        var text = s.Telling.First(t => t.Id == sceneId);
        var sb = new StringBuilder();
        var title = scene.Title ?? text.Title;
        sb.Append(CultureInfo.InvariantCulture,
            $"AUDIT OF SCENE {tell}, {sceneId}{(title is null ? "" : $" \"{Plain.OneLine(title)}\"")} · {(mode == AuditMode.Leaks ? "leaks" : "completeness")} · ledger head {s.Model.Head}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Read the scene's text, and no other prose: manuscript/{text.File} lines {text.LineStart}-{text.LineEnd}.\n");
        var cast = s.CastHolders(sceneId);
        sb.Append(CultureInfo.InvariantCulture, $"Point of view: {Name(s, scene.Pov)}. People present: {string.Join(", ", cast.Select(c => $"{Name(s, c.CastEntry)} ({c.CastEntry})"))}.\n");
        if (mode == AuditMode.Leaks)
            Leaks(s, sceneId, tell, cast, scan, sb);
        else
            Completeness(s, sceneId, cast, sb);
        return sb.ToString();
    }

    private static string Name(StoryState s, string id) => Plain.OneLine(s.Model.Entity(id)?.Name ?? id);

    /// <summary>A proposition as the auditor should read it: the way canon has it.</summary>
    private static string Fact(StoryState s, string prop) =>
        Plain.OneLine((s.Canon(prop) == "false" ? "It is NOT true that: " : "") + s.Model.Proposition(prop)!.Text);

    private static void Leaks(StoryState s, string sceneId, int tell, IReadOnlyList<(string CastEntry, string Person)> cast, ScanResult scan, StringBuilder sb)
    {
        sb.Append("""

            You are checking this scene for leaks: places where the text gives away something it must not. You are
            not judging the writing. A leak is in the text itself, whatever anyone meant.

            WHAT THE READER MUST NOT BE GIVEN YET
            A passage leaks if it states one of these, or leaves a careful reader certain of it. A hint the reader
            may or may not pick up is allowed.

            """.Replace("\r\n", "\n"));
        var withheld = new List<string>();
        var told = s.MustNotReveal(sceneId);
        var embargoes = s.EmbargoesInForce(sceneId);
        string Until(Embargo e) => s.Tell(e.UntilScene) is int n ? $"scene {n}, {e.UntilScene}" : $"{e.UntilScene}, not in the manuscript yet";
        foreach (var (prop, reveal) in told)
        {
            var mine = embargoes.Where(e => e.Prop == prop).Select(e => $"; embargo {e.Id} until {Until(e)}");
            withheld.Add($"  {prop}: {Fact(s, prop)} (first told in scene {s.Tell(reveal)}, {reveal}{string.Concat(mine)})");
        }
        foreach (var e in embargoes.Where(e => told.All(t => t.Prop != e.Prop)))
            if (s.Model.Proposition(e.Prop) is not null)
                withheld.Add($"  {e.Prop}: {Fact(s, e.Prop)} (embargo {e.Id} until {Until(e)})");
        if (withheld.Count == 0)
            withheld.Add("  nothing is being withheld from the reader at this point");
        foreach (var line in withheld.Distinct())
            sb.Append(line).Append('\n');

        sb.Append("""

            WHAT EACH PERSON PRESENT DOES NOT KNOW
            A passage leaks if the person says it, is told it, thinks it, or acts as only someone who knew it would.
            Someone else may say it in their hearing only if the scene is where they learn it, and then it is not
            listed here.

            """.Replace("\r\n", "\n"));
        foreach (var (entry, person) in cast)
        {
            var gaps = s.KnowledgeGaps(person, sceneId);
            var head = $"  {Name(s, entry)} ({entry}{(entry == s.Model.Scenes[sceneId].Pov ? ", point of view" : "")})";
            if (gaps.Count == 0)
            {
                sb.Append(head).Append(": nothing guarded that they do not know\n");
                continue;
            }
            sb.Append(head).Append(":\n");
            foreach (var gap in gaps)
                sb.Append(CultureInfo.InvariantCulture,
                    $"    {gap.Prop}: {Fact(s, gap.Prop)}{(gap.Status == KnowledgeStatus.Wrong ? " (they believe the opposite)" : "")}\n");
        }

        sb.Append("\nPASSAGES THE WORD SCAN POINTS AT\n");
        if (scan.Hits.Count == 0)
            sb.Append(scan.Patterns == 0
                ? "  The embargoes in force carry no patterns, so the scan points at nothing. Read all of it.\n"
                : "  None. That is not a clearance: read all of it.\n");
        else
        {
            sb.Append("  A hit is not a verdict, and a passage the scan missed may still leak.\n");
            foreach (var h in scan.Hits)
                sb.Append(CultureInfo.InvariantCulture, $"  line {h.Line}: \"{h.Matched}\" ({h.Embargo}, {h.Prop}): {h.Excerpt}\n");
        }
        foreach (var p in scan.Problems)
            sb.Append("  NOT CHECKED: ").Append(Plain.OneLine(p)).Append('\n');

        sb.Append($$"""

            REPORT
            One line for each leak, and nothing else, in this form:
              {{Leak}} <proposition id> <reader, or the id of the person> line <N>: "<the exact words of the text>" - <why, in a few words>
            Quote the text word for word, from one paragraph. Use only the ids listed above. Report each passage
            once, under the proposition it gives away. Then one last line: {{Clean}} if you found no leak, or
            {{Leak}}S and the count. Do not suggest changes, and do not report what the text merely makes possible.

            """.Replace("\r\n", "\n"));
    }

    private static void Completeness(StoryState s, string sceneId, IReadOnlyList<(string CastEntry, string Person)> cast, StringBuilder sb)
    {
        sb.Append("""

            The scene's text is taken as written. You are checking the story's records against it: what the scene
            does of weight that no record below says. Of weight means: someone comes to believe, doubt or learn
            something; someone says something that matters to the story; the reader is given a fact; a tracked
            object moves; something happens that people present witness or take part in.

            ALREADY RECORDED AS HAPPENING IN THIS SCENE

            """.Replace("\r\n", "\n"));
        var recorded = new List<string>();
        foreach (var e in s.Model.Events.Values.Where(x => x.Scene == sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
            recorded.Add($"  {e.Id}, event: {Plain.OneLine(e.Summary)}");
        foreach (var a in s.Model.Assertions.Values.Where(x => x.Scene == sceneId).OrderBy(x => x.StoryKey).ThenBy(x => x.Id, StringComparer.Ordinal))
            recorded.Add($"  {a.Id}, {Names.Of(a.Type)}: {Name(s, a.Speaker)} {(a.Polarity ? "asserts" : "denies")} {a.Prop}: {Fact(s, a.Prop)}");
        foreach (var change in s.RecordedChanges(sceneId))
            recorded.Add($"  {change.RecordId}: {Plain.OneLine(change.Text)}");
        if (recorded.Count == 0)
            recorded.Add("  nothing yet");
        foreach (var line in recorded)
            sb.Append(line).Append('\n');

        sb.Append("\nFACTS IN PLAY, BY ID (use these ids; a fact not listed is NEW)\n");
        var ids = s.InPlay(sceneId).ToHashSet(StringComparer.Ordinal);
        foreach (var a in s.Model.Assertions.Values.Where(x => x.Scene == sceneId))
            ids.Add(a.Prop);
        foreach (var b in s.Model.Beliefs.Where(x => x.Scene == sceneId))
            ids.Add(b.Prop);
        foreach (var d in s.Model.Disclosures.Where(x => x.Scene == sceneId))
            ids.Add(d.Prop);
        var listed = s.Model.Propositions.Where(p => ids.Contains(p.Id)).ToList();
        if (listed.Count == 0)
            sb.Append("  none\n");
        foreach (var p in listed)
            sb.Append(CultureInfo.InvariantCulture, $"  {p.Id}: {Fact(s, p.Id)}\n");
        sb.Append("\nPEOPLE BY ID: ").Append(string.Join(", ", cast.Select(c => $"{Name(s, c.CastEntry)} {c.CastEntry}"))).Append('\n');

        sb.Append($$"""

            REPORT
            One line for each thing of weight the text does that no record above says, and nothing else:
              {{Missing}} <belief, statement, disclosure, move or event> <proposition id or NEW> line <N>: "<the exact words of the text>" - <what should be recorded, in a few words>
            Quote the text word for word, from one paragraph. Then one last line: {{Complete}} if you found nothing
            missing, or {{Missing}} and the count. Do not report what the records say and the text does not: that is
            the submission's check, not yours.

            """.Replace("\r\n", "\n"));
    }
}
