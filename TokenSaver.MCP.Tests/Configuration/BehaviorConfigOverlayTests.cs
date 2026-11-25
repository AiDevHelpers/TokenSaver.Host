using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.Config.Contracts;
using AiDevTools.TokenSaver.ContextHelper.Configuration;
using AiDevTools.TokenSaver.Common.Core.Configuration;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

[Collection("GlobalConfigDirEnvVar")]
public sealed class BehaviorConfigOverlayTests : IDisposable
{
    private const string GlobalDirOverrideEnvVar = "TOKENSAVER_GLOBAL_CONFIG_DIR";

    private readonly string _workspaceRoot;
    private readonly string _globalDir;
    private readonly BehaviorConfigService _service;

    public BehaviorConfigOverlayTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("bcoTests-").FullName;
        // BehaviorConfigService.GlobalConfigPath otherwise resolves to the real machine-wide
        // %ProgramData%\TokenSaver\config.json, which WorkspaceConfigDefaults now populates with
        // real default overrides — redirect to an empty temp dir so these "no overrides" tests
        // stay true regardless of what's on the real machine.
        _globalDir = Directory.CreateTempSubdirectory("bcoTests-global-").FullName;
        Environment.SetEnvironmentVariable(GlobalDirOverrideEnvVar, _globalDir);
        _service = new BehaviorConfigService(NullLogger<BehaviorConfigService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
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
    public void Apply_WithNoOverrides_KeepsAppsettingsDefaults()
    {
        CodeHelperConfig codeHelper = new() { AutoEnabled = false, RequireAnchorMatch = true, WasteThreshold = 0.40 };
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot };

        BehaviorConfigOverlay overlay = new(
            _service,
            Options.Create(codeHelper),
            Options.Create(tokenSaver),
            Options.Create(new ContextHelperConfig()));
        overlay.Apply();

        Assert.False(codeHelper.AutoEnabled);
        Assert.True(codeHelper.RequireAnchorMatch);
        Assert.Equal(0.40, codeHelper.WasteThreshold);
    }

    [Fact]
    public void Apply_WithLocalOverride_MutatesSharedInstanceInPlace()
    {
        string localPath = BehaviorConfigService.LocalConfigPath(_workspaceRoot);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, """
            {
              "codeHelper": { "autoEnabled": true, "calibration": { "wasteThreshold": 0.75 } },
              "tokenEconomy": { "allowTerse": false }
            }
            """);

        CodeHelperConfig codeHelper = new() { AutoEnabled = false, WasteThreshold = 0.40 };
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot };
        tokenSaver.TokenEconomy.EnableTerse = true;

        BehaviorConfigOverlay overlay = new(
            _service,
            Options.Create(codeHelper),
            Options.Create(tokenSaver),
            Options.Create(new ContextHelperConfig()));
        overlay.Apply();

        Assert.True(codeHelper.AutoEnabled);
        Assert.Equal(0.75, codeHelper.WasteThreshold);
        Assert.False(tokenSaver.TokenEconomy.EnableTerse);
    }

    [Fact]
    public void Apply_AfterOverrideRemoved_RevertsToBaseline()
    {
        // Go through BehaviorConfigService.Set (not a raw file write) so the cache/ConfigChanged
        // update happens synchronously instead of depending on FileSystemWatcher timing.
        _service.Update(_workspaceRoot, ConfigScope.Local,
            c => c with { CodeHelper = (c.CodeHelper ?? new CodeHelperBehaviorConfig()) with { RequireAnchorMatch = false } });

        CodeHelperConfig codeHelper = new() { RequireAnchorMatch = true };
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot };

        BehaviorConfigOverlay overlay = new(
            _service,
            Options.Create(codeHelper),
            Options.Create(tokenSaver),
            Options.Create(new ContextHelperConfig()));
        overlay.Apply();
        Assert.False(codeHelper.RequireAnchorMatch);

        _service.Update(_workspaceRoot, ConfigScope.Local,
            c => c with { CodeHelper = (c.CodeHelper ?? new CodeHelperBehaviorConfig()) with { RequireAnchorMatch = null } });
        overlay.Apply();
        Assert.True(codeHelper.RequireAnchorMatch);
    }

    [Fact]
    public void Apply_WithContextDebugOverride_MutatesSharedContextHelperConfigInstance()
    {
        string localPath = BehaviorConfigService.LocalConfigPath(_workspaceRoot);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, """
            {
              "context": { "debug": true }
            }
            """);

        CodeHelperConfig codeHelper = new();
        TokenSaverConfig tokenSaver = new() { WorkspaceRoot = _workspaceRoot };
        ContextHelperConfig contextHelper = new() { Debug = false };

        BehaviorConfigOverlay overlay = new(
            _service,
            Options.Create(codeHelper),
            Options.Create(tokenSaver),
            Options.Create(contextHelper));
        overlay.Apply();

        Assert.True(contextHelper.Debug);

        _service.Update(_workspaceRoot, ConfigScope.Local,
            c => c with { Context = (c.Context ?? new ContextBehaviorConfig()) with { Debug = null } });
        overlay.Apply();

        Assert.False(contextHelper.Debug);
    }
}
