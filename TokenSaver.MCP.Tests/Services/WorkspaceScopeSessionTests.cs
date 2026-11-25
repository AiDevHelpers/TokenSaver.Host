#pragma warning disable CA1812 // The MCP SDK instantiates the probe tool class through DI.
#pragma warning disable CA2000 // Test host objects are disposed through the test's await-using scope.
using System.ComponentModel;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Middleware;
using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     Two MCP clients on one server: each session must see only its own workspace root. The probe tools
///     do exactly what <see cref="WorkspaceRootSessionResolver" /> does after it learns a root (set the
///     request scope, record the session, remember the last root), and read it back through the shared
///     <see cref="TokenSaverConfig" /> the way every real tool does.
/// </summary>
public sealed class WorkspaceScopeSessionTests
{
    [McpServerToolType]
    internal sealed class ScopeProbeTools(TokenSaverConfig config, WorkspaceSessionRoots roots)
    {
        [McpServerTool(Name = "set_root"), Description("Resolve this session's root.")]
        public string SetRoot(string root, McpServer server)
        {
            WorkspaceScope.Current!.Root = root;
            roots.Set(server.SessionId, root);
            config.RecordResolvedRoot(root);
            return root;
        }

        [McpServerTool(Name = "get_root"), Description("Read the root the config reports for this call.")]
        public string GetRoot()
        {
            return config.WorkspaceRoot;
        }
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton<TokenSaverConfig>();
        _ = builder.Services.AddSingleton<WorkspaceSessionRoots>();
        _ = builder.Services.AddMcpServer()
            .WithHttpTransport(o => { o.Stateless = false; })
            .WithTools<ScopeProbeTools>();

        WebApplication app = builder.Build();
        _ = app.UseMiddleware<WorkspaceScopeMiddleware>();
        _ = app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    private static async Task<McpClient> ConnectAsync(WebApplication app)
    {
        HttpClientTransport transport = new(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            app.GetTestClient(),
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, params (string Key, object Value)[] args)
    {
        Dictionary<string, object?> arguments = args.ToDictionary(a => a.Key, a => (object?)a.Value);
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    [Fact]
    public async Task TwoSessions_EachSeeOnlyTheirOwnRoot()
    {
        await using WebApplication app = await StartHostAsync();
        await using McpClient a = await ConnectAsync(app);
        await using McpClient b = await ConnectAsync(app);

        Assert.Equal("", await CallAsync(a, "get_root"));
        Assert.Equal("", await CallAsync(b, "get_root"));

        _ = await CallAsync(a, "set_root", ("root", @"C:\repos\alpha"));

        Assert.Equal(@"C:\repos\alpha", await CallAsync(a, "get_root"));

        // B has not resolved a root: it must not inherit A's.
        Assert.Equal("", await CallAsync(b, "get_root"));

        _ = await CallAsync(b, "set_root", ("root", @"C:\repos\beta"));

        Assert.Equal(@"C:\repos\alpha", await CallAsync(a, "get_root"));
        Assert.Equal(@"C:\repos\beta", await CallAsync(b, "get_root"));
    }

    [Fact]
    public async Task ConcurrentSessions_NeverCrossRoots()
    {
        await using WebApplication app = await StartHostAsync();
        await using McpClient a = await ConnectAsync(app);
        await using McpClient b = await ConnectAsync(app);
        _ = await CallAsync(a, "set_root", ("root", "A"));
        _ = await CallAsync(b, "set_root", ("root", "B"));

        async Task<List<string>> Hammer(McpClient client)
        {
            List<string> seen = [];
            for (int i = 0; i < 25; i++)
            {
                seen.Add(await CallAsync(client, "get_root"));
            }

            return seen;
        }

        List<string>[] results = await Task.WhenAll(Hammer(a), Hammer(b));

        Assert.All(results[0], r => Assert.Equal("A", r));
        Assert.All(results[1], r => Assert.Equal("B", r));
    }
}
