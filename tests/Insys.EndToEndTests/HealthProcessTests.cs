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
    private static readonly DateOnly Today = WindowClock.Today;

    private static readonly string[] Permissions =
    [
        HealthPermissions.SchemeRead, HealthPermissions.SchemeCreate, HealthPermissions.SchemeUpdate, HealthPermissions.MembershipRead, HealthPermissions.MembershipCreate,
        HealthPermissions.MembershipUpdate, HealthPermissions.EligibilityCheck, HealthPermissions.PreauthRead, HealthPermissions.PreauthCreate,
        HealthPermissions.PreauthReview, HealthPermissions.ClaimRead, HealthPermissions.ClaimCreate, HealthPermissions.ClaimUpdate, HealthPermissions.ClaimApprove,
        HealthPermissions.AccumulatorRead, HealthPermissions.CaseRead, HealthPermissions.CaseUpdate, HealthPermissions.RunRead, HealthPermissions.RunUpdate,
        HealthPermissions.ProviderRead, HealthPermissions.ProviderUpdate, HealthPermissions.ComplexCaseRead, HealthPermissions.ComplexCaseUpdate, HealthPermissions.ReferralRead,
        HealthPermissions.ReferralCreate, HealthPermissions.ReferralDecide, HealthPermissions.ReferralConsent,
        BillingPermissions.AccountRead, BillingPermissions.InvoiceRead, PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate,
        PaymentPermissions.PayoutRelease, PaymentPermissions.CallbackEndpointUpdate, FinancePermissions.JournalRead, FinancePermissions.PeriodUpdate,
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
        _watchdog = StallWatchdog.Start(_db, TimeSpan.FromSeconds(90), $"HealthProcessTests.{TestContext.Current.TestMethod?.MethodName}");
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
        // V0094: every service the tenant offers, for eligibility; only PROCEDURE needs authorisation (above).
        await Seed("health.cfg_service_types", "EFFECTIVE", "ST-CONS", ("service_type", "CONSULTATION"));
        await Seed("health.cfg_service_types", "EFFECTIVE", "ST-PROC", ("service_type", "PROCEDURE"));
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
        foreach (var account in new[] { "1100", "1200", "1300", "2100", "4000" })
        {
            await Seed("finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", ("account_code", account), ("name", account), ("type", "ASSET"),
                ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var (rule, type, debit, credit) in new[]
                 {
                     ("PR-PREM", TransactionTypes.PremiumDue, "1100", "4000"), ("PR-RECEIPT", TransactionTypes.PaymentReceived, "1200", "1100"),
                     ("PR-CLM-PAID", TransactionTypes.ClaimPaid, "2100", "1200"), ("PR-CLM-REV", TransactionTypes.ClaimPaidReversed, "1300", "2100"),
                     ("PR-REC-RAISED", TransactionTypes.ProviderRecoveryRaised, "1300", "2100"), ("PR-REC-OFFSET", TransactionTypes.ProviderRecoveryOffset, "1200", "1300"),
                     ("PR-REC-REPAID", TransactionTypes.ProviderRecoveryRepaid, "1300", "1200"), ("PR-REC-WOFF", TransactionTypes.ProviderRecoveryWrittenOff, "4000", "1300"),
                     ("PR-REC-REV", TransactionTypes.ProviderRecoveryReversed, "2100", "1300"), ("PR-REC-SETTLED", TransactionTypes.ProviderRecoverySettled, "1100", "1300"),
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

        _factory = new InsysApiFactory(_db, new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoff"] = "00:00:00" }, time: WindowClock.Time);
        _maker = _factory.ClientFor(Tenant, "health-administrator");
        _checker = _factory.ClientFor(Tenant, "health-manager");
        foreach (var period in Enumerable.Range(-6, 7).Select(m => month.AddMonths(m)))
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
        // BR-067: schemes are listed, by number and by sponsor.
        var schemes = await Get($"/api/v1/health/schemes?schemeNumber={schemeNumber}");
        DocumentedSchema.ResponseProblems(schemes, "core", "HealthListSchemes").ShouldBeEmpty();
        schemes.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(scheme);
        (await Get("/api/v1/health/schemes?sponsorCode=S-ACME")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ShouldContain(scheme);
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

        // BR-068, BR-070: the membership shows its lifecycle events, payer, billing account and currency.
        var history = await Get($"/api/v1/health/memberships/{membership}");
        DocumentedSchema.ResponseProblems(history, "core", "HealthGetMembership").ShouldBeEmpty();
        history.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ShouldBe(["ENROL", "ADD_DEPENDANT"]);
        (history.GetProperty("payerPartyId").GetGuid(), history.GetProperty("currency").GetString()).ShouldBe((principal, "ZAR"));
        history.GetProperty("billingAccountRef").ValueKind.ShouldBe(JsonValueKind.String);
        (await Get($"/api/v1/health/memberships/{membership}/accumulators")).EnumerateArray().ShouldAllBe(a => a.GetProperty("currency").GetString() == "ZAR");

        // CheckEligibility → EligibilityDecisionRecorded (HLT-003).
        var number = member.GetProperty("memberNumber").GetString()!;
        (await Eligibility(number, "00")).ShouldBe("E00");
        (await Eligibility(number, "01")).ShouldBe("E00");
        (await Eligibility(number, "07")).ShouldBe("E02");
        (await Eligibility(number, "00", Today.AddMonths(-6).AddDays(10))).ShouldBe("E03");
        (await Eligibility(number, "00", Today.AddMonths(-7))).ShouldBe("E01");

        // V0094: a general consultation (needs no authorisation) is checked above; a service the tenant doesn't offer is refused.
        var unoffered = await _maker.PostAsJsonAsync("/api/v1/health/eligibility",
            new { memberNumber = number, dependantSuffix = "00", providerId = "PR-1", serviceType = "DENTAL" });
        unoffered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await unoffered.Content.ReadAsStringAsync());
        (await unoffered.Code()).ShouldBe("HEALTH_CODE_NOT_IN_FORCE");

        // BR-069: a membership is found by its number in the body; BR-072: its recorded eligibility decisions are read the same way.
        var found = await (await _maker.PostAsJsonAsync("/api/v1/health/memberships/search", new { memberNumber = number })).Json();
        DocumentedSchema.ResponseProblems(found, "core", "HealthSearchMemberships").ShouldBeEmpty();
        found.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(membership);
        var decisions = await (await _maker.PostAsJsonAsync("/api/v1/health/eligibility-decisions/search", new { memberNumber = number })).Json();
        DocumentedSchema.ResponseProblems(decisions, "core", "HealthListEligibilityDecisions").ShouldBeEmpty();
        decisions.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("responseCode").GetString()).ShouldBe(["E00", "E00", "E02", "E03", "E01"]);
        (await (await _maker.PostAsJsonAsync("/api/v1/health/eligibility-decisions/search", new { memberNumber = number, dependantSuffix = "07" })).Json())
            .GetProperty("items").EnumerateArray().Single().GetProperty("responseCode").GetString().ShouldBe("E02");

        // Compensation: a rejected authorisation reserves nothing.
        var rejected = await Authorisation(membership, principal);
        (await _maker.PostAsync($"/api/v1/health/preauthorisations/{rejected}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // BR-073: a reason longer than INSYS stores is a 422 field error, not a 500.
        var tooLong = await _maker.PostAsJsonAsync($"/api/v1/health/preauthorisations/{rejected}/reject", new { reason = new string('X', 31) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await tooLong.Content.ReadAsStringAsync());
        (await tooLong.Json()).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("reason must be at most 30 characters.");
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

        // BR-068, BR-072: the authorisation names its clinical reviewer and decider, in the membership's currency; its diagnosis codes
        // come only from the clinical read, to a holder of the clinical review permission (HLT-008).
        var authorised = await Get($"/api/v1/health/preauthorisations/{preauth}");
        var makerId = await UserId("health-administrator");
        (authorised.GetProperty("clinicalReviewer").GetGuid(), authorised.GetProperty("decidedBy").GetGuid(), authorised.GetProperty("currency").GetString())
            .ShouldBe((makerId, makerId, "ZAR"));
        authorised.TryGetProperty("diagnosisCodes", out _).ShouldBeFalse();
        var clinicalPath = $"/api/v1/health/preauthorisations/{preauth}/clinical";
        var clinical = await Get($"{clinicalPath}?purpose=Clinical%20review%20of%20the%20request");
        clinical.GetProperty("diagnosisCodes").EnumerateArray().Select(d => d.GetString()).ShouldNotBeEmpty();
        await _db.UserWithPermissions(Tenant, "no-clinical", HealthPermissions.PreauthRead);
        var notClinical = _factory.ClientFor(Tenant, "no-clinical");
        (await notClinical.GetAsync($"/api/v1/health/preauthorisations/{preauth}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await notClinical.GetAsync($"{clinicalPath}?purpose=curious")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // HLT-008: every clinical read leaves exactly one audit entry (who, when, which preauthorisation, purpose); a refused one is DENIED.
        (await (await _maker.GetAsync(clinicalPath)).Code()).ShouldBe("HEALTH_PURPOSE_REQUIRED");
        var unknown = Guid.CreateVersion7();
        (await _maker.GetAsync($"/api/v1/health/preauthorisations/{unknown}/clinical?purpose=review")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DrainOutbox();
        // One row per read, in order: outcome, who, purpose, why refused.
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(concat_ws('|', outcome, actor_id, coalesce(details->>'purpose', '-'), coalesce(details->>'refused', '-')), ',' ORDER BY occurred_at)
            FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ReadClinical' AND target_id = @p AND occurred_at IS NOT NULL
            """, ("t", Tenant), ("p", preauth.ToString())))
            .ShouldBe($"SUCCESS|{makerId}|Clinical review of the request|-,DENIED|{makerId}|-|PURPOSE_REQUIRED");
        (await _db.ScalarAsOwner<string>("SELECT string_agg(outcome || ':' || (details->>'refused'), ',') FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ReadClinical' AND target_id = @p",
            ("t", Tenant), ("p", unknown.ToString()))).ShouldBe("DENIED:NOT_FOUND");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'AccessDenied' AND outcome = 'DENIED' AND details->>'path' = @e",
            ("t", Tenant), ("e", $"GET {clinicalPath}"))).ShouldBe(1L);

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
        // BR-079: the case says when it was opened, and is not concluded yet.
        var caseId = (await Get($"/api/v1/health/utilisation-cases?claimId={claim}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var openCase = await Get($"/api/v1/health/utilisation-cases/{caseId}");
        (openCase.GetProperty("openedAt").ValueKind, openCase.GetProperty("concludedAt").ValueKind).ShouldBe((JsonValueKind.String, JsonValueKind.Null));

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
        var approvedClaim = await Get($"/api/v1/health/claims/{claim}");
        (approvedClaim.GetProperty("approvedBy").GetGuid(), approvedClaim.GetProperty("approvedAt").ValueKind).ShouldBe((makerId, JsonValueKind.String), "BR-079");
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
        (await _maker.SimulatorCallback(_factory, _db, payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        // BR-079: the run names its approval, approver and release time, and each remittance its advice document and payment.
        var paidRun = await Get($"/api/v1/health/payment-runs/{run}");
        paidRun.GetProperty("status").GetString().ShouldBe("Reconciled");
        DocumentedSchema.ResponseProblems(paidRun, "core", "HealthGetRun").ShouldBeEmpty();
        paidRun.GetProperty("approvedBy").GetGuid().ShouldBe(await UserId("health-manager"));
        (paidRun.GetProperty("approvalId").ValueKind, paidRun.GetProperty("releasedAt").ValueKind).ShouldBe((JsonValueKind.String, JsonValueKind.String));
        // Each remittance is paid on its own: its payment, and through it the payout batch, is the remittance's paymentId.
        paidRun.GetProperty("remittances")[0].GetProperty("paymentId").GetGuid().ShouldBe(payment);
        paidRun.GetProperty("remittances")[0].GetProperty("documentId").GetGuid().ShouldBe(advice);

        // The remittance's payment follows its claims' branch too.
        var byRemittance = $"/api/v1/payments/payments?businessRefType={HealthReferences.RemittanceRefType}&businessRefId={remittance}";
        (await (await branchB.GetAsync(byRemittance)).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await (await north.GetAsync(byRemittance)).Json()).GetProperty("items").GetArrayLength().ShouldBe(1);

        // Compensation: a reversed claim gives its benefit use back.
        await Seed("health.cfg_provider_recovery_settings", "EFFECTIVE", "DEFAULT", ("setting_code", "DEFAULT"), ("medical_scheme", true), ("review_after_days", 90), ("invoice_collection_method", "EFT"));
        (await _maker.PostAsJsonAsync($"/api/v1/health/claims/{claim}/reverse", new { reason = "BILLED_IN_ERROR" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Accumulator(membership, "MEMBER")).Used.ShouldBe(0m, "the claim's use is given back");

        // The ledger follows, on every dated clock: claims paid (2100) nets to zero and the provider owes it (1300). The bank (1200) keeps its credit: no cash came back.
        await _factory.DrainOutbox();
        async Task<decimal> Net(string account) => await _db.ScalarAsOwner<decimal>(
            "SELECT coalesce(sum(debit - credit), 0) FROM finance.subledger_entry WHERE business_ref_type = 'MEDICAL_CLAIM' AND business_ref_id = @c AND account_code = @a", ("c", claim), ("a", account));
        var paidAmount = await _db.ScalarAsOwner<decimal>("SELECT total_paid FROM health.medical_claim WHERE id = @c", ("c", claim));
        paidAmount.ShouldBeGreaterThan(0m);
        ((await Net("2100"), await Net("1300"), await Net("1200"))).ShouldBe((0m, paidAmount, -paidAmount));

        // 5a.3: within validity and used by nothing else, the authorisation is restored with what the claim consumed.
        var restored = await Get($"/api/v1/health/preauthorisations/{preauth}");
        (restored.GetProperty("status").GetString(), restored.GetProperty("lines")[0].GetProperty("consumedAmount").GetDecimal(), restored.GetProperty("lines")[0].GetProperty("reservedAmount").GetDecimal())
            .ShouldBe(("Approved", 0m, restored.GetProperty("lines")[0].GetProperty("authorisedAmount").GetDecimal()));

        // The reversal raises a recovery: a medical scheme's provider is offset by default (OQ-139, addition 1).
        var recovery = (await Get("/api/v1/health/provider-recoveries?providerId=PR-1")).GetProperty("items").EnumerateArray().Single();
        var recoveryId = recovery.GetProperty("id").GetGuid();
        (recovery.GetProperty("basis").GetString(), recovery.GetProperty("status").GetString(), recovery.GetProperty("balance").GetDecimal()).ShouldBe(("OFFSET", "OPEN", paidAmount));
        DocumentedSchema.ResponseProblems(recovery, "core", "HealthGetRecovery").ShouldBeEmpty();

        // The provider reprocesses: the new claim names the reversed one, shown both ways.
        var replacement = await Created("/api/v1/health/claims", new
        {
            source = "PROVIDER", providerId = "PR-1", membershipId = membership, patientPartyId = principal, serviceDate = Today, formatCode = "API", currency = "ZAR", replacesClaimId = claim,
            lines = new[] { new { tariffCode = "0300", diagnosisCode = "K35", quantity = 1, claimedAmount = 1_200m } },
        });
        (await Get($"/api/v1/health/claims/{replacement}")).GetProperty("replacesClaimId").GetGuid().ShouldBe(claim);
        (await Get($"/api/v1/health/claims/{claim}")).GetProperty("replacedByClaimId").GetGuid().ShouldBe(replacement);
        (await _maker.PostAsync($"/api/v1/health/claims/{replacement}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsync($"/api/v1/health/claims/{replacement}/adjudicate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var pendedReplacement = await Get($"/api/v1/health/claims/{replacement}");
        pendedReplacement.GetProperty("status").GetString().ShouldBe("Adjudicated", pendedReplacement.GetRawText());
        (await _maker.PostAsync($"/api/v1/health/claims/{replacement}/approve", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        // Gap 2: the next run deducts the recovery. Its total is the sum of the nets, and a net of nil is an advice with no payment.
        var nextRun = await Created("/api/v1/health/payment-runs", new { runCode = "PRV-WEEKLY", currency = "ZAR" });
        (await _maker.PostAsync($"/api/v1/health/payment-runs/{nextRun}/cut-off", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var nextCalculated = await (await _maker.PostAsync($"/api/v1/health/payment-runs/{nextRun}/calculate", null)).Json();
        var nextRemittance = nextCalculated.GetProperty("remittances").EnumerateArray().Single();
        (nextRemittance.GetProperty("grossAmount").GetDecimal(), nextRemittance.GetProperty("offsetAmount").GetDecimal(), nextRemittance.GetProperty("amount").GetDecimal(), nextCalculated.GetProperty("totalAmount").GetDecimal())
            .ShouldBe((paidAmount, paidAmount, 0m, 0m));
        await _factory.DrainOutbox();
        await Decide(HealthApprovals.RunBusinessRefType, nextRun);
        (await _maker.PostAsync($"/api/v1/health/payment-runs/{nextRun}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        await _factory.DrainOutbox();
        (await Get($"/api/v1/health/payment-runs/{nextRun}")).GetProperty("status").GetString().ShouldBe("Reconciled", "nothing was to pay");
        var nextRemittanceId = nextRemittance.GetProperty("id").GetGuid();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE business_ref_id = @r", ("r", nextRemittanceId))).ShouldBe(0L);
        (await _db.ScalarAsOwner<Guid?>("SELECT document_id FROM health.remittance_advice WHERE id = @r", ("r", nextRemittanceId))).ShouldNotBeNull("the provider still gets its advice");

        // The remittance shows the original, the reversal, the new claim and the net.
        var detail = await Get($"/api/v1/health/remittances/{nextRemittanceId}");
        DocumentedSchema.ResponseProblems(detail, "core", "HealthGetRemittance").ShouldBeEmpty();
        var originalNumber = (await Get($"/api/v1/health/claims/{claim}")).GetProperty("claimNumber").GetString();
        var replacementNumber = pendedReplacement.GetProperty("claimNumber").GetString();
        (detail.GetProperty("netAmount").GetDecimal(), detail.GetProperty("paymentDue").GetBoolean()).ShouldBe((0m, false));
        (detail.GetProperty("claims")[0].GetProperty("claimNumber").GetString(), detail.GetProperty("claims")[0].GetProperty("replacesClaimNumber").GetString()).ShouldBe((replacementNumber, originalNumber));
        var offsetLine = detail.GetProperty("offsets")[0];
        (offsetLine.GetProperty("originalClaimNumber").GetString(), offsetLine.GetProperty("newClaimNumber").GetString(), offsetLine.GetProperty("amount").GetDecimal()).ShouldBe((originalNumber, replacementNumber, paidAmount));
        offsetLine.GetProperty("reversedOn").ValueKind.ShouldBe(JsonValueKind.String);

        // Recovered; the ledger balances: the receivable is nil and the bank paid only what really left it.
        (await Get($"/api/v1/health/provider-recoveries/{recoveryId}")).GetProperty("status").GetString().ShouldBe("RECOVERED");
        async Task<decimal> RecoveryNet(string account) => await _db.ScalarAsOwner<decimal>(
            "SELECT coalesce(sum(debit - credit), 0) FROM finance.subledger_entry WHERE business_ref_type = 'HEALTH_RECOVERY' AND business_ref_id = @c AND account_code = @a", ("c", recoveryId), ("a", account));
        ((await RecoveryNet("1300"), await RecoveryNet("1200"))).ShouldBe((-paidAmount, paidAmount));

        // A provider's credit note needs a second person, and then raises a recovery.
        var note = await Created("/api/v1/health/provider-credit-notes", new { providerId = "PR-1", providerReference = "CN-1", claimId = replacement, amount = 100m, currency = "ZAR", reason = "Billed one visit too many" });
        await _factory.DrainOutbox();
        (await Get("/api/v1/health/provider-recoveries?providerId=PR-1")).GetProperty("items").GetArrayLength().ShouldBe(1, "nothing is owed until it is approved");
        await Decide(HealthApprovals.CreditNoteBusinessRefType, note);
        await _factory.DrainOutbox();
        var noteRecovery = (await Get($"/api/v1/health/provider-credit-notes/{note}")).GetProperty("recoveryId").GetGuid();
        (await Get($"/api/v1/health/provider-recoveries/{noteRecovery}")).GetProperty("balance").GetDecimal().ShouldBe(100m);
        await _factory.DrainOutbox();
        (await RecoveryNetOf(noteRecovery, "1300")).ShouldBe(100m);

        // OQ-146: held for a dispute, it is not deducted; the case closing releases it.
        var holdCase = Guid.CreateVersion7();
        var noteUrl = $"/api/v1/health/provider-recoveries/{noteRecovery}";
        (await _maker.Send(HttpMethod.Post, $"{noteUrl}/hold", new { caseId = holdCase }, ifMatch: (await _maker.GetAsync(noteUrl)).ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.Send(HttpMethod.Post, $"{noteUrl}/invoice", ifMatch: (await _maker.GetAsync(noteUrl)).ETag())).StatusCode.ShouldBe(HttpStatusCode.Conflict, "not while it is disputed");

        // A write-off needs a reason and a second person; it is never simply dropped (addition 3).
        var writeOff = await _maker.Send(HttpMethod.Post, $"{noteUrl}/write-off", new { reason = "" }, ifMatch: (await _maker.GetAsync(noteUrl)).ETag());
        writeOff.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _maker.Send(HttpMethod.Post, $"{noteUrl}/write-off", new { reason = "Provider liquidated" }, ifMatch: (await _maker.GetAsync(noteUrl)).ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Get(noteUrl)).GetProperty("balance").GetDecimal().ShouldBe(100m, "still owed until a second person approves");
        await _factory.DrainOutbox();
        await Decide(HealthApprovals.RecoveryBusinessRefType, noteRecovery);
        await _factory.DrainOutbox();
        var writtenOff = await Get(noteUrl);
        (writtenOff.GetProperty("status").GetString(), writtenOff.GetProperty("balance").GetDecimal()).ShouldBe(("WRITTEN_OFF", 0m));
        await _factory.DrainOutbox();
        (await RecoveryNetOf(noteRecovery, "1300")).ShouldBe(0m);

        async Task<decimal> RecoveryNetOf(Guid id, string account) => await _db.ScalarAsOwner<decimal>(
            "SELECT coalesce(sum(debit - credit), 0) FROM finance.subledger_entry WHERE business_ref_type = 'HEALTH_RECOVERY' AND business_ref_id = @c AND account_code = @a", ("c", id), ("a", account));

        // Compensation: an authorisation that expires releases its reservation.
        var expiring = await Authorisation(membership, principal);
        var expiringLine = (await Get($"/api/v1/health/preauthorisations/{expiring}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await _maker.PostAsync($"/api/v1/health/preauthorisations/{expiring}/review", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/health/preauthorisations/{expiring}/approve",
            new { lines = new[] { new { lineId = expiringLine, authorisedAmount = 600m } }, validFrom = Today.AddDays(-40) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Accumulator(membership, "MEMBER", Today.AddDays(-40))).ShouldBe((0m, 600m), "reserved in the benefit period it is valid from");
        await _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await Get($"/api/v1/health/preauthorisations/{expiring}")).GetProperty("status").GetString().ShouldBe("Expired");
        (await Accumulator(membership, "MEMBER", Today.AddDays(-40))).ShouldBe((0m, 0m), "the expired reservation is released");

        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.outbox_event WHERE payload::text LIKE '%K35%'")).ShouldBe(0L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message")).ShouldBe(0L);
    }

    /// <summary>
    /// A reversal posts only the amount the event names: paid 1 000, reversed 400, claims paid (2100) nets to 600 and 1300 shows
    /// 400; the bank (1200) keeps its credit. Finance's posting only: the API reverses a whole claim today.
    /// </summary>
    [Fact]
    public async Task AReversalOfPartOfAPaidClaim_PostsOnlyTheReversedAmount()
    {
        var claim = Guid.CreateVersion7();
        var bus = _factory.Services.GetRequiredService<Insys.BuildingBlocks.Application.Messaging.IEventBus>();
        var now = _factory.Clock.GetUtcNow();
        Insys.BuildingBlocks.Contracts.EventEnvelope Envelope<T>(T e, string type) => new(Guid.CreateVersion7(), Tenant, type, 1, now, "MedicalClaim", claim, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(e, JsonSerializerOptions.Web));
        await bus.Deliver(Envelope(new MedicalClaimSettled(claim, "MC-PART", "PR-1", Guid.CreateVersion7(), 1_000m, "ZAR", Today), "health.MedicalClaimSettled"), CancellationToken.None);
        await bus.Deliver(Envelope(new MedicalClaimReversed(claim, "MC-PART", "PR-1", 400m, "ZAR", "BILLED_IN_ERROR"), "health.MedicalClaimReversed"), CancellationToken.None);

        async Task<decimal> Net(string account) => await _db.ScalarAsOwner<decimal>(
            "SELECT coalesce(sum(debit - credit), 0) FROM finance.subledger_entry WHERE business_ref_type = 'MEDICAL_CLAIM' AND business_ref_id = @c AND account_code = @a", ("c", claim), ("a", account));
        ((await Net("2100"), await Net("1300"), await Net("1200"))).ShouldBe((600m, 400m, -1_000m));
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

    /// <summary>
    /// A reversal gives back what the claim consumed: the deductible and co-payment too, on the patient's accumulators and the
    /// family's, never another dependant's. A reprocessed claim for the same service then takes them again, and is no duplicate.
    /// </summary>
    [Fact]
    public async Task AReversedClaim_GivesBackDeductibleAndCopayment_OnItsOwnAndTheFamilysAccumulators_AndAReprocessedClaimTakesThemAgain()
    {
        await Seed("health.cfg_co_payment_and_deductible_rules", "EFFECTIVE", "DED-GP", ("rule_code", "DED-GP"), ("benefit", "GP_VISITS"), ("type", "AMOUNT"),
            ("value", 100m), ("network_status", "IN_NETWORK"), ("rule_kind", "DEDUCTIBLE"), ("cap_basis", "PER_LINE"));
        await Seed("health.cfg_co_payment_and_deductible_rules", "EFFECTIVE", "CP-GP", ("rule_code", "CP-GP"), ("benefit", "GP_VISITS"), ("type", "PERCENT"),
            ("value", 10m), ("network_status", "IN_NETWORK"), ("cap", 40m), ("rule_kind", "CO_PAYMENT"), ("cap_basis", "PER_YEAR"));
        await Provider();
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Giveback");
        var spouse = await _maker.RegisterPerson("GivebackSpouse");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        (await _maker.Send(HttpMethod.Post, $"/api/v1/health/memberships/{membership}/dependants",
            new { partyId = spouse, dependantType = "SPOUSE", student = false, startDate = Today.AddMonths(-6) }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        async Task<Guid> Visit(Guid patient, int daysAgo)
        {
            var claim = await Created("/api/v1/health/claims", new
            {
                source = "PROVIDER", providerId = "PR-1", membershipId = membership, patientPartyId = patient, serviceDate = Today.AddDays(-daysAgo),
                formatCode = "API", currency = "ZAR", lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 500m } },
            });
            (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var adjudicated = await _maker.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null);
            adjudicated.StatusCode.ShouldBe(HttpStatusCode.OK, await adjudicated.Content.ReadAsStringAsync());
            (await adjudicated.Json()).GetProperty("status").GetString().ShouldBe("Adjudicated", "neither a duplicate nor a repeat");
            return claim;
        }

        Task<string?> Member(Guid patient) => _db.ScalarAsOwner<string>("""
            SELECT used_amount::numeric(18,2) || '/' || deductible_met::numeric(18,2) || '/' || copay_taken::numeric(18,2) FROM health.benefit_accumulator
            WHERE membership_id = @m AND benefit_category = 'GP_VISITS' AND scope = 'MEMBER' AND beneficiary_party_id = @p
            """, ("m", membership), ("p", patient));
        Task<decimal> Family() => _db.ScalarAsOwner<decimal>(
            "SELECT used_amount FROM health.benefit_accumulator WHERE membership_id = @m AND benefit_category = 'GP_VISITS' AND scope = 'FAMILY'", ("m", membership));

        // 500 less the deductible of 100, less 10% of the 400 (40, the year's cap): 360 paid.
        await Visit(principal, 1);
        var original = await Visit(spouse, 1);
        (await Member(principal)).ShouldBe("360.00/100.00/40.00");
        (await Member(spouse)).ShouldBe("360.00/100.00/40.00");
        (await Family()).ShouldBe(720m);

        // Paid (the run is covered by the flow above), then reversed.
        await _db.ExecuteAsOwner("UPDATE health.medical_claim SET status = 'PAID' WHERE id = @c", ("c", original));
        var reversed = await _maker.PostAsJsonAsync($"/api/v1/health/claims/{original}/reverse", new { reason = "BILLED_IN_ERROR" });
        reversed.StatusCode.ShouldBe(HttpStatusCode.OK, await reversed.Content.ReadAsStringAsync());
        (await Member(spouse)).ShouldBe("0.00/0.00/0.00", "the patient's own are given back");
        (await Member(principal)).ShouldBe("360.00/100.00/40.00", "another person's individual limit is not touched");
        (await Family()).ShouldBe(360m, "the family limit is shared, and given back");

        // Reprocessed: the same service, on a new claim, takes them again in full.
        var reprocessed = await Visit(spouse, 1);
        (await _db.ScalarAsOwner<decimal>("SELECT total_paid FROM health.medical_claim WHERE id = @c", ("c", reprocessed))).ShouldBe(360m);
        (await Member(spouse)).ShouldBe("360.00/100.00/40.00");
        (await Member(principal)).ShouldBe("360.00/100.00/40.00");
        (await Family()).ShouldBe(720m);
    }

    /// <summary>
    /// F034 to F037, OQ-143: a complex case's clinical detail is for its current manager or the clinical function, with a purpose,
    /// and every read is audited. Reassigning ends the previous manager's access. A plan's change keeps the last version; an
    /// overdue activity raises one task, for the manager, carrying no clinical text.
    /// </summary>
    [Fact]
    public async Task AComplexCasesClinicalDetail_IsForItsCurrentManager_AndReassigningEndsTheLastManagersAccess_OQ143_F034_F037()
    {
        string[] managerPermissions = [HealthPermissions.ComplexCaseRead, HealthPermissions.ComplexCaseUpdate, HealthPermissions.ComplexCaseManage];
        var firstId = await _db.UserWithPermissions(Tenant, "case-manager-1", managerPermissions);
        var secondId = await _db.UserWithPermissions(Tenant, "case-manager-2", managerPermissions);
        var outsiderId = await _db.UserWithPermissions(Tenant, "case-reader", HealthPermissions.ComplexCaseRead, HealthPermissions.ComplexCaseUpdate);
        await _db.UserWithPermissions(Tenant, "medical-adviser", HealthPermissions.ComplexCaseRead, HealthPermissions.ClinicalRead);
        await _db.AdoptFunctionCatalogue();
        var first = _factory.ClientFor(Tenant, "case-manager-1");
        var second = _factory.ClientFor(Tenant, "case-manager-2");
        var outsider = _factory.ClientFor(Tenant, "case-reader");
        var adviser = _factory.ClientFor(Tenant, "medical-adviser");
        const string Secret = "Stage 3 kidney disease";

        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Complex");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await _factory.DrainOutbox();
        var id = await Created("/api/v1/health/complex-cases", new { membershipId = membership, patientPartyId = principal, openedFrom = "MEMBERSHIP", conditionCategory = Secret });
        var url = $"/api/v1/health/complex-cases/{id}";
        var clinical = $"{url}/clinical?purpose=Care%20review";
        (await Get(url)).GetRawText().ShouldNotContain(Secret, Case.Sensitive, "a case's view holds no clinical detail");

        // Only someone holding the case manager function may be assigned.
        (await (await _maker.Send(HttpMethod.Post, $"{url}/case-manager", new { caseManagerId = outsiderId }, ifMatch: (await _maker.GetAsync(url)).ETag())).Code()).ShouldBe("NOT_A_CASE_MANAGER");
        var assigned = await _maker.Send(HttpMethod.Post, $"{url}/case-manager", new { caseManagerId = firstId }, ifMatch: (await _maker.GetAsync(url)).ETag());
        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        (await assigned.Json()).GetProperty("status").GetString().ShouldBe("ACTIVE");

        // The manager reads, with a purpose; someone else with case access does not, and that is audited too.
        (await (await first.GetAsync($"{url}/clinical")).Code()).ShouldBe("HEALTH_PURPOSE_REQUIRED");
        var read = await first.GetAsync(clinical);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        (await read.Json()).GetProperty("conditionCategory").GetString().ShouldBe(Secret);
        (await (await outsider.GetAsync(clinical)).Code()).ShouldBe("NOT_THE_CASE_MANAGER");
        (await adviser.GetAsync(clinical)).StatusCode.ShouldBe(HttpStatusCode.OK, "the clinical function");
        (await outsider.Send(HttpMethod.Post, $"{url}/notes", new { contactType = "INTERNAL", note = "Should not land" }, ifMatch: (await outsider.GetAsync(url)).ETag())).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden, "writing it needs the same access");

        // A plan, with an activity past its date; a note shared with a provider names the consent.
        var plan = await first.Send(HttpMethod.Post, $"{url}/care-plan", new
        {
            items = new object[]
            {
                new { kind = "GOAL", description = "Stable renal function", responsible = (string?)null, dueOn = (DateOnly?)null },
                new { kind = "ACTIVITY", description = "Nephrology visit", responsible = "PR-1", dueOn = (DateOnly?)Today.AddDays(-1) },
            },
        }, ifMatch: (await first.GetAsync(url)).ETag());
        plan.StatusCode.ShouldBe(HttpStatusCode.OK, await plan.Content.ReadAsStringAsync());
        (await (await first.Send(HttpMethod.Post, $"{url}/notes", new { contactType = "PROVIDER", note = "Results shared", sharedWithProviderId = "PR-1" },
            ifMatch: (await first.GetAsync(url)).ETag())).Code()).ShouldBe("CARE_CONSENT_REQUIRED");
        (await first.Send(HttpMethod.Post, $"{url}/notes", new { contactType = "PROVIDER", note = "Results shared", sharedWithProviderId = "PR-1", consentReference = "CONSENT-1" },
            ifMatch: (await first.GetAsync(url)).ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The daily job raises one task, for the manager, however often it runs; it carries no clinical text.
        var jobs = _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'HEALTH_CARE_ACTIVITY_FOLLOW_UP' AND t.assignee_id = @m
            """, ("m", firstId))).ShouldBe(1L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.outbox_event WHERE payload::text ILIKE '%nephrology%' OR payload::text ILIKE '%kidney%'")).ShouldBe(0L);

        // Reassigned, with a reason: the first manager's access ends at once, the second's starts.
        var etag = (await _maker.GetAsync(url)).ETag();
        (await _maker.Send(HttpMethod.Post, $"{url}/case-manager", new { caseManagerId = secondId }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "needs a reason");
        var moved = await _maker.Send(HttpMethod.Post, $"{url}/case-manager", new { caseManagerId = secondId, reason = "Leave" }, ifMatch: etag);
        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        (await (await first.GetAsync(clinical)).Code()).ShouldBe("NOT_THE_CASE_MANAGER", "addition 2: reassigning ends the previous manager's access");
        (await first.Send(HttpMethod.Post, $"{url}/notes", new { contactType = "INTERNAL", note = "Late note" }, ifMatch: (await first.GetAsync(url)).ETag())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await second.GetAsync(clinical)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var history = (await moved.Json()).GetProperty("assignments");
        history.GetArrayLength().ShouldBe(2);
        history[0].GetProperty("endedAt").ValueKind.ShouldBe(JsonValueKind.String);

        // A change keeps the last version.
        (await second.Send(HttpMethod.Post, $"{url}/care-plan", new { items = new object[] { new { kind = "GOAL", description = "New goal" } } }, ifMatch: (await second.GetAsync(url)).ETag())).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "a change needs a reason");
        (await second.Send(HttpMethod.Post, $"{url}/care-plan", new { items = new object[] { new { kind = "GOAL", description = "New goal" } }, reason = "Reassessed" }, ifMatch: (await second.GetAsync(url)).ETag())).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var plans = (await (await second.GetAsync(clinical)).Json()).GetProperty("plans");
        (plans.GetArrayLength(), plans[0].GetProperty("isCurrent").GetBoolean(), plans[1].GetProperty("isCurrent").GetBoolean()).ShouldBe((2, false, true));

        // Every clinical read is in the audit trail, with its purpose and no clinical content.
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'ReadClinical' AND target_id = @e", ("e", id.ToString()))).ShouldBeGreaterThanOrEqualTo(5L);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE details::text ILIKE '%kidney%'")).ShouldBe(0L);

        // Never closed by itself; closing needs a reason.
        (await (await _maker.PostAsJsonAsync($"{url}/close", new { reason = "" })).Code()).ShouldBe("VALIDATION_FAILED");
    }

    /// <summary>F038, OQ-144: a referral is recorded, decided and used up, its reason read only through the clinical route; past its date it expires. It affects no claim.</summary>
    [Fact]
    public async Task AClinicalReferral_IsDecidedAndUsedUp_ItsReasonIsClinical_AndItExpires_F038_OQ144()
    {
        const string Secret = "Suspected renal failure";
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Referred");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await _factory.DrainOutbox();
        Task<Guid> Refer(DateOnly to) => Created("/api/v1/health/referrals", new
        {
            membershipId = membership, patientPartyId = principal, referringProviderId = "PR-1", referredSpecialty = "NEPHROLOGY", clinicalReason = Secret, validFrom = Today.AddDays(-5), validTo = to, visitsAllowed = 2,
        });
        var id = await Refer(Today.AddDays(30));
        var url = $"/api/v1/health/referrals/{id}";
        (await Get(url)).GetRawText().ShouldNotContain(Secret, Case.Sensitive);
        (await _maker.GetAsync($"{url}/clinical?purpose=Review")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the clinical function only");

        (await (await _maker.Send(HttpMethod.Post, $"{url}/visits", ifMatch: (await _maker.GetAsync(url)).ETag())).Code()).ShouldBe("INVALID_STATE_TRANSITION", "not yet approved");
        (await _maker.Send(HttpMethod.Post, $"{url}/decide", new { approved = false }, ifMatch: (await _maker.GetAsync(url)).ETag())).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a decline needs a reason");
        (await _maker.Send(HttpMethod.Post, $"{url}/decide", new { approved = true }, ifMatch: (await _maker.GetAsync(url)).ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        for (var visit = 0; visit < 2; visit++)
        {
            (await _maker.Send(HttpMethod.Post, $"{url}/visits", ifMatch: (await _maker.GetAsync(url)).ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await Get(url)).GetProperty("status").GetString().ShouldBe("USED");

        var late = await Refer(Today.AddDays(-1));
        await _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>().Run(CancellationToken.None);
        (await Get($"/api/v1/health/referrals/{late}")).GetProperty("status").GetString().ShouldBe("EXPIRED");
    }

    /// <summary>
    /// F038 (Ade, 30 October 2026): on the portal, a referral's reason is for the provider who wrote it, and for the one it names only
    /// with the member's consent; a third gets not found. Each needs a purpose, and every read and refusal is audited.
    /// </summary>
    [Fact]
    public async Task AReferralsReason_IsForItsWriter_ForItsReceiverWithConsent_AndNotForAThirdProvider_F038()
    {
        const string Secret = "Suspected renal failure";
        foreach (var code in new[] { "PR-1", "PR-2", "PR-3" })
        {
            await _db.ExecuteAsOwner("""
                INSERT INTO health.mst_provider_register (tenant_id, code, version, effective_from, record_status, created_by, change_reason, provider_id, name,
                                                          registration_numbers, locations, status, provider_type)
                VALUES (@t, @p, 1, @from, 'ACTIVE', 'seed', 'seed', @p, @p, '{"practice":"P"}', '[]', 'ACTIVE', 'SPEC')
                ON CONFLICT DO NOTHING
                """, ("t", Tenant), ("p", code), ("from", Today.AddYears(-2)));
            var user = await _db.UserWithPermissions(Tenant, $"referral-{code}", HealthPermissions.ReferralRead, HealthPermissions.ReferralCreate);
            await _db.ExecuteAsOwner("""
                INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, provider_id, status, requested_by, decided_by, decided_at)
                VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'PROVIDER', @v, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
                """, ("t", Tenant), ("u", user), ("v", code));
        }

        var writer = _factory.ClientFor(Tenant, "referral-PR-1", "provider-portal", multiFactor: true);
        var receiver = _factory.ClientFor(Tenant, "referral-PR-2", "provider-portal", multiFactor: true);
        var third = _factory.ClientFor(Tenant, "referral-PR-3", "provider-portal", multiFactor: true);

        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("ReferredPortal");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await _factory.DrainOutbox();
        var memberNumber = (await Get($"/api/v1/health/memberships/{membership}")).GetProperty("memberNumber").GetString();

        // With channels in force, the writer refers on its portal and staff record consent through the back office.
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-PRV", ("channel_code", "PRV"), ("audience", "PROVIDER"), ("name", "Provider portal"), ("client_ids", new[] { "provider-portal" }));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", ("channel_code", "STF"), ("audience", "STAFF"), ("name", "Back office"), ("client_ids", new[] { "insys-web" }));
        var writerRole = (await _db.ScalarAsOwner<string>("""
            SELECT a.role_code FROM iam.user_role_assignment a JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id WHERE u.external_subject = 'referral-PR-1'
            """))!;
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-PRV-R", ("channel_code", "PRV"), ("role_code", writerRole),
            ("transaction_type", HealthPermissions.ReferralCreate), ("allowed", true));
        var staffRoles = (await _db.ScalarAsOwner<string[]>("""
            SELECT array_agg(DISTINCT a.role_code) FROM iam.user_role_assignment a JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id WHERE u.external_subject = 'health-administrator'
            """))!;
        foreach (var (role, i) in staffRoles.Select((r, i) => (r, i)))
        {
            await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", $"CP-STF-{i}", ("channel_code", "STF"), ("role_code", role),
                ("transaction_type", HealthPermissions.ReferralConsent), ("allowed", true));
        }

        await _db.AdoptFunctionCatalogue();
        _maker = _factory.ClientFor(Tenant, "health-administrator", "insys-web");

        async Task<string> Refer(string? consent)
        {
            var referred = await writer.Send(HttpMethod.Post, "/api/v1/portal/health/referrals", new
            {
                memberNumber, referredProviderId = "PR-2", clinicalReason = Secret, validFrom = Today, validTo = Today.AddDays(30), visitsAllowed = 1, consentReference = consent,
            }, idempotent: true);
            referred.StatusCode.ShouldBe(HttpStatusCode.Created, await referred.Content.ReadAsStringAsync());
            var body = await referred.Json();
            body.GetRawText().ShouldNotContain(Secret, Case.Sensitive);
            body.GetProperty("referringProviderId").GetString().ShouldBe("PR-1", "as itself, whatever a request says");
            return $"/api/v1/portal/health/referrals/{body.GetProperty("id").GetGuid()}/clinical";
        }

        var withoutConsent = await Refer(null);
        var withConsent = await Refer("CONSENT-1");
        const string Purpose = "?purpose=Treatment";

        // The writer reads its own reason, with a purpose.
        (await (await writer.GetAsync(withoutConsent)).Code()).ShouldBe("HEALTH_PURPOSE_REQUIRED");
        (await (await writer.GetAsync(withoutConsent + Purpose)).Json()).GetProperty("clinicalReason").GetString().ShouldBe(Secret);

        // The receiver only when the referral names the member's consent.
        var refused = await receiver.GetAsync(withoutConsent + Purpose);
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Forbidden, "CARE_CONSENT_REQUIRED"));
        (await (await receiver.GetAsync(withConsent + Purpose)).Json()).GetProperty("clinicalReason").GetString().ShouldBe(Secret);

        // Consent given later opens it to the receiver at once; withdrawn, it is 403 from then on. Both need a reason, and both are audited.
        var referralUrl = withoutConsent.Replace("/portal/health/referrals/", "/health/referrals/").Replace("/clinical", "");
        (await _maker.Send(HttpMethod.Post, $"{referralUrl}/consent", new { consentReference = "CONSENT-2", reason = "" }, ifMatch: (await _maker.GetAsync(referralUrl)).ETag())).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "a reason is required");
        (await _maker.Send(HttpMethod.Post, $"{referralUrl}/consent", new { consentReference = "CONSENT-2", reason = "Member signed the form" }, ifMatch: (await _maker.GetAsync(referralUrl)).ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await receiver.GetAsync(withoutConsent + Purpose)).Json()).GetProperty("clinicalReason").GetString().ShouldBe(Secret);
        (await _maker.Send(HttpMethod.Post, $"{referralUrl}/consent/withdraw", new { reason = "" }, ifMatch: (await _maker.GetAsync(referralUrl)).ETag())).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "a reason is required");
        var withdrawn = await _maker.Send(HttpMethod.Post, $"{referralUrl}/consent/withdraw", new { reason = "Member withdrew by phone" }, ifMatch: (await _maker.GetAsync(referralUrl)).ETag());
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync());
        var after = await withdrawn.Json();
        (after.GetProperty("consentReference").ValueKind, after.GetProperty("consentWithdrawnAt").ValueKind).ShouldBe((JsonValueKind.Null, JsonValueKind.String));
        (await receiver.GetAsync(withoutConsent + Purpose)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "withdrawal ends it at once");
        (await writer.GetAsync(withoutConsent + Purpose)).StatusCode.ShouldBe(HttpStatusCode.OK, "the writer's access does not depend on consent");
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(operation || ':' || (details->>'reason'), ',' ORDER BY occurred_at) FROM audit.audit_event WHERE operation IN ('RecordReferralConsent', 'WithdrawReferralConsent')
            """)).ShouldBe("RecordReferralConsent:Member signed the form,WithdrawReferralConsent:Member withdrew by phone");

        // A third provider gets not found on both, and sees neither listed.
        (await third.GetAsync(withConsent + Purpose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await third.GetAsync(withoutConsent + Purpose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await third.GetAsync("/api/v1/portal/health/referrals")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await (await receiver.GetAsync("/api/v1/portal/health/referrals")).Json()).GetProperty("items").GetArrayLength().ShouldBe(2);

        // Every read and refusal is audited, without the reason.
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'ReadClinical' AND target_type = 'ClinicalReferral' AND outcome = 'DENIED'")).ShouldBe(5L, "no purpose, no consent, withdrawn, and the third provider's two");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'ReadClinical' AND target_type = 'ClinicalReferral' AND outcome <> 'DENIED'")).ShouldBe(4L, "the writer's two, and the receiver's with each consent");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE details::text ILIKE '%renal%'")).ShouldBe(0L);
    }

    /// <summary>
    /// F028 to F030 (Wave 5a batch 5a.1): last month's claims give a provider its measures. A breach asks a person to look and
    /// changes nothing about the provider; a month with too few claims is shown but not judged. A provider's statement is matched
    /// against its paid claims, and is reconciled once a person has resolved each exception with a reason.
    /// </summary>
    [Fact]
    public async Task AProvidersMonth_IsMeasuredAndABreachAsksAPerson_AndItsStatementIsMatchedAndReconciled_F028_F029_F030()
    {
        await Seed("health.cfg_provider_recovery_settings", "EFFECTIVE", "DEFAULT", ("setting_code", "DEFAULT"), ("medical_scheme", true), ("review_after_days", 90), ("invoice_collection_method", "EFT"));
        await Seed("health.cfg_provider_measures", "EFFECTIVE", "PM-REJECT", ("measure_code", "REJECTION_RATE"), ("threshold", 0.25m), ("direction", "ABOVE"), ("min_claims", 2));
        await Seed("health.cfg_provider_measures", "EFFECTIVE", "PM-DISPUTES", ("measure_code", "DISPUTES_PER_100"), ("threshold", 1m), ("direction", "ABOVE"), ("min_claims", 100));
        await Provider();
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var principal = await _maker.RegisterPerson("Measured");
        var membership = await Created("/api/v1/health/memberships", new
        {
            principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
            currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        });
        await _factory.DrainOutbox();
        async Task<Guid> Visit(int daysAgo) => await Created("/api/v1/health/claims", new
        {
            source = "PROVIDER", providerId = "PR-1", membershipId = membership, patientPartyId = principal, serviceDate = Today.AddDays(-daysAgo),
            formatCode = "API", currency = "ZAR", lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 500m } },
        });
        var paid = await Visit(1);
        var other = await Visit(2);
        var rejected = await Visit(3);
        (await _maker.PostAsync($"/api/v1/health/claims/{rejected}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _maker.PostAsJsonAsync($"/api/v1/health/claims/{rejected}/reject", new { reason = "NOT_COVERED" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Last month's, as the job reads them, and two of them paid.
        var month = Today.AddMonths(-1);
        var monthKey = $"{month:yyyy-MM}";
        await _db.ExecuteAsOwner("UPDATE health.medical_claim SET submitted_at = @at WHERE membership_id = @m", ("at", new DateTimeOffset(month.Year, month.Month, 15, 10, 0, 0, TimeSpan.Zero)), ("m", membership));
        await _db.ExecuteAsOwner("UPDATE health.medical_claim SET status = 'PAID', total_paid = 500 WHERE id IN (@a, @b)", ("a", paid), ("b", other));

        await _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var measures = (await Get($"/api/v1/health/providers/PR-1/measures?monthKey={monthKey}")).GetProperty("items").EnumerateArray().ToDictionary(m => m.GetProperty("measureCode").GetString()!);
        measures.Count.ShouldBe(6);
        (measures["CLAIMS_SUBMITTED"].GetProperty("value").GetDecimal(), measures["AVERAGE_PAID"].GetProperty("value").GetDecimal()).ShouldBe((3m, 500m));
        var rate = measures["REJECTION_RATE"];
        (rate.GetProperty("value").GetDecimal(), rate.GetProperty("judged").GetBoolean(), rate.GetProperty("breached").GetBoolean()).ShouldBe((0.3333m, true, true));
        (measures["DISPUTES_PER_100"].GetProperty("judged").GetBoolean(), measures["DISPUTES_PER_100"].GetProperty("breached").GetBoolean()).ShouldBe((false, false), "too few claims to judge");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'HEALTH_PROVIDER_MEASURE_REVIEW' AND business_ref_id = @r",
            ("r", rate.GetProperty("id").GetGuid()))).ShouldBe(1L, "a person is asked to look");

        // Run again: the month is measured once.
        await _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>().Run(CancellationToken.None);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM health.provider_measure_result WHERE provider_id = 'PR-1' AND month_key = @k", ("k", monthKey))).ShouldBe(6L);

        // A person's review, with a score and a reason.
        (await _maker.Send(HttpMethod.Post, "/api/v1/health/providers/PR-1/quality-reviews", new { score = 6, reason = "Too high" }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _maker.Send(HttpMethod.Post, "/api/v1/health/providers/PR-1/quality-reviews", new { score = 2, reason = "Late submissions" }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Get("/api/v1/health/providers/PR-1/quality-reviews")).GetProperty("items")[0].GetProperty("score").GetInt32().ShouldBe(2);

        // The statement: one claim agrees, one INSYS has no record of, and the other paid claim is left out.
        var paidNumber = (await Get($"/api/v1/health/claims/{paid}")).GetProperty("claimNumber").GetString();
        var otherNumber = (await Get($"/api/v1/health/claims/{other}")).GetProperty("claimNumber").GetString();
        // A third claim, paid and then reversed, is listed by the provider too: on our records, with its own reason.
        var reversedClaim = await Visit(4);
        await _db.ExecuteAsOwner("UPDATE health.medical_claim SET status = 'REVERSED', total_paid = 500, reversed_at = @at WHERE id = @c", ("at", _factory.Clock.GetUtcNow()), ("c", reversedClaim));
        var reversedNumber = (await Get($"/api/v1/health/claims/{reversedClaim}")).GetProperty("claimNumber").GetString();
        var csv = $"claim_number,amount\n{paidNumber},500.00\nMC-UNKNOWN,120.00\n{reversedNumber},500.00\n";
        var body = new { providerId = "PR-1", statementRef = "ST-1", periodFrom = Today.AddDays(-10), periodTo = Today, currency = "ZAR", csv };
        var imported = await _maker.Send(HttpMethod.Post, "/api/v1/health/provider-statements/import", body, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        var statement = await imported.Json();
        statement.GetProperty("status").GetString().ShouldBe("EXCEPTIONS");
        var lines = statement.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("reference").GetString()!, l => l);
        lines[paidNumber!].GetProperty("matchResult").GetString().ShouldBe("MATCHED");
        lines["MC-UNKNOWN"].GetProperty("matchResult").GetString().ShouldBe("NOT_ON_OUR_RECORDS");
        lines[otherNumber!].GetProperty("matchResult").GetString().ShouldBe("NOT_ON_STATEMENT");
        var onOurs = lines[reversedNumber!];
        (onOurs.GetProperty("matchResult").GetString(), onOurs.GetProperty("recoveryStatus").GetString(), onOurs.GetProperty("reversedOn").GetString(), onOurs.GetProperty("ourAmount").GetDecimal())
            .ShouldBe(("REVERSED_BY_US", "OWED", Today.ToString("yyyy-MM-dd"), 500m));
        (await (await _maker.Send(HttpMethod.Post, "/api/v1/health/provider-statements/import", body, idempotent: true)).Code()).ShouldBe("PROVIDER_STATEMENT_DUPLICATE");
        var id = statement.GetProperty("id").GetGuid();
        foreach (var reference in new[] { "MC-UNKNOWN", otherNumber!, reversedNumber! })
        {
            var current = await _maker.GetAsync($"/api/v1/health/provider-statements/{id}");
            var resolved = await _maker.Send(HttpMethod.Post, $"/api/v1/health/provider-statements/{id}/lines/{lines[reference].GetProperty("id").GetGuid()}/resolve",
                new { reason = "Agreed with the provider" }, ifMatch: current.ETag());
            resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());
        }

        (await Get($"/api/v1/health/provider-statements/{id}")).GetProperty("status").GetString().ShouldBe("RECONCILED");
        (await _db.ScalarAsOwner<decimal>("SELECT total_paid FROM health.medical_claim WHERE id = @c", ("c", other))).ShouldBe(500m, "reconciling changes no payment");
    }

    /// <summary>
    /// F006 (Wave 4 batch 4.1): contributions unpaid past their due date start grace. A claim for a service from then is held,
    /// not declined, and is adjudicated once they are paid. Still unpaid on the suspension day, the membership is suspended
    /// for arrears and the member told; its held claims are declined. When the notice runs out a person is asked whether to
    /// terminate: never automatically.
    /// </summary>
    [Fact]
    public async Task UnpaidContributions_HoldClaimsInGrace_ReleaseThemOncePaid_AndSuspendThenAskAPersonToTerminate_F006()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "HGR",
            (Insys.Communications.Contracts.NotificationTypes.ContributionSuspension, "Suspended {member_number}", "{member_number} is suspended from {suspended_from}."));
        await Seed("health.cfg_tariff_schedules", "EFFECTIVE", "REF-0190-G", ("tariff_code", "0190"), ("price", 400m), ("schedule_code", "REF"));
        // Billing ages an overdue invoice into arrears only with a bucket in force (otherwise it parks it, and no grace starts).
        await Seed("billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "D1", ("bucket_code", "D1"), ("days_from", 1), ("days_to", 9999), ("action", "REMINDER"));
        await Seed("health.cfg_contribution_grace_rules", "EFFECTIVE", "CGR-ALL", ("grace_days", 3), ("suspend_after_days", 30), ("terminate_notice_days", 30));
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var schemeNumber = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_contribution_rules", "EFFECTIVE", $"CR-{schemeNumber}", ("scheme", schemeNumber), ("member_category", "ALL_STAFF"), ("rate", 1_000m),
            ("employer_share", 0m));

        async Task<(Guid Membership, Guid Principal)> Unpaid(string name, DateOnly due)
        {
            var principal = await _maker.RegisterPerson(name);
            await _db.AddContact(Tenant, principal, "EMAIL", $"{name.ToLowerInvariant()}@example.com");
            var membership = await Created("/api/v1/health/memberships", new
            {
                principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today.AddMonths(-6),
                currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
            });
            await _factory.DrainOutbox();
            await _db.ExecuteAsOwner("UPDATE billing.invoice SET due_date = @d, issue_date = @d WHERE source_domain LIKE 'HEALTH%' AND policy_ref = @m", ("d", due), ("m", membership));
            await _db.ExecuteAsOwner("""
                UPDATE billing.premium_instalment i SET due_date = @d FROM billing.premium_schedule s
                WHERE s.id = i.schedule_id AND s.policy_ref = @m AND i.status = 'INVOICED'
                """, ("d", due), ("m", membership));
            return (membership, principal);
        }

        Task<Guid> MemberClaim(Guid membership, Guid principal, DateOnly serviceDate) => Created("/api/v1/health/claims", new
        {
            source = "MEMBER", membershipId = membership, patientPartyId = principal, serviceDate, formatCode = "API", currency = "ZAR",
            lines = new[] { new { tariffCode = "0190", quantity = 1, claimedAmount = 400m } },
        });

        async Task<JsonElement> Adjudicated(Guid claim)
        {
            (await _maker.PostAsync($"/api/v1/health/claims/{claim}/validate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            return await (await _maker.PostAsync($"/api/v1/health/claims/{claim}/adjudicate", null)).Json();
        }

        var paying = await Unpaid("Paying", Today.AddDays(-5));
        var lapsing = await Unpaid("Lapsing", Today.AddDays(-40));
        await _factory.Services.GetRequiredService<Insys.Billing.Infrastructure.BillingJobs>().Run(CancellationToken.None);
        await _factory.DrainOutbox();

        // In grace: the claims are held (pended), not declined.
        var heldPaying = await MemberClaim(paying.Membership, paying.Principal, Today.AddDays(-3));
        var heldLapsing = await MemberClaim(lapsing.Membership, lapsing.Principal, Today.AddDays(-35));
        foreach (var claim in new[] { heldPaying, heldLapsing })
        {
            var held = await Adjudicated(claim);
            (held.GetProperty("status").GetString(), held.GetProperty("pendReason").GetString()).ShouldBe(("Pended", "CONTRIBUTIONS_UNPAID"), held.ToString());
        }

        // The suspension day has passed for one: one job run suspends it for arrears and declines its held claim; the other stays in grace.
        var jobs = _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();

        (await Get($"/api/v1/health/memberships/{lapsing.Membership}")).GetProperty("status").GetString().ShouldBe("Suspended");
        (await Get($"/api/v1/health/memberships/{paying.Membership}")).GetProperty("status").GetString().ShouldBe("Active");
        var declined = await Get($"/api/v1/health/claims/{heldLapsing}");
        (declined.GetProperty("status").GetString(), declined.GetProperty("pendReason").GetString()).ShouldBe(("Rejected", "CONTRIBUTIONS_ARREARS"));
        (await Get($"/api/v1/health/claims/{heldPaying}")).GetProperty("status").GetString().ShouldBe("Pended");

        // Paid: the held claim is adjudicated by the next job run.
        foreach (var payment in await _db.ScalarAsOwner<Guid[]>("""
                     SELECT array_agg(p.id) FROM payment.payment p JOIN billing.invoice i ON i.id = p.business_ref_id WHERE i.policy_ref = @m
                     """, ("m", paying.Membership)) ?? [])
        {
            (await _maker.SimulatorCallback(_factory, _db, payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _factory.DrainOutbox();
        await jobs.Run(CancellationToken.None);
        await _factory.DrainOutbox();
        var released = await Get($"/api/v1/health/claims/{heldPaying}");
        released.GetProperty("status").GetString().ShouldBe("Adjudicated", released.ToString());
        released.GetProperty("totalPaid").GetDecimal().ShouldBeGreaterThan(0m);

        // The notice ran out: a person is asked whether to terminate (once); the membership stays suspended.
        await _db.ExecuteAsOwner("UPDATE health.membership SET termination_notice_ends = @d WHERE id = @m", ("d", Today.AddDays(-1)), ("m", lapsing.Membership));
        for (var run = 0; run < 2; run++)
        {
            await jobs.Run(CancellationToken.None);
            await _factory.DrainOutbox();
        }

        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(CASE WHEN p.ended_at IS NULL THEN 'OPEN' ELSE 'ENDED' END || ':' || t.queue_code, ',')
            FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'HEALTH_ARREARS_TERMINATION' AND p.business_ref_id = @m
            """, ("m", lapsing.Membership))).ShouldBe("OPEN:HEALTH_MEMBERSHIP");
        (await Get($"/api/v1/health/memberships/{lapsing.Membership}")).GetProperty("status").GetString().ShouldBe("Suspended", "never terminated automatically");

        await _factory.Services.GetRequiredService<Insys.Communications.Infrastructure.NotificationSender>().RunForTenant(Tenant, CancellationToken.None);
        var memberNumber = (await Get($"/api/v1/health/memberships/{lapsing.Membership}")).GetProperty("memberNumber").GetString();
        (await _db.ScalarAsOwner<string>("""
            SELECT string_agg(status || ':' || body, '|') FROM communication.notification WHERE recipient_party_id = @p AND type_code = 'CONTRIBUTION_SUSPENSION'
            """, ("p", lapsing.Principal))).ShouldBe($"SENT:{memberNumber} is suspended from {Today:yyyy-MM-dd}.");
    }

    /// <summary>Wave 4 §4.5: a scheme's sponsor is told of its renewal by the scheme's renewal rule's notice, once per scheme year.</summary>
    [Fact]
    public async Task ASchemesSponsor_IsToldOfItsRenewal_ByTheRulesNotice_OncePerYear()
    {
        await _db.SeedEmailNotices(Tenant, Today.AddDays(-30), "SRN",
            (Insys.Communications.Contracts.NotificationTypes.SchemeRenewalNotice, "Scheme {scheme_number}", "{scheme_number} renews on {renewal_date}."));
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddDays(45), currency = "ZAR" });
        var number = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_scheme_renewal_rules", "EFFECTIVE", $"SRR-{number}", ("scheme", number), ("notice_days", 60), ("re_rating_basis", "COMMUNITY"));
        var jobs = _factory.Services.GetRequiredService<Insys.Health.Infrastructure.HealthJobs>();
        for (var run = 0; run < 2; run++)
        {
            await jobs.Run(CancellationToken.None);
            await _factory.DrainOutbox();
        }

        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM communication.notification WHERE recipient_party_id = @s AND type_code = 'SCHEME_RENEWAL_NOTICE' AND source_id = @c
            """, ("s", SponsorParty), ("c", scheme))).ShouldBe(1L, "sent once however often the job runs");
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
        var view = await Get($"/api/v1/health/memberships/{membership}");
        view.GetProperty("productCode").GetString().ShouldBe("HLT-PROD", "GAP-050");
        view.GetProperty("productVersion").GetInt32().ShouldBe(1, "GAP-050");
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
        (await Get($"/api/v1/health/memberships/{late}")).GetProperty("productCode").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.premium_schedule WHERE policy_ref = @m AND product_code IS NULL", ("m", late))).ShouldBe(1L);

        // The next version names the product: the daily job gives it to the membership, and Billing to its schedule and later invoices.
        await _db.UserWithPermissions(Tenant, "hlt-jobs");
        var jobs = _factory.ClientFor(Tenant, "hlt-jobs");
        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today.AddDays(1) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM health.membership WHERE id = @m", ("m", late))).ShouldBe("HLT-PROD");
        (await Get($"/api/v1/health/memberships/{late}")).GetProperty("productVersion").GetInt32().ShouldBe(1);
        (await _db.ScalarAsOwner<string>("SELECT product_code FROM billing.premium_schedule WHERE policy_ref = @m", ("m", late))).ShouldBe("HLT-PROD");
        (await jobs.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = Today.AddMonths(1) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var laterInvoice = await _db.ScalarAsOwner<Guid>(
            "SELECT id FROM billing.invoice WHERE policy_ref = @m AND product_code = 'HLT-PROD' ORDER BY due_date DESC LIMIT 1", ("m", late));
        laterInvoice.ShouldNotBe(Guid.Empty, "an invoice raised after the product is assigned carries it");
        (await _factory.ClientFor(Tenant, "hlt-own-product").GetAsync($"/api/v1/billing/invoices/{laterInvoice}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _factory.ClientFor(Tenant, "hlt-other-product").GetAsync($"/api/v1/billing/invoices/{laterInvoice}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// External-user access: a provider submits claims only as itself and reads only its own; a member opens only their own
    /// membership. Where the member portal's authentication policy requires MFA, a token without it is refused.
    /// </summary>
    [Fact]
    public async Task ExternalUsers_ProvidersAndMembersSeeOnlyTheirOwn_ExternalUserAccess()
    {
        var scheme = await Created("/api/v1/health/schemes", new { sponsorCode = "S-ACME", startDate = Today.AddYears(-1), renewalDate = Today.AddMonths(6), currency = "ZAR" });
        var schemeNumber = (await Get($"/api/v1/health/schemes/{scheme}")).GetProperty("schemeNumber").GetString()!;
        await Seed("health.cfg_contribution_rules", "EFFECTIVE", "CR-PORTAL", ("scheme", schemeNumber), ("member_category", "ALL_STAFF"), ("rate", 1_000m), ("employer_share", 0m));
        async Task<(Guid Membership, Guid Principal)> Member(string surname)
        {
            var principal = await _maker.RegisterPerson(surname);
            return (await Created("/api/v1/health/memberships", new
            {
                principalPartyId = principal, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = "HLT-CORE", optionCode = "A", startDate = Today,
                currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
            }), principal);
        }

        var (mine, me) = await Member("Portal");
        var (theirs, them) = await Member("Other");
        var memberNumber = (await Get($"/api/v1/health/memberships/{mine}")).GetProperty("memberNumber").GetString()!;
        foreach (var provider in new[] { "PR-1", "PR-2" })
        {
            await _db.ExecuteAsOwner("""
                INSERT INTO health.mst_provider_register (tenant_id, code, version, effective_from, record_status, created_by, change_reason, provider_id, name,
                                                          registration_numbers, locations, status, provider_type)
                VALUES (@t, @p, 1, @from, 'ACTIVE', 'seed', 'seed', @p, @p, '{"practice":"P"}', '[]', 'ACTIVE', 'SPEC')
                ON CONFLICT DO NOTHING
                """, ("t", Tenant), ("p", provider), ("from", Today.AddYears(-2)));
        }

        async Task<string> PortalUser(string subject, string kind, Guid? party, string? provider, params string[] permissions)
        {
            var user = await _db.UserWithPermissions(Tenant, subject, permissions);
            await _db.ExecuteAsOwner("""
                INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, provider_id,
                                                   status, requested_by, decided_by, decided_at)
                VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @v, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
                """, ("t", Tenant), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("v", (object?)provider ?? DBNull.Value));
            return (await _db.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE user_id = @u", ("u", user)))!;
        }

        string[] providerPermissions = [HealthPermissions.ClaimRead, HealthPermissions.ClaimCreate, HealthPermissions.PreauthRead, HealthPermissions.PreauthCreate,
            HealthPermissions.RunRead];
        var providerRole = await PortalUser("provider-1", "PROVIDER", null, "PR-1", providerPermissions);
        await PortalUser("provider-2", "PROVIDER", null, "PR-2", providerPermissions);
        await PortalUser("member-1", "PARTY", me, null, HealthPermissions.MembershipRead, HealthPermissions.ClaimRead);
        await PortalUser("member-2", "PARTY", them, null, HealthPermissions.MembershipRead, HealthPermissions.ClaimRead);

        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-PRV", ("channel_code", "PRV"), ("audience", "PROVIDER"), ("name", "Provider portal"),
            ("client_ids", new[] { "provider-portal" }));
        await Seed("channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM", ("channel_code", "MEM"), ("audience", "CUSTOMER"), ("name", "Member portal"),
            ("client_ids", new[] { "member-portal" }));
        await Seed("channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-PRV-C", ("channel_code", "PRV"), ("role_code", providerRole),
            ("transaction_type", HealthPermissions.ClaimCreate), ("allowed", true));
        await _db.AdoptFunctionCatalogue();
        var provider1 = _factory.ClientFor(Tenant, "provider-1", "provider-portal", multiFactor: true);
        var provider2 = _factory.ClientFor(Tenant, "provider-2", "provider-portal", multiFactor: true);

        // A provider submits as itself; another provider cannot read the claim or see it listed.
        var submitted = await provider1.Send(HttpMethod.Post, "/api/v1/portal/health/claims", new
        {
            memberNumber, serviceDate = Today, currency = "ZAR", lines = new[] { new { tariffCode = "0300", quantity = 1, claimedAmount = 500m } },
        }, idempotent: true);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var claim = await submitted.Json();
        claim.GetProperty("providerId").GetString().ShouldBe("PR-1");
        var claimId = claim.GetProperty("id").GetGuid();
        (await provider1.GetAsync($"/api/v1/portal/health/claims/{claimId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await provider2.GetAsync($"/api/v1/portal/health/claims/{claimId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await provider2.GetAsync("/api/v1/portal/health/claims")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        // A member opens their own membership, not another's.
        var member = _factory.ClientFor(Tenant, "member-1", "member-portal");
        (await member.GetAsync($"/api/v1/portal/health/memberships/{mine}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await member.GetAsync($"/api/v1/portal/health/memberships/{theirs}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A member sees their own claims, as patient or principal, and not another member's.
        var mineListed = await member.GetAsync("/api/v1/portal/health/claims");
        mineListed.StatusCode.ShouldBe(HttpStatusCode.OK, await mineListed.Content.ReadAsStringAsync());
        (await mineListed.Json()).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([claimId]);
        (await member.GetAsync($"/api/v1/portal/health/claims/{claimId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = _factory.ClientFor(Tenant, "member-2", "member-portal");
        (await other.GetAsync($"/api/v1/portal/health/claims/{claimId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await other.GetAsync("/api/v1/portal/health/claims")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        // Member MFA is the tenant's choice: once the member portal's policy requires it, a token without it is refused.
        await Seed("iam.cfg_authentication_and_session_policies", "EFFECTIVE", "AUTH-MEM", ("policy_code", "AUTH-MEM"), ("mfa_required_roles", Array.Empty<string>()),
            ("session_timeout", TimeSpan.FromMinutes(30)), ("lockout_attempts", 5), ("client_ids", new[] { "member-portal" }), ("mfa_required", true));
        (await (await member.GetAsync($"/api/v1/portal/health/memberships/{mine}")).Code()).ShouldBe("PORTAL_MFA_REQUIRED");
        (await _factory.ClientFor(Tenant, "member-1", "member-portal", multiFactor: true).GetAsync($"/api/v1/portal/health/memberships/{mine}")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
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

    /// <summary>The SPECIALIST accumulator of the benefit period covering the day (today by default): a period is a benefit year, so near a year end a reservation dated earlier sits in the previous one.</summary>
    private async Task<(decimal Used, decimal Reserved)> Accumulator(Guid membership, string scope, DateOnly? on = null)
    {
        var day = $"{on ?? Today:yyyy-MM-dd}";
        var accumulators = (await Get($"/api/v1/health/memberships/{membership}/accumulators")).EnumerateArray()
            .Where(a => a.GetProperty("benefitCategory").GetString() == "SPECIALIST" && a.GetProperty("scope").GetString() == scope
                && string.CompareOrdinal(a.GetProperty("periodStart").GetString(), day) <= 0 && string.CompareOrdinal(a.GetProperty("periodEnd").GetString(), day) >= 0)
            .ToList();
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
