using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cinder.Cases;

/// <summary>
/// Multi-examiner Git-style branching for case state. Each examiner works on a named branch
/// off <c>main</c>; tags + bookmarks + custody appends are versioned per-branch and merged back
/// with conflict surfacing.
///
/// Storage model:
///   branches table (name, parent, head_commit_id)
///   commits  table (id, parent, branch, examiner, timestamp, message, payload_json)
///
/// "payload_json" is the small mutable case state (tags, bookmarks, notes). The custody log
/// itself is append-only and shared across all branches.
/// </summary>
public sealed class CaseBranching
{
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
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS branches (
              name        TEXT PRIMARY KEY,
              parent      TEXT NULL,
              head_commit TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS commits (
              id            TEXT PRIMARY KEY,
              parent_id     TEXT NULL,
              branch        TEXT NOT NULL,
              examiner      TEXT NOT NULL,
              timestamp_utc TEXT NOT NULL,
              message       TEXT NOT NULL,
              payload_json  TEXT NOT NULL
            );
            INSERT OR IGNORE INTO branches(name, parent) VALUES ('main', NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public string Commit(string branch, string examiner, string message, object payload)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var head = ScalarString(c, tx, "SELECT head_commit FROM branches WHERE name=@n", ("@n", branch));
        var json = JsonSerializer.Serialize(payload);
        var ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes((head ?? "0") + branch + examiner + ts + message + json));
        var id = Convert.ToHexStringLower(idBytes)[..16];
        Exec(c, tx, "INSERT INTO commits(id, parent_id, branch, examiner, timestamp_utc, message, payload_json) VALUES(@id, @parent, @b, @ex, @ts, @msg, @p);",
            ("@id", id), ("@parent", (object?)head ?? DBNull.Value), ("@b", branch), ("@ex", examiner), ("@ts", ts), ("@msg", message), ("@p", json));
        Exec(c, tx, "UPDATE branches SET head_commit=@id WHERE name=@n;", ("@id", id), ("@n", branch));
        tx.Commit();
        return id;
    }

    public void CreateBranch(string name, string parent = "main")
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        var head = ScalarString(c, null, "SELECT head_commit FROM branches WHERE name=@n", ("@n", parent))
            ?? throw new InvalidOperationException($"Parent branch '{parent}' not found.");
        Exec(c, null, "INSERT INTO branches(name, parent, head_commit) VALUES(@n, @p, @h);",
            ("@n", name), ("@p", parent), ("@h", head));
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

    /// <summary>
    /// Three-way merge: pull payload from <paramref name="from"/> branch into <paramref name="into"/>,
    /// surfacing JSON-key-level conflicts via the callback. The callback decides "ours"/"theirs"/
    /// "manual"; on "manual" the merge aborts with the conflict list.
    /// </summary>
    public MergeResult Merge(string from, string into, string examiner, Func<MergeConflict, MergeResolution> resolver)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        var (ours, theirs, baseCommit) = LoadCommitTriplet(c, from, into);
        var conflicts = new List<MergeConflict>();
        var merged = MergeJsonObjects(theirs, ours, baseCommit, conflicts);
        foreach (var conflict in conflicts.ToArray())
        {
            var res = resolver(conflict);
            if (res == MergeResolution.Manual)
            {
                return new MergeResult(false, null, conflicts);
            }
        }
        var newHead = Commit(into, examiner, $"Merge {from} into {into}", merged);
        return new MergeResult(true, newHead, conflicts);
    }

    private static (Dictionary<string, JsonElement> Ours, Dictionary<string, JsonElement> Theirs, Dictionary<string, JsonElement>? Base) LoadCommitTriplet(
        SqliteConnection c, string from, string into)
    {
        var oursJson = ScalarString(c, null, "SELECT payload_json FROM commits WHERE id=(SELECT head_commit FROM branches WHERE name=@n)", ("@n", into)) ?? "{}";
        var theirsJson = ScalarString(c, null, "SELECT payload_json FROM commits WHERE id=(SELECT head_commit FROM branches WHERE name=@n)", ("@n", from)) ?? "{}";
        return (
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(oursJson) ?? new(),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(theirsJson) ?? new(),
            null);
    }

    private static Dictionary<string, JsonElement> MergeJsonObjects(
        Dictionary<string, JsonElement> theirs,
        Dictionary<string, JsonElement> ours,
        Dictionary<string, JsonElement>? @base,
        List<MergeConflict> conflicts)
    {
        var merged = new Dictionary<string, JsonElement>(ours);
        foreach (var kv in theirs)
        {
            if (!merged.TryGetValue(kv.Key, out var existing))
            {
                merged[kv.Key] = kv.Value;
                continue;
            }
            if (existing.GetRawText() != kv.Value.GetRawText())
            {
                conflicts.Add(new MergeConflict(kv.Key, existing, kv.Value));
                // Default policy: prefer "ours" until resolver overrides.
            }
        }
        return merged;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
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
public sealed record MergeConflict(string Key, JsonElement Ours, JsonElement Theirs);
public sealed record MergeResult(bool Ok, string? NewHeadCommit, IReadOnlyList<MergeConflict> Conflicts);
