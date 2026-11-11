using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Tools;

using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers DebugOutputTools through a fake IDebugOutputCapture. No process is launched or captured.
/// </summary>
public sealed class DebugOutputToolsTests
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

    [Fact]
    public async Task GetDebugOutput_NoProcessOrIde_ReturnsErrorWithoutQuerying()
    {
        FakeOutputCapture capture = new();
        DebugOutputTools tools = new(capture);

        string json = await tools.GetDebugOutput();
        JsonElement result = JsonElement.Parse(json);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("Must provide either processId or ideType", result.GetProperty("error").GetString());
        Assert.Equal(0, capture.GetCalls);
    }

    [Fact]
    public async Task GetDebugOutput_ProcessIdGiven_ReturnsLinesAndCapsLimit()
    {
        FakeOutputCapture capture = new()
        {
            Output = new DebugOutput(
                ProcessId: 42,
                IdeType: null,
                CapturedAt: Stamp,
                Lines: [new DebugOutputLine(1, Stamp, DebugSeverity.Error, "stderr", "boom")],
                IsComplete: true),
        };
        DebugOutputTools tools = new(capture);

        string json = await tools.GetDebugOutput(processId: 42, linesLimit: 99999);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(42, result.GetProperty("process_id").GetInt32());
        Assert.Equal(1, result.GetProperty("line_count").GetInt32());
        JsonElement line = result.GetProperty("lines")[0];
        Assert.Equal("Error", line.GetProperty("severity").GetString());
        Assert.Equal("boom", line.GetProperty("content").GetString());
        Assert.Equal("03:04:05.678", line.GetProperty("timestamp").GetString());
        Assert.Equal(5000, capture.LastLinesLimit);
    }

    [Fact]
    public async Task SearchDebugOutput_SeverityFilter_PassesParsedSeverityAndReturnsMatches()
    {
        FakeOutputCapture capture = new()
        {
            SearchResults = [new DebugOutputLine(7, Stamp, DebugSeverity.Fatal, "stderr", "NullReferenceException")],
        };
        DebugOutputTools tools = new(capture);

        string json = await tools.SearchDebugOutput("NullReference", severity: "fatal");
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(DebugSeverity.Fatal, capture.LastSeverity);
        Assert.Equal("NullReference", capture.LastPattern);
        Assert.Equal(1, result.GetProperty("match_count").GetInt32());
        Assert.Equal(7, result.GetProperty("matches")[0].GetProperty("line_number").GetInt32());
    }

    [Fact]
    public async Task SearchDebugOutput_UnknownSeverity_IgnoresFilter()
    {
        FakeOutputCapture capture = new();
        DebugOutputTools tools = new(capture);

        string json = await tools.SearchDebugOutput("anything", severity: "loud");
        JsonElement result = JsonElement.Parse(json);

        Assert.Null(capture.LastSeverity);
        Assert.Equal(0, result.GetProperty("match_count").GetInt32());
    }

    [Fact]
    public async Task ClearDebugOutput_ValidProcess_ClearsAndReportsSuccess()
    {
        FakeOutputCapture capture = new();
        DebugOutputTools tools = new(capture);

        string json = await tools.ClearDebugOutput(123);
        JsonElement result = JsonElement.Parse(json);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(123, capture.ClearedProcessId);
        Assert.Equal("Cleared debug output buffer for process 123", result.GetProperty("message").GetString());
    }

    private sealed class FakeOutputCapture : IDebugOutputCapture
    {
        public DebugOutput Output { get; set; } = new(null, null, Stamp, [], IsComplete: true);
        public IEnumerable<DebugOutputLine> SearchResults { get; set; } = [];
        public int GetCalls { get; private set; }
        public int LastLinesLimit { get; private set; }
        public string? LastPattern { get; private set; }
        public DebugSeverity? LastSeverity { get; private set; }
        public int? ClearedProcessId { get; private set; }

        public Task<DebugOutput> GetCapturedOutputAsync(int? processId = null, string? ideType = null, int linesLimit = 1000, CancellationToken ct = default)
        {
            GetCalls++;
            LastLinesLimit = linesLimit;
            return Task.FromResult(Output);
        }

        public Task ClearOutputAsync(int processId, CancellationToken ct = default)
        {
            ClearedProcessId = processId;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<DebugOutputLine>> SearchOutputAsync(string pattern, int? processId = null, DebugSeverity? severity = null, CancellationToken ct = default)
        {
            LastPattern = pattern;
            LastSeverity = severity;
            return Task.FromResult(SearchResults);
        }

        public void StartCapturing(Process process)
        {
        }

        public void StartCapturingIDE(string ideType, Process process)
        {
        }
    }
}
