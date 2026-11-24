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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The money group's Health and case redress steps (Ade, 24 November 2026), through the real host: DE-008, a failed remittance and a failed redress
/// payout are paid again only with the failed payment's reference typed and a second person's PAYOUT_RETRY approval, as a new payout attempt; DE-009,
/// a Billing credit note on a recovery's invoice lowers its balance, or, crediting it fully, withdraws the invoice and leaves the recovery OPEN.
/// </summary>
public sealed class MoneyRetryHealthAndRedressTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private const string Runs = "/api/v1/health/payment-runs";
    private const string Cases = "/api/v1/workflow/cases";

    private static readonly string[] Staff =
    [
        HealthPermissions.RunRead, HealthPermissions.RunUpdate, HealthPermissions.RecoveryRead, HealthPermissions.RecoveryManage,
        CasePermissions.Read, CasePermissions.Create, CasePermissions.Update, CasePermissions.Redress,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        // TODO: drop once R__function_catalogue.sql is regenerated at merge (a shared snapshot this branch leaves alone): the two new actions, as it will list them.
        await db.ExecuteAsOwner("""
            INSERT INTO iam.ref_function_catalogue (tenant_id, code, version, effective_from, record_status, record_source, record_class, created_by, approved_at, approved_by,
                change_reason, labels, sort_order, function_code, sub_domain_code, process_area, interaction_type, function_kind, parent_function_code, audiences, route_key,
                required_permissions, required_feature, privileged, allowed_while_onboarding)
            SELECT NULL, f.code, 1, DATE '2026-10-01', 'ACTIVE', 'insys-core', 'PUBLIC', 'test', now(), 'test', 'DE-008 test stand-in', f.labels::jsonb, 9000, f.code, f.sub_domain,
                   f.area, 'HUMAN', 'ACTION', f.parent, ARRAY['STAFF'], NULL, f.permissions, f.feature, false, f.onboarding
              FROM (VALUES ('HEALTH_REISSUE_REMITTANCE', '{"en":"Reissue remittance"}', 'HEALTH_PROVIDER_PAYMENTS', 'Payment runs (second-person release)', 'HEALTH_PAYMENT_RUNS',
                            ARRAY['provider_payment_run.update'], 'HEALTH', false),
                           ('CASE_RETRY_REDRESS_PAYOUT', '{"en":"Retry redress payout"}', 'CUSTOMER_SERVICE', 'Complaints', 'SERVICE_COMPLAINTS', ARRAY['case.redress'], NULL, true))
                   AS f(code, labels, sub_domain, area, parent, permissions, feature, onboarding)
             WHERE NOT EXISTS (SELECT 1 FROM iam.ref_function_catalogue c WHERE c.tenant_id IS NULL AND c.code = f.code)
            """);
        _factory = await fixture.Host();
        _rows = new RowFiller(db, DateTimeOffset.UtcNow);
        await db.UserWithPermissions(T, "retry-maker", Staff);
        var checker = await db.UserWithPermissions(T, "retry-checker", Staff);
        _maker = _factory.ClientFor(T, "retry-maker");
        _checker = _factory.ClientFor(T, "retry-checker");
        await CaseTests.SeedConfiguration(db);
        var role = await db.RoleWithPermissions(T);
        await db.AssignRole(T, checker, role);
        // The second person's authority for PAYOUT_RETRY, seeded here (the role templates give it to FINANCE_MANAGER).
        await db.SeedStatic(T, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "PAYOUT_RETRY-" + role, new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", PaymentApprovals.PayoutRetryApprovalType), ("limit_value", 100_000m), ("delegation_allowed", false), ("currency", "ZAR"));
        await db.SeedStatic(T, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "CASE_REDRESS-" + role, new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", CaseApprovals.RedressApprovalType), ("limit_value", 100_000m), ("delegation_allowed", false), ("currency", "ZAR"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AFailedRemittance_HoldsTheRun_AndIsPaidAgainOnlyWithItsReferenceAndASecondPerson_DE008_HLT009_SRS002()
    {
        var run = await _rows.Insert(T, "health.provider_payment_run", ("run_code", "PRV-RETRY"), ("status", "RELEASED"), ("currency", "ZAR"), ("total_amount", 900m));
        var party = Guid.CreateVersion7();
        var remittance = await _rows.Insert(T, "health.remittance_advice", ("run_id", run), ("provider_id", "PR-RETRY"), ("amount", 900m), ("gross_amount", 900m),
            ("provider_party_id", party), ("bank_account_id", Guid.CreateVersion7()));
        var failed = Guid.CreateVersion7();
        await Deliver("payment.PaymentFailed", failed, new PaymentFailed(failed, "PAY-RT-1", "OUT", PaymentPurposes.ClaimPayout, 900m, "ZAR",
            HealthReferences.RemittanceRefType, remittance, "Account closed"));

        var read = await _maker.GetAsync($"{Runs}/{run}");
        var body = await read.Json();
        var view = Remittance(body, remittance);
        (view.GetProperty("paymentStatus").GetString(), view.GetProperty("failedPaymentRef").GetString()).ShouldBe(("FAILED", "PAY-RT-1"));
        body.GetProperty("status").GetString().ShouldBe("Released");

        var url = $"{Runs}/{run}/remittances/{remittance}/reissue";
        var mismatch = await _maker.Send(HttpMethod.Post, url, new { paymentRef = "PAY-RT-9", reason = "Provider gave new details" }, ifMatch: read.ETag());
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await mismatch.Code()).ShouldBe("TYPED_REFERENCE_MISMATCH");
        (await _maker.Send(HttpMethod.Post, url, new { paymentRef = "PAY-RT-1", reason = "New account", bankAccountId = Guid.CreateVersion7() }, ifMatch: read.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "an unknown account is not the payee's verified one");

        var asked = await _maker.Send(HttpMethod.Post, url, new { paymentRef = "PAY-RT-1", reason = "Provider fixed its account" }, ifMatch: read.ETag());
        asked.StatusCode.ShouldBe(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        Remittance(await asked.Json(), remittance).GetProperty("reissuePending").GetBoolean().ShouldBeTrue();
        await _factory.DrainOutbox();

        var approval = await Approval(HealthReferences.RemittanceRefType, remittance);
        (await (await Decide(_maker, approval, "approve")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await Decide(_checker, approval, "approve")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var reissued = Remittance(await (await _maker.GetAsync($"{Runs}/{run}")).Json(), remittance);
        (reissued.GetProperty("paymentStatus").GetString(), reissued.GetProperty("attempt").GetInt32()).ShouldBe(("PENDING", 2));
        (await PayoutAttempt("health", run)).ShouldBe("2");

        var paid = Guid.CreateVersion7();
        await Deliver("payment.PaymentSucceeded", paid, new PaymentSucceeded(paid, "PAY-RT-2", PaymentPurposes.ClaimPayout, 900m, "ZAR", TestClock.Today,
            HealthReferences.RemittanceRefType, remittance));
        (await (await _maker.GetAsync($"{Runs}/{run}")).Json()).GetProperty("status").GetString().ShouldBe("Reconciled");
    }

    [Fact]
    public async Task ACreditNote_LowersTheRecoverysBalance_OrWithdrawsItsInvoice_LeavingItOpen_AndAnUnknownOutstandingChangesNothing_DE009()
    {
        var invoice = Guid.CreateVersion7();
        var recovery = await _rows.Insert(T, "health.provider_recovery", ("recovery_number", "RC-DE009"), ("debtor_kind", "PROVIDER"), ("provider_id", "PR-RC"),
            ("source", "FINDING"), ("source_id", Guid.CreateVersion7()), ("original_amount", 1_000m), ("balance", 1_000m), ("currency", "ZAR"), ("basis", "INVOICE"),
            ("status", "INVOICED"), ("reason", "Overpaid"), ("in_review", false), ("repay_amount", 0m), ("invoice_id", invoice), ("row_version", 1L));
        var url = $"/api/v1/health/provider-recoveries/{recovery}";

        await Deliver("billing.CreditNoteIssued", Guid.CreateVersion7(), Credit(invoice, recovery, 100m, null));
        (await (await _maker.GetAsync(url)).Json()).GetProperty("balance").GetDecimal().ShouldBe(1_000m, "an old producer's credit is unknown: nothing changes");

        await Deliver("billing.CreditNoteIssued", Guid.CreateVersion7(), Credit(invoice, recovery, 200m, 800m));
        var partly = await (await _maker.GetAsync(url)).Json();
        (partly.GetProperty("status").GetString(), partly.GetProperty("balance").GetDecimal(), partly.GetProperty("invoiceId").GetGuid()).ShouldBe(("INVOICED", 800m, invoice));

        await Deliver("billing.CreditNoteIssued", Guid.CreateVersion7(), Credit(invoice, recovery, 800m, 0m));
        var withdrawn = await (await _maker.GetAsync(url)).Json();
        (withdrawn.GetProperty("status").GetString(), withdrawn.GetProperty("balance").GetDecimal()).ShouldBe(("OPEN", 800m));
        withdrawn.GetProperty("invoiceId").ValueKind.ShouldBe(JsonValueKind.Null);
        withdrawn.GetProperty("credits").EnumerateArray().Select(c => c.GetProperty("outcome").GetString()).ShouldBe(["PARTIAL", "WITHDRAWN"]);

        var read = await _maker.GetAsync(url);
        (await _maker.Send(HttpMethod.Post, $"{url}/write-off", new { reason = "Provider closed" }, ifMatch: read.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AFailedRedressPayout_IsPaymentFailed_AndIsPaidAgainOnlyWithItsReferenceAndASecondPerson_DE008_OQ104()
    {
        var opened = await _maker.Send(HttpMethod.Post, Cases,
            new { caseTypeCode = "COMPLAINT_T", receivedChannel = "PHONE", complainantPartyId = Guid.CreateVersion7(), categoryCode = "SERVICE", summary = "S" }, idempotent: true);
        opened.StatusCode.ShouldBe(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        var id = (await opened.Json()).GetProperty("id").GetGuid();
        var account = Guid.CreateVersion7();
        var recorded = await _maker.Send(HttpMethod.Post, $"{Cases}/{id}/redress",
            new { redressType = "COMPENSATION", amount = 750.50m, currency = "ZAR", payeePartyId = Guid.CreateVersion7(), bankAccountId = account }, idempotent: true);
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created, await recorded.Content.ReadAsStringAsync());
        var redress = (await recorded.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await Decide(_checker, await Approval(CaseApprovals.RedressBusinessRefType, redress, CaseApprovals.RedressApprovalType), "approve")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var failed = Guid.CreateVersion7();
        await Deliver("payment.PaymentFailed", failed, new PaymentFailed(failed, "PAY-RD-1", "OUT", CaseApprovals.RedressPaymentPurpose, 750.50m, "ZAR",
            CaseApprovals.RedressBusinessRefType, redress, "Account closed"));
        var url = $"{Cases}/{id}/redress/{redress}";
        var read = await _maker.GetAsync(url);
        var view = await read.Json();
        (view.GetProperty("status").GetString(), view.GetProperty("failedPaymentRef").GetString()).ShouldBe(("PaymentFailed", "PAY-RD-1"));

        var mismatch = await _maker.Send(HttpMethod.Post, $"{url}/retry-payout", new { paymentRef = "PAY-RD-9", reason = "Payee fixed it" }, ifMatch: read.ETag());
        (await mismatch.Code()).ShouldBe("TYPED_REFERENCE_MISMATCH");
        var asked = await _maker.Send(HttpMethod.Post, $"{url}/retry-payout", new { paymentRef = "PAY-RD-1", reason = "Payee fixed it" }, ifMatch: read.ETag());
        asked.StatusCode.ShouldBe(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();

        var approval = await Approval(CaseApprovals.RedressBusinessRefType, redress, PaymentApprovals.PayoutRetryApprovalType);
        var version = (await (await _checker.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("rowVersion").GetInt64();
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Check the account first" }, ifMatch: $"\"{version}\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await (await _maker.GetAsync(url)).Json()).GetProperty("status").GetString().ShouldBe("PaymentFailed", "a rejected retry leaves it where it was");

        read = await _maker.GetAsync(url);
        (await _maker.Send(HttpMethod.Post, $"{url}/retry-payout", new { paymentRef = "PAY-RD-1", reason = "Account checked" }, ifMatch: read.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Decide(_checker, await Approval(CaseApprovals.RedressBusinessRefType, redress, PaymentApprovals.PayoutRetryApprovalType), "approve")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var retried = await (await _maker.GetAsync(url)).Json();
        (retried.GetProperty("status").GetString(), retried.GetProperty("attempt").GetInt32(), retried.GetProperty("payoutBankAccountId").GetGuid())
            .ShouldBe(("Approved", 2, account));
        (await PayoutAttempt("workflow", redress)).ShouldBe("2");
    }

    private static CreditNoteIssued Credit(Guid invoice, Guid recovery, decimal amount, decimal? outstanding) =>
        new(Guid.CreateVersion7(), "CN-" + Random.Shared.Next(100_000, 999_999), invoice, Guid.CreateVersion7(), HealthReferences.RecoverySourceDomain, recovery, TestClock.Today, "ZAR",
            amount, "BILLED_IN_ERROR", InvoiceOutstanding: outstanding);

    private static JsonElement Remittance(JsonElement run, Guid id) =>
        run.GetProperty("remittances").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    private Task Deliver<TEvent>(string type, Guid aggregate, TEvent payload) =>
        _factory.Services.GetRequiredService<IEventBus>().Deliver(new EventEnvelope(Guid.CreateVersion7(), T, type, 1, DateTimeOffset.UtcNow, "Test", aggregate,
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)), CancellationToken.None);

    private Task<Guid> Approval(string refType, Guid refId, string? approvalType = null) =>
        fixture.Database.ScalarAsOwner<Guid>("""
            SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @i AND status = 'REQUESTED'
              AND (@a::text IS NULL OR approval_type = @a)
            """, ("t", T), ("r", refType), ("i", refId), ("a", (object?)approvalType ?? DBNull.Value));

    private Task<string?> PayoutAttempt(string schema, Guid aggregate) =>
        fixture.Database.ScalarAsOwner<string>($"""
            SELECT coalesce(payload->>'attempt', payload->>'Attempt') FROM {schema}.outbox_event
            WHERE tenant_id = @t AND aggregate_id = @a AND event_type = 'payment.PayoutRequested' ORDER BY occurred_at DESC LIMIT 1
            """, ("t", T), ("a", aggregate));

    private static async Task<HttpResponseMessage> Decide(HttpClient client, Guid approval, string action)
    {
        var version = (await (await client.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("rowVersion").GetInt64();
        return await client.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/{action}", ifMatch: $"\"{version}\"");
    }
}
