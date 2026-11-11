using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog.Events;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TokenSaver.Common.Logging;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Installation;
using TokenSaver.Common.Models;
using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Docs;
using TokenSaver.Docs.Prompts;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.MCP.HealthChecks;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;
using TokenSaver.MCP;
using TokenSaver.MCP.Endpoints;
using TokenSaver.OllamaIntegration.Services;


/// <summary>Registers the MCP server, health checks and HTTP endpoints for the host (kept out of Program's top-level statements).</summary>
internal static class TokenSaverHostRegistration
{
    /// <summary>Adds the MCP server, its tool/resource/prompt registrations, and the stdio transport when launched that way.</summary>
    internal static void AddTokenSaverMcpServer(this IServiceCollection services, bool isStdio)
    {
        // Both SSE (GET) and streamable-http (POST) are supported on the same /mcp endpoint.
        // Stateless=true allows SSE clients while still handling streamable-http from others.
        IMcpServerBuilder mcpBuilder = services.AddMcpServer(options =>
            {
                options.ServerInstructions = DocResourceService.GetAgent();
            })
            .WithHttpTransport(o => { o.Stateless = true; })
            .WithTools<LocalModelHelperTools>()
            .WithTools<AccuracyTestTools>()
            .WithTools<ContextDiagnosticsTools>()
            .WithTools<CodeHelperTools>()
            .WithTools<AutoWorkflowTools>()
            .WithTools<HelpTools>()
            .WithTools<TokenEconomyTools>()
            .WithTools<SessionTools>()
            .WithTools<DevLoopTools>()
            .WithTools<SemanticIndexTools>()
            .WithTools<ManageConfigTools>()
            .WithTools<IDELauncherTools>()
            .WithTools<DebugOutputTools>()
            .WithTools<DebuggerControlTools>()
            .WithTools<DebuggerAnalysisTools>()
            .WithTools<TestExecutionTools>()
            .WithTools<WslControlTools>()
            .WithTools<LanguageHelperTools>()
            .WithTools<InstructionTools>()
            .WithResources<DocResourceService>()
            .WithPrompts<BuildConciseContextPrompt>();

        if (isStdio)
        {
            _ = mcpBuilder.WithStdioServerTransport();
        }
    }

    /// <summary>Health checks: simplemodelserver is tagged "ready" (gates /health/ready); others are untagged.</summary>
    internal static void AddTokenSaverHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<SimpleModelServerHealthCheck>("simplemodelserver", tags: ["ready"])
            .AddCheck<UnhandledFaultHealthCheck>("unhandled-fault")
            .AddCheck<SelfTestHealthCheck>("self-test")
            .AddCheck<FolderIndexHealthCheck>("folder-index");
    }

    /// <summary>Maps the MCP, debugger, provider-registration and health endpoints.</summary>
    internal static void MapTokenSaverEndpoints(this WebApplication app, Func<HttpContext, HealthReport, Task> healthWriter)
    {
        app.MapMcp("/mcp");

        app.MapDebuggerEndpoints();

        app.MapDebuggerStreamingEndpoints();

        app.MapGet("/api/register-providers", async (
            AiProviderConfigService aiProviders,
            IOptions<TokenSaverConfig> opts,
            HttpContext httpCtx) =>
        {
            int port = opts.Value.ServerPort;
            string workspaceRoot = opts.Value.WorkspaceRoot;

            string serverExePath = httpCtx.Request.Query["claudeProjectPath"].FirstOrDefault()
                                ?? httpCtx.Request.Query["serverExePath"].FirstOrDefault()
                                ?? Process.GetCurrentProcess().MainModule?.FileName
                                ?? "";

            IReadOnlyList<ConfigUpdateResult> results = await aiProviders.UpdateAllProvidersAsync(
                port, workspaceRoot: workspaceRoot, serverExePath: serverExePath);
            return Results.Ok(results.Select(r => new
            {
                provider = r.Provider.ToString(),
                success = r.Success,
                wasAlreadyConfigured = r.WasAlreadyConfigured,
                configPath = r.ConfigPath,
                message = r.Message
            }));
        });

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = healthWriter
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = healthWriter
        });
    }
}
