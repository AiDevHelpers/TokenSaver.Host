using System.Text.Json;

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Read-only startup check with two jobs:
///     1. Confirms the workspace's own MCP client registration (<c>.mcp.json</c> +
///     <c>.claude/settings.local.json</c>'s <c>enabledMcpjsonServers</c>) actually matches this
///     server's live settings (port), and that no <c>enabledMcpjsonServers</c> entry is orphaned
///     (references a server name no longer defined in <c>.mcp.json</c>).
///     2. Surfaces operative settings the calling AI needs to know about to use tools correctly —
///     currently whether <see cref="CodeHelperConfig.AutoEnabled" /> auto-intercept is on (tool
///     prompts may be silently rewritten/handled by the draft model instead of reaching the AI
///     unmodified) and whether SimpleModelServer's router is actually reachable at
///     <see cref="TokenSaverConfig.OllamaUrl" /> (auto-intercept enabled with an unreachable
///     router silently degrades instead of erroring loudly).
///     Never writes — auto-writing client config was removed (see <c>AiProviderConfigService</c>)
///     because it raced with editors rewriting the same file and left stale entries unnoticed.
///     Findings go through <see cref="DeferredNotificationService" /> so they reach the calling
///     AI on its next tool call instead of sitting unseen in the server log.
/// </summary>
internal sealed partial class McpRegistrationCheckService(
    IOptions<TokenSaverConfig> config,
    IOptions<CodeHelperConfig> codeHelperConfig,
    IHttpClientFactory httpClientFactory,
    DeferredNotificationService deferred,
    ILogger<McpRegistrationCheckService> logger) : BackgroundService
{
    private readonly DeferredNotificationService _deferred = deferred;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP registration check: {Name} in .mcp.json points to port {ConfiguredPort} (matches this server).")]
    private static partial void LogOk(ILogger logger, string name, int configuredPort);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP registration check: {Name} in .mcp.json points to port {RegisteredPort}, but this server is actually listening on {ActualPort}. Update .mcp.json or restart with the matching port.")]
    private static partial void LogPortMismatch(ILogger logger, string name, int registeredPort, int actualPort);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP registration check: settings.local.json enables '{Name}' but no such server is defined in .mcp.json — stale/orphaned entry, reconnect will fail.")]
    private static partial void LogOrphanedEnabled(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MCP registration check skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MCP registration check skipped: {File} is not valid JSON ({ParseError})")]
    private static partial void LogInvalidJson(ILogger logger, string file, string parseError);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operative settings: auto-intercept={AutoEnabled}, contextSizeLimit={ContextSizeLimit}, simpleModelServerRouter={RouterReachable} ({OllamaUrl}).")]
    private static partial void LogOperativeSettings(ILogger logger, bool autoEnabled, int contextSizeLimit, bool routerReachable, string ollamaUrl);

    private readonly TokenSaverConfig _config = config.Value;
    private readonly CodeHelperConfig _codeHelperConfig = codeHelperConfig.Value;
    private readonly ILogger<McpRegistrationCheckService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckOperativeSettingsAsync(stoppingToken);

        string root = _config.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            LogSkipped(_logger, "no workspace root configured");
            return;
        }

        Dictionary<string, JsonElement> mcpServers = ReadMcpServers(root);
        CheckPortsMatch(mcpServers);
        CheckEnabledEntriesExist(root, mcpServers);
    }

    private async Task CheckOperativeSettingsAsync(CancellationToken ct)
    {
        bool autoEnabled = _codeHelperConfig.AutoEnabled;
        bool routerReachable = await IsSimpleModelServerReachableAsync(ct);

        LogOperativeSettings(_logger, autoEnabled, _config.ContextSizeLimit, routerReachable, _config.OllamaUrl);

        _deferred.Post(
            "TokenSaver operative settings — " +
            $"context auto-intercept: {(autoEnabled ? "ON (tool prompts may be silently rewritten/handled by the draft model)" : "OFF")}; " +
            $"context size limit: {_config.ContextSizeLimit:N0} chars; " +
            $"SimpleModelServer router: {(routerReachable ? "reachable" : "UNREACHABLE")} at {_config.OllamaUrl}.");

        if (autoEnabled && !routerReachable)
        {
            _deferred.PostQuestion(
                $"Context auto-intercept is enabled but SimpleModelServer's router is unreachable at {_config.OllamaUrl} " +
                "— tool calls that would normally go through the draft model may hang or silently pass through " +
                "unmodified instead. Either start SimpleModelServer, or disable intercept by calling the " +
                "auto-workflow calibration tool with action='toggle', enabled=false.");
        }
    }

    private async Task<bool> IsSimpleModelServerReachableAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.OllamaUrl) ||
            !Uri.TryCreate(_config.OllamaUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return false;
        }

        try
        {
            using HttpClient client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            using HttpResponseMessage response = await client.GetAsync(new Uri(baseUri, "/health"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private Dictionary<string, JsonElement> ReadMcpServers(string root)
    {
        string path = Path.Combine(root, ".mcp.json");
        if (!File.Exists(path))
        {
            LogSkipped(_logger, ".mcp.json not present");
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mcpServers", out JsonElement servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            Dictionary<string, JsonElement> result = [];
            foreach (JsonProperty prop in servers.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }

            return result;
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ".mcp.json", ex.Message);
            return [];
        }
    }

    private void CheckPortsMatch(Dictionary<string, JsonElement> mcpServers)
    {
        foreach ((string name, JsonElement entry) in mcpServers)
        {
            if (!entry.TryGetProperty("url", out JsonElement urlEl) || urlEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? url = urlEl.GetString();
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            bool isLocal = uri.Host is "localhost" or "127.0.0.1" or "::1";
            if (!isLocal)
            {
                continue;
            }

            if (uri.Port == _config.ServerPort)
            {
                LogOk(_logger, name, _config.ServerPort);
            }
            else
            {
                LogPortMismatch(_logger, name, uri.Port, _config.ServerPort);
                _deferred.PostQuestion(
                    $"TokenSaver's MCP registration for '{name}' in .mcp.json points to port {uri.Port}, " +
                    $"but this server is actually listening on {_config.ServerPort}. Edit .mcp.json's " +
                    $"\"{name}\" entry to use \"http://localhost:{_config.ServerPort}/mcp\", then reconnect.");
            }
        }
    }

    private void CheckEnabledEntriesExist(string root, Dictionary<string, JsonElement> mcpServers)
    {
        string path = Path.Combine(root, ".claude", "settings.local.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("enabledMcpjsonServers", out JsonElement enabled) ||
                enabled.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement item in enabled.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? name = item.GetString();
                if (!string.IsNullOrEmpty(name) && !mcpServers.ContainsKey(name))
                {
                    LogOrphanedEnabled(_logger, name);
                    _deferred.PostQuestion(
                        $"TokenSaver's MCP registration check found settings.local.json enables '{name}', " +
                        "but no such server is defined in .mcp.json anymore (stale entry — likely renamed " +
                        $"or removed). Remove '{name}' from enabledMcpjsonServers in .claude/settings.local.json, " +
                        "or add it back to .mcp.json's mcpServers if it was meant to still exist.");
                }
            }
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ".claude/settings.local.json", ex.Message);
        }
    }
}
