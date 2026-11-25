using AiDevTools.TokenSaver.Common.Core.Contracts;
using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using AiDevTools.TokenSaver.Common.Core.Processes;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
/// Runs tests via dotnet test and Visual Studio test adapters.
/// Caches results per IDE type and supports filtering by test name, namespace, or project.
/// </summary>
internal sealed class TestRunnerService : ITestRunner
{
    private readonly ILogger<TestRunnerService> _logger;
    private readonly IOptions<TokenSaverConfig> _config;
    private readonly Dictionary<string, TestExecutionResult> _lastResults;
    private readonly object _lock = new();

    public TestRunnerService(
        ILogger<TestRunnerService> logger,
        IOptions<TokenSaverConfig> config)
    {
        _logger = logger;
        _config = config;
        _lastResults = new Dictionary<string, TestExecutionResult>();
    }

    public async Task<TestExecutionResult> RunTestsAsync(
        string ideType,
        string? testName = null,
        string? testNamespace = null,
        string? projectPath = null,
        bool failedOnly = false,
        CancellationToken ct = default)
    {
        try
        {
            ValidateIdeType(ideType);

            TestExecutionResult result = ideType switch
            {
                "VsCode" => await RunTestsViaCommandLineAsync(testName, testNamespace, projectPath, failedOnly, ct),
                "VisualStudio2026" => await RunTestsViaVisualStudioAsync(testName, testNamespace, projectPath, failedOnly, ct),
                _ => throw new ArgumentException($"Unknown IDE type: {ideType}", nameof(ideType))
            };

            lock (_lock)
            {
                _lastResults[ideType] = result;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running tests in {IdeType}", ideType);
            var failureResult = new TestExecutionResult(
                Success: false,
                TotalTests: 0,
                PassedTests: 0,
                FailedTests: 0,
                SkippedTests: 0,
                ElapsedMilliseconds: 0,
                Failures: new List<TestFailure>().AsReadOnly(),
                Message: $"Test execution failed: {ex.Message}");

            lock (_lock)
            {
                _lastResults[ideType] = failureResult;
            }

            return failureResult;
        }
    }

    public Task<TestExecutionResult?> GetLastTestResultAsync(
        string ideType,
        CancellationToken ct = default)
    {
        ValidateIdeType(ideType);

        lock (_lock)
        {
            var hasResult = _lastResults.TryGetValue(ideType, out var result);
            return Task.FromResult(hasResult ? result : null);
        }
    }

    private async Task<TestExecutionResult> RunTestsViaCommandLineAsync(
        string? testName,
        string? testNamespace,
        string? projectPath,
        bool failedOnly,
        CancellationToken ct)
    {
        // No working-directory fallback: searching for test projects needs a root the client named.
        if (projectPath is null && string.IsNullOrWhiteSpace(_config.Value.WorkspaceRoot))
        {
            return new TestExecutionResult(
                Success: false,
                TotalTests: 0,
                PassedTests: 0,
                FailedTests: 0,
                SkippedTests: 0,
                ElapsedMilliseconds: 0,
                Failures: new List<TestFailure>().AsReadOnly(),
                Message: "No workspace root known. Pass projectPath, or send a workspace folder from the client.");
        }

        var workspaceRoot = _config.Value.WorkspaceRoot!;
        var testProject = projectPath ?? FindDefaultTestProject(workspaceRoot);

        if (string.IsNullOrWhiteSpace(testProject))
        {
            return new TestExecutionResult(
                Success: false,
                TotalTests: 0,
                PassedTests: 0,
                FailedTests: 0,
                SkippedTests: 0,
                ElapsedMilliseconds: 0,
                Failures: new List<TestFailure>().AsReadOnly(),
                Message: "No test projects found in workspace");
        }

        // ArgumentList quotes each argument, so project paths with spaces work. The default console
        // summary ("Total tests: / Passed: / Failed:") is what ParseTestOutput reads.
        List<string> arguments = ["test", testProject, "--no-build"];
        if (!string.IsNullOrWhiteSpace(testName))
        {
            arguments.Add("--filter");
            arguments.Add($"FullyQualifiedName~{testName}");
        }
        else if (!string.IsNullOrWhiteSpace(testNamespace))
        {
            arguments.Add("--filter");
            arguments.Add($"FullyQualifiedName~{testNamespace}");
        }

        try
        {
            var sw = Stopwatch.StartNew();
            CommandResult run = await CommandRunner.RunAsync(
                new CommandRequest
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    WorkingDirectory = workspaceRoot,
                    Timeout = TimeSpan.FromMinutes(30),
                    OnStarted = process => ManagedProcessRegistry.Track(process, "test run"),
                },
                ct);
            sw.Stop();

            if (!run.Launched)
            {
                return CreateFailureResult($"Failed to start dotnet test process: {run.LaunchError}");
            }

            if (run.TimedOut)
            {
                return CreateFailureResult("Test execution timed out after 30 minutes and was killed");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Test run completed in {Elapsed}ms with exit code {ExitCode}", sw.ElapsedMilliseconds, run.ExitCode);
            }

            if (run.ExitCode != 0 && string.IsNullOrWhiteSpace(run.StdOut))
            {
                return CreateFailureResult($"Test execution failed with exit code {run.ExitCode}: {run.StdErr}");
            }

            TestExecutionResult parsed = ParseTestOutput(run.StdOut, sw.ElapsedMilliseconds);

            // A non-zero exit (build failure, crash, failed tests the parser missed) is never a success.
            return run.ExitCode != 0 && parsed.Success
                ? parsed with { Success = false, Message = $"dotnet test exited with code {run.ExitCode}. {parsed.Message}\n{run.StdErr}".Trim() }
                : parsed;
        }
        catch (OperationCanceledException)
        {
            return CreateFailureResult("Test execution was cancelled");
        }
    }

