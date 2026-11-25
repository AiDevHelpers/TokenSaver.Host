#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using System.Net;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class McpRegistrationCheckServiceTests : IDisposable
{
    private readonly string _workspaceRoot;

    public McpRegistrationCheckServiceTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("mrcTests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<DeferredNotificationService> RunAsync(
        TokenSaverConfig tokenSaverConfig,
        CodeHelperConfig codeHelperConfig,
        HttpStatusCode healthStatus = HttpStatusCode.OK)
    {
        DeferredNotificationService deferred = new();
        FakeHttpClientFactory httpClientFactory = new(healthStatus);

        McpRegistrationCheckService service = new(
            Options.Create(tokenSaverConfig),
            Options.Create(codeHelperConfig),
            httpClientFactory,
            deferred,
            NullLogger<McpRegistrationCheckService>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        if (service.ExecuteTask is not null)
        {
            await service.ExecuteTask;
        }

        return deferred;
    }

    private void WriteMcpJson(string content)
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, ".mcp.json"), content);
    }

    private void WriteClaudeSettings(string content)
    {
        string dir = Path.Combine(_workspaceRoot, ".claude");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.local.json"), content);
    }

    [Fact]
    public async Task NoMcpJson_ProducesNoPortMismatchNotification()
    {
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.DoesNotContain(items, i => i.Kind == DeferredNotificationService.ItemKind.Question && i.Message.Contains("points to port", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchingPort_ProducesNoMismatchNotification()
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:11436/mcp" } } }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.DoesNotContain(items, i => i.Message.Contains("tokensaver", StringComparison.Ordinal) && i.Message.Contains("points to port", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MismatchedPort_ProducesPostQuestion()
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:9999/mcp" } } }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i =>
            i.Kind == DeferredNotificationService.ItemKind.Question &&
            i.Message.Contains("tokensaver", StringComparison.Ordinal) &&
            i.Message.Contains("9999", StringComparison.Ordinal) &&
            i.Message.Contains("11436", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonLocalhostUrl_IsSkipped()
    {
        WriteMcpJson("""
            { "mcpServers": { "remote": { "url": "http://example.com:9999/mcp" } } }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.DoesNotContain(items, i => i.Message.Contains("remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrphanedEnabledEntry_ProducesPostQuestion()
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:11436/mcp" } } }
            """);
        WriteClaudeSettings("""
            { "enabledMcpjsonServers": ["tokensaver", "ghost-server"] }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i =>
            i.Kind == DeferredNotificationService.ItemKind.Question &&
            i.Message.Contains("ghost-server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnabledEntryPresentInMcpJson_ProducesNoOrphanNotification()
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:11436/mcp" } } }
            """);
        WriteClaudeSettings("""
            { "enabledMcpjsonServers": ["tokensaver"] }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.DoesNotContain(items, i => i.Message.Contains("stale/orphaned", StringComparison.Ordinal) || i.Message.Contains("no such server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidMcpJson_DoesNotThrow()
    {
        WriteMcpJson("{ not valid json ");

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        // Should complete without throwing; operative-settings Post is still there.
        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i => i.Kind == DeferredNotificationService.ItemKind.Notification);
    }

    [Fact]
    public async Task InvalidClaudeSettingsJson_DoesNotThrow()
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:11436/mcp" } } }
            """);
        WriteClaudeSettings("{ not valid json ");

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.DoesNotContain(items, i => i.Message.Contains("stale/orphaned", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyWorkspaceRoot_SkipsStepsButStillPostsOperativeSettings(string? root)
    {
        WriteMcpJson("""
            { "mcpServers": { "tokensaver": { "url": "http://localhost:9999/mcp" } } }
            """);

        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = root ?? "", ServerPort = 11436 };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i => i.Kind == DeferredNotificationService.ItemKind.Notification && i.Message.Contains("operative settings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Message.Contains("9999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonexistentWorkspaceDirectory_SkipsStepsButStillPostsOperativeSettings()
    {
        TokenSaverConfig tokenSaver = new()
        {
            WorkspaceRoot = Path.Combine(_workspaceRoot, "does-not-exist"),
            ServerPort = 11436
        };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i => i.Kind == DeferredNotificationService.ItemKind.Notification && i.Message.Contains("operative settings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AutoEnabledWithUnreachableRouter_ProducesWarningQuestion()
    {
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436, OllamaUrl = "http://localhost:11435" };
        CodeHelperConfig codeHelper = new() { AutoEnabled = true };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper, HttpStatusCode.ServiceUnavailable);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i =>
            i.Kind == DeferredNotificationService.ItemKind.Question &&
            i.Message.Contains("auto-intercept is enabled", StringComparison.OrdinalIgnoreCase) &&
            i.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AutoDisabled_ProducesNoRouterWarningRegardlessOfReachability()
    {
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436, OllamaUrl = "http://localhost:11435" };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferredUnreachable = await RunAsync(tokenSaver, codeHelper, HttpStatusCode.ServiceUnavailable);
        IReadOnlyList<DeferredNotificationService.DeferredItem> itemsUnreachable = deferredUnreachable.Drain();
        Assert.DoesNotContain(itemsUnreachable, i => i.Kind == DeferredNotificationService.ItemKind.Question);

        DeferredNotificationService deferredReachable = await RunAsync(tokenSaver, codeHelper, HttpStatusCode.OK);
        IReadOnlyList<DeferredNotificationService.DeferredItem> itemsReachable = deferredReachable.Drain();
        Assert.DoesNotContain(itemsReachable, i => i.Kind == DeferredNotificationService.ItemKind.Question);
    }

    [Fact]
    public async Task RouterReachable_OperativeSettingsNotificationReflectsReachable()
    {
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot, ServerPort = 11436, OllamaUrl = "http://localhost:11435" };
        CodeHelperConfig codeHelper = new() { AutoEnabled = false };

        DeferredNotificationService deferred = await RunAsync(tokenSaver, codeHelper, HttpStatusCode.OK);

        IReadOnlyList<DeferredNotificationService.DeferredItem> items = deferred.Drain();
        Assert.Contains(items, i => i.Message.Contains("reachable", StringComparison.Ordinal) && !i.Message.Contains("UNREACHABLE", StringComparison.Ordinal));
    }

    private sealed class FakeHttpClientFactory(HttpStatusCode statusCode) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new FakeHandler(statusCode), disposeHandler: true);
        }
    }

    private sealed class FakeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
