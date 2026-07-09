using Microsoft.Extensions.Logging.Abstractions;

using TokenSaver.Config;

using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class WorkspaceConfigDefaultsTests : IDisposable
{
    private readonly string _workspaceRoot;

    public WorkspaceConfigDefaultsTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("wcdTests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LocalDir => Path.Combine(_workspaceRoot, ".tokensaver");

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
        Assert.Contains("\"codeHelper\"", content);
        Assert.Contains("\"tokenEconomy\"", content);
        Assert.Contains("\"context\"", content);
        Assert.Contains("\"agentConfig\"", content);
        Assert.Contains("\"lazyContext\"", content);

        // ...but every actual setting is commented out (no active overrides)...
        Assert.Contains("// \"autoEnabled\": false,", content);
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
        Assert.Null(parsed!.AddTokenSaverToGitignore);
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
