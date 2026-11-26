using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nse.Ledger;

public sealed class LedgerException(string message) : Exception(message);

/// <summary>
/// The ledger is a directory of append-only change-set files. They are the truth about the story's state,
/// and they live in the same repository as the prose, so one commit captures both.
/// </summary>
public static class LedgerStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Read every change set and check the chain. Throws if a file was edited, or one before the newest was
    /// removed or reordered. Removing the newest file or files leaves a shorter chain that is whole in itself
    /// and cannot be told from an older state by the files alone; git, or <c>nse verify</c> with an index
    /// built earlier, is what notices that.
    /// </summary>
    public static IReadOnlyList<StoredChangeSet> Load(string ledgerDir)
    {
        var result = new List<StoredChangeSet>();
        if (!Directory.Exists(ledgerDir))
            return result;
        string? previous = null;
        foreach (var path in Directory.GetFiles(ledgerDir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            var cs = Parse(bytes, name);
            if (cs.Seq != result.Count + 1)
                throw new LedgerException(
                    $"{name}: sequence {cs.Seq} where {result.Count + 1} was expected. A change set is missing or out of order");
            if (name != FileName(cs.Seq, cs.Id))
                throw new LedgerException($"{name}: the file name does not match its contents ({FileName(cs.Seq, cs.Id)})");
            if (cs.Parent != previous)
                throw new LedgerException(
                    $"{name}: its parent hash does not match the file before it. The ledger was edited or two histories were mixed");
            previous = Hash(bytes);
            result.Add(new StoredChangeSet(cs, name, previous));
        }
        return result;
    }

    /// <summary>
    /// One file's bytes as a change set, checked against its own id. The id is a hash of what the change set
    /// says, so an edit shows even in the newest file, which has no successor to vouch for it.
    /// </summary>
    private static ChangeSet Parse(byte[] bytes, string name)
    {
        ChangeSet cs;
        string id;
        try
        {
            cs = NseJson.Read<ChangeSet>(bytes);
            id = IdFor(JsonNode.Parse(bytes)!);
        }
        catch (JsonException e)
        {
            throw new LedgerException($"{name}: not a readable change set: {e.Message}");
        }
        if (cs.Format != 1)
            throw new LedgerException($"{name}: format {cs.Format} is not one this version reads");
        if (cs.Id != id)
            throw new LedgerException($"{name}: its contents do not match its id. The file was edited after it was written");
        return cs;
    }

    /// <summary>Write the next change set. The file appears under its final name only when it is complete.</summary>
    public static StoredChangeSet Append(
        string ledgerDir, IReadOnlyList<StoredChangeSet> existing, ChangeSetDraft draft, string manuscriptHash,
        ChangeBasis? basis = null, AcceptedProposal? proposal = null)
    {
        Directory.CreateDirectory(ledgerDir);
        var seq = existing.Count + 1;
        var parent = existing.Count > 0 ? existing[^1].Hash : null;
        var unnamed = new ChangeSet
        {
            Seq = seq, Id = "", Parent = parent, Actor = draft.Actor, Note = draft.Note,
            Manuscript = manuscriptHash, Basis = basis, Proposal = proposal, Ops = draft.Ops,
        };
        var id = IdFor(JsonNode.Parse(NseJson.Write(unnamed))!);
        var cs = unnamed with { Id = id };
        var bytes = Utf8.GetBytes(NseJson.Write(cs) + "\n");
        var name = FileName(seq, id);
        // Read back what is about to be written, exactly as the next load will. A change set that cannot be
        // read again must never reach the ledger: every later command would fail on it.
        Parse(bytes, name);
        var final = Path.Combine(ledgerDir, name);
        if (File.Exists(final))
            throw new LedgerException($"{name} already exists");
        var temp = final + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, final);
        return new StoredChangeSet(cs, name, Hash(bytes));
    }

    /// <summary>
    /// A change set's id is a hash of its own JSON with the id taken out, in a fixed compact form. It is
    /// taken from the file as written, not from the program's model of it, so a later version of the program
    /// that knows more fields still finds every old file's id unchanged. Re-indenting a file does not change
    /// its id; changing anything it says does.
    /// </summary>
    public static string IdFor(JsonNode changeSet) => HashId(changeSet, "cs_");

    /// <summary>The same rule for anything else that is named by what it says: a held proposal is.</summary>
    /// <param name="digits">How many hex digits of the hash the id keeps.</param>
    public static string HashId(JsonNode document, string prefix, int digits = 10)
    {
        if (document is not JsonObject source)
            throw new JsonException("a JSON object was expected");
        var copy = source.DeepClone().AsObject();
        copy.Remove("id");
        return prefix + Hash(Utf8.GetBytes(copy.ToJsonString(Canonical)))[..digits];
    }

    public static string FileName(int seq, string id) =>
        seq.ToString("000000", CultureInfo.InvariantCulture) + "-" + id + ".json";

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>One value that names the whole ledger: the hash of its last file.</summary>
    public static string HeadHash(IReadOnlyList<StoredChangeSet> ledger) => ledger.Count > 0 ? ledger[^1].Hash : "empty";
}
