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
/// which made the log an activity record in name only. Tools call <see cref="LogAsync(CaseSession?, string, object, CancellationToken)"/> at
/// the points where something evidential happened; the call is a no-op when no case is open
/// and never throws, because custody bookkeeping must not be able to fail a parse.</para>
/// </summary>
public static class ActiveCaseContext
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The active case session, or null when the examiner is working outside a case.</summary>
    public static CaseSession? Current { get; private set; }

    /// <summary>
    /// Raised (on the caller's thread, which is the UI thread) whenever the active case changes,
    /// including to null. Lists that belong to a case reload from this so they can never show,
    /// export or delete another case's rows.
    /// </summary>
    public static event Action<CaseSession?>? Changed;

    public static void Set(CaseSession? session)
    {
        if (ReferenceEquals(Current, session))
        {
            return;
        }
        Current = session;
        Changed?.Invoke(session);
    }

    /// <summary>
    /// Appends an entry to the active case's custody log. <paramref name="details"/> is
    /// serialised to JSON; keep it to paths, counts and digests — the log is what a court sees.
    /// </summary>
    /// <remarks>
    /// This logs to whichever case is active <em>when the call is made</em>. That is right for
    /// something that happens in an instant. For anything that runs for a while — a parse, an
    /// export, a scan — capture <see cref="Current"/> before the first await and use the
    /// overload that takes the session, or an examiner who switches case tabs mid-run gets the
    /// entry written into the wrong case's chain of custody.
    /// </remarks>
    public static Task<bool> LogAsync(string action, object details, CancellationToken ct = default) =>
        LogAsync(Current, action, details, ct);

    /// <summary>
    /// Appends an entry to the custody log of <paramref name="session"/> — the case that was
    /// active when the work began, captured by the caller — regardless of which case is active
    /// now. A null session (no case was open when the work began) logs nothing, even if a case
    /// has been opened since: the work was not done under it.
    ///
    /// <para>Returns true when the entry was written. It never throws, because custody
    /// bookkeeping must not be able to fail the work it describes — but a failure is not
    /// silent either: an entry that could not be written is a hole in the record a court
    /// reads, so the examiner is told through <see cref="ShellNotice"/>. They are told once
    /// when writing starts to fail and once more when it works again (with the number of
    /// entries lost in between), not once per entry: a parser run that logs a hundred
    /// actions against an unplugged drive is one event, not a hundred.</para>
    /// </summary>
    public static async Task<bool> LogAsync(CaseSession? session, string action, object details, CancellationToken ct = default)
    {
        if (session?.Path is null)
        {
            return false;   // no case, or one that has no file: there is no log to write to
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(details, Json);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            ReportFailure(session, action, "(details could not be serialised)", "the entry's details could not be prepared: " + ex.Message, ex);
            return false;
        }

        if (!File.Exists(session.Path))
        {
            ReportFailure(session, action, json, "its case file is no longer there (" + session.Path + ").", null);
            return false;
        }

        try
        {
            var store = new CaseStore(session.Path);
            var log = new CustodyLog(store);
            await log.AppendAsync(session.Id, Environment.UserName, action, json, ct).ConfigureAwait(false);
            ReportSuccess(session);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller withdrew the request. That is their decision, not a failure of the log.
            Serilog.Log.Information("Custody append for {Action} on case {Case} was cancelled by the caller", action, session.Name);
            return false;
        }
        catch (Exception ex)
        {
            ReportFailure(session, action, json, CrashHandler.Describe(ex), ex);
            return false;
        }
    }

    // ---- failure bursts -------------------------------------------------------------------

    private static readonly object FailureSync = new();

    /// <summary>Case file path → entries lost since writing to it last worked. Present means "in a burst".</summary>
    private static readonly Dictionary<string, int> LostEntries = new(StringComparer.OrdinalIgnoreCase);

    private static void ReportFailure(CaseSession session, string action, string detailsJson, string reason, Exception? ex)
    {
        // The entry itself goes to the application log, every time, so what was lost can be
        // reconstructed from there even though it is not in the case.
        Serilog.Log.Warning(ex, "Custody entry NOT written: {Action} on case {Case} ({Path}): {Reason} Details: {Details}",
            action, session.Name, session.Path, reason, detailsJson);

        bool first;
        lock (FailureSync)
        {
            first = !LostEntries.TryGetValue(session.Path!, out var lost);
            LostEntries[session.Path!] = lost + 1;
        }
        if (first)
        {
            ShellNotice.Error(
                $"Chain of custody is NOT being recorded for \"{session.Name}\": {reason} " +
                "Your work continues, but it is missing from the custody log until this is fixed. " +
                "Entries that could not be written are kept in Cinder's own log file.");
        }
    }

    private static void ReportSuccess(CaseSession session)
    {
        int lost;
        lock (FailureSync)
        {
            if (!LostEntries.Remove(session.Path!, out lost))
            {
                return;
            }
        }
        ShellNotice.Error(
            $"Chain of custody for \"{session.Name}\" is being recorded again. " +
            $"{lost:N0} entr{(lost == 1 ? "y was" : "ies were")} not written while it was failing; " +
            "they are in Cinder's own log file, not in the case.");
    }

    /// <summary>Forgets any failure burst in progress. For tests, which share this process-wide state.</summary>
    internal static void ResetFailureTracking()
    {
        lock (FailureSync)
        {
            LostEntries.Clear();
        }
    }
}
