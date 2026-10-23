/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Testing;

/// <summary>The real API host (Program) against a migrated test database, connecting as the non-owner role, with any extra settings.</summary>
/// <param name="environment">The host environment; Testing unless a test checks environment-specific behaviour.</param>
/// <param name="waitForWarmup">False only for tests of requests that arrive while the endpoints are still being built.</param>
public sealed class InsysApiFactory(TestDatabase database, IReadOnlyDictionary<string, string?>? settings = null, string environment = "Testing",
    bool waitForWarmup = true) : WebApplicationFactory<Program>
{
    public TestDatabase Database { get; } = database;

    /// <summary>
    /// Starts the host and waits for its endpoint warm-up before any client is made, as readiness makes production wait:
    /// otherwise the warm-up and the first request build the same endpoints at the same time.
    /// </summary>
    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var host = base.CreateHost(builder);
        if (waitForWarmup)
        {
            host.Services.GetRequiredService<Insys.BuildingBlocks.Api.EndpointWarmup>().Completed.GetAwaiter().GetResult();
        }

        ColdStart = stopwatch.Elapsed;
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(TestTokens.HostSettings(Database)).AddInMemoryCollection(settings ?? new Dictionary<string, string?>()));
    }

    /// <summary>A client whose token carries only sub and tenant_id.</summary>
    public HttpClient ClientFor(Guid? tenantId, string subject)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(tenantId, subject));
        return client;
    }

    /// <summary>Publishes every pending outbox event to its consumers now (the relay is off in tests).</summary>
    /// <summary>Relays outbox events until none are left, so chains of events (a consumer publishing in turn) complete.</summary>
    public async Task<int> DrainOutbox()
    {
        var processor = Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>();
        var total = 0;
        for (var pass = 0; pass < 10; pass++)
        {
            var relayed = await processor.ProcessOnce(CancellationToken.None);
            if (relayed == 0)
            {
                break;
            }

            total += relayed;
        }

        return total;
    }

    /// <summary>Starts the host and waits until every endpoint is built (EndpointWarmup), so no test request pays for it.</summary>
    public async Task<InsysApiFactory> Warm()
    {
        _ = Server;
        await Services.GetRequiredService<Insys.BuildingBlocks.Api.EndpointWarmup>().Completed;
        return this;
    }

    /// <summary>How long the host took from start until its endpoints were built.</summary>
    public TimeSpan? ColdStart { get; private set; }

    /// <summary>
    /// Returns a shared host to the state a fresh one has between tests: in-memory caches (permissions, rating, languages,
    /// messages) emptied, and the stub webhook transport's recorded requests cleared.
    /// </summary>
    public void ResetState()
    {
        (Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>() as Microsoft.Extensions.Caching.Memory.MemoryCache)?.Clear();
        Services.GetService<Insys.Integration.Infrastructure.Webhooks.StubWebhookTransport>()?.Received.Clear();
    }

}
