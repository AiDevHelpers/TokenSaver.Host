using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Formerly a background service that waited for SimpleModelServer to warm up, then wrote this
///     MCP server's entry into every detected AI provider config (Continue, Claude, Cursor,
///     VS Code, Sixth, etc.) on every startup. That auto-write behavior is removed —
///     <see cref="AiProviderConfigService" /> no longer detects or writes any provider
///     configs, so this service now no-ops immediately instead of waiting on SimpleModelServer for
///     no payoff. Use the "TokenSaver: Open Install Prompt" VS Code command (or read
///     <c>INSTALL_PROMPT.md</c> directly) to wire up a provider manually instead.
/// </summary>
internal sealed partial class ProviderAutoRegistrationService(
    ILogger<ProviderAutoRegistrationService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "AI provider auto-registration is disabled. Use the \"TokenSaver: Open Install " +
            "Prompt\" VS Code command (or INSTALL_PROMPT.md) to configure a provider manually.");
        return Task.CompletedTask;
    }
}
