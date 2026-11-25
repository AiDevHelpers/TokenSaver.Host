using Microsoft.Extensions.Logging.Abstractions;

using AiDevTools.TokenSaver.Config;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

[Collection("GlobalConfigDirEnvVar")]
public sealed class ConfigUpgradeServiceTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _workspaceRoot;
    private readonly string _globalDir;

    public ConfigUpgradeServiceTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("cusTests-").FullName;
        _globalDir = Directory.CreateTempSubdirectory("cusTests-global-").FullName;
        Environment.SetEnvironmentVariable(GlobalDirOverrideEnvVar, _globalDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GlobalDirOverrideEnvVar, null);

        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        try
        {
            Directory.Delete(_globalDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LocalDir => Path.Combine(_workspaceRoot, ".tokensaver");
    private string LocalConfigPath => Path.Combine(LocalDir, BehaviorConfigService.ConfigFileName);
    private string GlobalConfigPath => Path.Combine(_globalDir, BehaviorConfigService.ConfigFileName);

    [Fact]
    public void UpgradeIfNeeded_NoFileYet_DoesNothing()
    {
        Exception? ex = Record.Exception(() => ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance));

        Assert.Null(ex);
        Assert.False(File.Exists(LocalConfigPath));
    }

    [Fact]
    public void UpgradeIfNeeded_LocalFileMissingNewerSection_AppendsItCommentedOut()
    {
        Directory.CreateDirectory(LocalDir);
        const string customContent = """{ "codeHelper": { "autoEnabled": true } }""";
        File.WriteAllText(LocalConfigPath, customContent);

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);

        string updated = File.ReadAllText(LocalConfigPath);

        // The user's existing value is untouched (codeHelper's own closing brace now trails the
        // newly-added sibling leaves/objects, so it's no longer textually adjacent to
        // "autoEnabled": true — that's expected; check the value survived, not exact adjacency).
        Assert.Contains(""""{ "codeHelper": { "autoEnabled": true"""", updated, StringComparison.Ordinal);

        // ...but every other known top-level section is now present, still commented out.
        Assert.Contains("\"repoHelper\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"tokenEconomy\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"context\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"agentConfig\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"lazyContext\"", updated, StringComparison.Ordinal);
        Assert.Contains("// \"enableDistill\": true,", updated, StringComparison.Ordinal);

        // Still parses, and the pre-existing value survived the round trip.
        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            updated,
            s_jsonOptions);
        Assert.NotNull(parsed);
        Assert.True(parsed!.CodeHelper?.AutoEnabled);
    }

    [Fact]
    public void UpgradeIfNeeded_GlobalFileMissingNewerSection_AppendsItActive()
    {
        Directory.CreateDirectory(_globalDir);
        const string customContent = """{ "tokenEconomy": { "enableDistill": false } }""";
        File.WriteAllText(GlobalConfigPath, customContent);

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);

        string updated = File.ReadAllText(GlobalConfigPath);

        Assert.Contains(""""{ "tokenEconomy": { "enableDistill": false"""", updated, StringComparison.Ordinal);
        Assert.Contains("\"repoHelper\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"autoEnabled\": false,", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("// \"autoEnabled\": false,", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeIfNeeded_FileAlreadyHasAllSections_LeavesContentUntouched()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);
        string before = File.ReadAllText(LocalConfigPath);

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);

        Assert.Equal(before, File.ReadAllText(LocalConfigPath));
    }

    [Fact]
    public void UpgradeIfNeeded_CalledTwice_SecondCallIsNoOp()
    {
        Directory.CreateDirectory(LocalDir);
        File.WriteAllText(LocalConfigPath, """{ "codeHelper": { "autoEnabled": true } }""");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string afterFirst = File.ReadAllText(LocalConfigPath);

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string afterSecond = File.ReadAllText(LocalConfigPath);

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void UpgradeIfNeeded_ExistingSectionMissingLeaf_AddsOnlyThatLeaf()
    {
        Directory.CreateDirectory(LocalDir);
        // codeHelper exists but predates requireAnchorMatch/ownership/planner/calibration.
        File.WriteAllText(LocalConfigPath, """{ "codeHelper": { "autoEnabled": true } }""");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string updated = File.ReadAllText(LocalConfigPath);

        Assert.Contains("\"autoEnabled\": true", updated, StringComparison.Ordinal);
        Assert.Contains("// \"requireAnchorMatch\": true,", updated, StringComparison.Ordinal);
        Assert.Contains("\"ownership\"", updated, StringComparison.Ordinal);
        Assert.Contains("// \"canDoThreshold\": 0.70,", updated, StringComparison.Ordinal);
        Assert.Contains("\"planner\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"calibration\"", updated, StringComparison.Ordinal);

        // Sibling sections were never touched (no repoHelper key was there to begin with, and
        // this upgrade pass only fills what's missing under codeHelper plus the other five
        // top-level sections — verify codeHelper's own added leaf survives re-parsing too).
        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            updated,
            s_jsonOptions);
        Assert.True(parsed!.CodeHelper?.AutoEnabled);
        Assert.Null(parsed.CodeHelper?.RequireAnchorMatch);
    }

    [Fact]
    public void UpgradeIfNeeded_DeeplyNestedObjectPartiallyPresent_AddsOnlyMissingLeaf()
    {
        Directory.CreateDirectory(LocalDir);
        // codeHelper.ownership exists with only canDoThreshold set — deferThreshold predates it.
        File.WriteAllText(
            LocalConfigPath,
            """{ "codeHelper": { "ownership": { "canDoThreshold": 0.9 } } }""");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string updated = File.ReadAllText(LocalConfigPath);

        Assert.Contains("\"canDoThreshold\": 0.9", updated, StringComparison.Ordinal);
        Assert.Contains("// \"deferThreshold\": 0.40,", updated, StringComparison.Ordinal);
        // planner/calibration (ownership's siblings under codeHelper) and requireAnchorMatch
        // should also have been added, since codeHelper itself is missing them too.
        Assert.Contains("\"planner\"", updated, StringComparison.Ordinal);
        Assert.Contains("\"calibration\"", updated, StringComparison.Ordinal);

        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            updated,
            s_jsonOptions);
        Assert.Equal(0.9, parsed!.CodeHelper?.Ownership?.CanDoThreshold);
        Assert.Null(parsed.CodeHelper?.Ownership?.DeferThreshold);
    }

    [Fact]
    public void UpgradeIfNeeded_EmptyRootObject_AddsEveryTopLevelSectionAndAllChildren()
    {
        Directory.CreateDirectory(LocalDir);
        File.WriteAllText(LocalConfigPath, "{}");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string updated = File.ReadAllText(LocalConfigPath);

        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            updated,
            s_jsonOptions);
        Assert.NotNull(parsed);

        // Result should be textually identical to a freshly-generated file for a fresh workspace
        // (both render from the same ConfigSchemaTemplates tree).
        string fresh = ConfigSchemaTemplates.CommentedFileTemplate;
        string freshParsedNormalized = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
                fresh,
                s_jsonOptions));
        string updatedParsedNormalized = System.Text.Json.JsonSerializer.Serialize(parsed);
        Assert.Equal(freshParsedNormalized, updatedParsedNormalized);
    }

    [Fact]
    public void UpgradeIfNeeded_PreservesUnrelatedCommentsAndFormatting()
    {
        Directory.CreateDirectory(LocalDir);
        const string customContent = """
            {
              // my own note about this workspace's setup — keep me around
              "codeHelper": { "autoEnabled": true }
            }
            """;
        File.WriteAllText(LocalConfigPath, customContent);

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string updated = File.ReadAllText(LocalConfigPath);

        Assert.Contains("my own note about this workspace's setup — keep me around", updated, StringComparison.Ordinal);
        Assert.Contains("\"autoEnabled\": true", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeIfNeeded_ExistingUncommentedLeaf_NotTreatedAsMissing()
    {
        // A user who manually uncommented/activated a local override must not have the
        // now-redundant commented-out documentation line re-added next to it.
        Directory.CreateDirectory(LocalDir);
        File.WriteAllText(LocalConfigPath, """{ "context": { "debug": true } }""");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string updated = File.ReadAllText(LocalConfigPath);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(updated, "\"debug\""));
    }

    [Fact]
    public void UpgradeIfNeeded_SentinelLeafInPartiallyPresentObject_NeverRepeatedlyReinserted()
    {
        // repoHelper.addTokenSaverToGitignore and agentConfig.anthropic's three fallback-to-global
        // overrides are always commented out, even in a freshly-generated file — their permanent
        // absence from live JSON is by design (see ConfigElementTemplate.SentinelNeverActive), not
        // a schema gap. Once the parent object exists, they must never be individually re-added.
        Directory.CreateDirectory(LocalDir);
        File.WriteAllText(
            LocalConfigPath,
            """{ "agentConfig": { "anthropic": { "enableContextEditing": true } } }""");

        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string first = File.ReadAllText(LocalConfigPath);
        ConfigUpgradeService.UpgradeIfNeeded(_workspaceRoot, NullLogger.Instance);
        string second = File.ReadAllText(LocalConfigPath);

        Assert.Equal(first, second);
        Assert.DoesNotContain("Overrides enablePromptCaching for Anthropic calls", first, StringComparison.Ordinal);
    }
}
