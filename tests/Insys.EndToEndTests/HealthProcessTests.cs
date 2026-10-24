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
using Insys.Finance.Contracts;
using Insys.Health.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.EndToEndTests;

/// <summary>
/// The process "Health care to provider payment" of domain-model.json (§27.3 scenarios 6–7, HLT-002 to HLT-009),
/// through the API: enrolment and contributions, eligibility, preauthorisation and its reservation, claim validation and
/// adjudication under ADR-012's defaults (engineering figures, pending actuarial confirmation), approval, the provider
/// payment run, the payout and the posting, with the compensations: a rejected authorisation reserves nothing, an
/// expired one releases its reservation, and a reversed claim gives its benefit use back.
/// </summary>
public sealed class HealthProcessTests(PostgresServer server) : IAsyncLifetime
{
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly Guid SponsorParty = Guid.CreateVersion7();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static readonly string[] Permissions =
    [
        HealthPermissions.SchemeRead, HealthPermissions.SchemeCreate, HealthPermissions.SchemeUpdate, HealthPermissions.MembershipRead, HealthPermissions.MembershipCreate,
        HealthPermissions.MembershipUpdate, HealthPermissions.EligibilityCheck, HealthPermissions.PreauthRead, HealthPermissions.PreauthCreate,
        HealthPermissions.PreauthReview, HealthPermissions.ClaimRead, HealthPermissions.ClaimCreate, HealthPermissions.ClaimUpdate, HealthPermissions.ClaimApprove,
        HealthPermissions.AccumulatorRead, HealthPermissions.CaseRead, HealthPermissions.CaseUpdate, HealthPermissions.RunRead, HealthPermissions.RunUpdate,
        BillingPermissions.AccountRead, BillingPermissions.InvoiceRead, PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate,
        PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackCreate, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, IntegrationPermissions.DeadLetterRead, PartyPermissions.PartyCreate,
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
        await _db.UserWithPermissions(Tenant, "health-administrator", Permissions);
        var checker = await _db.UserWithPermissions(Tenant, "health-manager", Permissions);
        var approverRole = await _db.RoleWithPermissions(Tenant);
        await _db.AssignRole(Tenant, checker, approverRole);
        await _db.AllowBankAccountVerification(Tenant, approverRole, Today.AddYears(-1));
        var from = Today.AddYears(-2);

        foreach (var type in new[] { HealthApprovals.RunApprovalType, PaymentApprovals.PayoutApprovalType })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 5_000_000m), ("delegation_allowed", false));
        }

        // Scheme, members and plan.
        await Seed("health.mst_sponsor_register", "ACTIVE", "S-ACME", ("sponsor_code", "S-ACME"), ("party", SponsorParty.ToString()), ("schemes", Array.Empty<string>()));
        // Remittance advices are generated from the format's template when a run is released.
        await Seed("document.ref_document_categories", "ACTIVE", "REMITTANCE_ADVICE", ("category_code", "REMITTANCE_ADVICE"), ("label", "Remittance advice"),
            ("classification", "CONFIDENTIAL"), ("domain", "HEALTH"), ("allowed_roles", Array.Empty<string>()));
        await Seed("document.cfg_document_templates", "EFFECTIVE", "TPL-RA", ("template_code", "TPL-RA"), ("language", "en"), ("document_type", "REMITTANCE_ADVICE"),
            ("body_ref", "doc://templates/remittance/v1"), ("merge_fields", new[] { "run_code", "provider_id", "amount" }));
        await Seed("health.cfg_remittance_advice_formats", "EFFECTIVE", "RA-STD", ("format_code", "RA-STD"), ("channel", "PORTAL"), ("template", "TPL-RA v1"));

        // The tenant's eligibility response codes, each mapped from an internal outcome.
        foreach (var (code, outcome) in new[] { ("E00", "ELIGIBLE"), ("E01", "NOT_ELIGIBLE"), ("E02", "NOT_FOUND"), ("E03", "WAITING_PERIOD") })
        {
            await Seed("health.ref_eligibility_response_codes", "ACTIVE", code, ("meaning", outcome), ("provider_action", "NONE"), ("outcome", outcome));
        }

        await Seed("health.cfg_scheme_member_categories", "EFFECTIVE", "ALL_STAFF", ("category_code", "ALL_STAFF"), ("eligibility_rule", "employment_type = 'PERMANENT'"),
            ("plan", "HLT-CORE"));
        await Seed("health.cfg_member_numbering_schemes", "EFFECTIVE", "HM", ("prefix", "HM"), ("sequence_length", 9), ("dependant_suffix", "-01"));
        await Seed("health.cfg_dependant_types_and_age_limits", "EFFECTIVE", "CHILD", ("type_code", "CHILD"), ("max_age", 21), ("student_extension_age", 26),
            ("evidence", "BIRTH_CERTIFICATE"));
        await Seed("health.cfg_dependant_types_and_age_limits", "EFFECTIVE", "SPOUSE", ("type_code", "SPOUSE"), ("max_age", 120), ("evidence", "MARRIAGE_CERTIFICATE"));
        await Seed("health.cfg_health_waiting_period_rules", "EFFECTIVE", "WP-GEN", ("rule_code", "WP-GEN"), ("type", "GENERAL"), ("duration", TimeSpan.FromDays(90)),
            ("exceptions", new[] { "EMERGENCY" }));
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-CORE-A", ("plan_code", "HLT-CORE"), ("option_code", "A"), ("benefit_schedule", "BS-CORE"),
            ("network", "NET-A"), ("reference_tariff_schedule_code", "REF"), ("benefit_year", "CALENDAR"));

        // ADR-012's rules for the specialist category: an annual member limit, a family pool, a 10% co-payment, preauthorisation required.
        await Seed("health.cfg_benefit_schedules", "EFFECTIVE", "BS-CORE-SPEC", ("schedule_code", "BS-CORE"), ("benefit", "SPECIALIST"), ("limit_value", 20_000m),
            ("frequency", "PER_YEAR"));
        await Seed("health.cfg_limit_definitions", "EFFECTIVE", "LIM-SPEC-FAM", ("benefit", "SPECIALIST"), ("period", "ANNUAL"), ("amount", 50_000m), ("basis", "PER_FAMILY"));
        await Seed("health.cfg_co_payment_and_deductible_rules", "EFFECTIVE", "CP-SPEC", ("rule_code", "CP-SPEC"), ("benefit", "SPECIALIST"), ("type", "PERCENT"),
            ("value", 10m), ("network_status", "IN_NETWORK"), ("cap", 500m), ("rule_kind", "CO_PAYMENT"), ("cap_basis", "PER_LINE"));
        await Seed("health.cfg_preauthorisation_required_lists", "EFFECTIVE", "PAR-SPEC", ("plan", "HLT-CORE"), ("service", "SPECIALIST"), ("threshold", 0m));
        await Seed("health.cfg_authorisation_validity_periods", "EFFECTIVE", "AV-PROC", ("service_type", "PROCEDURE"), ("days_valid", 30));
        await Seed("health.cfg_benefit_category_mappings", "EFFECTIVE", "M-SPEC", ("code_system", "NRPL"), ("code_from", "0300"), ("code_to", "0399"),
            ("benefit_category", "SPECIALIST"));
        await Seed("health.cfg_high_cost_thresholds", "EFFECTIVE", "HC-SPEC", ("service", "SPECIALIST"), ("amount", 750m), ("review_queue", "UTILISATION"));
        await Seed("health.cfg_payment_run_calendars", "EFFECTIVE", "PRV-WEEKLY", ("run_code", "PRV-WEEKLY"), ("frequency", "WEEKLY"), ("cut_off", "Wednesday 12:00"),
            ("calendar", "ZA-OFFICE"));
        await _db.ExecuteAsOwner("""
            INSERT INTO health.cfg_claim_format_definitions (tenant_id, code, version, effective_from, record_status, created_by, change_reason, format_code, field_mapping, transport)
            VALUES (@t, 'API', 1, @from, 'EFFECTIVE', 'seed', 'seed', 'API', '{}', 'API')
            """, ("t", Tenant), ("from", from));
        await _db.SeedGlobalStatic("health.ref_procedure_and_service_codes", "ACTIVE", "0300", from, ("code_system", "NRPL"), ("active", true));
        await _db.SeedGlobalStatic("health.ref_diagnosis_codes", "ACTIVE", "K35", from, ("code_system", "ICD-10"), ("active", true));
        await Seed("product.cfg_exclusion_library", "EFFECTIVE", "EXC-COSMETIC", ("name", "Cosmetic procedures"), ("wording_ref", "doc://exclusions/cosmetic"),
            ("scope", "BENEFIT"), ("diagnosis_codes", new[] { "L90-L99" }));

        // The network, the contract and the tariffs: contract 1 000, reference 800.
        await Seed("health.cfg_network_definitions", "EFFECTIVE", "NET-A", ("network_code", "NET-A"), ("name", "Core network"), ("providers", new[] { "PR-1" }),
            ("plans", new[] { "HLT-CORE" }), ("tariff_schedule_code", "NET-1"));
        await Seed("health.cfg_provider_contracts", "EFFECTIVE", "C-PR-1", ("contract_id", "C-PR-1"), ("provider", "PR-1"), ("network", "NET-A"),
            ("terms_ref", "doc://contracts/pr-1"), ("tariff_schedule_code", "CON-1"));
        await Seed("health.cfg_network_restriction_rules", "EFFECTIVE", "NR-CORE", ("plan", "HLT-CORE"), ("network", "NET-A"), ("out_of_network_treatment", "COVERED_AT_TARIFF"));
        await Seed("health.cfg_tariff_schedules", "EFFECTIVE", "CON-1-0300", ("tariff_code", "0300"), ("price", 1_000m), ("schedule_code", "CON-1"));
        await Seed("health.cfg_tariff_schedules", "EFFECTIVE", "REF-0300", ("tariff_code", "0300"), ("price", 800m), ("schedule_code", "REF"));

        // GP visits: no preauthorisation, a family pool of 2 000 a year (for the concurrency test).
        await Seed("health.cfg_benefit_category_mappings", "EFFECTIVE", "M-GP", ("code_system", "NRPL"), ("code_from", "0190"), ("code_to", "0199"),
            ("benefit_category", "GP_VISITS"));
        await Seed("health.cfg_limit_definitions", "EFFECTIVE", "LIM-GP-FAM", ("benefit", "GP_VISITS"), ("period", "ANNUAL"), ("amount", 2_000m), ("basis", "PER_FAMILY"));
        await Seed("health.cfg_tariff_schedules", "EFFECTIVE", "CON-1-0190", ("tariff_code", "0190"), ("price", 500m), ("schedule_code", "CON-1"));
        await _db.SeedGlobalStatic("health.ref_procedure_and_service_codes", "ACTIVE", "0190", from, ("code_system", "NRPL"), ("active", true));

        // Billing, Payments and Finance.
        await _db.SeedBillingAndPayments(Tenant, from);
        await Seed("payment.ref_payment_methods", "ACTIVE", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await _db.ExecuteAsOwner("UPDATE payment.mst_payment_provider_and_bank_register SET supported_methods = ARRAY['DEBIT_ORDER','EFT'] WHERE tenant_id = @t",
            ("t", Tenant));
        await Seed("tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        foreach (var account in new[] { "1100", "1200", "2100", "4000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"),
                 })
        {
            await Seed("finance.cfg_posting_rules", "EFFECTIVE", rule, ("rule_code", rule), ("transaction_type", type), ("debit_account", debit), ("credit_account", credit));
        }

        var month = new DateOnly(Today.Year, Today.Month, 1);
        foreach (var period in Enumerable.Range(-6, 7).Select(m => month.AddMonths(m)))
        {
            await Seed("finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{period:yyyy-MM}", ("period_code", $"{period:yyyy-MM}"), ("start_date", period),
                ("end_date", period.AddMonths(1).AddDays(-1)));
        }

        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" });
        _maker = _factory.ClientFor(Tenant, "health-administrator");
        _checker = _factory.ClientFor(Tenant, "health-manager");
        foreach (var period in Enumerable.Range(-6, 7).Select(m => month.AddMonths(m)))
        {
            (await _maker.PostAsync($"/api/v1/finance/periods/{period:yyyy-MM}/open", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009()
    {
        await Organisation();
        // The provider is a party with a verified bank account (ADR-013).
        var providerParty = await _maker.RegisterPerson("Specialist");
        var providerAccount = await _maker.AddVerifiedBankAccount(_checker, _factory, providerParty, "7700889911");
        await _db.ExecuteAsOwner("""
            INSERT INTO health.mst_provider_register (tenant_id, code, version, effective_from, record_status, created_by, change_reason, provider_id, name,
                                                      registration_numbers, locations, status, provider_type, party_id)
            VALUES (@t, 'PR-1', 1, @from, 'ACTIVE', 'seed', 'seed', 'PR-1', 'Dr Specialist', '{"practice":"PR1"}', '[]', 'ACTIVE', 'SPEC', @party)
            """, ("t", Tenant), ("from", Today.AddYears(-2)), ("party", providerParty));

        // RegisterScheme; the scheme's contribution per member; EnrolMember (HealthMemberEnrolled) → Billing's contribution schedule.
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var schemeNumber = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_contribution_rules", "EFFECTIVE", "CR-ACME", ("scheme", schemeNumber), ("member_category", "ALL_STAFF"), ("rate", 1_850m),
            ("employer_share", 50m));
        var principal = await _maker.RegisterPerson("Member");
        var enrolment = new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "CONTRACT" }, orgNodeCode = "ORG-BR-A",
        };
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/health/memberships", enrolment, idempotent: true)).Code()).ShouldBe("DEPENDANT_NOT_ELIGIBLE");
        var membership = await Created("/api/v1/health/memberships", enrolment with { facts = new { employment_type = "PERMANENT" } });
        await _factory.DrainOutbox();
        var member = await Get($"/api/v1/health/memberships/{membership}");
        member.GetProperty("status").GetString().ShouldBe("Active");
        member.GetProperty("waitingPeriods")[0].GetProperty("endsOn").GetString().ShouldBe(Today.AddMonths(-6).AddDays(90).ToString("yyyy-MM-dd"));
        // Half the contribution is the employer's, billed to the sponsor's account; the member's half to the payer's.
        (await Schedules(membership)).ShouldBe([("HEALTH", principal, 925m), ("HEALTH_ER", SponsorParty, 925m)]);

        // Dependants stay within their type's age limit; a spouse joins, and the contribution is amended for two members.
        var spouse = await _maker.RegisterPerson("Spouse");
        (await (await _maker.Send(HttpMethod.Post, $"/api/v1/health/memberships/{membership}/dependants",
            new { partyId = spouse, dependantType = "CHILD", student = false, startDate = Today }, idempotent: true)).Code()).ShouldBe("DEPENDANT_NOT_ELIGIBLE");
        (await _maker.Send(HttpMethod.Post, $"/api/v1/health/memberships/{membership}/dependants",
            new { partyId = spouse, dependantType = "SPOUSE", student = false, startDate = Today }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Schedules(membership)).ShouldBe([("HEALTH", principal, 1_850m), ("HEALTH_ER", SponsorParty, 1_850m)]);

        // CheckEligibility → EligibilityDecisionRecorded (HLT-003).
        var number = member.GetProperty("memberNumber").GetString()!;
        (await Eligibility(number, "00")).ShouldBe("E00");
        (await Eligibility(number, "01")).ShouldBe("E00");
        (await Eligibility(number, "07")).ShouldBe("E02");
        (await Eligibility(number, "00", Today.AddMonths(-6).AddDays(10))).ShouldBe("E03");
        (await Eligibility(number, "00", Today.AddMonths(-7))).ShouldBe("E01");

        // Compensation: a rejected authorisation reserves nothing.
        var rejected = await Authorisation(membership, principal);
        (await _maker.PostAsync($"/api/v1/health/preauthorisations/{rejected}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/health/preauthorisations/{rejected}/reject", new { reason = "NOT_INDICATED" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Accumulator(membership, "MEMBER")).ShouldBe((0m, 0m));

        // RequestAuthorisation; clinical review; PreauthorisationApproved → ReserveBenefit on the member's and the family's accumulators.
        var preauth = await Authorisation(membership, principal);
        var line = (await Get($"/api/v1/health/preauthorisations/{preauth}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/health/preauthorisations/{preauth}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/health/preauthorisations/{preauth}/approve",
            new { lines = new[] { new { lineId = line, authorisedAmount = 1_000m } }, validFrom = Today })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Accumulator(membership, "MEMBER")).ShouldBe((0m, 1_000m));
        (await Accumulator(membership, "FAMILY")).ShouldBe((0m, 1_000m));

        // MedicalClaimSubmitted; ValidateClaim; AdjudicateClaim (ADR-012): contract tariff 1 000, allowed 1 000, 10% co-payment 100, paid 900, the member pays 300.
        var claim = await Claim(membership, principal, 1_200m);
        (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var adjudicated = await (await _maker.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null)).Json();
        adjudicated.GetProperty("status").GetString().ShouldBe("Adjudicated");
        var result = adjudicated.GetProperty("lines")[0];
        (result.GetProperty("tariffAmount").GetDecimal(), result.GetProperty("allowedAmount").GetDecimal(), result.GetProperty("copayAmount").GetDecimal(),
            result.GetProperty("paidAmount").GetDecimal(), result.GetProperty("memberLiability").GetDecimal()).ShouldBe((1_000m, 1_000m, 100m, 900m, 300m));
        result.GetProperty("preauthLineId").GetGuid().ShouldBe(line);
        await _factory.DrainOutbox();

        // ConsumeBenefit: the reservation is released and the paid amount used; the authorisation is utilised; the high-cost line opens a review.
        (await Accumulator(membership, "MEMBER")).ShouldBe((900m, 0m));
        (await Accumulator(membership, "FAMILY")).ShouldBe((900m, 0m));
        (await Get($"/api/v1/health/preauthorisations/{preauth}")).GetProperty("status").GetString().ShouldBe("Utilised");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.utilisation_case WHERE claim_id = @c AND rule_code = 'HC-SPEC'", ("c", claim))).ShouldBe(1L);

        // A duplicate pends for a person, who rejects it; it changes no accumulator.
        var duplicate = await Claim(membership, principal, 1_200m);
        (await _maker.PostAsync($"/api/v1/health/claims/{duplicate}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var pended = await (await _maker.PostAsync($"/api/v1/health/claims/{duplicate}/adjudicate", null)).Json();
        (pended.GetProperty("status").GetString(), pended.GetProperty("pendReason").GetString()).ShouldBe(("Pended", "DUPLICATE"));
        var reviewed = await (await _maker.PostAsJsonAsync($"/api/v1/health/claims/{duplicate}/review", new { reviews = new[] { new { lineNo = 1, decision = "Reject" } } })).Json();
        (reviewed.GetProperty("status").GetString(), reviewed.GetProperty("totalPaid").GetDecimal()).ShouldBe(("Adjudicated", 0m));
        (await Accumulator(membership, "MEMBER")).ShouldBe((900m, 0m));

        // ApproveForPayment; the run is cut off, calculated (one remittance to the provider's verified account), approved in Workflow and released.
        (await _maker.PostAsync($"/api/v1/health/claims/{claim}/approve", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var run = await Created("/api/v1/health/payment-runs", new { runCode = "PRV-WEEKLY", currency = "ZAR" });
        (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/cut-off", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var calculated = await (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/calculate", null)).Json();
        (calculated.GetProperty("totalAmount").GetDecimal(), calculated.GetProperty("claimCount").GetInt32()).ShouldBe((900m, 1));
        calculated.GetProperty("remittances")[0].GetProperty("bankAccountId").GetGuid().ShouldBe(providerAccount);
        await _factory.DrainOutbox();

        // F103: the run pays a branch-A member's claim. A branch-B user cannot see or release it; a user covering branch A sees all of it.
        var branchB = await ScopedClient("run-branch-b", "ORG-BR-B");
        var north = await ScopedClient("run-north", "ORG-NORTH");
        (await branchB.GetAsync($"/api/v1/health/payment-runs/{run}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await branchB.PostAsync($"/api/v1/health/payment-runs/{run}/release", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var seen = await (await north.GetAsync($"/api/v1/health/payment-runs/{run}")).Json();
        (seen.GetProperty("partlyInScope").GetBoolean(), seen.GetProperty("totalAmount").GetDecimal()).ShouldBe((false, 900m));
        (await (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/release", null)).Code()).ShouldBe("INVALID_STATE_TRANSITION");
        await Decide(HealthApprovals.RunBusinessRefType, run);
        (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // ProviderPaymentRunReleased → the claim is paid (MedicalClaimSettled → Finance posts claims paid) and Payments pays the remittance in a batch.
        (await Get($"/api/v1/health/claims/{claim}")).GetProperty("status").GetString().ShouldBe("Paid");
        await _factory.DrainOutbox();
        var advice = await _db.ScalarAsOwner<Guid>("SELECT document_id FROM health.remittance_advice WHERE run_id = @r", ("r", run));
        (await _db.ScalarAsOwner<string>("SELECT category_code FROM document.document WHERE id = @d", ("d", advice))).ShouldBe("REMITTANCE_ADVICE");
        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM finance.journal
            WHERE source_event_id IN (SELECT id FROM health.outbox_event WHERE event_type = 'health.MedicalClaimSettled' AND aggregate_id = @c)
            """, ("c", claim))).ShouldBe(1L);
        var remittance = calculated.GetProperty("remittances")[0].GetProperty("id").GetGuid();
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.ClaimPayout, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        await Decide(PaymentApprovals.PayoutBusinessRefType, batch);
        (await _maker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", remittance));
        (await _db.ScalarAsOwner<Guid>("SELECT bank_account_id FROM payment.payment WHERE id = @p", ("p", payment))).ShouldBe(providerAccount);
        (await _maker.SimulatorCallback(_db, payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/health/payment-runs/{run}")).GetProperty("status").GetString().ShouldBe("Reconciled");

        // The remittance's payment follows its claims' branch too.
        var byRemittance = $"/api/v1/payments/payments?businessRefType={HealthReferences.RemittanceRefType}&businessRefId={remittance}";
        (await (await branchB.GetAsync(byRemittance)).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await (await north.GetAsync(byRemittance)).Json()).GetProperty("items").GetArrayLength().ShouldBe(1);

        // Compensation: a reversed claim gives its benefit use back.
        (await _maker.PostAsJsonAsync($"/api/v1/health/claims/{claim}/reverse", new { reason = "BILLED_IN_ERROR" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Accumulator(membership, "MEMBER")).ShouldBe((0m, 0m));

        // Compensation: an authorisation that expires releases its reservation.
        var expiring = await Authorisation(membership, principal);
        var expiringLine = (await Get($"/api/v1/health/preauthorisations/{expiring}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/health/preauthorisations/{expiring}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/health/preauthorisations/{expiring}/approve",
            new { lines = new[] { new { lineId = expiringLine, authorisedAmount = 600m } }, validFrom = Today.AddDays(-40) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Accumulator(membership, "MEMBER")).ShouldBe((0m, 600m));
        await _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/health/preauthorisations/{expiring}")).GetProperty("status").GetString().ShouldBe("Expired");
        (await Accumulator(membership, "MEMBER")).ShouldBe((0m, 0m));

        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.outbox_event WHERE payload::text LIKE '%K35%'")).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);
    }

    /// <summary>
    /// Performance smoke test of a hot path (Phase 7): adjudication against the benefit accumulators, which it locks and
    /// consumes. p95 within the standard operation target (NFR-002: 2 s): one membership's claims in turn, and claims of
    /// eight memberships eight at a time.
    /// </summary>
    [Fact]
    public async Task Adjudication_AgainstTheBenefitAccumulators_StaysWithinTheStandardLatencyTarget_NFR002()
    {
        await Provider();
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var memberships = new List<(Guid Membership, Guid Principal)>();
        for (var m = 0; m < 8; m++)
        {
            var principal = await _maker.RegisterPerson($"Smoke{m}");
            memberships.Add((await Created("/api/v1/health/memberships", new
            {
                principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
                currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
            }), principal));
        }

        await _factory.DrainOutbox();
        async Task<Guid> ValidatedClaim(int membership, int day)
        {
            var (id, principal) = memberships[membership];
            var claim = await Created("/api/v1/health/claims", new
            {
                source = "PROVIDER", providerId = "PR-1", membershipId = id, patientPartyId = principal, serviceDate = Today.AddDays(-1 - day), formatCode = "API",
                currency = "ZAR", lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 300m } },
            });
            (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            return claim;
        }

        var inTurn = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            inTurn.Add(await ValidatedClaim(0, i));
        }

        var together = new List<Guid>();
        for (var i = 0; i < 24; i++)
        {
            together.Add(await ValidatedClaim(i % 8, 30 + (i / 8)));
        }

        var adjudicator = _factory.ClientFor(Tenant, "health-administrator");
        async Task Adjudicate(Guid claim)
        {
            var response = await adjudicator.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        var sequential = await Latency.Sequential("Adjudication against the accumulators, sequential", 5, 20, i => Adjudicate(inTurn[i < 0 ? -i - 1 : i + 5]));
        var concurrent = await Latency.Concurrent("Adjudication against the accumulators, 8 memberships at a time", 24, 8, i => Adjudicate(together[i]));

        sequential.P95.ShouldBeLessThan(TimeSpan.FromSeconds(2), sequential.ToString());
        concurrent.P95.ShouldBeLessThan(TimeSpan.FromSeconds(2), concurrent.ToString());
    }

    /// <summary>ADR-012: accumulators are locked in one order and adjudication is retried on a conflict, so concurrent claims never overspend a shared pool.</summary>
    [Fact]
    public async Task ConcurrentClaims_AgainstOneFamilyLimit_NeverExceedIt_AndNeverDeadlock()
    {
        await Provider();
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Pool");
        var spouse = await _maker.RegisterPerson("PoolSpouse");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        (await _maker.Send(HttpMethod.Post, $"/api/v1/health/memberships/{membership}/dependants",
            new { partyId = spouse, dependantType = "SPOUSE", student = false, startDate = Today.AddMonths(-6) }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // Eight GP visits of 500 for two people, on different days: the family pool of 2 000 pays four of them.
        var claims = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var claim = await Created("/api/v1/health/claims", new
            {
                source = "PROVIDER", providerId = "PR-1", membershipId = membership, patientPartyId = i % 2 == 0 ? principal : spouse, serviceDate = Today.AddDays(-1 - i),
                formatCode = "API", currency = "ZAR", lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 500m } },
            });
            (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            claims.Add(claim);
        }

        var responses = await Task.WhenAll(claims.Select(claim => _factory.ClientFor(Tenant, "health-administrator").PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null)));
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        (await _db.ScalarAsOwner<decimal>("SELECT coalesce(sum(total_paid), 0) FROM health.medical_claim WHERE membership_id = @m", ("m", membership))).ShouldBe(2_000m);
        (await _db.ScalarAsOwner<decimal>(
            "SELECT used_amount FROM health.benefit_accumulator WHERE membership_id = @m AND benefit_category = 'GP_VISITS' AND scope = 'FAMILY'", ("m", membership)))
            .ShouldBe(2_000m);
        (await _db.ScalarAsOwner<long>(
            "SELECT count(*) FROM health.benefit_accumulator WHERE membership_id = @m AND limit_amount IS NOT NULL AND used_amount + reserved_amount > limit_amount",
            ("m", membership))).ShouldBe(0L);
    }

    /// <summary>Phase 5 review, item 5: member reimbursement on the plan's REFERENCE_TARIFF basis, paid to the principal's verified account through a run.</summary>
    [Fact]
    public async Task MemberClaim_IsPricedAtTheReferenceTariff_AndPaidToThePrincipalsVerifiedAccountInARun()
    {
        await Seed("health.cfg_tariff_schedules", "EFFECTIVE", "REF-0190", ("tariff_code", "0190"), ("price", 400m), ("schedule_code", "REF"));
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Claimant");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        var account = await _maker.AddVerifiedBankAccount(_checker, _factory, principal, "5500112233");

        // F009: Health's fraud indicators are evaluated when the claim is submitted; reaching the threshold opens a fraud case,
        // which does not hold up adjudication (it is investigated separately).
        await Seed("health.cfg_health_fraud_indicator_definitions", "EFFECTIVE", "E2E-MEMBER-SUBMITTED", ("indicator_code", "LATE_MEMBER"),
            ("logic", "days_to_submission >= 3"), ("score", 50), ("threshold", 50));
        var claim = await Created("/api/v1/health/claims", new
        {
            source = "MEMBER", membershipId = membership, patientPartyId = principal, serviceDate = Today.AddDays(-3), formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 650m } },
        });
        var claimNumber = (await _db.ScalarAsOwner<string>("SELECT claim_number FROM health.medical_claim WHERE id = @c", ("c", claim)))!;
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.fraud_case WHERE subject_type = 'CLAIM' AND subject_ref = @n", ("n", claimNumber)))
            .ShouldBe(1L, "F009: fraud indicators are evaluated at submission");

        // GAP-040: the fraud case is found from the claim it is about, and the claim from the membership.
        var cases = await Get($"/api/v1/health/fraud-cases?claimNumber={claimNumber}");
        DocumentedSchema.ResponseProblems(cases, "core", "HealthListFraudCases").ShouldBeEmpty();
        var fraudCase = cases.GetProperty("items").EnumerateArray().Single();
        (await Get($"/api/v1/health/fraud-cases/{fraudCase.GetProperty("id").GetGuid()}")).GetProperty("subjectRef").GetString().ShouldBe(claimNumber);
        (await Get($"/api/v1/health/claims?membershipId={membership}")).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldContain(claim);
        (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var adjudicated = await (await _maker.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null)).Json();
        adjudicated.GetProperty("status").GetString().ShouldBe("Adjudicated", adjudicated.ToString());
        var line = adjudicated.GetProperty("lines")[0];
        (line.GetProperty("tariffAmount").GetDecimal(), line.GetProperty("allowedAmount").GetDecimal()).ShouldBe((400m, 400m));
        var paid = adjudicated.GetProperty("totalPaid").GetDecimal();
        paid.ShouldBeGreaterThan(0m);

        (await _maker.PostAsync($"/api/v1/health/claims/{claim}/approve", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var run = await Created("/api/v1/health/payment-runs", new { runCode = "PRV-WEEKLY", currency = "ZAR" });
        (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/cut-off", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var calculated = await (await _maker.PostAsync($"/api/v1/health/payment-runs/{run}/calculate", null)).Json();
        var remittance = calculated.GetProperty("remittances").EnumerateArray().Single();
        var memberNumber = (await Get($"/api/v1/health/memberships/{membership}")).GetProperty("memberNumber").GetString();
        (remittance.GetProperty("providerId").GetString(), remittance.GetProperty("bankAccountId").GetGuid(), remittance.GetProperty("amount").GetDecimal())
            .ShouldBe((memberNumber, account, paid));
    }

    private async Task Provider()
    {
        var providerParty = await _maker.RegisterPerson("Doctor");
        await _db.ExecuteAsOwner("""
            INSERT INTO health.mst_provider_register (tenant_id, code, version, effective_from, record_status, created_by, change_reason, provider_id, name,
                                                      registration_numbers, locations, status, provider_type, party_id)
            VALUES (@t, 'PR-1', 1, @from, 'ACTIVE', 'seed', 'seed', 'PR-1', 'Dr Specialist', '{"practice":"PR1"}', '[]', 'ACTIVE', 'SPEC', @party)
            """, ("t", Tenant), ("from", Today.AddYears(-2)), ("party", providerParty));
    }

    private async Task<string> Eligibility(string memberNumber, string suffix, DateOnly? serviceDate = null)
    {
        var response = await _maker.PostAsJsonAsync("/api/v1/health/eligibility",
            new { memberNumber, dependantSuffix = suffix, providerId = "PR-1", serviceType = "CONSULTATION", serviceDate });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("responseCode").GetString()!;
    }

    private Task<Guid> Authorisation(Guid membership, Guid patient) =>
        Created("/api/v1/health/preauthorisations", new
        {
            membershipId = membership, patientPartyId = patient, providerId = "PR-1", diagnosisCodes = new[] { "K35" }, serviceType = "PROCEDURE",
            lines = new[] { new { procedureCode = "0300", quantity = 1, requestedAmount = 1_200m } },
        });

    /// <summary>
    /// addenda/health-plan-products.md: a plan names its Product Factory product. Enrolment needs it on sale and records it,
    /// so product scopes reach Health. A membership on a plan with no product takes it once a version names one; until
    /// then /health lists the plan.
    /// </summary>
    [Fact]
    public async Task HealthPlans_AreSoldAsProducts_AndProductScopesReachTheirMemberships_OQ080()
    {
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), 'HLT-PROD', 1, @from, 'PUBLISHED', 'seed', 'published', 'HLT-PROD', 'HEALTH', 'Comprehensive', @coverages, 'HEALTH', 'MEDICAL', 'ISSUE')
            """, ("t", Tenant), ("from", Today.AddYears(-1)), ("coverages", new[] { "SPECIALIST" }));
        (string, object)[] Plan(string plan, string? product) => new[] { ("plan_code", (object)plan), ("option_code", "A"), ("benefit_schedule", "BS-CORE"), ("network", "NET-A"),
            ("reference_tariff_schedule_code", "REF"), ("benefit_year", "CALENDAR"), ("product_code", (object?)product ?? DBNull.Value) };
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-PRD-A", Plan("HLT-PRD", "HLT-PROD"));
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-OFF-A", Plan("HLT-OFF", "HLT-NONE"));
        // A plan without a product until tomorrow, when its next version names one.
        await _db.SeedStatic(Tenant, "health.cfg_plans_and_options", "EFFECTIVE", "HLT-LATE-A", 1, Today.AddYears(-1), Today.AddDays(1), Plan("HLT-LATE", null));
        await _db.SeedStatic(Tenant, "health.cfg_plans_and_options", "EFFECTIVE", "HLT-LATE-A", 2, Today.AddDays(1), null, Plan("HLT-LATE", "HLT-PROD"));

        var principal = await _maker.RegisterPerson("Individual");
        object Enrolment(string plan) => new { principalPartyId = principal, planCode = plan, optionCode = "A", startDate = Today, currency = "ZAR", collectionMethod = "DEBIT_ORDER" };
        var membership = await Created("/api/v1/health/memberships", Enrolment("HLT-PRD"));
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM health.membership WHERE id = @m", ("m", membership))).ShouldBe("HLT-PROD");
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/health/memberships", Enrolment("HLT-OFF"), idempotent: true)).Code()).ShouldBe("HEALTH_PRODUCT_NOT_AVAILABLE");

        // Product scopes now reach Health.
        foreach (var (subject, product, status) in new[] { ("hlt-own-product", "HLT-PROD", HttpStatusCode.OK), ("hlt-other-product", "OTHER", HttpStatusCode.NotFound) })
        {
            var user = await _db.UserWithPermissions(Tenant, subject, Permissions);
            await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY[@p] WHERE user_id = @u", ("p", product), ("u", user));
            (await _factory.ClientFor(Tenant, subject).GetAsync($"/api/v1/health/memberships/{membership}")).StatusCode.ShouldBe(status, subject);
        }

        // A plan with no product is listed on /health; its membership takes the product once the next version names one.
        // So are plan options naming an unknown product, a product of another domain, or not the one their plan's other options name.
        await _db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), 'GEN-X', 1, @from, 'DRAFT', 'seed', 'draft', 'GEN-X', 'GENERAL', 'Motor', @coverages, 'MOTOR', 'PRIVATE', 'ISSUE')
            """, ("t", Tenant), ("from", Today.AddYears(-1)), ("coverages", new[] { "OWN_DAMAGE" }));
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-GEN-A", Plan("HLT-GEN", "GEN-X"));
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-MIX-A", Plan("HLT-MIX", "HLT-PROD"));
        await Seed("health.cfg_plans_and_options", "EFFECTIVE", "HLT-MIX-B", [.. Plan("HLT-MIX", "HLT-PROD").Select(f => f.Item1 switch
        {
            "option_code" => (f.Item1, (object)"B"),
            "product_code" => (f.Item1, "HLT-OTHER"),
            _ => f,
        })]);
        var check = (await (await _factory.CreateClient().GetAsync("/health")).Json()).GetProperty("checks").GetProperty("health-plan-products");
        check.GetProperty("status").GetString().ShouldBe("Degraded");
        var problems = check.GetProperty("data").GetProperty("planOptions").EnumerateArray().Select(p => p.GetString()!).ToList();
        foreach (var expected in new[]
                 {
                     "HLT-LATE/A names no product", "HLT-OFF/A names HLT-NONE, which is not a product", "HLT-GEN/A names GEN-X, a GENERAL product",
                     "HLT-MIX's options name different products (HLT-PROD, HLT-OTHER)",
                 })
        {
            problems.ShouldContain(p => p.EndsWith(expected), expected);
        }

        problems.ShouldNotContain(p => p.Contains("HLT-PRD/"), "a plan naming one Health product is fine");
        // Enrolled through a scheme, so it is billed: its schedule and first invoice have no product yet.
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var schemeNumber = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_scheme_member_categories", "EFFECTIVE", "LATE_STAFF", ("category_code", "LATE_STAFF"), ("eligibility_rule", "employment_type = 'PERMANENT'"),
            ("plan", "HLT-LATE"));
        await Seed("health.cfg_contribution_rules", "EFFECTIVE", "CR-LATE", ("scheme", schemeNumber), ("member_category", "LATE_STAFF"), ("rate", 1_000m), ("employer_share", 0m));
        var late = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "LATE_STAFF", planCode = "HLT-LATE", optionCode = "A", startDate = Today, currency = "ZAR",
            collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM health.membership WHERE id = @m", ("m", late))).ShouldBeNull();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.premium_schedule WHERE policy_ref = @m AND product_code IS NULL", ("m", late))).ShouldBe(1L);

        // The next version names the product: the daily job gives it to the membership, and Billing to its schedule and later invoices.
        await _db.UserWithPermissions(Tenant, "hlt-jobs", "daily_job.run");
        var jobs = _factory.ClientFor(Tenant, "hlt-jobs");
        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today.AddDays(1) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM health.membership WHERE id = @m", ("m", late))).ShouldBe("HLT-PROD");
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM billing.premium_schedule WHERE policy_ref = @m", ("m", late))).ShouldBe("HLT-PROD");
        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today.AddMonths(1) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var laterInvoice = await _db.ScalarAsOwner<Guid>(
            "SELECT id FROM billing.invoice WHERE policy_ref = @m AND product_code = 'HLT-PROD' ORDER BY due_date DESC LIMIT 1", ("m", late));
        laterInvoice.ShouldNotBe(Guid.Empty, "an invoice raised after the product is assigned carries it");
        (await _factory.ClientFor(Tenant, "hlt-own-product").GetAsync($"/api/v1/billing/invoices/{laterInvoice}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _factory.ClientFor(Tenant, "hlt-other-product").GetAsync($"/api/v1/billing/invoices/{laterInvoice}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task Organisation()
    {
        foreach (var (code, type, parent) in new[] { ("ORG-ROOT", "LEGAL_ENTITY", (string?)null), ("ORG-NORTH", "REGION", "ORG-ROOT"), ("ORG-BR-A", "BRANCH", "ORG-NORTH"),
                     ("ORG-BR-B", "BRANCH", "ORG-ROOT") })
        {
            await Seed("tenant.mst_organisation_structure", "ACTIVE", code, ("node_code", code), ("type", type), ("parent", (object?)parent ?? DBNull.Value), ("name", code));
        }
    }

    private async Task<HttpClient> ScopedClient(string subject, string orgNode)
    {
        var user = await _db.UserWithPermissions(Tenant, subject, Permissions);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = ARRAY[@n] WHERE user_id = @u", ("n", orgNode), ("u", user));
        return _factory.ClientFor(Tenant, subject);
    }

    private Task<Guid> Claim(Guid membership, Guid patient, decimal claimed) =>
        Created("/api/v1/health/claims", new
        {
            source = "PROVIDER", providerId = "PR-1", membershipId = membership, patientPartyId = patient, serviceDate = Today, formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0300", diagnosisCode = "K35", quantity = 1, claimedAmount = claimed } },
        });

    private async Task<(decimal Used, decimal Reserved)> Accumulator(Guid membership, string scope)
    {
        var accumulators = (await Get($"/api/v1/health/memberships/{membership}/accumulators")).EnumerateArray()
            .Where(a => a.GetProperty("benefitCategory").GetString() == "SPECIALIST" && a.GetProperty("scope").GetString() == scope).ToList();
        return accumulators.Count == 0 ? (0m, 0m) : (accumulators.Single().GetProperty("used").GetDecimal(), accumulators.Single().GetProperty("reserved").GetDecimal());
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

    private async Task<List<(string, Guid, decimal)>> Schedules(Guid membership)
    {
        var rows = new List<(string, Guid, decimal)>();
        foreach (var domain in new[] { "HEALTH", "HEALTH_ER" })
        {
            var payer = await _db.ScalarAsOwner<Guid>("""
                SELECT a.payer_party_id FROM billing.premium_schedule s JOIN billing.billing_account a ON a.tenant_id = s.tenant_id AND a.id = s.billing_account_id
                WHERE s.source_domain = @d AND s.policy_ref = @m
                """, ("d", domain), ("m", membership));
            var amount = await _db.ScalarAsOwner<decimal>("SELECT instalment_amount FROM billing.premium_schedule WHERE source_domain = @d AND policy_ref = @m",
                ("d", domain), ("m", membership));
            rows.Add((domain, payer, amount));
        }

        return rows;
    }

    /// <summary>
    /// F103 (addenda/access-scopes.md): a membership belongs to an organisation node, and a user scoped to a region sees
    /// only its branches' memberships. Health records have no product, so a product-scoped user sees none (OQ-080).
    /// </summary>
    [Fact]
    public async Task ScopedAssignments_SeeOnlyMembershipsWithinTheirOrganisation_AndProductScopesReachNone_F103_OQ080()
    {
        await Organisation();
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var schemeNumber = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_contribution_rules", "EFFECTIVE", "CR-SCOPE", ("scheme", schemeNumber), ("member_category", "ALL_STAFF"), ("rate", 1_000m), ("employer_share", 0m));
        var principal = await _maker.RegisterPerson("Scoped");
        async Task<Guid> Enrol(string orgNodeCode) => await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today,
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" }, orgNodeCode,
        });
        var inA = await Enrol("ORG-BR-A");
        var inB = await Enrol("ORG-BR-B");

        var north = await _db.UserWithPermissions(Tenant, "health-north", Permissions);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_branches = '{ORG-NORTH}' WHERE user_id = @u", ("u", north));
        var product = await _db.UserWithPermissions(Tenant, "health-product", Permissions);
        await _db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = '{HLT-CORE}' WHERE user_id = @u", ("u", product));
        var northClient = _factory.ClientFor(Tenant, "health-north");
        var productClient = _factory.ClientFor(Tenant, "health-product");

        (await (await northClient.GetAsync($"/api/v1/health/memberships?principalPartyId={principal}")).Json()).GetProperty("items").EnumerateArray()
            .Select(m => m.GetProperty("id").GetGuid()).ShouldBe([inA]);
        (await northClient.GetAsync($"/api/v1/health/memberships/{inA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await northClient.GetAsync($"/api/v1/health/memberships/{inB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await productClient.GetAsync($"/api/v1/health/memberships/{inA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a Health record has no product to match (OQ-080)");
        (await (await productClient.GetAsync($"/api/v1/health/memberships?principalPartyId={principal}")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
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
        _db.SeedStatic(Tenant, table, status, code, Today.AddYears(-2), fields);
}
