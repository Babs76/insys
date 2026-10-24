/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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

    /// <summary>
    /// /health reports outbox lag, Degraded then Unhealthy, for alerting; readiness stays OK throughout (GAP-034), since
    /// a stuck event is a messaging problem, not a reason to stop serving requests.
    /// </summary>
    [Fact]
    public async Task Health_ReportsOutboxLag_DegradedThenUnhealthy_WhileReadinessStaysOk_GAP034()
    {
        var client = _factory.CreateClient();

        // Events the host raised itself at start-up (its jobs' audit entries) are published first: the relay is off in tests,
        // and under a loaded run they can pass the one-minute warning before this test reads /health.
        await _factory.DrainOutbox();
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
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

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
        Latency.Note($"Cold start: router built {warmup.TotalMilliseconds:F0} ms after host start; test host start to ready {_factory.ColdStart?.TotalMilliseconds:F0} ms");
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
