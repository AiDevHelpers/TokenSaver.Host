using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Services;
using TokenSaver.LogHelper.Sources;
using TokenSaver.LogViewer;
using TokenSaver.MCP.Tools;
using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

public sealed class LogViewerToolsTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lvt-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly CountingLauncher _launcher = new();
    private readonly LogViewerHost _host;
    private readonly LogViewerTools _tools;

    public LogViewerToolsTests()
    {
        _ = Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "a.log");
        File.WriteAllText(_file, "2026-01-01 10:00:00 INFO ready\n");
        var options = new LogHelperOptions { AllowedRoots = new[] { _dir } };
        _host = new LogViewerHost(
            new LogWatcher(new[] { new FileLogSourceProvider(options) }, options),
            options,
            _launcher,
            new LogViewerOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200), MaxLaunchAttempts = 1 });
        _tools = new LogViewerTools(_host);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task LogWatch_List_WhenNothingIsWatched()
    {
        Assert.Equal("No log viewers open.", await _tools.LogWatch());
    }

    [Fact]
    public async Task LogWatch_Start_OpensATab_AndListShowsIt()
    {
        string started = await _tools.LogWatch("start", channel: $"file:{_file}", minLevel: "Error");

        Assert.Contains(_file, started, StringComparison.Ordinal);
        Assert.Contains("minLevel=Error", started, StringComparison.Ordinal);
        Assert.Contains(_file, await _tools.LogWatch("list"), StringComparison.Ordinal);
        await Eventually(() => _launcher.Opened >= 1);
    }

    [Fact]
    public async Task LogWatch_Stop_AndRestart_RejectAnUnknownId()
    {
        Assert.StartsWith("Error:", await _tools.LogWatch("stop", id: "nope"), StringComparison.Ordinal);
        Assert.StartsWith("Error:", await _tools.LogWatch("restart", id: "nope"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogWatch_Restart_AndStop_ActOnAKnownWatch()
    {
        string started = await _tools.LogWatch("start", channel: $"file:{_file}");
        string id = started.Split(' ')[0];

        Assert.StartsWith("Restarting", await _tools.LogWatch("restart", id: id), StringComparison.Ordinal);
        Assert.StartsWith("Stopped", await _tools.LogWatch("stop", id: id), StringComparison.Ordinal);
        Assert.Equal("No log viewers open.", await _tools.LogWatch("list"));
    }

    [Theory]
    [InlineData("start", null, "invalid channel")]
    [InlineData("start", "nonsense", "invalid channel")]
    [InlineData("explode", "file:C:/x.log", "action must be")]
    public async Task LogWatch_RejectsBadInput(string action, string? channel, string expected)
    {
        string text = await _tools.LogWatch(action, channel: channel);
        Assert.StartsWith("Error:", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogWatch_Start_RejectsABadMinLevel()
    {
        Assert.Contains("invalid minLevel", await _tools.LogWatch("start", channel: $"file:{_file}", minLevel: "loud"), StringComparison.Ordinal);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class CountingLauncher : IViewerLauncher
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public void OpenTab(string title, string pipeName) => Interlocked.Increment(ref _opened);

        public bool IsRunning(int pid) => false;

        public void Kill(int pid)
        {
        }
    }
}
