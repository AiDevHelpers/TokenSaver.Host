using Microsoft.Extensions.Options;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.LogHelper.Contracts;
using TokenSaver.LogHelper.Models;
using TokenSaver.MCP.Tools;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

public sealed class LogToolsTests
{
    private static LogTools CreateTools(FakeLogService? service = null, FakeSavings? savings = null) =>
        new(
            service ?? new FakeLogService(),
            savings ?? new FakeSavings(),
            Options.Create(new TokenSaverConfig { WorkspaceRoot = @"C:\ws" }));

    [Fact]
    public async Task LogChannels_ListsChannelIds()
    {
        var service = new FakeLogService
        {
            Channels =
            [
                new LogChannelInfo(ChannelId.Parse("vscode:Git"), "Git output", null),
                new LogChannelInfo(ChannelId.Parse(@"file:C:\x.log"), "x", 12),
            ],
        };

        string text = await CreateTools(service).LogChannels();

        Assert.Contains("vscode:Git", text, StringComparison.Ordinal);
        Assert.Contains(@"file:C:\x.log", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogQuery_Summary_PassesFilterFieldsToService()
    {
        var service = new FakeLogService { Result = Ok("summary text") };

        await CreateTools(service).LogQuery(
            @"file:C:\x.log",
            match: "db",
            exclude: "noise",
            minLevel: "Warning",
            category: "App",
            view: "summary",
            maxTokens: 800);

        Assert.NotNull(service.LastRequest);
        Assert.Equal("summary", service.LastRequest!.View);
        Assert.Equal(800, service.LastRequest.MaxTokens);
        Assert.Equal("db", service.LastRequest.Filter!.Match);
        Assert.Equal("noise", service.LastRequest.Filter.Exclude);
        Assert.Equal(LogSeverity.Warning, service.LastRequest.Filter.MinLevel);
        Assert.Equal("App", service.LastRequest.Filter.Category);
    }

    [Fact]
    public async Task LogQuery_RecordsSavings_RawAndReturnedTokens()
    {
        var savings = new FakeSavings();
        var service = new FakeLogService { Result = Ok("summary", rawChars: 4000, returnedChars: 400) };

        await CreateTools(service, savings).LogQuery(@"file:C:\x.log");

        Assert.NotNull(savings.Last);
        Assert.Equal(@"C:\ws", savings.Last!.WorkspaceRoot);
        Assert.Equal("logQuery", savings.Last.Tool);
        Assert.True(savings.Last.Terse);
        Assert.Equal(1000, savings.Last.RawTokens);
        Assert.Equal(100, savings.Last.DistilledTokens);
    }

    [Fact]
    public async Task LogQuery_InvalidChannel_ReturnsError()
    {
        string text = await CreateTools().LogQuery("nope:x");

        Assert.StartsWith("Error:", text, StringComparison.Ordinal);
        Assert.Contains("LogChannels", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogQuery_InvalidMinLevel_ReturnsError()
    {
        string text = await CreateTools().LogQuery(@"file:C:\x.log", minLevel: "loud");

        Assert.StartsWith("Error:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogQuery_ServiceError_ReturnedAsText()
    {
        var service = new FakeLogService
        {
            Result = new LogQueryResult(false, "boom", string.Empty, 0, 0, null, false),
        };

        string text = await CreateTools(service).LogQuery(@"file:C:\x.log");

        Assert.Equal("Error: boom", text);
    }

    [Fact]
    public async Task LogQuery_ReturnsCursorAndTruncationFooter()
    {
        var service = new FakeLogService
        {
            Result = new LogQueryResult(true, null, "body", 10, 4, "tok123", Truncated: true),
        };

        string text = await CreateTools(service).LogQuery(@"file:C:\x.log");

        Assert.Contains("body", text, StringComparison.Ordinal);
        Assert.Contains("cursor: tok123", text, StringComparison.Ordinal);
        Assert.Contains("truncated", text, StringComparison.Ordinal);
    }

    private static LogQueryResult Ok(string text, int rawChars = 0, int returnedChars = 0) =>
        new(true, null, text, rawChars, returnedChars, null, false);

    private sealed class FakeLogService : ILogService
    {
        public IReadOnlyList<LogChannelInfo> Channels { get; set; } = Array.Empty<LogChannelInfo>();

        public LogQueryResult Result { get; set; } = new(true, null, string.Empty, 0, 0, null, false);

        public LogQueryRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<LogChannelInfo>> ListChannelsAsync(string? nameFilter, LogRequestContext context, CancellationToken ct) =>
            Task.FromResult(Channels);

        public Task<LogQueryResult> QueryAsync(LogQueryRequest request, LogRequestContext context, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }

        public Task<LogQueryResult> MarkRunAsync(ChannelId channel, LogRequestContext context, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LogQueryResult> SinkCompareAsync(ChannelId appChannel, ChannelId sinkChannel, int maxTokens, LogRequestContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed record SavingsCall(string WorkspaceRoot, string Tool, bool Terse, int RawTokens, int DistilledTokens);

    private sealed class FakeSavings : ISavingsTracker
    {
        public SavingsCall? Last { get; private set; }

        public Task RecordAsync(
            string workspaceRoot,
            string tool,
            string role,
            bool terse,
            int rawTokens,
            int distilledTokens,
            int durationMs,
            int conversationTurns = 0,
            int conversationTokens = 0,
            CancellationToken ct = default)
        {
            Last = new SavingsCall(workspaceRoot, tool, terse, rawTokens, distilledTokens);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SavingsAggregate>> GetReportAsync(string workspaceRoot, int sinceDays, bool groupByRole, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SavingsAggregate> GetOneAsync(string workspaceRoot, string target, bool groupByRole, int sinceDays, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public SavingsVerdict Verdict(SavingsAggregate aggregate) => throw new NotSupportedException();
    }
}
