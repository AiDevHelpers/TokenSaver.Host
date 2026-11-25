# Test Runner Contract

**Status**: Public MCP tool surface  
**Version**: 1.0  
**Last Updated**: 2026-07-11

## Overview

The Test Runner contract defines how the MCP server executes tests in VS Code and Visual Studio, tracks results, and supports re-running failed tests. This enables calling AI to run tests interactively, debug test failures, and validate fixes.

## Public Interface

Four MCP tools expose test execution:

### RunTest

Run a specific test by name or file path.

**Input:**
```json
{
  "ideType": "VsCode",
  "testNameOrPath": "TokenSaver.Library.Tests.WorkspaceIndexServiceTests.IndexRepoAsync_ReturnsCorrectFileCount"
}
```

**Output:**
```json
{
  "success": true,
  "total_tests": 1,
  "passed_tests": 1,
  "failed_tests": 0,
  "skipped_tests": 0,
  "elapsed_ms": 500,
  "message": "Test 'TokenSaver.Library.Tests.WorkspaceIndexServiceTests.IndexRepoAsync_ReturnsCorrectFileCount' passed",
  "failures": []
}
```

**Behavior:**
- Test name can be fully-qualified class.method name or file path
- Runs single test and returns immediate result
- Stores result for later retrieval via `GetLastTestResult`

### RunAllTests

Run all tests in a project or the entire solution.

**Input:**
```json
{
  "ideType": "VsCode",
  "projectPath": "src/Core/TokenSaver.Library.Tests"
}
```

**Output:**
```json
{
  "success": true,
  "total_tests": 42,
  "passed_tests": 40,
  "failed_tests": 2,
  "skipped_tests": 0,
  "elapsed_ms": 5000,
  "message": "40 passed, 2 failed",
  "failures": [
    {
      "test_name": "TokenSaver.Library.Tests.ConfigTests.ParseConfig_InvalidJson_ThrowsException",
      "failure_message": "Expected FormatException but got InvalidOperationException",
      "stack_trace": "at TokenSaver.Library.Tests.ConfigTests.ParseConfig_InvalidJson_ThrowsException() in ConfigTests.cs:line 42",
      "expected_value": "FormatException",
      "actual_value": "InvalidOperationException"
    }
  ]
}
```

**Behavior:**
- If `projectPath` not provided, runs all tests in solution
- Collects all results and failure details
- Returns summary plus detailed failure info
- Stores results for failed-test re-run

### RunFailedTests

Re-run only the tests that failed in the previous test run.

**Input:**
```json
{
  "ideType": "VsCode"
}
```

**Output:**
```json
{
  "success": true,
  "total_tests": 2,
  "passed_tests": 2,
  "failed_tests": 0,
  "skipped_tests": 0,
  "elapsed_ms": 1000,
  "message": "All 2 previously failed tests now pass",
  "failures": []
}
```

**Behavior:**
- Re-runs only tests that failed in the last test run
- Returns error if no previous test run or no failed tests
- Useful for test-driven development cycle

### GetLastTestResult

Retrieve results from the last test run (useful for checking results after async runs).

**Input:**
```json
{
  "ideType": "VsCode"
}
```

**Output:**
```json
{
  "success": true,
  "total_tests": 42,
  "passed_tests": 40,
  "failed_tests": 2,
  "skipped_tests": 0,
  "elapsed_ms": 5000,
  "message": "40 passed, 2 failed",
  "failures": [...]
}
```

## Implementation Details

### Service: `ITestRunner` (TokenSaver.Common.Core)

Implemented by `TestRunnerService` in `TokenSaver.Library.Services`.

**Responsibilities:**
- Execute tests via IDE test runners or command-line (dotnet test, VS Test Explorer)
- Track last test run results per IDE
- Parse test output and extract failure details
- Support filtering by failed tests

### IDE Test Runner Integration

Each IDE has a test runner interface:

| IDE | Test Runner | Integration |
|-----|-------------|-------------|
| VS Code | Jest, Mocha, xunit (via Test Explorer) | Extension calls vscode.testing API |
| Visual Studio 2026 | VS Test Explorer (MSTest, xunit, NUnit) | Extension calls dte.tests API |

The extension communicates with the MCP via named pipe, passing test commands and receiving results.

**Named pipe message format**:

```json
{
  "command": "RunTest",
  "testName": "MyProject.Tests.MyTest",
  "id": "cmd_123"
}
```

```json
{
  "response": "ok",
  "id": "cmd_123",
  "result": {
    "passed": true,
    "elapsedMs": 500
  }
}
```

## Usage Example

**Scenario**: Claude is fixing a failing test and wants to validate the fix.

```
Claude: "There's a test failing in ConfigTests. Let me run it to see the error."

Claude (via MCP RunTest):
  {
    "ideType": "VsCode",
    "testNameOrPath": "TokenSaver.Library.Tests.ConfigTests.ParseConfig_InvalidJson_ThrowsException"
  }

MCP → VS Code Extension → Test Explorer
  → Runs test, captures result

MCP Response:
  {
    "success": false,
    "total_tests": 1,
    "failed_tests": 1,
    "failures": [
      {
        "failure_message": "Expected FormatException but got InvalidOperationException",
        "stack_trace": "...ConfigTests.cs:line 42",
        "expected_value": "FormatException",
        "actual_value": "InvalidOperationException"
      }
    ]
  }

Claude: "The test expects FormatException but the code throws InvalidOperationException. 
Let me fix that exception type."

Claude: "Now let me re-run this test to verify the fix."

Claude (via MCP RunTest):
  {
    "ideType": "VsCode",
    "testNameOrPath": "TokenSaver.Library.Tests.ConfigTests.ParseConfig_InvalidJson_ThrowsException"
  }

MCP Response:
  {
    "success": true,
    "passed_tests": 1,
    "failed_tests": 0,
    "message": "Test passed"
  }

Claude: "Great! The test now passes. Let me also run all tests to make sure I didn't break anything else."

Claude (via MCP RunAllTests):
  {
    "ideType": "VsCode"
  }

MCP Response:
  {
    "success": true,
    "total_tests": 42,
    "passed_tests": 42,
    "failed_tests": 0,
    "message": "All 42 tests passed"
  }
```

## Test Result Tracking

Results are cached per IDE to support workflows like:
1. Run all tests → 2 failures
2. View failures → inspect error messages
3. Fix code
4. Re-run failed tests → verify fixes
5. Run all tests → confirm no regression

## Error Handling

| Error Case | Behavior |
|-----------|----------|
| No previous test run | `GetLastTestResult` returns error |
| No failed tests to re-run | `RunFailedTests` returns success with 0 tests |
| Test runner not available | Returns error with clear message |
| Invalid test name | Test runner logs error, zero tests run |
| Timeout during test execution | Returns partial results (failed to complete) |

## Future Extensions

- **Coverage reports**: Return code coverage metrics alongside test results
- **Test filtering**: Run tests by tag, priority, or custom filter criteria
- **Parallel execution**: Configure test parallelism for faster runs
- **Performance analysis**: Track test duration over time, identify slow tests
- **Snapshot comparison**: Visual diff of expected vs actual in assertion failures
- **Test categorization**: Group tests by feature, module, or test type
- **Watch mode**: Auto-run tests on file changes
- **Profiling**: CPU/memory profiling for performance tests

## Revision History

| Date | Version | Changes |
|------|---------|---------|
| 2026-07-11 | 1.0 | Initial contract: RunTest, RunAllTests, RunFailedTests, GetLastTestResult |
