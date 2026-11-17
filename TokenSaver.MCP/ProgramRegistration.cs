using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog.Events;
using System.Collections.ObjectModel;
using System.Diagnostics;
using SperryDev.Logger.Configuration;
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
            .WithTools<IndexChangeTools>()
            .WithTools<AutoWorkflowTools>()
            .WithTools<HelpTools>()
            .WithTools<TokenEconomyTools>()
            .WithTools<SessionTools>()
            .WithTools<DevLoopTools>()
            .WithTools<SemanticIndexTools>()
            .WithTools<ManageConfigTools>()
            .WithTools<DebuggerAnalysisTools>()
            .WithTools<WslControlTools>()
            .WithTools<LanguageHelperTools>()
            .WithTools<InstructionTools>()
            .WithTools<LogTools>()
            .WithTools<IdeTools>()
            .WithTools<ManagedProcessTools>()
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
    /// <summary>Registers the TokenSaver services, health checks and MCP server.</summary>
    internal static void RegisterTokenSaverServices(WebApplicationBuilder builder, TokenSaverConfig ollamaConfig, bool isStdio)
    {
        // ═══════════════════════════════════════════════════════════════════════════════
        // ── REGISTER SERVICES (via extension methods) ──
        // ═══════════════════════════════════════════════════════════════════════════════

        // Compute User-Agent header with version for SimpleModelServer requests
        string version = VersionDetector.GetVersionString();
        string userAgent = $"TokenSaver/{version}";

        // Stamp the User-Agent on EVERY HttpClient the factory produces — including the unnamed
        // clients created via IHttpClientFactory.CreateClient() by the background probes
        // (EndpointHealthMonitor's /api/tags poll, McpRegistrationCheckService's /health poll). Without
        // this, those probes reach SimpleModelServer's router with no User-Agent and show up as an
        // unidentified caller in its request log. Applying it here (rather than per-typed-client) keeps
        // the identifier in one place and guarantees any future HttpClient is attributed too.
        builder.Services.ConfigureHttpClientDefaults(http =>
            http.ConfigureHttpClient(client =>
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent)));

        builder.Services
            .AddConfigurationSections(builder.Configuration)
            .AddContextPipeline()
            .AddCodeHelperServices()
            .AddIndexingServices()
            .AddMcpCoreServices()
            .AddTokenEconomyServices()
            .AddOllamaHttpClients(ollamaConfig)
            .AddPipelineHttpClients(ollamaConfig)
            .AddAgentHelpersAdapters(ollamaConfig.OllamaModel)
            .AddWorkspaceSessionServices()
            .AddDebuggerServices()
            .AddWslControlServices()
            .AddLanguageHelperServices()
            .AddLogHelperServices();

        builder.Services.AddTokenSaverHealthChecks();

        builder.Services.AddTokenSaverMcpServer(isStdio);
    }

}

/// <summary>Host bootstrap: configuration files, logging, startup/shutdown logging and the server run loop.</summary>
internal static class TokenSaverHostBootstrap
{
    /// <summary>Loads optional --server-config, Windows Service hosting, and logging/config binding.</summary>
    internal static void ConfigureHostConfiguration(WebApplicationBuilder builder, string[] args, bool isWindowsService)
    {
        // ── Load optional external server config file ──
        // --server-config <path> layers an additional JSON config file on top of the default
        // appsettings.json. Must be an absolute path. See below for rationale.
        string? serverConfigPath = GetArgValue(args, "--server-config");
        if (!string.IsNullOrWhiteSpace(serverConfigPath))
        {
            if (!Path.IsPathRooted(serverConfigPath))
            {
                Console.Error.WriteLine(
                    $"--server-config must be an absolute path (got '{serverConfigPath}'). " +
                    "Relative paths resolve against the project's source directory under `dotnet run`, " +
                    "not the shell's working directory — pass a full path instead.");
                Program.PauseForConsoleIfInteractive(isWindowsService);

                Program.WriteFatal("BadArgument", $"--server-config must be an absolute path (got '{serverConfigPath}').");
                Environment.Exit(1);
            }

            builder.Configuration.AddJsonFile(serverConfigPath, optional: false, reloadOnChange: true);
        }

        // ── Windows Service support ──
        // A no-op when launched normally (console, or spawned stdio by an MCP host) — only takes
        // effect when the SCM launches this exe non-interactively.
        if (isWindowsService)
        {
            builder.Host.UseWindowsService(o => o.ServiceName = "TokenSaverMCP");
        }

        // ── Configure logging ──
        // Serilog-backed logging via TokenSaver.Common.Logging (shared with SimpleModelServer).
        // Providers: Console (single-line) + File (%ProgramData%\TokenSaver\logs) + EventLog + VS Code JSON
        if (isWindowsService && OperatingSystem.IsWindows())
        {
            EventLogRegistration.EnsureTokenSaverEventLogSourceExists();
        }

        LogEventLevel minimumLevel =
            builder.Configuration.GetValue<bool>($"{TokenSaverConfig.SectionName}:VerboseLogging")
                ? LogEventLevel.Debug
                : LogEventLevel.Information;

        builder.Logging.ClearProviders();
        builder.Host.UseAppLogging("TokenSaver", minimumLevel, cfg =>
        {
            cfg.MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning);
            cfg.MinimumLevel.Override("Microsoft.Extensions.Diagnostics.HealthChecks", LogEventLevel.Warning);
        });

