#pragma warning disable CA1859, CA2000 // Test helpers return interfaces for clarity; temp objects are short-lived.
using System.Text;
using System.Text.Json.Nodes;

using AiDevTools.TokenSaver.Common.Models;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     The provider config writer on a temporary profile. Nothing here touches a real client file: every path lives
///     under a per-test temp folder.
/// </summary>
public sealed class ProviderConfigWriterTests : IDisposable
{
    private const string Token = "tok-0123456789abcdef0123456789abcdef0123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcw-" + Guid.NewGuid().ToString("N"));
    private readonly ProviderPaths _paths;

    public ProviderConfigWriterTests()
    {
        _ = Directory.CreateDirectory(_root);
        _paths = new ProviderPaths(Path.Combine(_root, "home"), Path.Combine(_root, "appdata"));
    }

    public void Dispose()
    {
        JsonConfigEditor.BeforeCommitForTests = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AiProviderConfigService Service(string? token = Token) => new(_paths, () => token);

    private string VsCodeMcp => _paths.ConfigPath(ProviderType.VsCodeCopilot);

    private void InstallVsCode()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(VsCodeMcp)!);
    }

    private async Task<ConfigUpdateResult> RunOne(ProviderType type, AiProviderConfigService? service = null)
    {
        IReadOnlyList<ConfigUpdateResult> all = await (service ?? Service()).UpdateAllProvidersAsync(11436);
        return all.Single(r => r.Provider == type);
    }

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path).TrimStart('\uFEFF'))!;

    [Fact]
    public async Task VsCode_MissingFile_IsCreatedWithTheEntryAndTheToken()
    {
        InstallVsCode();

        ConfigUpdateResult result = await RunOne(ProviderType.VsCodeCopilot);

        Assert.True(result.Success, result.Message);
        JsonNode entry = Read(VsCodeMcp)["servers"]!["tokensaver"]!;
        Assert.Equal("http", (string?)entry["type"]);
        Assert.Equal("http://localhost:11436/mcp", (string?)entry["url"]);
        Assert.Equal($"Bearer {Token}", (string?)entry["headers"]!["Authorization"]);
    }

    [Fact]
    public async Task OtherServersAndSettings_AreKeptInOrderAndABackupIsMade()
    {
        InstallVsCode();
        string original = "{\n  \"inputs\": [],\n  \"servers\": {\n    \"other\": { \"command\": \"x\", \"args\": [\"a\"] }\n  },\n  \"zeta\": 1\n}\n";
        File.WriteAllText(VsCodeMcp, original);

        ConfigUpdateResult result = await RunOne(ProviderType.VsCodeCopilot);

        Assert.True(result.Success, result.Message);
        JsonObject root = (JsonObject)Read(VsCodeMcp);
        Assert.Equal(["inputs", "servers", "zeta"], root.Select(kv => kv.Key).ToArray());
        Assert.Equal(["other", "tokensaver"], ((JsonObject)root["servers"]!).Select(kv => kv.Key).ToArray());
        string backup = Directory.GetFiles(Path.GetDirectoryName(VsCodeMcp)!, "mcp.json.bak-*").Single();
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public async Task RunningTwice_IsIdempotentAndMakesNoSecondBackup()
    {
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, "{ \"servers\": {} }");
        _ = await RunOne(ProviderType.VsCodeCopilot);
        string afterFirst = File.ReadAllText(VsCodeMcp);

        ConfigUpdateResult second = await RunOne(ProviderType.VsCodeCopilot);

        Assert.True(second.Success);
        Assert.True(second.WasAlreadyConfigured);
        Assert.Equal(afterFirst, File.ReadAllText(VsCodeMcp));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(VsCodeMcp)!, "mcp.json.bak-*"));
    }

    [Fact]
    public async Task ATokenChange_UpdatesTheEntry_AndNoTokenMeansNoHeader()
    {
        InstallVsCode();
        _ = await RunOne(ProviderType.VsCodeCopilot);

        ConfigUpdateResult changed = await RunOne(ProviderType.VsCodeCopilot, Service("new-token-0123456789abcdef0123456789abcdef"));
        Assert.True(changed.Success);
        Assert.False(changed.WasAlreadyConfigured);
        Assert.Equal("Bearer new-token-0123456789abcdef0123456789abcdef", (string?)Read(VsCodeMcp)["servers"]!["tokensaver"]!["headers"]!["Authorization"]);

        _ = await RunOne(ProviderType.VsCodeCopilot, Service(token: null));
        Assert.Null(Read(VsCodeMcp)["servers"]!["tokensaver"]!["headers"]);
    }

    [Fact]
    public async Task AnExistingEntryUnderADifferentCase_IsUpdatedInPlaceNotDuplicated()
    {
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, "{ \"servers\": { \"TokenSaver\": { \"type\": \"http\", \"url\": \"http://localhost:1/mcp\" } } }");

        _ = await RunOne(ProviderType.VsCodeCopilot);

        JsonObject servers = (JsonObject)Read(VsCodeMcp)["servers"]!;
        Assert.Equal(["TokenSaver"], servers.Select(kv => kv.Key).ToArray());
        Assert.Equal("http://localhost:11436/mcp", (string?)servers["TokenSaver"]!["url"]);
    }

    [Theory]
    [InlineData("{ // user note\n  \"servers\": {} }", ConfigFailure.CommentsOrTrailingCommas)]
    [InlineData("{ /* block */ \"servers\": {} }", ConfigFailure.CommentsOrTrailingCommas)]
    [InlineData("{ \"servers\": { \"a\": {}, }, }", ConfigFailure.CommentsOrTrailingCommas)]
    [InlineData("{ \"servers\": ", ConfigFailure.InvalidJson)]
    [InlineData("[1,2,3]", ConfigFailure.UnexpectedStructure)]
    [InlineData("{ \"servers\": [] }", ConfigFailure.UnexpectedStructure)]
    public async Task AFileThatCannotBeRewrittenFaithfully_IsLeftByteForByteAloneWithAReason(string content, string reason)
    {
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, content);
        byte[] before = File.ReadAllBytes(VsCodeMcp);

        ConfigUpdateResult result = await RunOne(ProviderType.VsCodeCopilot);

        Assert.False(result.Success);
        Assert.Equal(reason, result.FailureReason);
        Assert.Equal(before, File.ReadAllBytes(VsCodeMcp));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(VsCodeMcp)!, "*.bak-*"));
    }

    [Fact]
    public async Task BomAndCrlfLineEndings_ArePreserved()
    {
        InstallVsCode();
        File.WriteAllBytes(VsCodeMcp, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("{\r\n  \"servers\": {}\r\n}\r\n")]);

        _ = await RunOne(ProviderType.VsCodeCopilot);

        byte[] after = File.ReadAllBytes(VsCodeMcp);
        Assert.Equal([0xEF, 0xBB, 0xBF], after[..3]);
        string text = Encoding.UTF8.GetString(after, 3, after.Length - 3);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        // Every newline is CRLF (no bare LF, no doubled CR).
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
        Assert.Equal(text.Count(c => c == '\r'), text.Count(c => c == '\n'));
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnLfFile_StaysLf_EvenOnWindows()
    {
        // Regression: System.Text.Json writes the OS newline, which turned an LF file into CRLF on Windows and
        // changed every line of a large ~/.claude.json. Found by running the writer on a copy of a real config.
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, "{\n  \"servers\": {}\n}\n");

        _ = await RunOne(ProviderType.VsCodeCopilot);

        string text = File.ReadAllText(VsCodeMcp);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALargeClaudeCodeStyleFile_KeepsEverythingElseExactly()
    {
        // ~/.claude.json is large, deeply nested and full of values a naive rewrite mangles.
        _ = Directory.CreateDirectory(_paths.Home);
        string path = _paths.ConfigPath(ProviderType.ClaudeCode);
        JsonObject projects = [];
        for (int i = 0; i < 400; i++)
        {
            projects[$"C:\\\\repos\\\\p{i}"] = new JsonObject
            {
                ["allowedTools"] = new JsonArray("a", "b"),
                ["history"] = new JsonArray(Enumerable.Range(0, 20).Select(n => (JsonNode?)JsonValue.Create($"prompt {n} \"quoted\" ünïcode ✓ & <tag>")).ToArray()),
                ["lastCost"] = 1.50,
            };
        }

        string text = "{\"numStartups\": 12345678901234567890, \"ratio\": 1.50, \"tiny\": 1e-7, \"projects\": " + projects.ToJsonString() + ", \"mcpServers\": {\"x\": {\"command\": \"y\"}}}";
        File.WriteAllText(path, text);
        Assert.True(new FileInfo(path).Length > 100_000);

        ConfigUpdateResult result = await RunOne(ProviderType.ClaudeCode);

        Assert.True(result.Success, result.Message);
        JsonNode before = JsonNode.Parse(text)!;
        JsonNode after = Read(path);
        _ = ((JsonObject)after["mcpServers"]!).Remove("tokensaver");
        Assert.True(JsonNode.DeepEquals(before, after), "everything except the new entry must be unchanged");
        string raw = File.ReadAllText(path);
        Assert.Contains("12345678901234567890", raw, StringComparison.Ordinal);
        Assert.Contains("1.50", raw, StringComparison.Ordinal);
        Assert.Contains("1e-7", raw, StringComparison.Ordinal);
        Assert.Contains("ünïcode ✓ & <tag>", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaudeCode_IsNotInstalledWithoutItsStateFile_AndTheFileIsNeverCreated()
    {
        IReadOnlyList<ConfigUpdateResult> all = await Service().UpdateAllProvidersAsync(11436);

        Assert.DoesNotContain(all, r => r.Provider == ProviderType.ClaudeCode);
        Assert.False(File.Exists(_paths.ConfigPath(ProviderType.ClaudeCode)));
    }

    [Fact]
    public async Task AnotherProcessEditingTheFileMidWrite_IsDetectedAndItsChangeIsKept()
    {
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, "{ \"servers\": {} }");
        int calls = 0;
        JsonConfigEditor.BeforeCommitForTests = path =>
        {
            if (calls++ == 0)
            {
                File.WriteAllText(path, "{ \"servers\": {}, \"theirSetting\": true }");
            }
        };

        ConfigUpdateResult result = await RunOne(ProviderType.VsCodeCopilot);

        Assert.True(result.Success, result.Message);
        JsonNode final = Read(VsCodeMcp);
        Assert.True((bool?)final["theirSetting"]);
        Assert.NotNull(final["servers"]!["tokensaver"]);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task AFileHeldOpenExclusively_FailsAsLockedWithoutChangingIt()
    {
        InstallVsCode();
        File.WriteAllText(VsCodeMcp, "{ \"servers\": {} }");
        byte[] before = File.ReadAllBytes(VsCodeMcp);

        ConfigUpdateResult result;
        await using (FileStream held = new(VsCodeMcp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await RunOne(ProviderType.VsCodeCopilot);
        }

        Assert.False(result.Success);
        Assert.Equal(ConfigFailure.Locked, result.FailureReason);
        Assert.Equal(before, File.ReadAllBytes(VsCodeMcp));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(VsCodeMcp)!, "*.tokensaver-tmp-*"));
    }

    [Fact]
    public async Task ClineAndSixth_GetTheirOwnShapes()
    {
        _ = Directory.CreateDirectory(Path.Combine(_paths.AppData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"));
        _ = Directory.CreateDirectory(Path.Combine(_paths.AppData, "Code", "User", "globalStorage", "sixth.sixth-ai"));

        ConfigUpdateResult cline = await RunOne(ProviderType.Cline);
        ConfigUpdateResult sixth = await RunOne(ProviderType.Sixth);

        Assert.True(cline.Success, cline.Message);
        Assert.True(sixth.Success, sixth.Message);
        JsonNode c = Read(cline.ConfigPath!)["mcpServers"]!["tokensaver"]!;
        Assert.Equal("streamableHttp", (string?)c["type"]);
        Assert.False((bool?)c["disabled"]);
        JsonNode s = Read(sixth.ConfigPath!)["mcpServers"]!["tokensaver"]!;
        Assert.Null(s["type"]);
        Assert.False((bool?)s["disabled"]);
    }

    [Fact]
    public async Task Cursor_UsesMcpServersWithoutAType()
    {
        _ = Directory.CreateDirectory(Path.Combine(_paths.Home, ".cursor"));

        ConfigUpdateResult result = await RunOne(ProviderType.Cursor);

        Assert.True(result.Success, result.Message);
        JsonNode entry = Read(result.ConfigPath!)["mcpServers"]!["tokensaver"]!;
        Assert.Null(entry["type"]);
        Assert.Equal("http://localhost:11436/mcp", (string?)entry["url"]);
    }

    [Fact]
    public async Task Continue_GetsAFileOfItsOwn_NotAMergeIntoTheUsersConfig()
    {
        string userConfig = Path.Combine(_paths.Home, ".continue", "config.yaml");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(userConfig)!);
        File.WriteAllText(userConfig, "name: mine\nmodels: []\n");

        ConfigUpdateResult first = await RunOne(ProviderType.Continue);
        ConfigUpdateResult second = await RunOne(ProviderType.Continue);

        Assert.True(first.Success, first.Message);
        Assert.True(second.WasAlreadyConfigured);
        Assert.Equal("name: mine\nmodels: []\n", File.ReadAllText(userConfig));
        string yaml = File.ReadAllText(first.ConfigPath!);
        Assert.Contains("type: streamable-http", yaml, StringComparison.Ordinal);
        Assert.Contains($"Authorization: Bearer {Token}", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaudeDesktop_IsReportedUnsupportedNotWritten()
    {
        _ = Directory.CreateDirectory(Path.Combine(_paths.AppData, "Claude"));

        ConfigUpdateResult result = await RunOne(ProviderType.ClaudeDesktop);

        Assert.False(result.Success);
        Assert.Equal(ConfigFailure.Unsupported, result.FailureReason);
        Assert.False(File.Exists(result.ConfigPath));
    }

    [Fact]
    public async Task ClientsThatAreNotInstalled_AreSkipped()
    {
        IReadOnlyList<ConfigUpdateResult> all = await Service().UpdateAllProvidersAsync(11436);

        Assert.Empty(all);
        Assert.False(Directory.Exists(_paths.Home));
    }
}

