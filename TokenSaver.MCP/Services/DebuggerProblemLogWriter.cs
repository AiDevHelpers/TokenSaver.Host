using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using AiDevTools.TokenSaver.Common.Core.Contracts;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
/// Persists debug problems to disk in JSONL format with automatic rotation and retention.
/// Runs asynchronously to avoid blocking problem recording.
/// </summary>
internal sealed class DebuggerProblemLogWriter : IHostedService
{
    private readonly ILogger<DebuggerProblemLogWriter> _logger;
    private readonly IDebuggerProblemCapture _problemCapture;
    private readonly string _logDirectory;
    private readonly int _maxLogFileSizeMb;
    private readonly int _retentionDays;
    private readonly Channel<DebuggerProblem> _writeQueue;
    private Task? _backgroundTask;
    private CancellationTokenSource? _cancellationTokenSource;

    public DebuggerProblemLogWriter(
        ILogger<DebuggerProblemLogWriter> logger,
        IDebuggerProblemCapture problemCapture,
        string? logDirectory = null,
        int maxLogFileSizeMb = 50,
        int retentionDays = 30)
    {
        _logger = logger;
        _problemCapture = problemCapture;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TokenSaver", "problem-logs");
        _maxLogFileSizeMb = maxLogFileSizeMb;
        _retentionDays = retentionDays;
        _writeQueue = Channel.CreateUnbounded<DebuggerProblem>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Problem logging started, output directory: {LogDirectory}", _logDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create problem log directory: {LogDirectory}", _logDirectory);
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
            _logger.LogInformation("Problem logging stopped");
        }
    }

    /// <summary>
    /// Queue a problem for persistence. Non-blocking.
    /// </summary>
    public void QueueProblemForPersistence(DebuggerProblem problem)
    {
        if (!_writeQueue.Writer.TryWrite(problem))
        {
            _logger.LogWarning("Failed to queue problem for persistence, queue full");
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
                await foreach (var problem in _writeQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            id = problem.Id.ToString("N"),
                            timestamp = problem.Timestamp,
                            type = problem.ProblemType,
                            severity = problem.Severity.ToString(),
                            title = problem.Title,
                            file = problem.FilePath,
                            line = problem.LineNumber,
                            column = problem.ColumnNumber,
                            details = problem.Details,
                            suggestion = problem.Suggestion,
                            tool = problem.ToolName,
                            client = problem.ClientName,
                            ruleId = problem.RuleId
                        }, options);

                        var line = $"{json}\n";
                        await writer.WriteAsync(line);
                        currentFileSizeBytes += System.Text.Encoding.UTF8.GetByteCount(line);

                        // Check if rotation needed
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
                        _logger.LogError(ex, "Error writing problem to log");
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
            _logger.LogError(ex, "Background problem log writer failed");
        }
    }

    private string GetCurrentLogFilePath()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(_logDirectory, $"problems-{date}.jsonl");
    }

    private void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);
            var oldFiles = Directory.GetFiles(_logDirectory, "problems-*.jsonl")
                .Where(f =>
                {
                    var filename = Path.GetFileNameWithoutExtension(f);
                    var datePart = filename.Replace("problems-", "", StringComparison.Ordinal);
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
                        _logger.LogInformation("Deleted old problem log: {File}", Path.GetFileName(file));
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
            _logger.LogError(ex, "Error cleaning up old problem logs");
        }
    }
}

/// <summary>
/// Service for querying persisted problems from disk.
/// </summary>
internal sealed class DebuggerProblemQueryService
{
    private readonly ILogger<DebuggerProblemQueryService> _logger;
    private readonly string _logDirectory;

    public DebuggerProblemQueryService(
        ILogger<DebuggerProblemQueryService> logger,
        string? logDirectory = null)
    {
        _logger = logger;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TokenSaver", "problem-logs");
    }

    /// <summary>
    /// Query problems from persisted logs by date range and filters.
    /// </summary>
    public IReadOnlyList<DebuggerProblemLogRecord> QueryProblems(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? problemType = null,
        string? severity = null,
        string? filePath = null,
        int? limitCount = null)
    {
        var results = new List<DebuggerProblemLogRecord>();

        try
        {
            if (!Directory.Exists(_logDirectory))
                return results.AsReadOnly();

            startDate ??= DateTime.UtcNow.AddDays(-30);
            endDate ??= DateTime.UtcNow;

            var logFiles = Directory.GetFiles(_logDirectory, "problems-*.jsonl")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var logFile in logFiles)
            {
                var filename = Path.GetFileNameWithoutExtension(logFile);
                var datePart = filename.Replace("problems-", "", StringComparison.Ordinal);

                if (!DateTime.TryParse(datePart, out var fileDate))
                    continue;

                if (fileDate < startDate.Value.Date || fileDate > endDate.Value.Date)
                    continue;

                var fileResults = ReadLogFile(logFile, problemType, severity, filePath);
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
            _logger.LogError(ex, "Error querying problem logs");
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Get available problem log files.
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

            var logFiles = Directory.GetFiles(_logDirectory, "problems-*.jsonl")
                .OrderBy(f => f)
                .ToList();

            foreach (var logFile in logFiles)
            {
                var filename = Path.GetFileNameWithoutExtension(logFile);
                var datePart = filename.Replace("problems-", "", StringComparison.Ordinal);

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
            _logger.LogError(ex, "Error retrieving problem log file info");
        }

        return new DebuggerLogFileInfo(files, oldestDate, newestDate, totalSizeBytes);
    }

    private List<DebuggerProblemLogRecord> ReadLogFile(
        string filePath,
        string? typeFilter,
        string? severityFilter,
        string? filePathFilter)
    {
        var results = new List<DebuggerProblemLogRecord>();

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

                    var type = root.GetProperty("type").GetString();
                    var severity = root.GetProperty("severity").GetString();
                    var file = root.TryGetProperty("file", out var f) ? f.GetString() : null;

                    if (typeFilter != null && type != typeFilter)
                        continue;

                    if (severityFilter != null && severity != severityFilter)
                        continue;

                    if (filePathFilter != null && file != filePathFilter)
                        continue;

                    var record = new DebuggerProblemLogRecord(
                        Id: root.GetProperty("id").GetString() ?? "",
                        Timestamp: DateTime.Parse(root.GetProperty("timestamp").GetString() ?? "", CultureInfo.InvariantCulture),
                        Type: type ?? "",
                        Severity: severity ?? "",
                        Title: root.GetProperty("title").GetString() ?? "",
                        FilePath: file,
                        LineNumber: root.TryGetProperty("line", out var ln) && ln.ValueKind != JsonValueKind.Null ? ln.GetInt32() : null,
                        RuleId: root.TryGetProperty("ruleId", out var rid) ? rid.GetString() : null);

                    results.Add(record);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error parsing problem log line");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading problem log file: {FilePath}", filePath);
        }

        return results;
    }
}

/// <summary>Problem record read from persisted log.</summary>
internal sealed record DebuggerProblemLogRecord(
    string Id,
    DateTime Timestamp,
    string Type,
    string Severity,
    string Title,
    string? FilePath,
    int? LineNumber,
    string? RuleId);
