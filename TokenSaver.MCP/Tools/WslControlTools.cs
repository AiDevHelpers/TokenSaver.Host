using System.ComponentModel;
using System.Globalization;
using System.Text;

using ModelContextProtocol.Server;

using AiDevTools.TokenSaver.WslControl.Contracts;
using AiDevTools.TokenSaver.WslControl.Models;

namespace AiDevTools.TokenSaver.MCP.Tools;

/// <summary>
///     MCP surface for the Windows Subsystem for Linux (WSL). The headline capability is
///     <see cref="WslDo" />: the calling AI describes intent in plain language and the local model
///     synthesizes the exact command — the caller never authors shell syntax. Mutating actions
///     (apt install/update/upgrade, sudo, file writes) are gated behind an explicit
///     <c>confirm</c> flag so they never run without user approval.
/// </summary>
[McpServerToolType]
internal sealed class WslControlTools
{
    private readonly IWslProvider _wsl;
    private readonly IWslActionPlanner _planner;
    private readonly IWslDebugConfigService _debug;

    public WslControlTools(
        IWslProvider wsl,
        IWslActionPlanner planner,
        IWslDebugConfigService debug)
    {
        _wsl = wsl;
        _planner = planner;
        _debug = debug;
    }

    // ── WslStatus ───────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Report whether WSL is installed and list the registered Linux distributions " +
        "(name, WSL version, which is default). Call this first to discover what is available.")]
    public async Task<string> WslStatus(CancellationToken ct = default)
    {
        WslAvailability availability = await _wsl.CheckAvailabilityAsync(ct);
        if (!availability.IsInstalled)
        {
            return $"WSL is not available: {availability.Reason}\nInstall guide: {availability.InstallUrl}";
        }

        IReadOnlyList<WslDistroInfo> distros = await _wsl.ListDistrosAsync(ct);
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"WSL is installed — {distros.Count} distribution(s), default: {availability.DefaultDistro ?? "(none)"}.");

        if (distros.Count == 0)
        {
            _ = sb.AppendLine("No distributions registered. Install one, e.g. `wsl --install -d Ubuntu`.");
        }

        foreach (WslDistroInfo d in distros)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {(d.IsDefault ? "*" : " ")} {d.Name} (WSL {d.WslVersion})");
        }

        return sb.ToString().TrimEnd();
    }

    // ── WslHealth ───────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Check whether a WSL distribution is actually working: OS, kernel, default user, and " +
        "whether sudo and apt are usable. Omit 'distro' to probe the default distribution.")]
    public async Task<string> WslHealth(
        [Description("Distribution name (see WslStatus). Omit for the default distribution.")]
        string? distro = null,
        CancellationToken ct = default)
    {
        WslHealthReport r = await _wsl.CheckHealthAsync(distro, ct);
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{r.Distro}: {(r.IsHealthy ? "healthy" : "NOT healthy")}");
        if (r.OsDescription is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  os:     {r.OsDescription}");
        }

        if (r.Kernel is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  kernel: {r.Kernel}");
        }

        if (r.DefaultUser is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  user:   {r.DefaultUser}");
        }

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  sudo:   {(r.HasSudo ? "available (passwordless)" : "not available non-interactively")}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  apt:    {(r.HasApt ? "available" : "not found")}");
        if (r.Detail is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  detail: {r.Detail}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── WslDo ───────────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Do something in WSL by describing it in plain language — the local model turns your " +
        "request into the exact Linux command; you do NOT write shell syntax. Examples: " +
        "'update the package lists', 'install ripgrep and fd', 'show disk usage'. " +
        "Read-only requests run immediately. Anything that changes the system (installs/removes " +
        "software, uses sudo, writes or deletes files) is NOT run unless you pass confirm=true — " +
        "when confirm is false the tool returns the proposed command for you to show the user first.")]
    public async Task<string> WslDo(
        [Description("Natural-language description of what to do in WSL.")]
        string request,
        [Description("Distribution name (see WslStatus). Omit for the default distribution.")]
        string? distro = null,
        [Description("Set true only after the user approves a system-changing action. Ignored for read-only commands.")]
        bool confirm = false,
        CancellationToken ct = default)
    {
        // Give the planner the distro's OS so it can pick distro-appropriate package names.
        WslHealthReport health = await _wsl.CheckHealthAsync(distro, ct);
        string? osHint = health.OsDescription;

        WslActionPlan plan = await _planner.PlanAsync(request, distro, osHint, ct);

        if (plan.Kind == WslActionKind.Unknown)
        {
            return $"Could not translate the request into a command.\n{plan.Explanation}";
        }

        string preview = BuildPlanPreview(plan);

        // Gate: mutating actions require explicit confirmation.
        if (plan.RequiresConfirmation && !confirm)
        {
            return preview +
                "\n\nThis action changes the system. Show it to the user; if they approve, re-call " +
                "WslDo with the same request and confirm=true.";
        }

        WslCommandResult result = plan.Kind switch
        {
            WslActionKind.AptInstall => await _wsl.AptInstallAsync(plan.Packages, distro, ct),
            WslActionKind.AptUpdate => await _wsl.AptUpdateAsync(distro, ct),
            WslActionKind.AptUpgrade => await _wsl.AptUpgradeAsync(distro, ct),
            _ => await _wsl.RunBashAsync(plan.Command, distro, null, ct),
        };

        return preview + "\n\n" + FormatResult(result);
    }

    // ── WslRun ──────────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Run an explicit bash command line in a WSL distribution and return its output. Use this " +
        "when you already know the exact command; prefer WslDo to have the command synthesized " +
        "for you. The line runs via /bin/bash -c, so pipes and redirection work.")]
    public async Task<string> WslRun(
        [Description("The exact bash command line to run.")]
        string command,
        [Description("Distribution name (see WslStatus). Omit for the default distribution.")]
        string? distro = null,
        CancellationToken ct = default)
    {
        WslCommandResult result = await _wsl.RunBashAsync(command, distro, null, ct);
        return FormatResult(result);
    }

    // ── WslDebugSetup ─────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Prepare a WSL distribution as a debug destination: verify the debugger backend for the " +
        "runtime (dotnet=vsdbg, python=debugpy, node=inspector) is installed in the distro and " +
        "return a VS Code launch.json configuration that runs the program inside WSL. When the " +
        "backend is missing, the result includes an install hint you can pass back to WslDo. " +
        "Set write=true (with workspaceRoot) to create .vscode/launch.json when it does not exist.")]
    public async Task<string> WslDebugSetup(
        [Description("Target runtime: 'dotnet', 'python', or 'node'.")]
        string runtime,
        [Description("Path to the program/artifact to launch, as seen inside WSL (e.g. a Linux path or dll).")]
        string programPath,
        [Description("Distribution name (see WslStatus). Omit for the default distribution.")]
        string? distro = null,
        [Description("Workspace root for writing .vscode/launch.json. Required when write=true.")]
        string? workspaceRoot = null,
        [Description("Create .vscode/launch.json when it does not already exist. Never overwrites an existing file.")]
        bool write = false,
        CancellationToken ct = default)
    {
        if (!TryParseRuntime(runtime, out WslRuntime parsed))
        {
            return $"Unknown runtime '{runtime}'. Use one of: dotnet, python, node.";
        }

        if (write && string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return "write=true requires workspaceRoot so .vscode/launch.json can be located.";
        }

        WslDebugSetupResult r = await _debug.PrepareDebugTargetAsync(
            parsed, programPath, distro, workspaceRoot, write, ct);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"WSL debug target: {r.Runtime} in {r.Distro}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  backend: {(r.BackendReady ? "ready" : "MISSING")} — {r.BackendDetail}");
        if (!r.BackendReady && r.InstallHint is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  install: {r.InstallHint}");
        }

        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"  launch.json: {(r.ConfigWritten ? "written to .vscode/launch.json" : "not written — merge the config below")}");
        _ = sb.AppendLine();
        _ = sb.AppendLine("Launch configuration:");
        _ = sb.AppendLine(r.LaunchConfigJson);

        return sb.ToString().TrimEnd();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool TryParseRuntime(string value, out WslRuntime runtime)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "DOTNET" or ".NET" or "CORECLR" or "CSHARP":
                runtime = WslRuntime.DotNet;
                return true;
            case "PYTHON" or "PY":
                runtime = WslRuntime.Python;
                return true;
            case "NODE" or "NODEJS" or "JAVASCRIPT" or "JS":
                runtime = WslRuntime.Node;
                return true;
            default:
                runtime = WslRuntime.DotNet;
                return false;
        }
    }

    private static string BuildPlanPreview(WslActionPlan plan)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Plan ({plan.Kind}): {plan.Explanation}");
        if (plan.Packages.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  packages: {string.Join(", ", plan.Packages)}");
        }

        if (!string.IsNullOrEmpty(plan.Command))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  command:  {plan.Command}");
        }

        if (plan.Warning is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ {plan.Warning}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatResult(WslCommandResult result)
    {
        StringBuilder sb = new();
        if (result.ExecutionError is not null)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{result.Distro}] failed to run: {result.ExecutionError}");
            return sb.ToString().TrimEnd();
        }

        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"[{result.Distro}] exit {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}");

        if (!string.IsNullOrEmpty(result.Stdout))
        {
            _ = sb.AppendLine("stdout:");
            _ = sb.AppendLine(result.Stdout);
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            _ = sb.AppendLine("stderr:");
            _ = sb.AppendLine(result.Stderr);
        }

        return sb.ToString().TrimEnd();
    }
}
