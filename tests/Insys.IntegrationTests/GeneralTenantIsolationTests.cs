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
using System.Net.Http.Json;
using Insys.General.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// Tenant isolation through the General module's API (SRS §27.4, TEN-002, TEN-004): another tenant's risk objects,
/// quotes, policies and claims are neither read nor changed, and look as if they do not exist. The host connects as the
/// non-owner role, so row-level security is the last line of defence.
/// </summary>
public sealed class GeneralTenantIsolationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly string[] All =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.RiskUpdate, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteUpdate, GeneralPermissions.PolicyRead,
        GeneralPermissions.PolicyCancel, GeneralPermissions.EndorsementCreate, GeneralPermissions.ClaimRead, GeneralPermissions.ClaimCreate, GeneralPermissions.ClaimUpdate,
        GeneralPermissions.ClaimDecide,
    ];

    private InsysApiFactory _factory = null!;
    private Guid _risk;
    private Guid _quote;
    private Guid _policy;
    private Guid _claim;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "general-a", All);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "general-none");
        (_risk, _quote, _policy, _claim) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        const string audit = "row_version, created_at, created_by, updated_at, updated_by";
        const string stamp = "1, now(), 'seed', now(), 'seed'";
        await fixture.Database.ExecuteAsOwner($"""
            INSERT INTO general.risk_object (tenant_id, id, {audit}, object_type, attributes, attribute_schema_version, status)
            VALUES (@b, @r, {stamp}, 'VEHICLE', jsonb_build_object(), 1, 'ACTIVE');
            INSERT INTO general.quote (tenant_id, id, {audit}, quote_number, quote_version, product_code, product_version, proposer_party_id, status, valid_until, currency)
            VALUES (@b, @q, {stamp}, @qn, 1, 'MOTOR', 1, gen_random_uuid(), 'DRAFT', current_date + 30, 'ZAR');
            INSERT INTO general.policy (tenant_id, id, {audit}, policy_number, product_code, product_version, rating_version, policyholder_party_id, status,
                                        period_start, period_end, currency, annual_premium, billing_account_ref, cover_starts_on)
            VALUES (@b, @p, {stamp}, @pn, 'MOTOR', 1, 1, gen_random_uuid(), 'IN_FORCE', current_date - 10, current_date + 355, 'ZAR', 1200, gen_random_uuid(), 'ISSUE');
            INSERT INTO general.claim (tenant_id, id, {audit}, policy_id, loss_date, notified_at, cause_code, peril_code, status, coverage_code)
            VALUES (@b, @c, {stamp}, @p, current_date, now(), 'COLLISION', 'ACC', 'FIRST_NOTIFICATION', 'OWN_DAMAGE');
            """, ("b", DatabaseFixture.TenantB), ("r", _risk), ("q", _quote), ("p", _policy), ("c", _claim),
            ("qn", "GQ-" + _quote.ToString("N")[..12]), ("pn", "GP-" + _policy.ToString("N")[..12]));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnotherTenantsRecords_AreNotFound_TEN002()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "general-a");

        foreach (var url in new[]
                 {
                     $"/api/v1/general/risk-objects/{_risk}", $"/api/v1/general/quotes/{_quote}", $"/api/v1/general/policies/{_policy}", $"/api/v1/general/claims/{_claim}",
                     $"/api/v1/general/claims/{_claim}/assessments",
                 })
        {
            var response = await client.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, url); // a claim's assessments too, since BR-034 (it was an empty list)
        }
    }

    [Fact]
    public async Task AnotherTenantsRecords_AreNotChanged_TEN004()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "general-a");

        (await client.PostAsync($"/api/v1/general/risk-objects/{_risk}/deactivate", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/v1/general/quotes/{_quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/v1/general/policies/{_policy}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = TestClock.Today }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity);
        (await client.PostAsync($"/api/v1/general/claims/{_claim}/register", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.Send(HttpMethod.Post, "/api/v1/general/claims", new
        {
            policyId = _policy, coverageCode = "OWN_DAMAGE", lossDate = TestClock.Today, causeCode = "COLLISION", perilCode = "ACC",
        }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM general.policy WHERE id = @p", ("p", _policy))).ShouldBe("IN_FORCE");
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM general.risk_object WHERE id = @r", ("r", _risk))).ShouldBe("ACTIVE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM general.claim WHERE tenant_id = @a", ("a", DatabaseFixture.TenantA))).ShouldBe(0L);
    }

    [Fact]
    public async Task WithoutPermission_IsForbidden()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "general-none");

        (await client.GetAsync($"/api/v1/general/policies/{_policy}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/v1/general/claims/{_claim}/register", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ModuleRole_SeesOnlyItsTenantsRows_IncludingQuotedRisks()
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.Database.ConnectionFor("insys_general"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new Npgsql.NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", DatabaseFixture.TenantA.ToString());
            await set.ExecuteScalarAsync();
        }

        foreach (var table in new[] { "risk_object", "quote", "quote_risk", "quote_coverage", "policy", "claim" })
        {
            await using var count = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM general.{table} WHERE tenant_id <> @t", connection, transaction);
            count.Parameters.AddWithValue("t", DatabaseFixture.TenantA);
            ((long)(await count.ExecuteScalarAsync())!).ShouldBe(0L, table);
        }
    }
}
