using System.ComponentModel;
using System.Text.Json;

using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Tools;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers DevLoopTools without starting docker, git, dotnet or any other CLI. Container tools
///     are exercised only with container names the tool rejects before any process is spawned.
///     DiffSummarize, RunTests and LintFix are exercised with paths or commands that cannot be
///     launched, so the OS refuses to create the process. BuildWatch reads a temp file and uses a
///     fake distillation service, so no model is called.
/// </summary>
public sealed class DevLoopToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-devloop-tests-" + Guid.NewGuid().ToString("N"));

    public DevLoopToolsTests()
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

    private DevLoopTools CreateTools(FakeDistillationService? distill = null)
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = _root };
        IOptions<TokenSaverConfig> options = Options.Create(cfg);
        RawArtifactWriter rawWriter = new(options);
        return new DevLoopTools(
            distill ?? new FakeDistillationService(),
            rawWriter,
            options,
            WorkspaceRootSessionResolverTestFactory.Create(cfg));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("../escape")]
    [InlineData("-leading-dash")]
    public async Task ContainerLogs_InvalidContainerName_ReturnsValidationErrorWithoutDocker(string name)
    {
        DevLoopTools tools = CreateTools();

        string result = await tools.ContainerLogs(name);

        Assert.StartsWith($"Invalid container name '{name}'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerExec_InvalidContainerName_ReturnsValidationErrorWithoutDocker()
    {
        DevLoopTools tools = CreateTools();

        string result = await tools.ContainerExec("bad name", "echo hi");

        Assert.StartsWith("Invalid container name 'bad name'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerStart_InvalidContainerName_ReturnsValidationErrorWithoutDocker()
    {
        DevLoopTools tools = CreateTools();

        string result = await tools.ContainerStart("bad name");

        Assert.StartsWith("Invalid container name 'bad name'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerStop_InvalidContainerName_ReturnsValidationErrorWithoutDocker()
    {
        DevLoopTools tools = CreateTools();

        string result = await tools.ContainerStop("bad name");

        Assert.StartsWith("Invalid container name 'bad name'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerRestart_InvalidContainerName_ReturnsValidationErrorWithoutDocker()
    {
        DevLoopTools tools = CreateTools();

        string result = await tools.ContainerRestart("bad name");

        Assert.StartsWith("Invalid container name 'bad name'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffSummarize_RepoPathDoesNotExist_ReturnsGitUnavailableMessage()
    {
        DevLoopTools tools = CreateTools();
        string missing = Path.Combine(_root, "no-such-repo");

        string result = await tools.DiffSummarize(missing);

        Assert.StartsWith("git CLI unavailable or", result, StringComparison.Ordinal);
        Assert.Contains(missing, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTests_MissingTestCommand_ThrowsWin32ExceptionBeforeAnyOutput()
    {
        DevLoopTools tools = CreateTools();

        // A missing test runner is a guard response, not an exception escaping the tool.
        string result = await tools.RunTests(_root, testCommand: "tokensaver-no-such-test-runner --all");

        Assert.StartsWith("Could not run 'tokensaver-no-such-test-runner'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LintFix_MissingLintCommand_ThrowsWin32ExceptionBeforeAnyOutput()
    {
        DevLoopTools tools = CreateTools();

        // A missing linter is a guard response, not an exception escaping the tool.
        string result = await tools.LintFix(_root, lintCommand: "tokensaver-no-such-linter --check");

        Assert.StartsWith("Could not run 'tokensaver-no-such-linter'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildWatch_SinceLinePastEnd_ReturnsEmptyErrorsAndLastLine()
    {
        string logPath = Path.Combine(_root, "build.log");
        await File.WriteAllTextAsync(logPath, "line1\nline2\n");
        DevLoopTools tools = CreateTools();

        string json = await tools.BuildWatch(logPath, sinceLine: 10);
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(0, result.GetProperty("new_errors").GetArrayLength());
        Assert.Equal(3, result.GetProperty("last_line").GetInt32());
    }

    [Fact]
    public async Task BuildWatch_SmallNewSection_ReturnsRawWithoutDistilling()
    {
        string logPath = Path.Combine(_root, "small.log");
        await File.WriteAllTextAsync(logPath, "ok\nerror CS1002: ; expected\n");
        FakeDistillationService distill = new();
        DevLoopTools tools = CreateTools(distill);

        string json = await tools.BuildWatch(logPath, sinceLine: 1);
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("error CS1002: ; expected\n", result.GetProperty("summary").GetString());
        Assert.Equal(0, distill.CallCount);
    }

    [Fact]
    public async Task BuildWatch_PreReducesBeforeDistill_RawTokensLower()
    {
        string logPath = Path.Combine(_root, "repeat.log");
        string big = string.Concat(Enumerable.Repeat("warning something noisy here\n", 60));
        await File.WriteAllTextAsync(logPath, "start\n" + big);
        FakeDistillationService distill = new();
        DevLoopTools tools = CreateTools(distill);

        await tools.BuildWatch(logPath, sinceLine: 1);

        Assert.NotNull(distill.LastRaw);
        Assert.True(distill.LastRaw!.Length * 4 < big.Length, $"distilled input {distill.LastRaw.Length} chars of {big.Length}");
        Assert.Contains("x60", distill.LastRaw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildWatch_LargeNewSection_ReturnsDistilledSummary()
    {
        string logPath = Path.Combine(_root, "large.log");
        string big = string.Concat(Enumerable.Repeat("warning something noisy here\n", 60));
        await File.WriteAllTextAsync(logPath, "start\n" + big);
        FakeDistillationService distill = new() { SummaryText = "2 anomalous lines" };
        DevLoopTools tools = CreateTools(distill);

        string json = await tools.BuildWatch(logPath, sinceLine: 1);
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("2 anomalous lines", result.GetProperty("summary").GetString());
        Assert.Equal(1, distill.CallCount);
    }

    private sealed class FakeDistillationService : IDistillationService
    {
        public string SummaryText { get; set; } = "summary";
        public int CallCount { get; private set; }
        public string? LastRaw { get; private set; }

        public Task<DistillResult> DistillAsync(
            string workspaceRoot,
            string tool,
            DistillRole role,
            string raw,
            int maxTokens = 300,
            bool? terse = null,
            string? model = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastRaw = raw;
            return Task.FromResult(new DistillResult(SummaryText, RawTokens: 100, DistilledTokens: 10, Terse: false, Distilled: true));
        }

        public int EstimateTokens(string text) => text.Length / 4;
    }
}
