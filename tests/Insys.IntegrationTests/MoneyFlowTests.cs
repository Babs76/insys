using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Finance.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;

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
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static readonly string[] Permissions =
    [
        Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate,
        Insys.Party.Contracts.PartyPermissions.BankAccountVerify,
        BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead,
        BillingPermissions.InvoiceCreate, BillingPermissions.RefundRead, BillingPermissions.RefundCreate, PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve,
        PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.ReconciliationRead,
        PaymentPermissions.CallbackCreate, FinancePermissions.JournalRead, FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate,
        FinancePermissions.PostingExceptionRead, ClaimsPermissions.ClaimRead, ClaimsPermissions.ClaimCreate, ClaimsPermissions.FinancialsRead,
        ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionCreate, ClaimsPermissions.InstructionRelease,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database);
        var db = fixture.Database;
        await db.UserWithPermissions(Tenant, "money-maker", Permissions);
        var checker = await db.UserWithPermissions(Tenant, "money-checker", Permissions);
        _maker = _factory.ClientFor(Tenant, "money-maker");
        _checker = _factory.ClientFor(Tenant, "money-checker");

        // The checker holds approval authority for refunds, payout batches and claim payments (WFL-004).
        var approverRole = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, checker, approverRole);
        await db.AllowBankAccountVerification(Tenant, approverRole, Today.AddDays(-90));
        foreach (var type in new[] { BillingApprovals.RefundApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", 1, Today.AddDays(-90), null,
                ("role_code", approverRole), ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", 1, Today.AddDays(-90), null,
            ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));

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

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

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
        (await Callback(payment, "PAID", signature: "not-the-signature")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
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

        // A person decides from evidence; only then is it received, allocated and posted.
        (await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/resolve", new { succeeded = true, evidence = "Bank statement 2026-10-21 line 7" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Journals("PAYMENT", payment)).Count.ShouldBe(1);
        (await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
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

        // The approved refund waits for a payout batch, which a second person approves before release.
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Refund, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Approve(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

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
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 1000m, currency = "ZAR" }))
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

    private async Task<HttpResponseMessage> Callback(Guid payment, string status, string? signature = null)
    {
        var externalRef = await fixture.Database.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));
        var body = JsonSerializer.Serialize(new { externalRef, status }, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/providers/SIMULATOR/callbacks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Signature", signature
            ?? Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestTokens.SimulatorCallbackSecret), Encoding.UTF8.GetBytes(body))));
        return await _maker.SendAsync(request);
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
