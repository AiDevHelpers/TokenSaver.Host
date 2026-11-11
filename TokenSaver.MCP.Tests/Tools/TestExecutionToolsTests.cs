using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Tools;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers TestExecutionTools through a fake ITestRunner, so no IDE or test process is started.
/// </summary>
public sealed class TestExecutionToolsTests
{
    [Fact]
    public async Task RunTests_PassingRun_ReturnsSuccessAndCounts()
    {
        FakeTestRunner runner = new()
        {
            RunResult = new TestExecutionResult(
                Success: true, TotalTests: 5, PassedTests: 5, FailedTests: 0, SkippedTests: 0,
                ElapsedMilliseconds: 120, Failures: [], Message: "all good"),
        };
        TestExecutionTools tools = new(runner);

        string json = await tools.RunTests("VsCode", testNamespace: "MyProject.Tests", failedOnly: false);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(5, result.GetProperty("total_tests").GetInt32());
        Assert.Equal(0, result.GetProperty("failed_tests").GetInt32());
        Assert.Equal("all good", result.GetProperty("message").GetString());
        Assert.Equal(0, result.GetProperty("failures").GetArrayLength());
        Assert.Equal("VsCode", runner.LastIdeType);
        Assert.Equal("MyProject.Tests", runner.LastNamespace);
    }

    [Fact]
    public async Task RunTests_FailedRun_SerializesFailureDetails()
    {
        FakeTestRunner runner = new()
        {
            RunResult = new TestExecutionResult(
                Success: false, TotalTests: 2, PassedTests: 1, FailedTests: 1, SkippedTests: 0,
                ElapsedMilliseconds: 55, Message: "1 failed",
                Failures:
                [
                    new TestFailure("MyProject.Tests.Foo.Bar", "Assert.Equal failed", "at Foo.Bar()", "1", "2"),
                ]),
        };
        TestExecutionTools tools = new(runner);

        string json = await tools.RunTests("VisualStudio2026", testName: "MyProject.Tests.Foo.Bar", failedOnly: true);
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        JsonElement failure = result.GetProperty("failures")[0];
        Assert.Equal("MyProject.Tests.Foo.Bar", failure.GetProperty("test_name").GetString());
        Assert.Equal("Assert.Equal failed", failure.GetProperty("failure_message").GetString());
        Assert.Equal("1", failure.GetProperty("expected_value").GetString());
        Assert.Equal("2", failure.GetProperty("actual_value").GetString());
        Assert.True(runner.LastFailedOnly);
        Assert.Equal("MyProject.Tests.Foo.Bar", runner.LastTestName);
    }

    [Fact]
    public async Task GetLastTestResult_NoPriorRun_ReturnsNoResultsMessage()
    {
        FakeTestRunner runner = new() { LastResult = null };
        TestExecutionTools tools = new(runner);

        string json = await tools.GetLastTestResult("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("No test results available", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetLastTestResult_PriorRun_ReturnsStoredCounts()
    {
        FakeTestRunner runner = new()
        {
            LastResult = new TestExecutionResult(
                Success: true, TotalTests: 3, PassedTests: 2, FailedTests: 0, SkippedTests: 1,
                ElapsedMilliseconds: 10, Failures: [], Message: "prior"),
        };
        TestExecutionTools tools = new(runner);

        string json = await tools.GetLastTestResult("VsCode");
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.GetProperty("skipped_tests").GetInt32());
        Assert.Equal("prior", result.GetProperty("message").GetString());
    }

    private sealed class FakeTestRunner : ITestRunner
    {
        public TestExecutionResult RunResult { get; set; } = new(true, 0, 0, 0, 0, 0, [], "");
        public TestExecutionResult? LastResult { get; set; }
        public string? LastIdeType { get; private set; }
        public string? LastNamespace { get; private set; }
        public string? LastTestName { get; private set; }
        public bool LastFailedOnly { get; private set; }

        public Task<TestExecutionResult> RunTestsAsync(
            string ideType,
            string? testName = null,
            string? testNamespace = null,
            string? projectPath = null,
            bool failedOnly = false,
            CancellationToken ct = default)
        {
            LastIdeType = ideType;
            LastTestName = testName;
            LastNamespace = testNamespace;
            LastFailedOnly = failedOnly;
            return Task.FromResult(RunResult);
        }

        public Task<TestExecutionResult?> GetLastTestResultAsync(string ideType, CancellationToken ct = default)
        {
            LastIdeType = ideType;
            return Task.FromResult(LastResult);
        }
    }
}
