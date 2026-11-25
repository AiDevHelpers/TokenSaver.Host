using AiDevTools.TokenSaver.MCP.Services;

namespace AiDevTools.TokenSaver.MCP.Middleware;

/// <summary>
///     Wraps every request in a try/catch that records unhandled exceptions into
///     <see cref="UnhandledFaultTracker" /> before re-throwing, so a stack trace anywhere
///     downstream (an MCP tool call, <see cref="ContextInterceptMiddleware" />, the
///     provider-registration endpoint, etc.) is visible from <c>/health</c> even though the
///     process itself keeps running and answering requests normally otherwise.
///     <para>
///     Deliberately registered as early as possible in the pipeline (see Program.cs) so it wraps
///     every other middleware and the MCP endpoint itself — the goal is "any unhandled exception
///     on this server", not just ones from a specific subsystem.
///     </para>
///     <para>
///     Re-throws rather than swallowing: this middleware only adds observability. Downstream
///     error handling (ASP.NET Core's default 500 response, or any exception-handler middleware
///     added later) behaves exactly as it would without this middleware present.
///     </para>
/// </summary>
internal sealed class FaultTrackingMiddleware(RequestDelegate next, UnhandledFaultTracker faultTracker)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            faultTracker.RecordFault(ex);
            throw;
        }
    }
}
