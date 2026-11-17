#pragma warning disable CA2000 // Test host objects are disposed through the test's await-using scope.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Endpoints;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Security;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>The /api/auth routes through the real pipeline: the token gate, route binding and validation.</summary>
public sealed class AuthEndpointsTests : IDisposable
{
    private sealed class MemoryGroup : ILocalUserGroup
    {
        public List<string> Added { get; } = [];

        public string Name => "Backup Operators";

        public bool EnsureExists() => true;

        public IReadOnlyList<string> Members() => Added.ToArray();

        public string? Add(string account)
        {
            if (account.Contains("ghost", StringComparison.Ordinal))
            {
                return "No such account: 'ghost'.";
            }

            Added.Add(account);
            return null;
        }

        public string? Remove(string account)
        {
            _ = Added.Remove(account);
            return null;
        }

        public IReadOnlyList<string> CandidateUsers() => ["PC\\alice", "PC\\bob"];
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsauthapi-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryGroup _group = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(WebApplication App, HttpClient Client, string Token)> StartAsync()
    {
        TokenSaverConfig cfg = new() { AuthTokenPath = Path.Combine(_dir, "auth-token") };
        AuthTokenStore tokens = new(Options.Create(cfg), _group, NullLogger<AuthTokenStore>.Instance);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton(Options.Create(cfg));
        _ = builder.Services.AddSingleton(tokens);
        _ = builder.Services.AddSingleton<ILocalUserGroup>(_group);

        WebApplication app = builder.Build();
        _ = app.UseMiddleware<EndpointAuthMiddleware>();
        app.MapAuthEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient(), tokens.Token);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string? token, object? body = null)
    {
        HttpRequestMessage request = new(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [Fact]
    public async Task WithoutTheToken_EveryAuthRouteIs401()
    {
        (WebApplication app, HttpClient client, _) = await StartAsync();
        await using WebApplication host = app;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/auth/users", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/users", null, new { account = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Delete, "/api/auth/users?account=x", null))).StatusCode);
    }

    [Fact]
    public async Task ListAddAndRemove_WorkWithTheToken()
    {
        (WebApplication app, HttpClient client, string token) = await StartAsync();
        await using WebApplication host = app;

        HttpResponseMessage list = await client.SendAsync(Req(HttpMethod.Get, "/api/auth/users", token));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("Backup Operators", doc.RootElement.GetProperty("group").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("candidates").GetArrayLength());

        HttpResponseMessage add = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/users", token, new { account = "PC\\alice" }));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Contains("PC\\alice", _group.Added);

        HttpResponseMessage remove = await client.SendAsync(
            Req(HttpMethod.Delete, "/api/auth/users?account=" + Uri.EscapeDataString("PC\\alice"), token));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Empty(_group.Added);
    }

    [Theory]
    [InlineData("a;rm -rf")]
    [InlineData("..\\x")]
    [InlineData("")]
    public async Task InvalidAccounts_AreRejectedBeforeTheGroupIsTouched(string account)
    {
        (WebApplication app, HttpClient client, string token) = await StartAsync();
        await using WebApplication host = app;

        HttpResponseMessage response = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/users", token, new { account }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_group.Added);
    }

    [Fact]
    public async Task AnUnknownAccount_ReturnsTheGroupsError()
    {
        (WebApplication app, HttpClient client, string token) = await StartAsync();
        await using WebApplication host = app;

        HttpResponseMessage response = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/users", token, new { account = "ghost" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No such account", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
