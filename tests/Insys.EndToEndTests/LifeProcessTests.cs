using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Insys.Billing.Contracts;
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
/// death claim paid to the beneficiaries through the Claims Framework (LIF-008, CLM-003). Reinsurance cession and
/// commission clawback come with those modules in Phase 6. The premium is an engineering case (OQ-052).
/// </summary>
public sealed class LifeProcessTests(PostgresServer server) : IAsyncLifetime
{
    private const string Product = "E2E-LTERM";
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static readonly string[] Permissions =
    [
        LifePermissions.ProposalRead, LifePermissions.ProposalCreate, LifePermissions.ProposalUpdate, LifePermissions.UnderwritingRead, LifePermissions.UnderwritingUpdate,
        LifePermissions.UnderwritingWaive, LifePermissions.UnderwritingDecide, LifePermissions.PolicyRead, LifePermissions.PolicyCreate, LifePermissions.PolicyUpdate,
        LifePermissions.ClaimRead, LifePermissions.ClaimCreate, LifePermissions.ClaimUpdate, LifePermissions.ClaimDecide, RatingPermissions.CalculationCreate,
        RatingPermissions.CalculationRead, RulesPermissions.DecisionCreate, RulesPermissions.DecisionRead, BillingPermissions.AccountRead, BillingPermissions.InvoiceRead,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackCreate,
        ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead, ClaimsPermissions.FinancialsUpdate, ClaimsPermissions.InstructionRead,
        ClaimsPermissions.InstructionRelease, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate, WorkflowPermissions.ApprovalRead,
        WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, DocumentPermissions.DocumentRead, PartyPermissions.PartyCreate,
        PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
    ];

    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        await _db.SeedTenant(Tenant, "TENA");
        await _db.UserWithPermissions(Tenant, "life-underwriter", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "senior-life-underwriter", Permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);
        var from = Today.AddYears(-1);

