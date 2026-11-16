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
using Insys.ClaimsFramework.Contracts;
using Insys.General.Contracts;
using Insys.Health.Contracts;
using Insys.Payments.Contracts;
using Insys.Rating.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// F103 (addenda/access-scopes.md §1, §2), the write sweep's batch 1: a change is refused with OUT_OF_SCOPE when the record it changes, or the record a
/// new one is made against, is outside the caller's scope. Each caller here holds one product (and, where it says so, one branch).
/// </summary>
public sealed class WriteScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
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

    /// <summary>
    /// F103, OQ-176: resolving, reversing or allocating a payment needs its whole record in scope, and so does making one; a payment with no record is
    /// for unrestricted callers only. Allocation is refused before Billing plans over the account's invoices, so no other product's invoice is touched.
    /// </summary>
    [Fact]
    public async Task PaymentChanges_NeedThePaymentsRecordInScope_AndOneWithNoRecord_AnUnrestrictedCaller_F103_OQ176()
    {
        var client = await Scoped("ws-pay", "WS-HOME", PaymentPermissions.PaymentRead, PaymentPermissions.PaymentCreate, PaymentPermissions.PaymentResolve,
            PaymentPermissions.PaymentUpdate);
        var home = await Invoice("WS-HOME");
        var motor = await Invoice("WS-MOTOR");
        var outside = await Payment("SUCCEEDED", motor);
        var noRecord = await Payment("UNKNOWN", null);
        var inside = await Payment("SUCCEEDED", home);

        foreach (var (payment, action, body) in new (Guid, string, object?)[]
                 {
                     (outside, "allocate", null), (outside, "reverse", new { reason = "Duplicate" }), (noRecord, "resolve", new { succeeded = false, evidence = "Bank says no" }),
                     (noRecord, "reverse", new { reason = "Duplicate" }),
                 })
        {
            var refused = await client.Send(HttpMethod.Post, $"/api/v1/payments/payments/{payment}/{action}", body, ifMatch: "\"1\"");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{action}: {await refused.Content.ReadAsStringAsync()}");
            (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        }

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM payment.payment WHERE id = @p", ("p", outside))).ShouldBe("SUCCEEDED");
        var allowed = await client.Send(HttpMethod.Post, $"/api/v1/payments/payments/{inside}/allocate", ifMatch: "\"1\"");
        (await allowed.Code()).ShouldNotBe("OUT_OF_SCOPE", await allowed.Content.ReadAsStringAsync());

        var made = await client.Send(HttpMethod.Post, "/api/v1/payments/payments", new
        {
            direction = "In", purpose = "PREMIUM", methodCode = "EFT", amount = 100m, currency = "ZAR", businessRefType = "INVOICE", businessRefId = motor,
        }, idempotent: true);
        made.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await made.Content.ReadAsStringAsync());
        (await made.Code()).ShouldBe("OUT_OF_SCOPE");

        // Labelling it a commission payout doesn't lift the check: only a payout against a commission statement is outside branch scope (OQ-174).
        var labelled = await client.Send(HttpMethod.Post, "/api/v1/payments/payments", new
        {
            direction = "In", purpose = "COMMISSION", methodCode = "EFT", amount = 100m, currency = "ZAR", businessRefType = "INVOICE", businessRefId = motor,
        }, idempotent: true);
        labelled.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await labelled.Content.ReadAsStringAsync());
        (await labelled.Code()).ShouldBe("OUT_OF_SCOPE");
    }

    /// <summary>OQ-170: a reconciliation exception is acted on through the payment it references; one with no payment, by an unrestricted caller only (OQ-176).</summary>
    [Fact]
    public async Task ReconciliationExceptions_AreActedOnThroughTheirPaymentsScope_OQ170_OQ176()
    {
        var client = await Scoped("ws-recon", "WS-HOME", PaymentPermissions.ReconciliationRead, PaymentPermissions.ReconciliationUpdate);
        var outside = await Exception(await Payment("UNKNOWN", await Invoice("WS-MOTOR")));
        var unmatched = await Exception(null);
        var inside = await Exception(await Payment("UNKNOWN", await Invoice("WS-HOME")));
        foreach (var exception in new[] { outside, unmatched })
        {
            var refused = await client.Send(HttpMethod.Post, $"/api/v1/payments/reconciliation-exceptions/{exception}/assign", ifMatch: "\"1\"");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
            (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        }

        var allowed = await client.Send(HttpMethod.Post, $"/api/v1/payments/reconciliation-exceptions/{inside}/assign", ifMatch: "\"1\"");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>F103 §1: a General claim's assessments and recoveries are its child records, changed only within the claim's scope.</summary>
    [Fact]
    public async Task AClaimsAssessmentsAndRecoveries_AreChangedOnlyWithinTheClaimsScope_F103()
    {
        var client = await Scoped("ws-general", "WS-HOME", GeneralPermissions.ClaimRead, GeneralPermissions.AssessmentUpdate, GeneralPermissions.RecoveryUpdate);
        var outsideClaim = await _rows.Insert(T, "general.claim", ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"), ("product_code", "WS-MOTOR"));
        var insideClaim = await _rows.Insert(T, "general.claim", ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"), ("product_code", "WS-HOME"));
        var assessment = await _rows.Insert(T, "general.loss_assessment", ("claim_id", outsideClaim), ("status", "ASSIGNED"), ("assessed_amount", 0m), ("excess_applied", 0m));
        var recovery = await _rows.Insert(T, "general.recovery_case", ("claim_id", outsideClaim), ("recovery_type", "SALVAGE"), ("expected_amount", 1000m),
            ("recovered_amount", 0m), ("status", "OPEN"));
        var ownRecovery = await _rows.Insert(T, "general.recovery_case", ("claim_id", insideClaim), ("recovery_type", "SALVAGE"), ("expected_amount", 1000m),
            ("recovered_amount", 0m), ("status", "OPEN"));

        foreach (var url in new[] { $"/api/v1/general/assessments/{assessment}/start", $"/api/v1/general/recoveries/{recovery}/close" })
        {
            var refused = await client.Send(HttpMethod.Post, url, ifMatch: "\"1\"");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{url}: {await refused.Content.ReadAsStringAsync()}");
            (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        }

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM general.recovery_case WHERE id = @r", ("r", recovery))).ShouldBe("OPEN");
        var allowed = await client.Send(HttpMethod.Post, $"/api/v1/general/recoveries/{ownRecovery}/demand", ifMatch: "\"1\"");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>F103 §2: a claim is registered in the Claims Framework only at a product and node in the caller's scope.</summary>
    [Fact]
    public async Task AClaim_IsRegisteredOnlyAtAProductAndNodeInScope_F103()
    {
        var client = await Scoped("ws-claims", "WS-HOME", ClaimsPermissions.ClaimRead, ClaimsPermissions.ClaimCreate);
        object Claim(string product) => new { domain = "GENERAL", domainClaimId = Guid.CreateVersion7(), policyRef = Guid.CreateVersion7(), productCode = product, orgNodeCode = "BR-A" };

        var refused = await client.Send(HttpMethod.Post, "/api/v1/claims/claims", Claim("WS-MOTOR"), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        (await client.Send(HttpMethod.Post, "/api/v1/claims/claims", Claim("WS-HOME"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>F103: staff submit a Health claim only on a membership in their scope.</summary>
    [Fact]
    public async Task AHealthClaim_IsSubmittedByStaffOnlyOnAMembershipInScope_F103()
    {
        var client = await Scoped("ws-health", "WS-HEALTH", HealthPermissions.ClaimRead, HealthPermissions.ClaimCreate);
        var outside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "WS-OTHER"), ("member_number", $"WS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var party = await fixture.Database.ScalarAsOwner<Guid>("SELECT principal_party_id FROM health.membership WHERE id = @m", ("m", outside));

        var refused = await client.Send(HttpMethod.Post, "/api/v1/health/claims", new
        {
            source = "MEMBER", membershipId = outside, patientPartyId = party, serviceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3), formatCode = "API",
            currency = "ZAR", lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 650m } },
        }, idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM health.medical_claim WHERE membership_id = @m", ("m", outside))).ShouldBe(0);

        var inside = await _rows.Insert(T, "health.membership", ("status", "ACTIVE"), ("product_code", "WS-HEALTH"), ("member_number", $"WS{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var own = await client.Send(HttpMethod.Post, "/api/v1/health/claims", new
        {
            source = "MEMBER", membershipId = inside, patientPartyId = await fixture.Database.ScalarAsOwner<Guid>("SELECT principal_party_id FROM health.membership WHERE id = @m", ("m", inside)),
            serviceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3), formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 650m } },
        }, idempotent: true);
        own.StatusCode.ShouldBe(HttpStatusCode.Created, await own.Content.ReadAsStringAsync());
    }

    /// <summary>F103, OQ-168: a premium calculation is made only for a product in the caller's scope, judged by product only.</summary>
    [Fact]
    public async Task APremiumCalculation_IsMadeOnlyForAProductInScope_F103_OQ168()
    {
        var client = await Scoped("ws-rating", "WS-HOME", RatingPermissions.CalculationRead, RatingPermissions.CalculationCreate);
        object Calculation(string product) => new
        {
            requestRef = $"WS-{Guid.NewGuid():N}"[..20], sourceDomain = "GENERAL", subjectType = "GENERAL_QUOTE", subjectId = Guid.CreateVersion7(), productCode = product,
            productVersion = 1, currency = "ZAR", frequency = "MONTHLY", values = new { },
        };

        var refused = await client.Send(HttpMethod.Post, "/api/v1/rating/calculations", Calculation("WS-MOTOR"), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        var inScope = await client.Send(HttpMethod.Post, "/api/v1/rating/calculations", Calculation("WS-HOME"), idempotent: true);
        (await inScope.Code()).ShouldNotBe("OUT_OF_SCOPE", "within the product scope, the request goes on to be rated (here, a product with no rating set up)");
    }

    private async Task<HttpClient> Scoped(string subject, string product, params string[] permissions)
    {
        var user = await fixture.Database.UserWithPermissions(T, subject, permissions);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", product), ("u", user));
        return _factory.ClientFor(T, subject);
    }

    private async Task<Guid> Invoice(string product)
    {
        var account = await _rows.Insert(T, "billing.billing_account", ("status", "ACTIVE"));
        await _rows.Insert(T, "billing.premium_schedule", ("billing_account_id", account), ("product_code", product), ("org_node_code", "BR-A"));
        return await _rows.Insert(T, "billing.invoice", ("billing_account_id", account), ("product_code", product), ("org_node_code", "BR-A"), ("status", "ISSUED"),
            ("document_type", "INVOICE"), ("currency", "ZAR"), ("source_domain", "GENERAL"), ("net_amount", 100m), ("tax_amount", 0m), ("total_amount", 100m),
            ("invoice_number", $"WS{Random.Shared.Next(100_000_000, 999_999_999)}"));
    }

    private Task<Guid> Payment(string status, Guid? invoice) =>
        _rows.Insert(T, "payment.payment", [("direction", "IN"), ("purpose", "PREMIUM"), ("status", status), ("amount", 100m), ("currency", "ZAR"), ("method_code", "EFT"),
            ("payment_ref", $"WS-{Guid.NewGuid():N}"[..20]), ("idempotency_key", Guid.NewGuid().ToString()),
            .. invoice is { } id ? new (string, object)[] { ("business_ref_type", "INVOICE"), ("business_ref_id", id) } : []]);

    private Task<Guid> Exception(Guid? payment) =>
        _rows.Insert(T, "payment.reconciliation_exception", [("exception_type", payment is null ? "UNMATCHED_LINE" : "UNKNOWN_OUTCOME"), ("amount", 100m), ("status", "OPEN"),
            .. payment is { } id ? new (string, object)[] { ("payment_id", id) } : []]);
}
