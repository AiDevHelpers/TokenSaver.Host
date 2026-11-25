using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     FindRepoRoot is pure (no shared state) and gets full coverage here.
/// </summary>
public sealed class WorkspaceRootResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceRootResolverTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("wrrTests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FindRepoRoot_GitDirAtSearchFrom_ReturnsSearchFrom()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".git"));

        string? result = WorkspaceRootResolver.FindRepoRoot(_tempRoot);

        Assert.Equal(_tempRoot, result);
    }

    [Fact]
    public void FindRepoRoot_GitDirSeveralLevelsUp_WalksUpAndFindsIt()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".git"));
        string nested = Path.Combine(_tempRoot, "src", "Host", "Deep");
        Directory.CreateDirectory(nested);

        string? result = WorkspaceRootResolver.FindRepoRoot(nested);

        Assert.Equal(_tempRoot, result);
    }

    [Fact]
    public void FindRepoRoot_SlnFileAtSearchFrom_ReturnsSearchFrom()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "Solution.sln"), "");

        string? result = WorkspaceRootResolver.FindRepoRoot(_tempRoot);

        Assert.Equal(_tempRoot, result);
    }

    [Fact]
    public void FindRepoRoot_SlnxFileUpOneLevel_WalksUpAndFindsIt()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "Solution.slnx"), "");
        string nested = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(nested);

        string? result = WorkspaceRootResolver.FindRepoRoot(nested);

        Assert.Equal(_tempRoot, result);
    }

    [Fact]
    public void FindRepoRoot_NoMarkerAnywhereInIsolatedTree_ReturnsNull()
    {
        // Bounded tree with no .git/.sln/.slnx anywhere in it or its ancestors up to the temp
        // root's own parent — but the walk keeps going past that up to the real filesystem root,
        // which (on a dev machine under a real repo checkout) may eventually hit a marker. Use a
        // nested dir under the OS temp folder, which is never itself inside a git repo or beneath
        // one that has a .sln/.slnx, to keep this deterministic.
        string isolated = Path.Combine(_tempRoot, "no-markers-here");
        Directory.CreateDirectory(isolated);

        string? result = WorkspaceRootResolver.FindRepoRoot(isolated);

        Assert.Null(result);
    }

    [Fact]
    public void FindRepoRoot_PrefersNearestMarkerOverFartherOne()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".git"));
        string nestedWithOwnGit = Path.Combine(_tempRoot, "nested");
        Directory.CreateDirectory(Path.Combine(nestedWithOwnGit, ".git"));

        string? result = WorkspaceRootResolver.FindRepoRoot(nestedWithOwnGit);

        Assert.Equal(nestedWithOwnGit, result);
    }
}
