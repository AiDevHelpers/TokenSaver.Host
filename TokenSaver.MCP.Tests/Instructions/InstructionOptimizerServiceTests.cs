using TokenSaver.AgentHelpers.Core.Models;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.Config.Contracts;
using AiDevTools.TokenSaver.MCP.Services;

using Microsoft.Extensions.Logging.Abstractions;

using System.Text.Json.Nodes;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

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
        _behaviorConfig.Update(_workspaceRoot, ConfigScope.Local, c => c with
        {
            RepoHelper = (c.RepoHelper ?? new RepoHelperBehaviorConfig()) with { OptimizeInstructions = true },
        });
    }

    [Fact]
    public void EnsureSynced_OptimizeInstructionsExplicitlyOff_DoesNothing()
    {
        _behaviorConfig.Update(_workspaceRoot, ConfigScope.Local, c => c with
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
        _behaviorConfig.Update(_workspaceRoot, ConfigScope.Local, c => c with
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

    // ── repoHelper.combineClaudeMdForClaudeCode ──────────────────────────────────

    private void EnableCombineClaudeMdForClaudeCode()
    {
        _behaviorConfig.Update(_workspaceRoot, ConfigScope.Local, c => c with
        {
            RepoHelper = (c.RepoHelper ?? new RepoHelperBehaviorConfig()) with
            {
                OptimizeInstructions = true,
                CombineClaudeMdForClaudeCode = true,
            },
        });
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagUnset_StillSkipsBecauseNativelyLoaded()
    {
        EnableOptimizeInstructions();
        File.WriteAllText(_globalClaudeMdPath, "# Global rules\nAlways do X.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "CLAUDE.md")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "CLAUDE.human.md")));
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagOn_RenamesToHumanMdAndWritesCombinedClaudeMd()
    {
        EnableCombineClaudeMdForClaudeCode();
        File.WriteAllText(_globalClaudeMdPath, "# Global\nAlways do X.");
        string workspaceClaudeMdPath = Path.Combine(_workspaceRoot, "CLAUDE.md");
        File.WriteAllText(workspaceClaudeMdPath, "# Project\nHand-authored project rules.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string humanPath = Path.Combine(_workspaceRoot, "CLAUDE.human.md");
        Assert.True(File.Exists(humanPath));
        Assert.Equal("# Project\nHand-authored project rules.", File.ReadAllText(humanPath));

        Assert.True(File.Exists(workspaceClaudeMdPath));
        string generated = File.ReadAllText(workspaceClaudeMdPath);
        Assert.Contains("Generated by AiDevTools.TokenSaver.MCP", generated, StringComparison.Ordinal);
        Assert.Contains("Always do X.", generated, StringComparison.Ordinal);
        Assert.Contains("Hand-authored project rules.", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagOn_ReRunWithoutChanges_LeavesFileUntouched()
    {
        EnableCombineClaudeMdForClaudeCode();
        File.WriteAllText(_globalClaudeMdPath, "# Global\nAlways do X.");
        string workspaceClaudeMdPath = Path.Combine(_workspaceRoot, "CLAUDE.md");
        File.WriteAllText(workspaceClaudeMdPath, "# Project\nRules.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);
        DateTime firstWriteTimeUtc = File.GetLastWriteTimeUtc(workspaceClaudeMdPath);

        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.Equal(firstWriteTimeUtc, File.GetLastWriteTimeUtc(workspaceClaudeMdPath));
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagOn_HandEditedGeneratedFile_SkipsOverwrite()
    {
        EnableCombineClaudeMdForClaudeCode();
        File.WriteAllText(_globalClaudeMdPath, "# Global\nFirst version.");
        string workspaceClaudeMdPath = Path.Combine(_workspaceRoot, "CLAUDE.md");
        File.WriteAllText(workspaceClaudeMdPath, "# Project\nRules.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        // Someone edits the generated file directly, unaware it's managed.
        File.WriteAllText(workspaceClaudeMdPath, "Hand-edited directly, oblivious to the marker.");

        // Upstream source changes too — without the hash guard this would normally regenerate.
        File.WriteAllText(_globalClaudeMdPath, "# Global\nSecond version.");
        _service.EnsureSynced(_workspaceRoot, caller);

        Assert.Equal("Hand-edited directly, oblivious to the marker.", File.ReadAllText(workspaceClaudeMdPath));
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagOn_WritesClaudeMdExcludesForGlobalFile()
    {
        EnableCombineClaudeMdForClaudeCode();
        File.WriteAllText(_globalClaudeMdPath, "# Global\nAlways do X.");
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string settingsPath = Path.Combine(_workspaceRoot, ".claude", "settings.local.json");
        Assert.True(File.Exists(settingsPath));
        JsonNode root = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        JsonArray excludes = root["claudeMdExcludes"]!.AsArray();
        Assert.Contains(excludes, n => n!.GetValue<string>() == _globalClaudeMdPath);
    }

    [Fact]
    public void EnsureSynced_ClaudeCode_CombineFlagOn_OversizedSectionBecomesRealClaudeRulesFile()
    {
        EnableCombineClaudeMdForClaudeCode();
        string bigSection = "## Big Section\n" + string.Concat(Enumerable.Repeat("Detail. ", 600));
        File.WriteAllText(_globalClaudeMdPath, "# Global\n\nIntro.\n\n" + bigSection);
        CallerInfo caller = new() { ClientName = "claude-code" };

        _service.EnsureSynced(_workspaceRoot, caller);

        string rulePath = Path.Combine(_workspaceRoot, ".claude", "rules", "tokensaver-big-section.md");
        Assert.True(File.Exists(rulePath));
        Assert.Contains("Detail.", File.ReadAllText(rulePath), StringComparison.Ordinal);

        string generated = File.ReadAllText(Path.Combine(_workspaceRoot, "CLAUDE.md"));
        Assert.Contains("loaded on demand", generated, StringComparison.Ordinal);
    }
}
