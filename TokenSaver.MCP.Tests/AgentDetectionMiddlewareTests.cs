using System.Text;

using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.Config;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Covers <see cref="AgentDetectionMiddleware" />'s contract: only acts on POST /mcp under the
///     1MB body cap, only resolves on the "initialize" method, never throws out of the
///     resolve/log step, and always leaves the request body readable (position reset to 0) for
///     downstream middleware/the MCP endpoint itself. Uses a minimal TestServer host (same
///     pattern as <see cref="HealthTestHost" />) rather than the full Program.cs composition
///     root, with a spy <see cref="ICallerDetectionService" /> standing in for the real
///     detection pipeline so tests can assert whether resolution was attempted.
/// </summary>
[Collection("GlobalConfigDirEnvVar")]
public sealed class AgentDetectionMiddlewareTests
{
    private sealed class SpyCallerDetectionService : ICallerDetectionService
    {
        public int CallCount { get; private set; }

        public CallerInfo Detect(HttpContext httpContext, string? mcpClientName = null, string? mcpClientVersion = null)
        {
            CallCount++;
            return new CallerInfo { ClientName = "test-client" };
        }
    }

    private static (TestServer server, SpyCallerDetectionService spy) CreateServer(string? workspaceRoot)
    {
        SpyCallerDetectionService spy = new();

        IWebHostBuilder builder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging(l => l.ClearProviders());
                services.AddSingleton<IOptions<TokenSaverConfig>>(
                    Options.Create(new TokenSaverConfig { WorkspaceRoot = workspaceRoot ?? "" }));

                services.AddSingleton<ICallerDetectionService>(spy);
                services.AddSingleton<IEnumerable<IAgentAdapter>>(Array.Empty<IAgentAdapter>());
                services.AddSingleton<AgentAdapterRegistry>();
                services.AddSingleton<BehaviorConfigService>();
                services.AddSingleton<AgentConfigResolver>();
                services.AddSingleton<InstructionCacheService>();
                services.AddSingleton<InstructionOptimizerService>();
            })
            .Configure(app =>
            {
                app.UseMiddleware<AgentDetectionMiddleware>();

                // Terminal endpoint: echoes the request body back so tests can assert it's still
                // readable (and reset to position 0) after the middleware runs.
                app.Run(async ctx =>
                {
                    using StreamReader reader = new(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                    string body = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync(body);
                });
            });

        TestServer server = new(builder);
        return (server, spy);
    }

    [Fact]
    public async Task NonPostRequestToMcp_PassesThrough()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/mcp");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task PostToNonMcpPath_PassesThrough()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/other", new StringContent("""{"method":"initialize"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task PostToMcp_InitializeMethod_ResolvesAndBodyStillReadable()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        const string payload = """{"method":"initialize","id":1}""";
        HttpResponseMessage response = await client.PostAsync(
            "/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string echoed = await response.Content.ReadAsStringAsync();
        Assert.Equal(payload, echoed);
        Assert.Equal(1, spy.CallCount);
    }

    [Fact]
    public async Task PostToMcp_NonInitializeMethod_DoesNotResolve()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        const string payload = """{"method":"tools/call","id":2}""";
        HttpResponseMessage response = await client.PostAsync(
            "/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string echoed = await response.Content.ReadAsStringAsync();
        Assert.Equal(payload, echoed);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task PostToMcp_MalformedJson_DoesNotThrowAndBodyStillReadable()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        const string payload = "{not valid json";
        HttpResponseMessage response = await client.PostAsync(
            "/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string echoed = await response.Content.ReadAsStringAsync();
        Assert.Equal(payload, echoed);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task PostToMcp_InitializeMethod_RecreatesMissingLocalConfig()
    {
        const string globalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";
        string workspaceRoot = Directory.CreateTempSubdirectory("adwTests-").FullName;
        // EnsureDefaults also bootstraps the machine-global config; redirect it to a temp dir so
        // this test never touches (or races on) the real %ProgramData%\TokenSaver\config.json.
        string globalDir = Directory.CreateTempSubdirectory("adwTests-global-").FullName;
        Environment.SetEnvironmentVariable(globalDirOverrideEnvVar, globalDir);
        try
        {
            (TestServer server, _) = CreateServer(workspaceRoot);
            using HttpClient client = server.CreateClient();

            string configPath = Path.Combine(workspaceRoot, ".tokensaver", "config.json");
            Assert.False(File.Exists(configPath));

            const string payload = """{"method":"initialize","id":1}""";
            HttpResponseMessage response = await client.PostAsync(
                "/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(File.Exists(configPath), "Session connect should recreate a missing local config.json.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(globalDirOverrideEnvVar, null);

            try
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
            catch (IOException)
            {
            }

            try
            {
                Directory.Delete(globalDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PostToMcp_EmptyWorkspaceRoot_SkipsResolutionButPassesThrough()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer(workspaceRoot: "   ");
        using HttpClient client = server.CreateClient();

        const string payload = """{"method":"initialize"}""";
        HttpResponseMessage response = await client.PostAsync(
            "/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string echoed = await response.Content.ReadAsStringAsync();
        Assert.Equal(payload, echoed);
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task PostToMcp_ContentLengthOverCap_PassesThroughWithoutBuffering()
    {
        (TestServer server, SpyCallerDetectionService spy) = CreateServer("C:\\workspace");
        using HttpClient client = server.CreateClient();

        // Just over the middleware's 1MB peek cap; content itself doesn't matter since it should
        // never be read/parsed.
        string oversized = new('x', (1 * 1024 * 1024) + 10);
        HttpResponseMessage response = await client.PostAsync(
            "/mcp", new StringContent(oversized, Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string echoed = await response.Content.ReadAsStringAsync();
        Assert.Equal(oversized, echoed);
        Assert.Equal(0, spy.CallCount);
    }
}
