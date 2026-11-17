using TokenSaver.Common.Core.Configuration;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.MCP.Tools;

using Microsoft.Extensions.Options;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers SessionTools snapshot/restore against temp folders. Blobs stay under the 4000-character
///     distill threshold, so the fake distillation service is never asked to summarize.
/// </summary>
public sealed class SessionToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-session-tests-" + Guid.NewGuid().ToString("N"));

    public SessionToolsTests()
    {
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionTools CreateTools(IDistillationService? distill = null)
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = _root };
        return new SessionTools(
            distill ?? new NeverDistill(),
            Options.Create(cfg),
            WorkspaceRootSessionResolverTestFactory.Create(cfg));
    }

    [Fact]
    public async Task SessionSnapshot_SmallBlobWithExplicitPath_WritesSnapshotAndFullText()
    {
        SessionTools tools = CreateTools();
        string dest = Path.Combine(_root, "custom", "snap.json");

        string result = await tools.SessionSnapshot("s1", "short context", outputPath: dest);

        Assert.Equal($"Saved snapshot: {dest}", result);
        Assert.True(File.Exists(dest));
        Assert.True(File.Exists(Path.Combine(_root, "custom", "snap.full.txt")));
        Assert.Equal("short context", await File.ReadAllTextAsync(Path.Combine(_root, "custom", "snap.full.txt")));
    }

    [Fact]
    public async Task SessionSnapshot_OutputPathWithoutJsonExtension_KeepsSnapshotAndBlobSeparateAndRestores()
    {
        SessionTools tools = CreateTools();
        string dest = Path.Combine(_root, "custom", "snap");

        _ = await tools.SessionSnapshot("s1", "short context", outputPath: dest);

        Assert.NotEqual(dest, Path.ChangeExtension(dest, ".full.txt"));
        Assert.Contains("\"summary\"", await File.ReadAllTextAsync(dest));
        Assert.Equal("short context", await tools.SessionRestore(dest, full: true));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task SessionSnapshot_PathLikeSessionId_IsRejected(string sessionId)
    {
        SessionTools tools = CreateTools();

        string result = await tools.SessionSnapshot(sessionId, "blob");

        Assert.Contains("invalid_session_id", result);
        Assert.False(Directory.Exists(Path.Combine(_root, ".tokensaver", "sessions")));
    }

    [Fact]
    public async Task SessionSnapshot_NoOutputPath_DefaultsToWorkspaceSessionsFolder()
    {
        SessionTools tools = CreateTools();
        string expected = Path.Combine(_root, ".tokensaver", "sessions", "abc.json");

        string result = await tools.SessionSnapshot("abc", "blob");

        Assert.Equal($"Saved snapshot: {expected}", result);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task SessionRestore_DefaultMode_ReturnsStoredSummary()
    {
        SessionTools tools = CreateTools();
        string dest = Path.Combine(_root, "restore-summary.json");
        _ = await tools.SessionSnapshot("r1", "the remembered context", outputPath: dest);

        string restored = await tools.SessionRestore(dest);

        Assert.Equal("the remembered context", restored);
    }

    [Fact]
    public async Task SessionRestore_FullMode_ReturnsOriginalBlob()
    {
        SessionTools tools = CreateTools();
        string dest = Path.Combine(_root, "restore-full.json");
        _ = await tools.SessionSnapshot("r2", "original blob text", outputPath: dest);

        string restored = await tools.SessionRestore(dest, full: true);

        Assert.Equal("original blob text", restored);
    }

    private sealed class NeverDistill : IDistillationService
    {
        public Task<DistillResult> DistillAsync(
            string workspaceRoot,
            string tool,
            DistillRole role,
            string raw,
            int maxTokens = 300,
            bool? terse = null,
            string? model = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("small blobs must not be distilled in these tests");

        public int EstimateTokens(string text) => text.Length / 4;
    }
}