    private async Task<TestExecutionResult> RunTestsViaVisualStudioAsync(
        string? testName,
        string? testNamespace,
        string? projectPath,
        bool failedOnly,
        CancellationToken ct)
    {
        // Visual Studio test execution via command-line (vstest.console.exe or test adapter)
        // For now, delegate to dotnet test as fallback — full VS DTE integration would require
        // running inside VS or using its extensibility APIs which require COM interop.

        _logger.LogInformation("Visual Studio test execution via command-line adapter");
        return await RunTestsViaCommandLineAsync(testName, testNamespace, projectPath, failedOnly, ct);
    }

    private string? FindDefaultTestProject(string workspaceRoot)
    {
        try
        {
            var testProjects = Directory.EnumerateFiles(
                workspaceRoot,
                "*.csproj",
                SearchOption.AllDirectories)
                .Where(p => Path.GetFileNameWithoutExtension(p).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            return testProjects;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error finding test projects");
            return null;
        }
    }

    private TestExecutionResult ParseTestOutput(string output, long elapsedMs)
    {
        // Simplified parsing: count pass/fail from output
        // A full implementation would parse the JSON logger output or use test adapters
        var totalMatch = Regex.Matches(output, @"Total tests:\s*(\d+)", RegexOptions.IgnoreCase);
        var passedMatch = Regex.Matches(output, @"Passed:\s*(\d+)", RegexOptions.IgnoreCase);
        var failedMatch = Regex.Matches(output, @"Failed:\s*(\d+)", RegexOptions.IgnoreCase);
        var skippedMatch = Regex.Matches(output, @"Skipped:\s*(\d+)", RegexOptions.IgnoreCase);

        int total = totalMatch.Count > 0 ? int.Parse(totalMatch[0].Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int passed = passedMatch.Count > 0 ? int.Parse(passedMatch[0].Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int failed = failedMatch.Count > 0 ? int.Parse(failedMatch[0].Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int skipped = skippedMatch.Count > 0 ? int.Parse(skippedMatch[0].Groups[1].Value, CultureInfo.InvariantCulture) : 0;

        // Extract failure details from output
        var failures = new List<TestFailure>();
        var failurePattern = @"(?:FAILED|Failed)\s+(?<name>[^\n]+)(?:\n\s+(?<message>[^\n]+))?";
        var failureMatches = Regex.Matches(output, failurePattern, RegexOptions.IgnoreCase);

        foreach (Match match in failureMatches)
        {
            failures.Add(new TestFailure(
                TestName: match.Groups["name"].Value.Trim(),
                FailureMessage: match.Groups["message"].Value.Trim(),
                StackTrace: "",
                ExpectedValue: null,
                ActualValue: null));
        }

        bool success = failed == 0;
        string message = $"Tests run: {total}, Passed: {passed}, Failed: {failed}, Skipped: {skipped}";

        return new TestExecutionResult(
            Success: success,
            TotalTests: total,
            PassedTests: passed,
            FailedTests: failed,
            SkippedTests: skipped,
            ElapsedMilliseconds: elapsedMs,
            Failures: failures.AsReadOnly(),
            Message: message);
    }

    private TestExecutionResult CreateFailureResult(string message)
    {
        return new TestExecutionResult(
            Success: false,
            TotalTests: 0,
            PassedTests: 0,
            FailedTests: 0,
            SkippedTests: 0,
            ElapsedMilliseconds: 0,
            Failures: new List<TestFailure>().AsReadOnly(),
            Message: message);
    }

    private static void ValidateIdeType(string ideType)
    {
        if (ideType != "VsCode" && ideType != "VisualStudio2026")
        {
            throw new ArgumentException($"Invalid IDE type '{ideType}'. Must be 'VsCode' or 'VisualStudio2026'.", nameof(ideType));
        }
    }
}
