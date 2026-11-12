using Cinder.Core.Cases;
using Cinder.Core.Custody;
using Microsoft.Data.Sqlite;

namespace Cinder.Cli;

/// <summary>
/// <c>cinder custody verify &lt;case-path&gt;</c> — walks the chain-of-custody log of every case
/// in a case file and reports, per case, whether the hash chain is intact.
///
/// <para>The exit code is the contract, because this verb is what a script or a CI job gates
/// on: <see cref="ExitOk"/> only when every case was read and every chain verified;
/// <see cref="ExitBroken"/> when any chain is broken, naming the case and the sequence number
/// where it breaks; <see cref="ExitUnreadable"/> when the file is missing, is not a Cinder case
/// database, or holds nothing to verify. A file that could not be checked is never reported as
/// good.</para>
///
/// <para>What a pass means is bounded by what the chain can prove — see
/// <see cref="CustodyLog"/>: it detects an edited, removed, reordered or spliced entry, not a
/// wholesale rewrite by someone who recomputed every hash.</para>
/// </summary>
public static class CustodyVerifyCommand
{
    public const int ExitOk = 0;
    public const int ExitBroken = 1;
    public const int ExitUnreadable = 2;

    public static async Task<int> RunAsync(string casePath, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (string.IsNullOrWhiteSpace(casePath) || !File.Exists(casePath))
        {
            // Checked first and explicitly: opening a missing path through CaseStore would
            // create an empty database and then "verify" it.
            error.WriteLine($"cinder: case file not found: {casePath}");
            return ExitUnreadable;
        }

        List<(string Id, string Name)> cases;
        HashSet<string> idsWithEntries;
        try
        {
            (cases, idsWithEntries) = ReadCaseIds(casePath);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidCastException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"cinder: not a readable Cinder case file: {casePath} ({ex.Message})");
            return ExitUnreadable;
        }

        // Custody entries whose case record is gone are still verified — and reported, since a
        // chain without its case is itself a finding.
        var known = cases.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = idsWithEntries.Where(id => !known.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

        if (cases.Count == 0 && orphans.Count == 0)
        {
            error.WriteLine($"cinder: {casePath} contains no cases; nothing was verified.");
            return ExitUnreadable;
        }

        var log = new CustodyLog(new CaseStore(casePath));
        var broken = 0;
        var entriesTotal = 0L;

        foreach (var (id, name) in cases.Concat(orphans.Select(id => (Id: id, Name: "(no case record)"))))
        {
            ct.ThrowIfCancellationRequested();
            var label = $"{Printable(id)}  \"{Printable(name)}\"";
            var isOrphan = !known.Contains(id);

            if (!Guid.TryParse(id, out var caseId) || !string.Equals(caseId.ToString("D"), id, StringComparison.Ordinal))
            {
                output.WriteLine($"BROKEN  {label}  case id is not a canonical GUID; its custody entries cannot be addressed.");
                broken++;
                continue;
            }

            CustodyVerificationResult result;
            try
            {
                result = await log.VerifyAsync(caseId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.WriteLine($"BROKEN  {label}  custody entries could not be read: {ex.Message}");
                broken++;
                continue;
            }

            entriesTotal += result.EntriesChecked;
            if (!result.Ok)
            {
                output.WriteLine(
                    $"BROKEN  {label}  chain breaks at sequence {result.FirstBrokenSequence}: {result.Reason} " +
                    $"({result.EntriesChecked} entr{(result.EntriesChecked == 1 ? "y" : "ies")} verified before the break)");
                broken++;
            }
            else if (result.EntriesChecked == 0)
            {
                // Every case is created with a CaseCreated entry, so an empty log is a log that
                // was emptied. The chain itself cannot notice that; this can.
                output.WriteLine($"BROKEN  {label}  no custody entries; every case is created with one.");
                broken++;
            }
            else if (isOrphan)
            {
                output.WriteLine($"BROKEN  {label}  {result.EntriesChecked} chained custody entries but the case record is missing.");
                broken++;
            }
            else
            {
                output.WriteLine($"OK      {label}  {result.EntriesChecked} entr{(result.EntriesChecked == 1 ? "y" : "ies")}");
            }
        }

        var total = cases.Count + orphans.Count;
        if (broken > 0)
        {
            error.WriteLine($"cinder: custody verification FAILED for {broken} of {total} case{(total == 1 ? "" : "s")} in {casePath}.");
            return ExitBroken;
        }

        output.WriteLine($"Custody chain intact: {total} case{(total == 1 ? "" : "s")}, {entriesTotal} entr{(entriesTotal == 1 ? "y" : "ies")}.");
        return ExitOk;
    }

    /// <summary>Case names and ids come out of the file being judged; keep them to one printable line.</summary>
    private static string Printable(string s) =>
        string.Concat(s.Select(c => char.IsControl(c) ? '?' : c));

    /// <summary>
    /// Lists the cases and the case ids that have custody entries, over a read-only,
    /// unpooled connection so that a file which is not a case database is rejected without
    /// being written to or held open.
    /// </summary>
    private static (List<(string Id, string Name)> Cases, HashSet<string> IdsWithEntries) ReadCaseIds(string casePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = casePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var cases = new List<(string, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM cases ORDER BY created_utc, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                cases.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT case_id FROM custody_entries;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }
        }
        return (cases, ids);
    }
}
