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

    /// <summary>Scope batch 3 (read sweep, Party's policy export): an export holds only the policies its reader's scope allows, filtered in the query.</summary>
    [Fact]
    public async Task APolicyExport_HoldsOnlyThePoliciesItsReadersScopeAllows_F103()
    {
        var suffix = Random.Shared.Next(100_000, 999_999);
        var (inside, outside) = ($"RI{suffix}", $"RO{suffix}");
        await _rows.Insert(T, "general.policy", ("product_code", "RS-GEN"), ("policy_number", inside), ("status", "IN_FORCE"));
        await _rows.Insert(T, "general.policy", ("product_code", "RS-GEN-OTHER"), ("policy_number", outside), ("status", "IN_FORCE"));

        var scoped = await Scoped("rs-exporter", "RS-GEN", Insys.Party.Contracts.PartyPermissions.BulkJobCreate, Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        await fixture.Database.UserWithPermissions(T, "rs-export-checker", Insys.Party.Contracts.PartyPermissions.BulkJobApprove,
            Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        await fixture.Database.UserWithPermissions(T, "rs-export-all", Insys.Party.Contracts.PartyPermissions.BulkJobRead);
        var requested = await scoped.Send(HttpMethod.Post, "/api/v1/party/bulk-jobs/policy-exports", new { domain = "GENERAL" }, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var job = (await requested.Json()).GetProperty("id").GetGuid();
        var checker = _factory.ClientFor(T, "rs-export-checker");
        var etag = (await checker.GetAsync($"/api/v1/party/bulk-jobs/{job}")).Headers.ETag?.Tag;
        var approved = await checker.Send(HttpMethod.Post, $"/api/v1/party/bulk-jobs/{job}/approve", ifMatch: etag);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        var mine = await (await scoped.GetAsync($"/api/v1/party/bulk-jobs/{job}/content")).Content.ReadAsStringAsync();
        mine.ShouldContain(inside);
        mine.ShouldNotContain(outside, Case.Sensitive, "a policy outside the reader's scope isn't exported");

        var all = await (await _factory.ClientFor(T, "rs-export-all").GetAsync($"/api/v1/party/bulk-jobs/{job}/content")).Content.ReadAsStringAsync();
        all.ShouldContain(outside, Case.Sensitive, "an unrestricted reader gets every policy");
    }

    private async Task<HttpClient> Scoped(string subject, string product, params string[] permissions)
    {
        var user = await fixture.Database.UserWithPermissions(T, subject, permissions);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", product), ("u", user));
        return _factory.ClientFor(T, subject);
    }
}
