using System.Collections.Concurrent;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     The workspace root each MCP session (<c>Mcp-Session-Id</c>) has resolved. Lets a later request
///     of the same session start with its root already known, and keeps one client's root out of
///     another's requests.
/// </summary>
internal sealed class WorkspaceSessionRoots
{
    private readonly ConcurrentDictionary<string, string> _roots = new(StringComparer.Ordinal);

    /// <summary>The root a session resolved earlier, or null (unknown session or no session id).</summary>
    public string? Get(string? sessionId)
    {
        return !string.IsNullOrEmpty(sessionId) && _roots.TryGetValue(sessionId, out string? root) ? root : null;
    }

    public void Set(string? sessionId, string root)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            _roots[sessionId] = root;
        }
    }

    /// <summary>Forgets a session's root (call when the session ends).</summary>
    public void Forget(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            _ = _roots.TryRemove(sessionId, out _);
        }
    }
}
