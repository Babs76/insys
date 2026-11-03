/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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
using System.Text.Json;

namespace Insys.Seed;

/// <summary>What lets the published products take business, and the providers and brokers it runs through. All invented, and marked so; the benefit figures are samples, not actuarial.</summary>
public sealed partial class OftzaSeed
{
    private const string Scheme = "OFT-MED";
    private const string SponsorCode = "OFT-SPONSOR";
    private const string Tariff = "OFT-TARIFF";
    private const string Reference = "OFT-REF";

    private Persona Clerk => context.Tenant(TenantId, "oftza-general-underwriter");

    private async Task LineConfiguration()
    {
        if (_loadersDone)
        {
            return;
        }

        await Static("workflow/static/delegation-and-approval-authority", "AUTH-BANK_ACCOUNT_VERIFICATION-FINANCE_MANAGER", new { roleCode = "FINANCE_MANAGER", transactionType = "BANK_ACCOUNT_VERIFICATION", limitValue = 0m, delegationAllowed = false });
        await Static("documents/static/document-categories", "POLICY_SCHEDULE", new { categoryCode = "POLICY_SCHEDULE", label = "Policy schedule", classification = "PERSONAL", domain = "ALL", allowedRoles = Array.Empty<string>() });
        foreach (var (route, code, fields) in new (string, string, object)[]
                 {
                     ("life/static/life-benefit-types", "DEATH", new { label = "Death", payoutType = "LUMP_SUM" }), ("life/static/life-benefit-types", "FUNERAL", new { label = "Funeral", payoutType = "LUMP_SUM" }),
                     ("life/static/cause-of-event-codes", "I21", new { codeSystem = "ICD-10", accidental = false }), ("health/static/diagnosis-codes", "K35", new { codeSystem = "ICD-10", active = true }),
                     ("health/static/procedure-and-service-codes", "0300", new { codeSystem = "NRPL", active = true }), ("health/static/procedure-and-service-codes", "0190", new { codeSystem = "NRPL", active = true }),
                 })
        {
            await Static(context.PlatformMaker, context.PlatformChecker, $"{route}/global", code, fields);
        }

        await Static("documents/static/document-categories", "KYC_EVIDENCE", new { categoryCode = "KYC_EVIDENCE", label = "Due diligence evidence", classification = "PERSONAL", domain = "ALL", allowedRoles = Array.Empty<string>() });
        await LifeConfiguration();
        await GeneralConfiguration();
        await Providers();
        await Brokers();
        await HealthConfiguration();
        await ClaimConfiguration();
        await Reinsurance();
        await Static("workflow/static/delegation-and-approval-authority", "AUTH-EXTERNAL_SUBJECT_LINK", new { roleCode = "TENANT_ADMIN", transactionType = "EXTERNAL_SUBJECT_LINK", limitValue = 0m, delegationAllowed = false });
    }

