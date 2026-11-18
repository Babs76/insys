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
using Insys.Health.Contracts;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// F103 (addenda/access-scopes.md §1), the read-scope sweep's fix batches from 2 on: a record's child records are read only within the parent's scope, and outside
/// it the parent isn't found. Each caller here holds one product.
/// </summary>
public sealed class ReadScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Scope batch 2 (read sweep row 1): a membership's benefit accumulators are read only within the membership's scope.</summary>
    [Fact]
    public async Task AMembershipsAccumulators_AreReadOnlyWithinItsScope_OutsideItIsNotFound_F103()
    {
        var client = await Scoped("rs-health", "RS-HEALTH", HealthPermissions.AccumulatorRead, HealthPermissions.MembershipRead);
        var outside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "RS-OTHER"), ("member_number", $"RS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var inside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "RS-HEALTH"), ("member_number", $"RS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        foreach (var membership in new[] { outside, inside })
        {
            await _rows.Insert(T, "health.benefit_accumulator", ("membership_id", membership), ("benefit_category", "GP"));
        }

        var refused = await client.GetAsync($"/api/v1/health/memberships/{outside}/accumulators");
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a read outside the scope finds nothing (§1)");

        var read = await client.GetAsync($"/api/v1/health/memberships/{inside}/accumulators");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        (await read.Json()).GetArrayLength().ShouldBe(1);
    }

    private async Task<HttpClient> Scoped(string subject, string product, params string[] permissions)
    {
        var user = await fixture.Database.UserWithPermissions(T, subject, permissions);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", product), ("u", user));
        return _factory.ClientFor(T, subject);
    }
}