        foreach (var type in new[] { LifeApprovals.UnderwritingApprovalType, LifeApprovals.ClaimApprovalType, PaymentApprovals.PayoutApprovalType, ClaimsApprovals.InstructionApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 5_000_000m), ("delegation_allowed", false));
        }

        // A published Life term product with death and disability benefits, and its policy schedule template.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'LIFE', 'Term life', @coverages, 'LIFE-RISK', 'TERM', 'ISSUE')
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
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }),
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

        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" });
        _maker = _factory.ClientFor(Tenant, "life-underwriter");
        _checker = _factory.ClientFor(Tenant, "senior-life-underwriter");
        foreach (var period in Enumerable.Range(-4, 5).Select(m => month.AddMonths(m)))
        {
            (await _maker.PostAsync($"/api/v1/finance/periods/{period:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

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
        await Decide(LifeApprovals.UnderwritingBusinessRefType, caseId);
        (await Get($"/api/v1/life/underwriting-cases/{caseId}")).GetProperty("status").GetString().ShouldBe("Decided");
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
        (await Get($"/api/v1/life/proposals/{proposal}")).GetProperty("status").GetString().ShouldBe("Converted");
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
        (await _maker.SimulatorCallback(_db, collection, "BOUNCED")).StatusCode.ShouldBe(HttpStatusCode.OK);
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

    [Fact]
    public async Task DeathClaim_IsCheckedRegisteredApprovedAndPaidToTheBeneficiary_ThenThePolicyIsClaimed_LIF008()
    {
        var applicant = await _maker.RegisterPerson("Insured");
        var spouse = await _maker.RegisterPerson("Widow");
        var policy = await IssuedPolicy(applicant, spouse, Today.AddMonths(-1));
        var view = await Get($"/api/v1/life/policies/{policy}");
        var life = view.GetProperty("lives")[0].GetProperty("id").GetGuid();

        // A stranger is not entitled to claim (LIF-008).
        var stranger = await _maker.RegisterPerson("Stranger");
        var refused = await Created("/api/v1/life/claims", new { policyId = policy, claimType = "DEATH", eventDate = Today, causeCode = "I21", claimantPartyId = stranger, insuredLifeId = life });
        (await (await _maker.PostAsync($"/api/v1/life/claims/{refused}/register", null)).Code()).ShouldBe("CLAIMANT_NOT_AUTHORISED");

        var claim = await Created("/api/v1/life/claims", new { policyId = policy, claimType = "DEATH", eventDate = Today, causeCode = "I21", claimantPartyId = spouse, insuredLifeId = life });
        var registered = await (await _maker.PostAsync($"/api/v1/life/claims/{claim}/register", null)).Json();
        var number = registered.GetProperty("claimNumber").GetString()!;
        number.ShouldStartWith("LIF");
        (await _maker.PostAsJsonAsync($"/api/v1/claims/claims/{number}/reserves", new { action = "Set", reserveType = "INDEMNITY", amount = 1_000_000m, currency = "ZAR" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var death = view.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH");
        (await _maker.PostAsJsonAsync($"/api/v1/life/claims/{claim}/assessment",
            new { lines = new[] { new { policyBenefitId = death.GetProperty("id").GetGuid(), amount = 1_000_000m } } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var account = await _maker.AddVerifiedBankAccount(spouse, "4400112233");
        (await _maker.PostAsJsonAsync($"/api/v1/life/claims/{claim}/approval", new
        {
            payees = new[] { new { partyId = spouse, bankAccountId = account, sharePct = 100m } }, reserveType = "INDEMNITY", decisionReason = "DEATH_PROVEN",
            rationale = "Death certificate verified; cause not excluded.",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(LifeApprovals.ClaimBusinessRefType, claim);
        (await Get($"/api/v1/life/claims/{claim}")).GetProperty("status").GetString().ShouldBe("Approved");

        // The Claims Framework releases one instruction per payee; Payments pays it in an approved batch; the claim settles and the policy closes.
        var instruction = await _db.ScalarAsOwner<Guid>("SELECT id FROM claims.claim_payment_instruction WHERE claim_number = @n", ("n", number));
        (await _db.ScalarAsOwner<Guid>("SELECT bank_account_id FROM claims.claim_payment_instruction WHERE id = @i", ("i", instruction))).ShouldBe(account);
        await PayOut(instruction, "PAID");
        (await Get($"/api/v1/life/claims/{claim}")).GetProperty("status").GetString().ShouldBe("Settled");
        var closed = await Get($"/api/v1/life/policies/{policy}");
        closed.GetProperty("benefits").EnumerateArray().Single(b => b.GetProperty("benefitCode").GetString() == "DEATH").GetProperty("status").GetString().ShouldBe("CLAIMED");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM life.outbox_event WHERE payload::text LIKE '%4400112233%'")).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);
    }

    private static int Ages(DateOnly on)
    {
        var birth = new DateOnly(1985, 6, 15);
        var age = on.Year - birth.Year - (birth.AddYears(on.Year - birth.Year) > on ? 1 : 0);
        return age + 1;
    }

    private async Task<Guid> Proposal(Guid applicant, Guid spouse, DateOnly inception) =>
        await Created("/api/v1/life/proposals", new
        {
            productCode = Product, applicantPartyId = applicant, channelCode = "BRANCH", currency = "ZAR", inceptionDate = inception, frequency = "MONTHLY",
            collectionMethod = "DEBIT_ORDER",
            lives = new[] { new { partyId = applicant, lifeRole = "MAIN", smokerStatus = "NS" } },
            covers = new[] { new { insuredPartyId = applicant, benefitCode = "DEATH", sumAssured = 1_000_000m, termYears = 20 }, new { insuredPartyId = applicant, benefitCode = "DISABILITY", sumAssured = 500_000m, termYears = 20 } },
            disclosures = new[] { new { insuredPartyId = applicant, questionCode = "SMOKE_12M", questionVersion = 1, answer = JsonSerializer.SerializeToElement(new { value = false }) } },
            nominations = new[] { new { partyId = spouse, relationshipCode = "SPOUSE", sharePct = 100m } },
        });

    private async Task<Guid> IssuedPolicy(Guid applicant, Guid spouse, DateOnly inception)
    {
        var proposal = await Proposal(applicant, spouse, inception);
        await Rate(proposal, 1_500_000m);
        await Eligibility(proposal, Ages(inception));
        (await _maker.PostAsync($"/api/v1/life/proposals/{proposal}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var caseId = (await Get($"/api/v1/life/proposals/{proposal}/underwriting-case")).GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/life/underwriting-cases/{caseId}/start-assessment", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/life/underwriting-cases/{caseId}/decision", new { outcome = "Accept", decisionCode = "STD", reason = "STANDARD" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await Decide(LifeApprovals.UnderwritingBusinessRefType, caseId);
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

    private async Task Eligibility(Guid proposal, int ageNext)
    {
        var decided = await _maker.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Eligibility", code = "LTERM-ELIG", subjectType = LifeSubjects.Proposal, subjectId = proposal, inputs = new { age_next = ageNext } }, idempotent: true);
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
        (await _maker.SimulatorCallback(_db, payment, providerStatus)).StatusCode.ShouldBe(HttpStatusCode.OK);
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
