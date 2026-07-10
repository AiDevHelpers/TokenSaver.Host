using TokenSaver.Library.Configuration;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Options;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Background service that periodically pings every configured Ollama endpoint's
///     <c>/api/tags</c> (a cheap, always-available Ollama route) and updates
///     <see cref="OllamaEndpointRegistry" /> health state accordingly. Logs healthy&lt;-&gt;unhealthy
///     transitions (the registry itself logs the transition; this service just drives the polling
///     loop). Interval is configurable via <see cref="TokenSaverConfig.HealthCheckInterval" />
///     (default 30s).
/// </summary>
internal sealed partial class EndpointHealthMonitor(
    OllamaEndpointRegistry registry,
    IHttpClientFactory httpClientFactory,
    IOptions<TokenSaverConfig> opts,
    ILogger<EndpointHealthMonitor> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "EndpointHealthMonitor starting — probing every {Interval}.")]
    private static partial void LogStarting(ILogger logger, TimeSpan interval);

    // Deliberately logs ex.Message, not the Exception itself — a probe failure here is a routine,
    // expected condition (endpoint down, connection refused) that recurs every interval while an
    // endpoint stays unreachable, not an unexpected fault. Passing the full Exception to
    // [LoggerMessage] dumps its entire stack trace on every occurrence, which is pure noise for
    // something this routine; OllamaEndpointRegistry.SetHealth already logs the health-state
    // transition itself, so nothing meaningful is lost by not repeating the stack trace here.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Health probe failed for endpoint '{Name}' ({Url}): {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, string name, string url, string reason);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = opts.Value.HealthCheckInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromSeconds(30);
        }

        LogStarting(logger, interval);

        using PeriodicTimer timer = new(interval);
        try
        {
            do
            {
                await ProbeAllAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProbeAllAsync(CancellationToken ct)
    {
        IReadOnlyList<OllamaEndpointStatus> endpoints = registry.GetAllEndpoints();
        if (endpoints.Count == 0)
        {
            return;
        }

        using HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = ProbeTimeout;

        // CA2025 false positive: `tasks` is materialized (ToList) and fully awaited via
        // Task.WhenAll before this method returns and `client` is disposed at scope exit, so
        // there's no risk of the HttpClient being disposed while a probe is still in flight.
        // The analyzer can't see through the deferred LINQ `Select` + `using` combination.
#pragma warning disable CA2025
        List<Task> tasks = [.. endpoints.Select(endpoint => ProbeOneAsync(client, endpoint.Name, endpoint.Url, ct))];
#pragma warning restore CA2025
        await Task.WhenAll(tasks);
    }

    private async Task ProbeOneAsync(HttpClient client, string name, string url, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            using HttpResponseMessage response = await client.GetAsync(new Uri(new Uri(url), "/api/tags"), cts.Token);
            registry.SetHealth(name, response.IsSuccessStatusCode);
        }
        catch (Exception ex)
        {
            LogProbeFailed(logger, name, url, ex.Message);
            registry.SetHealth(name, false);
        }
    }
}
