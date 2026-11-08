using System.Net;
using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Covers the two ways TokenSaver.MCP's /health can report degraded status: an unhandled
///     exception recorded by FaultTrackingMiddleware/UnhandledFaultTracker, and a failing
///     SelfTestHealthCheck check. Mirrors DraftModel.Loader.Tests's DegradedHealthTests — same
///     two mechanisms, same "shouldn't affect /health/ready" boundary, adapted to this project's
///     ASP.NET Core health-checks-based architecture instead of a hand-rolled HttpListener.
///     Note: unlike DraftModel's router, ASP.NET Core's HealthCheckMiddleware maps
///     HealthStatus.Degraded to HTTP 200 by default (only Unhealthy maps to 503) — these tests
///     assert on the JSON body's "status" field rather than expecting a non-200 status code.
/// </summary>
public sealed class DegradedHealthTests
{
    [Fact]
    public async Task Baseline_Health_IsHealthy()
    {
        await using HealthTestHost host = HealthTestHost.Create();

        using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute));
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnhandledExceptionInRequest_MakesHealthDegraded()
    {
        await using HealthTestHost host = HealthTestHost.Create();

        // /throw triggers a real unhandled exception through FaultTrackingMiddleware — the exact
        // same code path a genuinely-broken MCP tool call or any other request would hit, not a
        // test-only injection hook.
        await Assert.ThrowsAnyAsync<Exception>(() => host.Client.GetAsync(new Uri("/throw", UriKind.RelativeOrAbsolute)));

        Assert.True(host.FaultTracker.HasRecentFault);
        Assert.Equal("simulated tool failure", host.FaultTracker.LastFaultMessage);

        using HttpResponseMessage healthResponse = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute));
        JsonDocument doc = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());

        Assert.Equal("Degraded", doc.RootElement.GetProperty("status").GetString());

        JsonElement faultCheck = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "unhandled-fault");
        Assert.Equal("Degraded", faultCheck.GetProperty("status").GetString());
        Assert.Contains("simulated tool failure", faultCheck.GetProperty("description").GetString(), StringComparison.Ordinal);

        // Fault tracking is untagged (not "ready"), so /health/ready must stay unaffected.
        using HttpResponseMessage readyResponse = await host.Client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute));
        JsonDocument readyDoc = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", readyDoc.RootElement.GetProperty("status").GetString());
        Assert.Empty(readyDoc.RootElement.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task FailingSelfTestCheck_MakesHealthDegraded_WithoutAffectingHealthReady()
    {
        await using HealthTestHost host = HealthTestHost.Create();
        host.SelfTestCheck.ExtraChecks.Add(("always-fails", () => false));

        using HttpResponseMessage healthResponse = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute));
        JsonDocument doc = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());

        Assert.Equal("Degraded", doc.RootElement.GetProperty("status").GetString());
        JsonElement selfTestCheck = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "self-test");
        Assert.Equal("Degraded", selfTestCheck.GetProperty("status").GetString());

        using HttpResponseMessage readyResponse = await host.Client.GetAsync(new Uri("/health/ready", UriKind.RelativeOrAbsolute));
        JsonDocument readyDoc = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", readyDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SelfTestCheck_ThrowingException_IsCaughtAndReportedAsDegraded()
    {
        await using HealthTestHost host = HealthTestHost.Create();
        host.SelfTestCheck.ExtraChecks.Add(("throws", () => throw new InvalidOperationException("boom")));

        using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute));

        // A throwing self-test check must not itself surface as a 500 to the /health caller.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Degraded", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FaultTracker_ClearFault_RestoresHealthyStatus()
    {
        await using HealthTestHost host = HealthTestHost.Create();
        host.FaultTracker.RecordFault(new InvalidOperationException("temporary"));

        using (HttpResponseMessage degraded = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute)))
        {
            JsonDocument degradedDoc = JsonDocument.Parse(await degraded.Content.ReadAsStringAsync());
            Assert.Equal("Degraded", degradedDoc.RootElement.GetProperty("status").GetString());
        }

        host.FaultTracker.ClearFault();

        using HttpResponseMessage healthy = await host.Client.GetAsync(new Uri("/health", UriKind.RelativeOrAbsolute));
        JsonDocument healthyDoc = JsonDocument.Parse(await healthy.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", healthyDoc.RootElement.GetProperty("status").GetString());
    }
}
