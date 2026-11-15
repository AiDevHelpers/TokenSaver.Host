using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TokenSaver.Common.Core.Contracts;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Manages Server-Sent Events (SSE) subscriptions and broadcasts debug events
/// to connected clients in real-time with optional filtering.
/// </summary>
internal sealed class DebuggerEventStreamManager
{
    private readonly ILogger<DebuggerEventStreamManager> _logger;
    private readonly ConcurrentDictionary<string, DebuggerStreamSubscription> _subscriptions;
    private readonly IDebuggerEventCapture _eventCapture;
    private readonly int _maxSubscriptions;
    private readonly int _maxEventsPerSecond;
    private readonly object _broadcastLock = new();

    public DebuggerEventStreamManager(
        ILogger<DebuggerEventStreamManager> logger,
        IDebuggerEventCapture eventCapture,
        int maxSubscriptions = 50,
        int maxEventsPerSecond = 1000)
    {
        _logger = logger;
        _eventCapture = eventCapture;
        _subscriptions = new();
        _maxSubscriptions = maxSubscriptions;
        _maxEventsPerSecond = maxEventsPerSecond;
    }

    /// <summary>
    /// Register a new SSE subscription with optional filtering.
    /// </summary>
    public string AddSubscription(
        Func<DebuggerEvent, Task> onEventAsync,
        Func<DebuggerEvent, bool>? filter = null,
        string? subscriptionName = null)
    {
        if (_subscriptions.Count >= _maxSubscriptions)
        {
            _logger.LogWarning(
                "Maximum subscriptions ({MaxSubscriptions}) reached, rejecting new subscription",
                _maxSubscriptions);
            throw new InvalidOperationException($"Maximum subscriptions ({_maxSubscriptions}) reached");
        }

        var subscriptionId = Guid.NewGuid().ToString("N");
        var subscription = new DebuggerStreamSubscription(
            SubscriptionId: subscriptionId,
            SubscriptionName: subscriptionName ?? $"Subscription-{subscriptionId.Substring(0, 8)}",
            OnEventAsync: onEventAsync,
            Filter: filter,
            CreatedAt: DateTime.UtcNow,
            LastEventSentAt: null,
            EventsCount: 0);

        if (_subscriptions.TryAdd(subscriptionId, subscription))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "SSE subscription added: {SubscriptionName} (ID: {SubscriptionId})",
                    subscription.SubscriptionName,
                    subscriptionId);
            }
            return subscriptionId;
        }

        throw new InvalidOperationException("Failed to add subscription");
    }

    /// <summary>
    /// Remove a subscription when client disconnects.
    /// </summary>
    public void RemoveSubscription(string subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var subscription))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "SSE subscription removed: {SubscriptionName} (sent {EventCount} events)",
                    subscription.SubscriptionName,
                    subscription.EventsCount);
            }
        }
    }

    /// <summary>
    /// Broadcast an event to all subscriptions that match their filters.
    /// </summary>
    public async Task BroadcastEventAsync(DebuggerEvent evt)
    {
        lock (_broadcastLock)
        {
            // Rate limiting check
            if (!_subscriptions.IsEmpty)
            {
                var now = DateTime.UtcNow;
                var activeSubscriptions = _subscriptions.Values
                    .Where(s => s.LastEventSentAt != null && (now - s.LastEventSentAt.Value).TotalSeconds < 1)
                    .Count();

                if (activeSubscriptions >= _maxEventsPerSecond)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Rate limit reached ({MaxEventsPerSecond}/sec)", _maxEventsPerSecond);
                    }
                    return;
                }
            }
        }

        var tasks = new List<Task>();
        foreach (var kvp in _subscriptions)
        {
            var subscription = kvp.Value;

            // Check if event matches filter
            if (subscription.Filter != null && !subscription.Filter(evt))
            {
                continue;
            }

            // Send event to subscription
            tasks.Add(SendEventToSubscriptionAsync(subscription, evt));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// Get current subscription statistics.
    /// </summary>
    public DebuggerStreamStats GetStreamStats()
    {
        var subscriptions = _subscriptions.Values.ToList();
        return new DebuggerStreamStats(
            TotalSubscriptions: subscriptions.Count,
            ActiveSubscriptions: subscriptions.Count(s => s.LastEventSentAt != null),
            IdleSubscriptions: subscriptions.Count(s => s.LastEventSentAt == null),
            TotalEventsSent: subscriptions.Sum(s => s.EventsCount),
            OldestSubscription: subscriptions.MinBy(s => s.CreatedAt)?.CreatedAt,
            MostRecentEvent: subscriptions.MaxBy(s => s.LastEventSentAt)?.LastEventSentAt);
    }

    /// <summary>
    /// Get details of all subscriptions for monitoring.
    /// </summary>
    public IReadOnlyList<DebuggerStreamSubscriptionInfo> GetSubscriptionDetails()
    {
        return _subscriptions.Values.Select(s => new DebuggerStreamSubscriptionInfo(
            SubscriptionId: s.SubscriptionId,
            SubscriptionName: s.SubscriptionName,
            CreatedAt: s.CreatedAt,
            LastEventSentAt: s.LastEventSentAt,
            EventsCount: s.EventsCount,
            HasFilter: s.Filter != null,
            IsActive: s.LastEventSentAt != null && DateTime.UtcNow - s.LastEventSentAt < TimeSpan.FromMinutes(1))).ToList().AsReadOnly();
    }

    /// <summary>
    /// Clear all subscriptions (e.g., on server shutdown).
    /// </summary>
    public void ClearAllSubscriptions()
    {
        var count = _subscriptions.Count;
        _subscriptions.Clear();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Cleared {SubscriptionCount} subscriptions", count);
        }
    }

    private async Task SendEventToSubscriptionAsync(DebuggerStreamSubscription subscription, DebuggerEvent evt)
    {
        try
        {
            await subscription.OnEventAsync(evt);

            lock (_broadcastLock)
            {
                _subscriptions[subscription.SubscriptionId] = subscription with
                {
                    LastEventSentAt = DateTime.UtcNow,
                    EventsCount = subscription.EventsCount + 1
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error sending event to subscription {SubscriptionName}: {Message}",
                subscription.SubscriptionName,
                ex.Message);

            // Remove subscription on error (e.g., client disconnected)
            RemoveSubscription(subscription.SubscriptionId);
        }
    }
}

/// <summary>Internal representation of an SSE subscription.</summary>
internal sealed record DebuggerStreamSubscription(
    string SubscriptionId,
    string SubscriptionName,
    Func<DebuggerEvent, Task> OnEventAsync,
    Func<DebuggerEvent, bool>? Filter,
    DateTime CreatedAt,
    DateTime? LastEventSentAt,
    int EventsCount);

/// <summary>Statistics about active event streams.</summary>
internal sealed record DebuggerStreamStats(
    int TotalSubscriptions,
    int ActiveSubscriptions,
    int IdleSubscriptions,
    long TotalEventsSent,
    DateTime? OldestSubscription,
    DateTime? MostRecentEvent);

/// <summary>Information about a single stream subscription for monitoring.</summary>
internal sealed record DebuggerStreamSubscriptionInfo(
    string SubscriptionId,
    string SubscriptionName,
    DateTime CreatedAt,
    DateTime? LastEventSentAt,
    int EventsCount,
    bool HasFilter,
    bool IsActive);
