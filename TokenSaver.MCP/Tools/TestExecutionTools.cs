using TokenSaver.Common.Core.Contracts;

using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text.Json;

namespace TokenSaver.MCP.Tools;

/// <summary>
///     Test execution tools for running tests in VS Code and Visual Studio.
///     Supports running individual tests, all tests, re-running failed tests, and viewing results.
///     See docs/test-runner-contract.md.
/// </summary>
[McpServerToolType]
internal sealed class TestExecutionTools(ITestRunner testRunner)
{
    [McpServerTool]
    [Description(
        "Run tests with flexible filtering. Can run a specific test, all tests in a namespace, " +
        "tests in a project, or re-run failed tests.")]
    public async Task<string> RunTests(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        [Description("Optional: run a specific test by name (e.g., 'MyProject.Tests.MyTestClass.MyTest')")]
        string? testName = null,
        [Description("Optional: run all tests in a namespace (e.g., 'MyProject.Tests.ConfigTests')")]
        string? testNamespace = null,
        [Description("Optional: run tests in a specific project; if null and no other filters, runs all tests")]
        string? projectPath = null,
        [Description("If true, re-run only tests that failed in the last test run (overrides other filters)")]
        bool failedOnly = false,
        CancellationToken ct = default)
    {
        TestExecutionResult result = await testRunner.RunTestsAsync(
            ideType, testName, testNamespace, projectPath, failedOnly, ct);

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            total_tests = result.TotalTests,
            passed_tests = result.PassedTests,
            failed_tests = result.FailedTests,
            skipped_tests = result.SkippedTests,
            elapsed_ms = result.ElapsedMilliseconds,
            message = result.Message,
            failures = result.Failures.Select(f => new
            {
                test_name = f.TestName,
                failure_message = f.FailureMessage,
                stack_trace = f.StackTrace,
                expected_value = f.ExpectedValue,
                actual_value = f.ActualValue
            }).ToList()
        });
    }

    [McpServerTool]
    [Description("Get the results from the last test run.")]
    public async Task<string> GetLastTestResult(
        [Description("IDE type: 'VsCode' or 'VisualStudio2026'")] string ideType,
        CancellationToken ct = default)
    {
        TestExecutionResult? result = await testRunner.GetLastTestResultAsync(ideType, ct);

        if (result == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "No test results available"
            });
        }

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            total_tests = result.TotalTests,
            passed_tests = result.PassedTests,
            failed_tests = result.FailedTests,
            skipped_tests = result.SkippedTests,
            elapsed_ms = result.ElapsedMilliseconds,
            message = result.Message,
            failures = result.Failures.Select(f => new
            {
                test_name = f.TestName,
                failure_message = f.FailureMessage,
                stack_trace = f.StackTrace,
                expected_value = f.ExpectedValue,
                actual_value = f.ActualValue
            }).ToList()
        });
    }
}
