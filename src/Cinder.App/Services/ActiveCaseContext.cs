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
    public static Task LogAsync(string action, object details, CancellationToken ct = default) =>
        LogAsync(Current, action, details, ct);

    /// <summary>
    /// Appends an entry to the custody log of <paramref name="session"/> — the case that was
    /// active when the work began, captured by the caller — regardless of which case is active
    /// now. A null session (no case was open when the work began) logs nothing, even if a case
    /// has been opened since: the work was not done under it.
    /// </summary>
    public static async Task LogAsync(CaseSession? session, string action, object details, CancellationToken ct = default)
    {
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
