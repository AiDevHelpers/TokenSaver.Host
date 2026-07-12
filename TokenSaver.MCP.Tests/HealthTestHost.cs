using TokenSaver.Common.Core.Configuration;
using TokenSaver.Library.Services;
using TokenSaver.MCP.HealthChecks;
using TokenSaver.MCP.Middleware;
using TokenSaver.MCP.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Minimal, purpose-built ASP.NET Core test host for the degraded-health/self-test feature —
///     deliberately does NOT reuse the real Program.cs composition root, which wires up ~20
///     singletons, background index warm-up, and real DraftModel connectivity waits that would
///     make these tests slow and flaky for something that's really just "does the fault-tracking
///     middleware + these two health checks behave correctly". Wires only what that needs:
///     <see cref="FaultTrackingMiddleware" />, <see cref="UnhandledFaultHealthCheck" />,
///     <see cref="SelfTestHealthCheck" />, the real /health and /health/ready mappings (same
///     shape as Program.cs), and a throw-on-demand test endpoint to trigger real unhandled
///     exceptions the same way a real MCP tools/call failure would.
/// </summary>
internal sealed class HealthTestHost : IAsyncDisposable
{
    public TestServer Server { get; }
    public HttpClient Client { get; }
    public UnhandledFaultTracker FaultTracker { get; }
    public SelfTestHealthCheck SelfTestCheck { get; }

    private HealthTestHost(TestServer server)
    {
        Server = server;
        Client = server.CreateClient();
        FaultTracker = server.Services.GetRequiredService<UnhandledFaultTracker>();

        // Resolved as the concrete singleton type directly (not via GetServices<IHealthCheck>() —
        // AddCheck<T> wraps checks in HealthCheckRegistration objects rather than exposing them
        // as resolvable IHealthCheck services), which is exactly the same instance AddCheck<T>
        // picked up via ActivatorUtilities.GetServiceOrCreateInstance in Create() below.
        SelfTestCheck = server.Services.GetRequiredService<SelfTestHealthCheck>();
    }

    public static HealthTestHost Create()
    {
        IWebHostBuilder builder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging(l => l.ClearProviders());
                services.AddSingleton<IOptions<TokenSaverConfig>>(
                    Microsoft.Extensions.Options.Options.Create(new TokenSaverConfig()));
                services.AddSingleton<OllamaEndpointRegistry>();
                services.AddSingleton<UnhandledFaultTracker>();

                // Registered as singletons ourselves (rather than relying on AddHealthChecks()
                // .AddCheck<T>()'s default transient-or-existing resolution) so the exact same
                // instance the health check middleware queries is also the one HealthTestHost's
                // constructor resolves to mutate SelfTestHealthCheck.ExtraChecks — a second,
                // separately-constructed instance would silently ignore that mutation.
                services.AddSingleton<UnhandledFaultHealthCheck>();
                services.AddSingleton<SelfTestHealthCheck>();

                services.AddRouting();

                services.AddHealthChecks()
                    .AddCheck<UnhandledFaultHealthCheck>("unhandled-fault")
                    .AddCheck<SelfTestHealthCheck>("self-test");
            })
            .Configure(app =>
            {
                app.UseMiddleware<FaultTrackingMiddleware>();

                app.Map("/throw", branch => branch.Run(_ => Task.FromException(new InvalidOperationException("simulated tool failure"))));

                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    // Same JSON ResponseWriter Program.cs uses for both routes, so tests parse
                    // the exact shape a real client sees rather than the framework's bare
                    // plaintext default.
                    endpoints.MapHealthChecks("/health", new HealthCheckOptions
                    {
                        ResponseWriter = WriteJsonHealthResponse
                    });
                    endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
                    {
                        Predicate = check => check.Tags.Contains("ready"),
                        ResponseWriter = WriteJsonHealthResponse
                    });
                });
            });

        TestServer server = new(builder);
        return new HealthTestHost(server);
    }

    // Mirrors Program.cs's WriteJsonHealthResponse exactly, so tests exercise the same response
    // shape a real client gets rather than a parallel reimplementation of it.
    private static async Task WriteJsonHealthResponse(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var result = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message,
                data = e.Value.Data.Count > 0 ? e.Value.Data : null
            }),
            totalDuration = report.TotalDuration.TotalMilliseconds
        };
        await ctx.Response.WriteAsJsonAsync(result);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Server.Dispose();
        await ValueTask.CompletedTask;
    }
}
