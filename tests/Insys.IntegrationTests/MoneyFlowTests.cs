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
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.ClaimsFramework.Contracts;
using Insys.Finance.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Phase 3 through the API: premium billed, collected, confirmed by a signed callback, allocated and posted; posting
/// rules resolved by version at the accounting date and stored on each entry (FIN-004); an unknown outcome never
/// treated as success (PAY-005); refunds, payout batches and claim payments released only after a second person's
/// approval (SRS-002).
/// </summary>
public sealed class MoneyFlowTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = TestClock.Today;

    private static readonly string[] Permissions =
    [
        Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate,
        Insys.Party.Contracts.PartyPermissions.BankAccountVerify,
        BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead,
        BillingPermissions.InvoiceCreate, BillingPermissions.RefundRead, BillingPermissions.RefundCreate, PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve,
        PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.ReconciliationRead, PaymentPermissions.ReconciliationUpdate,
        PaymentPermissions.CallbackEndpointUpdate, FinancePermissions.JournalRead, FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate,
        FinancePermissions.PostingExceptionRead, ClaimsPermissions.ClaimRead, ClaimsPermissions.ClaimCreate, ClaimsPermissions.FinancialsRead,
        ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionCreate, ClaimsPermissions.InstructionRelease,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(Tenant, "money-maker", Permissions);
        var checker = await db.UserWithPermissions(Tenant, "money-checker", Permissions);
        _maker = _factory.ClientFor(Tenant, "money-maker");
        _checker = _factory.ClientFor(Tenant, "money-checker");

        // The checker holds approval authority for refunds, payout batches and claim payments (WFL-004).
        var approverRole = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, checker, approverRole);
        await db.AllowBankAccountVerification(Tenant, approverRole, Today.AddDays(-90));
        foreach (var type in new[] { BillingApprovals.RefundApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType, PaymentApprovals.UnknownResolutionApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", 1, Today.AddDays(-90), null,
                ("role_code", approverRole), ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", 1, Today.AddDays(-90), null,
            ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));

        // Finance: accounts, an open period covering today, and posting rules. PREMIUM_DUE has three versions: one
        // that has ended, the one in force today, and a later one not yet in force.
        foreach (var account in new[] { "1100", "1200", "2100", "4000", "4001", "4002", "5000", "6000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", 1, Today.AddDays(-90), null,
                ("account_code", account), ("name", $"Account {account}"), ("type", "ASSET"), ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{month:yyyy-MM}", 1, Today.AddDays(-90), null,
            ("period_code", $"{month:yyyy-MM}"), ("start_date", month), ("end_date", month.AddMonths(1).AddDays(-1)));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-PREM", 1, Today.AddDays(-90), Today, Rule("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-PREM", 2, Today, Today.AddDays(5), Rule("PR-PREM", TransactionTypes.PremiumDue, "1100", "4001"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-PREM", 3, Today.AddDays(5), null, Rule("PR-PREM", TransactionTypes.PremiumDue, "1100", "4002"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-RECEIPT", 1, Today.AddDays(-90), null, Rule("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-REFUND", 1, Today.AddDays(-90), null, Rule("PR-REFUND", TransactionTypes.RefundPaid, "2100", "1200"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-REDRESS", 1, Today.AddDays(-90), null, Rule("PR-REDRESS", TransactionTypes.RedressPaid, "2100", "1200"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-CLM-UP", 1, Today.AddDays(-90), null, Rule("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"));
        await Seed("finance.cfg_posting_rules", "EFFECTIVE", "PR-CLM-PAID", 1, Today.AddDays(-90), null, Rule("PR-CLM-PAID", TransactionTypes.ClaimPaid, "6000", "1200"));
        // Each test seeds the same database; the period is opened by the first.
        (await _maker.PostAsync($"/api/v1/finance/periods/{month:yyyy-MM}/open", null)).StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        // Billing and Payments: a debit order collected by the simulator provider, and EFT for payouts.
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", 1, Today.AddDays(-90), null, ("label", "Monthly"), ("periods_per_year", 12));
        await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", 1, Today.AddDays(-90), null,
            ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "OVERPAID", 1, Today.AddDays(-90), null,
            ("label", "Overpaid"), ("requires_approval", false), ("accounting_impact", "REFUND"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", 1, Today.AddDays(-90), null,
            ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", 1, Today.AddDays(-90), null,
            ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", 1, Today.AddDays(-90), null,
            ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        foreach (var (status, platform) in new[] { ("PAID", "SUCCEEDED"), ("BOUNCED", "FAILED") })
        {
            await Seed("payment.ref_payment_status_mappings", "ACTIVE", $"SIM-{status}", 1, Today.AddDays(-90), null,
                ("provider_code", "SIMULATOR"), ("provider_status", status), ("platform_status", platform), ("final", true));
        }

        // Claims: a reserve type and the general domain's numbering scheme.
        await Seed("claims.ref_reserve_types", "ACTIVE", "INDEMNITY", 1, Today.AddDays(-90), null, ("category", "INDEMNITY"), ("posting_rule", "PR-CLM-UP"));
        await Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", 1, Today.AddDays(-90), null,
            ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Premium_IsBilledCollectedAllocatedAndPosted_WithTheRuleVersionInForceOnTheAccountingDate()
    {
        var (account, invoice) = await IssueInvoice(300m);
        await _factory.DrainOutbox();

        // Billed: posted with PR-PREM v2, the version in force today, not v1 (ended) nor v3 (not yet in force) (FIN-004).
        var billed = (await Journals("INVOICE", invoice)).Single();
        billed.GetProperty("lines").EnumerateArray().ShouldAllBe(l => l.GetProperty("postingRuleCode").GetString() == "PR-PREM" && l.GetProperty("postingRuleVersion").GetInt32() == 2);
        billed.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("credit").GetDecimal() > 0).GetProperty("accountCode").GetString().ShouldBe("4001");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM finance.subledger_entry WHERE business_ref_id = @i AND posting_rule_version = 2", ("i", invoice)))
            .ShouldBe(2L);

        // Collected: sent to the simulator, pending until its signed callback.
        var payment = await CollectionFor(invoice);
        (await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json()).GetProperty("status").GetString().ShouldBe("Pending");
        (await Journals("PAYMENT", payment)).ShouldBeEmpty();
        (await Callback(payment, "PAID", signingSecret: "not-the-secret")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        var paid = await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json();
        paid.GetProperty("status").GetString().ShouldBe("Succeeded");
        paid.GetProperty("allocations").EnumerateArray().Single().GetProperty("invoiceId").GetGuid().ShouldBe(invoice);
        (await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
        var receipt = (await Journals("PAYMENT", payment)).Single();
        receipt.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("debit").GetDecimal()).ShouldBe(300m);
        receipt.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("credit").GetDecimal()).ShouldBe(300m);
        account.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    /// Performance smoke test of a hot path (Phase 7): a signed provider callback confirms a collection and allocates it to
    /// its invoice. p95 within the standard operation target (NFR-002: 2 s), sequentially and 8 at a time.
    /// </summary>
    [Fact]
    public async Task PaymentAllocation_StaysWithinTheStandardLatencyTarget_NFR002()
    {
        var invoices = new List<Guid>();
        for (var i = 0; i < 60; i++)
        {
            invoices.Add((await IssueInvoice(100m + i)).Invoice);
        }

        await _factory.DrainOutbox();
        var payments = new List<Guid>();
        foreach (var invoice in invoices)
        {
            payments.Add(await CollectionFor(invoice));
        }

        var sequential = await Latency.Sequential("Payment callback and allocation, sequential", 5, 25, async i =>
            (await Callback(payments[i < 0 ? -i - 1 : i + 5], "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK));
        var concurrent = await Latency.Concurrent("Payment callback and allocation, 8 at a time", 30, 8, async i =>
            (await Callback(payments[30 + i], "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK));

        sequential.P95.ShouldBeLessThan(TimeSpan.FromSeconds(2), sequential.ToString());
        concurrent.P95.ShouldBeLessThan(TimeSpan.FromSeconds(2), concurrent.ToString());
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(DISTINCT payment_id) FROM payment.payment_allocation WHERE payment_id = ANY(@p)", ("p", payments.ToArray())))
            .ShouldBe(60L);
    }

    [Fact]
    public async Task UnknownOutcome_IsNeverTreatedAsSucceeded_PAY005()
    {
        // The simulator gives no answer for an amount ending in .99.
        var (_, invoice) = await IssueInvoice(150.99m);
        await _factory.DrainOutbox();

        var payment = await CollectionFor(invoice);
        var view = await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json();
        view.GetProperty("status").GetString().ShouldBe("Unknown");
        view.GetProperty("allocations").GetArrayLength().ShouldBe(0);
        (await Journals("PAYMENT", payment)).ShouldBeEmpty();
        (await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Issued");
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM payment.reconciliation_exception WHERE payment_id = @p AND status = 'OPEN'", ("p", payment))).ShouldBe(1L);

        // GAP-046: it counts among the payments waiting for a decision on the dashboard, until it is decided.
        async Task<(long Count, decimal Zar)> Unknown()
        {
            var figure = (await (await _maker.GetAsync("/api/v1/payments/summary")).Json()).GetProperty("figures").EnumerateArray().Single();
            figure.GetProperty("code").GetString().ShouldBe("PAYMENTS_UNKNOWN");
            return (figure.GetProperty("count").GetInt64(),
                figure.GetProperty("amounts").EnumerateArray().Where(a => a.GetProperty("currency").GetString() == "ZAR").Sum(a => a.GetProperty("amount").GetDecimal()));
        }

        var waiting = await Unknown();
        waiting.Count.ShouldBeGreaterThanOrEqualTo(1L);
        waiting.Zar.ShouldBeGreaterThanOrEqualTo(150.99m);

        // A person decides from evidence; only then is it received, allocated and posted. With a bank statement line matched
        // to it, one person decides (BR-042).
        await MatchedStatementLine(payment);
        var resolved = await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = true, evidence = "Bank statement 2026-10-21 line 7" });
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());
        (await resolved.Json()).GetProperty("status").GetString().ShouldBe("Succeeded");
        await _factory.DrainOutbox();
        (await Unknown()).ShouldBe((waiting.Count - 1, waiting.Zar - 150.99m));
        (await Journals("PAYMENT", payment)).Count.ShouldBe(1);
        (await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
    }

    /// <summary>
    /// BR-042: with no bank statement line matched to it, resolving an Unknown payment as succeeded is a request that a
    /// second person approves; the requester cannot, and nothing else resolves it while it waits.
    /// </summary>
    [Fact]
    public async Task ResolvingAnUnknownPaymentAsSucceeded_WithoutAMatchedStatementLine_NeedsASecondPerson_BR042()
    {
        var (_, invoice) = await IssueInvoice(160.99m);
        await _factory.DrainOutbox();
        var payment = await CollectionFor(invoice);

        var requested = await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = true, evidence = "Provider report 17" });
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted, await requested.Content.ReadAsStringAsync());
        var view = await requested.Json();
        ShouldMatchDocumented(view, "PaymentsResolveUnknown");
        view.GetProperty("status").GetString().ShouldBe("Unknown");
        view.GetProperty("resolutionRequest").GetProperty("evidence").GetString().ShouldBe("Provider report 17");

        // While it waits: not resolved another way, by anyone.
        (await (await _checker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = false, evidence = "Bounced" })).Code())
            .ShouldBe("PAYMENT_RESOLUTION_PENDING");

        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PaymentBusinessRefType, payment);
        var after = await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json();
        after.GetProperty("status").GetString().ShouldBe("Succeeded");
        after.GetProperty("resolutionRequest").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Journals("PAYMENT", payment)).Count.ShouldBe(1);
        (await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
    }

    /// <summary>BR-042: a rejected request leaves the payment Unknown; resolving it as failed stays one person's decision.</summary>
    [Fact]
    public async Task ARejectedResolution_LeavesThePaymentUnknown_AndResolvingAsFailedIsOnePersonsDecision_BR042()
    {
        var (_, invoice) = await IssueInvoice(170.99m);
        await _factory.DrainOutbox();
        var payment = await CollectionFor(invoice);
        (await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = true, evidence = "Provider report 18" }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.DrainOutbox();

        var approval = (await (await _checker.GetAsync(
            $"/api/v1/workflow/approvals?status=Requested&businessRefType={PaymentApprovals.PaymentBusinessRefType}&businessRefId={payment}")).Json())
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "No bank evidence" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var view = await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json();
        view.GetProperty("status").GetString().ShouldBe("Unknown");
        view.GetProperty("resolutionRequest").ValueKind.ShouldBe(JsonValueKind.Null);

        var failed = await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = false, evidence = "Returned unpaid" });
        failed.StatusCode.ShouldBe(HttpStatusCode.OK, await failed.Content.ReadAsStringAsync());
        (await failed.Json()).GetProperty("status").GetString().ShouldBe("Failed");
        (await Journals("PAYMENT", payment)).ShouldBeEmpty();
    }

    /// <summary>
    /// A payout batch is released only as approved: a payment in it changed after the approval (no API changes one, so an
    /// owner SQL update stands in) and the release is refused, PAYOUT_BATCH_CHANGED; put back as approved, it is released.
    /// </summary>
    [Fact]
    public async Task APayoutBatchThatChangedAfterItsReleaseWasApproved_IsNotReleased()
    {
        var payer = await _maker.RegisterPerson("Refundee changed");
        var bankAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payer, "62000555511");
        var (account, _) = await IssueInvoice(80m, payer);
        await _factory.DrainOutbox();
        var refund = (await (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/refunds",
            new { amount = 25m, reasonCode = "OVERPAID", bankAccountId = bankAccount }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(BillingApprovals.RefundBusinessRefType, refund);
        await _factory.DrainOutbox();
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PayoutBusinessRefType, batch);

        await fixture.Database.ExecuteAsOwner("UPDATE payment.payment SET amount = amount + 1 WHERE payout_batch_id = @b", ("b", batch));
        var refused = await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Code()).ShouldBe("PAYOUT_BATCH_CHANGED");
        (await (await _maker.GetAsync($"/api/v1/payments/payout-batches/{batch}")).Json()).GetProperty("status").GetString().ShouldBe("Approved");

        await fixture.Database.ExecuteAsOwner("UPDATE payment.payment SET amount = amount - 1 WHERE payout_batch_id = @b", ("b", batch));
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A bank statement line matched to the payment, by its reference and amount (PAY-003).</summary>
    /// <summary>
    /// BR-050, BR-052, BR-053: payments are found without their business record, by reference, status and initiation time,
    /// paged; each gives when it started, whom it pays, its bank account and why it failed, with its ETag. An allocation names
    /// its rule. A reconciliation exception reads alone, with its statement line, currency and raised and resolved times.
    /// </summary>
    [Fact]
    public async Task Payments_AreFoundWithoutTheirRecord_WithTheirDetails_AndAnExceptionReadsAlone_BR050_BR052_BR053()
    {
        var payer = await _maker.RegisterPerson("Findable");
        var (_, firstInvoice) = await IssueInvoice(120m, payer);
        var (_, secondInvoice) = await IssueInvoice(130m, payer);
        await _factory.DrainOutbox();
        var (failed, paid) = (await CollectionFor(firstInvoice), await CollectionFor(secondInvoice));
        (await Callback(failed, "BOUNCED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Callback(paid, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // By its own reference, alone: with its start, payee, failure reason and ETag.
        var failedRead = await _maker.GetAsync($"/api/v1/payments/payments/{failed}");
        var failedView = await failedRead.Json();
        failedRead.Headers.ETag!.Tag.ShouldBe($"\"{failedView.GetProperty("rowVersion").GetInt64()}\"");
        var byReference = await (await _maker.GetAsync($"/api/v1/payments/payments?reference={failedView.GetProperty("paymentRef").GetString()}")).Json();
        ShouldMatchDocumented(byReference, "PaymentsList");
        var found = byReference.GetProperty("items").EnumerateArray().Single();
        (found.GetProperty("id").GetGuid(), found.GetProperty("status").GetString(), found.GetProperty("counterpartyPartyId").GetGuid(),
            found.GetProperty("failureReason").GetString()).ShouldBe((failed, "Failed", payer, "Provider status BOUNCED"));
        found.GetProperty("initiatedAt").GetDateTimeOffset().ShouldBe(failedView.GetProperty("initiatedAt").GetDateTimeOffset());

        // By status and initiation time, paged one at a time: the failed one, not the paid one.
        var paidView = await (await _maker.GetAsync($"/api/v1/payments/payments/{paid}")).Json();
        paidView.GetProperty("failureReason").ValueKind.ShouldBe(JsonValueKind.Null);
        paidView.GetProperty("allocations").EnumerateArray().Single().GetProperty("rule").GetString().ShouldNotBeNullOrEmpty();
        var from = Uri.EscapeDataString(failedView.GetProperty("initiatedAt").GetDateTimeOffset().ToString("O"));
        var to = Uri.EscapeDataString(paidView.GetProperty("initiatedAt").GetDateTimeOffset().AddTicks(1).ToString("O"));
        var failures = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await (await _maker.GetAsync($"/api/v1/payments/payments?status=Failed&from={from}&to={to}&limit=1{(cursor is null ? "" : $"&cursor={cursor}")}")).Json();
            failures.AddRange(page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString() is { } next ? Uri.EscapeDataString(next) : null;
        }
        while (cursor is not null);
        failures.ShouldContain(failed);
        failures.ShouldNotContain(paid);
        failures.Distinct().Count().ShouldBe(failures.Count);
        (await (await _maker.GetAsync("/api/v1/payments/payments?businessRefType=INVOICE")).Code()).ShouldBe("VALIDATION_FAILED");

        // An unmatched statement line is an exception that reads alone, with its line and currency, and when it was raised and resolved.
        var reference = $"NOMATCH-{Guid.NewGuid():N}"[..20];
        (await _maker.Send(HttpMethod.Post, "/api/v1/payments/bank-statements",
            new { accountRef = "BANK-1", lines = new[] { new { date = Today, amount = 77.70m, currency = "ZAR", reference } } }, idempotent: true)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var listed = (await (await _maker.GetAsync("/api/v1/payments/reconciliation-exceptions?status=Open&limit=200")).Json()).EnumerateArray()
            .Single(e => e.GetProperty("statementLine").ValueKind == JsonValueKind.Object && e.GetProperty("statementLine").GetProperty("reference").GetString() == reference);
        var exceptionId = listed.GetProperty("id").GetGuid();
        var exception = await (await _maker.GetAsync($"/api/v1/payments/reconciliation-exceptions/{exceptionId}")).Json();
        ShouldMatchDocumented(exception, "PaymentsGetException");
        (exception.GetProperty("exceptionType").GetString(), exception.GetProperty("currency").GetString(), exception.GetProperty("statementLine").GetProperty("amount").GetDecimal(),
            exception.GetProperty("resolvedAt").ValueKind).ShouldBe(("UNMATCHED_LINE", "ZAR", 77.70m, JsonValueKind.Null));
        exception.GetProperty("raisedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.MinValue);
        (await _maker.PostAsync($"/api/v1/payments/reconciliation-exceptions/{exceptionId}/assign", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = await (await _maker.PostAsync($"/api/v1/payments/reconciliation-exceptions/{exceptionId}/resolve", null)).Json();
        resolved.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await _maker.GetAsync($"/api/v1/payments/reconciliation-exceptions/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task MatchedStatementLine(Guid payment)
    {
        var view = await (await _maker.GetAsync($"/api/v1/payments/payments/{payment}")).Json();
        var imported = await _maker.Send(HttpMethod.Post, "/api/v1/payments/bank-statements", new
        {
            accountRef = "BANK-1",
            lines = new[] { new { date = Today, amount = view.GetProperty("amount").GetDecimal(), currency = "ZAR", reference = view.GetProperty("paymentRef").GetString() } },
        }, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        (await imported.Json()).GetProperty("matched").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Refund_AndItsPayoutBatch_EachNeedASecondPerson_SRS002()
    {
        var payer = await _maker.RegisterPerson("Refundee");
        var bankAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payer, "62000555501");
        var (account, _) = await IssueInvoice(80m, payer);
        await _factory.DrainOutbox();

        // A refund goes only to the payer's own verified account, by id; no account number is sent.
        var other = await _maker.AddVerifiedBankAccount(_checker, _factory, await _maker.RegisterPerson("Stranger"), "62000555502");
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/refunds", new { amount = 25m, reasonCode = "OVERPAID", bankAccountId = other },
            idempotent: true)).Code()).ShouldBe("REFUND_ACCOUNT_NOT_PAYABLE");
        var refund = (await (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/refunds",
            new { amount = 25m, reasonCode = "OVERPAID", bankAccountId = bankAccount }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(BillingApprovals.RefundBusinessRefType, refund);
        (await (await _maker.GetAsync($"/api/v1/billing/refunds/{refund}")).Json()).GetProperty("status").GetString().ShouldBe("Approved");

        // GAP-036: the payout exists from approval, and the refund shows it before it is paid; it can be found from the refund.
        await _factory.DrainOutbox();
        var created = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", refund));
        (await (await _maker.GetAsync($"/api/v1/billing/refunds/{refund}")).Json()).GetProperty("paymentId").GetGuid().ShouldBe(created);
        var found = await (await _maker.GetAsync($"/api/v1/payments/payments?businessRefType={BillingApprovals.RefundBusinessRefType}&businessRefId={refund}")).Json();
        ShouldMatchDocumented(found, "PaymentsList");
        found.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(created);

        // The approved refund waits for a payout batch, which a second person approves before release.
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PayoutBusinessRefType, batch);

        // BR-051: the batch is listed by status, purpose and currency, with when it was built and the approval that approved it.
        var approval = (await (await _maker.GetAsync($"/api/v1/workflow/approvals?businessRefType={PaymentApprovals.PayoutBusinessRefType}&businessRefId={batch}")).Json())
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var batches = await (await _maker.GetAsync($"/api/v1/payments/payout-batches?status=Approved&purpose={PaymentPurposes.Refund}&currency=ZAR&limit=200")).Json();
        ShouldMatchDocumented(batches, "PaymentsListBatches");
        var listedBatch = batches.GetProperty("items").EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == batch);
        (listedBatch.GetProperty("approvalId").GetGuid(), listedBatch.GetProperty("builtAt").ValueKind).ShouldBe((approval, JsonValueKind.String));
        (await (await _maker.GetAsync("/api/v1/payments/payout-batches?status=Built&limit=200")).Json()).GetProperty("items").EnumerateArray()
            .ShouldNotContain(b => b.GetProperty("id").GetGuid() == batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _maker.GetAsync($"/api/v1/payments/payout-batches/{batch}/payments")).Json()).EnumerateArray().Select(p => p.GetProperty("id").GetGuid())
            .ShouldBe([created]);

        var payout = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", refund));
        (await Callback(payout, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await (await _maker.GetAsync($"/api/v1/billing/refunds/{refund}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
        (await Journals("PAYMENT", payout)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString().ShouldBe("PR-REFUND");
    }

    [Fact]
    public async Task ClaimPayment_IsReleasedOnlyAfterApproval_AndDrawsDownTheReserve_SRS002_CLM004()
    {
        var claim = await (await _maker.PostAsJsonAsync("/api/v1/claims/claims",
            new { domain = "GENERAL", domainClaimId = Guid.CreateVersion7(), policyRef = Guid.CreateVersion7() })).Json();
        var number = claim.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("GEN-");
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 1000m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var payee = await _maker.RegisterPerson("Claimant");
        var bankAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62000777701");
        var drafted = await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/payment-instructions",
            new { instructionRef = "SETTLE-1", payeePartyId = payee, bankAccountId = bankAccount, amount = 400m, approvedAmount = 400m, currency = "ZAR", reserveType = "INDEMNITY" });
        drafted.StatusCode.ShouldBe(HttpStatusCode.Created);
        var instruction = (await drafted.Json()).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/claims/payment-instructions/{instruction}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/claims/payment-instructions/{instruction}/release", null)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Approve(ClaimsApprovals.InstructionBusinessRefType, instruction);
        (await _maker.PostAsync($"/api/v1/claims/payment-instructions/{instruction}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // GAP-036: the released instruction shows its payment before the payment completes, and the claim lists its instructions.
        var created = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", instruction));
        var instructions = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/payment-instructions")).Json();
        ShouldMatchDocumented(instructions, "ClaimsListInstructions");
        instructions.EnumerateArray().Single().GetProperty("paymentId").GetGuid().ShouldBe(created);

        var batch = (await (await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" },
            idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payout = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", instruction));
        (await Callback(payout, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
        var financials = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/financials")).Json();
        financials.GetProperty("outstandingReserves").TryGetProperty("INDEMNITY", out _).ShouldBeTrue(financials.ToString());
        financials.GetProperty("outstandingReserves").GetProperty("INDEMNITY").GetDecimal().ShouldBe(600m);
        var paidMovement = financials.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("movementType").GetString() == "Payment").GetProperty("id").GetGuid();
        (await Journals("CLAIM_MOVEMENT", paidMovement)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString()
            .ShouldBe("PR-CLM-PAID");
    }

    /// <summary>
    /// An automatic release refused by the reserve never stops silently: the approved instruction is held with why, the
    /// claims role gets a Workflow task, and a person releases it once the reserve is raised.
    /// </summary>
    [Fact]
    public async Task AutomaticRelease_RefusedByTheReserve_IsHeldWithItsReason_AndTasksTheClaimsRole()
    {
        var number = (await (await _maker.PostAsJsonAsync("/api/v1/claims/claims",
            new { domain = "GENERAL", domainClaimId = Guid.CreateVersion7(), policyRef = Guid.CreateVersion7() })).Json()).GetProperty("claimNumber").GetString()!;
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 100m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var payee = await _maker.RegisterPerson("Held Payee");
        var bankAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62000888801");
        var hash = await fixture.Database.ScalarAsOwner<string>("SELECT account_number_hash FROM party.bank_account WHERE id = @a", ("a", bankAccount));

        var approved = new Insys.General.Contracts.GeneralClaimApproved(Guid.CreateVersion7(), number, payee, bankAccount, hash!, 400m, "ZAR", "INDEMNITY",
            Guid.CreateVersion7(), Guid.CreateVersion7());
        await _factory.Services.GetRequiredService<IEventBus>().Publish(new Insys.BuildingBlocks.Contracts.EventEnvelope(
            Guid.CreateVersion7(), Tenant, "general.GeneralClaimApproved", 1, DateTimeOffset.UtcNow, "GeneralClaim", approved.ClaimId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(approved, JsonSerializerOptions.Web)), CancellationToken.None);

        var instruction = (await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/payment-instructions")).Json()).EnumerateArray().Single();
        instruction.GetProperty("status").GetString().ShouldBe("Approved");
        instruction.GetProperty("heldReason").GetString().ShouldBe("RESERVE_BELOW_ZERO");
        instruction.GetProperty("heldDetail").GetString()!.ShouldContain("400");
        var id = instruction.GetProperty("id").GetGuid();

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT t.queue_code FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'CLAIM_PAYMENT_HELD' AND p.business_ref_id = @i AND t.status = 'OPEN'
            """, ("i", id))).ShouldBe("CLAIMS_MANAGER");

        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 500m, currency = "ZAR", reason = "Assessed" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/claims/payment-instructions/{id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var released = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{id}")).Json();
        released.GetProperty("status").GetString().ShouldBe("Released");
        released.GetProperty("heldReason").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static void ShouldMatchDocumented(JsonElement response, string operationId) =>
        DocumentedSchema.ResponseProblems(response, "core", operationId).ShouldBeEmpty(operationId);

    private async Task<(Guid Account, Guid Invoice)> IssueInvoice(decimal instalment, Guid? payer = null)
    {
        var account = (await (await _maker.Send(HttpMethod.Post, "/api/v1/billing/accounts/",
            new { payerPartyId = payer ?? Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" }, idempotent: true)).Json()).GetProperty("id").GetGuid();
        (await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/schedules",
            new
            {
                sourceDomain = "GENERAL", policyRef = Guid.CreateVersion7(), productVersionRef = "MOTOR-v1", frequency = "MONTHLY", startDate = Today,
                endDate = (DateOnly?)null, instalmentAmount = instalment,
            }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await _maker.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{account}/invoices", new { asOf = Today }, idempotent: true);
        issued.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (account, (await issued.Json()).EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    private async Task<Guid> CollectionFor(Guid invoice) =>
        await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_type = 'INVOICE' AND business_ref_id = @i", ("i", invoice));

    private Task<HttpResponseMessage> Callback(Guid payment, string status, string? signingSecret = null) =>
        _maker.SimulatorCallback(_factory, fixture.Database, payment, status, signingSecret);

    /// <summary>The maker cannot approve their own request; the checker can (SRS-002).</summary>
    private async Task Approve(string businessRefType, Guid businessRefId)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
    }

    /// <summary>OQ-104: case redress paid out through Payments is posted to the ledger (REDRESS_PAID), as a refund is, once.</summary>
    [Fact]
    public async Task PaidCaseRedress_IsPostedToTheLedger_Once_OQ104()
    {
        var payment = Guid.CreateVersion7();
        var succeeded = new BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), Tenant, "payment.PaymentSucceeded", 1, DateTimeOffset.UtcNow, "Payment", payment,
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(new PaymentSucceeded(payment, "PAY-REDRESS-1", PaymentPurposes.CaseRedress, 750.25m, "ZAR", Today,
                CaseApprovals.RedressBusinessRefType, Guid.CreateVersion7()), BuildingBlocks.Infrastructure.Persistence.ModuleDbContext.EventJson));
        var bus = _factory.Services.GetRequiredService<IEventBus>();
        await bus.Publish(succeeded, CancellationToken.None);
        await bus.Publish(succeeded, CancellationToken.None);
        await _factory.DrainOutbox();

        var journal = (await Journals("PAYMENT", payment)).Single();
        journal.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("debit").GetDecimal()).ShouldBe(750.25m);
        journal.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("credit").GetDecimal()).ShouldBe(750.25m);
    }

    private async Task<List<JsonElement>> Journals(string businessRefType, Guid businessRefId)
    {
        var response = await _maker.GetAsync($"/api/v1/finance/journals?businessRefType={businessRefType}&businessRefId={businessRefId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Json()).EnumerateArray().ToList();
    }

    private static (string, object)[] Rule(string ruleCode, string transactionType, string debit, string credit) =>
        [("rule_code", ruleCode), ("transaction_type", transactionType), ("debit_account", debit), ("credit_account", credit)];

    private Task Seed(string table, string recordStatus, string code, int version, DateOnly from, DateOnly? to, params (string Column, object Value)[] fields)
    {
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var values = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        return fixture.Database.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, effective_to, record_status, created_by, change_reason, {columns})
            VALUES (@t, @code, @version, @from, @to, @status, 'seed', 'test seed', {values})
            ON CONFLICT DO NOTHING
            """,
            [("t", Tenant), ("code", code), ("version", version), ("from", from), ("to", (object?)to ?? DBNull.Value), ("status", recordStatus),
             .. fields.Select((f, i) => ($"p{i}", f.Value))]);
    }
}
