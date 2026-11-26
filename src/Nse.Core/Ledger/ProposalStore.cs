using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nse.Ledger;

/// <summary>A file among the held proposals that cannot be used, and why. It is reported and never applied.</summary>
public sealed record UnreadableProposal(string File, string Why);

/// <summary>
/// Proposals are change sets held and not applied. They wait in a folder beside the ledger, one file each,
/// until the author accepts or declines them. They are not part of the chain and no answer about the story
/// reads them. A proposal's id is a hash of what it says, checked on every read: the id the author approves
/// names exactly one content, and a file changed after it was held no longer answers to its id.
/// </summary>
/// <remarks>
/// The ledger is the record of what was accepted: the change set an accepted proposal became names the
/// proposal. So a file left behind here by a crash between the append and the tidy-up is told apart by
/// asking the ledger, never by trusting the folder.
/// </remarks>
public static partial class ProposalStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public const string Prefix = "pr_";

    /// <summary>
    /// A proposal's id keeps sixteen hex digits of its hash. The id is what the author approves, and a
    /// proposal's note can be varied freely, so the id has to be too long for anyone to search for a second
    /// proposal that hashes to one the author has already read. A change set's shorter id is not relied on
    /// that way: the chain vouches for it.
    /// </summary>
    public const int IdDigits = 16;

    public static string Dir(string ledgerDir) => Path.Combine(ledgerDir, "proposals");

    public static string DeclinedDir(string ledgerDir) => Path.Combine(Dir(ledgerDir), "declined");

    [GeneratedRegex(@"\Apr_[0-9a-f]{16}\z")]
    private static partial Regex IdShape();

    /// <summary>Whether a string has the shape of a proposal id. Checked before an id is ever made into a file name.</summary>
    public static bool IsId(string id) => IdShape().IsMatch(id);

    /// <summary>A draft as a proposal made at a ledger head, named by what it says.</summary>
    public static Proposal Make(ChangeSetDraft draft, int baseHead)
    {
        var unnamed = new Proposal { Id = "", Actor = draft.Actor, Note = draft.Note ?? "", BaseHead = baseHead, Ops = draft.Ops };
        return unnamed with { Id = LedgerStore.HashId(JsonNode.Parse(NseJson.Write(unnamed))!, Prefix, IdDigits) };
    }

    /// <summary>Every proposal waiting in the folder, oldest base first, and every file there that cannot be used.</summary>
    public static (IReadOnlyList<Proposal> Held, IReadOnlyList<UnreadableProposal> Unreadable) Load(string ledgerDir)
    {
        var held = new List<Proposal>();
        var unreadable = new List<UnreadableProposal>();
        var dir = Dir(ledgerDir);
        if (!Directory.Exists(dir))
            return (held, unreadable);
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var proposal = Parse(JsonNode.Parse(bytes), NseJson.Read<Proposal>(bytes));
                if (name != proposal.Id + ".json")
                    throw new JsonException($"the file name does not match its contents ({proposal.Id}.json)");
                // A proposal the engine could not write out again could be neither applied nor declined:
                // both write it. A file can be put here by hand, so that is found out now, not then.
                NseJson.Read<Proposal>(NseJson.Write(proposal));
                held.Add(proposal);
            }
            catch (ArgumentException)
            {
                // How the writer refuses a number that is not finite: there is no JSON for infinity.
                unreadable.Add(new UnreadableProposal(name, "a number in it is not finite, so it could never be written to the ledger"));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadableProposal(name, e.Message));
            }
        }
        return (held.OrderBy(p => p.BaseHead).ThenBy(p => p.Id, StringComparer.Ordinal).ToList(), unreadable);
    }

    /// <summary>A proposal read from JSON, checked against its own id.</summary>
    private static Proposal Parse(JsonNode? node, Proposal proposal)
    {
        if (proposal.Format != 1)
            throw new JsonException($"format {proposal.Format} is not one this version reads");
        if (node is null || proposal.Id != LedgerStore.HashId(node, Prefix, IdDigits))
            throw new JsonException("its contents do not match its id. The file was changed after the proposal was held");
        return proposal;
    }

    /// <summary>The record of a declined proposal, or null if there is none by that id, or none that can be read.</summary>
    public static DeclinedProposal? FindDeclined(string ledgerDir, string id)
    {
        if (!IsId(id))
            return null;
        var path = Path.Combine(DeclinedDir(ledgerDir), id + ".json");
        if (!File.Exists(path))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var declined = NseJson.Read<DeclinedProposal>(bytes);
            Parse(JsonNode.Parse(bytes)?["proposal"], declined.Proposal);
            return declined.Proposal.Id == id ? declined : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsHeld(string ledgerDir, string id) => IsId(id) && File.Exists(Path.Combine(Dir(ledgerDir), id + ".json"));

    /// <summary>Put a proposal in the folder. The file appears under its name only when it is complete.</summary>
    public static void Hold(string ledgerDir, Proposal proposal) =>
        WriteWhole(Path.Combine(Dir(ledgerDir), proposal.Id + ".json"), NseJson.Write(proposal));

    /// <summary>Take a proposal out of the folder: it was accepted, and the ledger now says so.</summary>
    public static void Remove(string ledgerDir, string id)
    {
        if (IsId(id))
            File.Delete(Path.Combine(Dir(ledgerDir), id + ".json"));
    }

    /// <summary>
    /// Record that a proposal was declined, then take it out of the folder. In that order: if the second step
    /// never happens, the record of the decision is what readers go by.
    /// </summary>
    public static void Decline(string ledgerDir, Proposal proposal, string by, string? reason, int atHead)
    {
        var declined = new DeclinedProposal { By = by, Reason = reason, AtHead = atHead, Proposal = proposal };
        WriteWhole(Path.Combine(DeclinedDir(ledgerDir), proposal.Id + ".json"), NseJson.Write(declined));
        Remove(ledgerDir, proposal.Id);
    }

    private static void WriteWhole(string final, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temp = final + ".tmp";
        File.WriteAllBytes(temp, Utf8.GetBytes(json + "\n"));
        File.Move(temp, final, overwrite: true);
    }
}
