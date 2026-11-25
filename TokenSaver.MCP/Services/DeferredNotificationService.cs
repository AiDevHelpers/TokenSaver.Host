using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Thread-safe queue for messages and questions that background services need to
///     surface to the user. Background services (e.g. ProviderAutoRegistrationService)
///     have no active MCP session to reply through, so they post here instead.
///     The next tool call that reaches an active client drains the queue and prepends
///     the content to its response.
///     Two kinds of items:
///     Notification — informational (e.g. "Registered with Continue at ~/.continue/…")
///     Question     — requires a follow-up action (e.g. "Found new providers — call X to register")
///     Use Post() for fire-and-forget info; PostQuestion() when you want the user to act.
/// </summary>
internal sealed class DeferredNotificationService
{
    internal enum ItemKind
    {
        Notification,
        Question
    }

    private readonly ConcurrentQueue<DeferredItem> _queue = new();

    // ── Read ──────────────────────────────────────────────────────────────────

    public bool HasPending => !_queue.IsEmpty;

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Queues an informational message to surface on the next tool call.</summary>
    public void Post(string message)
    {
        _queue.Enqueue(new DeferredItem(ItemKind.Notification, message));
    }

    /// <summary>
    ///     Queues a question that requires user action.
    ///     Include the tool call the user should make to answer in the message text.
    /// </summary>
    public void PostQuestion(string question)
    {
        _queue.Enqueue(new DeferredItem(ItemKind.Question, question));
    }

    /// <summary>
    ///     Removes and returns all pending items. Call from a tool handler that has
    ///     an active client session so the content actually reaches the user.
    /// </summary>
    public IReadOnlyList<DeferredItem> Drain()
    {
        List<DeferredItem> items = [];
        while (_queue.TryDequeue(out DeferredItem? item))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    ///     Formats drained items into a block ready to prepend to a tool response.
    ///     Returns an empty string if the queue was empty.
    /// </summary>
    public string DrainFormatted()
    {
        IReadOnlyList<DeferredItem> items = Drain();
        if (items.Count == 0)
        {
            return "";
        }

        StringBuilder sb = new();
        _ = sb.AppendLine("---");

        foreach (DeferredItem item in items)
        {
            string prefix = item.Kind == ItemKind.Question ? "❓" : "ℹ️";
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix} {item.Message}");
        }

        _ = sb.AppendLine("---");
        return sb.ToString();
    }

    internal sealed record DeferredItem(ItemKind Kind, string Message);
}
