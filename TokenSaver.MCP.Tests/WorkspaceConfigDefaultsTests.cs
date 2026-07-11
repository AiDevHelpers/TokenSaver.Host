using Microsoft.Extensions.Logging.Abstractions;

using TokenSaver.Config;

using Xunit;

namespace TokenSaver.MCP.Tests;

[Collection("GlobalConfigDirEnvVar")]
public sealed class WorkspaceConfigDefaultsTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private readonly string _workspaceRoot;
    private readonly string _globalDir;

    public WorkspaceConfigDefaultsTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("wcdTests-").FullName;
        // EnsureDefaults also bootstraps the machine-global config; point it at a temp dir instead
        // of the real %ProgramData%\TokenSaver so tests never touch (or race on) the real file.
        _globalDir = Directory.CreateTempSubdirectory("wcdTests-global-").FullName;
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
    private string GlobalConfigPath => Path.Combine(_globalDir, BehaviorConfigService.ConfigFileName);

    [Fact]
    public void EnsureDefaults_FreshWorkspace_CreatesLocalDir()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.True(Directory.Exists(LocalDir));
    }

    [Fact]
    public void EnsureDefaults_FreshWorkspace_CreatesConfigJsonWithAllSettingsCommentedOut()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        string configPath = Path.Combine(LocalDir, BehaviorConfigService.ConfigFileName);
        Assert.True(File.Exists(configPath));
        string content = File.ReadAllText(configPath);

        // Every top-level section is present so the file documents the full schema...
        Assert.Contains("\"repoHelper\"", content);
        Assert.Contains("\"codeHelper\"", content);
        Assert.Contains("\"tokenEconomy\"", content);
        Assert.Contains("\"context\"", content);
        Assert.Contains("\"agentConfig\"", content);
        Assert.Contains("\"lazyContext\"", content);

        // ...but every actual setting is commented out (no active overrides)...
        Assert.Contains("// \"addTokenSaverToGitignore\": true,", content);
        Assert.Contains("// \"optimizeInstructions\": true", content);
        Assert.Contains("// \"autoSplitOversizedInstructions\": true", content);
        Assert.Contains("// \"autoEnabled\": true,", content);
        Assert.Contains("// \"enableDistill\": true,", content);
        Assert.Contains("// \"contextSizeLimit\": 120000,", content);

        // ...and each setting has a preceding description comment.
        Assert.Contains("// Master switch for CodeHelper auto-workflow.", content);

        // File still parses as valid JSON-with-comments once loaded through the same reader
        // BehaviorConfigService uses (comments/commented-out lines are all ignored), yielding an
        // effectively-empty (all-null) config — i.e. this template changes nothing until a line
        // is uncommented.
        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            content,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        Assert.NotNull(parsed);
        Assert.Null(parsed!.RepoHelper?.AddTokenSaverToGitignore);
        Assert.Null(parsed.RepoHelper?.OptimizeInstructions);
    }

    [Fact]
    public void EnsureDefaults_FreshWorkspace_CreatesIgnoreAdditionalWithCommentHeader()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        string path = Path.Combine(LocalDir, ".ignore-additional");
        Assert.True(File.Exists(path));
        Assert.Contains("TokenSaver: extra gitignore-syntax patterns", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDefaults_FreshWorkspace_CreatesIgnoreBypassWithCommentHeader()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        string path = Path.Combine(LocalDir, ".ignore-bypass");
        Assert.True(File.Exists(path));
        Assert.Contains("force-include paths", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDefaults_FreshMachine_CreatesGlobalConfigJsonWithSettingsActive()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.True(File.Exists(GlobalConfigPath));
        string content = File.ReadAllText(GlobalConfigPath);

        // Every top-level section is present, same as the local template...
        Assert.Contains("\"repoHelper\"", content);
        Assert.Contains("\"codeHelper\"", content);
        Assert.Contains("\"tokenEconomy\"", content);
        Assert.Contains("\"context\"", content);
        Assert.Contains("\"agentConfig\"", content);
        Assert.Contains("\"lazyContext\"", content);

        // ...but settings are active (uncommented) rather than commented out...
        Assert.Contains("\"optimizeInstructions\": true", content);
        Assert.Contains("\"autoSplitOversizedInstructions\": true", content);
        Assert.Contains("\"autoEnabled\": true,", content);
        Assert.Contains("\"enableDistill\": true,", content);
        Assert.Contains("\"contextSizeLimit\": 120000,", content);
        Assert.DoesNotContain("// \"optimizeInstructions\": true", content);
        Assert.DoesNotContain("// \"autoSplitOversizedInstructions\": true", content);
        Assert.DoesNotContain("// \"autoEnabled\": true,", content);
        Assert.DoesNotContain("// \"enableDistill\": true,", content);

        // ...except the stateful "never asked yet" sentinel, which has no real default...
        Assert.Contains("// \"addTokenSaverToGitignore\": true,", content);

        // ...and each setting still has a preceding description comment.
        Assert.Contains("// Master switch for CodeHelper auto-workflow.", content);

        // Parses to exactly TokenSaverBehaviorConfig.Defaults() (all real defaults populated,
        // only the gitignore-prompt sentinel left null).
        TokenSaverBehaviorConfig? parsed = System.Text.Json.JsonSerializer.Deserialize<TokenSaverBehaviorConfig>(
            content,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        Assert.NotNull(parsed);
        Assert.Null(parsed!.RepoHelper?.AddTokenSaverToGitignore);
        Assert.True(parsed.RepoHelper?.OptimizeInstructions);
        Assert.True(parsed.RepoHelper?.AutoSplitOversizedInstructions);
        Assert.Equal(true, parsed.CodeHelper?.AutoEnabled);
        Assert.Equal(120_000, parsed.Context?.ContextSizeLimit);
        Assert.Equal(true, parsed.LazyContext?.Overrides?["claude-code"]);
    }

    [Fact]
    public void EnsureDefaults_PreExistingGlobalConfigJson_LeavesContentUntouched()
    {
        Directory.CreateDirectory(_globalDir);
        const string customContent = """{ "tokenEconomy": { "enableDistill": false } }""";
        File.WriteAllText(GlobalConfigPath, customContent);

        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.Equal(customContent, File.ReadAllText(GlobalConfigPath));
    }

    [Fact]
    public void EnsureDefaults_FreshWorkspace_DoesNotCreateContextExclusionsJson()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.False(File.Exists(Path.Combine(LocalDir, "context-exclusions.json")));
    }

    [Fact]
    public void EnsureDefaults_CalledTwice_DoesNotThrow()
    {
        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);
        Exception? ex = Record.Exception(() => WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureDefaults_PreExistingConfigJson_LeavesContentUntouched()
    {
        Directory.CreateDirectory(LocalDir);
        string configPath = Path.Combine(LocalDir, BehaviorConfigService.ConfigFileName);
        const string customContent = """{ "codeHelper": { "autoEnabled": true } }""";
        File.WriteAllText(configPath, customContent);

        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.Equal(customContent, File.ReadAllText(configPath));
    }

    [Fact]
    public void EnsureDefaults_PreExistingIgnoreAdditional_LeavesContentUntouched()
    {
        Directory.CreateDirectory(LocalDir);
        string path = Path.Combine(LocalDir, ".ignore-additional");
        const string customContent = "custom-pattern/**" + "\n";
        File.WriteAllText(path, customContent);

        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.Equal(customContent, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDefaults_PreExistingContextExclusionsJson_LeftUntouched()
    {
        Directory.CreateDirectory(LocalDir);
        string path = Path.Combine(LocalDir, "context-exclusions.json");
        const string customContent = """["**/bin/**"]""";
        File.WriteAllText(path, customContent);

        WorkspaceConfigDefaults.EnsureDefaults(_workspaceRoot, NullLogger.Instance);

        Assert.True(File.Exists(path));
        Assert.Equal(customContent, File.ReadAllText(path));
    }
}
