using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Persists debug events to disk in JSONL format with automatic rotation and retention.
/// Runs asynchronously to avoid blocking event recording.
/// </summary>
internal sealed class DebuggerEventLogWriter : IHostedService
{
    private readonly ILogger<DebuggerEventLogWriter> _logger;
    private readonly IDebuggerEventCapture _eventCapture;
    private readonly string _logDirectory;
    private readonly int _maxLogFileSizeMb;
    private readonly int _retentionDays;
    private readonly Channel<DebuggerEvent> _writeQueue;
    private Task? _backgroundTask;
    private CancellationTokenSource? _cancellationTokenSource;

    public DebuggerEventLogWriter(
        ILogger<DebuggerEventLogWriter> logger,
        IDebuggerEventCapture eventCapture,
        string? logDirectory = null,
        int maxLogFileSizeMb = 100,
        int retentionDays = 30)
    {
        _logger = logger;
        _eventCapture = eventCapture;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TokenSaver", "debugger-logs");
        _maxLogFileSizeMb = maxLogFileSizeMb;
        _retentionDays = retentionDays;
        _writeQueue = Channel.CreateUnbounded<DebuggerEvent>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Debug event logging started, output directory: {LogDirectory}", _logDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create debug log directory: {LogDirectory}", _logDirectory);
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = RunBackgroundWriterAsync(_cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _writeQueue.Writer.TryComplete();

        if (_backgroundTask != null)
        {
            if (_cancellationTokenSource is not null)
            {
                await _cancellationTokenSource.CancelAsync();
            }
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _cancellationTokenSource?.Dispose();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Debug event logging stopped");
        }
    }

    /// <summary>
    /// Queue an event for persistence. Non-blocking.
    /// </summary>
    public void QueueEventForPersistence(DebuggerEvent evt)
    {
        if (!_writeQueue.Writer.TryWrite(evt))
        {
            _logger.LogWarning("Failed to queue event for persistence, queue full");
        }
    }

    private async Task RunBackgroundWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = false };
            var currentLogFile = GetCurrentLogFilePath();
            long currentFileSizeBytes = 0;

            if (File.Exists(currentLogFile))
            {
                currentFileSizeBytes = new FileInfo(currentLogFile).Length;
            }

            var writer = new StreamWriter(
                new FileStream(currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read),
                System.Text.Encoding.UTF8)
            { AutoFlush = true };

            try
            {
                await foreach (var evt in _writeQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            id = evt.Id.ToString("N"),
                            timestamp = evt.Timestamp,
                            type = evt.EventType.ToString(),
                            severity = evt.Severity.ToString(),
                            toolName = evt.ToolName,
                            clientName = evt.CallerInfo.ClientName,
                            duration = evt.Duration?.TotalMilliseconds,
                            data = evt.Data
                        }, options);

                        var line = $"{json}\n";
                        await writer.WriteAsync(line);
                        currentFileSizeBytes += System.Text.Encoding.UTF8.GetByteCount(line);

                        // Check if rotation needed (max file size)
                        if (currentFileSizeBytes > _maxLogFileSizeMb * 1024 * 1024)
                        {
                            await writer.FlushAsync(CancellationToken.None);
                            await writer.DisposeAsync();

                            currentLogFile = GetCurrentLogFilePath();
                            currentFileSizeBytes = 0;

                            writer = new StreamWriter(
                                new FileStream(currentLogFile, FileMode.Create, FileAccess.Write, FileShare.Read),
                                System.Text.Encoding.UTF8)
                            { AutoFlush = true };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error writing event to log");
                    }
                }
            }
            finally
            {
                await writer.FlushAsync(CancellationToken.None);
                await writer.DisposeAsync();
            }

            // Cleanup old logs
            CleanupOldLogs();
        }
        catch (OperationCanceledException)
        {
            // Service stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background event log writer failed");
        }
    }

    private string GetCurrentLogFilePath()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(_logDirectory, $"events-{date}.jsonl");
    }

    private void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);
            var oldFiles = Directory.GetFiles(_logDirectory, "events-*.jsonl")
                .Where(f =>
                {
                    var filename = Path.GetFileNameWithoutExtension(f);
                    var datePart = filename.Replace("events-", "", StringComparison.Ordinal);
                    return DateTime.TryParse(datePart, out var fileDate) && fileDate < cutoffDate;
                })
                .ToList();

            foreach (var file in oldFiles)
            {
                try
                {
                    File.Delete(file);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Deleted old debug log: {File}", Path.GetFileName(file));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old log file: {File}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old debug logs");
        }
    }
}

/// <summary>
/// Service for querying persisted debug events from disk.
/// </summary>
internal sealed class DebuggerEventQueryService
{
    private readonly ILogger<DebuggerEventQueryService> _logger;
    private readonly string _logDirectory;

