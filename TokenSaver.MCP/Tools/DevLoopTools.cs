using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Library.Models.TokenEconomy;
using TokenSaver.Library.Services;
using TokenSaver.MCP.Services;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

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

    [McpServerTool]
    [Description(
        "Fetch a Docker container's recent logs and distill them to new/anomalous error lines only. " +
        "Requires the `docker` CLI to be on PATH and a running Docker Desktop/daemon — returns a clear " +
        "error message (not raw log noise) if either is unavailable.")]
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

        string raw;
        try
        {
            raw = await RunProcessOutputOnlyAsync("docker", $"logs --tail {tailLines} {containerName}", ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"docker CLI unavailable or container '{containerName}' not found: {ex.Message}";
        }

        string? rawPath = await rawWriter.WriteAsync("containerLogs", raw, ct);

        if (verbose)
        {
            return raw;
        }

        DistillResult result = await distill.DistillAsync(
            _config.WorkspaceRoot, "containerLogs", DistillRole.LogErrors, raw, ct: ct);

        string tail = rawPath is not null ? $"\n\n[{result.RawTokens}→{result.DistilledTokens} tokens; full log: {rawPath}]" : "";
        return result.Summary + tail;
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

        string args = stagedOnly ? "diff --staged" : "diff";
        string diff;
        try
        {
            diff = await RunProcessInAsync(repoPath, "git", args, ct);
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

        (string command, string args) = SplitCommand(testCommand ?? "dotnet test");
        (int exitCode, string stdout, string stderr) = await RunProcessAsync(command, args, ct, projectPath);
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

        (string command, string args) = SplitCommand(lintCommand ?? "dotnet format --verify-no-changes");
        (int exitCode, string stdout, string stderr) = await RunProcessAsync(command, args, ct, projectPath);
        string raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        if (exitCode == 0 && string.IsNullOrWhiteSpace(raw))
        {
            return "Clean — no remaining lint issues.";
        }

        DistillResult result = await distill.DistillAsync(_config.WorkspaceRoot, "lintFix", DistillRole.LintSummary, raw, ct: ct);
        return $"{result.Summary}\n\n[{result.RawTokens}→{result.DistilledTokens} tokens]";
    }

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

        string content = await File.ReadAllTextAsync(logPath, ct);
        string[] lines = content.Split('\n');
        string[] newLines = sinceLine < lines.Length ? lines[sinceLine..] : [];

        if (newLines.Length == 0)
        {
            return JsonSerializer.Serialize(new { new_errors = Array.Empty<string>(), last_line = lines.Length });
        }

        string raw = string.Join('\n', newLines);
        string summary = raw.Length > 800
            ? (await distill.DistillAsync(_config.WorkspaceRoot, "buildWatch", DistillRole.LogErrors, raw, ct: ct)).Summary
            : raw;

        return JsonSerializer.Serialize(new { summary, last_line = lines.Length });
    }

    /// <summary>Splits "dotnet test --filter Foo" into ("dotnet", "test --filter Foo") for ProcessStartInfo.</summary>
    private static (string Command, string Arguments) SplitCommand(string commandLine)
    {
        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (commandLine, "") : (commandLine[..space], commandLine[(space + 1)..]);
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

    /// <summary>Runs an external process to completion, returning its exit code and captured stdout/stderr separately.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct, string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using Process process = new() { StartInfo = startInfo };
        _ = process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }
}
