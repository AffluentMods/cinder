using System.Text.Json;
using Cinder.App.ViewModels;
using Cinder.Core.Cases;
using Cinder.Core.Custody;

namespace Cinder.App.Services;

/// <summary>
/// The case the examiner currently has in front of them, made reachable from any tool so that
/// examiner actions land in that case's chain-of-custody log.
///
/// <para>Before this existed the custody log recorded case creation and manual hashing and
/// nothing else — every parser run, mount, verification, export and report went unrecorded,
/// which made the log an activity record in name only. Tools call <see cref="LogAsync"/> at
/// the points where something evidential happened; the call is a no-op when no case is open
/// and never throws, because custody bookkeeping must not be able to fail a parse.</para>
/// </summary>
public static class ActiveCaseContext
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The active case session, or null when the examiner is working outside a case.</summary>
    public static CaseSession? Current { get; private set; }

    public static void Set(CaseSession? session) => Current = session;

    /// <summary>
    /// Appends an entry to the active case's custody log. <paramref name="details"/> is
    /// serialised to JSON; keep it to paths, counts and digests — the log is what a court sees.
    /// </summary>
    public static async Task LogAsync(string action, object details, CancellationToken ct = default)
    {
        var session = Current;
        if (session?.Path is null || !File.Exists(session.Path))
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(details, Json);
            var store = new CaseStore(session.Path);
            var log = new CustodyLog(store);
            await log.AppendAsync(session.Id, Environment.UserName, action, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Surface in the log file, never to the tool that was doing real work.
            Serilog.Log.Warning(ex, "Custody append failed for {Action} on case {Case}", action, session.Name);
        }
    }
}
