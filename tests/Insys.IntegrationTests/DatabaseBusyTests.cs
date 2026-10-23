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
using Insys.Integration.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// GAP-039: when no pooled connection is free in time, concurrent requests get a retryable 503 DATABASE_BUSY with
/// Retry-After, never a 500, and the instance keeps answering once connections are free again.
/// </summary>
public sealed class DatabaseBusyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ConcurrentRequests_WithThePoolExhausted_Get503WithRetryAfter_ThenSucceed_GAP039()
    {
        var factory = await fixture.Host(new Dictionary<string, string?> { ["Database:Pools:insys_integration"] = "1", ["Database:PoolWaitSeconds"] = "1" });
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "busy-reader", IntegrationPermissions.DeadLetterRead);
        var client = factory.ClientFor(DatabaseFixture.TenantA, "busy-reader");

        // Hold the Integration pool's only connection.
        var dataSource = factory.Services.GetRequiredKeyedService<NpgsqlDataSource>("integration");
        var held = await dataSource.OpenConnectionAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/v1/integration/dead-letters")));
        await held.DisposeAsync();

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        foreach (var response in responses)
        {
            (await response.Code()).ShouldBe("DATABASE_BUSY");
            response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(2));
        }

        // Once the connection is back, the same burst succeeds, queued on the one connection.
        var after = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/v1/integration/dead-letters")));
        after.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
    }
}
