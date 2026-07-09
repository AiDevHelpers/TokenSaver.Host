namespace TokenSaver.MCP.Services;

/// <summary>
///     Tracks the most recent unhandled exception thrown while processing a request (MCP
///     tools/call or any other HTTP endpoint on this server), so <c>/health</c> can report
///     degraded status even when every other health check (DraftModel reachability, etc.) still
///     passes. Mirrors DraftModel.Loader's <c>ModelHealthMonitor.RecordFault</c>/<c>HasRecentFault</c>
///     design: a stack trace on the server isn't something a liveness probe can detect on its
///     own — the process is still up and answering requests — so it's surfaced as a time-boxed
///     signal instead. Only the exception's message is retained (not the full stack trace) since
///     this value is expected to be surfaced in a health response body.
/// </summary>
internal sealed class UnhandledFaultTracker
{
    /// <summary>How long a recorded fault keeps <see cref="HasRecentFault" /> true before aging out.</summary>
    public static readonly TimeSpan FaultTtl = TimeSpan.FromMinutes(2);

    private readonly Lock faultLock = new();
    private string? lastFaultMessage;
    private DateTime lastFaultAtUtc;

    public void RecordFault(Exception ex)
    {
        lock (faultLock)
        {
            lastFaultMessage = ex.Message;
            lastFaultAtUtc = DateTimeOffset.UtcNow.UtcDateTime;
        }
    }

    public bool HasRecentFault
    {
        get
        {
            lock (faultLock)
            {
                return lastFaultMessage is not null &&
                       DateTimeOffset.UtcNow.UtcDateTime - lastFaultAtUtc < FaultTtl;
            }
        }
    }

    public string? LastFaultMessage
    {
        get
        {
            lock (faultLock)
            {
                return HasRecentFault ? lastFaultMessage : null;
            }
        }
    }

    /// <summary>Clears any recorded fault immediately — used by tests; not called from production code paths.</summary>
    public void ClearFault()
    {
        lock (faultLock)
        {
            lastFaultMessage = null;
        }
    }
}
