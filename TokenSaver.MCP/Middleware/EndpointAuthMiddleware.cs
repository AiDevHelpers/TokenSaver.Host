using Microsoft.Extensions.Options;

using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Security;

namespace TokenSaver.MCP.Middleware;

/// <summary>
///     The trust boundary for the HTTP endpoint. For /mcp and /api/* it refuses, in order:
///     1. a <c>Host</c> header that is not loopback (DNS rebinding),
///     2. any <c>Origin</c> header (a browser page; MCP clients and the extensions send none),
///     3. a missing or wrong <c>Authorization: Bearer</c> token (when <c>RequireAuth</c> is on).
///     /health and /health/ready stay open so liveness checks need no secret.
/// </summary>
internal sealed class EndpointAuthMiddleware
{
    private static readonly string[] s_loopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    private readonly RequestDelegate _next;

    public EndpointAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    internal static bool IsProtected(PathString path)
    {
        return path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLoopbackHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        // Strip the port: "localhost:11436", "[::1]:11436", "127.0.0.1".
        string host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']', StringComparison.Ordinal);
            host = close < 0 ? host : host[..(close + 1)];
        }
        else
        {
            int colon = host.LastIndexOf(':');
            if (colon >= 0 && host.IndexOf(':', StringComparison.Ordinal) == colon)
            {
                host = host[..colon];
            }
        }

        return s_loopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? BearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        return authorization is not null && authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    public async Task InvokeAsync(HttpContext context, AuthTokenStore tokens, IOptions<TokenSaverConfig> config)
    {
        if (!IsProtected(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (!IsLoopbackHost(context.Request.Headers.Host.ToString()))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Host not allowed.");
            return;
        }

        if (context.Request.Headers.ContainsKey("Origin"))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Browser requests (Origin header) are not allowed.");
            return;
        }

        if (config.Value.RequireAuth && !tokens.IsValid(BearerToken(context.Request.Headers.Authorization.ToString())))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Reject(context, StatusCodes.Status401Unauthorized, "A valid bearer token is required.");
            return;
        }

        await _next(context);
    }

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(message);
    }
}