    /// <summary>One quota share treaty on the General products, 30 % retained, with a sample reinsurer: policies issued after it are ceded.</summary>
    private async Task Reinsurance()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await Static("reinsurance/static/reinsurer-register", "OFT-RE-1", new { reinsurerCode = "OFT-RE-1", legalName = "Sample Reinsurance (invented)", country = "ZA", status = "APPROVED" });
        await Static("reinsurance/static/treaty-definitions", "OFT-QS-1", new { treatyCode = "OFT-QS-1", type = "QUOTA_SHARE", retention = "30", capacity = 5_000_000m, periodFrom = today.AddYears(-1), periodTo = today.AddYears(1), participants = new[] { new { reinsurer = "OFT-RE-1", share = 100 } } });
        await Static("reinsurance/static/cession-rules", "OFT-CR-1", new { ruleCode = "OFT-CR-1", treaty = "OFT-QS-1", products = new[] { "OFT-MOTOR", "OFT-HOME" }, allocationBasis = "SUM_INSURED" });
    }

    private async Task LifeConfiguration()
    {
        await Static("life/static/policy-numbering-schemes", "LIFE", new { productLine = "LIFE", prefix = "LP", sequenceLength = 9 });
        await Static("claims-framework/static/claim-numbering-schemes", "LIF", new { domain = "LIFE", prefix = "LIF", sequenceLength = 8 });
        await Static("life/static/grace-period-rules", "GRACE-M", new { frequency = "MONTHLY", graceDays = 30, coverDuringGrace = true });
        await Static("rules/static/acceptance-and-referral-rules", "OFT-UW-LIMIT", new { ruleCode = "OFT-UW-LIMIT", condition = "sum_assured > threshold", threshold = 5_000_000m, outcome = "REFER", domain = "LIFE" });
        foreach (var relationship in new[] { "SPOUSE", "CHILD" })
        {
            await Static("life/static/beneficiary-relationship-types", relationship, new { label = relationship });
        }

        foreach (var (code, label, terms) in new[] { ("STD", "Standard terms", false), ("EXCL", "Accepted with an exclusion", true), ("DECL", "Declined", false) })
        {
            await Static("life/static/underwriting-decision-codes", code, new { label, requiresTerms = terms });
        }

        await Static("life/static/alteration-types", "ALT-INT", new { alterationCode = "INTERMEDIARY", allowedStates = new[] { "IN_FORCE" }, reUnderwriting = false, authorityRole = "LIFE_UNDERWRITER", documentType = "ENDORSEMENT" });
        foreach (var (code, benefit) in new[] { ("DEATH", "DEATH"), ("FUNERAL", "FUNERAL") })
        {
            await Static("life/static/life-claim-types", code, new { label = code, benefitType = benefit });
        }

        foreach (var product in new[] { "OFT-FUNERAL", "OFT-TERM" })
        {
            await Static("life/static/nomination-rules", $"NOM-{product}", new { productCode = product, maxBeneficiaries = 5, minShare = 10m, allowedRelationships = new[] { "SPOUSE", "CHILD" } });
            await Static("life/static/lapse-and-reinstatement-rules", $"LAPSE-{product}", new { productCode = product, missedPaymentsToLapse = 2, reinstatementWindow = "P180D", evidenceRequired = "DECLARATION" });
        }
    }

    private async Task GeneralConfiguration()
    {
        await Static("general/static/risk-object-types-and-attribute-schemas", "VEHICLE", new { objectType = "VEHICLE", attributes = new[] { "registration:String(10):Y", "vehicle_group:String(5):Y", "year:Integer:Y" } });
        await Static("general/static/risk-object-types-and-attribute-schemas", "BUILDING", new { objectType = "BUILDING", attributes = new[] { "construction:String(10):Y", "year:Integer:Y" } });
        await Static("general/static/policy-numbering-schemes", "GP", new { productLine = "GENERAL", prefix = "GP", sequenceLength = 9 });
        await Static("general/static/cancellation-reasons-and-refund-rules", "INS_REQUEST", new { reasonCode = "INS_REQUEST", initiator = "INSURED", refundBasis = "PRO_RATA", fee = 0m });
        await Static("billing/static/adjustment-and-reversal-reason-codes", "INS_REQUEST", new { label = "Cancelled at the insured's request", requiresApproval = false, accountingImpact = "CREDIT" });
        await Static("general/static/endorsement-types", "CHANGE_SI", new { premiumMethod = "PRO_RATA", documents = new[] { "ENDORSEMENT" }, requiresUnderwriting = false });
        await Static("documents/static/document-templates", "OFT-TPL-RENEWAL", new { templateCode = "OFT-TPL-RENEWAL", language = "en", documentType = "RENEWAL_NOTICE", bodyRef = "sample", mergeFields = Array.Empty<string>() });
        foreach (var product in new[] { "OFT-MOTOR", "OFT-HOME" })
        {
            await Static("general/static/quote-validity-periods", $"QV-{product}", new { productCode = product, daysValid = 30, reRateOnExpiry = false });
            await Static("general/static/renewal-rules", $"RR-{product}", new { productCode = product, noticeDays = 31, autoRenew = false, reRating = false, invitationTemplate = "OFT-TPL-RENEWAL", acceptance = "ON_ACCEPTANCE" });
        }
    }

    /// <summary>An organisation with a verified account, the maker adding and the finance manager verifying: the seed bypasses no second person.</summary>
    private async Task<Guid> PaidOrganisation(string name, int account)
    {
        var party = await Party(Clerk, name, name, null, new { organisation = new { legalName = name, legalFormCode = "PTY" }, confirmNew = true, reason = "Sample party (invented)" });
        var url = $"/api/v1/party/parties/{party}/bank-accounts";
        var existing = await Clerk.Send(HttpMethod.Get, url);
        var accounts = (existing.ValueKind == JsonValueKind.Array ? existing : existing.GetProperty("items")).EnumerateArray().ToList();
        var id = accounts.Count > 0 ? accounts[0].GetProperty("id").GetGuid()
            : (await Clerk.Send(HttpMethod.Post, url, new { accountNumber = $"{account:000000000}", bankCode = "000000", accountHolderName = name, accountType = "CURRENT", currency = "ZAR" }, key: $"{TenantCode}:bank-account:{name}")).GetProperty("id").GetGuid();
        await context.Settle();
        await Approve(context.Tenant(TenantId, "oftza-finance-manager"), id);
        await context.Settle();
        return party;
    }

    private async Task Providers()
    {
        foreach (var (code, label) in new[] { ("GP", "General practitioner"), ("SPEC", "Specialist"), ("HOSP", "Day hospital") })
        {
            await Static("health/static/provider-types-and-specialties", code, new { typeCode = code, specialtyCode = code, label });
        }

        var number = 0;
        foreach (var (code, name, type) in SampleParties.Providers)
        {
            var party = await PaidOrganisation(name, ++number);
            await Static("health/static/provider-register", code, new { providerId = code, name, registrationNumbers = new { practice = $"SAMPLE-{number}" }, locations = Array.Empty<object>(), status = "ACTIVE", providerType = type, partyId = party });
        }
    }

    private async Task Brokers()
    {
        await Static("distribution/static/commission-plans", "OFT-COM-10", new { planCode = "OFT-COM-10", basis = "PREMIUM", rate = 10m, clawbackRule = "UNEARNED" });
        var number = SampleParties.Providers.Length;
        foreach (var (code, name) in SampleParties.Brokers)
        {
            var party = await PaidOrganisation(name, ++number);
            await Static("distribution/static/intermediary-register", code, new { intermediaryCode = code, type = "BROKER", legalName = name, licenceNumbers = new[] { $"SAMPLE-FSP-{number}" }, status = "ACTIVE", hierarchyNode = "OFTZA-ROOT", partyId = party });
            foreach (var product in new[] { "OFT-MOTOR", "OFT-HOME", "OFT-FUNERAL", "OFT-TERM", "OFT-HEALTH" })
            {
                await Static("distribution/static/intermediary-product-permissions", $"{code}:{product}", new { intermediary = code, product, transactionsAllowed = Array.Empty<string>(), planCode = "OFT-COM-10" });
            }
        }
    }

    /// <summary>One scheme, one plan with two options, a tariff and a formulary. A sponsor company pays half of each contribution.</summary>
    private async Task HealthConfiguration()
    {
        var sponsor = await Party(Clerk, SampleParties.Companies[0], SampleParties.Companies[0], null, new { });
        await Static("health/static/sponsor-register", SponsorCode, new { sponsorCode = SponsorCode, party = sponsor.ToString(), schemes = Array.Empty<string>() });
        foreach (var (code, outcome) in new[] { ("E00", "ELIGIBLE"), ("E01", "NOT_ELIGIBLE"), ("E02", "NOT_FOUND"), ("E03", "WAITING_PERIOD") })
        {
            await Static("health/static/eligibility-response-codes", code, new { meaning = outcome, providerAction = "NONE", outcome });
        }

        await Static("health/static/scheme-member-categories", "ALL_STAFF", new { categoryCode = "ALL_STAFF", eligibilityRule = "employment_type = 'PERMANENT'", plan = Scheme });
        await Static("health/static/member-numbering-schemes", "HM", new { prefix = "HM", sequenceLength = 9, dependantSuffix = "-01" });
        await Static("health/static/dependant-types-and-age-limits", "CHILD", new { typeCode = "CHILD", maxAge = 21, studentExtensionAge = 26, evidence = "BIRTH_CERTIFICATE" });
        await Static("health/static/dependant-types-and-age-limits", "SPOUSE", new { typeCode = "SPOUSE", maxAge = 120, evidence = "MARRIAGE_CERTIFICATE" });
        await Static("health/static/health-waiting-period-rules", "WP-GEN", new { ruleCode = "WP-GEN", type = "GENERAL", duration = "P0D", exceptions = new[] { "EMERGENCY" } });
        foreach (var (option, limit, schedule) in new[] { ("ESS", 20_000m, "OFT-BS-ESS"), ("PLUS", 60_000m, "OFT-BS-PLUS") })
        {
            await Static("health/static/plans-and-options", $"{Scheme}-{option}", new { planCode = Scheme, optionCode = option, benefitSchedule = schedule, network = "OFT-NET", benefitYear = "CALENDAR", memberClaimBasis = "REFERENCE_TARIFF", productCode = "OFT-HEALTH", referenceTariffScheduleCode = Reference });
            await Static("health/static/benefit-schedules", $"{schedule}-SPEC", new { scheduleCode = schedule, benefit = "SPECIALIST", limitValue = limit, frequency = "PER_YEAR" });
        }

        await Static("health/static/limit-definitions", "LIM-SPEC-FAM", new { benefit = "SPECIALIST", period = "ANNUAL", amount = 100_000m, basis = "PER_FAMILY" });
        await Static("health/static/limit-definitions", "LIM-GP-FAM", new { benefit = "GP_VISITS", period = "ANNUAL", amount = 20_000m, basis = "PER_FAMILY" });
        await Static("health/static/co-payment-and-deductible-rules", "CP-SPEC", new { ruleCode = "CP-SPEC", benefit = "SPECIALIST", type = "PERCENT", value = 10m, networkStatus = "IN_NETWORK", cap = 500m, ruleKind = "CO_PAYMENT", capBasis = "PER_LINE" });
        await Static("health/static/preauthorisation-required-lists", "PAR-SPEC", new { plan = Scheme, service = "SPECIALIST", threshold = 0m });
        await Static("health/static/authorisation-validity-periods", "AV-PROC", new { serviceType = "PROCEDURE", daysValid = 30 });
        foreach (var service in new[] { "CONSULTATION", "PROCEDURE" })
        {
            await Static("health/static/service-types", $"ST-{service}", new { serviceType = service });
        }

        foreach (var (code, from, to, category) in new[] { ("M-SPEC", "0300", "0399", "SPECIALIST"), ("M-GP", "0190", "0199", "GP_VISITS") })
        {
            await Static("health/static/benefit-category-mappings", code, new { codeSystem = "NRPL", codeFrom = from, codeTo = to, benefitCategory = category });
        }

        await Static("health/static/high-cost-thresholds", "HC-SPEC", new { service = "SPECIALIST", amount = 5_000m, reviewQueue = "UTILISATION" });
        await Static("health/static/payment-run-calendars", "PRV-WEEKLY", new { runCode = "PRV-WEEKLY", frequency = "WEEKLY", cutOff = "Wednesday 12:00", calendar = "CAL-ZA" });
        await Static("health/static/claim-format-definitions", "API", new { formatCode = "API", fieldMapping = new { }, transport = "API" });
        await Static("health/static/network-definitions", "OFT-NET", new { networkCode = "OFT-NET", name = "Sample network", providers = SampleParties.Providers.Select(p => p.Code).ToArray(), plans = new[] { Scheme }, tariffScheduleCode = Tariff });
        foreach (var (code, _, _) in SampleParties.Providers)
        {
            await Static("health/static/provider-contracts", $"C-{code}", new { contractId = $"C-{code}", provider = code, network = "OFT-NET", termsRef = "doc://sample/contract", tariffScheduleCode = Tariff });
        }

        await Static("health/static/network-restriction-rules", "NR-MED", new { plan = Scheme, network = "OFT-NET", outOfNetworkTreatment = "COVERED_AT_TARIFF" });
        foreach (var (schedule, code, price) in new[] { (Tariff, "0300", 1_000m), (Reference, "0300", 800m), (Tariff, "0190", 500m), (Reference, "0190", 400m) })
        {
            await Static("health/static/tariff-schedules", $"{schedule}-{code}", new { tariffCode = code, price, scheduleCode = schedule });
        }

        await Static("health/static/contribution-grace-rules", "GR-ALL", new { graceDays = 30, suspendAfterDays = 60, terminateNoticeDays = 30 });
        await Static("health/static/preventive-schedules", "PS-GP", new { scheduleCode = "PS-GP", itemKind = "SCREENING", serviceCode = "0190", benefitCategory = "GP_VISITS", serviceInterval = "P1Y", noCopay = true });
        await Static("health/static/provider-measures", "PM-COST", new { measureCode = "AVERAGE_PAID", threshold = 5_000m, direction = "ABOVE", minClaims = 10 });
        await Static("health/static/medicine-formulary", "OFT-MED-1", new { productCode = "OFT-MED-1", activeIngredient = "Sample ingredient", tier = "1", referencePrice = 100m });
    }

    /// <summary>What can only follow activation: a scheme is a business record, and its contribution rule names the number it gets on registration.</summary>
    private async Task AfterActivation()
    {
        if (_loadersDone)
        {
            return;
        }

        // The contribution rule names the scheme's number, which the scheme gets on registration.
        var administrator = context.Tenant(TenantId, "oftza-health-membership-administrato");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var schemes = (await administrator.Send(HttpMethod.Get, $"/api/v1/health/schemes?sponsorCode={SponsorCode}")).GetProperty("items");
        var number = schemes.GetArrayLength() > 0
            ? schemes[0].GetProperty("schemeNumber").GetString()!
            : (await administrator.Send(HttpMethod.Post, "/api/v1/health/schemes", new { sponsorCode = SponsorCode, startDate = today.AddYears(-1), renewalDate = today.AddMonths(6), currency = "ZAR" }, key: $"{TenantCode}:scheme")).GetProperty("schemeNumber").GetString()!;
        await Static("health/static/contribution-rules", "CR-OFT", new { scheme = number, memberCategory = "ALL_STAFF", rate = 1_850m, employerShare = 50m });
    }

    private Task<Guid> Person(int index) => Party(Clerk, $"{SampleParties.People[index].FirstNames} {SampleParties.People[index].Surname}", SampleParties.People[index].IdNumber, SampleParties.People[index].IdNumber, new { });

    /// <summary>The records of a working insurer, each by its normal process. A record already there is left as it is, found by its policyholder.</summary>
    private async Task Business()
    {
        foreach (var index in Enumerable.Range(0, SampleParties.People.Count))
        {
            await Cleared(await Person(index));
        }

        await PortalLinks();
        await LifeBook();
        await GeneralBook();
        await HealthBook();
        await Claims();
        if (context.Jobs is not null)
        {
            await context.Jobs();
        }
    }

    /// <summary>About 12 Life policies: in force, some older than today allows by the normal route, so migrated (B-013), and in grace and lapsed once the hourly jobs have aged what is unpaid. INSYS has no route that cancels a Life policy.</summary>
    private async Task LifeBook()
    {
        foreach (var holder in new[] { 3, 5, 7 })
        {
            await LifePolicy("OFT-FUNERAL", "FUNERAL", 30_000m, holder);
        }

        foreach (var holder in new[] { 4, 6, 8 })
        {
            await LifePolicy("OFT-TERM", "DEATH", 1_000_000m, holder);
        }

        await MigratedLife(9);
        await MigratedLife(10);

        // Backdated, so past instalments are unpaid: one in grace, two or more past it. The overdue invoice on the account is what makes Billing report its arrears; a migrated premium alone isn't counted (handover, register).
        foreach (var (holder, days) in new[] { (11, 10), (12, 10), (13, 65), (14, 65) })
        {
            await LifePolicy("OFT-TERM", "DEATH", 500_000m, holder, days);
        }
    }

    /// <summary>A policy that began last year.</summary>
    private async Task MigratedLife(int holder)
    {
        var underwriter = context.Tenant(TenantId, "oftza-life-underwriter");
        var party = await Person(holder);
        if ((await underwriter.Send(HttpMethod.Get, $"/api/v1/life/policies?policyholderPartyId={party}")).GetProperty("items").GetArrayLength() > 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reference = $"OFT-LM-{holder}";
        var born = DateOnly.Parse(SampleParties.People[holder].BirthDate, System.Globalization.CultureInfo.InvariantCulture);
        await underwriter.Send(HttpMethod.Post, "/api/v1/life/migration/policies", new
        {
            batchCode = reference, currency = "ZAR", controlCount = 1, controlPremium = 150m,
            rows = new[]
            {
                new
                {
                    sourceReference = reference, productCode = "OFT-TERM", productVersion = 1, policyholderPartyId = party, inceptionDate = today.AddYears(-1), termEndDate = today.AddYears(9), currency = "ZAR", billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER",
                    lives = new[] { new { partyId = party, lifeRole = "MAIN", entryAge = today.Year - born.Year, benefits = new[] { new { benefitCode = "DEATH", sumAssured = 500_000m, premium = 150m } } } },
                },
            },
        }, key: $"{TenantCode}:{reference}");
        await context.Settle();
    }

    private async Task Unpaid(string domain, string reference, Guid policy, decimal amount, int[] daysAgo)
    {
        if (daysAgo.Length == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await context.Tenant(TenantId, "oftza-finance-officer").Send(HttpMethod.Post, "/api/v1/billing/migration/outstanding-premiums", new
        {
            batchCode = $"{reference}-I", currency = "ZAR", controlCount = daysAgo.Length, controlAmount = amount * daysAgo.Length,
            rows = daysAgo.Select(d => new { sourceReference = $"{reference}-I{d}", sourceDomain = domain, policyRef = policy, amount, dueDate = today.AddDays(-d) }).ToArray(),
        }, key: $"{TenantCode}:{reference}-I");
        await context.Settle();
    }

    /// <summary>About 12 General policies, motor and household: in force by the normal route and from last year, cancelled, and two near the end of their period, awaiting renewal.</summary>
    private async Task GeneralBook()
    {
        foreach (var holder in new[] { 0, 2, 4, 6, 8, 10 })
        {
            await GeneralPolicy("OFT-MOTOR", "VEHICLE", "vehicle_group", "G3", "OWN_DAMAGE", holder, cancel: holder is 8 or 10);
        }

        foreach (var holder in new[] { 1, 3, 5, 7 })
        {
            await GeneralPolicy("OFT-HOME", "BUILDING", "construction", "BRICK", "BUILDINGS", holder, cancel: false);
        }

        await MigratedGeneral(9);
        await MigratedGeneral(11);
    }

    /// <summary>Quote to policy, as in GeneralProcessTests: a risk object, a quote rated and accepted, a proposal accepted, the notice acknowledged, the policy issued.</summary>
    private async Task GeneralPolicy(string product, string objectType, string attribute, string value, string coverage, int holder, bool cancel)
    {
        var underwriter = Clerk;
        var pricer = context.Tenant(TenantId, "oftza-product-manager");
        var party = await Person(holder);
        var existing = (await underwriter.Send(HttpMethod.Get, $"/api/v1/general/policies?policyholderPartyId={party}")).GetProperty("items");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid policy;
        if (existing.GetArrayLength() > 0)
        {
            policy = existing[0].GetProperty("id").GetGuid();
        }
        else
        {
            var attributes = new Dictionary<string, object> { [attribute] = value, ["year"] = today.Year - 2 };
            if (objectType == "VEHICLE")
            {
                attributes["registration"] = $"SMP{holder:000}GP";
            }

            var risk = (await underwriter.Send(HttpMethod.Post, "/api/v1/general/risk-objects", new { objectType, attributes }, key: $"{TenantCode}:risk:{holder}")).GetProperty("id").GetGuid();
            var quote = (await underwriter.Send(HttpMethod.Post, "/api/v1/general/quotes", new
            {
                productCode = product, proposerPartyId = party, currency = "ZAR", periodStart = today, periodEnd = today.AddYears(1).AddDays(-1), intermediaryCode = holder < 4 ? SampleParties.Brokers[0].Code : null,
                risks = new[] { new { riskObjectId = risk, sumInsured = 100_000m, coverages = new object[] { new { coverageCode = coverage, sumInsured = 100_000m } } } },
            }, key: $"{TenantCode}:quote:{holder}")).GetProperty("id").GetGuid();
            var url = $"/api/v1/general/quotes/{quote}";
            if ((await underwriter.Send(HttpMethod.Get, url)).GetProperty("premium").ValueKind == JsonValueKind.Null)
            {
                await pricer.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
                {
                    requestRef = $"G-{quote:N}"[..20], sourceDomain = "GENERAL", subjectType = "GENERAL_QUOTE", subjectId = quote, productCode = product, productVersion = 1, currency = "ZAR", frequency = "ANNUAL",
                    values = new Dictionary<string, object> { ["sum_insured"] = 100_000m, [attribute] = value },
                });
                await context.Settle();
            }

            await Acknowledge(party, "QUOTE", "GENERAL_QUOTE", quote);
            await Step(underwriter, url, "Rated", "issue");
            await Step(underwriter, url, "Issued", "accept");
            var proposal = (await underwriter.Send(HttpMethod.Post, "/api/v1/general/proposals", new { quoteId = quote, declarations = new { claims_last_3_years = 0 } }, key: $"{TenantCode}:proposal:{quote}")).GetProperty("id").GetGuid();
            if ((await underwriter.Send(HttpMethod.Get, $"/api/v1/general/proposals/{proposal}")).GetProperty("status").GetString() != "Accepted")
            {
                await underwriter.Send(HttpMethod.Post, $"/api/v1/general/proposals/{proposal}/accept", idempotent: false);
            }

            await Acknowledge(party, "ISSUE", "GENERAL_QUOTE", quote);
            policy = (await underwriter.Send(HttpMethod.Post, "/api/v1/general/policies", new { quoteId = quote, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER" }, key: $"{TenantCode}:gpolicy:{quote}")).GetProperty("id").GetGuid();
            await context.Settle();
        }

        if (cancel && (await underwriter.Send(HttpMethod.Get, $"/api/v1/general/policies/{policy}")).GetProperty("status").GetString() != "Cancelled")
        {
            await underwriter.Send(HttpMethod.Post, $"/api/v1/general/policies/{policy}/cancel", new { reasonCode = "INS_REQUEST", effectiveDate = today }, key: $"{TenantCode}:cancel:{policy}");
            await context.Settle();
        }
    }

    /// <summary>A quote's step, unless an earlier run took it.</summary>
    private static async Task Step(Persona persona, string url, string from, string action)
    {
        if (action != "none" && (await persona.Send(HttpMethod.Get, url)).GetProperty("status").GetString() == from)
        {
            await persona.Send(HttpMethod.Post, $"{url}/{action}", idempotent: false);
        }
    }

    /// <summary>A policy from last year ending within the renewal notice, so the hourly job takes it to its renewal.</summary>
    private async Task MigratedGeneral(int holder)
    {
        var party = await Person(holder);
        if ((await Clerk.Send(HttpMethod.Get, $"/api/v1/general/policies?policyholderPartyId={party}")).GetProperty("items").GetArrayLength() > 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reference = $"OFT-GM-{holder}";
        await Clerk.Send(HttpMethod.Post, "/api/v1/general/migration/policies", new
        {
            batchCode = reference, currency = "ZAR", controlCount = 1, controlPremium = 1_200m,
            rows = new[]
            {
                new
                {
                    sourceReference = reference, productCode = "OFT-MOTOR", productVersion = 1, policyholderPartyId = party, periodStart = today.AddYears(-1).AddDays(20), periodEnd = today.AddDays(19), currency = "ZAR", annualPremium = 1_200m, billingFrequency = "MONTHLY", collectionMethod = "DEBIT_ORDER",
                    risks = new[] { new { objectType = "VEHICLE", attributes = new { registration = $"SMP{holder:000}GP", vehicle_group = "G3", year = today.Year - 2 }, valuation = 180_000m, valuationBasis = "RETAIL", sumInsured = 180_000m, coverages = new[] { new { coverageCode = "OWN_DAMAGE", sumInsured = 180_000m } } } },
                },
            },
        }, key: $"{TenantCode}:{reference}");
        await context.Settle();
    }

    /// <summary>About 12 members: active, some suspended and some terminated by their routes.</summary>
    private async Task HealthBook()
    {
        foreach (var principal in Enumerable.Range(5, 8))
        {
            await Membership(principal);
        }

        foreach (var (principal, action) in new[] { (13, "suspend"), (14, "suspend"), (15, "terminate"), (16, "terminate") })
        {
            var id = await Membership(principal);
            var url = $"/api/v1/health/memberships/{id}";
            var administrator = context.Tenant(TenantId, "oftza-health-membership-administrato");
            if ((await administrator.Send(HttpMethod.Get, url)).GetProperty("status").GetString() == "Active")
            {
                await administrator.Send(HttpMethod.Post, $"{url}/{action}", new { effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow), reason = "Sample" }, key: $"{TenantCode}:{action}:{id}");
                await context.Settle();
            }
        }
    }

    /// <summary>The portal personas, each linked to who they are: a member to a person, the broker to its intermediary, the provider to its practice. A second administrator approves each link.</summary>
    private async Task PortalLinks()
    {
        foreach (var (name, body) in new (string, object)[]
                 {
                     ("oftza-member", new { kind = "Party", partyId = await Person(5), intermediaryCode = (string?)null, providerId = (string?)null }),
                     ("oftza-broker", new { kind = "Intermediary", partyId = (Guid?)null, intermediaryCode = SampleParties.Brokers[0].Code, providerId = (string?)null }),
                     ("oftza-provider", new { kind = "Provider", partyId = (Guid?)null, intermediaryCode = (string?)null, providerId = SampleParties.Providers[0].Code }),
                 })
        {
            if (await User(name) is not { } found)
            {
                continue;
            }

            var id = found.GetProperty("id").GetGuid();
            if ((await _adminMaker!.Send(HttpMethod.Get, $"/api/v1/identity/users/{id}")).GetProperty("externalSubjectLink").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                await _adminMaker.Send(HttpMethod.Post, $"/api/v1/identity/users/{id}/subject-link", body, await Version(id), idempotent: false);
                await context.Settle();
            }

            // Only the links: other approvals waiting (a claim's, from a stopped run) are another person's to decide.
            foreach (var approval in (await _adminChecker!.Send(HttpMethod.Get, "/api/v1/workflow/approvals?status=Requested")).GetProperty("items").EnumerateArray()
                         .Where(a => a.GetProperty("approvalType").GetString() == "EXTERNAL_SUBJECT_LINK"))
            {
                await _adminChecker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: $"\"{approval.GetProperty("rowVersion").GetInt64()}\"", idempotent: false);
            }

            await context.Settle();
        }
    }

    /// <summary>A party's due diligence, done as a clerk does it: each outstanding check run, or recorded against a sample document, until nothing is left.</summary>
    private async Task Cleared(Guid party)
    {
        var url = $"/api/v1/party/parties/{party}";
        for (var pass = 0; pass < 8; pass++)
        {
            var state = await Clerk.Send(HttpMethod.Get, $"{url}/due-diligence");
            var outstanding = state.GetProperty("outstanding").EnumerateArray().Select(o => o.GetString()!).ToList();
            if (state.GetProperty("status").GetString() == "CLEARED" || outstanding.Count == 0)
            {
                return;
            }

            var check = outstanding[0];
            if (check is "SANCTIONS" or "PEP" or "SCREENING" or "ADVERSE_MEDIA")
            {
                await Clerk.Send(HttpMethod.Post, $"{url}/due-diligence/checks", new { checkType = "SCREENING" }, key: $"{TenantCode}:dd:{party}:SCREENING:{pass}");
            }
            else if (check == "IDENTITY")
            {
                var identifier = (await Clerk.Send(HttpMethod.Get, url)).GetProperty("identifiers")[0].GetProperty("id").GetGuid();
                await Clerk.Send(HttpMethod.Post, $"{url}/due-diligence/checks", new { checkType = check, identifierId = identifier }, key: $"{TenantCode}:dd:{party}:{check}:{pass}");
            }
            else
            {
                var document = (await Clerk.Send(HttpMethod.Post, "/api/v1/documents/documents", new
                {
                    categoryCode = "KYC_EVIDENCE", businessRefType = "PARTY", businessRefId = party, fileName = "sample-evidence.txt", mimeType = "text/plain", contentBase64 = Convert.ToBase64String("Sample evidence, invented"u8.ToArray()), productVersionRef = (string?)null, jurisdiction = "ZA",
                }, key: $"{TenantCode}:dd-doc:{party}:{check}")).GetProperty("id").GetGuid();
                await Clerk.Send(HttpMethod.Post, $"{url}/due-diligence/checks", new { checkType = check, documentId = document, reference = "Sample evidence" }, key: $"{TenantCode}:dd:{party}:{check}");
            }

            await context.Settle();
        }
    }

    /// <summary>Proposal to policy, as in LifeProcessTests: rated, found eligible, submitted, accepted on standard terms within the underwriter's authority, issued.</summary>
    private async Task LifePolicy(string product, string benefit, decimal sumAssured, int holder, int backdatedDays = 0)
    {
        var underwriter = context.Tenant(TenantId, "oftza-life-underwriter");
        // No underwriter template holds the calculation or the rule evaluation (handover, register): only the product manager does, and the seed adds no grant.
        var pricer = context.Tenant(TenantId, "oftza-product-manager");
        var applicant = await Person(holder);
        if ((await underwriter.Send(HttpMethod.Get, $"/api/v1/life/policies?policyholderPartyId={applicant}")).GetProperty("items").GetArrayLength() > 0)
        {
            return;
        }

        var spouse = await Person((holder + 10) % SampleParties.People.Count);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var born = DateOnly.Parse(SampleParties.People[holder].BirthDate, System.Globalization.CultureInfo.InvariantCulture);
        var ageNext = today.AddDays(-backdatedDays).Year - born.Year + 1;
        var existing = (await underwriter.Send(HttpMethod.Get, $"/api/v1/life/proposals?applicantPartyId={applicant}")).GetProperty("items");
        var proposal = existing.GetArrayLength() > 0 ? existing[0].GetProperty("id").GetGuid() : (await underwriter.Send(HttpMethod.Post, "/api/v1/life/proposals", new
        {
            productCode = product, applicantPartyId = applicant, channelCode = "BRANCH", currency = "ZAR", inceptionDate = today.AddDays(-backdatedDays), frequency = "MONTHLY", collectionMethod = "DEBIT_ORDER",
            lives = new[] { new { partyId = applicant, lifeRole = "MAIN", smokerStatus = "NS" } },
            covers = new[] { new { insuredPartyId = applicant, benefitCode = benefit, sumAssured, termYears = 20 } },
            disclosures = new[] { new { insuredPartyId = applicant, questionCode = "SMOKE_12M", questionVersion = 1, answer = JsonSerializer.SerializeToElement(new { value = false }) } },
            nominations = new[] { new { partyId = spouse, relationshipCode = "SPOUSE", sharePct = 100m } },
        }, key: $"{TenantCode}:proposal:{product}:{holder}")).GetProperty("id").GetGuid();
        var url = $"/api/v1/life/proposals/{proposal}";
        if ((await underwriter.Send(HttpMethod.Get, url)).GetProperty("status").GetString() is "Draft" or "Quoted")
        {
            await pricer.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
            {
                requestRef = $"L-{proposal:N}"[..20], sourceDomain = "LIFE", subjectType = "LIFE_PROPOSAL", subjectId = proposal, productCode = product, productVersion = 1, currency = "ZAR", frequency = "MONTHLY",
                values = new { sum_insured = sumAssured, age_band = ageNext <= 40 ? "18-40" : "41-65" },
            });
            await context.Settle();
            await pricer.Send(HttpMethod.Post, "/api/v1/rules/decisions", new { kind = "Eligibility", code = "OFT-LIFE-ELIG", subjectType = "LIFE_PROPOSAL", subjectId = proposal, inputs = new { age_next = ageNext } });
            await context.Settle();
            await underwriter.Send(HttpMethod.Post, $"{url}/submit", idempotent: false);
            await context.Settle();
        }

        if ((await underwriter.Send(HttpMethod.Get, url)).GetProperty("status").GetString() == "InUnderwriting")
        {
            var underwriting = (await underwriter.Send(HttpMethod.Get, $"{url}/underwriting-case")).GetProperty("id").GetGuid();
            await underwriter.Send(HttpMethod.Post, $"/api/v1/life/underwriting-cases/{underwriting}/start-assessment", idempotent: false);
            await pricer.Send(HttpMethod.Post, "/api/v1/rules/decisions", new { kind = "Underwriting", code = product, subjectType = "LIFE_UNDERWRITING_CASE", subjectId = underwriting, inputs = new { sum_assured = sumAssured } });
            await context.Settle();
            await underwriter.Send(HttpMethod.Post, $"/api/v1/life/underwriting-cases/{underwriting}/decision", new { outcome = "Accept", decisionCode = "STD", reason = "STANDARD" }, idempotent: false);
            await context.Settle();
        }

        await Acknowledge(applicant, "ISSUE", "LIFE_PROPOSAL", proposal);
        var policy = await underwriter.Send(HttpMethod.Post, "/api/v1/life/policies", new { proposalId = proposal }, key: $"{TenantCode}:policy:{proposal}");
        await context.Settle();
        if (backdatedDays > 0)
        {
            await Unpaid("LIFE", $"OFT-LA-{holder}", policy.GetProperty("id").GetGuid(), 100m, [backdatedDays]);
        }
    }

    /// <summary>The customer's acknowledgement of the notice shown them, recorded by staff. No underwriter template holds it (handover, register), so the integration administrator records it.</summary>
    private Task<JsonElement> Acknowledge(Guid party, string point, string? referenceType, Guid? reference) => context.Tenant(TenantId, "oftza-integration-administrator").Send(HttpMethod.Post, "/api/v1/integration/disclosure-acknowledgements",
        new { partyId = party, displayPoint = point, noticeType = "POLICY_TERMS", jurisdiction = "ZA", language = "en", businessRefType = referenceType, businessRefId = reference }, key: $"{TenantCode}:ack:{point}:{party}:{reference}");

    private async Task<Guid> Membership(int principal)
    {
        var administrator = context.Tenant(TenantId, "oftza-health-membership-administrato");
        var party = await Person(principal);
        var existing = (await administrator.Send(HttpMethod.Get, $"/api/v1/health/memberships?principalPartyId={party}")).GetProperty("items");
        if (existing.GetArrayLength() > 0)
        {
            return existing[0].GetProperty("id").GetGuid();
        }

        var scheme = (await administrator.Send(HttpMethod.Get, $"/api/v1/health/schemes?sponsorCode={SponsorCode}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        await Acknowledge(party, "ISSUE", null, null);
        var created = await administrator.Send(HttpMethod.Post, "/api/v1/health/memberships", new
        {
            principalPartyId = party, schemeId = scheme, memberCategory = "ALL_STAFF", planCode = Scheme, optionCode = principal % 2 == 0 ? "ESS" : "PLUS", startDate = DateOnly.FromDateTime(DateTime.UtcNow), currency = "ZAR", collectionMethod = "DEBIT_ORDER", facts = new { employment_type = "PERMANENT" },
        }, key: $"{TenantCode}:membership:{principal}");
        await context.Settle();
        return created.GetProperty("id").GetGuid();
    }
}
