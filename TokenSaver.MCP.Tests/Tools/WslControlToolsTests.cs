using AiDevTools.TokenSaver.MCP.Tools;
using AiDevTools.TokenSaver.WslControl.Contracts;
using AiDevTools.TokenSaver.WslControl.Models;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Exercises WslControlTools through hand-written fakes for the WSL contracts. No WSL process
///     is ever started: every IWslProvider call is answered from fields set by each test.
/// </summary>
public sealed class WslControlToolsTests
{
    private static WslControlTools CreateTools(FakeWslProvider? provider = null, FakePlanner? planner = null, FakeDebugConfig? debug = null)
    {
        return new WslControlTools(
            provider ?? new FakeWslProvider(),
            planner ?? new FakePlanner(),
            debug ?? new FakeDebugConfig());
    }

    [Fact]
    public async Task WslStatus_NotInstalled_ReturnsUnavailableWithInstallUrl()
    {
        FakeWslProvider provider = new()
        {
            Availability = new WslAvailability(IsInstalled: false, Reason: "wsl.exe not found", InstallUrl: WslAvailability.DefaultInstallUrl),
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslStatus();

        Assert.StartsWith("WSL is not available: wsl.exe not found", result, StringComparison.Ordinal);
        Assert.Contains(WslAvailability.DefaultInstallUrl.ToString(), result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslStatus_Installed_ListsDistrosAndMarksDefault()
    {
        FakeWslProvider provider = new()
        {
            Availability = new WslAvailability(IsInstalled: true, DefaultDistro: "Ubuntu", DistroCount: 2),
            Distros =
            [
                new WslDistroInfo("Ubuntu", IsDefault: true, WslVersion: 2),
                new WslDistroInfo("Debian", IsDefault: false, WslVersion: 2),
            ],
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslStatus();

        Assert.Contains("WSL is installed — 2 distribution(s), default: Ubuntu.", result, StringComparison.Ordinal);
        Assert.Contains("* Ubuntu (WSL 2)", result, StringComparison.Ordinal);
        Assert.Contains("Debian (WSL 2)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslStatus_InstalledWithNoDistros_SuggestsInstall()
    {
        FakeWslProvider provider = new()
        {
            Availability = new WslAvailability(IsInstalled: true),
            Distros = [],
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslStatus();

        Assert.Contains("No distributions registered", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslHealth_HealthyDistro_ReportsSudoAndApt()
    {
        FakeWslProvider provider = new()
        {
            Health = new WslHealthReport("Ubuntu", IsHealthy: true, OsDescription: "Ubuntu 24.04", HasSudo: true, HasApt: true),
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslHealth("Ubuntu");

        Assert.StartsWith("Ubuntu: healthy", result, StringComparison.Ordinal);
        Assert.Contains("os:     Ubuntu 24.04", result, StringComparison.Ordinal);
        Assert.Contains("sudo:   available (passwordless)", result, StringComparison.Ordinal);
        Assert.Contains("apt:    available", result, StringComparison.Ordinal);
        Assert.Equal("Ubuntu", provider.LastHealthDistro);
    }

    [Fact]
    public async Task WslDo_MutatingPlanWithoutConfirm_ReturnsPreviewAndDoesNotRun()
    {
        FakeWslProvider provider = new();
        FakePlanner planner = new()
        {
            Plan = new WslActionPlan(WslActionKind.AptInstall, "sudo apt-get install -y ripgrep", ["ripgrep"], "Install ripgrep", RequiresConfirmation: true),
        };
        WslControlTools tools = CreateTools(provider, planner);

        string result = await tools.WslDo("install ripgrep", confirm: false);

        Assert.Contains("Plan (AptInstall): Install ripgrep", result, StringComparison.Ordinal);
        Assert.Contains("confirm=true", result, StringComparison.Ordinal);
        Assert.Equal(0, provider.AptInstallCalls);
        Assert.Equal(0, provider.RunBashCalls);
    }

    [Fact]
    public async Task WslDo_UnknownPlan_ReturnsCouldNotTranslate()
    {
        FakePlanner planner = new() { Plan = WslActionPlan.UnknownPlan("ambiguous request") };
        WslControlTools tools = CreateTools(planner: planner);

        string result = await tools.WslDo("do something vague");

        Assert.StartsWith("Could not translate the request into a command.", result, StringComparison.Ordinal);
        Assert.Contains("ambiguous request", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslDo_ReadOnlyPlan_RunsCommandWithoutConfirm()
    {
        FakeWslProvider provider = new()
        {
            BashResult = new WslCommandResult("Ubuntu", "df -h", ExitCode: 0, Stdout: "disk ok"),
        };
        FakePlanner planner = new()
        {
            Plan = new WslActionPlan(WslActionKind.RunCommand, "df -h", [], "Show disk usage", RequiresConfirmation: false),
        };
        WslControlTools tools = CreateTools(provider, planner);

        string result = await tools.WslDo("show disk usage");

        Assert.Equal("df -h", provider.LastBashCommandLine);
        Assert.Contains("disk ok", result, StringComparison.Ordinal);
        Assert.Contains("exit 0", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslDo_AptInstallConfirmed_CallsAptInstall()
    {
        FakeWslProvider provider = new()
        {
            AptResult = new WslCommandResult("Ubuntu", "apt-get install", ExitCode: 0, Stdout: "installed"),
        };
        FakePlanner planner = new()
        {
            Plan = new WslActionPlan(WslActionKind.AptInstall, "sudo apt-get install -y ripgrep", ["ripgrep"], "Install ripgrep", RequiresConfirmation: true),
        };
        WslControlTools tools = CreateTools(provider, planner);

        string result = await tools.WslDo("install ripgrep", confirm: true);

        Assert.Equal(1, provider.AptInstallCalls);
        Assert.Equal(["ripgrep"], provider.LastAptPackages);
        Assert.Contains("installed", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslRun_NonZeroExit_ReportsExitCodeAndStderr()
    {
        FakeWslProvider provider = new()
        {
            BashResult = new WslCommandResult("Ubuntu", "false", ExitCode: 1, Stderr: "boom", TimedOut: false),
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslRun("false");

        Assert.Contains("exit 1", result, StringComparison.Ordinal);
        Assert.Contains("stderr:", result, StringComparison.Ordinal);
        Assert.Contains("boom", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslRun_ExecutionError_ReportsFailureToRun()
    {
        FakeWslProvider provider = new()
        {
            BashResult = new WslCommandResult("Ubuntu", "ls", ExitCode: -1, ExecutionError: "distro not found"),
        };
        WslControlTools tools = CreateTools(provider);

        string result = await tools.WslRun("ls", "Ubuntu");

        Assert.Contains("failed to run: distro not found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslDebugSetup_UnknownRuntime_ReturnsErrorWithoutPreparing()
    {
        FakeDebugConfig debug = new();
        WslControlTools tools = CreateTools(debug: debug);

        string result = await tools.WslDebugSetup("cobol", "/app/main");

        Assert.Equal("Unknown runtime 'cobol'. Use one of: dotnet, python, node.", result);
        Assert.Equal(0, debug.PrepareCalls);
    }

    [Fact]
    public async Task WslDebugSetup_WriteWithoutWorkspaceRoot_ReturnsError()
    {
        FakeDebugConfig debug = new();
        WslControlTools tools = CreateTools(debug: debug);

        string result = await tools.WslDebugSetup("python", "/app/main.py", write: true, workspaceRoot: "  ");

        Assert.Equal("write=true requires workspaceRoot so .vscode/launch.json can be located.", result);
        Assert.Equal(0, debug.PrepareCalls);
    }

    [Fact]
    public async Task WslDebugSetup_ValidRuntime_ReportsBackendAndLaunchConfig()
    {
        FakeDebugConfig debug = new()
        {
            Result = new WslDebugSetupResult(
                Distro: "Ubuntu",
                Runtime: WslRuntime.Python,
                BackendReady: false,
                BackendDetail: "debugpy not installed",
                LaunchConfigName: "WSL Python",
                LaunchConfigJson: "{ \"name\": \"WSL Python\" }",
                ConfigWritten: false,
                InstallHint: "pip install debugpy"),
        };
        WslControlTools tools = CreateTools(debug: debug);

        string result = await tools.WslDebugSetup("py", "/app/main.py");

        Assert.Contains("WSL debug target: Python in Ubuntu", result, StringComparison.Ordinal);
        Assert.Contains("backend: MISSING", result, StringComparison.Ordinal);
        Assert.Contains("install: pip install debugpy", result, StringComparison.Ordinal);
        Assert.Contains("not written", result, StringComparison.Ordinal);
        Assert.Contains("{ \"name\": \"WSL Python\" }", result, StringComparison.Ordinal);
        Assert.Equal(WslRuntime.Python, debug.LastRuntime);
    }

    private sealed class FakeWslProvider : IWslProvider
    {
        public WslAvailability Availability { get; set; } = new(IsInstalled: true);
        public IReadOnlyList<WslDistroInfo> Distros { get; set; } = [];
        public WslHealthReport Health { get; set; } = new("Ubuntu", IsHealthy: true);
        public WslCommandResult BashResult { get; set; } = new("Ubuntu", "", ExitCode: 0);
        public WslCommandResult AptResult { get; set; } = new("Ubuntu", "", ExitCode: 0);

        public int RunBashCalls { get; private set; }
        public int AptInstallCalls { get; private set; }
        public string? LastBashCommandLine { get; private set; }
        public string? LastHealthDistro { get; private set; }
        public IReadOnlyList<string> LastAptPackages { get; private set; } = [];

        public Task<WslAvailability> CheckAvailabilityAsync(CancellationToken ct = default) => Task.FromResult(Availability);

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken ct = default) => Task.FromResult(Distros);

        public Task<WslDistroInfo?> GetDefaultDistroAsync(CancellationToken ct = default) =>
            Task.FromResult(Distros.FirstOrDefault(d => d.IsDefault));

        public Task<WslHealthReport> CheckHealthAsync(string? distro = null, CancellationToken ct = default)
        {
            LastHealthDistro = distro;
            return Task.FromResult(Health);
        }

        public Task<WslCommandResult> RunBashAsync(string commandLine, string? distro = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            RunBashCalls++;
            LastBashCommandLine = commandLine;
            return Task.FromResult(BashResult);
        }

        public Task<WslCommandResult> AptUpdateAsync(string? distro = null, CancellationToken ct = default) => Task.FromResult(AptResult);

        public Task<WslCommandResult> AptUpgradeAsync(string? distro = null, CancellationToken ct = default) => Task.FromResult(AptResult);

        public Task<WslCommandResult> AptInstallAsync(IReadOnlyList<string> packages, string? distro = null, CancellationToken ct = default)
        {
            AptInstallCalls++;
            LastAptPackages = packages;
            return Task.FromResult(AptResult);
        }

        public Task<WslCommandResult> BuildAsync(string workingDirectory, string buildCommand, string? distro = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Task.FromResult(BashResult);
    }

    private sealed class FakePlanner : IWslActionPlanner
    {
        public WslActionPlan Plan { get; set; } = WslActionPlan.UnknownPlan("not configured");

        public Task<WslActionPlan> PlanAsync(string request, string? distro = null, string? osHint = null, CancellationToken ct = default) =>
            Task.FromResult(Plan);
    }

    private sealed class FakeDebugConfig : IWslDebugConfigService
    {
        public WslDebugSetupResult? Result { get; set; }
        public int PrepareCalls { get; private set; }
        public WslRuntime? LastRuntime { get; private set; }

        public Task<WslDebugSetupResult> PrepareDebugTargetAsync(
            WslRuntime runtime,
            string programPath,
            string? distro = null,
            string? workspaceRoot = null,
            bool writeToWorkspace = false,
            CancellationToken ct = default)
        {
            PrepareCalls++;
            LastRuntime = runtime;
            return Task.FromResult(Result ?? new WslDebugSetupResult(distro ?? "Ubuntu", runtime, true, "ok", "cfg", "{}", writeToWorkspace));
        }
    }
}
