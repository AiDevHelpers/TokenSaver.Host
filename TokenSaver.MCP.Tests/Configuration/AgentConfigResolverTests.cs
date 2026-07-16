using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.Config;
using TokenSaver.MCP.Services;

using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class AgentConfigResolverStaticResolveTests
{
    [Fact]
    public void Resolve_Anthropic_UsesFamilyOverridesOverGlobalDefaults()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
            Anthropic = new AnthropicAgentConfig
            {
                CacheTtlSeconds = 3600,
                EnableContextEditing = true,
                ContextEditingTriggerTokens = 50_000,
                ContextEditingKeepToolUses = 5,
            },
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "Anthropic");

        Assert.True(result.EnablePromptCaching);
        Assert.Equal(3600, result.CacheTtlSeconds);
        Assert.Equal(1024, result.MinCacheableTokens);
        Assert.True(result.EnableContextEditing);
        Assert.Equal(50_000, result.ContextEditingTriggerTokens);
        Assert.Equal(5, result.ContextEditingKeepToolUses);
    }

    [Fact]
    public void Resolve_Anthropic_FallsBackToGlobalsWhenFamilySectionAbsent()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = false,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "Anthropic");

        Assert.False(result.EnablePromptCaching);
        Assert.Equal(300, result.CacheTtlSeconds);
        Assert.Equal(1024, result.MinCacheableTokens);
        Assert.Null(result.EnableContextEditing);
        Assert.Null(result.ContextEditingTriggerTokens);
        Assert.Null(result.ContextEditingKeepToolUses);
    }

    [Fact]
    public void Resolve_DeepSeek_UsesOwnFlagButGlobalCacheTuning()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = false,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
            DeepSeek = new DeepSeekAgentConfig { EnablePromptCaching = true },
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "DeepSeek");

        Assert.True(result.EnablePromptCaching);
        Assert.Equal(300, result.CacheTtlSeconds);
        Assert.Equal(1024, result.MinCacheableTokens);
    }

    [Fact]
    public void Resolve_DeepSeek_FallsBackToGlobalEnableFlagWhenSectionAbsent()
    {
        AgentBehaviorConfig config = new() { EnablePromptCaching = true };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "DeepSeek");

        Assert.True(result.EnablePromptCaching);
    }

    [Fact]
    public void Resolve_Qwen_UsesFamilyOverridesOverGlobalDefaults()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
            Qwen = new QwenAgentConfig { CacheTtlSeconds = 600, MinCacheableTokens = 2048 },
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "Qwen");

        Assert.True(result.EnablePromptCaching);
        Assert.Equal(600, result.CacheTtlSeconds);
        Assert.Equal(2048, result.MinCacheableTokens);
    }

    [Fact]
    public void Resolve_Qwen_FallsBackToGlobalsWhenSectionAbsent()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "Qwen");

        Assert.True(result.EnablePromptCaching);
        Assert.Equal(300, result.CacheTtlSeconds);
        Assert.Equal(1024, result.MinCacheableTokens);
    }

    [Fact]
    public void Resolve_OpenSource_UsesOwnFieldsAndDoesNotInheritTtlOrMinTokens()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
            OpenSource = new OpenSourceAgentConfig
            {
                EnablePromptCaching = false,
                UseFlashAttention = true,
                KvCacheQuantization = "q8_0",
                NumCtx = 8192,
            },
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "OpenSource");

        Assert.False(result.EnablePromptCaching);
        Assert.True(result.UseFlashAttention);
        Assert.Equal("q8_0", result.KvCacheQuantization);
        Assert.Equal(8192, result.NumCtx);
        Assert.Null(result.CacheTtlSeconds);
        Assert.Null(result.MinCacheableTokens);
    }

    [Fact]
    public void Resolve_OpenSource_FallsBackToGlobalEnableFlagWhenSectionAbsent()
    {
        AgentBehaviorConfig config = new() { EnablePromptCaching = true };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "OpenSource");

        Assert.True(result.EnablePromptCaching);
        Assert.Null(result.UseFlashAttention);
        Assert.Null(result.KvCacheQuantization);
        Assert.Null(result.NumCtx);
    }

    [Fact]
    public void Resolve_UnknownFamily_ExposesOnlyGlobalDefaults()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            CacheTtlSeconds = 300,
            MinCacheableTokens = 1024,
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "Default");

        Assert.True(result.EnablePromptCaching);
        Assert.Equal(300, result.CacheTtlSeconds);
        Assert.Equal(1024, result.MinCacheableTokens);
        Assert.Null(result.EnableContextEditing);
        Assert.Null(result.UseFlashAttention);
    }

    [Fact]
    public void Resolve_UnrecognizedFamilyString_FallsThroughToDefaultBranch()
    {
        AgentBehaviorConfig config = new() { EnablePromptCaching = true };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "SomeUnknownFamily");

        Assert.True(result.EnablePromptCaching);
        Assert.Null(result.EnableContextEditing);
        Assert.Null(result.UseFlashAttention);
    }

    [Fact]
    public void Resolve_FamilyMatchIsCaseSensitive_UnmatchedCaseFallsThroughToDefault()
    {
        AgentBehaviorConfig config = new()
        {
            EnablePromptCaching = true,
            Anthropic = new AnthropicAgentConfig { EnableContextEditing = true },
        };

        EffectiveAgentSettings result = AgentConfigResolver.Resolve(config, "anthropic");

        Assert.True(result.EnablePromptCaching);
        Assert.Null(result.EnableContextEditing);
    }

    [Fact]
    public void Resolve_NullAgentConfig_ReturnsAllNullsForKnownFamily()
    {
        EffectiveAgentSettings result = AgentConfigResolver.Resolve(null, "Anthropic");

        Assert.Null(result.EnablePromptCaching);
        Assert.Null(result.CacheTtlSeconds);
        Assert.Null(result.MinCacheableTokens);
        Assert.Null(result.EnableContextEditing);
        Assert.Null(result.ContextEditingTriggerTokens);
        Assert.Null(result.ContextEditingKeepToolUses);
    }

    [Fact]
    public void Resolve_NullAgentConfig_ReturnsAllNullsForDefaultFamily()
    {
        EffectiveAgentSettings result = AgentConfigResolver.Resolve(null, "Default");

        Assert.Null(result.EnablePromptCaching);
        Assert.Null(result.CacheTtlSeconds);
        Assert.Null(result.MinCacheableTokens);
    }
}

