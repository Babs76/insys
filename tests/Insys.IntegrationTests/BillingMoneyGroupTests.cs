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
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>DE-008: a refund's payout made to fail, for tests that start from a failed payout.</summary>
internal static class RefundPayouts
{
    /// <summary>Payments' PaymentFailed for the refund's payout, as its provider's refusal raises it; returns the failed payment's reference.</summary>
    public static async Task<string> Fail(InsysApiFactory factory, TestDatabase db, Guid tenant, Guid refund)
    {
        await factory.DrainOutbox();
        var (payment, reference) = (await db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE tenant_id = @t AND business_ref_id = @r", ("t", tenant), ("r", refund)),
            (await db.ScalarAsOwner<string>("SELECT payment_ref FROM payment.payment WHERE tenant_id = @t AND business_ref_id = @r", ("t", tenant), ("r", refund)))!);
        var failed = new BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), tenant, "payment.PaymentFailed", 1, DateTimeOffset.UtcNow, "Payment", payment,
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(new PaymentFailed(payment, reference, "OUT", PaymentPurposes.Refund, 25m, "ZAR",
                BillingApprovals.RefundBusinessRefType, refund, "Account closed"), BuildingBlocks.Infrastructure.Persistence.ModuleDbContext.EventJson));
        await factory.Services.GetRequiredService<IEventBus>().Publish(failed, CancellationToken.None);
        await factory.DrainOutbox();
        return reference;
    }
}

