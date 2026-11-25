#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Endpoints;
using AiDevTools.TokenSaver.MCP.Middleware;
using AiDevTools.TokenSaver.MCP.Security;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class EndpointAuthTests : IDisposable
{
    private sealed class FakeGroup : ILocalUserGroup
    {
        public string Name => "Backup Operators";

        public bool EnsureExists() => true;

        public IReadOnlyList<string> Members() => [];

        public string? Add(string account) => null;

        public string? Remove(string account) => null;

        public IReadOnlyList<string> CandidateUsers() => [];
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsauth-" + Guid.NewGuid().ToString("N"));
    private readonly AuthTokenStore _tokens;

    public EndpointAuthTests()
    {
        _tokens = NewStore(_dir);
    }

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

    private static AuthTokenStore NewStore(string dir)
    {
        TokenSaverConfig cfg = new() { AuthTokenPath = Path.Combine(dir, "auth-token") };
        return new AuthTokenStore(Options.Create(cfg), new FakeGroup(), NullLogger<AuthTokenStore>.Instance);
    }

    private async Task<(int Status, bool NextCalled, string? Challenge)> SendAsync(
        string path,
        string? host = "localhost:11436",
        string? origin = null,
        string? authorization = null,
        bool requireAuth = true)
    {
        bool nextCalled = false;
        EndpointAuthMiddleware middleware = new(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (host is not null)
        {
            context.Request.Headers.Host = host;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        await middleware.InvokeAsync(context, _tokens, Options.Create(new TokenSaverConfig { RequireAuth = requireAuth }));
        return (context.Response.StatusCode, nextCalled, context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/sse")]
    [InlineData("/api/debugger/sessions")]
    [InlineData("/api/register-providers")]
    [InlineData("/api/auth/users")]
    public async Task ProtectedPaths_WithoutAToken_Return401WithAChallenge(string path)
    {
        (int status, bool next, string? challenge) = await SendAsync(path);

        Assert.Equal(401, status);
        Assert.False(next);
        Assert.Equal("Bearer", challenge);
    }

    [Fact]
    public async Task WrongOrMalformedTokens_Return401()
    {
        Assert.Equal(401, (await SendAsync("/mcp", authorization: "Bearer not-the-token")).Status);
        Assert.Equal(401, (await SendAsync("/mcp", authorization: "Basic " + _tokens.Token)).Status);
        Assert.Equal(401, (await SendAsync("/mcp", authorization: _tokens.Token)).Status);
        Assert.Equal(401, (await SendAsync("/mcp", authorization: "Bearer ")).Status);
    }

    [Fact]
    public async Task ACorrectToken_ReachesTheNextMiddleware()
    {
        (int status, bool next, _) = await SendAsync("/mcp", authorization: "Bearer " + _tokens.Token);

        Assert.Equal(200, status);
        Assert.True(next);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_StayOpen(string path)
    {
        (int status, bool next, _) = await SendAsync(path);

        Assert.Equal(200, status);
        Assert.True(next);
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("evil.example.com:11436")]
    [InlineData("192.168.1.20:11436")]
    [InlineData("localhost.evil.com")]
    [InlineData("")]
    public async Task NonLoopbackHosts_AreRefusedEvenWithAToken(string host)
    {
        (int status, bool next, _) = await SendAsync("/mcp", host: host, authorization: "Bearer " + _tokens.Token);

        Assert.Equal(403, status);
        Assert.False(next);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:11436")]
    [InlineData("127.0.0.1:11436")]
    [InlineData("[::1]:11436")]
    [InlineData("app.localhost")]
    public async Task LoopbackHosts_AreAccepted(string host)
    {
        (int status, _, _) = await SendAsync("/mcp", host: host, authorization: "Bearer " + _tokens.Token);

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task ABrowserOrigin_IsRefusedEvenWithAToken()
    {
        (int status, bool next, _) = await SendAsync(
            "/mcp", origin: "https://evil.example.com", authorization: "Bearer " + _tokens.Token);

        Assert.Equal(403, status);
        Assert.False(next);
    }

    [Fact]
    public async Task WhenAuthIsOff_HostAndOriginAreStillChecked()
    {
        Assert.Equal(200, (await SendAsync("/mcp", requireAuth: false)).Status);
        Assert.Equal(403, (await SendAsync("/mcp", host: "evil.example.com", requireAuth: false)).Status);
        Assert.Equal(403, (await SendAsync("/mcp", origin: "http://x", requireAuth: false)).Status);
    }

    [Fact]
    public void TheTokenIsStableAcrossRestartsAndLongEnough()
    {
        AuthTokenStore again = NewStore(_dir);

        Assert.Equal(_tokens.Token, again.Token);
        Assert.True(_tokens.Token.Length >= 32);
        Assert.True(again.IsValid(_tokens.Token));
        Assert.False(again.IsValid(_tokens.Token + "x"));
        Assert.False(again.IsValid(""));
        Assert.False(again.IsValid(null));
    }

    [Fact]
    public void TheTokenFile_HoldsTheTokenAndIsNotWorldReadable()
    {
        Assert.Equal(_tokens.Token, File.ReadAllText(_tokens.Path).Trim());
        if (OperatingSystem.IsWindows())
        {
            System.Security.AccessControl.FileSecurity acl = new FileInfo(_tokens.Path).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            bool everyone = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-32-545" or "S-1-5-11");
            Assert.False(everyone);
        }
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("DESKTOP-1\\alice", true)]
    [InlineData("first last", true)]
    [InlineData("svc$", true)]
    [InlineData("", false)]
    [InlineData("..\\x", false)]
    [InlineData("a/b", false)]
    [InlineData("a;rm -rf", false)]
    [InlineData("a\"b", false)]
    [InlineData("a&b", false)]
    public void AccountNames_AreValidatedBeforeTouchingTheGroup(string account, bool valid)
    {
        Assert.Equal(valid, AuthEndpoints.IsValidAccount(account));
    }

    [Fact]
    public void BearerToken_ParsesTheHeader()
    {
        Assert.Equal("abc", EndpointAuthMiddleware.BearerToken("Bearer abc"));
        Assert.Equal("abc", EndpointAuthMiddleware.BearerToken("bearer abc"));
        Assert.Null(EndpointAuthMiddleware.BearerToken("Token abc"));
        Assert.Null(EndpointAuthMiddleware.BearerToken(null));
    }
}
