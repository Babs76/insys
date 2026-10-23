/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : EndToEndTests
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
/// GEN-008), with their compensations, and endorsement and cancellation (GEN-003, GEN-004). The commission and
/// reinsurance steps are in EnterpriseProcessTests.
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
        ClaimsPermissions.InstructionRead, ClaimsPermissions.InstructionRelease, ClaimsPermissions.InstructionCreate, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate,
        DocumentPermissions.DocumentRead, Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate,
        Insys.Party.Contracts.PartyPermissions.BankAccountRead, Insys.Party.Contracts.PartyPermissions.BankAccountVerify,
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
        await _db.AllowBankAccountVerification(Tenant, approverRole, Today.AddYears(-1));
        var from = Today.AddDays(-30);

        foreach (var type in new[] { GeneralApprovals.EndorsementApprovalType, GeneralApprovals.SettlementApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}",
                ("role_code", approverRole), ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        // A published General motor product: coverages, a limit and an excess schedule, and its document templates.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'GEN-MOTOR', 'MOTOR', 'ISSUE')
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
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
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

    /// <summary>
    /// GAP-031: General's quote, proposal, policy and risk object responses match the schemas the core document gives
    /// them. Before module-specific schema IDs, General's proposal and policy were documented with Life's shapes.
    /// </summary>
    [Fact]
    public async Task GeneralResponses_MatchTheirDocumentedSchemas_GAP031()
    {
        var (policy, vehicle) = await IssuedPolicy();
        var policyView = await Policy(policy);
        var quote = await _db.ScalarAsOwner<Guid>("SELECT quote_id FROM general.policy WHERE id = @p", ("p", policy));
        var proposal = await _db.ScalarAsOwner<Guid>("SELECT id FROM general.proposal WHERE quote_id = @q", ("q", quote));

        ShouldMatchDocumented(policyView, "GeneralGetPolicy");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json(), "GeneralGetQuote");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/proposals/{proposal}")).Json(), "GeneralGetProposal");
        ShouldMatchDocumented(await (await _maker.GetAsync($"/api/v1/general/risk-objects/{vehicle}")).Json(), "GeneralGetRiskObject");

        // GAP-032: the lists find them by party and by number.
        var policyNumber = policyView.GetProperty("policyNumber").GetString();
        var holder = policyView.GetProperty("policyholderPartyId").GetGuid();
        var byNumber = await (await _maker.GetAsync($"/api/v1/general/policies?policyNumber={policyNumber}")).Json();
        ShouldMatchDocumented(byNumber, "GeneralListPolicies");
        byNumber.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(policy);
        (await (await _maker.GetAsync($"/api/v1/general/policies?policyholderPartyId={holder}")).Json()).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ShouldContain(policy);
        var proposer = (await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json()).GetProperty("proposerPartyId").GetGuid();
        var quotes = await (await _maker.GetAsync($"/api/v1/general/quotes?proposerPartyId={proposer}")).Json();
        ShouldMatchDocumented(quotes, "GeneralListQuotes");
        quotes.GetProperty("items").EnumerateArray().Select(q => q.GetProperty("id").GetGuid()).ShouldContain(quote);
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
        // F009: the fraud indicators in force are evaluated when the claim is registered. This one fires (a loss on the day
        // of inception) below its referral threshold, so it is recorded without referring the claim.
        await Seed("claims.cfg_fraud_indicator_definitions", "EFFECTIVE", "E2E-EARLY-GEN", ("indicator_code", "EARLY_CLAIM"), ("logic", "days_since_inception < 30"),
            ("score", 10), ("referral_threshold", 100), ("referral_queue", "FRAUD"));
        var claim = await Created("/api/v1/general/claims", new
        {
            policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC",
            description = "Rear-ended at a robot",
        });
        var registered = await (await _maker.PostAsync($"/api/v1/general/claims/{claim}/register", null)).Json();
        var number = registered.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("GEN-");
        registered.GetProperty("status").GetString().ShouldBe("Registered");
        ShouldMatchDocumented(await Claim(claim), "GeneralGetClaim");
        (await _db.ScalarAsOwner<string>("SELECT indicator_code || ':' || referred FROM claims.fraud_indicator_hit WHERE claim_number = @n", ("n", number)))
            .ShouldBe("EARLY_CLAIM:false", customMessage: "F009: fraud indicators are evaluated at registration");
        var claims = await (await _maker.GetAsync($"/api/v1/general/claims?claimNumber={number}")).Json();
        ShouldMatchDocumented(claims, "GeneralListClaims");
        claims.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(claim);
        (await (await _maker.GetAsync($"/api/v1/general/claims?policyId={policy}")).Json()).GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid()).ShouldContain(claim);
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
        // The payee and their verified bank account (party.bank_account): the settlement names the account by id only.
        var payee = await _maker.RegisterPerson("Repairs");
        var firstAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "62 0001 2345");
        var settlement = new { payeePartyId = payee, bankAccountId = firstAccount, reserveType = "INDEMNITY", decisionCode = "SETTLE", rationale = "Repair estimate within the assessed amount" };
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

        // The settlement approval covered this payee and account, so it stands in for the instruction's approval
        // (ADR-009). The account number appears in no event, approval or claim, only its keyed hash and id.
        instructed.GetProperty("bankAccountId").GetGuid().ShouldBe(firstAccount);
        (await _db.ScalarAsOwner<long>("""
            SELECT (SELECT count(*) FROM general.outbox_event WHERE payload::text LIKE '%00012345%')
                 + (SELECT count(*) FROM claims.outbox_event WHERE payload::text LIKE '%00012345%')
                 + (SELECT count(*) FROM workflow.approval WHERE reason LIKE '%00012345%')
            """)).ShouldBe(0L);

        // Payout batch, approved and released. Compensation: the provider rejects the payment; the instruction returns to
        // Failed and is re-released to the same approved account at once, as a new payment.
        var first = await PayOut(instruction, "BOUNCED");
        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Failed");
        (await Claim(claim)).GetProperty("status").GetString().ShouldBe("Approved");
        (await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = firstAccount })).Json())
            .GetProperty("status").GetString().ShouldBe("Released");
        await _factory.DrainOutbox();

        // The payee changes bank account before the batch is released: the account the payment was approved for is
        // superseded, so the payment fails without reaching the provider, and the new account needs a fresh approval.
        var second = await PayOut(instruction, null, beforeRelease: async () => await _maker.AddVerifiedBankAccount(_checker, _factory, payee, "1234567890"));
        (await _db.ScalarAsOwner<string>("SELECT status FROM payment.payment WHERE id = @p", ("p", second))).ShouldBe("FAILED");
        (await _db.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", second))).ShouldBeNull();
        (await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json()).GetProperty("status").GetString().ShouldBe("Failed");
        var newAccount = (await (await _maker.GetAsync($"/api/v1/party/parties/{payee}/bank-accounts")).Json()).EnumerateArray()
            .Single(a => a.GetProperty("status").GetString() == "ACTIVE").GetProperty("id").GetGuid();
        (await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = firstAccount }))
            .Code()).ShouldBe("PAYEE_BANK_ACCOUNT_CHANGED");
        var retried = await (await _maker.PostAsJsonAsync($"/api/v1/claims/payment-instructions/{instruction}/retry", new { payeePartyId = payee, bankAccountId = newAccount }))
            .Json();
        retried.GetProperty("status").GetString().ShouldBe("Failed");
        retried.GetProperty("pendingBankAccountId").GetGuid().ShouldBe(newAccount);
        await _factory.DrainOutbox();
        await Decide(ClaimsApprovals.InstructionBusinessRefType, instruction, approve: true);
        var released = await (await _maker.GetAsync($"/api/v1/claims/payment-instructions/{instruction}")).Json();
        released.GetProperty("status").GetString().ShouldBe("Released");
        released.GetProperty("bankAccountId").GetGuid().ShouldBe(newAccount);
        var third = await PayOut(instruction, "PAID");
        new[] { first, second, third }.Distinct().Count().ShouldBe(3);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'ReadBankAccountForPayment'")).ShouldBeGreaterThanOrEqualTo(2L);

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

    [Fact]
    public async Task ReferredQuote_DeclinedWithAReason_IsFinal()
    {
        await Seed("general.ref_referral_reason_codes", "ACTIVE", "HIGH_VALUE", ("label", "High value"), ("target_queue", "UW"));
        await Seed("general.ref_underwriting_decision_codes", "ACTIVE", "DECLINE", ("label", "Declined"), ("requires_terms", false));
        var vehicle = await Created("/api/v1/general/risk-objects", new { objectType = "VEHICLE", attributes = new { registration = "CA9", vehicle_group = "G3", year = Today.Year } });
        var quote = await Created("/api/v1/general/quotes", new
        {
            productCode = Product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
            risks = new[] { new { riskObjectId = vehicle, sumInsured = 400_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 400_000m } } } },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 400_000m);
        (await _maker.PostAsJsonAsync($"/api/v1/general/quotes/{quote}/refer", new { reasonCode = "HIGH_VALUE" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var declined = await (await _checker.PostAsJsonAsync($"/api/v1/general/quotes/{quote}/referral-decision",
            new { approved = false, decisionCode = "DECLINE", declineReason = "Outside the vehicle value appetite" })).Json();

        declined.GetProperty("status").GetString().ShouldBe("Declined");
        declined.GetProperty("referrals")[0].GetProperty("declineReason").GetString().ShouldBe("Outside the vehicle value appetite");
        (await (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM general.outbox_event WHERE event_type = 'general.GeneralQuoteDeclined'")).ShouldBe(1L);
    }

    [Fact]
    public async Task CoverStartingOnFirstPayment_StartsWhenTheFirstInvoiceIsSettled()
    {
        const string firstPayment = "E2E-GFP";
        var from = Today.AddDays(-30);
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'GENERAL', 'Motor', @coverages, 'MOTOR', 'FIRST_PAYMENT')
            """, ("t", Tenant), ("p", firstPayment), ("from", from), ("coverages", new[] { "OWN_DAMAGE", "THEFT" }));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GFP-P1", ("parameter_code", "GMOT-BASE"), ("product_version", $"{firstPayment} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "GFP-P2", ("parameter_code", "POLICY_FEE"), ("product_version", $"{firstPayment} v1"), ("value", 50m), ("unit", "AMOUNT"));
        await Seed("general.cfg_quote_validity_periods", "EFFECTIVE", "QV-GFP", ("product_code", firstPayment), ("days_valid", 30), ("re_rate_on_expiry", false));
        foreach (var type in new[] { "POLICY_SCHEDULE", "CERTIFICATE" })
        {
            await Seed("product.cfg_product_template_assignments", "EFFECTIVE", $"TAF-{type}", ("product_code", firstPayment), ("document_type", type),
                ("template", $"TPL-{type}"), ("language", "en"));
        }

        var (policy, vehicle) = await IssuedPolicy(firstPayment);
        await _factory.DrainOutbox();
        var issued = await Policy(policy);
        issued.GetProperty("status").GetString().ShouldBe("Issued");
        issued.GetProperty("coverStartsOn").GetString().ShouldBe("FIRST_PAYMENT");
        var loss = await Created("/api/v1/general/claims", new { policyId = policy, riskObjectId = vehicle, coverageCode = "OWN_DAMAGE", lossDate = Today, causeCode = "COLLISION", perilCode = "ACC" });
        (await (await _maker.PostAsync($"/api/v1/general/claims/{loss}/register", null)).Code()).ShouldBe("LOSS_NOT_COVERED");

        // InvoiceSettled → cover starts.
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p", ("p", policy));
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await Callback(payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var inForce = await Policy(policy);
        inForce.GetProperty("status").GetString().ShouldBe("InForce");
        inForce.GetProperty("firstPaidOn").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        (await (await _maker.PostAsync($"/api/v1/general/claims/{loss}/register", null)).Json()).GetProperty("status").GetString().ShouldBe("Registered");
    }

    /// <summary>A vehicle, a quote rated through Rating, issued and accepted (GeneralQuoteAccepted), a proposal, and the policy issued from it.</summary>
    private async Task<(Guid Policy, Guid Vehicle)> IssuedPolicy(string product = Product)
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
            productCode = product, proposerPartyId = Guid.CreateVersion7(), currency = "ZAR", periodStart = Today, periodEnd = Today.AddYears(1).AddDays(-1),
            risks = new[]
            {
                new
                {
                    riskObjectId = vehicle, sumInsured = 100_000m,
                    coverages = new object[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 100_000m, excessScheduleCode = "MOT-EXC" }, new { coverageCode = "THEFT", sumInsured = 50_000m } },
                },
            },
        });
        await Rate(GeneralRatingSubjects.Quote, quote, 100_000m, product);
        (await (await _maker.GetAsync($"/api/v1/general/quotes/{quote}")).Json()).GetProperty("premium").GetDecimal().ShouldBe(3_050m);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/issue", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/general/quotes/{quote}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = await Created("/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } });
        (await _maker.PostAsync($"/api/v1/general/proposals/{proposal}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var policy = await Created("/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" });
        if (product != Product)
        {
            return (policy, vehicle);
        }

        (await Policy(policy)).GetProperty("risks")[0].GetProperty("coverages").EnumerateArray().Sum(c => c.GetProperty("premium").GetDecimal()).ShouldBe(3_050m);
        return (policy, vehicle);
    }

    private async Task Rate(string subjectType, Guid subjectId, decimal sumInsured, string product = Product)
    {
        var calculated = await _maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"E2E-{subjectId:N}"[..20], sourceDomain = GeneralRatingSubjects.SourceDomain, subjectType, subjectId, productCode = product, productVersion = 1,
            currency = "ZAR", frequency = "ANNUAL", values = new { sum_insured = sumInsured, vehicle_group = "G3" },
        }, idempotent: true);
        calculated.StatusCode.ShouldBe(HttpStatusCode.Created, await calculated.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    /// <summary>Batches the payout, approves and releases it, and answers it with the provider's status (none when the payment never reaches the provider).</summary>
    private async Task<Guid> PayOut(Guid instruction, string? providerStatus, Func<Task>? beforeRelease = null)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();

        // The payout batch keeps its own release approval, whatever approved the payments in it (SRS-002).
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch, approve: true);
        if (beforeRelease is not null)
        {
            await beforeRelease();
        }

        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i AND payout_batch_id = @b", ("i", instruction), ("b", batch));
        if (providerStatus is not null)
        {
            (await Callback(payment, providerStatus)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

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

    private static void ShouldMatchDocumented(JsonElement response, string operationId) =>
        DocumentedSchema.ResponseProblems(response, "core", operationId).ShouldBeEmpty(operationId);

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
