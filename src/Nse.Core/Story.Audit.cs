using System.Text;
using Nse.Query;

namespace Nse;

/// <summary>
/// Checking a draft for what its author did not declare. The engine checks what an
/// agent declares about a scene; a leak nobody declares is invisible to that. Two things narrow the gap. The
/// word scan here is deterministic and points at candidate passages. The audit brief is what an auditor with a
/// fresh context is given, so that a model reads the text against what must not be given away, and nothing more.
/// </summary>
public sealed partial class Story
{
    /// <summary>
    /// Scan a scene for the tripwires of every embargo in force on entry to it. With neither a file nor a text the
    /// scene is scanned as it stands in the manuscript, with the line numbers of its file.
    /// </summary>
    /// <param name="file">A draft on disk: a whole manuscript file, from which the scene's part is taken, or a file holding only the draft.</param>
    /// <param name="text">A draft given as text, read the same way as a file.</param>
    public ScanResult Scan(string sceneId, string? file = null, string? text = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        if (file is not null && text is not null)
            throw new ArgumentException("give a file or a text to scan, not both");
        var state = State();
        if (state.Tell(sceneId) is null)
            throw new KeyNotFoundException(
                $"Scene {sceneId} has no marker in the manuscript, so it has no place in the telling and nothing says which embargoes hold in it. Add its marker first.");
        if (file is null && text is null)
        {
            var scene = state.Telling.First(t => t.Id == sceneId);
            return DraftScan.Run(state, sceneId, DraftScan.Paragraphs(scene), $"manuscript/{scene.File} lines {scene.LineStart}-{scene.LineEnd}");
        }
        var (body, first) = DraftScan.SceneOf(text ?? File.ReadAllText(file!, Encoding.UTF8), sceneId);
        return DraftScan.Run(state, sceneId, DraftScan.Paragraphs(body, first), file is null ? "the text given" : Text.Plain.OneLine(file));
    }

    /// <summary>Every scene in the manuscript, scanned as it stands, in the order they are told.</summary>
    public IReadOnlyList<ScanResult> ScanAll()
    {
        var state = State();
        return state.Telling.Select(t => DraftScan.Run(state, t.Id, DraftScan.Paragraphs(t), $"manuscript/{t.File} lines {t.LineStart}-{t.LineEnd}")).ToList();
    }

    /// <summary>What the auditor of one scene is given. Reads only; opens no task.</summary>
    public string AuditBrief(string sceneId, AuditMode mode = AuditMode.Leaks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        var state = State();
        var scene = state.Telling.FirstOrDefault(t => t.Id == sceneId)
                    ?? throw new KeyNotFoundException($"Scene {sceneId} has no marker in the manuscript, so there is no text to audit.");
        var scan = DraftScan.Run(state, sceneId, DraftScan.Paragraphs(scene), $"manuscript/{scene.File} lines {scene.LineStart}-{scene.LineEnd}");
        return Query.AuditBrief.Text(state, sceneId, mode, scan);
    }
}
