using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nse.Ledger;
using Nse.Manuscript;
using Nse.Model;
using Nse.Query;
using Nse.Text;

namespace Nse.Index;

/// <summary>
/// What an index was built from. If any of the three hashes differs from the files, the index is stale. The
/// layout hash is there because the index stores line numbers, and those move when blank lines are added
/// between scenes, which changes no scene's text and so leaves the manuscript hash as it was.
/// </summary>
public sealed record IndexInfo(int Schema, int Head, string LedgerHash, string ManuscriptHash, string Layout, int SeenHead)
{
    public bool Matches(IReadOnlyList<StoredChangeSet> ledger, ManuscriptSnapshot manuscript) =>
        Schema == StoryIndex.Schema && LedgerHash == LedgerStore.HeadHash(ledger)
        && ManuscriptHash == manuscript.Hash && Layout == manuscript.Layout;
}

/// <summary>Whether a record's quote is still in the scene it cites.</summary>
public sealed record EvidenceStatus(string RecordId, string Kind, string Scene, string Quote, string Status);

/// <summary>
/// A SQLite file derived from the ledger and the manuscript. It holds nothing that cannot be rebuilt, so it
/// can be deleted at any time and is never committed. Any SQLite browser pointed at it is a read-only console.
/// For now every record is one row with its JSON and a few columns to filter on; typed tables can replace
/// this when a query needs them, and the change is a rebuild, not a migration.
/// </summary>
public static class StoryIndex
{
    public const int Schema = 3;

