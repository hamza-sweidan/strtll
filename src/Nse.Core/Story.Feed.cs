using System.Globalization;
using System.Text;
using Nse.Ledger;
using Nse.Query;

namespace Nse;

/// <summary>How a scene's text as it stands compares with what has been submitted for it.</summary>
public enum FedState
{
    /// <summary>The text as it stands is the text its latest submission was made with.</summary>
    Fed,

    /// <summary>A submission is recorded, and the text has changed since.</summary>
    Changed,

    /// <summary>Nothing submitted for it is in force, and no record names it.</summary>
    NotFed,

    /// <summary>Records name it, and none came by a scene submission: the author put them, or an import did.</summary>
    Recorded,

    /// <summary>It was submitted by an engine that did not keep which text it was submitted with.</summary>
    Unknown,

    /// <summary>It has a marker and no text yet.</summary>
    Empty,

    /// <summary>It lacks a scene record or a place in story time, which are the author's to set.</summary>
    NotReady,
}

/// <summary>One scene of the manuscript, with where feeding it stands.</summary>
/// <param name="Seq">The change set of its latest submission, if it has one.</param>
public sealed record FedScene(
    string Scene, int Tell, string? Title, string File, int LineStart, int LineEnd, FedState State, int? Seq, string? ChangeSet, string Detail);

/// <summary>Where feeding a manuscript stands: every scene in the order told.</summary>
public sealed record FeedProgress(int Head, IReadOnlyList<FedScene> Scenes)
{
    /// <summary>The scenes whose present text still has to be submitted, in the order told.</summary>
    public IReadOnlyList<FedScene> Unfinished => Scenes.Where(s => s.State is FedState.NotFed or FedState.Changed).ToList();

    /// <summary>The first of them: where a session that has lost its place picks up.</summary>
    public FedScene? Next => Unfinished.Count > 0 ? Unfinished[0] : null;

    /// <summary>How many scenes stand recorded at their present text, by a submission or by the author's hand.</summary>
    public int Done => Scenes.Count(s => s.State is FedState.Fed or FedState.Recorded);
}

public sealed partial class Story
{
    /// <summary>
    /// Where feeding the manuscript stands. A scene submission writes into its change set a hash of the scene's
    /// text as it was submitted, so the ledger alone says which revision of each scene has been taken in. This
    /// reads that back and sets it against the text as it stands. Nothing is written, and nothing but the
    /// ledger and the manuscript is read: a session that was cut short, a context that was compacted, a task
    /// folder that was cleared and an index that was deleted all leave the answer as it was.
    /// </summary>
    public FeedProgress Feed()
    {
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        var projection = Projection.Build(ledger.Select(s => s.ChangeSet));
        var model = new StoryModel(projection.LiveAt(projection.Head), projection.Head);
        // The latest submission applied for each scene. A refused submission wrote nothing and is not here.
        var latest = new Dictionary<string, StoredChangeSet>(StringComparer.Ordinal);
        foreach (var stored in ledger)
            if (stored.ChangeSet.Basis is { } basis)
                latest[basis.Scene] = stored;
        var scenes = new List<FedScene>();
        foreach (var text in manuscript.Scenes)
        {
            FedScene Is(FedState state, string detail, StoredChangeSet? by = null) =>
                new(text.Id, text.Tell, text.Title, text.File, text.LineStart, text.LineEnd, state, by?.ChangeSet.Seq, by?.ChangeSet.Id, detail);
            if (!model.Scenes.ContainsKey(text.Id))
            {
                scenes.Add(Is(FedState.NotReady, "it has no scene record: its cast and point of view are the author's to set"));
                continue;
            }
            if (!model.SceneTimes.ContainsKey(text.Id))
            {
                scenes.Add(Is(FedState.NotReady, "it has no place in story time, which is the author's to set"));
                continue;
            }
            if (text.Blocks.Count == 0)
            {
                scenes.Add(Is(FedState.Empty, "it has no text yet"));
                continue;
            }
            var named = model.EventsIn(text.Id).Count + model.AssertionsIn(text.Id).Count + model.BeliefsIn(text.Id).Count
                        + model.DisclosuresIn(text.Id).Count + model.PlacementsIn(text.Id).Count;
            if (!latest.TryGetValue(text.Id, out var by))
            {
                scenes.Add(named == 0
                    ? Is(FedState.NotFed, "nothing has been submitted for it")
                    : Is(FedState.Recorded, string.Create(CultureInfo.InvariantCulture, $"{named} record(s) name it, none by a scene submission")));
                continue;
            }
            var seq = by.ChangeSet.Seq;
            var brought = by.ChangeSet.Ops.OfType<PutOp>().ToList();
            var inForce = brought.Count(put => projection.Find(put.Record.Id) is { } t && t.CreatedSeq == seq && t.LiveAt(projection.Head));
            if (brought.Count > 0 && inForce == 0 && named == 0)
                scenes.Add(Is(FedState.NotFed, string.Create(CultureInfo.InvariantCulture,
                    $"what was submitted for it in change set {seq} was rejected or withdrawn in full, and nothing else records it"), by));
            else if (by.ChangeSet.Basis!.Text is not { } then)
                scenes.Add(Is(FedState.Unknown, string.Create(CultureInfo.InvariantCulture,
                    $"submitted in change set {seq} by an engine that did not keep which text it was submitted with"), by));
            else if (then == text.Hash)
                scenes.Add(Is(FedState.Fed, string.Create(CultureInfo.InvariantCulture,
                    $"submitted at this text in change set {seq}; {inForce} of the {brought.Count} record(s) it brought are in force"), by));
            else
                scenes.Add(Is(FedState.Changed, string.Create(CultureInfo.InvariantCulture,
                    $"its text has changed since change set {seq}, where it was last submitted"), by));
        }
        return new FeedProgress(projection.Head, scenes);
    }
}

