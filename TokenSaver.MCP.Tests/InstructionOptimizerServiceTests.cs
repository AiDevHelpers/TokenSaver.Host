using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.Config;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace TokenSaver.MCP.Tests;

[Collection("GlobalConfigDirEnvVar")]
public sealed class InstructionOptimizerServiceTests : IDisposable
{
    private const string GlobalConfigDirEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";
    private const string GlobalClaudeMdEnvVar = "TOKENSAVER_GLOBAL_CLAUDE_MD_PATH";

    private readonly string _workspaceRoot;
    private readonly string _globalConfigDir;
    private readonly string _globalClaudeMdPath;
    private readonly BehaviorConfigService _behaviorConfig;
    private readonly InstructionOptimizerService _service;

    public InstructionOptimizerServiceTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("iosTests-").FullName;
        _globalConfigDir = Directory.CreateTempSubdirectory("iosTests-globalcfg-").FullName;
        _globalClaudeMdPath = Path.Combine(Directory.CreateTempSubdirectory("iosTests-claudemd-").FullName, "CLAUDE.md");

        Environment.SetEnvironmentVariable(GlobalConfigDirEnvVar, _globalConfigDir);
        Environment.SetEnvironmentVariable(GlobalClaudeMdEnvVar, _globalClaudeMdPath);

        _behaviorConfig = new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance);
        _service = new InstructionOptimizerService(_behaviorConfig, NullLogger<InstructionOptimizerService>.Instance);
    }

    public void Dispose()
    {
        _behaviorConfig.Dispose();
        Environment.SetEnvironmentVariable(GlobalConfigDirEnvVar, null);
        Environment.SetEnvironmentVariable(GlobalClaudeMdEnvVar, null);
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void EnableOptimizeInstructions()
    {
        _behaviorConfig.Set(_workspaceRoot, ConfigScope.Local, c => c with
        {
            RepoHelper = (c.RepoHelper ?? new RepoHelperBehaviorConfig()) with { OptimizeInstructions = true },
        });
    }

    [Fact]
    public void EnsureSynced_OptimizeInstructionsOff_DoesNothing()
    {
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc")));
    }

    [Fact]
    public void EnsureSynced_NoGlobalClaudeMd_DoesNothing()
    {
        EnableOptimizeInstructions();
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc")));
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_SkipsBecauseNativelyLoaded()
    {
        EnableOptimizeInstructions();
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        // No CLAUDE.md, no AGENTS.md, no scoped-rule file should appear.
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "CLAUDE.md")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "AGENTS.md")));
    }

    [Fact]
    public void EnsureSynced_Cursor_WritesScopedRuleFileWithFrontMatterAndContent()
    {
        EnableOptimizeInstructions();
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string path = Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc");
        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);
        Assert.Contains("alwaysApply: true", content, StringComparison.Ordinal);
        Assert.Contains("Always do X.", content, StringComparison.Ordinal);
        Assert.Contains("tokensaver:global-instructions:start", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSynced_UnrecognizedClient_FallsBackToAgentsMd()
    {
        EnableOptimizeInstructions();
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "some-future-tool" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string path = Path.Combine(_workspaceRoot, "AGENTS.md");
        Assert.True(File.Exists(path));
        Assert.Contains("Always do X.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSynced_ReRunAfterGlobalChanges_UpdatesBlockAndPreservesSurroundingContent()
    {
        EnableOptimizeInstructions();
        CallerInfo caller = new() { ClientName = "copilot" };
        string path = Path.Combine(_workspaceRoot, ".github", "instructions", "tokensaver-global.instructions.md");

        File.WriteAllText(_globalClaudeMdPath, "First version.");
        _service.EnsureSynced(_workspaceRoot, caller);
        string firstContent = File.ReadAllText(path);
        Assert.Contains("First version.", firstContent, StringComparison.Ordinal);

        // Simulate a user hand-editing something outside the managed block.
        File.WriteAllText(path, "<!-- user note, keep me -->" + Environment.NewLine + firstContent);

        File.WriteAllText(_globalClaudeMdPath, "Second version.");
        _service.EnsureSynced(_workspaceRoot, caller);
        string updated = File.ReadAllText(path);

        Assert.Contains("user note, keep me", updated, StringComparison.Ordinal);
        Assert.Contains("Second version.", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("First version.", updated, StringComparison.Ordinal);
    }
}
