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
    private readonly InstructionCacheService _cache;
    private readonly InstructionOptimizerService _service;

    public InstructionOptimizerServiceTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("iosTests-").FullName;
        _globalConfigDir = Directory.CreateTempSubdirectory("iosTests-globalcfg-").FullName;
        _globalClaudeMdPath = Path.Combine(Directory.CreateTempSubdirectory("iosTests-claudemd-").FullName, "CLAUDE.md");

        Environment.SetEnvironmentVariable(GlobalConfigDirEnvVar, _globalConfigDir);
        Environment.SetEnvironmentVariable(GlobalClaudeMdEnvVar, _globalClaudeMdPath);

        _behaviorConfig = new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance);
        _cache = new InstructionCacheService(NullLogger<InstructionCacheService>.Instance);
        _service = new InstructionOptimizerService(_behaviorConfig, _cache, NullLogger<InstructionOptimizerService>.Instance);
    }

    public void Dispose()
    {
        _behaviorConfig.Dispose();
        _cache.Dispose();
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
    public void EnsureSynced_OptimizeInstructionsExplicitlyOff_DoesNothing()
    {
        _behaviorConfig.Set(_workspaceRoot, ConfigScope.Local, c => c with
        {
            RepoHelper = (c.RepoHelper ?? new RepoHelperBehaviorConfig()) with { OptimizeInstructions = false },
        });
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc")));
    }

    /// <summary>
    ///     No config.json has ever recorded a value for optimizeInstructions here (neither Set nor
    ///     EnableOptimizeInstructions was called) — this must still sync, since the feature is
    ///     documented as on by default and must not depend on
    ///     <c>WorkspaceConfigDefaults.EnsureDefaults</c> having already written a config.json.
    /// </summary>
    [Fact]
    public void EnsureSynced_OptimizeInstructionsUnset_SyncsByDefault()
    {
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.True(File.Exists(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc")));
    }

    /// <summary>
    ///     No CLAUDE.md exists anywhere (global or project) — but since optimizeInstructions is
    ///     on, InstructionCacheService bootstraps a starter CLAUDE.md at the workspace root (see
    ///     InstructionCacheServiceTests.EnsureFresh_NoDocsAnywhere_BootstrapsStarterClaudeMdAtWorkspaceRoot)
    ///     rather than syncing nothing, so this now syncs that bootstrapped doc like any other.
    /// </summary>
    [Fact]
    public void EnsureSynced_NoClaudeMdAnywhere_SyncsBootstrappedStarterDoc()
    {
        EnableOptimizeInstructions();
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.True(File.Exists(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc")));
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

    [Fact]
    public void EnsureSynced_AutoSplitUnset_SplitsOversizedSectionByDefaultWithoutTouchingSourceFile()
    {
        // Only optimizeInstructions is set explicitly here — autoSplitOversizedInstructions is
        // left unset, and must still default to on (same as optimizeInstructions itself).
        EnableOptimizeInstructions();
        string bigSection = "## Big Section\n" + string.Concat(Enumerable.Repeat("Detail. ", 600));
        string originalGlobalContent = "# Global\n\nIntro.\n\n" + bigSection;
        File.WriteAllText(_globalClaudeMdPath, originalGlobalContent);
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        // The real source file is never touched — only the synced target reflects the split.
        Assert.Equal(originalGlobalContent, File.ReadAllText(_globalClaudeMdPath));
        string synced = File.ReadAllText(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc"));
        Assert.Contains("loaded on demand", synced, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSynced_AutoSplitExplicitlyOff_LeavesOversizedDocUnsplitInSyncedTarget()
    {
        _behaviorConfig.Set(_workspaceRoot, ConfigScope.Local, c => c with
        {
            RepoHelper = (c.RepoHelper ?? new RepoHelperBehaviorConfig()) with
            {
                OptimizeInstructions = true,
                AutoSplitOversizedInstructions = false,
            },
        });
        string bigSection = "## Big Section\n" + string.Concat(Enumerable.Repeat("Detail. ", 600));
        File.WriteAllText(_globalClaudeMdPath, "# Global\n\nIntro.\n\n" + bigSection);
        CallerInfo caller = new() { ClientName = "cursor" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string synced = File.ReadAllText(Path.Combine(_workspaceRoot, ".cursor", "rules", "tokensaver-global.mdc"));
        Assert.DoesNotContain("loaded on demand", synced, StringComparison.Ordinal);
    }
}
