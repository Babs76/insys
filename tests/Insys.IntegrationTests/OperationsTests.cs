using System.Net;
using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Insys.IntegrationTests;

/// <summary>Outbox lag in the health checks (review item 6) and the single-instance guard (ADR-006).</summary>
public sealed class OperationsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Readiness_ReportsOutboxLag_DegradedThenUnhealthy()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Lag(client)).GetProperty("status").GetString().ShouldBe("Healthy");

        var id = Guid.CreateVersion7();
        await InsertPending(id, TimeSpan.FromMinutes(2));
        (await Lag(client)).GetProperty("status").GetString().ShouldBe("Degraded");
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.Database.ExecuteAsOwner("UPDATE audit.outbox_event SET occurred_at = now() - interval '10 minutes' WHERE id = @id", ("id", id));
        var lag = await Lag(client);
        lag.GetProperty("status").GetString().ShouldBe("Unhealthy");
        lag.GetProperty("data").GetProperty("slowestOutbox").GetString().ShouldBe("audit");
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        await fixture.Database.ExecuteAsOwner("UPDATE audit.outbox_event SET published_at = now() WHERE id = @id", ("id", id));
        (await Lag(client)).GetProperty("status").GetString().ShouldBe("Healthy");
    }

    /// <summary>A new instance builds its endpoints at startup and is ready only then; the times are reported for the cold-start record.</summary>
    [Fact]
    public async Task Readiness_WaitsForTheEndpointWarmup_AndReportsItsDuration()
    {
        var warmup = await _factory.Services.GetRequiredService<EndpointWarmup>().Completed;
        var check = (await (await _factory.CreateClient().GetAsync("/health")).Json()).GetProperty("checks").GetProperty("endpoints");

        check.GetProperty("status").GetString().ShouldBe("Healthy");
        check.GetProperty("data").GetProperty("warmupMilliseconds").GetDouble().ShouldBe(Math.Round(warmup.TotalMilliseconds));
        (await new EndpointWarmupHealthCheck(new EndpointWarmup()).CheckHealthAsync(null!)).Status.ShouldBe(HealthStatus.Unhealthy);
        Latency.Note($"Cold start: endpoints built {warmup.TotalMilliseconds:F0} ms after host start; test host start to ready {_factory.ColdStart?.TotalMilliseconds:F0} ms "
            + $"({_factory.EndpointCount} endpoints)");
    }

    [Fact]
    public async Task SecondApiInstance_OnTheSameDatabase_RefusesToStart_ADR006()
    {
        _ = _factory.CreateClient();

        await using var second = new InsysApiFactory(fixture.Database);
        var error = Should.Throw<InvalidOperationException>(() => second.CreateClient());
        error.Message.ShouldContain("Another INSYS API instance");

        await using var allowed = new MultiInstanceFactory(fixture.Database);
        (await allowed.CreateClient().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> Lag(HttpClient client) =>
        (await (await client.GetAsync("/health")).Json()).GetProperty("checks").GetProperty("outbox-lag");

    private Task InsertPending(Guid id, TimeSpan age) => fixture.Database.ExecuteAsOwner("""
        INSERT INTO audit.outbox_event (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, aggregate_type, aggregate_id,
                                        event_type, event_version, payload, occurred_at, correlation_id, publish_attempts)
        VALUES (@t, @id, 1, now(), 'test', now(), 'test', 'Test', gen_random_uuid(), 'test.Nothing', 1, '{}', now() - @age, gen_random_uuid(), 0)
        """, ("t", DatabaseFixture.TenantA), ("id", id), ("age", age));

    private sealed class MultiInstanceFactory(TestDatabase database) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(TestTokens.HostSettings(database));
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:PermissionCache:AllowMultipleInstances"] = "true" });
            });
        }
    }
}