[Collection("GlobalConfigDirEnvVar")]
public sealed class AgentConfigResolverEndToEndTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private readonly string _workspaceRoot;
    private readonly string _globalDir;
    private readonly BehaviorConfigService _behaviorConfig;

    public AgentConfigResolverEndToEndTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("acrTests-").FullName;
        // BehaviorConfigService.GlobalConfigPath otherwise resolves to the real machine-wide
        // %ProgramData%\TokenSaver\config.json, which WorkspaceConfigDefaults now populates with
        // real default overrides — redirect to an empty temp dir so "no config file on disk"
        // tests stay true regardless of what's on the real machine.
        _globalDir = Directory.CreateTempSubdirectory("acrTests-global-").FullName;
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

    private static AgentAdapterRegistry CreateRegistry(IEnumerable<IAgentAdapter> adapters) =>
        new(adapters, NullLogger<AgentAdapterRegistry>.Instance);

    [Fact]
    public void Resolve_WithHttpContext_DetectsCallerAndMergesFamilyConfig()
    {
        string localPath = BehaviorConfigService.LocalConfigPath(_workspaceRoot);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, """
            {
              "agentConfig": {
                "enablePromptCaching": true,
                "cacheTtlSeconds": 300,
                "anthropic": { "cacheTtlSeconds": 3600, "enableContextEditing": true }
              }
            }
            """);

        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "claude-code" });
        AgentAdapterRegistry registry = CreateRegistry([new FakeModelAdapter("Anthropic")]);
        AgentConfigResolver resolver = new(detection, registry, _behaviorConfig);

        DefaultHttpContext httpContext = new();
        AgentConfigResolution result = resolver.Resolve(httpContext, _workspaceRoot);

        Assert.Equal("Anthropic", result.Family);
        Assert.Equal("claude-code", result.Caller.ClientName);
        Assert.True(result.Settings.EnablePromptCaching);
        Assert.Equal(3600, result.Settings.CacheTtlSeconds);
        Assert.True(result.Settings.EnableContextEditing);
    }

    [Fact]
    public void Resolve_WithNullHttpContext_UsesDefaultCallerInfo()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "should-not-be-used" });
        AgentAdapterRegistry registry = CreateRegistry([]);
        AgentConfigResolver resolver = new(detection, registry, _behaviorConfig);

        AgentConfigResolution result = resolver.Resolve(null, _workspaceRoot);

        Assert.Equal("", result.Caller.ClientName);
        Assert.False(detection.WasCalled);
        Assert.Equal("Default", result.Family);
    }

    [Fact]
    public void Resolve_NoMatchingAdapter_FallsBackToDefaultFamily()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "unknown-client" });
        AgentAdapterRegistry registry = CreateRegistry([]);
        AgentConfigResolver resolver = new(detection, registry, _behaviorConfig);

        AgentConfigResolution result = resolver.Resolve(new DefaultHttpContext(), _workspaceRoot);

        Assert.Equal("Default", result.Family);
    }

    [Fact]
    public void Resolve_WithNoConfigFileOnDisk_ReturnsNullSettings()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "claude-code" });
        AgentAdapterRegistry registry = CreateRegistry([new FakeModelAdapter("Anthropic")]);
        AgentConfigResolver resolver = new(detection, registry, _behaviorConfig);

        AgentConfigResolution result = resolver.Resolve(new DefaultHttpContext(), _workspaceRoot);

        Assert.Equal("Anthropic", result.Family);
        Assert.Null(result.Settings.EnablePromptCaching);
        Assert.Null(result.Settings.EnableContextEditing);
    }

    private sealed class FakeCallerDetectionService(CallerInfo toReturn) : ICallerDetectionService
    {
        public bool WasCalled { get; private set; }

        public CallerInfo Detect(HttpContext httpContext, string? mcpClientName = null, string? mcpClientVersion = null)
        {
            WasCalled = true;
            return toReturn;
        }
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
}
