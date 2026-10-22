using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.Finance.Contracts;
using Insys.General.Contracts;
using Insys.Integration.Contracts;
using Insys.Payments.Contracts;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.EndToEndTests;

/// <summary>
/// The General processes of domain-model.json, through the API with the real migrations and every module involved:
/// "General quote to cash" (§27.3 scenarios 2–4) and "General claim to settlement" (§27.3 scenario 5, GEN-006 to
/// GEN-008), with their compensations, and endorsement and cancellation (GEN-003, GEN-004). Commission and
/// reinsurance steps come with those modules in Phase 6.
/// </summary>
public sealed class GeneralProcessTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-GMOT";
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly Guid Assessor = Guid.CreateVersion7();
    private static readonly Guid Repairer = Guid.CreateVersion7();

    private static readonly string[] Permissions =
    [
        GeneralPermissions.RiskRead, GeneralPermissions.RiskCreate, GeneralPermissions.RiskUpdate, GeneralPermissions.QuoteRead, GeneralPermissions.QuoteCreate,
        GeneralPermissions.QuoteUpdate, GeneralPermissions.QuoteUnderwrite, GeneralPermissions.PolicyRead, GeneralPermissions.PolicyCreate, GeneralPermissions.PolicyUpdate,
        GeneralPermissions.PolicyCancel, GeneralPermissions.EndorsementRead, GeneralPermissions.EndorsementCreate, GeneralPermissions.ClaimRead, GeneralPermissions.ClaimCreate,
        GeneralPermissions.ClaimUpdate, GeneralPermissions.ClaimDecide, GeneralPermissions.AssessmentUpdate, GeneralPermissions.RecoveryUpdate,
        RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease,
        PaymentPermissions.CallbackCreate, ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate,
        ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionRelease, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate,
        DocumentPermissions.DocumentRead,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        await _db.SeedTenant(Tenant, "TENA");
        await _db.UserWithPermissions(Tenant, "underwriter", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "senior-underwriter", Permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);
        var from = Today.AddDays(-30);

        foreach (var type in new[] { GeneralApprovals.EndorsementApprovalType, GeneralApprovals.SettlementApprovalType, PaymentApprovals.PayoutApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}",
                ("role_code", approverRole), ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        // A published General motor product: coverages, a limit and an excess schedule, and its document templates.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason, labels,
                                                                     product_code, domain, name, coverages)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', '{"family":"GEN-MOTOR","class":"MOTOR"}', @p, 'GENERAL', 'Motor', @coverages)
            """, ("t", Tenant), ("p", Product), ("from", from), ("coverages", new[] { "OWN_DAMAGE", "THEFT" }));
        await Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-LIM", ("schedule_code", "MOT-LIM"), ("coverage", "OWN_DAMAGE"), ("limit_type", "LIMIT"),
            ("amount", 500_000m), ("rule", "FIXED"), ("currency", "ZAR"));
        await Seed("product.cfg_limit_and_deductible_schedules", "EFFECTIVE", "MOT-EXC", ("schedule_code", "MOT-EXC"), ("coverage", "OWN_DAMAGE"), ("limit_type", "EXCESS"),
            ("amount", 5_000m), ("percentage", 5m), ("rule", "GREATER_OF"), ("currency", "ZAR"));
        foreach (var (type, fields) in new[]
                 {
                     ("POLICY_SCHEDULE", new[] { "policy_number", "annual_premium" }), ("CERTIFICATE", new[] { "policy_number", "period_start" }),
                     ("ENDORSEMENT", new[] { "endorsement_number", "effective_date" }),
                 })
        {
            await Seed("document.ref_document_categories", "ACTIVE", type, ("category_code", type), ("label", type), ("classification", "PERSONAL"), ("domain", "ALL"),
                ("allowed_roles", Array.Empty<string>()));
            await Seed("document.cfg_document_templates", "EFFECTIVE", $"TPL-{type}", ("template_code", $"TPL-{type}"), ("language", "en"), ("document_type", type),
                ("body_ref", $"doc://templates/{type}/v1"), ("merge_fields", fields));
            if (type != "CERTIFICATE")
            {
                await AssignTemplate(type);
            }
        }

        // Rating: premium = sum insured × 0.03 for vehicle group G3, plus a fee of 50.
        await Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", ("factor_code", "VEHICLE_GROUP"), ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await _db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'GMOT-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'GMOT-BASE', ARRAY['VEHICLE_GROUP'], '[{"key":["G3"],"value":0.03}]', 'NONE')
            """, ("t", Tenant), ("from", from));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P1", ("parameter_code", "GMOT-BASE"), ("product_version", $"{Product} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GMOT-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{Product} v1"), ("value", 50m), ("unit", "AMOUNT"));
        await Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));

        // General: the vehicle type, quote validity, numbering, cancellation, endorsement and renewal rules, codes and panels.
        await _db.ExecuteAsOwner("""
            INSERT INTO general.cfg_risk_object_types_and_attribute_schemas (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                             object_type, attributes, validation)
            VALUES (@t, 'VEHICLE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'VEHICLE', '["registration:String(10):Y","vehicle_group:String(5):Y","year:Integer:Y"]',
                    '["year <= current_year"]')
            """, ("t", Tenant), ("from", from));
        await Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-GMOT", ("product_code", Product), ("days_valid", 30), ("re_rate_on_expiry", false));
        await Seed("general.cfg_policy_numbering_schemes", "EFFECTIVE", "GP", ("product_line", "GENERAL"), ("prefix", "GP"), ("sequence_length", 9));
        await Seed("general.cfg_cancellation_reasons_and_refund_rules", "EFFECTIVE", "INS_REQUEST", ("reason_code", "INS_REQUEST"), ("initiator", "INSURED"),
            ("refund_basis", "PRO_RATA"), ("fee", 0m));
        await Seed("general.cfg_endorsement_types", "EFFECTIVE", "CHANGE_SI", ("premium_method", "PRO_RATA"), ("documents", new[] { "ENDORSEMENT" }), ("requires_underwriting", false));
        await Seed("general.ref_loss_cause_codes", "ACTIVE", "COLLISION", ("cause_code", "COLLISION"), ("peril", "ACC"), ("label", "Collision"));
        await Seed("general.ref_peril_codes", "ACTIVE", "ACC", ("peril_code", "ACC"), ("label", "Accident"), ("line", "MOTOR"), ("catastrophe", false));
        await Seed("general.ref_recovery_types", "ACTIVE", "SUBROGATION", ("label", "Subrogation"));
        await Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "ASSESSOR-1", ("party", Assessor.ToString()), ("panel_type", "ASSESSOR"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));
        await Seed("general.mst_repairer_and_assessor_panels", "ACTIVE", "REPAIRER-1", ("party", Repairer.ToString()), ("panel_type", "REPAIRER"), ("location", "CPT"),
            ("approved_lines", new[] { "MOTOR" }), ("status", "ACTIVE"));

        // Billing, Payments, Claims and Finance, as in Phase 3.
        await Seed("billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
        await Seed("billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await Seed("billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "INS_REQUEST", ("label", "Cancelled at the insured's request"),
            ("requires_approval", false), ("accounting_impact", "CREDIT"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"), ("currencies", new[] { "ZAR" }),
            ("countries", new[] { "ZA" }));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await Seed("payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"),
            ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        foreach (var (status, platform) in new[] { ("PAID", "SUCCEEDED"), ("BOUNCED", "FAILED") })
        {
            await Seed("payment.ref_payment_status_mappings", "ACTIVE", $"SIM-{status}", ("provider_code", "SIMULATOR"), ("provider_status", status),
                ("platform_status", platform), ("final", true));
        }

        await Seed("claims.ref_reserve_types", "ACTIVE", "INDEMNITY", ("category", "INDEMNITY"), ("posting_rule", "PR-CLM-UP"));
        await Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "GENERAL", ("domain", "GENERAL"), ("prefix", "GEN"), ("sequence_length", 8));
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        foreach (var account in new[] { "1100", "1200", "2100", "4000", "5000", "6000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CREDIT", TransactionTypes.PremiumCredited, "4000", "1100"), ("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"),
                     ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"), ("PR-CLM-REC", TransactionTypes.ClaimRecovery, "1200", "5000"),
                     ("PR-REVERSED", TransactionTypes.PaymentReversed, "1100", "1200"),
                 })
        {
            await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{month:yyyy-MM}", ("period_code", $"{month:yyyy-MM}"), ("start_date", month),
            ("end_date", month.AddMonths(1).AddDays(-1)));

        // Fast retries, so a failing consumer is dead-lettered within one drain of the outbox.
        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" });
        _maker = _factory.ClientFor(Tenant, "underwriter");
        _checker = _factory.ClientFor(Tenant, "senior-underwriter");
        (await _maker.PostAsync($"/api/v1/finance/periods/{month:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GeneralQuoteToCash_WithItsCompensations()
    {
        var (policy, _) = await IssuedPolicy();
        await _factory.DrainOutbox();

        // GenerateDocument: the certificate has no template assigned, so generation fails, is retried and dead-lettered.
        // Compensation: the policy is not rolled back, and the documents are regenerated by replaying it once fixed.
        (await Policy(policy)).GetProperty("status").GetString().ShouldBe("InForce");
        (await Documents(policy, "POLICY_SCHEDULE")).ShouldBe(0);
        (await Documents(policy, "CERTIFICATE")).ShouldBe(0);
        var deadLetter = await _db.ScalarAsOwner<Guid>("""
            SELECT id FROM integration.dead_letter_message
            WHERE source = 'outbox:general' AND payload ->> 'eventType' = 'general.GeneralPolicyIssued' AND resolution IS NULL
            """);
        deadLetter.ShouldNotBe(Guid.Empty);
        await AssignTemplate("CERTIFICATE");
        var replay = await _maker.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{deadLetter}/replay", ifMatch: "\"1\"");
        replay.StatusCode.ShouldBe(HttpStatusCode.NoContent, await replay.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await Documents(policy, "CERTIFICATE")).ShouldBe(1);
        (await Documents(policy, "POLICY_SCHEDULE")).ShouldBe(1);

        // OpenAccount, CreateSchedule and IssueInvoice: the first monthly instalment of 3 050.00, posted (PREMIUM_DUE).
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p AND document_type = 'INVOICE'", ("p", policy));
        var issued = await (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json();
        issued.GetProperty("totalAmount").GetDecimal().ShouldBe(254.17m);
        (await Journals("INVOICE", invoice)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString().ShouldBe("PR-PREM");

        // InitiatePayment (collection), the provider confirms, PaymentReceived and PaymentAllocated, MarkSettled, posted.
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await InvoiceStatus(invoice)).ShouldBe("Paid");
        (await Journals("PAYMENT", payment)).Count.ShouldBe(1);

        // Compensation: the payment is reversed; the invoice reopens.
        (await _maker.PostAsJsonAsync($"/api/v1/payments/payments/{payment}/reverse", new { reason = "Debit order returned" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await InvoiceStatus(invoice)).ShouldBe("Issued");
        (await Journals("PAYMENT", payment)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task GeneralClaimToSettlement_WithItsCompensations()
    {
        var (policy, vehicle) = await IssuedPolicy();
        await AssignTemplate("CERTIFICATE");
        await _factory.DrainOutbox();

        // NotifyLoss, then RegisterClaim: the cover is validated and the Claims Framework allocates the number.
        var uncovered = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "THEFT", lossDate = Today.AddDays(-400), causeCode = "COLLISION", perilCode = "ACC",
        });
        (await (await _maker.PostAsync($"/api/v1/general/claims/{uncovered}/register", null)).Code()).ShouldBe("LOSS_NOT_COVERED");
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
            description = "Rear-ended at a robot",
        });
        var registered = await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json();
        var number = registered.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("GEN-");
        registered.GetProperty("status").GetString().ShouldBe("Registered");
        (await _db.ScalarAsOwner<Guid>("SELECT domain_claim_id FROM claims.claim_register WHERE claim_number = @n", ("n", number))).ShouldBe(claim);
        (await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json()).GetProperty("claimNumber").GetString().ShouldBe(number);

        // SetReserve (Claims Framework), posted.
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 50_000m, currency = "ZAR" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // AppointAssessor; the loss assessment is reported and accepted. The excess is the greater of 5 000 and 5 %.
        (await (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Guid.CreateVersion7() })).Code())
            .ShouldBe("ASSESSOR_NOT_ON_PANEL");
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/assessor", new { assessorPartyId = Assessor })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var assessment = (await (await _maker.GetAsync($"/api/v1/general/claims/{claim}/assessments")).Json()).EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/estimates", new { repairerPartyId = Repairer, amount = 41_000m }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var reported = await (await _maker.PostAsJsonAsync($"/api/v1/general/assessments/{assessment}/report", new { assessedAmount = 40_000m })).Json();
        reported.GetProperty("excessApplied").GetDecimal().ShouldBe(5_000m);
        (await _maker.PostAsync($"/api/v1/general/assessments/{assessment}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // RequestApproval for the settlement. Compensation: rejected, the claim stays under assessment.
        var payee = Guid.CreateVersion7();
        var settlement = new { payeePartyId = payee, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Repair estimate within the assessed amount" };
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement", settlement)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: false);
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("UnderAssessment");

        // Again: maker and checker differ; GeneralClaimApproved; DraftInstruction and ReleaseInstruction for 35 000.00.
        (await _maker.PostAsJsonAsync($"/api/v1/general/claims/{claim}/settlement", settlement)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(GeneralApprovals.SettlementBusinessRefType, claim, approve: true);
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Approved");
        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        var instructed = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        instructed.GetProperty("status").GetString().ShouldBe("Released");
        instructed.GetProperty("amount").GetDecimal().ShouldBe(35_000m);

        // Payout batch, approved and released. Compensation: the payment fails; the instruction returns to Failed and is
        // re-released with corrected details, as a new payment.
        var first = await PayOut(instruction, "BOUNCED");
        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Failed");
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Approved");
        (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var second = await PayOut(instruction, "PAID");
        second.ShouldNotBe(first);

        // ClaimPaymentCompleted → GeneralClaimSettled; the payment draws down the reserve and is posted.
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Settled");
        var financials = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/financials")).Json();
        financials.GetProperty("outstandingReserves").GetProperty("INDEMNITY").GetDecimal().ShouldBe(15_000m);
        var paid = financials.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("movementType").GetString() == "Payment").GetProperty("id").GetGuid();
        (await Journals("CLAIM_MOVEMENT", paid)).Single().GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleCode").GetString().ShouldBe("PR-CLM-PAID");

        // A third-party recovery, recorded as a claim financial movement (CLM-004), then the claim is closed.
        var recovery = await Created($"/api/v1/general/claims/{claim}/recoveries", new { recoveryType = "SUBROGATION", counterpartyPartyId = Guid.CreateVersion7(), expectedAmount = 20_000m });
        (await _maker.PostAsync($"/api/v1/general/recoveries/{recovery}/demand", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.Send(HttpMethod.Post, $"/api/v1/general/recoveries/{recovery}/recovered", new { amount = 12_000m }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        financials = await (await _maker.GetAsync($"/api/v1/claims/claims/{number}/financials")).Json();
        financials.GetProperty("movements").EnumerateArray().Count(m => m.GetProperty("movementType").GetString() == "Recovery").ShouldBe(1);
        (await _maker.PostAsync($"/api/v1/general/claims/{claim}/close", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Closed");
    }

    [Fact]
    public async Task Endorsement_IsAppliedAfterApproval_AndCancellationCreditsTheRefund_GEN003_GEN004()
    {
        var (policy, vehicle) = await IssuedPolicy();
        await AssignTemplate("CERTIFICATE");
        await _factory.DrainOutbox();

        var endorsement = await Created($"/api/v1/general/policies/{policy}/endorsements", new
        {
            endorsementType = "CHANGE_SI", effectiveDate = Today,
            changes = new[] { new { action = "ChangeRisk", riskObjectId = vehicle, sumInsured = 150_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 150_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Endorsement, endorsement, 150_000m);
        var rated = await (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).Json();
        rated.GetProperty("status").GetString().ShouldBe("Rated");
        rated.GetProperty("newAnnualPremium").GetDecimal().ShouldBe(4_550m);

        (await _maker.PostAsync($"/api/v1/general/endorsements/{endorsement}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Policy(policy)).GetProperty("annualPremium").GetDecimal().ShouldBe(3_050m);
        await Decide(GeneralApprovals.EndorsementBusinessRefType, endorsement, approve: true);

        // Applied: the policy's premium and coverages, the billing schedule from the effective date, and the endorsement document.
        var endorsed = await Policy(policy);
        endorsed.GetProperty("annualPremium").GetDecimal().ShouldBe(4_550m);
        var applied = await (await _maker.GetAsync($"/api/v1/general/endorsements/{endorsement}")).Json();
        applied.GetProperty("status").GetString().ShouldBe("Applied");
        applied.GetProperty("documentId").ValueKind.ShouldBe(JsonValueKind.String);
        (await _db.ScalarAsOwner<decimal>("SELECT instalment_amount FROM billing.premium_schedule WHERE policy_ref = @p", ("p", policy))).ShouldBe(379.17m);

        // CancelPolicy: pro rata refund, the schedule stops, and the refund is credited against the invoice.
        var cancelled = await (await _maker.PostAsJsonAsync($"/api/v1/general/policies/{policy}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = Today })).Json();
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>(
            "SELECT count(*) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id WHERE s.policy_ref = @p AND i.status = 'SCHEDULED'",
            ("p", policy))).ShouldBe(0L);
        (await _db.ScalarAsOwner<decimal>("SELECT total_amount FROM billing.invoice WHERE policy_ref = @p AND document_type = 'CREDIT_NOTE'", ("p", policy))).ShouldBe(254.17m);

        (await _maker.PostAsync($"/api/v1/general/policies/{policy}/reinstate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Policy(policy)).GetProperty("status").GetString().ShouldBe("InForce");
    }

    /// <summary>A vehicle, a quote rated through Rating, issued and accepted (GeneralQuoteAccepted), a proposal, and the policy issued from it.</summary>
    private async Task<(Guid Policy, Guid Vehicle)> IssuedPolicy()
    {
        var vehicle = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA123456", vehicle_group = "G3", year = Today.Year - 2 }, valuation = 180_000m, valuationBasis = (string?)null,
        }, expectFailureCode: "VALUATION_BASIS_REQUIRED");
        vehicle = await Created("/api/v1/general/risk-objects", new
        {
            objectType = "VEHICLE", attributes = new { registration = "CA123456", vehicle_group = "G3", year = Today.Year - 2 },
        });
        var quote = await Created("/api/v1/general/quotes", new
        {
            productCode = Product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1),
            risks = new[]
            {
                new
                {
                    riskObjectId = vehicle, sumInsured = 100_000m,
                    coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" }, new { coverageCode = "THEFT", sumInsured = 50_000m } },
                },
            },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 100_000m);
        (await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json()).GetProperty("premium").GetDecimal().ShouldBe(3_050m);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = await Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        (await _maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var policy = await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });
        (await Policy(policy)).GetProperty("risks")[0].GetProperty("coverages").EnumerateArray().Sum(c => c.GetProperty("premium").GetDecimal()).ShouldBe(3_050m);
        return (policy, vehicle);
    }

    private async Task Rate(string subjectType, Guid subjectId, decimal sumInsured)
    {
        var calculated = await _maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"E2E-{subjectId:N}"[..20], sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType, subjectId, productCode = Product, productVersion = 1,
            currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = sumInsured, vehicle_group = "G3" },
        }, idempotent: true);
        calculated.StatusCode.ShouldBe(HttpStatusCode.Created, await calculated.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    /// <summary>Batches the instruction's payout, approves it as the checker, releases it, and answers it with the provider's status.</summary>
    private async Task<Guid> PayOut(Guid instruction, string providerStatus)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch, approve: true);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i AND payout_batch_id = @b", ("i", instruction), ("b", batch));
        (await Callback(payment, providerStatus)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        return payment;
    }

    /// <summary>The maker cannot decide their own request (SRS-002); the checker does.</summary>
    private async Task Decide(string businessRefType, Guid businessRefId, bool approve)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var decided = approve
            ? await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")
            : await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Check the estimate" }, ifMatch: "\"1\"");
        decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private async Task<HttpResponseMessage> Callback(Guid payment, string status)
    {
        var externalRef = await _db.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));
        var body = JsonSerializer.Serialize(new { externalRef, status }, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/providers/SIMULATOR/callbacks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestTokens.SimulatorCallbackSecret), Encoding.UTF8.GetBytes(body))));
        return await _maker.SendAsync(request);
    }

    private async Task<Guid> Created(string url, object body, string? expectFailureCode = null)
    {
        var response = await _maker.Send(HttpMethod.Post, url, body, idempotent: true);
        if (expectFailureCode is not null)
        {
            (await response.Code()).ShouldBe(expectFailureCode);
            return Guid.Empty;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Policy(Guid id) => await (await _maker.GetAsync($"/api/v1/general/policies/{id}")).Json();

    private async Task<JsonElement> Claim(Guid id) => await (await _maker.GetAsync($"/api/v1/general/claims/{id}")).Json();

    private async Task<string?> InvoiceStatus(Guid id) => (await (await _maker.GetAsync($"/api/v1/billing/invoices/{id}")).Json()).GetProperty("status").GetString();

    private async Task<List<JsonElement>> Journals(string businessRefType, Guid businessRefId) =>
        (await (await _maker.GetAsync($"/api/v1/finance/journals?businessRefType={businessRefType}&businessRefId={businessRefId}")).Json()).EnumerateArray().ToList();

    private Task<long> Documents(Guid policy, string category) =>
        _db.ScalarAsOwner<long>("SELECT count(*) FROM document.document WHERE business_ref_id = @p AND category_code = @c", ("p", policy), ("c", category));

    private Task AssignTemplate(string documentType) =>
        Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TA-{documentType}", ("product_code", Product), ("document_type", documentType),
            ("template", $"TPL-{documentType}"), ("language", "en"));

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        _db.SeedStatic(Tenant, table, status, code, Today.AddDays(-30), fields);
}