        // ── Configure TokenSaver ──
        builder.Services.Configure<TokenSaverConfig>(
            builder.Configuration.GetSection(TokenSaverConfig.SectionName));
        builder.Configuration.AddEnvironmentVariables("LOCALHELPER_");
        builder.Configuration.AddEnvironmentVariables("TOKENSAVER_");
    }

    /// <summary>Registers HTTP context access and resolves the effective Ollama/TokenSaver configuration.</summary>
    internal static TokenSaverConfig ConfigureOllamaAndHttpServices(WebApplicationBuilder builder)
    {
        // ── HTTP context access ──
        builder.Services.AddHttpContextAccessor();

        // ── Resolve Ollama configuration ──
        TokenSaverConfig ollamaConfig = builder.Configuration
            .GetSection(TokenSaverConfig.SectionName)
            .Get<TokenSaverConfig>() ?? new TokenSaverConfig();

        // Propagate resolved configuration back into DI
        string resolvedOllamaUrl = ollamaConfig.OllamaUrl;
        builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.OllamaUrl = resolvedOllamaUrl);

        Collection<OllamaEndpointConfig> resolvedEndpoints = ollamaConfig.Endpoints;
        builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.Endpoints = resolvedEndpoints);

        // WorkspaceRoot is resolved lazily by WorkspaceRootSessionResolver on first tool call
        if (!string.IsNullOrWhiteSpace(ollamaConfig.WorkspaceRoot))
        {
            builder.Services.PostConfigure<TokenSaverConfig>(cfg => cfg.WorkspaceRoot = ollamaConfig.WorkspaceRoot);
        }

        // Behavior config path (defaults to {workspaceRoot}/.tokensaver/config.json)
        if (!string.IsNullOrWhiteSpace(ollamaConfig.BehaviorConfigPath))
        {
            Environment.SetEnvironmentVariable("TOKENSAVER_LOCAL_CONFIG_PATH", ollamaConfig.BehaviorConfigPath);
        }

                return ollamaConfig;
    }

    /// <summary>Logs the server-starting line with the resolved port, Ollama URL and workspace.</summary>
    internal static void LogServerStarting(WebApplication app, bool isStdio)
    {
        using (IServiceScope scope = app.Services.CreateScope())
        {
            ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            TokenSaverConfig config = scope.ServiceProvider.GetRequiredService<IOptions<TokenSaverConfig>>().Value;

            string transportMode = isStdio ? "stdio+http" : "http";
            Program.LogServerStarting(logger, config.ServerPort, config.OllamaUrl, config.WorkspaceRoot ?? "(none)", transportMode);

            // Warm up model/index asynchronously (non-blocking)
            // The workspace index service is registered but warmup is deferred until first tool call
            // when WorkspaceRoot is actually known (resolved by WorkspaceRootSessionResolver).
        }
    }

    /// <summary>Runs the HTTP server, mapping port-in-use and fatal failures to clear exits.</summary>
    internal static void RunServer(WebApplication app)
    {
        try
        {
            TokenSaverConfig config = app.Services.GetRequiredService<IOptions<TokenSaverConfig>>().Value;
            // Loopback only: the server exposes command-execution tools and has no auth.
            app.Urls.Clear();
            app.Urls.Add($"http://127.0.0.1:{config.ServerPort}");
            app.Urls.Add($"http://[::1]:{config.ServerPort}");
            app.Run();
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            using (IServiceScope scope = app.Services.CreateScope())
            {
                ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                TokenSaverConfig config = scope.ServiceProvider.GetRequiredService<IOptions<TokenSaverConfig>>().Value;
                Program.LogPortInUse(logger, config.ServerPort);
            }

            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            // Anything that stops the server after startup (host failure, fatal error in a hosted service)
            // is recorded before the process exits; the exception then propagates as before.
            Program.WriteFatal("ServerStopped", ex);
            throw;
        }
    }

    internal static string? GetArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Logs who requested shutdown, and when, once the host is stopping.</summary>
    internal static void RegisterShutdownLogging(WebApplication app)
    {
            IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            ILogger<Program> stopLogger = app.Services.GetRequiredService<ILogger<Program>>();
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            _ = lifetime.ApplicationStopping.Register(() =>
                Program.LogShutdownRequested(stopLogger, (DateTimeOffset.UtcNow - startedAt).TotalSeconds, Program.ReadAndClearStopRequest()));
    }

}
