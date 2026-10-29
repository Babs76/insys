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
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.Finance.Contracts;
using Insys.Integration.Contracts;
using Insys.Life.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Rating.Contracts;
using Insys.Rules.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.EndToEndTests;

/// <summary>
/// The Life processes of domain-model.json, through the API with the real migrations and every module involved: "Life
/// proposal to policy" (LIF-001 to LIF-005) and "Missed premium to lapse" (LIF-005), with their compensations, and a
/// death claim paid to the beneficiaries through the Claims Framework (LIF-008, CLM-003). Commission and reinsurance
/// steps are tested on General in EnterpriseProcessTests. The premium is an engineering case (OQ-052).
/// </summary>
public sealed class LifeProcessTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-LTERM";
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = WindowClock.Today;

    private static readonly string[] Permissions =
    [
        LifePermissions.ProposalRead, LifePermissions.ProposalCreate, LifePermissions.ProposalUpdate, LifePermissions.UnderwritingRead, LifePermissions.UnderwritingUpdate,
        LifePermissions.UnderwritingWaive, LifePermissions.UnderwritingDecide, LifePermissions.PolicyRead, LifePermissions.PolicyCreate, LifePermissions.PolicyUpdate, LifePermissions.LoanRead, LifePermissions.LoanCreate,
        LifePermissions.ClaimRead, LifePermissions.ClaimCreate, LifePermissions.ClaimUpdate, LifePermissions.ClaimDecide, RatingPermissions.CalculationCreate,
        RatingPermissions.CalculationRead, RulesPermissions.DecisionCreate, RulesPermissions.DecisionRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackEndpointUpdate,
        ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead,
        ClaimsPermissions.InstructionRelease, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate, WorkflowPermissions.ApprovalRead,
        WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, DocumentPermissions.DocumentRead, PartyPermissions.PartyCreate,
        PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;
    private string _approverRole = "";

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        _watchdog = StallWatchdog.Start(_db, TimeSpan.FromSeconds(90), $"LifeProcessTests.{TestContext.Current.TestMethod?.MethodName}");
        await _db.SeedTenant(Tenant, "TENA");
        var underwriter = await _db.UserWithPermissions(Tenant, "life-underwriter", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "senior-life-underwriter", Permissions);
        var approverRole = _approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);

        // The underwriter may decide standard terms alone up to 2 000 000 sum assured, when the rules agree (rules.cfg_authority_matrices).
        var underwritingRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, underwriter, underwritingRole);
        await Seed("rules.cfg_authority_matrices", "EFFECTIVE", "AM-UW", ("role_code", underwritingRole), ("product_code", Product), ("max_sum_assured", 2_000_000m),
            ("decision_types", new[] { "STD" }));
        await Seed("rules.cfg_acceptance_and_referral_rules", "EFFECTIVE", "UW-SA-LIMIT", ("rule_code", "UW-SA-LIMIT"), ("condition", "sum_assured > threshold"),
            ("threshold", 5_000_000m), ("outcome", "REFER"));
        await _db.AllowBankAccountVerification(Tenant, approverRole, Today.AddYears(-1));
        var from = Today.AddYears(-1);

        // Life claims are approved within the claims authority limits by claim type (life.cfg_claims_authority_limits), not the delegation table.
        await Seed("life.cfg_claims_authority_limits", "EFFECTIVE", "CAL-DEATH", ("role", approverRole), ("claim_type", "DEATH"), ("limit_value", 500_000m));
        foreach (var type in new[] { LifeApprovals.UnderwritingApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 5_000_000m), ("delegation_allowed", false));
        }

        // A published Life term product with death and disability benefits, and its policy schedule template.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on,
                                                                     eligibility_ruleset_code)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'LIFE', 'Term life', @coverages, 'LIFE-RISK', 'TERM', 'ISSUE', 'LTERM-ELIG')
            """, ("t", Tenant), ("p", Product), ("from", from), ("coverages", new[] { "DEATH", "DISABILITY" }));
        await Seed("document.ref_document_categories", "ACTIVE", "POLICY_SCHEDULE", ("category_code", "POLICY_SCHEDULE"), ("label", "Policy schedule"),
            ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await Seed("document.cfg_document_templates", "EFFECTIVE", "TPL-LSCH", ("template_code", "TPL-LSCH"), ("language", "en"), ("document_type", "POLICY_SCHEDULE"),
            ("body_ref", "doc://templates/life-schedule/v1"), ("merge_fields", new[] { "policy_number", "sum_assured" }));
        await Seed("product.cfg_product_template_assignments", "EFFECTIVE", "TA-LSCH", ("product_code", Product), ("document_type", "POLICY_SCHEDULE"),
            ("template", "TPL-LSCH"), ("language", "en"));

        // Rating (engineering case): monthly premium = sum assured × 0.00045 for a non-smoker.
        await Seed("rating.cfg_rating_factor_definitions", "EFFECTIVE", "SMOKER", ("factor_code", "SMOKER"), ("data_type", "CODE"), ("source_attribute", "smoker"));
        await _db.ExecuteAsOwner("""
            INSERT INTO rating.cfg_rating_tables (tenant_id, code, version, effective_from, record_status, created_by, change_reason, table_code, dimensions, values, interpolation)
            VALUES (@t, 'LTERM-BASE', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'LTERM-BASE', ARRAY['SMOKER'], '[{"key":["NS"],"value":0.00045}]', 'NONE')
            """, ("t", Tenant), ("from", from));
        await Seed("rating.cfg_premium_parameters", "EFFECTIVE", "LTERM-P1", ("parameter_code", "LTERM-BASE"), ("product_version", $"{Product} v1"), ("value", 1m), ("unit", "FACTOR"));
        await Seed("rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", ("rule_code", "ZAR-2DP"), ("currency", "ZAR"), ("step", 0.01m),
            ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));

        // Rules: eligible from 18 to 65 next birthday (when no rule fires, the outcome is INELIGIBLE: OQ-051's proposal).
        await Seed("rules.cfg_eligibility_rule_sets", "EFFECTIVE", "LTERM-ADULT", ("ruleset_code", "LTERM-ELIG"), ("conditions", "age_next >= 18 AND age_next <= 65"),
            ("outcome", "ELIGIBLE"));
        await Seed("rules.cfg_eligibility_rule_sets", "EFFECTIVE", "ANY-ADULT", ("ruleset_code", "ANY-ELIG"), ("conditions", "age_next >= 18"), ("outcome", "ELIGIBLE"));

        // Life: numbering, nominations, grace, lapse and reinstatement, decision codes, claim types and causes.
        await Seed("life.cfg_policy_numbering_schemes", "EFFECTIVE", "LIFE", ("product_line", "LIFE"), ("prefix", "LP"), ("sequence_length", 9));
        await Seed("life.cfg_nomination_rules", "EFFECTIVE", "NOM-LTERM", ("product_code", Product), ("max_beneficiaries", 5), ("min_share", 10m),
            ("allowed_relationships", new[] { "SPOUSE", "CHILD" }));
        foreach (var relationship in new[] { "SPOUSE", "CHILD" })
        {
            await Seed("life.ref_beneficiary_relationship_types", "ACTIVE", relationship, ("label", relationship));
        }

        await Seed("life.cfg_grace_period_rules", "EFFECTIVE", "GRACE-M", ("frequency", "MONTHLY"), ("grace_days", 30), ("cover_during_grace", true));
        await Seed("life.cfg_lapse_and_reinstatement_rules", "EFFECTIVE", "LAPSE-LTERM", ("product_code", Product), ("missed_payments_to_lapse", 2),
            ("reinstatement_window", TimeSpan.FromDays(180)), ("evidence_required", "DECLARATION"));
        await Seed("life.ref_underwriting_decision_codes", "ACTIVE", "STD", ("label", "Standard terms"), ("requires_terms", false));
        await Seed("life.ref_underwriting_decision_codes", "ACTIVE", "EXCL", ("label", "Accepted with an exclusion"), ("requires_terms", true));
        await Seed("life.ref_underwriting_decision_codes", "ACTIVE", "DECL", ("label", "Declined"), ("requires_terms", false));
        // A change of servicing intermediary is applied by a holder of the approver's role (life.cfg_alteration_types.authority_role).
        await Seed("life.cfg_alteration_types", "EFFECTIVE", "ALT-INT", ("alteration_code", "INTERMEDIARY"), ("allowed_states", new[] { "IN_FORCE" }),
            ("re_underwriting", false), ("authority_role", approverRole), ("document_type", "ENDORSEMENT"));
        await Seed("life.ref_life_claim_types", "ACTIVE", "DEATH", ("label", "Death"), ("benefit_type", "DEATH"));
        await _db.SeedGlobalStatic("life.ref_cause_of_event_codes", "ACTIVE", "I21", Today.AddYears(-1), ("code_system", "ICD-10"), ("accidental", false));

        // Billing, Payments, Claims and Finance.
        await _db.SeedBillingAndPayments(Tenant, from);
        await Seed("billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "D30", ("bucket_code", "D30"), ("days_from", 1), ("days_to", 9999), ("action", "LAPSE"));
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await _db.ExecuteAsOwner("UPDATE payment.mst_payment_provider_and_bank_register SET supported_methods = ARRAY['DEBIT_ORDER','EFT'] WHERE tenant_id = @t",
            ("t", Tenant));
        await Seed("claims.ref_reserve_types", "ACTIVE", "INDEMNITY", ("category", "INDEMNITY"), ("posting_rule", "PR-CLM-UP"));
        await Seed("claims.cfg_claim_numbering_schemes", "EFFECTIVE", "LIFE", ("domain", "LIFE"), ("prefix", "LIF"), ("sequence_length", 8));
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        foreach (var account in new[] { "1100", "1200", "2100", "4000", "5000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CLM-UP", TransactionTypes.ClaimReserveIncrease, "5000", "2100"), ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"),
                     ("PR-REVERSED", TransactionTypes.PaymentReversed, "1100", "1200"),
                 })
        {
            await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        foreach (var period in Enumerable.Range(-4, 5).Select(m => month.AddMonths(m)))
        {
            await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{period:yyyy-MM}", ("period_code", $"{period:yyyy-MM}"), ("start_date", period),
                ("end_date", period.AddMonths(1).AddDays(-1)));
        }

        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" }, time: WindowClock.Time);
        _maker = _factory.ClientFor(Tenant, "life-underwriter");
        _checker = _factory.ClientFor(Tenant, "senior-life-underwriter");
        foreach (var period in Enumerable.Range(-4, 5).Select(m => month.AddMonths(m)))
        {
            (await _maker.PostAsync($"/api/v1/finance/periods/{period:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private StallWatchdog? _watchdog;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (_watchdog is not null)
        {
            await _watchdog.DisposeAsync();
        }
    }

    [Fact]
    public async Task LifeProposalToPolicy_QuotesChecksUnderwritesIssuesBillsAndDocuments_WithItsCompensations_LIF001_LIF005()
    {
        var applicant = await _maker.RegisterPerson("Applicant");
        var spouse = await _maker.RegisterPerson("Spouse");

        // Compensation: an ineligible proposal cannot be submitted.
        var ineligible = await Proposal(applicant, spouse, Today);
        await Rate(ineligible, 1_500_000m);
        await Eligibility(ineligible, 70);
        (await (await _maker.PostAsync($"/api/v1/life/proposals/{ineligible}/submit", null)).Code()).ShouldBe("PROPOSAL_NOT_ELIGIBLE");

        // CaptureProposal and QuotePremium (PremiumCalculated), RuleDecisionRecorded (eligibility), then LifeProposalSubmitted.
        var proposal = await Proposal(applicant, spouse, Today);
        (await Get($"/api/v1/life/proposals/{proposal}")).GetProperty("lives")[0].GetProperty("ageNext").GetInt32().ShouldBe(Ages(Today));
        await Rate(proposal, 1_500_000m);
        var quoted = await Get($"/api/v1/life/proposals/{proposal}");
        (quoted.GetProperty("status").GetString(), quoted.GetProperty("quotedPremium").GetDecimal()).ShouldBe(("Quoted", 675m));
        // Only the product version's eligibility rule set (eligibility_ruleset_code) counts; another rule set's result is not attached.
        await Eligibility(proposal, Ages(Today), "ANY-ELIG");
        (await Get($"/api/v1/life/proposals/{proposal}")).GetProperty("eligibilityOutcome").ValueKind.ShouldBe(JsonValueKind.Null);
        await Eligibility(proposal, Ages(Today));
        (await _maker.PostAsync($"/api/v1/life/proposals/{proposal}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // OpenCase with the rule set version (LIF-002); RequestEvidence; the decision is approved by a second person in Workflow.
        var proposalView = await Get($"/api/v1/life/proposals/{proposal}");
        proposalView.GetProperty("status").GetString().ShouldBe("InUnderwriting");
        var underwritingCase = await Get($"/api/v1/life/proposals/{proposal}/underwriting-case");
        var caseId = underwritingCase.GetProperty("id").GetGuid();
        underwritingCase.GetProperty("rulesetCode").GetString().ShouldBe("LTERM-ELIG");
        var requirement = (await (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/evidence", new { evidenceType = "MEDICAL", mandatory = true })).Json())
            .GetProperty("requirements")[0].GetProperty("id").GetGuid();
        var early = await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/decision", new { outcome = "Accept", decisionCode = "STD", reason = "STANDARD" });
        (await early.Code()).ShouldBe("INVALID_STATE_TRANSITION");
        (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/evidence/{requirement}/received", new { documentId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var decision = new
        {
            outcome = "Accept", decisionCode = "EXCL", reason = "AVIATION",
            items = new[] { new { benefitCode = "DEATH", decisionCode = "EXCL", exclusionCode = "EXC-AVIATION" } },
        };
        (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/decision", decision)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/life/proposals/{proposal}")).GetProperty("status").GetString().ShouldBe("InUnderwriting");

        // BR-076, BR-074: a decision waiting for a second person names its proposer and approval, and the case is listed as pending.
        var waiting = await Get($"/api/v1/life/underwriting-cases/{caseId}");
        var approvalId = (await (await _maker.GetAsync(
                $"/api/v1/workflow/approvals?status=Requested&businessRefType={LifeApprovals.UnderwritingBusinessRefType}&businessRefId={caseId}")).Json())
            .GetProperty("items")[0].GetProperty("id").GetGuid();
        (waiting.GetProperty("decisionPending").GetBoolean(), waiting.GetProperty("approvalId").GetGuid()).ShouldBe((true, approvalId));
        waiting.GetProperty("requestedBy").GetGuid().ShouldBe(await UserId("life-underwriter"));
        var pendingCases = await Get("/api/v1/life/underwriting-cases?decisionPending=true");
        DocumentedSchema.ResponseProblems(pendingCases, "core", "LifeListUnderwritingCases").ShouldBeEmpty();
        pendingCases.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldContain(caseId);
        (await Get("/api/v1/life/underwriting-cases?decisionPending=false")).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())
            .ShouldNotContain(caseId);
        await Decide(LifeApprovals.UnderwritingBusinessRefType, caseId);
        var decided = await Get($"/api/v1/life/underwriting-cases/{caseId}");
        decided.GetProperty("status").GetString().ShouldBe("Decided");
        decided.GetProperty("decidedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await Get("/api/v1/life/underwriting-cases?status=Decided")).GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == caseId)
            .GetProperty("proposalId").GetGuid().ShouldBe(proposal);
        (await Get($"/api/v1/life/proposals/{proposal}")).GetProperty("status").GetString().ShouldBe("Accepted");

        // IssuePolicy on the approved product and rating versions (LIF-003); Billing opens the account, schedule and invoice; Documents the schedule.
        var policy = await Created("/api/v1/life/policies", new { proposalId = proposal });
        await _factory.DrainOutbox();
        var issued = await Get($"/api/v1/life/policies/{policy}");
        (issued.GetProperty("status").GetString(), issued.GetProperty("productVersion").GetInt32(), issued.GetProperty("ratingVersion").GetInt32())
            .ShouldBe(("InForce", 1, 1));
        issued.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH").GetProperty("exclusionCodes")[0].GetString()
            .ShouldBe("EXC-AVIATION");
        issued.GetProperty("beneficiaries").EnumerateArray().Single().GetProperty("partyId").GetGuid().ShouldBe(spouse);
        var converted = await Get($"/api/v1/life/proposals/{proposal}");
        (converted.GetProperty("status").GetString(), converted.GetProperty("policyId").GetGuid()).ShouldBe(("Converted", policy), "BR-075, BR-086");
        var byProposal = (await Get($"/api/v1/life/policies?proposalId={proposal}")).GetProperty("items").EnumerateArray().Single();
        (byProposal.GetProperty("id").GetGuid(), byProposal.GetProperty("proposalId").GetGuid()).ShouldBe((policy, proposal));

        // GAP-040: the policy is found in the list by its number, by the policyholder, and by status.
        var policyNumber = issued.GetProperty("policyNumber").GetString()!;
        var listed = await Get($"/api/v1/life/policies?policyNumber={policyNumber}");
        DocumentedSchema.ResponseProblems(listed, "core", "LifeListPolicies").ShouldBeEmpty();
        listed.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(policy);
        (await Get($"/api/v1/life/policies?policyholderPartyId={issued.GetProperty("policyholderPartyId").GetGuid()}&status=InForce")).GetProperty("items")
            .EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldContain(policy);
        (await Get($"/api/v1/life/proposals?status=Converted")).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldContain(proposal);
        (await _db.ScalarAsOwner<decimal>("SELECT total_amount FROM billing.invoice WHERE source_domain = 'LIFE' AND policy_ref = @p", ("p", policy))).ShouldBe(675m);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id WHERE s.policy_ref = @p",
            ("p", policy))).ShouldBe(240L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM document.document WHERE business_ref_id = @p AND category_code = 'POLICY_SCHEDULE'", ("p", policy))).ShouldBe(1L);
        (await _maker.Send(HttpMethod.Post, "/api/v1/life/policies", new { proposalId = proposal }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM life.policy WHERE proposal_id = @p", ("p", proposal))).ShouldBe(1L);

        // Compensation: a declined proposal ends the process; no policy is issued from it.
        var declined = await Proposal(applicant, spouse, Today);
        await Rate(declined, 1_500_000m);
        await Eligibility(declined, Ages(Today));
        (await _maker.PostAsync($"/api/v1/life/proposals/{declined}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var declinedCase = (await Get($"/api/v1/life/proposals/{declined}/underwriting-case")).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/life/underwriting-cases/{declinedCase}/start-assessment", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{declinedCase}/decision", new { outcome = "Decline", decisionCode = "DECL", reason = "MEDICAL" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(LifeApprovals.UnderwritingBusinessRefType, declinedCase);
        (await Get($"/api/v1/life/proposals/{declined}")).GetProperty("status").GetString().ShouldBe("Declined");
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/life/policies", new { proposalId = declined }, idempotent: true)).Code()).ShouldBe("INVALID_STATE_TRANSITION");

        // Value commands wait for actuarial calculations.
        (await (await _maker.PostAsync($"/api/v1/life/policies/{policy}/surrender", null)).Code()).ShouldBe("LIFE_VALUE_NOT_CONFIGURED");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);
    }

    [Fact]
    public async Task MissedPremiumToLapse_GraceThenLapse_BillingStops_AndReinstatementResumesIt_LIF005()
    {
        var applicant = await _maker.RegisterPerson("Lapser");
        var spouse = await _maker.RegisterPerson("Partner");
        var inception = Today.AddMonths(-3);
        var policy = await IssuedPolicy(applicant, spouse, inception);

        // The policy was issued backdated: its past instalments are invoiced now. As if issued at inception, the invoice fell due then.
        await _db.ExecuteAsOwner("UPDATE billing.invoice SET due_date = @d, issue_date = @d WHERE source_domain = 'LIFE' AND policy_ref = @p", ("d", inception), ("p", policy));

        // PaymentFailed: the debit order bounces.
        var collection = await _db.ScalarAsOwner<Guid>("""
            SELECT p.id FROM payment.payment p JOIN billing.invoice i ON i.id = p.business_ref_id WHERE i.policy_ref = @p
            """, ("p", policy));
        (await _maker.SimulatorCallback(_factory, _db, collection, "BOUNCED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // DetectArrears → ArrearsDetected → RecordMissedPremium: the grace period starts at the oldest unpaid instalment.
        await _factory.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var schedule = (await Get($"/api/v1/life/policies/{policy}")).GetProperty("schedule");
        (schedule.GetProperty("missedCount").GetInt32(), schedule.GetProperty("arrearsSince").GetString()).ShouldBe((3, inception.ToString("yyyy-MM-dd")));
        schedule.GetProperty("graceUntil").GetString().ShouldBe(inception.AddDays(30).ToString("yyyy-MM-dd"));

        // LapsePolicy when grace has ended and the missed payments reach the product's limit; Billing stops the schedule.
        await _factory.Services.GetRequiredService<Insys.Life.Infrastructure.LifeJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var lapsed = await Get($"/api/v1/life/policies/{policy}");
        lapsed.GetProperty("status").GetString().ShouldBe("Lapsed");
        lapsed.GetProperty("lapseHistory")[0].GetProperty("effectiveDate").GetString().ShouldBe(inception.AddDays(31).ToString("yyyy-MM-dd"));
        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id
            WHERE s.policy_ref = @p AND i.status = 'SCHEDULED'
            """, ("p", policy))).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM life.outbox_event WHERE event_type = 'life.LifePolicyLapsed' AND aggregate_id = @p", ("p", policy))).ShouldBe(1L);

        // ReinstatePolicy within the window, with the declaration the rule requires; Billing resumes from today.
        (await (await _maker.PostAsJsonAsync($"/api/v1/life/policies/{policy}/reinstate", new { evidenceRef = (Guid?)null })).Code())
            .ShouldBe("LIFE_REINSTATEMENT_REFUSED");
        (await _maker.PostAsJsonAsync($"/api/v1/life/policies/{policy}/reinstate", new { evidenceRef = Guid.CreateVersion7() })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var reinstated = await Get($"/api/v1/life/policies/{policy}");
        (reinstated.GetProperty("status").GetString(), reinstated.GetProperty("schedule").GetProperty("missedCount").GetInt32()).ShouldBe(("InForce", 0));
        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id
            WHERE s.policy_ref = @p AND i.status IN ('SCHEDULED', 'INVOICED') AND i.due_date >= @today
            """, ("p", policy), ("today", Today))).ShouldBeGreaterThan(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);
    }

    /// <summary>
    /// BR-103 (found by the front end's Phase 8 run): Billing detects arrears on one business date, and Life processes the
    /// event on a later one (a late, retried or parked delivery). Life counts the missed premiums Billing saw on the date it
    /// detected them, not those that fell due since, so the count and the grace period are the same however late it is.
    /// </summary>
    [Fact]
    public async Task ArrearsDetected_DeliveredOnALaterBusinessDate_CountsWhatWasOverdueWhenBillingDetectedIt_BR103()
    {
        var applicant = await _maker.RegisterPerson("Late");
        var spouse = await _maker.RegisterPerson("Delivery");
        var policy = await IssuedPolicy(applicant, spouse, Today.AddMonths(-3));
        var instalments = await _db.ScalarAsOwner<Guid[]>("""
            SELECT array_agg(i.id ORDER BY i.due_date, i.id) FROM billing.premium_instalment i JOIN billing.premium_schedule s ON s.id = i.schedule_id
            WHERE s.policy_ref = @p AND i.status = 'INVOICED'
            """, ("p", policy));
        instalments!.Length.ShouldBeGreaterThan(1);

        // One instalment was overdue when Billing ran on the detection date; the others fell due after it, before the delivery.
        var detectedOn = Today.AddDays(-30);
        var oldest = Today.AddDays(-60);
        await _db.ExecuteAsOwner("UPDATE billing.invoice SET due_date = @d, issue_date = @d WHERE source_domain = 'LIFE' AND policy_ref = @p", ("d", oldest), ("p", policy));
        await _db.ExecuteAsOwner("UPDATE billing.premium_instalment SET due_date = @d WHERE id = @i", ("d", oldest), ("i", instalments[0]));
        await _db.ExecuteAsOwner("UPDATE billing.premium_instalment SET due_date = @d WHERE id = ANY(@rest)", ("d", Today.AddDays(-5)), ("rest", instalments[1..]));
        var collection = await _db.ScalarAsOwner<Guid>("""
            SELECT p.id FROM payment.payment p JOIN billing.invoice i ON i.id = p.business_ref_id WHERE i.policy_ref = @p LIMIT 1
            """, ("p", policy));
        (await _maker.SimulatorCallback(_factory, _db, collection, "BOUNCED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // Billing ages the account as of the detection date; nothing is delivered until the business date has moved on.
        using (BusinessDateOverride.Use(detectedOn))
        {
            await _factory.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
        }

        (await _db.ScalarAsOwner<string>("""
            SELECT payload ->> 'detectedOn' FROM billing.outbox_event WHERE event_type = 'billing.ArrearsDetected' ORDER BY occurred_at DESC LIMIT 1
            """)).ShouldBe(detectedOn.ToString("yyyy-MM-dd"), customMessage: "the event carries the date Billing detected the arrears on");
        await _factory.DrainOutbox();

        var schedule = (await Get($"/api/v1/life/policies/{policy}")).GetProperty("schedule");
        (schedule.GetProperty("missedCount").GetInt32(), schedule.GetProperty("arrearsSince").GetString())
            .ShouldBe((1, oldest.ToString("yyyy-MM-dd")), "only the instalment overdue on the detection date, not those due since");
        schedule.GetProperty("graceUntil").GetString().ShouldBe(oldest.AddDays(30).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task DeathClaim_IsCheckedRegisteredApprovedAndPaidToTheBeneficiary_ThenThePolicyIsClaimed_LIF008()
    {
        await Organisation();
        var applicant = await _maker.RegisterPerson("Insured");
        var spouse = await _maker.RegisterPerson("Widow");
        var policy = await IssuedPolicy(applicant, spouse, Today.AddMonths(-1), "ORG-BR-A");

        // Only a user holding the alteration type's authority role applies it.
        var alteration = new { alterationCode = "INTERMEDIARY", effectiveDate = Today, intermediaryCode = "BRK-2" };
        (await (await _maker.PostAsJsonAsync($"/api/v1/life/policies/{policy}/alterations", alteration)).Code()).ShouldBe("LIFE_AUTHORITY_MISSING");
        var altered = await _checker.PostAsJsonAsync($"/api/v1/life/policies/{policy}/alterations", alteration);
        altered.StatusCode.ShouldBe(HttpStatusCode.OK, await altered.Content.ReadAsStringAsync());
        var view = await Get($"/api/v1/life/policies/{policy}");
        var life = view.GetProperty("lives")[0].GetProperty("id").GetGuid();

        // A stranger is not entitled to claim (LIF-008).
        var stranger = await _maker.RegisterPerson("Stranger");
        var refused = await Created("/api/v1/life/claims", new { policyId = policy, claimType = "DEATH", eventDate = Today, causeCode = "I21", claimantPartyId = stranger, insuredLifeId = life });
        (await (await _maker.PostAsync($"/api/v1/life/claims/{refused}/register", null)).Code()).ShouldBe("CLAIMANT_NOT_AUTHORISED");

        // F009: the fraud indicators in force are evaluated at registration with the claim's facts; this one (a death claim)
        // fires below its referral threshold, so it is recorded without referring the claim.
        await Seed("claims.cfg_fraud_indicator_definitions", "EFFECTIVE", "E2E-DEATH-LIFE", ("indicator_code", "DEATH_CLAIM"), ("logic", "claim_type = 'DEATH'"),
            ("score", 10), ("referral_threshold", 100), ("referral_queue", "FRAUD"));
        // OQ-079: death pays a lump sum, so the claim starts with a reserve of the claimed cover's sum assured.
        await _db.SeedGlobalStatic("life.ref_life_benefit_types", "ACTIVE", "DEATH", Today.AddDays(-30), ("label", "Death"), ("payout_type", "LUMP_SUM"));
        var claim = await Created("/api/v1/life/claims", new { policyId = policy, claimType = "DEATH", eventDate = Today, causeCode = "I21", claimantPartyId = spouse, insuredLifeId = life });
        var registered = await (await _maker.PostAsync($"/api/v1/life/claims/{claim}/register", null)).Json();
        var number = registered.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("LIF");
        (await _db.ScalarAsOwner<string>("SELECT indicator_code || ':' || referred FROM claims.fraud_indicator_hit WHERE claim_number = @n", ("n", number)))
            .ShouldBe("DEATH_CLAIM:false", customMessage: "F009: fraud indicators are evaluated at registration");

        var reserve = await Get($"/api/v1/claims/claims/{number}/financials");
        reserve.GetProperty("outstandingReserves").GetProperty("INDEMNITY").GetDecimal().ShouldBe(1_000_000m, "OQ-079: the death cover's sum assured, set at registration");
        reserve.GetProperty("movements").GetArrayLength().ShouldBe(1);
        var death = view.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH");
        (await _maker.PostAsJsonAsync($"/api/v1/life/claims/{claim}/assessment",
            new { lines = new[] { new { policyBenefitId = death.GetProperty("id").GetGuid(), amount = 1_000_000m } } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var account = await _maker.AddVerifiedBankAccount(_checker, _factory, spouse, "4400112233");
        (await _maker.PostAsJsonAsync($"/api/v1/life/claims/{claim}/approval", new
        {
            payees = new[] { new { partyId = spouse, bankAccountId = account, sharePct = 100m } }, reserveType = "INDEMNITY", decisionReason = "DEATH_PROVEN",
            rationale = "Death certificate verified; cause not excluded.",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        // The checker's claims authority for DEATH (500 000) does not cover 1 000 000; with a higher limit for the role, it does.
        var pending = (await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={LifeApprovals.ClaimBusinessRefType}&businessRefId={claim}"))
            .Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{pending}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("APPROVAL_AUTHORITY_EXCEEDED");
        await Seed("life.cfg_claims_authority_limits", "EFFECTIVE", "CAL-DEATH-SNR", ("role", _approverRole), ("claim_type", "DEATH"), ("limit_value", 1_000_000m));
        await Decide(LifeApprovals.ClaimBusinessRefType, claim);
        // BR-065: the claim names its approval, who proposed and who decided it, when, when it was notified, and its reserve type.
        var approved = await Get($"/api/v1/life/claims/{claim}");
        approved.GetProperty("status").GetString().ShouldBe("Approved");
        approved.GetProperty("approvalId").GetGuid().ShouldBe(pending);
        approved.GetProperty("requestedBy").GetGuid().ShouldBe(await UserId("life-underwriter"));
        approved.GetProperty("decidedBy").GetGuid().ShouldBe(await UserId("senior-life-underwriter"));
        approved.GetProperty("decidedAt").ValueKind.ShouldBe(JsonValueKind.String);
        approved.GetProperty("notifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
        approved.GetProperty("reserveType").GetString().ShouldBe("INDEMNITY");
        DocumentedSchema.ResponseProblems(approved, "core", "LifeGetClaim").ShouldBeEmpty();

        // The Claims Framework releases one instruction per payee; Payments pays it in an approved batch; the claim settles and the policy closes.
        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        (await _db.ScalarAsOwner<Guid>("SELECT bank_account_id FROM claims.claim_payment_instruction WHERE id = @i", ("i", instruction))).ShouldBe(account);
        await PayOut(instruction, "PAID");
        (await Get($"/api/v1/life/claims/{claim}")).GetProperty("status").GetString().ShouldBe("Settled");
        var closed = await Get($"/api/v1/life/policies/{policy}");
        closed.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH").GetProperty("status").GetString().ShouldBe("CLAIMED");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM life.outbox_event WHERE payload::text LIKE '%4400112233%'")).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);

        // F103: the claim payment follows its claim's branch, through /payments and through its payout batch.
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", instruction));
        var batch = await _db.ScalarAsOwner<Guid>("SELECT payout_batch_id FROM payment.payment WHERE id = @p", ("p", payment));
        var byInstruction = $"/api/v1/payments/payments?businessRefType={ClaimsApprovals.InstructionBusinessRefType}&businessRefId={instruction}";
        foreach (var (subject, node, sees) in new[] { ("pay-branch-b", "ORG-BR-B", false), ("pay-north", "ORG-NORTH", true) })
        {
            var user = await _db.UserWithPermissions(Tenant, subject, Permissions);
            await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = ARRAY[@n] WHERE user_id = @u", ("n", node), ("u", user));
            var client = _factory.ClientFor(Tenant, subject);
            var expected = sees ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            (await (await client.GetAsync(byInstruction)).Json()).GetProperty("items").GetArrayLength().ShouldBe(sees ? 1 : 0, subject);
            (await client.GetAsync($"/api/v1/payments/payments/{payment}")).StatusCode.ShouldBe(expected, subject);
            (await client.GetAsync($"/api/v1/payments/payout-batches/{batch}")).StatusCode.ShouldBe(expected, subject);
            (await client.GetAsync($"/api/v1/payments/payout-batches/{batch}/payments")).StatusCode.ShouldBe(expected, subject);
        }

        // Last, as the portals' channels then apply to staff too (F005). The portals (external-user access): the claimant
        // sees the claim, and so does the policy's broker; a stranger does not; the view leaves out the assessment and reasons.
        var claimant = await PortalClient("claimant", "PARTY", spouse, null);
        var outsider = await PortalClient("outsider", "PARTY", stranger, null);
        var broker = await PortalClient("broker-2", "INTERMEDIARY", null, "BRK-2");
        var seen = await claimant.GetAsync($"/api/v1/portal/life/claims/{claim}");
        seen.StatusCode.ShouldBe(HttpStatusCode.OK, await seen.Content.ReadAsStringAsync());
        var portalView = await seen.Json();
        portalView.TryGetProperty("decisionRationale", out _).ShouldBeFalse();
        portalView.TryGetProperty("reserveType", out _).ShouldBeFalse();
        (await (await claimant.GetAsync("/api/v1/portal/life/claims")).Json()).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldContain(claim);
        (await outsider.GetAsync($"/api/v1/portal/life/claims/{claim}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await outsider.GetAsync("/api/v1/portal/life/claims")).Json()).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldContain(refused,
            "the stranger made that one");
        (await broker.GetAsync($"/api/v1/portal/life/claims/{claim}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// BR-062, BR-063, BR-065: a policy shows no cash value until one is stored (the actuarial basis, OQ-052, is not
    /// defined), then its latest; a loan against it is listed by policy and status, in the policy's currency.
    /// </summary>
    [Fact]
    public async Task PolicyLoans_AreListedInThePolicysCurrency_AndThePolicyShowsItsLatestCashValue_BR062_BR063_BR065()
    {
        var applicant = await _maker.RegisterPerson("Borrower");
        var spouse = await _maker.RegisterPerson("Spouse");
        var policy = await IssuedPolicy(applicant, spouse, Today.AddMonths(-2));
        var before = await Get($"/api/v1/life/policies/{policy}");
        (before.GetProperty("cashValue").ValueKind, before.GetProperty("cashValueDate").ValueKind).ShouldBe((JsonValueKind.Null, JsonValueKind.Null));

        foreach (var (date, value) in new[] { (Today.AddMonths(-1), 8_000m), (Today.AddDays(-1), 10_000m), (Today.AddDays(5), 99_000m) })
        {
            await _db.ExecuteAsOwner("""
                INSERT INTO life.cash_value (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, policy_id, valuation_date, cash_value,
                                             surrender_value, scale_code, scale_version)
                VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @p, @d, @v, @v, 'SV-E2E', 1)
                """, ("t", Tenant), ("p", policy), ("d", date), ("v", value));
        }

        var valued = await Get($"/api/v1/life/policies/{policy}");
        (valued.GetProperty("cashValue").GetDecimal(), valued.GetProperty("cashValueDate").GetString()).ShouldBe((10_000m, $"{Today.AddDays(-1):yyyy-MM-dd}"),
            "the latest on or before today; a future valuation is not shown");

        await Seed("life.cfg_policy_loan_rates", "EFFECTIVE", "LR-STD", ("rate_code", "LR-STD"), ("rate", 9.5m), ("compounding", "MONTHLY"));
        await Seed("life.cfg_loan_eligibility_limits", "EFFECTIVE", "LE-LTERM", ("product_code", Product), ("max_pct_cash_value", 80m),
            ("min_policy_duration", TimeSpan.FromDays(1)));
        var loan = await Created($"/api/v1/life/policies/{policy}/loans", new { principal = 5_000m, rateCode = "LR-STD" });
        (await Get($"/api/v1/life/loans/{loan}")).GetProperty("currency").GetString().ShouldBe("ZAR");

        var listed = await Get($"/api/v1/life/loans?policyId={policy}");
        DocumentedSchema.ResponseProblems(listed, "core", "LifeListLoans").ShouldBeEmpty();
        var row = listed.GetProperty("items").EnumerateArray().Single();
        (row.GetProperty("id").GetGuid(), row.GetProperty("currency").GetString(), row.GetProperty("balance").GetDecimal()).ShouldBe((loan, "ZAR", 5_000m));
        (await Get("/api/v1/life/loans?status=Active")).GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ShouldContain(loan);
        (await Get("/api/v1/life/loans?status=Repaid")).GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ShouldNotContain(loan);
    }

    private Task<Guid> UserId(string subject) =>
        _db.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = @s", ("t", Tenant), ("s", subject));

    private async Task Organisation()
    {
        foreach (var (code, type, parent) in new[] { ("ORG-ROOT", "LEGAL_ENTITY", (string?)null), ("ORG-NORTH", "REGION", "ORG-ROOT"), ("ORG-BR-A", "BRANCH", "ORG-NORTH"),
                     ("ORG-BR-B", "BRANCH", "ORG-ROOT") })
        {
            await Seed("tenant.mst_organisation_structure", "ACTIVE", code, ("node_code", code), ("type", type), ("parent", (object?)parent ?? DBNull.Value), ("name", code));
        }

    }

    /// <summary>
    /// F103 (addenda/access-scopes.md): a Life policy keeps its proposal's organisation node, and a user scoped to a region
    /// sees and acts on the proposals and policies of its branches only.
    /// </summary>
    [Fact]
    public async Task ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103()
    {
        await Organisation();
        var applicant = await _maker.RegisterPerson("Scoped");
        var spouse = await _maker.RegisterPerson("Partner");
        var inA = await Proposal(applicant, spouse, Today, "ORG-BR-A");
        var inB = await Proposal(applicant, spouse, Today, "ORG-BR-B");
        var policyInB = await IssuedPolicy(applicant, spouse, Today, "ORG-BR-B");
        (await _db.ScalarAsOwner<string>("SELECT org_node_code FROM life.policy WHERE id = @p", ("p", policyInB))).ShouldBe("ORG-BR-B", "a policy keeps its proposal's node");

        var north = await _db.UserWithPermissions(Tenant, "life-north", [.. Permissions, BillingPermissions.AccountUpdate]);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = '{ORG-NORTH}' WHERE user_id = @u", ("u", north));
        var northClient = _factory.ClientFor(Tenant, "life-north");

        var listed = (await (await northClient.GetAsync($"/api/v1/life/proposals?applicantPartyId={applicant}")).Json()).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ToList();
        listed.ShouldBe([inA]);
        (await (await northClient.GetAsync($"/api/v1/life/policies?policyholderPartyId={applicant}")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await northClient.GetAsync($"/api/v1/life/proposals/{inA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await northClient.GetAsync($"/api/v1/life/proposals/{inB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await northClient.GetAsync($"/api/v1/life/policies/{policyInB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var refused = await northClient.PostAsync($"/api/v1/life/proposals/{inB}/withdraw", null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");

        // Billing follows the policy: its schedule and invoices carry the policy's product and node, and so do not reach the North user.
        (await _db.ScalarAsOwner<string>("SELECT product_code || '@' || org_node_code FROM billing.premium_schedule WHERE policy_ref = @p", ("p", policyInB)))
            .ShouldBe($"{Product}@ORG-BR-B");
        var invoice = await _db.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p ORDER BY id LIMIT 1", ("p", policyInB));
        var account = await _db.ScalarAsOwner<Guid>("SELECT billing_account_id FROM billing.invoice WHERE id = @i", ("i", invoice));
        (await _db.ScalarAsOwner<string>("SELECT org_node_code FROM billing.invoice WHERE id = @i", ("i", invoice))).ShouldBe("ORG-BR-B");
        (await northClient.GetAsync($"/api/v1/billing/invoices/{invoice}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await northClient.GetAsync($"/api/v1/billing/accounts/{account}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await northClient.GetAsync($"/api/v1/billing/accounts/{account}/invoices")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _maker.GetAsync($"/api/v1/billing/invoices/{invoice}")).StatusCode.ShouldBe(HttpStatusCode.OK, "an unscoped user still reads it");

        // The payment collecting that invoice is not shown either.
        var collections = $"/api/v1/payments/payments?businessRefType=INVOICE&businessRefId={invoice}";
        var collection = (await (await _maker.GetAsync(collections)).Json()).GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();
        (await (await northClient.GetAsync(collections)).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await northClient.GetAsync($"/api/v1/payments/payments/{collection}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // OQ-081: one account billing policies in two branches. The North user sees its branch-A part only: no arrears, no
        // branch-B schedule, invoice or payment, and no account-wide operation.
        var policyInA = await IssuedPolicy(applicant, spouse, Today, "ORG-BR-A");
        await _db.ExecuteAsOwner("UPDATE billing.premium_schedule SET billing_account_id = @a WHERE policy_ref = @p", ("a", account), ("p", policyInA));
        await _db.ExecuteAsOwner("UPDATE billing.invoice SET billing_account_id = @a WHERE policy_ref = @p", ("a", account), ("p", policyInA));
        await _db.ExecuteAsOwner("""
            INSERT INTO billing.arrears_case (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, billing_account_id, bucket_code, days_overdue,
                                              amount_overdue, currency, next_action, status)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @a, '30', 31, 999.00, 'ZAR', 'REMIND', 'OPEN')
            """, ("t", Tenant), ("a", account));
        var shared = await (await northClient.GetAsync($"/api/v1/billing/accounts/{account}")).Json();
        shared.GetProperty("partlyInScope").GetBoolean().ShouldBeTrue();
        shared.GetProperty("schedules").EnumerateArray().Select(x => x.GetProperty("policyRef").GetGuid()).ShouldBe([policyInA]);
        shared.GetProperty("arrears").GetArrayLength().ShouldBe(0, "account-level figures are not shown to a user covering only part of the account");
        (await (await northClient.GetAsync($"/api/v1/billing/accounts/{account}/invoices")).Json()).EnumerateArray()
            .ShouldAllBe(i => i.GetProperty("policyRef").GetGuid() == policyInA);
        var aged = await northClient.PostAsync($"/api/v1/billing/accounts/{account}/age", null);
        aged.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await aged.Code()).ShouldBe("OUT_OF_SCOPE");
        var whole = await (await _maker.GetAsync($"/api/v1/billing/accounts/{account}")).Json();
        (whole.GetProperty("partlyInScope").GetBoolean(), whole.GetProperty("schedules").GetArrayLength(), whole.GetProperty("arrears").GetArrayLength()).ShouldBe((false, 2, 1));
    }

    private static int Ages(DateOnly on)
    {
        var birth = new DateOnly(1985, 6, 15);
        var age = on.Year - birth.Year - (birth.AddYears(on.Year - birth.Year) > on ? 1 : 0);
        return age + 1;
    }

    private async Task<Guid> Proposal(Guid applicant, Guid spouse, DateOnly inception, string? orgNodeCode = null) =>
        await Created("/api/v1/life/proposals", new
        {
            productCode = Product, orgNodeCode, applicantPartyId = applicant, channelCode = "BRANCH", currency = "ZAR", inceptionDate = inception, frequency = "MONTHLY",
            collectionMethod = "DEBIT_ORDER",
            lives = new[] { new { partyId = applicant, lifeRole = "MAIN", smokerStatus = "NS" } },
            covers = new[] { new { insuredPartyId = applicant, benefitCode = "DEATH", sumAssured = 1_000_000m, termYears = 20 }, new { insuredPartyId = applicant, benefitCode = "DISABILITY", sumAssured = 500_000m, termYears = 20 } },
            disclosures = new[] { new { insuredPartyId = applicant, questionCode = "SMOKE_12M", questionVersion = 1, answer = JsonSerializer.SerializeToElement(new { value = false }) } },
            nominations = new[] { new { partyId = spouse, relationshipCode = "SPOUSE", sharePct = 100m } },
        });

    /// <summary>A linked portal user (external-user access) on its portal's client, with Life policy and claim reads.</summary>
    private async Task<HttpClient> PortalClient(string subject, string kind, Guid? party, string? broker)
    {
        var user = await _db.UserWithPermissions(Tenant, subject, LifePermissions.PolicyRead, LifePermissions.ClaimRead);
        await _db.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @i, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", Tenant), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("i", (object?)broker ?? DBNull.Value));
        var (client, audience) = kind == "PARTY" ? ("member-portal", "CUSTOMER") : ("broker-portal", "BROKER");
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", $"CH-{audience}", ("channel_code", audience[..3]), ("audience", audience), ("name", audience),
            ("client_ids", new[] { client }));
        if (broker is not null)
        {
            await Seed("commission.mst_intermediary_register", "ACTIVE", broker, ("intermediary_code", broker), ("type", "BROKER"), ("legal_name", broker),
                ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", $"NODE-{broker}"));
        }

        return _factory.ClientFor(Tenant, subject, client, multiFactor: true);
    }

    private async Task<Guid> IssuedPolicy(Guid applicant, Guid spouse, DateOnly inception, string? orgNodeCode = null)
    {
        var proposal = await Proposal(applicant, spouse, inception, orgNodeCode);
        await Rate(proposal, 1_500_000m);
        await Eligibility(proposal, Ages(inception));
        (await _maker.PostAsync($"/api/v1/life/proposals/{proposal}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var caseId = (await Get($"/api/v1/life/proposals/{proposal}/underwriting-case")).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/life/underwriting-cases/{caseId}/start-assessment", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The underwriting rule set accepts, and standard terms are within the underwriter's authority: one person decides, audited, with no Workflow approval.
        var ruled = await _maker.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Underwriting", code = Product, subjectType = LifeSubjects.UnderwritingCase, subjectId = caseId, inputs = new { sum_assured = 1_500_000m } },
            idempotent: true);
        ruled.StatusCode.ShouldBe(HttpStatusCode.Created, await ruled.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        var decided = await (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/decision", new { outcome = "Accept", decisionCode = "STD", reason = "STANDARD" }))
            .Json();
        (decided.GetProperty("status").GetString(), decided.GetProperty("ruleOutcome").GetString(), decided.GetProperty("decisionPending").GetBoolean())
            .ShouldBe(("Decided", "ACCEPT", false));
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE business_ref_id = @c", ("c", caseId))).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'DecideCase' AND target_id = @c", ("c", caseId.ToString()))).ShouldBe(1L);
        var policy = await Created("/api/v1/life/policies", new { proposalId = proposal });
        await _factory.DrainOutbox();
        return policy;
    }

    private async Task Rate(Guid proposal, decimal sumAssured)
    {
        var calculated = await _maker.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = $"L-{proposal:N}"[..20], sourceDomain = LifeSubjects.SourceDomain, subjectType = LifeSubjects.Proposal, subjectId = proposal, productCode = Product,
            productVersion = 1, currency = "ZAR", frequency = "MONTHLY", values = new { sum_insured = sumAssured, smoker = "NS" },
        }, idempotent: true);
        calculated.StatusCode.ShouldBe(HttpStatusCode.Created, await calculated.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private async Task Eligibility(Guid proposal, int ageNext, string ruleset = "LTERM-ELIG")
    {
        var decided = await _maker.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Eligibility", code = ruleset, subjectType = LifeSubjects.Proposal, subjectId = proposal, inputs = new { age_next = ageNext } }, idempotent: true);
        decided.StatusCode.ShouldBe(HttpStatusCode.Created, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private async Task PayOut(Guid instruction, string providerStatus)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i AND payout_batch_id = @b", ("i", instruction), ("b", batch));
        (await _maker.SimulatorCallback(_factory, _db, payment, providerStatus)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
    }

    private async Task Decide(string businessRefType, Guid businessRefId)
    {
        var page = await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var decided = await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
    }

    private async Task<Guid> Created(string url, object body)
    {
        var response = await _maker.Send(HttpMethod.Post, url, body, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Get(string url)
    {
        var response = await _maker.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        _db.SeedStatic(Tenant, table, status, code, Today.AddYears(-1), fields);
}
