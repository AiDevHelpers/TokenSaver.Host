using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Captures and retrieves debug output (stdout/stderr) from processes and IDE launches.
/// Maintains per-process capture buffers with severity classification.
/// </summary>
internal sealed class DebugOutputCaptureService : IDebugOutputCapture
{
    private readonly ILogger<DebugOutputCaptureService> _logger;
    private readonly Dictionary<int, ProcessCapture> _captures = new();
    private readonly Dictionary<string, IDECapture> _ideCaptures = new();
    private readonly object _lock = new();

    public DebugOutputCaptureService(ILogger<DebugOutputCaptureService> logger)
    {
        _logger = logger;
    }

    public Task<DebugOutput> GetCapturedOutputAsync(
        int? processId = null,
        string? ideType = null,
        int linesLimit = 1000,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (ideType != null)
            {
                if (!_ideCaptures.TryGetValue(ideType, out var ideCapture))
                {
                    return Task.FromResult(new DebugOutput(
                        ProcessId: null,
                        IdeType: ideType,
                        CapturedAt: DateTime.UtcNow,
                        Lines: new List<DebugOutputLine>().AsReadOnly(),
                        IsComplete: false));
                }

                var lines = ideCapture.Lines
                    .TakeLast(linesLimit)
                    .ToList()
                    .AsReadOnly();

                return Task.FromResult(new DebugOutput(
                    ProcessId: ideCapture.ProcessId,
                    IdeType: ideType,
                    CapturedAt: DateTime.UtcNow,
                    Lines: lines,
                    IsComplete: true));
            }

            if (processId == null)
            {
                return Task.FromResult(new DebugOutput(
                    ProcessId: null,
                    IdeType: null,
                    CapturedAt: DateTime.UtcNow,
                    Lines: new List<DebugOutputLine>().AsReadOnly(),
                    IsComplete: false));
            }

            if (!_captures.TryGetValue(processId.Value, out var capture))
            {
                return Task.FromResult(new DebugOutput(
                    ProcessId: processId,
                    IdeType: null,
                    CapturedAt: DateTime.UtcNow,
                    Lines: new List<DebugOutputLine>().AsReadOnly(),
                    IsComplete: false));
            }

            var capturedLines = capture.Lines
                .TakeLast(linesLimit)
                .ToList()
                .AsReadOnly();

            return Task.FromResult(new DebugOutput(
                ProcessId: processId,
                IdeType: null,
                CapturedAt: DateTime.UtcNow,
                Lines: capturedLines,
                IsComplete: true));
        }
    }

    public Task ClearOutputAsync(int processId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_captures.TryGetValue(processId, out var capture))
            {
                capture.Lines.Clear();
                _logger.LogInformation("Cleared output for process {ProcessId}", processId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<DebugOutputLine>> SearchOutputAsync(
        string pattern,
        int? processId = null,
        DebugSeverity? severity = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = new List<DebugOutputLine>();

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                results.AddRange(SearchWithRegex(regex));
            }
            catch (RegexParseException ex)
            {
                _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", pattern);
                results.AddRange(SearchWithSubstring());
            }

            return Task.FromResult((IEnumerable<DebugOutputLine>)results);

            List<DebugOutputLine> SearchWithRegex(Regex regex)
            {
                var found = new List<DebugOutputLine>();
                if (processId != null)
                {
                    if (_captures.TryGetValue(processId.Value, out var singleCap))
                    {
                        found.AddRange(SearchLines(singleCap.Lines, regex, severity));
                    }
                }
                else
                {
                    foreach (var allCap in _captures.Values)
                    {
                        found.AddRange(SearchLines(allCap.Lines, regex, severity));
                    }
                }
                return found;
            }

            List<DebugOutputLine> SearchWithSubstring()
            {
                var found = new List<DebugOutputLine>();
                var comparer = StringComparison.OrdinalIgnoreCase;
                if (processId != null)
                {
                    if (_captures.TryGetValue(processId.Value, out var singleCapture))
                    {
                        found.AddRange(singleCapture.Lines
                            .Where(l => l.Content.Contains(pattern, comparer)
                                    && (!severity.HasValue || l.Severity == severity.Value))
                            .ToList());
                    }
                }
                else
                {
                    foreach (var allCapture in _captures.Values)
                    {
                        found.AddRange(allCapture.Lines
                            .Where(l => l.Content.Contains(pattern, comparer)
                                    && (!severity.HasValue || l.Severity == severity.Value))
                            .ToList());
                    }
                }
                return found;
            }
        }
    }

    public void StartCapturing(Process process)
    {
        if (process == null || process.HasExited)
        {
            _logger.LogWarning("Cannot start capturing: process is null or already exited");
            return;
        }

        lock (_lock)
        {
            var capture = new ProcessCapture { ProcessId = process.Id };
            _captures[process.Id] = capture;

            if (process.StandardOutput != null)
            {
                _ = Task.Run(() => ReadStreamAsync(process.StandardOutput, capture, "stdout"));
            }

            if (process.StandardError != null)
            {
                _ = Task.Run(() => ReadStreamAsync(process.StandardError, capture, "stderr"));
            }

            _logger.LogInformation("Started capturing output for process {ProcessId}", process.Id);
        }
    }

    public void StartCapturingIDE(string ideType, Process process)
    {
        if (process == null || process.HasExited)
        {
            _logger.LogWarning("Cannot start capturing IDE output: process is null or already exited");
            return;
        }

        lock (_lock)
        {
            var capture = new IDECapture { ProcessId = process.Id };
            _ideCaptures[ideType] = capture;

            if (process.StandardOutput != null)
            {
                _ = Task.Run(() => ReadStreamAsync(process.StandardOutput, capture, "stdout"));
            }

            if (process.StandardError != null)
            {
                _ = Task.Run(() => ReadStreamAsync(process.StandardError, capture, "stderr"));
            }

            _logger.LogInformation("Started capturing output for IDE {IdeType} (PID {ProcessId})", ideType, process.Id);
        }
    }

    private async Task ReadStreamAsync(StreamReader stream, ProcessCapture capture, string streamType)
    {
        try
        {
            string? line;
            while ((line = await stream.ReadLineAsync()) != null)
            {
                lock (_lock)
                {
                    capture.Lines.Add(new DebugOutputLine(
                        LineNumber: capture.Lines.Count + 1,
                        Timestamp: DateTime.UtcNow,
                        Severity: ClassifySeverity(line),
                        Stream: streamType,
                        Content: line));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading {StreamType}", streamType);
        }
    }

    private async Task ReadStreamAsync(StreamReader stream, IDECapture capture, string streamType)
    {
        try
        {
            string? line;
            while ((line = await stream.ReadLineAsync()) != null)
            {
                lock (_lock)
                {
                    capture.Lines.Add(new DebugOutputLine(
                        LineNumber: capture.Lines.Count + 1,
                        Timestamp: DateTime.UtcNow,
                        Severity: ClassifySeverity(line),
                        Stream: streamType,
                        Content: line));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading {StreamType}", streamType);
        }
    }

    private List<DebugOutputLine> SearchLines(List<DebugOutputLine> lines, Regex regex, DebugSeverity? severity)
    {
        return lines
            .Where(l => regex.IsMatch(l.Content)
                    && (!severity.HasValue || l.Severity == severity.Value))
            .ToList();
    }

    private DebugSeverity ClassifySeverity(string line)
    {
        var lowerLine = line.ToLowerInvariant();
        if (lowerLine.Contains("fatal") || lowerLine.Contains("critical"))
            return DebugSeverity.Fatal;
        if (lowerLine.Contains("error") || lowerLine.Contains("exception"))
            return DebugSeverity.Error;
        if (lowerLine.Contains("warning") || lowerLine.Contains("warn"))
            return DebugSeverity.Warning;
        return DebugSeverity.Info;
    }

    private sealed class ProcessCapture
    {
        public int ProcessId { get; set; }
        public List<DebugOutputLine> Lines { get; } = new();
    }

    private sealed class IDECapture
    {
        public int ProcessId { get; set; }
        public List<DebugOutputLine> Lines { get; } = new();
    }
}
