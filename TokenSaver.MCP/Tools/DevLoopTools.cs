using TokenSaver.Common.Core.Configuration;
using TokenSaver.Common.Core.Processes;
using TokenSaver.Common.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Dev-loop shell-out tools whose raw output is usually pure noise: container logs, test
///     runs, lint, build-log tailing, git diffs. Every one of these follows the same shape: run
///     the thing, retain the raw output on disk (RawArtifactWriter), distill it with a
///     role-specific prompt (DistillationService), return the compact result plus a pointer to
///     the full artifact. Windows/Docker-Desktop oriented (ContainerLogs shells out to the
///     `docker` CLI rather than depending on a Docker SDK package) — see
///     TokenSaver/docs/token-economy-tools-contract.md for per-tool degraded behavior when the
///     underlying command isn't available.
/// </summary>
[McpServerToolType]
internal sealed class DevLoopTools(
    IDistillationService distill,
    RawArtifactWriter rawWriter,
    IOptions<TokenSaverConfig> config,
    WorkspaceRootSessionResolver workspaceRootResolver)
{
    private readonly TokenSaverConfig _config = config.Value;

    /// <summary>Docker container names: letters, digits, '_', '.', '-'. Keeps caller input from adding docker arguments.</summary>
    private static readonly Regex ContainerNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant);

    /// <summary>docker inspect format: state|health|exitCode|finishedAt.</summary>
    private const string InspectFormat =
        "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}|{{.State.ExitCode}}|{{.State.FinishedAt}}";

    [McpServerTool]
    [Description(
        "Check Docker is installed, the daemon is running, and the container's status and health, then fetch " +
        "its recent logs distilled to new/anomalous error lines only. Each failed check returns a clear " +
        "message instead of logs.")]
    public async Task<string> ContainerLogs(
        [Description("Container name or ID")] string containerName,
        [Description("Number of trailing log lines to fetch (default 500)")]
        int tailLines = 500,
        [Description("Return raw untruncated output instead of a distilled summary")]
        bool verbose = false,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        (string? error, string header) = await DockerPreflightAsync(containerName, ct);
        if (error is not null)
        {
            return error;
        }

        // Logs are fetched even when the container is stopped, since that is when they matter most.
        string raw;
        try
        {
            raw = await RunProcessOutputOnlyAsync("docker", $"logs --tail {tailLines} {containerName}", ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return header + $"docker logs failed: {ex.Message}";
        }

        string? rawPath = await rawWriter.WriteAsync("containerLogs", raw, ct);

        if (verbose)
        {
            return header + raw;
        }

        DistillResult result = await distill.DistillAsync(
            _config.WorkspaceRoot, "containerLogs", DistillRole.LogErrors, raw, ct: ct);

        string tail = rawPath is not null ? $"\n\n[{result.RawTokens}→{result.DistilledTokens} tokens; full log: {rawPath}]" : "";
        return header + result.Summary + tail;
    }

    [McpServerTool]
    [Description("Start a stopped Docker container, then report its status and health.")]
    public Task<string> ContainerStart(
        [Description("Container name or ID")] string containerName,
        McpServer server = null!,
        CancellationToken ct = default)
        => RunContainerActionAsync("start", "started", containerName, server, ct);

    [McpServerTool]
    [Description("Stop a running Docker container (Docker's default 10 s grace period), then report its status.")]
    public Task<string> ContainerStop(
        [Description("Container name or ID")] string containerName,
        McpServer server = null!,
        CancellationToken ct = default)
        => RunContainerActionAsync("stop", "stopped", containerName, server, ct);

    [McpServerTool]
    [Description("Restart a Docker container, then report its status and health.")]
    public Task<string> ContainerRestart(
        [Description("Container name or ID")] string containerName,
        McpServer server = null!,
        CancellationToken ct = default)
        => RunContainerActionAsync("restart", "restarted", containerName, server, ct);

    [McpServerTool]
    [Description(
        "Run one shell command inside a container (via /bin/sh -c) and return its exit code, stdout and stderr. " +
        "One-shot only: there is no interactive console. A timeout kills the docker client; the command itself may " +
        "keep running inside the container.")]
    public async Task<string> ContainerExec(
        [Description("Container name or ID")] string containerName,
        [Description("Shell command to run inside the container, passed to /bin/sh -c")] string command,
        [Description("Seconds before the docker client is killed (1-600, default 60)")]
        int timeoutSeconds = 60,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        (string? error, _) = await DockerPreflightAsync(containerName, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return "command is empty.";
        }

        if (timeoutSeconds is < 1 or > 600)
        {
            return "timeoutSeconds must be between 1 and 600.";
        }

        (int exitCode, string stdout, string stderr, bool timedOut) =
            await RunDockerExecAsync(containerName, command, TimeSpan.FromSeconds(timeoutSeconds), ct);

        if (timedOut)
        {
            return $"Timed out after {timeoutSeconds} s; the docker client was killed (the command may still run inside the container).";
        }

        return $"exit={exitCode}\n--- stdout\n{Capped(stdout)}\n--- stderr\n{Capped(stderr)}";
    }

    /// <summary>Caps one output stream so a chatty command can't flood the caller's context.</summary>
    private static string Capped(string text)
    {
        const int maxChars = 20_000;
        return text.Length <= maxChars ? text : text[..maxChars] + $"\n[truncated: {text.Length - maxChars} more characters]";
    }

    /// <summary>
    ///     Runs `docker exec &lt;container&gt; /bin/sh -c &lt;command&gt;` with arguments passed as a list (no shell
    ///     quoting of the caller's text). Kills the docker client on timeout or cancellation.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunDockerExecAsync(
        string containerName, string command, TimeSpan timeout, CancellationToken ct)
    {
        CommandResult result = await CommandRunner.RunAsync(
            Request("docker", ["exec", containerName, "/bin/sh", "-c", command], null, timeout), ct);
        if (!result.Launched)
        {
            throw new Win32Exception(result.LaunchError);
        }

        return (result.TimedOut ? -1 : result.ExitCode, result.StdOut, result.StdErr, result.TimedOut);
    }

    /// <summary>Runs a docker lifecycle verb on a container after the same preflight as container_logs.</summary>
    private async Task<string> RunContainerActionAsync(
        string verb, string pastTense, string containerName, McpServer server, CancellationToken ct)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        (string? error, _) = await DockerPreflightAsync(containerName, ct);
        if (error is not null)
        {
            return error;
        }

        (int exitCode, _, string stderr) = await RunProcessAsync("docker", $"{verb} {containerName}", ct);
        if (exitCode != 0)
        {
            return $"docker {verb} {containerName} failed (exit {exitCode}): {stderr.Trim()}";
        }

        (string? afterError, string header) = await DockerPreflightAsync(containerName, ct);
        return afterError ?? $"Container '{containerName}' {pastTense}.\n{header}";
    }

    /// <summary>
    ///     Checks the name is a valid docker container name, the docker CLI is installed, the daemon is
    ///     running, and the container exists. Returns an error to hand back to the caller, or the status header.
    /// </summary>
    private static async Task<(string? Error, string Header)> DockerPreflightAsync(string containerName, CancellationToken ct)
    {
        if (!ContainerNamePattern.IsMatch(containerName))
        {
            return ($"Invalid container name '{containerName}': use letters, digits, '_', '.' and '-' only.", "");
        }

        // 1. Is the docker CLI installed?
        try
        {
            (int cliExit, _, string cliErr) = await RunProcessAsync("docker", "--version", ct);
            if (cliExit != 0)
            {
                return ($"Docker CLI is installed but failed (exit {cliExit}): {cliErr.Trim()}", "");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ($"Docker is not installed or not on PATH: {ex.Message}", "");
        }

        // 2. Is the daemon (Docker Desktop) running?
        (int infoExit, _, string infoErr) = await RunProcessAsync("docker", "info --format \"{{.ServerVersion}}\"", ct);
        if (infoExit != 0)
        {
            return ($"Docker is installed but the daemon is not running (start Docker Desktop): {infoErr.Trim()}", "");
        }

        // 3. Container state and health.
        (int inspectExit, string inspectOut, string inspectErr) =
            await RunProcessAsync("docker", $"inspect -f \"{InspectFormat}\" {containerName}", ct);
        if (inspectExit != 0)
        {
            return ($"Container '{containerName}' not found: {inspectErr.Trim()}", "");
        }

        string[] state = inspectOut.Trim().Split('|');
        string header = state.Length == 4
            ? $"Docker: installed, daemon running.\nContainer '{containerName}': status={state[0]}, health={state[1]}, exitCode={state[2]}, finished={state[3]}\n\n"
            : $"Docker: installed, daemon running.\nContainer '{containerName}': {inspectOut.Trim()}\n\n";

        return (null, header);
    }

    [McpServerTool]
    [Description(
        "Summarize a git diff as a commit message (one-line summary + up to 4 bullets) instead of returning the " +
        "raw diff text.")]
    public async Task<string> DiffSummarize(
        [Description("Path to the git repository")] string repoPath,
        [Description("Diff staged changes only instead of the full working-tree diff")]
        bool stagedOnly = false,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        PathCheck repo = WorkspacePathGuard.Resolve(repoPath, _config.WorkspaceRoot);
        if (!repo.Ok)
        {
            return repo.Error!;
        }

        string args = stagedOnly ? "diff --staged" : "diff";
        string diff;
        try
        {
            diff = await RunProcessInAsync(repo.FullPath, "git", args, ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"git CLI unavailable or '{repoPath}' is not a git repository: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            return "No changes.";
        }

        DistillResult result = await distill.DistillAsync(_config.WorkspaceRoot, "diffSummarize", DistillRole.DiffSummary, diff, ct: ct);
        return $"{result.Summary}\n\n[{result.RawTokens}→{result.DistilledTokens} tokens]";
    }

    [McpServerTool]
    [Description(
        "Run the project's test suite. Returns \"PASS\" on success, or a distilled failure summary (which tests " +
        "failed, one-line reason each, most likely fix location) instead of the raw 10-30k-token stack-trace dump " +
        "a real failure produces.")]
    public async Task<string> RunTests(
        [Description("Project directory to run the test command in")]
        string projectPath,
        [Description("Test command to run; defaults to 'dotnet test' for this codebase's own convention")]
        string? testCommand = null,
        [Description("Return raw untruncated output on failure instead of a distilled summary")]
        bool verbose = false,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        (CommandCheck cmd, string workDir) = CheckCommand(testCommand ?? "dotnet test", projectPath);
        if (!cmd.Ok)
        {
            return cmd.Error!;
        }

        (string command, string args) = (cmd.Command, cmd.Arguments);
        (int exitCode, string stdout, string stderr) run;
        try
        {
            run = await RunProcessAsync(command, args, ct, workDir);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"Could not run '{command}': {ex.Message}";
        }

        (int exitCode, string stdout, string stderr) = run;
        string raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        if (exitCode == 0)
        {
            return "PASS (all tests)";
        }

        string? rawPath = await rawWriter.WriteAsync("runTests", raw, ct);
        if (verbose)
        {
            return raw;
        }

        DistillResult result = await distill.DistillAsync(_config.WorkspaceRoot, "runTests", DistillRole.TestFailure, raw, ct: ct);
        string tail = rawPath is not null ? $"\n\n[{result.RawTokens}→{result.DistilledTokens} tokens; full: {rawPath}]" : "";
        return $"FAIL\n{result.Summary}{tail}";
    }

    [McpServerTool]
    [Description(
        "Run a linter and distill its output grouped by rule (rule name, count, one representative file:line) " +
        "instead of dumping every line. Returns \"Clean\" if nothing remains after the run.")]
    public async Task<string> LintFix(
        [Description("Project directory to run the lint command in")]
        string projectPath,
        [Description("Lint command to run; defaults to 'dotnet format --verify-no-changes' for this codebase's own convention")]
        string? lintCommand = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        (CommandCheck cmd, string workDir) = CheckCommand(lintCommand ?? "dotnet format --verify-no-changes", projectPath);
        if (!cmd.Ok)
        {
            return cmd.Error!;
        }

        (string command, string args) = (cmd.Command, cmd.Arguments);
        (int exitCode, string stdout, string stderr) run;
        try
        {
            run = await RunProcessAsync(command, args, ct, workDir);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"Could not run '{command}': {ex.Message}";
        }

        (int exitCode, string stdout, string stderr) = run;
        string raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        if (exitCode == 0 && string.IsNullOrWhiteSpace(raw))
        {
            return "Clean — no remaining lint issues.";
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"Lint command failed (exit {exitCode}) with no output.";
        }

        DistillResult result = await distill.DistillAsync(_config.WorkspaceRoot, "lintFix", DistillRole.LintSummary, raw, ct: ct);
        if (result.DistilledTokens >= result.RawTokens)
        {
            // The model did not shrink the output; returning its text would only add tokens and risk invented detail.
            string capped = raw.Length > LintRawCapChars ? raw[..LintRawCapChars] + "\n[truncated]" : raw;
            return $"exit {exitCode}\n{capped}";
        }

        return $"exit {exitCode}\n{result.Summary}\n\n[{result.RawTokens}→{result.DistilledTokens} tokens]";
    }

    private const int LintRawCapChars = 8000;

    [McpServerTool]
    [Description(
        "Read new lines appended to a build log since a given line number and distill them to new/anomalous " +
        "errors only (skipped entirely — returned as-is — below a small size threshold, since distilling a " +
        "handful of lines isn't worth the round trip). Track last_line from the response and pass it back in as " +
        "sinceLine on the next call to only see what's new.")]
    public async Task<string> BuildWatch(
        [Description("Path to the build log file")] string logPath,
        [Description("Only return lines after this line number (0 = whole file)")]
        int sinceLine = 0,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await workspaceRootResolver.EnsureResolvedAsync(server, ct);

        PathCheck log = WorkspacePathGuard.Resolve(logPath, _config.WorkspaceRoot);
        if (!log.Ok)
        {
            return log.Error!;
        }

        string content = await File.ReadAllTextAsync(log.FullPath, ct);
        string[] lines = content.Split('\n');
        string[] newLines = sinceLine < lines.Length ? lines[sinceLine..] : [];

        if (newLines.Length == 0)
        {
            return JsonSerializer.Serialize(new { new_errors = Array.Empty<string>(), last_line = lines.Length });
        }

        string raw = string.Join('\n', newLines);
        string summary = raw.Length > 800
            ? (await distill.DistillAsync(_config.WorkspaceRoot, "buildWatch", DistillRole.LogErrors, CollapseBuildWatchOutput(raw), ct: ct)).Summary
            : raw;

        return JsonSerializer.Serialize(new { summary, last_line = lines.Length });
    }

    // Collapse exact repeated lines and put error-like lines first before the distiller sees large build output.
    private static string CollapseBuildWatchOutput(string raw)
    {
        var groups = raw.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .GroupBy(line => line, StringComparer.Ordinal)
            .OrderByDescending(group => IsErrorLine(group.Key))
            .ThenByDescending(group => group.Count())
            .Take(80);

        return string.Join('\n', groups.Select(group => group.Count() == 1
            ? group.Key
            : $"{group.Key} (repeated {group.Count()} times)"));
    }

    private static bool IsErrorLine(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
        || line.Contains("warning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Confines the working directory to the workspace and the executable to the configured allowlist before
    ///     anything is launched.
    /// </summary>
    private (CommandCheck Check, string WorkDir) CheckCommand(string commandLine, string projectPath)
    {
        PathCheck dir = WorkspacePathGuard.Resolve(projectPath, _config.WorkspaceRoot);
        return dir.Ok
            ? (CommandAllowlist.Check(commandLine, _config.AllowedCommands), dir.FullPath)
            : (new CommandCheck(false, "", "", dir.Error), "");
    }

    private static Task<string> RunProcessInAsync(string workingDirectory, string fileName, string arguments, CancellationToken ct)
    {
        return RunProcessOutputOnlyAsync(fileName, arguments, ct, workingDirectory);
    }

    /// <summary>Runs an external process to completion and returns combined stdout (stderr appended on non-zero exit). Used by tools that don't need the exit code separately (git diff, docker logs).</summary>
    private static async Task<string> RunProcessOutputOnlyAsync(string fileName, string arguments, CancellationToken ct, string? workingDirectory = null)
    {
        (int exitCode, string stdout, string stderr) = await RunProcessAsync(fileName, arguments, ct, workingDirectory);
        if (exitCode != 0)
        {
            return string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}";
        }

        return stdout;
    }

    /// <summary>
    ///     Runs an external process to completion through the shared <see cref="CommandRunner" />: arguments are split
    ///     (never shell-parsed), both pipes drain concurrently, the tree is killed on timeout or cancellation, and the
    ///     process is tracked in the registry. Launch failures surface as <see cref="Win32Exception" /> (the callers'
    ///     "could not run" path) and a timeout as <see cref="TimeoutException" />.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct, string? workingDirectory = null)
    {
        CommandResult result = await CommandRunner.RunAsync(
            Request(fileName, CommandLine.Split(arguments), workingDirectory, ProcessTimeout), ct);
        if (!result.Launched)
        {
            throw new Win32Exception(result.LaunchError);
        }

        if (result.TimedOut)
        {
            throw new TimeoutException($"'{fileName}' did not finish within {ProcessTimeout.TotalMinutes:0} minutes and was killed.");
        }

        return (result.ExitCode, result.StdOut, result.StdErr);
    }

    private static CommandRequest Request(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
    {
        return new CommandRequest
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            OnStarted = process => ManagedProcessRegistry.Track(process, "dev command"),
        };
    }

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);
}