    public DebuggerEventQueryService(
        ILogger<DebuggerEventQueryService> logger,
        string? logDirectory = null)
    {
        _logger = logger;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TokenSaver", "debugger-logs");
    }

    /// <summary>
    /// Query events from persisted logs by date range and optional filters.
    /// </summary>
    public IReadOnlyList<DebuggerEventLogRecord> QueryEvents(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? eventType = null,
        string? severity = null,
        string? clientName = null,
        int? limitCount = null)
    {
        var results = new List<DebuggerEventLogRecord>();

        try
        {
            if (!Directory.Exists(_logDirectory))
                return results.AsReadOnly();

            startDate ??= DateTime.UtcNow.AddDays(-30);
            endDate ??= DateTime.UtcNow;

            var logFiles = Directory.GetFiles(_logDirectory, "events-*.jsonl")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var logFile in logFiles)
            {
                var filename = Path.GetFileNameWithoutExtension(logFile);
                var datePart = filename.Replace("events-", "", StringComparison.Ordinal);

                if (!DateTime.TryParse(datePart, out var fileDate))
                    continue;

                if (fileDate < startDate.Value.Date || fileDate > endDate.Value.Date)
                    continue;

                var fileResults = ReadLogFile(logFile, eventType, severity, clientName);
                results.AddRange(fileResults);

                if (limitCount.HasValue && results.Count >= limitCount.Value)
                {
                    results = results.Take(limitCount.Value).ToList();
                    break;
                }
            }

            if (limitCount.HasValue && results.Count > limitCount.Value)
            {
                results = results.TakeLast(limitCount.Value).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying debug event logs");
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Get available log files and their date range.
    /// </summary>
    public DebuggerLogFileInfo GetAvailableLogFiles()
    {
        var files = new List<string>();
        DateTime? oldestDate = null;
        DateTime? newestDate = null;
        long totalSizeBytes = 0;

        try
        {
            if (!Directory.Exists(_logDirectory))
                return new DebuggerLogFileInfo(files, oldestDate, newestDate, 0);

            var logFiles = Directory.GetFiles(_logDirectory, "events-*.jsonl")
                .OrderBy(f => f)
                .ToList();

            foreach (var logFile in logFiles)
            {
                var filename = Path.GetFileNameWithoutExtension(logFile);
                var datePart = filename.Replace("events-", "", StringComparison.Ordinal);

                if (DateTime.TryParse(datePart, out var fileDate))
                {
                    files.Add(filename);
                    oldestDate = oldestDate == null ? fileDate : new DateTime(Math.Min(oldestDate.Value.Ticks, fileDate.Ticks));
                    newestDate = newestDate == null ? fileDate : new DateTime(Math.Max(newestDate.Value.Ticks, fileDate.Ticks));
                }

                var fileInfo = new FileInfo(logFile);
                totalSizeBytes += fileInfo.Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log file info");
        }

        return new DebuggerLogFileInfo(files, oldestDate, newestDate, totalSizeBytes);
    }

    private List<DebuggerEventLogRecord> ReadLogFile(
        string filePath,
        string? eventTypeFilter,
        string? severityFilter,
        string? clientNameFilter)
    {
        var results = new List<DebuggerEventLogRecord>();

        try
        {
            using var reader = new StreamReader(filePath);
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var eventType = root.GetProperty("type").GetString();
                    var severity = root.GetProperty("severity").GetString();
                    var clientName = root.TryGetProperty("clientName", out var cn) ? cn.GetString() : null;

                    if (eventTypeFilter != null && eventType != eventTypeFilter)
                        continue;

                    if (severityFilter != null && severity != severityFilter)
                        continue;

                    if (clientNameFilter != null && clientName != clientNameFilter)
                        continue;

                    var record = new DebuggerEventLogRecord(
                        Id: root.GetProperty("id").GetString() ?? "",
                        Timestamp: DateTime.Parse(root.GetProperty("timestamp").GetString() ?? "", CultureInfo.InvariantCulture),
                        Type: eventType ?? "",
                        Severity: severity ?? "",
                        ToolName: root.TryGetProperty("toolName", out var tn) ? tn.GetString() : null,
                        ClientName: clientName,
                        Duration: root.TryGetProperty("duration", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetDouble() : null);

                    results.Add(record);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error parsing log line");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading log file: {FilePath}", filePath);
        }

        return results;
    }
}

/// <summary>Event record read from persisted log.</summary>
internal sealed record DebuggerEventLogRecord(
    string Id,
    DateTime Timestamp,
    string Type,
    string Severity,
    string? ToolName,
    string? ClientName,
    double? Duration);

/// <summary>Information about available log files.</summary>
internal sealed record DebuggerLogFileInfo(
    List<string> Files,
    DateTime? OldestDate,
    DateTime? NewestDate,
    long TotalSizeBytes);
