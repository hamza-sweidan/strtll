using System.Text.Json.Serialization;
using Nse.Model;

namespace Nse.Ledger;

/// <summary>The few things that can happen to a record. An edit is a retire and a put.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PutOp), "put")]
[JsonDerivedType(typeof(RetireOp), "retire")]
[JsonDerivedType(typeof(RatifyOp), "ratify")]
[JsonDerivedType(typeof(RejectOp), "reject")]
public abstract record Operation;

public sealed record PutOp : Operation
{
    public required LedgerRecord Record { get; init; }
    public Approval Approval { get; init; } = Approval.Provisional;
}

public sealed record RetireOp : Operation
{
    public required string Id { get; init; }

    /// <summary>Why: retcon, correction, duplicate or withdrawn.</summary>
    public required string Reason { get; init; }
}

public sealed record RatifyOp : Operation
{
    public required IReadOnlyList<string> Ids { get; init; }
}

public sealed record RejectOp : Operation
{
    public required IReadOnlyList<string> Ids { get; init; }
}

/// <summary>What a caller asks to have applied. The engine assigns the sequence, the id and the parent.</summary>
public sealed record ChangeSetDraft
{
    public required string Actor { get; init; }
    public string? Note { get; init; }
    public required IReadOnlyList<Operation> Ops { get; init; }
}

/// <summary>
/// What a scene submission rested on: the context it was drafted from. Written into the change set by the
/// engine after it has checked that the state that context showed is still the state. It cannot be supplied
/// by a caller, so its presence means the check was made.
/// </summary>
/// <param name="Scene">The scene that was drafted or revised.</param>
/// <param name="Manifest">The id of the context the draft was written from.</param>
/// <param name="BaseHead">The ledger head that context was compiled at.</param>
/// <param name="Rebased">
/// True when the ledger had moved on since then and the change was applied all the same, because nothing the
/// context said about the story had changed. A rebase is always recorded, never silent.
/// </param>
/// <param name="Text">
/// A hash of the scene's text as it stood when it was submitted: which revision of the scene these records are
/// an account of. It is what lets the ledger say, with nothing else to go on, which scenes have been taken in at
/// their present text and which have changed since. Absent from change sets written before it was kept.
/// </param>
public sealed record ChangeBasis(string Scene, string Manifest, int BaseHead, bool Rebased, string? Text = null);

/// <summary>
/// The proposal a change set was, before the author accepted it: who proposed it, why, and the ledger head
/// they proposed it at. The change set's own actor is whoever accepted it. Written only by the engine, when
/// it applies a held proposal on the author's decision, so its presence means that decision was made.
/// </summary>
public sealed record AcceptedProposal(string Id, string Actor, string Note, int BaseHead);

/// <summary>
/// The unit of history: one file in the ledger directory. Each names the hash of the file before it,
/// so a change set that was edited, removed or reordered is detected on load.
/// </summary>
public sealed record ChangeSet
{
    public int Format { get; init; } = 1;
    public required int Seq { get; init; }
    public required string Id { get; init; }

    /// <summary>SHA-256 of the previous change-set file, or null for the first.</summary>
    public string? Parent { get; init; }
    public required string Actor { get; init; }
    public string? Note { get; init; }

    /// <summary>Hash of the manuscript the records were checked against.</summary>
    public required string Manuscript { get; init; }

    /// <summary>Set on a scene submission; absent on every other change set.</summary>
    public ChangeBasis? Basis { get; init; }

    /// <summary>Set when the change set is a held proposal the author accepted; absent on every other change set.</summary>
    public AcceptedProposal? Proposal { get; init; }
    public required IReadOnlyList<Operation> Ops { get; init; }
}

/// <summary>
/// A change set held and not applied: something an agent wants changed that is the author's to decide. It
/// names the ledger head it was proposed at; it is checked again, against the ledger as it then stands, when
/// the author accepts it.
/// </summary>
public sealed record Proposal
{
    public int Format { get; init; } = 1;

    /// <summary>A hash of everything else the proposal says, so the id the author approves names exactly this content.</summary>
    public required string Id { get; init; }
    public required string Actor { get; init; }

    /// <summary>Why the change is proposed. The author decides from this and from the operations.</summary>
    public required string Note { get; init; }
    public required int BaseHead { get; init; }
    public required IReadOnlyList<Operation> Ops { get; init; }
}

/// <summary>A proposal the author declined, kept so the decision and its reason are on record.</summary>
public sealed record DeclinedProposal
{
    public int Format { get; init; } = 1;
    public required string By { get; init; }
    public string? Reason { get; init; }

    /// <summary>The ledger head when it was declined.</summary>
    public required int AtHead { get; init; }
    public required Proposal Proposal { get; init; }
}

/// <summary>A change set as found on disk.</summary>
public sealed record StoredChangeSet(ChangeSet ChangeSet, string File, string Hash);