/// <summary>Where feeding stands, in words: the whole of it for the author, and the few lines a session starts from.</summary>
public static class FeedReport
{
    private static string Label(FedState state) => state switch
    {
        FedState.Fed => "fed",
        FedState.Changed => "CHANGED",
        FedState.NotFed => "NOT FED",
        FedState.Recorded => "recorded",
        FedState.Unknown => "unknown",
        FedState.Empty => "no text",
        _ => "NOT READY",
    };

    private static string Where(FedScene s) =>
        string.Create(CultureInfo.InvariantCulture, $"scene {s.Tell}, {s.Scene} (manuscript/{s.File} lines {s.LineStart}-{s.LineEnd})");

    /// <summary>Every scene in the order told, with where it stands, and where to pick up.</summary>
    public static string Text(FeedProgress p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"FEED · ledger head {p.Head} · each scene's text as it stands, against what has been submitted for it, in the order told\n\n");
        var width = p.Scenes.Count == 0 ? 0 : Math.Min(24, p.Scenes.Max(s => s.Scene.Length));
        foreach (var s in p.Scenes)
            sb.Append(CultureInfo.InvariantCulture, $"  {s.Tell,3}  {s.Scene.PadRight(width)}  {Label(s.State),-9}  {s.Detail}\n");
        sb.Append('\n').Append(Summary(p));
        return sb.ToString();
    }

    /// <summary>
    /// What a session needs at its start, and again after its context has been cut down: what is done and not
    /// to be read again, and the scene to pick up at. Empty when every scene stands recorded at its present
    /// text, which is how a story that is not being fed reads.
    /// </summary>
    public static string Brief(FeedProgress p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Scenes.Any(s => s.State is FedState.NotFed or FedState.Changed or FedState.Unknown or FedState.NotReady)
            ? "\nFeeding the manuscript\n" + Summary(p, "  ")
            : "";
    }

    private static string Summary(FeedProgress p, string indent = "")
    {
        var sb = new StringBuilder();
        var unfinished = p.Unfinished;
        sb.Append(CultureInfo.InvariantCulture,
            $"{indent}{p.Done} of {p.Scenes.Count} scene(s) stand recorded at their present text. Do not read or submit those again: the ledger has them.\n");
        if (p.Next is { } next)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{indent}Pick up at {Where(next)}: {next.Detail}.\n");
            if (unfinished.Count > 1)
            {
                const int shown = 10;
                sb.Append(CultureInfo.InvariantCulture, $"{indent}After it, {unfinished.Count - 1} more: ")
                    .Append(string.Join(", ", unfinished.Skip(1).Take(shown).Select(s => s.State == FedState.Changed ? $"{s.Scene} (changed)" : s.Scene)))
                    .Append(unfinished.Count - 1 > shown ? ", and so on in the order told" : "").Append(".\n");
            }
        }
        else
            sb.Append(indent).Append("Nothing is left to submit.\n");
        void Others(FedState state, string what)
        {
            var these = p.Scenes.Where(s => s.State == state).ToList();
            if (these.Count > 0)
                sb.Append(CultureInfo.InvariantCulture, $"{indent}{what}: {string.Join(", ", these.Select(s => s.Scene))}.\n");
        }
        Others(FedState.NotReady, "Not ready, and the author's to set up before they can be submitted");
        Others(FedState.Unknown, "Submitted before the engine kept the text they were submitted with, so not known to be current");
        return sb.ToString();
    }
}
