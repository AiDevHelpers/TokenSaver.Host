using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     FindRepoRoot is pure (no shared state) and gets full coverage here. ReadPersistedWorkspaceRoot
///     and PersistWorkspaceRoot both read/write the real, shared
///     %ProgramData%\TokenSaver\workspace-root.json — there's no seam to redirect them to a temp
///     path (BootstrapFilePath is a private static property, not injectable). PersistWorkspaceRoot
///     is a no-op when the value hasn't changed, so the round-trip test below saves the real
///     pre-existing value first and restores it in Dispose, keeping this safe to run alongside a
///     real TokenSaver install on the same machine.
/// </summary>
public sealed class WorkspaceRootResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _originalPersistedRoot;

    public WorkspaceRootResolverTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("wrrTests-").FullName;
        _originalPersistedRoot = WorkspaceRootResolver.ReadPersistedWorkspaceRoot();
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

        // Restore whatever was persisted before this test ran (best-effort; PersistWorkspaceRoot
        // itself swallows IO/UnauthorizedAccessException, and a null original means "nothing to
        // restore" — there's no delete API, so we leave a freshly-created file in place in that
        // case, matching what a first-ever run on this machine would already look like).
        if (_originalPersistedRoot is not null)
        {
            WorkspaceRootResolver.PersistWorkspaceRoot(_originalPersistedRoot);
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

    [Fact]
    public void PersistThenRead_RoundTripsSameValue()
    {
        string candidate = _tempRoot;

        WorkspaceRootResolver.PersistWorkspaceRoot(candidate);
        string? readBack = WorkspaceRootResolver.ReadPersistedWorkspaceRoot();

        Assert.Equal(candidate, readBack);
    }

    [Fact]
    public void PersistWorkspaceRoot_SameValueTwice_DoesNotThrow()
    {
        string candidate = _tempRoot;

        WorkspaceRootResolver.PersistWorkspaceRoot(candidate);
        Exception? ex = Record.Exception(() => WorkspaceRootResolver.PersistWorkspaceRoot(candidate));

        Assert.Null(ex);
    }
}
