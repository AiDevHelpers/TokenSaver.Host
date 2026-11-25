using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class LazyContextResolverStaticResolveTests
{
    [Theory]
    [InlineData("cursor")]
    [InlineData("Cursor")]
    [InlineData("cursor-ide")]
    [InlineData("CURSOR-IDE")]
    public void Resolve_CursorClientNames_AlwaysTrueViaBuiltInAdapter(string clientName)
    {
        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve(clientName, null);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.BuiltInAdapter, source);
    }

    [Fact]
    public void Resolve_CursorClientName_OverridesConfigOverride()
    {
        LazyContextBehaviorConfig config = new()
        {
            Overrides = new Dictionary<string, bool> { ["cursor"] = false },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("cursor", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.BuiltInAdapter, source);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("github-copilot")]
    [InlineData("vscode-copilot")]
    [InlineData("github.copilot")]
    [InlineData("github copilot")]
    [InlineData("copilot-chat")]
    [InlineData("Copilot")]
    public void Resolve_CopilotClientNames_AlwaysFalseViaBuiltInAdapter(string clientName)
    {
        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve(clientName, null);

        Assert.False(willAsk);
        Assert.Equal(LazyContextSource.BuiltInAdapter, source);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("continue.dev")]
    [InlineData("continuedev")]
    [InlineData("Continue")]
    public void Resolve_ContinueClientNames_AlwaysFalseViaBuiltInAdapter(string clientName)
    {
        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve(clientName, null);

        Assert.False(willAsk);
        Assert.Equal(LazyContextSource.BuiltInAdapter, source);
    }

    [Fact]
    public void Resolve_CopilotClientName_CannotBeOverriddenByConfig()
    {
        LazyContextBehaviorConfig config = new()
        {
            Overrides = new Dictionary<string, bool> { ["copilot"] = true },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("copilot", config);

        Assert.False(willAsk);
        Assert.Equal(LazyContextSource.BuiltInAdapter, source);
    }

    [Fact]
    public void Resolve_ConfigOverridePresent_UsesOverrideValue()
    {
        LazyContextBehaviorConfig config = new()
        {
            DefaultWillAskForMissingContext = false,
            Overrides = new Dictionary<string, bool> { ["claude-code"] = true },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("claude-code", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.ConfigOverride, source);
    }

    [Fact]
    public void Resolve_ConfigOverrideLookup_IsCaseInsensitive()
    {
        LazyContextBehaviorConfig config = new()
        {
            Overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Claude-Code"] = true },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("claude-code", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.ConfigOverride, source);
    }

    [Fact]
    public void Resolve_NoOverrideEntry_FallsBackToWorkspaceDefault()
    {
        LazyContextBehaviorConfig config = new()
        {
            DefaultWillAskForMissingContext = true,
            Overrides = new Dictionary<string, bool> { ["some-other-client"] = false },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("claude-code", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.WorkspaceDefault, source);
    }

    [Fact]
    public void Resolve_NullConfig_FallsBackToHardcodedFalseDefault()
    {
        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("claude-code", null);

        Assert.False(willAsk);
        Assert.Equal(LazyContextSource.WorkspaceDefault, source);
    }

    [Fact]
    public void Resolve_ConfigWithNullDefaultAndNoOverrides_FallsBackToFalse()
    {
        LazyContextBehaviorConfig config = new();

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("unknown-client", config);

        Assert.False(willAsk);
        Assert.Equal(LazyContextSource.WorkspaceDefault, source);
    }

    [Fact]
    public void Resolve_EmptyClientName_SkipsAdapterAndOverrideLookup_UsesWorkspaceDefault()
    {
        LazyContextBehaviorConfig config = new()
        {
            DefaultWillAskForMissingContext = true,
            Overrides = new Dictionary<string, bool> { [""] = false },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.WorkspaceDefault, source);
    }

    [Fact]
    public void Resolve_WhitespaceClientName_IsTreatedAsUnmatchedAndChecksOverrides()
    {
        LazyContextBehaviorConfig config = new()
        {
            DefaultWillAskForMissingContext = false,
            Overrides = new Dictionary<string, bool> { ["   "] = true },
        };

        (bool willAsk, LazyContextSource source) = LazyContextResolver.Resolve("   ", config);

        Assert.True(willAsk);
        Assert.Equal(LazyContextSource.ConfigOverride, source);
    }
}

[Collection("GlobalConfigDirEnvVar")]
public sealed class LazyContextResolverEndToEndTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private readonly string _workspaceRoot;
    private readonly string _globalDir;
    private readonly BehaviorConfigService _behaviorConfig;

    public LazyContextResolverEndToEndTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("lcrTests-").FullName;
        // BehaviorConfigService.GlobalConfigPath otherwise resolves to the real machine-wide
        // %ProgramData%\TokenSaver\config.json, which WorkspaceConfigDefaults now populates with
        // real default overrides — redirect to an empty temp dir so "no config file on disk"
        // tests stay true regardless of what's on the real machine.
        _globalDir = Directory.CreateTempSubdirectory("lcrTests-global-").FullName;
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

    [Fact]
    public void Resolve_WithHttpContext_DetectsCallerAndAppliesConfigOverride()
    {
        string localPath = BehaviorConfigService.LocalConfigPath(_workspaceRoot);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, """
            {
              "lazyContext": {
                "defaultWillAskForMissingContext": false,
                "overrides": { "my-custom-agent": true }
              }
            }
            """);

        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "my-custom-agent" });
        LazyContextResolver resolver = new(detection, _behaviorConfig);

        LazyContextResolution result = resolver.Resolve(new DefaultHttpContext(), _workspaceRoot);

        Assert.True(result.WillAskForMissingContext);
        Assert.Equal(LazyContextSource.ConfigOverride, result.Source);
        Assert.Equal("my-custom-agent", result.Caller.ClientName);
    }

    [Fact]
    public void Resolve_WithNullHttpContext_UsesDefaultCallerInfoAndSkipsDetection()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "should-not-be-used" });
        LazyContextResolver resolver = new(detection, _behaviorConfig);

        LazyContextResolution result = resolver.Resolve((HttpContext?)null, _workspaceRoot);

        Assert.False(detection.WasCalled);
        Assert.Equal("", result.Caller.ClientName);
        Assert.False(result.WillAskForMissingContext);
        Assert.Equal(LazyContextSource.WorkspaceDefault, result.Source);
    }

    [Fact]
    public void Resolve_WithPreDetectedCallerInfo_SkipsDetectionEntirely()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "should-not-be-used" });
        LazyContextResolver resolver = new(detection, _behaviorConfig);

        CallerInfo caller = new() { ClientName = "cursor" };
        LazyContextResolution result = resolver.Resolve(caller, _workspaceRoot);

        Assert.False(detection.WasCalled);
        Assert.True(result.WillAskForMissingContext);
        Assert.Equal(LazyContextSource.BuiltInAdapter, result.Source);
        Assert.Same(caller, result.Caller);
    }

    [Fact]
    public void Resolve_WithNoConfigFileOnDisk_FallsBackToHardcodedFalseDefault()
    {
        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "claude-code" });
        LazyContextResolver resolver = new(detection, _behaviorConfig);

        LazyContextResolution result = resolver.Resolve(new DefaultHttpContext(), _workspaceRoot);

        Assert.False(result.WillAskForMissingContext);
        Assert.Equal(LazyContextSource.WorkspaceDefault, result.Source);
    }

    [Fact]
    public void Resolve_CopilotClientDetected_IgnoresAnyConfigOverride()
    {
        string localPath = BehaviorConfigService.LocalConfigPath(_workspaceRoot);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, """
            {
              "lazyContext": { "overrides": { "copilot": true } }
            }
            """);

        FakeCallerDetectionService detection = new(new CallerInfo { ClientName = "copilot" });
        LazyContextResolver resolver = new(detection, _behaviorConfig);

        LazyContextResolution result = resolver.Resolve(new DefaultHttpContext(), _workspaceRoot);

        Assert.False(result.WillAskForMissingContext);
        Assert.Equal(LazyContextSource.BuiltInAdapter, result.Source);
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
}
