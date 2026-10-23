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
using System.Text;
using System.Text.Json;
using Insys.Analytics.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Compliance.Contracts;
using Insys.Distribution.Contracts;
using Insys.Documents.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Contracts;
using Insys.Rating.Contracts;
using Insys.Reinsurance.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// SRS §27.4 for records: a user of tenant B, holding every permission involved, can neither read, find nor change
/// tenant A's records through the API (the later modules: parties and bank accounts, approvals, documents, commission,
/// reinsurance, returns, webhooks, projections); caches are kept per tenant; and an event in tenant B's envelope that
/// names tenant A's record changes nothing (TEN-004). The host connects as the non-owner role.
/// </summary>
public sealed class CrossTenantSuiteTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid A = DatabaseFixture.TenantA;
    private static readonly Guid B = DatabaseFixture.TenantB;

    private static readonly string[] Permissions =
    [
        PartyPermissions.PartyRead, PartyPermissions.PartyCreate, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, DocumentPermissions.DocumentRead, DocumentPermissions.DocumentCreate,
        DistributionPermissions.CommissionRead, DistributionPermissions.CommissionUpdate, ReinsurancePermissions.RecoveryRead, ReinsurancePermissions.RecoveryUpdate,
        CompliancePermissions.ReturnRead, CompliancePermissions.ReturnUpdate, CompliancePermissions.ReturnApprove, IntegrationPermissions.WebhookRead,
        IntegrationPermissions.WebhookUpdate, AnalyticsPermissions.ProjectionRead, AnalyticsPermissions.ProjectionUpdate, RatingPermissions.CalculationCreate,
        RatingPermissions.CalculationRead,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _a = null!;
    private HttpClient _b = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(A, "cross-a", Permissions);
        await fixture.Database.UserWithPermissions(B, "cross-b", Permissions);
        _a = _factory.ClientFor(A, "cross-a");
        _b = _factory.ClientFor(B, "cross-b");
        foreach (var tenant in new[] { A, B })
        {
            await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "NOTE", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
                ("category_code", "NOTE"), ("label", "Note"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnotherTenantsRecords_AreNeitherRead_NorFound_NorChanged_ThroughTheApi_TEN004()
    {
        // Tenant A's records: a party with a bank account waiting for verification (and its approval), a document,
        // and rows of each Phase 6 module.
        var party = await _a.RegisterPerson("TenantA");
        var added = await _a.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "123456789", bankCode = "250655", accountHolderName = "Tenant A", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var account = (await added.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        var approval = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE business_ref_id = @a", ("a", account));
        var stored = await _a.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "NOTE", businessRefType = "PARTY", businessRefId = party, mimeType = "text/plain", contentBase64 = Convert.ToBase64String("A's note"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        var document = (await stored.Json()).GetProperty("id").GetGuid();
        var (entitlement, recovery, regulatoryReturn, delivery) = await SeedPhase6Rows(A);

        // Tenant B reads none of them.
        foreach (var url in new[]
                 {
                     $"/api/v1/party/parties/{party}", $"/api/v1/party/parties/{party}/bank-accounts", $"/api/v1/documents/documents/{document}",
                     $"/api/v1/documents/documents/{document}/content", $"/api/v1/distribution/entitlements/{entitlement}", $"/api/v1/reinsurance/recoveries/{recovery}",
                     $"/api/v1/compliance/returns/{regulatoryReturn}", $"/api/v1/workflow/approvals/{approval}",
                 })
        {
            (await _b.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
        }

        // Nor finds them through lists and searches.
        foreach (var url in new[]
                 {
                     $"/api/v1/workflow/approvals?businessRefType=BANK_ACCOUNT&businessRefId={account}", $"/api/v1/distribution/entitlements?intermediaryCode=BRK-A",
                     "/api/v1/reinsurance/recoveries?claimNumber=GEN-A-1", "/api/v1/compliance/returns?periodCode=2026-09", "/api/v1/integration/webhook-deliveries",
                     $"/api/v1/documents/documents?businessRefType=PARTY&businessRefId={party}",
                     "/api/v1/analytics/written-premium?from=2026-01-01&to=2026-12-31",
                 })
        {
            var found = await (await _b.GetAsync(url)).Json();
            (found.ValueKind == JsonValueKind.Array ? found : found.GetProperty("items")).GetArrayLength().ShouldBe(0, url);
        }

        // Nor changes them: each change is not found, and A's rows are as they were.
        foreach (var (url, body) in new (string, object?)[]
                 {
                     ($"/api/v1/distribution/entitlements/{entitlement}/claw-back", new { reason = "CROSS_TENANT" }),
                     ($"/api/v1/reinsurance/recoveries/{recovery}/settle", new { amount = 1m, settledOn = "2026-10-01" }),
                     ($"/api/v1/compliance/returns/{regulatoryReturn}/review", null), ($"/api/v1/integration/webhook-deliveries/{delivery}/retry", null),
                     ($"/api/v1/party/parties/{party}/bank-accounts", new
                     {
                         accountNumber = "999999999", bankCode = "250655", accountHolderName = "B", accountType = "CURRENT", currency = "ZAR",
                     }),
                 })
        {
            var response = await _b.Send(HttpMethod.Post, url, body, idempotent: true);
            ((int)response.StatusCode).ShouldBeOneOf([404, 422], url);
        }

        (await _b.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _b.PostAsync("/api/v1/analytics/projections/rebuild", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM commission.commission_entitlement WHERE id = @i", ("i", entitlement))).ShouldBe("ACCRUED");
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM reinsurance.recovery WHERE id = @i", ("i", recovery))).ShouldBe("BILLED");
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM reporting.regulatory_return_run WHERE id = @i", ("i", regulatoryReturn))).ShouldBe("PREPARED");
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM workflow.approval WHERE id = @i", ("i", approval))).ShouldBe("REQUESTED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.bank_account WHERE party_id = @p", ("p", party))).ShouldBe(1L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM analytics.fact_written_premium WHERE tenant_id = @t", ("t", A))).ShouldBe(1L);

        // The attempts were denied in B's own tenant only; nothing about them was recorded in A's audit trail.
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND actor_id IN (SELECT id::text FROM iam.user_account WHERE external_subject = 'cross-b')",
            ("t", A))).ShouldBe(0L);
    }

    /// <summary>A person's permissions are resolved and cached per tenant: the same subject holds different permissions in each.</summary>
    [Fact]
    public async Task PermissionCache_IsKeptPerTenant_TEN004()
    {
        await fixture.Database.UserWithPermissions(A, "shared-subject", PartyPermissions.PartyRead, PartyPermissions.PartyCreate);
        await fixture.Database.UserWithPermissions(B, "shared-subject");
        var inA = _factory.ClientFor(A, "shared-subject");
        var inB = _factory.ClientFor(B, "shared-subject");
        var party = await inA.RegisterPerson("Cached");

        (await inA.GetAsync($"/api/v1/party/parties/{party}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await inB.GetAsync($"/api/v1/party/parties/{party}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await inA.GetAsync($"/api/v1/party/parties/{party}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Rating configuration is cached per tenant: the same product code is priced by each tenant's own tables.</summary>
    [Fact]
    public async Task RatingCache_IsKeptPerTenant_TEN004()
    {
        await RatingProduct(A, 0.03m);
        await RatingProduct(B, 0.05m);

        (await Premium(_a)).ShouldBe(3_050m);
        (await Premium(_b)).ShouldBe(5_050m);
        (await Premium(_a)).ShouldBe(3_050m);
    }

    /// <summary>A consumer acts in its envelope's tenant only: tenant B's approval of tenant A's bank account changes nothing.</summary>
    [Fact]
    public async Task EventConsumers_ActInTheEnvelopesTenantOnly_TEN004()
    {
        var party = await _a.RegisterPerson("Consumer");
        var added = await _a.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "555444333", bankCode = "250655", accountHolderName = "Tenant A", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        var account = (await added.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        var approval = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE business_ref_id = @a", ("a", account));
        var hash = await fixture.Database.ScalarAsOwner<string>("SELECT subject_hash FROM workflow.approval WHERE id = @i", ("i", approval));

        var granted = new ApprovalGranted(approval, "BANK_ACCOUNT", account, "BANK_ACCOUNT_VERIFICATION", Guid.CreateVersion7(), Guid.CreateVersion7(), hash);
        await _factory.Services.GetRequiredService<IEventBus>().Publish(new EventEnvelope(Guid.CreateVersion7(), B, "workflow.ApprovalGranted", 1, DateTimeOffset.UtcNow,
            "Approval", approval, Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(granted, ModuleDbContext.EventJson)), CancellationToken.None);

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM party.bank_account WHERE id = @a", ("a", account))).ShouldBe("PENDING_VERIFICATION");
    }

    private async Task<(Guid Entitlement, Guid Recovery, Guid Return, Guid Delivery)> SeedPhase6Rows(Guid tenant)
    {
        var (entitlement, recovery, regulatoryReturn, delivery) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO commission.commission_entitlement (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, intermediary_code, source_type,
                source_id, policy_ref, plan_code, plan_version, basis_amount, rate, amount, currency, status, product_code, source_domain, due_date)
            VALUES (@t, @e, 1, now(), 'seed', now(), 'seed', 'BRK-A', 'INVOICE', gen_random_uuid(), gen_random_uuid(), 'PLAN-A', 1, 1000, 10, 100, 'ZAR', 'ACCRUED',
                    'MOTOR', 'GENERAL', current_date);
            INSERT INTO reinsurance.recovery (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, claim_number, treaty_code, recoverable, currency,
                status, source_domain, claim_id, policy_ref, paid_amount, billed_on)
            VALUES (@t, @r, 1, now(), 'seed', now(), 'seed', 'GEN-A-1', 'QS-A', 700, 'ZAR', 'BILLED', 'GENERAL', gen_random_uuid(), gen_random_uuid(), 1000, current_date);
            INSERT INTO reporting.regulatory_return_run (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, return_code, period_code, status,
                field_values)
            VALUES (@t, @g, 1, now(), 'seed', now(), 'seed', 'RET-A', '2026-09', 'PREPARED', '{"gross_premium": 1}');
            INSERT INTO integration.webhook_delivery (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, subscription_id, event_id, attempt, status,
                next_attempt_at, event_type, payload)
            VALUES (@t, @w, 1, now(), 'seed', now(), 'seed', 'WH-A', gen_random_uuid(), 0, 'PENDING', now(), 'general.GeneralPolicyIssued', '{}');
            INSERT INTO analytics.fact_written_premium (tenant_id, refreshed_at, date_key, product_code, channel_code, branch_code, policy_count, written_premium, currency)
            VALUES (@t, now(), '2026-10-01', 'MOTOR', 'DIRECT', 'ALL', 1, 1000, 'ZAR');
            """, ("t", tenant), ("e", entitlement), ("r", recovery), ("g", regulatoryReturn), ("w", delivery));
        return (entitlement, recovery, regulatoryReturn, delivery);
    }

    /// <summary>A General motor product rated at sum insured × the tenant's own factor, plus a fee of 50.</summary>
    private async Task RatingProduct(Guid tenant, decimal factor)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), 'X-MOT', 1, @from, 'PUBLISHED', 'seed', 'published', 'X-MOT', 'GENERAL', 'Motor', ARRAY['OWN_DAMAGE'], 'GEN-MOTOR', 'MOTOR', 'ISSUE');
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'XMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'XMOT-BASE', ARRAY['VEHICLE_GROUP'], @values::jsonb, 'NONE')
            """, ("t", tenant), ("from", from), ("values", $$"""[{"key":["G3"],"value":{{factor}}}]"""));
        await fixture.Database.SeedStatic(tenant, "rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", from, ("factor_code", "VEHICLE_GROUP"),
            ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await fixture.Database.SeedStatic(tenant, "rating.cfg_premium_parameters", "EFFECTIVE", "XMOT-P1", from, ("parameter_code", "XMOT-BASE"),
            ("product_version", "X-MOT v1"), ("value", 1m), ("unit", "FACTOR"));
        await fixture.Database.SeedStatic(tenant, "rating.cfg_premium_parameters", "EFFECTIVE", "XMOT-P2", from, ("parameter_code", "POLICY_FEE"),
            ("product_version", "X-MOT v1"), ("value", 50m), ("unit", "AMOUNT"));
        await fixture.Database.SeedStatic(tenant, "rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", from, ("rule_code", "ZAR-2DP"),
            ("currency", "ZAR"), ("step", 0.01m), ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));
    }

    private static async Task<decimal> Premium(HttpClient client)
    {
        var response = await client.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"X-{Guid.CreateVersion7():N}"[..20], sourceDomain = "GENERAL", subjectType = "GENERAL_QUOTE", subjectId = Guid.CreateVersion7(), productCode = "X-MOT",
            productVersion = 1, currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = 100_000m, vehicle_group = "G3" },
        }, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("premium").GetDecimal();
    }
}