    private const string Ddl = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE change_set(
            seq INTEGER PRIMARY KEY, id TEXT NOT NULL, hash TEXT NOT NULL, parent TEXT, actor TEXT NOT NULL,
            note TEXT, manuscript TEXT NOT NULL, file TEXT NOT NULL);
        CREATE TABLE manuscript_scene(
            id TEXT PRIMARY KEY, tell INTEGER NOT NULL, title TEXT, file TEXT NOT NULL,
            line_start INTEGER NOT NULL, line_end INTEGER NOT NULL, hash TEXT NOT NULL, text TEXT NOT NULL);
        CREATE TABLE manuscript_block(
            scene_id TEXT NOT NULL, idx INTEGER NOT NULL, line_start INTEGER NOT NULL, line_end INTEGER NOT NULL,
            text TEXT NOT NULL, PRIMARY KEY (scene_id, idx));
        CREATE TABLE record(
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, ord INTEGER NOT NULL,
            created_seq INTEGER NOT NULL, retired_seq INTEGER, retire_reason TEXT,
            initial_approval TEXT NOT NULL, decided_approval TEXT, decided_seq INTEGER,
            scene_id TEXT, prop_id TEXT, holder_id TEXT, story_key REAL, json TEXT NOT NULL);
        CREATE INDEX record_by_kind ON record(kind, created_seq);
        CREATE INDEX record_by_holder ON record(holder_id, prop_id, story_key);
        CREATE TABLE evidence(
            record_id TEXT NOT NULL, kind TEXT NOT NULL, scene_id TEXT NOT NULL, quote TEXT NOT NULL, status TEXT NOT NULL);
        """;

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        // No pooling: a pooled handle would keep the file locked after the index is "closed".
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    public static IndexInfo? ReadInfo(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var c = Open(path, SqliteOpenMode.ReadOnly);
            var meta = new Dictionary<string, string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                meta[r.GetString(0)] = r.GetString(1);
            return new IndexInfo(int.Parse(meta["schema"], CultureInfo.InvariantCulture),
                int.Parse(meta["head"], CultureInfo.InvariantCulture), meta["ledger_hash"], meta["manuscript_hash"], meta["layout_hash"],
                int.Parse(meta["seen_head"], CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is SqliteException or KeyNotFoundException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null; // unreadable or from another version: treat as absent and rebuild
        }
    }

    /// <summary>
    /// Build the index from scratch beside the old one, then swap it in. Each build writes to a file of its own,
    /// so two processes that both find the index stale do not write into each other's work. The swap fails with
    /// an <see cref="IOException"/> if another process keeps the old file open; the old index is then left as
    /// it was, and the caller answers from the files instead.
    /// </summary>
    /// <param name="seenHead">
    /// The longest the ledger has been seen in this folder, carried forward from the index being replaced. It is
    /// the one thing in the index that cannot be rebuilt from the files, and the reason a shortened ledger is
    /// still noticed after any number of ordinary reads.
    /// </param>
    /// <param name="patient">
    /// Whether to wait about a second for another program to let go of the old file. A write or an explicit
    /// rebuild waits. A read does not: it has the files to answer from, and waiting on every read for a swap
    /// that cannot happen is a second lost each time.
    /// </param>
    public static void Rebuild(
        string path, IReadOnlyList<StoredChangeSet> ledger, Projection projection, ManuscriptSnapshot manuscript,
        int seenHead, bool patient = true)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        SweepAbandoned(dir, Path.GetFileName(path));
        var temp = FormattableString.Invariant($"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            Build(temp, ledger, projection, manuscript, Math.Max(seenHead, ledger.Count));
            Swap(temp, path, patient ? 20 : 2);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // left for a later sweep
            }
        }
    }

    /// <summary>Put the new file in place of the old. A reader has the old one open for a moment at most, so wait for it briefly.</summary>
    private static void Swap(string temp, string path, int tries)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt + 1 >= tries)
                    throw new IOException($"The index at {path} is held open by another program and could not be replaced. It is only a cache: answers are computed from the ledger and the manuscript instead.", e);
                Thread.Sleep(tries > 2 ? 50 : 15);
            }
        }
    }

    /// <summary>Remove build files that a process left behind when it was killed. Anything recent may still be in use.</summary>
    private static void SweepAbandoned(string dir, string indexName)
    {
        try
        {
            foreach (var old in Directory.GetFiles(dir, indexName + ".*.tmp*"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddMinutes(-10))
                    File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // someone else's file, or still open: not ours to worry about
        }
    }

    private static void Build(
        string temp, IReadOnlyList<StoredChangeSet> ledger, Projection projection, ManuscriptSnapshot manuscript, int seenHead)
    {
        using (var c = Open(temp, SqliteOpenMode.ReadWriteCreate))
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, Ddl);
            foreach (var (key, value) in new (string, string)[]
                     {
                         ("schema", Schema.ToString(CultureInfo.InvariantCulture)),
                         ("head", projection.Head.ToString(CultureInfo.InvariantCulture)),
                         ("ledger_hash", LedgerStore.HeadHash(ledger)), ("manuscript_hash", manuscript.Hash),
                         ("layout_hash", manuscript.Layout),
                         ("seen_head", seenHead.ToString(CultureInfo.InvariantCulture)),
                     })
                Exec(c, tx, "INSERT INTO meta VALUES (@k, @v)", ("@k", key), ("@v", value));
            foreach (var s in ledger)
                Exec(c, tx, "INSERT INTO change_set VALUES (@seq, @id, @hash, @parent, @actor, @note, @ms, @file)",
                    ("@seq", s.ChangeSet.Seq), ("@id", s.ChangeSet.Id), ("@hash", s.Hash), ("@parent", s.ChangeSet.Parent),
                    ("@actor", s.ChangeSet.Actor), ("@note", s.ChangeSet.Note), ("@ms", s.ChangeSet.Manuscript), ("@file", s.File));
            foreach (var s in manuscript.Scenes)
            {
                Exec(c, tx, "INSERT INTO manuscript_scene VALUES (@id, @tell, @title, @file, @ls, @le, @hash, @text)",
                    ("@id", s.Id), ("@tell", s.Tell), ("@title", s.Title), ("@file", s.File), ("@ls", s.LineStart),
                    ("@le", s.LineEnd), ("@hash", s.Hash), ("@text", s.Text));
                foreach (var b in s.Blocks)
                    Exec(c, tx, "INSERT INTO manuscript_block VALUES (@s, @i, @ls, @le, @text)",
                        ("@s", s.Id), ("@i", b.Index), ("@ls", b.LineStart), ("@le", b.LineEnd), ("@text", b.Text));
            }
            foreach (var t in projection.All.OrderBy(t => t.Order))
            {
                var (scene, prop, holder, key) = Columns(t.Record);
                Exec(c, tx,
                    "INSERT INTO record VALUES (@id, @kind, @ord, @created, @retired, @reason, @initial, @decided, @decidedSeq, @scene, @prop, @holder, @key, @json)",
                    ("@id", t.Record.Id), ("@kind", t.Record.KindName), ("@ord", t.Order), ("@created", t.CreatedSeq),
                    ("@retired", t.RetiredSeq), ("@reason", t.RetireReason), ("@initial", Names.Of(t.InitialApproval)),
                    ("@decided", t.DecidedApproval is { } d ? Names.Of(d) : null), ("@decidedSeq", t.DecidedSeq),
                    ("@scene", scene), ("@prop", prop), ("@holder", holder), ("@key", key),
                    ("@json", JsonSerializer.Serialize(t.Record, NseJson.Compact)));
            }
            foreach (var e in Evidence(projection, manuscript))
                Exec(c, tx, "INSERT INTO evidence VALUES (@r, @k, @s, @q, @st)",
                    ("@r", e.RecordId), ("@k", e.Kind), ("@s", e.Scene), ("@q", e.Quote), ("@st", e.Status));
            tx.Commit();
        }
    }

    /// <summary>
    /// The records that count as of a ledger head, and the telling order, read back from the index. Returns null
    /// when the index is not the one built from these files: another process may have replaced it between the
    /// caller's check and this read, and an answer must never come from a different state than was asked about.
    /// </summary>
    public static StoryState? Load(string path, int? head, IReadOnlyList<StoredChangeSet> ledger, ManuscriptSnapshot manuscript)
    {
        using var c = Open(path, SqliteOpenMode.ReadOnly);
        // One read transaction, so the check and the rows come from the same file contents. Deferred: it takes
        // a shared lock when it first reads, so any number of readers go through at once.
        using var tx = c.BeginTransaction(deferred: true);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT key, value FROM meta";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                meta[r.GetString(0)] = r.GetString(1);
        }
        if (meta.GetValueOrDefault("schema") != Schema.ToString(CultureInfo.InvariantCulture)
            || meta.GetValueOrDefault("ledger_hash") != LedgerStore.HeadHash(ledger)
            || meta.GetValueOrDefault("manuscript_hash") != manuscript.Hash
            || meta.GetValueOrDefault("layout_hash") != manuscript.Layout)
            return null;
        var at = head ?? ledger.Count;
        var live = new List<Tracked>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT json, ord, created_seq, retired_seq, retire_reason, initial_approval, decided_approval, decided_seq
                FROM record
                WHERE created_seq <= @head AND (retired_seq IS NULL OR retired_seq > @head)
                ORDER BY ord
                """;
            cmd.Parameters.AddWithValue("@head", at);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var record = JsonSerializer.Deserialize<LedgerRecord>(r.GetString(0), NseJson.Compact)!;
                var t = new Tracked(record, r.GetInt32(1), r.GetInt32(2), ParseName<Approval>(r.GetString(5)),
                    r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(6) ? null : ParseName<Approval>(r.GetString(6)), r.IsDBNull(7) ? null : r.GetInt32(7));
                if (t.LiveAt(at))
                    live.Add(t);
            }
        }
        // The telling is the manuscript as just read. The index holds the same scenes and paragraphs, which
        // the hashes above have confirmed, so there is nothing to gain by reading them back.
        return new StoryState(new StoryModel(live, at), manuscript.Scenes)
        {
            ManuscriptHash = manuscript.Hash, FromIndex = true, LedgerHead = ledger.Count,
            SeenHead = int.TryParse(meta.GetValueOrDefault("seen_head"), NumberStyles.None, CultureInfo.InvariantCulture, out var seen) ? seen : 0,
        };
    }

    /// <summary>The same answer computed straight from the ledger, for when the index cannot be used.</summary>
    public static StoryState FromFiles(Projection projection, int? head, ManuscriptSnapshot manuscript, int seenHead = 0)
    {
        var at = head ?? projection.Head;
        return new StoryState(new StoryModel(projection.LiveAt(at), at), manuscript.Scenes)
        {
            ManuscriptHash = manuscript.Hash, LedgerHead = projection.Head, SeenHead = seenHead,
        };
    }

    /// <summary>For every live record that carries a quote: is the quote still in the scene it cites?</summary>
    public static IReadOnlyList<EvidenceStatus> Evidence(Projection projection, ManuscriptSnapshot manuscript)
    {
        var result = new List<EvidenceStatus>();
        foreach (var t in projection.LiveAt(projection.Head))
        {
            var (quote, scene) = t.Record switch
            {
                Event e => (e.Quote, e.QuoteScene ?? e.Scene),
                Assertion a => (a.Quote, a.Scene),
                BeliefChange b => (b.Quote, b.QuoteScene ?? b.Scene),
                Disclosure d => (d.Quote, d.Scene),
                Placement p => (p.Quote, p.QuoteScene ?? p.Scene),
                _ => (null, null),
            };
            if (quote is null || scene is null)
                continue;
            var status = manuscript.Find(scene) is not { } found ? "scene_missing"
                : QuoteMatcher.Contains(found.Blocks.Select(b => b.Text), quote) ? "exact" : "missing";
            result.Add(new EvidenceStatus(t.Record.Id, t.Record.KindName, scene, quote, status));
        }
        return result;
    }

    private static (string? Scene, string? Prop, string? Holder, double? Key) Columns(LedgerRecord record) => record switch
    {
        SceneTime t => (t.Scene, null, null, t.StoryKey),
        FactChange f => (null, f.Prop, null, f.StoryKey),
        Event e => (e.Scene, null, null, e.StoryKey),
        Assertion a => (a.Scene, a.Prop, a.Speaker, a.StoryKey),
        BeliefChange b => (b.Scene, b.Prop, b.Holder, b.StoryKey),
        Disclosure d => (d.Scene, d.Prop, null, null),
        Placement p => (p.Scene, null, p.Object, p.StoryKey),
        Embargo e => (e.UntilScene, e.Prop, null, null),
        SceneContract c => (c.Scene, null, null, null),
        _ => (null, null, null, null),
    };

    private static T ParseName<T>(string name) where T : struct, Enum =>
        System.Enum.GetValues<T>().First(v => Names.Of(v) == name);

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
