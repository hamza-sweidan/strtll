using Nse.Model;

namespace Nse.Ledger;

/// <summary>
/// A record with its history: when it entered the ledger, whether it was retired, and what was decided about
/// it. There is at most one decision, because a decision is final (see <see cref="Projection"/>); that is what
/// lets a single decided sequence number answer "as of head N" for every N.
/// </summary>
public sealed record Tracked(
    LedgerRecord Record, int Order, int CreatedSeq, Approval InitialApproval,
    int? RetiredSeq = null, string? RetireReason = null, Approval? DecidedApproval = null, int? DecidedSeq = null)
{
    public Approval ApprovalAt(int head) =>
        DecidedSeq is int seq && seq <= head ? DecidedApproval!.Value : InitialApproval;

    /// <summary>Whether the record counts as of a ledger head: created, not yet retired, not rejected.</summary>
    public bool LiveAt(int head) =>
        CreatedSeq <= head && (RetiredSeq is null || RetiredSeq > head) && ApprovalAt(head) != Approval.Rejected;
}

/// <summary>
/// The ledger replayed: every record with its history. This is computed, never stored as truth.
/// Asking "as of head N" is a filter over it, which is how an older state can always be reproduced.
/// </summary>
public sealed class Projection
{
    private readonly Dictionary<string, Tracked> _byId = new(StringComparer.Ordinal);
    private int _order;

    public int Head { get; private set; }

    public IReadOnlyCollection<Tracked> All => _byId.Values;

    public Tracked? Find(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<Tracked> LiveAt(int head) => _byId.Values.Where(t => t.LiveAt(head)).OrderBy(t => t.Order);

    public static Projection Build(IEnumerable<ChangeSet> changeSets)
    {
        var p = new Projection();
        foreach (var cs in changeSets)
            p.Apply(cs);
        return p;
    }

    /// <summary>A copy with one more change set applied, for checking a draft before it is written.</summary>
    public Projection With(ChangeSet next)
    {
        var p = new Projection { Head = Head, _order = _order };
        foreach (var (id, t) in _byId)
            p._byId[id] = t;
        p.Apply(next);
        return p;
    }

    private void Apply(ChangeSet cs)
    {
        foreach (var op in cs.Ops)
        {
            switch (op)
            {
                case PutOp put:
                    if (_byId.ContainsKey(put.Record.Id))
                        throw new LedgerException($"change set {cs.Seq}: the id {put.Record.Id} is already in the ledger. Ids are never reused");
                    _byId[put.Record.Id] = new Tracked(put.Record, ++_order, cs.Seq, put.Approval);
                    break;
                case RetireOp retire:
                    var old = Live(retire.Id, cs.Seq, "retire");
                    _byId[retire.Id] = old with { RetiredSeq = cs.Seq, RetireReason = retire.Reason };
                    break;
                case RatifyOp ratify:
                    foreach (var id in ratify.Ids)
                        _byId[id] = Undecided(id, cs.Seq, "ratify") with { DecidedApproval = Approval.Ratified, DecidedSeq = cs.Seq };
                    break;
                case RejectOp reject:
                    foreach (var id in reject.Ids)
                        _byId[id] = Undecided(id, cs.Seq, "reject") with { DecidedApproval = Approval.Rejected, DecidedSeq = cs.Seq };
                    break;
                default:
                    throw new LedgerException($"change set {cs.Seq}: unknown operation");
            }
        }
        Head = cs.Seq;
    }

    /// <summary>
    /// A decision is made once, on a record that is still provisional. If a second decision could overwrite the
    /// first, the first would be lost and "as of head N" would give a different answer for an N in between.
    /// What was ratified in error is retired, with a reason; what was rejected in error is put again as a new record.
    /// </summary>
    private Tracked Undecided(string id, int seq, string verb)
    {
        var t = Live(id, seq, verb);
        if (t.DecidedSeq is int at)
            throw new LedgerException(
                $"change set {seq}: cannot {verb} {id}, which was {Named(t.DecidedApproval!.Value)} in change set {at}. A decision is final: retire a ratified record with a reason, or put a rejected one again as a new record");
        if (t.InitialApproval != Approval.Provisional)
            throw new LedgerException(
                $"change set {seq}: cannot {verb} {id}, which entered the ledger as {Named(t.InitialApproval)} in change set {t.CreatedSeq}. Only a provisional record awaits a decision");
        return t;
    }

    private static string Named(Approval a) => a.ToString().ToLowerInvariant();

    private Tracked Live(string id, int seq, string verb)
    {
        if (!_byId.TryGetValue(id, out var t))
            throw new LedgerException($"change set {seq}: cannot {verb} {id}, which is not in the ledger");
        if (t.RetiredSeq is not null)
            throw new LedgerException($"change set {seq}: cannot {verb} {id}, which was retired in change set {t.RetiredSeq}");
        return t;
    }
}
