using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cinder.Cases;

/// <summary>
/// Multi-examiner Git-style branching for case state. Each examiner works on a named branch
/// off <c>main</c>; tags + bookmarks + notes are versioned per-branch and merged back with
/// conflict surfacing.
///
/// Storage model:
///   branches table (name, parent, head_commit)
///   commits  table (id, parent_id, merge_parent_id, branch, examiner, timestamp, message, payload_json)
///
/// "payload_json" is the small mutable case state (tags, bookmarks, notes) as one JSON object.
/// The custody log itself is append-only and shared across all branches; it is not stored here.
///
/// <para>A merge commit records both of its parents. That is what makes the next merge between
/// the same two branches a true three-way merge against their latest common ancestor instead
/// of against the original fork point.</para>
/// </summary>
public sealed class CaseBranching
{
    private const char Separator = (char)0x1F;

    private readonly string _connectionString;

    public CaseBranching(string casePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = casePath, Pooling = true, ForeignKeys = true }.ToString();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS branches (
                  name        TEXT PRIMARY KEY,
                  parent      TEXT NULL,
                  head_commit TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS commits (
                  id              TEXT PRIMARY KEY,
                  parent_id       TEXT NULL,
                  merge_parent_id TEXT NULL,
                  branch          TEXT NOT NULL,
                  examiner        TEXT NOT NULL,
                  timestamp_utc   TEXT NOT NULL,
                  message         TEXT NOT NULL,
                  payload_json    TEXT NOT NULL
                );
                INSERT OR IGNORE INTO branches(name, parent) VALUES ('main', NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        // Case files created before merge parents were recorded have no such column.
        var hasMergeParent = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('commits');";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                hasMergeParent |= r.GetString(0) == "merge_parent_id";
            }
        }
        if (!hasMergeParent)
        {
            Exec(c, null, "ALTER TABLE commits ADD COLUMN merge_parent_id TEXT NULL;");
        }
    }

    /// <summary>Records a new commit at the head of <paramref name="branch"/> and returns its id.</summary>
    /// <exception cref="InvalidOperationException">
    /// The branch does not exist. A commit to a misspelt branch used to be inserted and then
    /// attached to nothing — an id came back, and the work was unreachable.
    /// </exception>
    public string Commit(string branch, string examiner, string message, object payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentNullException.ThrowIfNull(examiner);
        ArgumentNullException.ThrowIfNull(message);

        var json = JsonSerializer.Serialize(payload);
        using (var probe = JsonDocument.Parse(json))
        {
            if (probe.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Commit payload must serialise to a JSON object; merges work key by key.", nameof(payload));
            }
        }

        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var head = RequireBranch(c, tx, branch);
        var id = InsertCommit(c, tx, branch, examiner, message, json, head, mergeParent: null);
        tx.Commit();
        return id;
    }

    /// <summary>Creates <paramref name="name"/> pointing at the current head of <paramref name="parent"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The parent branch does not exist, or <paramref name="name"/> already does. A parent that
    /// exists but has no commits yet — <c>main</c> in a new case — is a valid starting point.
    /// </exception>
    public void CreateBranch(string name, string parent = "main")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(parent);

        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var head = RequireBranch(c, tx, parent);
        if (BranchExists(c, tx, name))
        {
            throw new InvalidOperationException($"Branch '{name}' already exists.");
        }
        Exec(c, tx, "INSERT INTO branches(name, parent, head_commit) VALUES(@n, @p, @h);",
            ("@n", name), ("@p", parent), ("@h", head));
        tx.Commit();
    }

    public IReadOnlyList<string> ListBranches()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM branches ORDER BY name;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>The id of the commit at the head of <paramref name="branch"/>, or null if it has none.</summary>
    public string? GetHead(string branch)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        return RequireBranch(c, null, branch);
    }

    /// <summary>The payload at the head of <paramref name="branch"/>, key by key; empty if it has no commits.</summary>
    public IReadOnlyDictionary<string, JsonElement> GetPayload(string branch)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        return LoadPayload(c, RequireBranch(c, null, branch));
    }

    /// <summary>
    /// Three-way merge of <paramref name="from"/> into <paramref name="into"/>, key by key,
    /// against the two heads' latest common ancestor:
    /// <list type="bullet">
    /// <item>a key only one side changed (added, edited or removed) takes that side's change;</item>
    /// <item>a key both sides changed to the same thing takes that;</item>
    /// <item>a key both sides changed differently is a conflict and goes to
    /// <paramref name="resolver"/>, whose answer is applied: <see cref="MergeResolution.Ours"/>
    /// keeps <paramref name="into"/>'s value, <see cref="MergeResolution.Theirs"/> takes
    /// <paramref name="from"/>'s, <see cref="MergeResolution.Manual"/> aborts the merge with
    /// nothing committed.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Either branch does not exist, or <paramref name="into"/> moved while the conflicts were
    /// being resolved (nothing is committed; merge again).
    /// </exception>
    public MergeResult Merge(string from, string into, string examiner, Func<MergeConflict, MergeResolution> resolver)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(into);
        ArgumentNullException.ThrowIfNull(resolver);
        if (string.Equals(from, into, StringComparison.Ordinal))
        {
            throw new ArgumentException("A branch cannot be merged into itself.", nameof(from));
        }

        using var c = new SqliteConnection(_connectionString);
        c.Open();

        // Read phase, outside a write transaction: the resolver may be a person at a dialog,
        // and the case file must not stay locked while they think.
        var oursHead = RequireBranch(c, null, into);
        var theirsHead = RequireBranch(c, null, from);
        var ours = LoadPayload(c, oursHead);
        var theirs = LoadPayload(c, theirsHead);
        var @base = LoadPayload(c, FindMergeBase(c, oursHead, theirsHead));

        var merged = new Dictionary<string, JsonElement>(ours, StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();

        foreach (var key in ours.Keys.Union(theirs.Keys, StringComparer.Ordinal).Union(@base.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inOurs = ours.TryGetValue(key, out var o);
            var inTheirs = theirs.TryGetValue(key, out var t);
            var inBase = @base.TryGetValue(key, out var b);

            if (Same(inOurs, o, inTheirs, t))
            {
                continue; // both sides agree (including "both removed it")
            }
            if (Same(inTheirs, t, inBase, b))
            {
                continue; // only ours changed: already in merged
            }
            if (Same(inOurs, o, inBase, b))
            {
                Apply(merged, key, inTheirs, t); // only theirs changed
                continue;
            }

            conflicts.Add(new MergeConflict(key, inOurs ? o : default, inTheirs ? t : default));
        }

        // Every conflict is found before any is put to the resolver, so an aborted merge
        // reports the complete list rather than the conflicts up to the one that stopped it.
        foreach (var conflict in conflicts)
        {
            switch (resolver(conflict))
            {
                case MergeResolution.Manual:
                    return new MergeResult(false, null, conflicts);
                case MergeResolution.Theirs:
                    Apply(merged, conflict.Key, conflict.Theirs.ValueKind != JsonValueKind.Undefined, conflict.Theirs);
                    break;
                case MergeResolution.Ours:
                default:
                    break;
            }
        }

        // Write phase. If someone committed to the target meanwhile, the merge was computed
        // against a state that no longer exists; committing it would silently drop their work.
        using var tx = c.BeginTransaction();
        var currentOurs = RequireBranch(c, tx, into);
        var currentTheirs = RequireBranch(c, tx, from);
        if (!string.Equals(currentOurs, oursHead, StringComparison.Ordinal) ||
            !string.Equals(currentTheirs, theirsHead, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Branch '{into}' or '{from}' changed while the merge was being resolved. Nothing was committed; merge again.");
        }
        var newHead = InsertCommit(c, tx, into, examiner, $"Merge {from} into {into}",
            JsonSerializer.Serialize(merged), oursHead, mergeParent: theirsHead);
        tx.Commit();
        return new MergeResult(true, newHead, conflicts);
    }

    private static void Apply(Dictionary<string, JsonElement> merged, string key, bool present, JsonElement value)
    {
        if (present)
        {
            merged[key] = value;
        }
        else
        {
            merged.Remove(key);
        }
    }

    /// <summary>Two sides are the same when both lack the key or both hold the same JSON value.</summary>
    private static bool Same(bool aPresent, JsonElement a, bool bPresent, JsonElement b)
    {
        if (aPresent != bPresent)
        {
            return false;
        }
        return !aPresent || JsonElement.DeepEquals(a, b);
    }

    /// <summary>
    /// The merge base of two heads: a commit reachable from both (following both parents of
    /// merge commits) that is not an ancestor of any other such commit. Null when the histories
    /// share nothing — the base is then the empty state.
    ///
    /// <para>"Closest by hop count" is not the same thing: after a merge, the merged-in head
    /// and the original fork point are equally many hops back, and picking the fork point
    /// would re-apply changes that were already merged.</para>
    /// </summary>
    private static string? FindMergeBase(SqliteConnection c, string? oursHead, string? theirsHead)
    {
        if (oursHead is null || theirsHead is null)
        {
            return null;
        }

        var common = AncestorsOf(c, oursHead);
        common.IntersectWith(AncestorsOf(c, theirsHead));
        if (common.Count == 0)
        {
            return null;
        }

        var candidates = new HashSet<string>(common, StringComparer.Ordinal);
        foreach (var id in common)
        {
            if (!candidates.Contains(id))
            {
                continue;
            }
            var older = AncestorsOf(c, id);
            older.Remove(id);
            candidates.ExceptWith(older);
        }

        // A criss-cross history can leave more than one; any of them is a sound base, and the
        // ordinal minimum makes the choice repeatable.
        return candidates.Order(StringComparer.Ordinal).First();
    }

    /// <summary>The commit itself and everything reachable from it.</summary>
    private static HashSet<string> AncestorsOf(SqliteConnection c, string commitId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(commitId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
            {
                continue;
            }
            foreach (var p in ParentsOf(c, id))
            {
                queue.Enqueue(p);
            }
        }
        return seen;
    }

    private static List<string> ParentsOf(SqliteConnection c, string commitId)
    {
        var parents = new List<string>(2);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT parent_id, merge_parent_id FROM commits WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", commitId);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            if (!r.IsDBNull(0)) parents.Add(r.GetString(0));
            if (!r.IsDBNull(1)) parents.Add(r.GetString(1));
        }
        return parents;
    }

    private static Dictionary<string, JsonElement> LoadPayload(SqliteConnection c, string? commitId)
    {
        if (commitId is null)
        {
            return new(StringComparer.Ordinal);
        }
        var json = ScalarString(c, null, "SELECT payload_json FROM commits WHERE id=@id", ("@id", commitId))
            ?? throw new InvalidOperationException($"Commit '{commitId}' is referenced but missing from the case file.");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
        return new Dictionary<string, JsonElement>(parsed, StringComparer.Ordinal);
    }

    private static bool BranchExists(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM branches WHERE name=@n;";
        if (tx is not null) cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Returns the branch's head (null when it has no commits yet) and throws when the branch
    /// itself does not exist — two states a bare <c>SELECT head_commit</c> cannot tell apart.
    /// </summary>
    private static string? RequireBranch(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        if (!BranchExists(c, tx, name))
        {
            throw new InvalidOperationException($"Branch '{name}' not found.");
        }
        return ScalarString(c, tx, "SELECT head_commit FROM branches WHERE name=@n", ("@n", name));
    }

    private static string InsertCommit(SqliteConnection c, SqliteTransaction tx, string branch, string examiner,
        string message, string json, string? parent, string? mergeParent)
    {
        var ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // Fields are joined with U+001F so that ("ab", "c") and ("a", "bc") cannot produce the
        // same id; a random nonce keeps two otherwise identical commits in the same tick apart.
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var material = string.Join(Separator, parent ?? "0", mergeParent ?? "0", branch, examiner, ts, message, json, nonce);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];

        Exec(c, tx,
            "INSERT INTO commits(id, parent_id, merge_parent_id, branch, examiner, timestamp_utc, message, payload_json) " +
            "VALUES(@id, @parent, @merge, @b, @ex, @ts, @msg, @p);",
            ("@id", id), ("@parent", parent), ("@merge", mergeParent), ("@b", branch), ("@ex", examiner),
            ("@ts", ts), ("@msg", message), ("@p", json));

        var updated = ExecCount(c, tx, "UPDATE branches SET head_commit=@id WHERE name=@n;", ("@id", id), ("@n", branch));
        if (updated != 1)
        {
            throw new InvalidOperationException($"Branch '{branch}' not found; the commit was not recorded.");
        }
        return id;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
        => ExecCount(c, tx, sql, args);

    private static int ExecCount(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static string? ScalarString(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : (string?)r;
    }
}

public enum MergeResolution { Ours, Theirs, Manual }

/// <summary>
/// A key both branches changed differently. A side that removed the key has a
/// <see cref="JsonElement"/> whose <see cref="JsonElement.ValueKind"/> is
/// <see cref="JsonValueKind.Undefined"/>.
/// </summary>
public sealed record MergeConflict(string Key, JsonElement Ours, JsonElement Theirs);
public sealed record MergeResult(bool Ok, string? NewHeadCommit, IReadOnlyList<MergeConflict> Conflicts);
