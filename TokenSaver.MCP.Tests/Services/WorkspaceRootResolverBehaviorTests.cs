using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     The workspace root resolver and the per-session root store, without a real MCP session: a null server has no
///     client roots to ask, so resolution must leave the configured root alone and never guess.
/// </summary>
public sealed class WorkspaceRootResolverBehaviorTests
{
    [Fact]
    public async Task WithoutAServer_AnUnknownRootLeavesTheConfiguredRootAlone()
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = "" };
        WorkspaceRootSessionResolver resolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);

        await resolver.EnsureResolvedAsync(server: null!, CancellationToken.None);

        Assert.Equal("", cfg.WorkspaceRoot);
    }

    [Fact]
    public async Task WithoutAServer_AnExistingConfiguredRootIsNotReplaced()
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = @"C:\configured" };
        WorkspaceRootSessionResolver resolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);

        await resolver.EnsureResolvedAsync(server: null!, CancellationToken.None);

        Assert.Equal(@"C:\configured", cfg.WorkspaceRoot);
    }

    [Fact]
    public async Task ARootHintThatIsNotADirectory_IsIgnored()
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = "" };
        WorkspaceRootSessionResolver resolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);
        string missing = Path.Combine(Path.GetTempPath(), "no-such-root-" + Guid.NewGuid().ToString("N"));

        await resolver.EnsureResolvedAsync(server: null!, rootHint: missing, CancellationToken.None);

        Assert.Equal("", cfg.WorkspaceRoot);
    }

    [Fact]
    public async Task ARootHintThatExists_DoesNotChangeTheConfiguredRoot()
    {
        // The hint only starts that folder's warm-up (disabled in tests); it must not become the default root.
        TokenSaverConfig cfg = new() { WorkspaceRoot = @"C:\configured" };
        WorkspaceRootSessionResolver resolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);

        await resolver.EnsureResolvedAsync(server: null!, rootHint: Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(@"C:\configured", cfg.WorkspaceRoot);
    }

    [Fact]
    public void SessionRoots_StoreAndForgetPerSession()
    {
        WorkspaceSessionRoots roots = new();

        roots.Set("a", @"C:\alpha");
        roots.Set("b", @"C:\beta");

        Assert.Equal(@"C:\alpha", roots.Get("a"));
        Assert.Equal(@"C:\beta", roots.Get("b"));

        roots.Forget("a");

        Assert.Null(roots.Get("a"));
        Assert.Equal(@"C:\beta", roots.Get("b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SessionRoots_WithoutASessionId_NeverStoresOrReturnsAnything(string? sessionId)
    {
        WorkspaceSessionRoots roots = new();

        roots.Set(sessionId, @"C:\x");

        Assert.Null(roots.Get(sessionId));
        roots.Forget(sessionId);
    }
}
