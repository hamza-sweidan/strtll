using Nse.Index;
using Nse.Ledger;
using Nse.Query;
using Nse.Validation;

namespace Nse;

public sealed partial class Story
{
    /// <summary>
    /// What a change set would do, worked out in memory against the ledger and the manuscript as they stand.
    /// Every check <see cref="Put"/> makes is made, and its findings are reported, but what the change set would
    /// change is shown either way, because a retcon that breaks a secret is exactly the one whose consequences
    /// the author wants to see. Nothing is written, no lock is taken, and no task is touched.
    /// </summary>
    public WhatIfResult WhatIf(ChangeSetDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (clean, malformed) = Normalised(draft);
        if (clean is null)
            return new WhatIfResult(draft.Actor ?? "", draft.Ops?.Count ?? 0, ReadLedger().Count, malformed!.Findings, [], []);
        draft = clean;
        var ledger = ReadLedger();
        var manuscript = ReadManuscript();
        var before = Projection.Build(ledger.Select(s => s.ChangeSet));
        if (draft.Ops.Count == 0)
            return new WhatIfResult(draft.Actor, 0, before.Head, [new Finding("EMPTY", null, "the change set has no operations")], [], []);
        var candidate = new ChangeSet
        {
            Seq = before.Head + 1, Id = "cs_what_if", Actor = draft.Actor, Note = draft.Note, Manuscript = manuscript.Hash, Ops = draft.Ops,
        };
        Projection after;
        try
        {
            after = before.With(candidate);
        }
        catch (LedgerException e)
        {
            // Nothing can be said about what it would change: it cannot be laid on the ledger at all.
            return new WhatIfResult(draft.Actor, draft.Ops.Count, before.Head, [new Finding("LEDGER", null, e.Message)], [], []);
        }
        var findings = Validator.Check(before, after, draft, manuscript);
        var was = StoryIndex.FromFiles(before, null, manuscript);
        var now = StoryIndex.FromFiles(after, null, manuscript);
        IReadOnlyList<string> changes;
        try
        {
            changes = WhatIf(was, now);
        }
        catch (KeyNotFoundException e)
        {
            // A change set the checks refuse may leave the state unable to answer, for instance by naming a
            // record that is not there. The checks have said why; the comparison says it could not be made.
            changes = [$"the answers could not be worked out with this change set laid on: {e.Message}"];
        }
        return new WhatIfResult(draft.Actor, draft.Ops.Count, before.Head, findings, changes, Contexts(was, now));
    }

    private static IReadOnlyList<string> WhatIf(StoryState was, StoryState now) => Query.WhatIf.Differences(was, now);

    /// <summary>Each scene that can have a context, compiled before and after, with how many of its lines differ.</summary>
    private static IReadOnlyList<ContextChange> Contexts(StoryState was, StoryState now)
    {
        var result = new List<ContextChange>();
        foreach (var t in now.Telling)
        {
            ContextPackage? Compile(StoryState s)
            {
                if (!s.Model.Scenes.ContainsKey(t.Id) || !s.Model.SceneTimes.ContainsKey(t.Id))
                    return null;
                try
                {
                    return ContextCompiler.Compile(s, t.Id, int.MaxValue);
                }
                catch (KeyNotFoundException)
                {
                    return null;
                }
            }
            var (a, b) = (Compile(was), Compile(now));
            if (a is null && b is null)
                continue;
            var lines = a is null || b is null
                ? (a ?? b)!.Sections.Sum(x => x.Lines.Count + 1)
                : Freshness.Differences(a, b).Count;
            if (lines > 0)
                result.Add(new ContextChange(t.Id, t.Tell, lines));
        }
        return result;
    }
}
