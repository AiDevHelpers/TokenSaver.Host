using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Services;

namespace AiDevTools.TokenSaver.MCP.Middleware;

/// <summary>
///     Opens a <see cref="WorkspaceScope" /> for every request, seeded with the root the request's MCP
///     session (<c>Mcp-Session-Id</c> header) resolved earlier. Everything downstream that reads
///     <see cref="TokenSaverConfig.WorkspaceRoot" /> then answers for this session, so two clients on
///     two repositories never see each other's root. A session that has not resolved a root yet gets
///     the configured default (or empty); it never inherits another client's root.
/// </summary>
internal sealed class WorkspaceScopeMiddleware
{
    internal const string SessionHeader = "Mcp-Session-Id";

    private readonly RequestDelegate _next;

    public WorkspaceScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, WorkspaceSessionRoots roots)
    {
        string? sessionId = context.Request.Headers[SessionHeader].FirstOrDefault();
        _ = WorkspaceScope.Begin(roots.Get(sessionId));

        await _next(context);

        // The client ended the session: drop its root.
        if (HttpMethods.IsDelete(context.Request.Method))
        {
            roots.Forget(sessionId);
        }
    }
}