/// <summary>
/// The money group through the API (Ade, 24 November 2026): a refund whose payout failed is paid again only on a second person's PAYOUT_RETRY,
/// with the failed payment's reference typed, or cancelled with its money kept as credit (DE-008); a credit note says what its invoice still asks
/// for (DE-009); a credit note whose reason requires approval waits for a CREDIT_NOTE and credits at most what is still outstanding (DE-010, OQ-058).
/// </summary>
public sealed class BillingMoneyGroupTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = TestClock.Today;

    private static readonly string[] Permissions =
    [
        PartyPermissions.PartyCreate, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountVerify,
        BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead,
        BillingPermissions.InvoiceCreate, BillingPermissions.RefundRead, BillingPermissions.RefundCreate, BillingPermissions.RefundRetryPayout, BillingPermissions.RefundCancel,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease,
        PaymentPermissions.CallbackEndpointUpdate, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(Tenant, "mg-maker", Permissions.MakerSide());
        var checker = await db.UserWithPermissions(Tenant, "mg-checker", Permissions.CheckerSide());
        _maker = _factory.ClientFor(Tenant, "mg-maker");
        _checker = _factory.ClientFor(Tenant, "mg-checker");
        var approverRole = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, checker, approverRole);
        await db.AllowBankAccountVerification(Tenant, approverRole, Today.AddDays(-90));
        foreach (var type in new[] { BillingApprovals.RefundApprovalType, PaymentApprovals.PayoutApprovalType, PaymentApprovals.PayoutRetryApprovalType, BillingApprovals.CreditNoteApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
        await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "OVERPAID", ("label", "Overpaid"), ("requires_approval", false), ("accounting_impact", "REFUND"));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "GOODWILL", ("label", "Goodwill"), ("requires_approval", false), ("accounting_impact", "CREDIT"));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "WRITE_OFF", ("label", "Written off"), ("requires_approval", true), ("accounting_impact", "CREDIT"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"), ("currencies", new[] { "ZAR" }),
            ("countries", new[] { "ZA" }));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"),
            ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        foreach (var (status, platform) in new[] { ("PAID", "SUCCEEDED"), ("BOUNCED", "FAILED") })
        {
            await Seed("payment.ref_payment_status_mappings", "ACTIVE", $"SIM-{status}", ("provider_code", "SIMULATOR"), ("provider_status", status), ("platform_status", platform),
                ("final", true));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// DE-008, SRS-002, PAY-005: a refund paid out through a released batch whose provider refuses it is PaymentFailed, naming the failed payment.
    /// A retry needs that reference typed and the payer's own payable account; a second person approves it; the refund is approved again on its
    /// second attempt and Payments is asked for a new payout (PayoutRequested), which pays it.
    /// </summary>
    [Fact]
    public async Task ARefundWhosePayoutFailed_IsPaidAgainOnlyOnASecondPersonsApproval_WithTheFailedReferenceTyped_DE008_SRS002()
    {
        var payer = await _maker.RegisterPerson("Bounced refundee");
        var first = await _maker.AddVerifiedBankAccount(_checker, _factory, payer, "62000666601");
        var (account, _) = await IssueInvoice(80m, payer);
        var refund = await ApprovedRefund(account, first);

        // Paid out in a released batch, and refused by the provider.
        var payout = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", refund));
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _checker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.SimulatorCallback(_factory, fixture.Database, payout, "BOUNCED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        var failedRef = (await fixture.Database.ScalarAsOwner<string>("SELECT payment_ref FROM payment.payment WHERE id = @p", ("p", payout)))!;
        var view = await Refund(refund);
        (view.GetProperty("status").GetString(), view.GetProperty("failedPaymentRef").GetString(), view.GetProperty("failedPaymentId").GetGuid())
            .ShouldBe(("PaymentFailed", failedRef, payout));
        (await (await _maker.GetAsync($"/api/v1/billing/refunds?accountId={account}&status=PaymentFailed")).Json()).GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).ShouldBe([refund]);

        // The payer gives a new account, which supersedes the one that failed. The typed reference must be the failed payment's, and the account
        // the payer's own payable one (the superseded one no longer is); nothing changes otherwise.
        var second = await _maker.AddVerifiedBankAccount(_checker, _factory, payer, "62000666602");
        (await Retry(refund, failedRef, first)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var stranger = await _maker.AddVerifiedBankAccount(_checker, _factory, await _maker.RegisterPerson("Not the payer"), "62000666603");
        var mismatch = await Retry(refund, "PAY-NOT-THIS-ONE", second);
        (mismatch.StatusCode, await mismatch.Code()).ShouldBe((HttpStatusCode.UnprocessableEntity, "TYPED_REFERENCE_MISMATCH"));
        (await Retry(refund, failedRef, stranger)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Refund(refund)).GetProperty("retryPending").GetBoolean().ShouldBeFalse();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE business_ref_id = @r AND approval_type = 'PAYOUT_RETRY'", ("r", refund)))
            .ShouldBe(0L);

        var asked = await Retry(refund, failedRef, second);
        asked.StatusCode.ShouldBe(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        (await asked.Json()).GetProperty("retryPending").GetBoolean().ShouldBeTrue();
        (await Retry(refund, failedRef, second)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "one retry at a time");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<decimal>(
            "SELECT authority_limit FROM workflow.approval WHERE business_ref_type = 'REFUND_REQUEST' AND business_ref_id = @r AND approval_type = 'PAYOUT_RETRY' AND status = 'REQUESTED'",
            ("r", refund))).ShouldBe(25m);

        await Approve(BillingApprovals.RefundBusinessRefType, refund);
        view = await Refund(refund);
        (view.GetProperty("status").GetString(), view.GetProperty("payoutAttempt").GetInt32(), view.GetProperty("bankAccountId").GetGuid(),
            view.GetProperty("retryPending").GetBoolean()).ShouldBe(("Approved", 2, second, false));
        var requested = await OutboxPayload("payment.PayoutRequested", refund);
        (requested.GetProperty("attempt").GetInt32(), requested.GetProperty("bankAccountId").GetGuid(), requested.GetProperty("amount").GetDecimal(),
            requested.GetProperty("purpose").GetString(), requested.GetProperty("gated").GetBoolean()).ShouldBe((2, second, 25m, PaymentPurposes.Refund, true));

        // The new payout's success pays the refund, as the first one's would have.
        var retried = Guid.CreateVersion7();
        await Publish("payment.PaymentSucceeded", retried, new PaymentSucceeded(retried, "PAY-RETRY-2", PaymentPurposes.Refund, 25m, "ZAR", Today,
            BillingApprovals.RefundBusinessRefType, refund));
        (await Refund(refund)).GetProperty("status").GetString().ShouldBe("Paid");
    }

    /// <summary>
    /// DE-008: a refund whose payout failed is cancelled with the failed payment's reference typed, audited, with no approval; it reads Cancelled,
    /// is listed by that status, and Payments is told the payout is abandoned. Nothing reached the ledger, so the money stays as credit.
    /// </summary>
    [Fact]
    public async Task ARefundWhosePayoutFailed_IsCancelledWithTheReferenceTyped_AndThePayoutIsAbandoned_DE008()
    {
        var payer = await _maker.RegisterPerson("Cancelled refundee");
        var bank = await _maker.AddVerifiedBankAccount(_checker, _factory, payer, "62000666611");
        var (account, _) = await IssueInvoice(80m, payer);
        var refund = await ApprovedRefund(account, bank);
        var failedRef = await RefundPayouts.Fail(_factory, fixture.Database, Tenant, refund);

        var wrong = await Cancel(refund, "PAY-NOT-THIS-ONE");
        (wrong.StatusCode, await wrong.Code()).ShouldBe((HttpStatusCode.UnprocessableEntity, "TYPED_REFERENCE_MISMATCH"));
        (await Refund(refund)).GetProperty("status").GetString().ShouldBe("PaymentFailed");
        (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/refunds/{refund}/cancel", new { paymentRef = failedRef, reason = "Keep it as credit" }, raw: true))
            .StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired, "If-Match is required");

        var cancelled = await Cancel(refund, failedRef);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        var view = await Refund(refund);
        (view.GetProperty("status").GetString(), view.GetProperty("cancelReason").GetString()).ShouldBe(("Cancelled", "Keep it as credit"));
        (await (await _maker.GetAsync($"/api/v1/billing/refunds?accountId={account}&status=Cancelled")).Json()).GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).ShouldBe([refund]);
        (await OutboxPayload("payment.PayoutAbandoned", refund)).GetProperty("reason").GetString().ShouldBe("Keep it as credit");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'CancelRefund' AND target_id = @r",
            ("t", Tenant), ("r", refund.ToString()))).ShouldBe(1L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE business_ref_id = @r", ("r", refund))).ShouldBe(1L, "only the refund's own");
        (await Cancel(refund, failedRef)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// DE-010 (OQ-058, Ade), DE-009: a credit note whose reason requires approval is asked for (202) and waits; the invoice still takes a payment;
    /// once a second person grants it, the credit note is issued for what is still outstanding, never more than asked, and its event says the
    /// invoice now asks for nothing. A reason that needs no approval still issues at once (201). A rejected request credits nothing.
    /// </summary>
    [Fact]
    public async Task ACreditNoteWhoseReasonNeedsApproval_WaitsForIt_AndCreditsWhatIsStillOutstanding_DE010_DE009_OQ058()
    {
        var (_, invoice) = await IssueInvoice(300m);
        var asked = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice}/credit-notes", new { amount = 200m, reasonCode = "WRITE_OFF" }, idempotent: true);
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync());
        var request = (await asked.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await CreditNoteRequest(request)).GetProperty("status").GetString().ShouldBe("Pending");
        var read = await Invoice(invoice);
        (read.GetProperty("credited").GetDecimal(), read.GetProperty("creditNoteRequests").EnumerateArray().Single().GetProperty("id").GetGuid()).ShouldBe((0m, request));

        // The invoice isn't frozen: 150 is allocated while the request waits.
        await Publish("payment.PaymentAllocated", Guid.CreateVersion7(), new PaymentAllocated(Guid.CreateVersion7(), Guid.CreateVersion7(), invoice, 150m, "ZAR"));
        (await Invoice(invoice)).GetProperty("outstanding").GetDecimal().ShouldBe(150m);

        await Approve(BillingApprovals.CreditNoteBusinessRefType, request);
        var decided = await CreditNoteRequest(request);
        (decided.GetProperty("status").GetString(), decided.GetProperty("creditedAmount").GetDecimal()).ShouldBe(("Issued", 150m));
        var credit = await Invoice(decided.GetProperty("creditNoteId").GetGuid());
        (credit.GetProperty("documentType").GetString(), credit.GetProperty("totalAmount").GetDecimal()).ShouldBe(("CreditNote", 150m));
        (await Invoice(invoice)).GetProperty("status").GetString().ShouldBe("Credited");
        var issued = await OutboxPayload("billing.CreditNoteIssued", credit.GetProperty("id").GetGuid(), "creditNoteId");
        (issued.GetProperty("totalAmount").GetDecimal(), issued.GetProperty("invoiceOutstanding").GetDecimal()).ShouldBe((150m, 0m));

        // A reason that needs no approval issues at once, and says what the invoice still asks for; a rejected request credits nothing.
        var (_, other) = await IssueInvoice(100m);
        var now = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{other}/credit-notes", new { amount = 40m, reasonCode = "GOODWILL" }, idempotent: true);
        now.StatusCode.ShouldBe(HttpStatusCode.Created, await now.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await OutboxPayload("billing.CreditNoteIssued", (await now.Json()).GetProperty("id").GetGuid(), "creditNoteId")).GetProperty("invoiceOutstanding").GetDecimal()
            .ShouldBe(60m);
        var refused = (await (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{other}/credit-notes", new { amount = 60m, reasonCode = "WRITE_OFF" },
            idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        var approval = (await (await _checker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={BillingApprovals.CreditNoteBusinessRefType}&businessRefId={refused}"))
            .Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Not a write-off" }, ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var rejected = await CreditNoteRequest(refused);
        (rejected.GetProperty("status").GetString(), rejected.GetProperty("ending").GetString(), rejected.GetProperty("latestRejection").GetProperty("approvalType").GetString())
            .ShouldBe(("Rejected", "REJECTED", BillingApprovals.CreditNoteApprovalType));
        (await Invoice(other)).GetProperty("outstanding").GetDecimal().ShouldBe(60m);
    }

    private async Task<Guid> ApprovedRefund(Guid account, Guid bankAccount)
    {
        await _factory.DrainOutbox();
        var created = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/refunds", new { amount = 25m, reasonCode = "OVERPAID", bankAccountId = bankAccount },
            idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var refund = (await created.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(BillingApprovals.RefundBusinessRefType, refund);
        return refund;
    }

    private async Task<HttpResponseMessage> Retry(Guid refund, string paymentRef, Guid bankAccount) =>
        await _maker.Send(HttpMethod.Post, $"/api/v1/billing/refunds/{refund}/retry-payout", new { paymentRef, reason = "The payer gave another account", bankAccountId = bankAccount },
            ifMatch: await ETag(refund));

    private async Task<HttpResponseMessage> Cancel(Guid refund, string paymentRef) =>
        await _maker.Send(HttpMethod.Post, $"/api/v1/billing/refunds/{refund}/cancel", new { paymentRef, reason = "Keep it as credit" }, ifMatch: await ETag(refund));

    private async Task<string> ETag(Guid refund) => $"\"{(await Refund(refund)).GetProperty("rowVersion").GetInt64()}\"";

    private async Task<JsonElement> Refund(Guid refund) => await Ok($"/api/v1/billing/refunds/{refund}");

    private async Task<JsonElement> Invoice(Guid invoice) => await Ok($"/api/v1/billing/invoices/{invoice}");

    private async Task<JsonElement> CreditNoteRequest(Guid request) => await Ok($"/api/v1/billing/credit-note-requests/{request}");

    private async Task<JsonElement> Ok(string url)
    {
        var response = await _maker.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync()}");
        return await response.Json();
    }

    /// <summary>The one event of the type Billing's outbox holds for the record (by the payload field naming it).</summary>
    private async Task<JsonElement> OutboxPayload(string eventType, Guid id, string field = "businessRefId")
    {
        var payload = await fixture.Database.ScalarAsOwner<string>(
            $"SELECT payload::text FROM billing.outbox_event WHERE tenant_id = @t AND event_type = @e AND payload->>'{field}' = @id", ("t", Tenant), ("e", eventType),
            ("id", id.ToString()));
        payload.ShouldNotBeNull($"no {eventType} for {id} in Billing's outbox");
        return JsonDocument.Parse(payload).RootElement;
    }

    private async Task Publish(string eventType, Guid aggregateId, BuildingBlocks.Contracts.IIntegrationEvent body)
    {
        var envelope = new BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), Tenant, eventType, 1, DateTimeOffset.UtcNow, "Payment", aggregateId,
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(body, body.GetType(), BuildingBlocks.Infrastructure.Persistence.ModuleDbContext.EventJson));
        await _factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
        await _factory.DrainOutbox();
    }

    private async Task<(Guid Account, Guid Invoice)> IssueInvoice(decimal instalment, Guid? payer = null)
    {
        var account = (await (await _maker.Send(HttpMethod.Post, "/api/v1/billing/accounts/",
            new { payerPartyId = payer ?? Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/schedules", new
        {
            sourceDomain = "GENERAL", policyRef = Guid.CreateVersion7(), productVersionRef = "MOTOR-v1", frequency = "MONTHLY", startDate = Today, endDate = (DateOnly?)null,
            instalmentAmount = instalment,
        }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/invoices", new { asOf = Today }, idempotent: true);
        issued.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (account, (await issued.Json()).EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    /// <summary>The maker cannot approve their own request; the checker can (SRS-002).</summary>
    private async Task Approve(string businessRefType, Guid businessRefId)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
    }

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        fixture.Database.SeedStatic(Tenant, table, status, code, Today.AddDays(-90), fields);
}
