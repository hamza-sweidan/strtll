using System.Globalization;
using System.Text;
using Nse.Ledger;
using Nse.Text;

namespace Nse;

/// <summary>What a scene submission came to, in words. Both front ends print exactly this.</summary>
public static class SubmitReport
{
    public static string Text(SubmitResult result)
    {
        var sb = new StringBuilder();
        if (result.ChangeSet is not { } stored)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Refused. Nothing was written. {result.Findings.Count} finding(s):\n");
            // A finding may quote what the caller sent. It is printed as one line whatever that held.
            foreach (var f in result.Findings)
                sb.Append("  ").Append(Plain.OneLine(f.ToString())).Append('\n');
            return sb.ToString();
        }
        var puts = stored.ChangeSet.Ops.OfType<PutOp>().Count();
        var withdrawn = stored.ChangeSet.Ops.OfType<RetireOp>().Count();
        sb.Append(CultureInfo.InvariantCulture,
            $"Applied as {stored.File}: {puts} record(s), all provisional{(withdrawn > 0 ? $", and {withdrawn} earlier record(s) of the scene withdrawn" : "")}, ledger head {stored.ChangeSet.Seq}.\n");
        if (result.Basis is { Rebased: true } basis)
            sb.Append(CultureInfo.InvariantCulture,
                $"Rebased: the draft was written from ledger head {basis.BaseHead} and the ledger had moved to {stored.ChangeSet.Seq - 1}. Nothing the context for {basis.Scene} says had changed, so it was applied, and the rebase is recorded in the change set.\n");
        if (result.Notes.Count == 0)
            sb.Append("The scene does what its contract plans, as far as the records declared go.\n");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"For the author to settle ({result.Notes.Count}):\n");
            foreach (var note in result.Notes)
                sb.Append("  ").Append(Plain.OneLine(note.ToString())).Append('\n');
        }
        sb.Append("Nothing here is confirmed until the author ratifies it. Tell the author what was recorded and what is listed above.\n");
        // Said here because this is the moment the session knows it: whether what it sent is all of the scene.
        if (result.Notes.All(n => n.Code != "TASK_NOT_KEPT"))
            sb.Append("The scene's task is put away once the author has decided on these records. If more of the scene is still to be written, say so now: task_note with more true, and the next step.\n");
        return sb.ToString();
    }
}
