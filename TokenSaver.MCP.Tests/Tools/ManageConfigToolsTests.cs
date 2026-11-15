using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Config;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Closes CLAUDE.md's "Known gaps" entry for the MCP Tools/*.cs classes, starting with
///     ManageConfigTools since it is entirely synchronous and file/config-based (no live
///     Ollama/SimpleModelServer dependency needed). Covers every action in ManageConfig's dispatcher
///     against a temp workspace root, asserting on the returned JSON/text contract.
/// </summary>
[Collection("GlobalConfigDirEnvVar")]
public sealed class ManageConfigToolsTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private readonly string _workspaceRoot;
    private readonly string _globalDir;
    private readonly BehaviorConfigService _behaviorConfig;

    public ManageConfigToolsTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("mctTests-").FullName;
        // BehaviorConfigService.GlobalConfigPath otherwise resolves to the real machine-wide
        // %ProgramData%\TokenSaver\config.json, which WorkspaceConfigDefaults now populates with
        // real default overrides (e.g. lazyContext claude-code=true) — merging that in would
        // break tests asserting on local-only state. Redirect to an empty temp dir instead.
        _globalDir = Directory.CreateTempSubdirectory("mctTests-global-").FullName;
        Environment.SetEnvironmentVariable(GlobalDirOverrideEnvVar, _globalDir);
        _behaviorConfig = new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance);
    }

    public void Dispose()
    {
        _behaviorConfig.Dispose();
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

    private ManageConfigTools CreateTools(HttpContext? httpContext = null)
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = _workspaceRoot };
        WorkspaceConfigManager workspaceConfig = new(Options.Create(cfg), NullLogger<WorkspaceConfigManager>.Instance);

        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "claude-code" });
        AgentAdapterRegistry registry = new([new FakeModelAdapter("Anthropic")], NullLogger<AgentAdapterRegistry>.Instance);
        AgentConfigResolver agentConfigResolver = new(detection, registry, _behaviorConfig);
        LazyContextResolver lazyContextResolver = new(detection, _behaviorConfig);

        HttpContextAccessor accessor = new() { HttpContext = httpContext };
        WorkspaceRootSessionResolver workspaceRootResolver = WorkspaceRootSessionResolverTestFactory.Create(cfg);

        return new ManageConfigTools(_behaviorConfig, workspaceConfig, agentConfigResolver, lazyContextResolver, accessor, workspaceRootResolver);
    }

    private sealed class FakeCallerDetectionService(CallerInfo toReturn) : ICallerDetectionService
    {
        public CallerInfo Detect(HttpContext httpContext, string? mcpClientName = null, string? mcpClientVersion = null) => toReturn;
    }

    private sealed class FakeModelAdapter(string family) : IAgentAdapter
    {
        public string Family => family;
        public AdapterKind Kind => AdapterKind.Model;
        public int Priority => 100;

        public bool CanHandle(CallerInfo caller) => true;

        public ContextFormat GetFormat(CallerInfo caller) => ContextFormat.Default;

        public string AdaptContext(string rawContext, CallerInfo caller, ContextFormat format) => rawContext;
    }

    // ── get ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsMergedConfigAndPaths()
    {
        ManageConfigTools tools = CreateTools();

        string json = await tools.ManageConfig("get");
        JsonElement root = JsonElement.Parse(json);

        Assert.True(root.TryGetProperty("merged", out _));
        Assert.Equal(BehaviorConfigService.LocalConfigPath(_workspaceRoot), root.GetProperty("localPath").GetString());
        Assert.Equal(BehaviorConfigService.GlobalConfigPath, root.GetProperty("globalPath").GetString());
        Assert.True(root.TryGetProperty("gitignorePromptPending", out _));
    }

    [Fact]
    public async Task Get_WithHttpContext_PopulatesDetectedAgentAndDetectedLazyContext()
    {
        ManageConfigTools tools = CreateTools(new DefaultHttpContext());

        string json = await tools.ManageConfig("get");
        JsonElement root = JsonElement.Parse(json);

        JsonElement detectedAgent = root.GetProperty("detectedAgent");
        Assert.Equal("Anthropic", detectedAgent.GetProperty("family").GetString());
        Assert.Equal("claude-code", detectedAgent.GetProperty("clientName").GetString());

        JsonElement detectedLazy = root.GetProperty("detectedLazyContext");
        Assert.Equal("claude-code", detectedLazy.GetProperty("clientName").GetString());
    }

    [Fact]
    public async Task Get_WithoutHttpContext_LeavesDetectedFieldsNull()
    {
        ManageConfigTools tools = CreateTools(httpContext: null);

        string json = await tools.ManageConfig("get");
        JsonElement root = JsonElement.Parse(json);

        Assert.Equal(JsonValueKind.Null, root.GetProperty("detectedAgent").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("detectedLazyContext").ValueKind);
    }

    [Fact]
    public async Task UnknownAction_ReturnsUsageMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("bogus-action");

        Assert.Contains("Unknown action", result, StringComparison.Ordinal);
        Assert.Contains("get, set-gitignore-pref", result, StringComparison.Ordinal);
    }

    // ── set-gitignore-pref ─────────────────────────────────────────────────────

    [Fact]
    public async Task SetGitignorePref_AddTrue_CreatesGitignoreAndAppendsEntry()
    {
        ManageConfigTools tools = CreateTools();
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".gitignore")));

        string result = await tools.ManageConfig("set-gitignore-pref", add: true, scope: "local");

        string gitignorePath = Path.Combine(_workspaceRoot, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Contains(".tokensaver", await File.ReadAllTextAsync(gitignorePath), StringComparison.Ordinal);
        Assert.Contains("Recorded", result, StringComparison.Ordinal);

        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.True(cfg.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public async Task SetGitignorePref_AddTrue_WithExistingGitignoreCoveringEntry_DoesNotDuplicate()
    {
        string gitignorePath = Path.Combine(_workspaceRoot, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, ".tokensaver/\n");
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-gitignore-pref", add: true, scope: "local");

        Assert.Contains("already covered", result, StringComparison.Ordinal);
        Assert.Equal(1, (await File.ReadAllLinesAsync(gitignorePath)).Count(l => l.Contains(".tokensaver", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SetGitignorePref_AddFalse_RecordsPreferenceWithoutTouchingGitignore()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-gitignore-pref", add: false, scope: "local");

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".gitignore")));
        Assert.Contains("will not add", result, StringComparison.Ordinal);

        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.False(cfg.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public async Task SetGitignorePref_MissingAdd_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-gitignore-pref", add: null, scope: "local");

        Assert.Contains("requires 'add'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetGitignorePref_MissingScope_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-gitignore-pref", add: true, scope: null);

        Assert.Contains("requires 'scope'", result, StringComparison.Ordinal);
    }

    // ── ignore-additional / ignore-bypass ──────────────────────────────────────

    [Fact]
    public async Task IgnoreAdditional_AppendsPatternsToIgnoreAdditionalFile()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("ignore-additional", paths: ["**/generated/**", "*.snap"]);

        string path = WorkspaceIgnoreFilter.AdditionalPath(_workspaceRoot);
        Assert.True(File.Exists(path));
        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("**/generated/**", content, StringComparison.Ordinal);
        Assert.Contains("*.snap", content, StringComparison.Ordinal);
        Assert.Contains("Added 2", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoreBypass_AppendsPatternsToIgnoreBypassFile()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("ignore-bypass", paths: ["vendor/important.lock"]);

        string path = WorkspaceIgnoreFilter.BypassPath(_workspaceRoot);
        Assert.True(File.Exists(path));
        Assert.Contains("vendor/important.lock", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Contains("bypass (force-include)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoreAdditional_NoPaths_ReturnsGuidanceMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("ignore-additional", paths: null);

        Assert.Contains("No patterns supplied", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoreAdditional_DuplicatePattern_ReportsNothingAdded()
    {
        ManageConfigTools tools = CreateTools();
        await tools.ManageConfig("ignore-additional", paths: ["dup.txt"]);

        string result = await tools.ManageConfig("ignore-additional", paths: ["dup.txt"]);

        Assert.Contains("Nothing added", result, StringComparison.Ordinal);
    }

    // ── set ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Set_BoolField_Local_UpdatesConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: "true");

        Assert.Contains("Set 'autoIntercept' = true (Local)", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.True(cfg.Context?.AutoIntercept);
    }

    [Fact]
    public async Task Set_NumericField_UpdatesConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: "contextSizeLimit", value: "50000");

        Assert.Contains("Set 'contextSizeLimit' = 50000", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.Equal(50000, cfg.Context?.ContextSizeLimit);
    }

    [Fact]
    public async Task Set_DoubleField_UpdatesConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: "wasteThreshold", value: "0.42");

        Assert.Contains("Set 'wasteThreshold' = 0.42", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.Equal(0.42, cfg.CodeHelper?.Calibration?.WasteThreshold);
    }

    [Fact]
    public async Task Set_ValueNull_ClearsOverride()
    {
        ManageConfigTools tools = CreateTools();
        await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: "true");
        Assert.True(_behaviorConfig.GetConfig(_workspaceRoot).Context?.AutoIntercept);

        string result = await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: null);

        Assert.Contains("Cleared override for 'autoIntercept'", result, StringComparison.Ordinal);
        Assert.Null(_behaviorConfig.GetConfig(_workspaceRoot).Context?.AutoIntercept);
    }

    [Fact]
    public async Task Set_ValueLiteralNull_ClearsOverride()
    {
        ManageConfigTools tools = CreateTools();
        await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: "true");

        string result = await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: "null");

        Assert.Contains("Cleared override for 'autoIntercept'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_UnknownField_ReturnsErrorMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: "notAField", value: "true");

        Assert.Contains("Unknown field 'notAField'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_MissingField_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: null, value: "true");

        Assert.Contains("requires 'field'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_MissingScope_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: null, field: "autoIntercept", value: "true");

        Assert.Contains("requires 'scope'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_BoolField_NonBooleanValue_ReturnsErrorMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set", scope: "local", field: "autoIntercept", value: "notabool");

        Assert.Contains("expects a boolean value", result, StringComparison.Ordinal);
    }

    // NOTE: no test writes scope="global" here — BehaviorConfigService.GlobalConfigPath resolves
    // to the real machine-wide %ProgramData%\TokenSaver\config.json, which every test process on
    // this machine (including other test classes running in parallel) reads via GetConfig(). A
    // test that mutates it, even if it reverts afterward, races other in-flight tests and was
    // observed to flake Set_ValueNull_ClearsOverride above under xunit's default parallel-by-class
    // execution. Local-vs-global merge precedence is already covered without touching the real
    // file by BehaviorConfigOverlayTests (constructs both configs in isolation); the "scope" label
    // itself (TryParseScope) is exercised via the local-scope tests' "(Local)" assertions and the
    // missing-scope validation tests above.

    // ── set-agent ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAgent_GlobalField_UpdatesTopLevelAgentConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", field: "enablePromptCaching", value: "true");

        Assert.Contains("Set 'global.enablePromptCaching' = true", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.True(cfg.AgentConfig?.EnablePromptCaching);
    }

    [Fact]
    public async Task SetAgent_FamilySpecificOverride_UpdatesFamilySection()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "anthropic", field: "cacheTtlSeconds", value: "3600");

        Assert.Contains("Set 'anthropic.cacheTtlSeconds' = 3600", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.Equal(3600, cfg.AgentConfig?.Anthropic?.CacheTtlSeconds);
    }

    // NOTE: ManageConfig's own [Description] documents deepSeek's cacheTtlSeconds/minCacheableTokens
    // as rejected ("DeepSeek's context caching isn't configurable"), but SetAgentField's isInt gate
    // only restricts Anthropic-/OpenSource-only fields to their family — cacheTtlSeconds and
    // minCacheableTokens are in AgentCommonIntFields, so family=deepSeek passes the gate. It then
    // falls into ApplyAgentIntField, whose deepSeek branch doesn't exist, so the mutation silently
    // no-ops while the tool still reports "Set ... (Local)". These two tests pin the actual
    // (buggy) behavior rather than the documented contract — see CLAUDE.md's "Known gaps" note
    // about this tool class having zero tests/contract enforcement before this file.
    [Fact]
    public async Task SetAgent_DeepSeekTtl_SilentlyNoOpsDespiteReportingSuccess()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "deepSeek", field: "cacheTtlSeconds", value: "3600");

        Assert.Contains("Set 'deepSeek.cacheTtlSeconds' = 3600", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        // ApplyAgentIntField has no "deepSeek" branch at all (only global/anthropic/qwen/openSource),
        // so it falls through to `return a;` unchanged. DeepSeekAgentConfig doesn't even have a
        // CacheTtlSeconds/MinCacheableTokens property to set — the only field that exists on it is
        // EnablePromptCaching, which BehaviorConfigService.Update() seeds from
        // TokenSaverBehaviorConfig.Defaults() (DeepSeekAgentConfig.Defaults() => EnablePromptCaching
        // = true) whenever it reads a not-yet-existing local file, independent of this call. So the
        // only verifiable evidence of the no-op is: no exception, no ttl-like field appears anywhere
        // in the written JSON, and EnablePromptCaching is exactly the seeded default rather than
        // something this "cacheTtlSeconds" call could have produced.
        Assert.True(cfg.AgentConfig?.DeepSeek?.EnablePromptCaching);
        string localJson = await File.ReadAllTextAsync(BehaviorConfigService.LocalConfigPath(_workspaceRoot));
        Assert.DoesNotContain("3600", localJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_DeepSeekMinTokens_SilentlyNoOpsDespiteReportingSuccess()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "deepSeek", field: "minCacheableTokens", value: "1024");

        Assert.Contains("Set 'deepSeek.minCacheableTokens' = 1024", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_DeepSeekEnablePromptCaching_IsAllowed()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "deepSeek", field: "enablePromptCaching", value: "true");

        Assert.Contains("Set 'deepSeek.enablePromptCaching' = true", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_ValueNull_ClearsOverride()
    {
        ManageConfigTools tools = CreateTools();
        await tools.ManageConfig("set-agent", scope: "local", family: "anthropic", field: "enableContextEditing", value: "true");

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "anthropic", field: "enableContextEditing", value: null);

        Assert.Contains("Cleared override for 'anthropic.enableContextEditing'", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.Null(cfg.AgentConfig?.Anthropic?.EnableContextEditing);
    }

    [Fact]
    public async Task SetAgent_KvCacheQuantization_OpenSourceOnly()
    {
        ManageConfigTools tools = CreateTools();

        string rejected = await tools.ManageConfig("set-agent", scope: "local", family: "anthropic", field: "kvCacheQuantization", value: "q8_0");
        Assert.Contains("only valid for family=openSource", rejected, StringComparison.Ordinal);

        string ok = await tools.ManageConfig("set-agent", scope: "local", family: "openSource", field: "kvCacheQuantization", value: "q8_0");
        Assert.Contains("Set 'openSource.kvCacheQuantization' = q8_0", ok, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.Equal("q8_0", cfg.AgentConfig?.OpenSource?.KvCacheQuantization);
    }

    [Fact]
    public async Task SetAgent_KvCacheQuantization_InvalidValue_IsRejected()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "openSource", field: "kvCacheQuantization", value: "zstd");

        Assert.Contains("expects 'f16', 'q8_0', 'q4_0'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_UnknownFamily_ReturnsErrorMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", family: "bogus", field: "enablePromptCaching", value: "true");

        Assert.Contains("Unknown family 'bogus'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_MissingField_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: "local", field: null, value: "true");

        Assert.Contains("requires 'field'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAgent_MissingScope_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-agent", scope: null, field: "enablePromptCaching", value: "true");

        Assert.Contains("requires 'scope'", result, StringComparison.Ordinal);
    }

    // ── set-lazy-context ────────────────────────────────────────────────────

    [Fact]
    public async Task SetLazyContext_WorkspaceDefault_UpdatesConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", value: "true");

        Assert.Contains("Set lazyContext default", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.True(cfg.LazyContext?.DefaultWillAskForMissingContext);
    }

    [Fact]
    public async Task SetLazyContext_PerClientOverride_UpdatesConfig()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", clientName: "claude-code", value: "true");

        Assert.Contains("Set lazy-context override for 'claude-code' = true", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.True(cfg.LazyContext?.Overrides?["claude-code"]);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("copilot")]
    [InlineData("github-copilot")]
    [InlineData("continue")]
    [InlineData("continue.dev")]
    public async Task SetLazyContext_HardcodedClientNames_AreRejected(string clientName)
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", clientName: clientName, value: "true");

        Assert.Contains("cannot be overridden", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.False(cfg.LazyContext?.Overrides?.ContainsKey(clientName) ?? false);
    }

    [Fact]
    public async Task SetLazyContext_ClearClientOverride_RemovesEntry()
    {
        ManageConfigTools tools = CreateTools();
        await tools.ManageConfig("set-lazy-context", scope: "local", clientName: "claude-code", value: "true");

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", clientName: "claude-code", value: null);

        Assert.Contains("Cleared lazy-context override for 'claude-code'", result, StringComparison.Ordinal);
        TokenSaverBehaviorConfig cfg = _behaviorConfig.GetConfig(_workspaceRoot);
        Assert.False(cfg.LazyContext?.Overrides?.ContainsKey("claude-code") ?? false);
    }

    [Fact]
    public async Task SetLazyContext_WorkspaceDefault_CannotBeClearedToNull()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", value: null);

        Assert.Contains("cannot be cleared to null", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLazyContext_MissingScope_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: null, value: "true");

        Assert.Contains("requires 'scope'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLazyContext_NonBooleanClientValue_ReturnsValidationMessage()
    {
        ManageConfigTools tools = CreateTools();

        string result = await tools.ManageConfig("set-lazy-context", scope: "local", clientName: "claude-code", value: "maybe");

        Assert.Contains("requires 'value' to be true, false, or 'null'", result, StringComparison.Ordinal);
    }
}
