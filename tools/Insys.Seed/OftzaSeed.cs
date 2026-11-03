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

/// <summary>
/// Workstream F's sample tenant, OFTZA (docs/plans/workstream-f-oftza-seed.md). All of it invented. It goes through the governed API, a maker and a checker for every static row,
/// and a run again skips what exists by its code. OFTZA is a sample tenant, so it reaches only stubs.
/// </summary>
public sealed partial class OftzaSeed(SeedContext context)
{
    public const string TenantCode = "OFTZA";
    public const string FirstAdministrator = "oftza-admin-1";
    public const string SecondAdministrator = "oftza-admin-2";
    public const string LoaderMaker = "oftza-seed-loader-1";
    public const string LoaderChecker = "oftza-seed-loader-2";
    private const string LoaderRole = "OFTZA_SEED_LOADER";

    private const string Reason = "OFTZA sample data (invented)";
    private Persona _maker = null!;
    private Persona _checker = null!;
    private readonly List<string> _templates = [];
    private Persona? _adminMaker;
    private Persona? _adminChecker;
    private bool _loadersDone;

    public Guid TenantId { get; private set; }

    public async Task Run()
    {
        TenantId = await Tenant();
        // Global rows the platform owns, shared with every tenant: plain reference data, as a platform steward would load it.
        await Static(context.PlatformMaker, context.PlatformChecker, "static-data/static/currencies/global", "ZAR", new { name = "South African rand", minorUnits = 2, active = true });
        await Static(context.PlatformMaker, context.PlatformChecker, "static-data/static/countries/global", "ZA", new { alpha3 = "ZAF", name = "South Africa", active = true });
        foreach (var (code, name) in new[] { ("en", "English"), ("af", "Afrikaans"), ("zu", "isiZulu"), ("xh", "isiXhosa") })
        {
            await Static(context.PlatformMaker, context.PlatformChecker, "static-data/static/languages/global", code, new { name });
        }

        await Static(context.PlatformMaker, context.PlatformChecker, "static-data/static/time-zones/global", "ZA-JHB", new { zone = "Africa/Johannesburg", country = "ZA" });
        foreach (var feature in new[] { "LIFE", "GENERAL", "HEALTH", "REINSURANCE", "BROKER_PORTAL", "PROVIDER_PORTAL", "CUSTOMER_PORTAL" })
        {
            await Static(context.PlatformMaker, context.PlatformChecker, "tenancy/static/feature-entitlements/global", $"{TenantCode}:{feature}", new { tenant = TenantCode, feature, enabled = true });
        }

        await FirstAdministrators();
        _maker = context.Tenant(TenantId, FirstAdministrator);
        _checker = context.Tenant(TenantId, SecondAdministrator);
        await Roles();
        await Loaders();
        await Locale();
        await Configuration();
        await Personas();
        await Products();
        await Parties();
        await LineConfiguration();
        await FromReadiness();
        await Channels();
        await Missing();
        await Activate();
        await AfterActivation();
        await EndLoaders();
        await Business();
        if (context.Accounts)
        {
            await Accounts();
        }
    }

    /// <summary>What readiness still lists after the sample values above: filled so a demo can run, with authority for the types the guide's samples don't name on the tenant administrator.</summary>
    private async Task FromReadiness()
    {
        if (_loadersDone)
        {
            return;
        }

        List<string> Missing(List<JsonElement> checks, string code) => checks.Where(c => c.GetProperty("code").GetString() == code).SelectMany(c => c.GetProperty("missing").EnumerateArray()).Select(m => m.GetString()!).ToList();
        var checks = await Checks();
        var rule = 100;
        foreach (var type in Missing(checks, "POSTING_RULES"))
        {
            await Static("finance/static/posting-rules", $"PR-{++rule}", new { ruleCode = $"PR-{rule}", transactionType = type, debitAccount = "1100", creditAccount = "1200" });
        }

        foreach (var type in Missing(checks, "APPROVAL_AUTHORITY"))
        {
            await Static("workflow/static/delegation-and-approval-authority", $"AUTH-{type}-ADMIN", new { roleCode = "TENANT_ADMIN", transactionType = type, limitValue = 100_000m, delegationAllowed = false, currency = "ZAR" });
        }

        foreach (var reason in Missing(checks, "HEALTH_DECLINE_REASONS"))
        {
            await Static("claims-framework/static/claim-decision-reason-codes", reason, new { decisionType = "DECLINE", customerWording = "Your claim could not be paid (sample wording)." });
        }

        await Static("life/static/claims-authority-limits", "LCL-DEATH", new { role = "CLAIMS_MANAGER", claimType = "DEATH", limitValue = 500_000m });
        await Static("payments/static/tenant-bank-accounts", "OFT-BANK", new { accountRef = "OFT-BANK", bank = "SAMPLE BANK", accountNumber = "000000000", currency = "ZAR", purpose = "COLLECTIONS", glAccount = "1200" });
        await Static("integration/static/disclosure-and-notice-texts", "DISC-PROPOSAL", new { noticeType = "POLICY_TERMS", jurisdiction = "ZA", language = "en", text = "Invented sample terms, not legal wording.", textVersion = 1, displayPoint = "PROPOSAL" });
        var finance = context.Tenant(TenantId, "oftza-finance-manager");
        var first = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        foreach (var start in Enumerable.Range(-11, 13).Select(first.AddMonths))
        {
            await Static("finance/static/accounting-calendar-and-periods", $"CAL-{start:yyyy-MM}", new { periodCode = $"{start:yyyy-MM}", startDate = start, endDate = start.AddMonths(1).AddDays(-1) });
        }

        if ((await finance.Send(HttpMethod.Get, $"/api/v1/finance/periods/{first:yyyy-MM}")).GetProperty("status").GetString() == "Future")
        {
            await finance.Send(HttpMethod.Post, $"/api/v1/finance/periods/{first:yyyy-MM}/open", idempotent: false);
        }

        var integration = context.Tenant(TenantId, "oftza-integration-administrator");
        var endpoints = await integration.Send(HttpMethod.Get, "/api/v1/payments/callback-endpoints");
        if ((endpoints.ValueKind == JsonValueKind.Array ? endpoints : endpoints.GetProperty("items")).GetArrayLength() == 0)
        {
            await integration.Send(HttpMethod.Post, "/api/v1/payments/callback-endpoints", new { providerCode = "SIMULATOR" }, key: $"{TenantCode}:callback-endpoint:SIMULATOR");
        }
    }

    /// <summary>Every staff role's functions on the back-office channel, as readiness lists what is uncovered.</summary>
    private async Task Channels()
    {
        if (_loadersDone)
        {
            return;
        }

        var missing = (await Checks()).Where(c => c.GetProperty("code").GetString() == "CHANNELS").SelectMany(c => c.GetProperty("missing").EnumerateArray()).Select(m => m.GetString()!).ToList();
        if (missing.Count > 0)
        {
            var parts = missing.Select(m => m.Split(": ", 2)).ToList();
            await Import("integration/static/channel-transaction-permissions", $"CTP-{parts[0][0]}-{parts[0][1]}",
                parts.Select(p => (object)new { channelCode = "STF", roleCode = p[0], functionCode = p[1], allowed = true, code = $"CTP-{p[0]}-{p[1]}", changeReason = Reason }).ToList());
        }
    }

    private async Task<List<JsonElement>> Checks() =>
        (await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/tenancy/tenants/{TenantId}/readiness")).GetProperty("steps").EnumerateArray().SelectMany(s => s.GetProperty("checks").EnumerateArray()).ToList();

    /// <summary>Says at once everything a step would refuse, rather than one step at a time. END_TO_END_TESTS's run and the waived checks come later.</summary>
    private async Task Missing()
    {
        var gaps = (await Checks()).Where(c => !c.GetProperty("ready").GetBoolean() && !(c.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.True))
            .Select(c => $"{c.GetProperty("code").GetString()}: {string.Join(",", c.GetProperty("missing").EnumerateArray().Take(8).Select(m => m.GetString()))}").ToList();
        Console.Error.WriteLine("Readiness gaps: " + string.Join(" | ", gaps));
        Gaps = gaps;
    }

    public IReadOnlyList<string> Gaps { get; private set; } = [];

    /// <summary>Walks every onboarding step, then activates. A step that isn't ready stops the seed, naming what is missing.</summary>
    private async Task Activate()
    {
        var url = $"/api/v1/tenancy/tenants/{TenantId}";
        var tenant = await context.PlatformMaker.Send(HttpMethod.Get, url);
        if (tenant.GetProperty("status").GetString() != "Onboarding")
        {
            return;
        }

        string Etag(JsonElement t) => $"\"{t.GetProperty("rowVersion").GetInt64()}\"";
        var steps = tenant.GetProperty("onboardingSteps").EnumerateArray().Select(x => x.GetString()!).SkipWhile(x => x != tenant.GetProperty("onboardingStep").GetString()).ToList();
        foreach (var step in steps)
        {
            tenant = await context.PlatformMaker.Send(HttpMethod.Get, url);
            if (step == "END_TO_END_TESTS")
            {
                var run = (await Checks()).Single(c => c.GetProperty("code").GetString() == "SMOKE_RUN");
                if (!run.GetProperty("ready").GetBoolean())
                {
                    if (!run.TryGetProperty("attestation", out var recorded) || recorded.ValueKind != JsonValueKind.Object)
                    {
                        await context.PlatformMaker.Send(HttpMethod.Post, $"{url}/smoke-run", new { runReference = "OFTZA-SEED", runDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), outcome = "PASSED", ranBy = "Seed" }, Etag(tenant), idempotent: false);
                        tenant = await context.PlatformMaker.Send(HttpMethod.Get, url);
                    }

                    await context.PlatformChecker.Send(HttpMethod.Post, $"{url}/smoke-run/approve", ifMatch: Etag(tenant), idempotent: false);
                    tenant = await context.PlatformMaker.Send(HttpMethod.Get, url);
                }
            }

            object body = step switch
            {
                "INTEGRATION_AND_REPORTING" => new { step, attestations = new[] { new { check = "WEBHOOKS_OR_NONE", note = "Sample tenant: no partners" } } },
                _ => new { step },
            };
            await context.PlatformMaker.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", body, Etag(tenant), idempotent: false);
        }

        tenant = await context.PlatformMaker.Send(HttpMethod.Get, url);
        await context.PlatformMaker.Send(HttpMethod.Post, $"{url}/activate", new { reason = "OFTZA sample tenant" }, Etag(tenant), idempotent: false);
    }

    /// <summary>About 30 invented parties, with details that reach no one (docs/plans/workstream-f-oftza-seed.md, Fake personal details), by a persona who registers parties.</summary>
    private async Task Parties()
    {
        if (_loadersDone)
        {
            return;
        }

        await Static("party/static/identity-document-types", "ZA-NID", new { country = "ZA", docType = "NATIONAL_ID", label = "National identity number", formatPattern = "^[0-9]{13}$" });
        await Static("party/static/party-types-and-legal-forms", "PTY", new { partyKind = "ORGANISATION", label = "Private company (sample)" });
        var clerk = context.Tenant(TenantId, "oftza-general-underwriter");
        foreach (var person in SampleParties.People)
        {
            var id = await Party(clerk, $"{person.FirstNames} {person.Surname}", person.IdNumber, person.IdNumber, new
            {
                individual = new { firstNames = person.FirstNames, surname = person.Surname, birthDate = person.BirthDate }, confirmNew = true, reason = "Sample party (invented)",
            });
            var url = $"/api/v1/party/parties/{id}";
            if ((await clerk.Send(HttpMethod.Get, url)).GetProperty("identifiers").GetArrayLength() == 0)
            {
                await clerk.Send(HttpMethod.Post, $"{url}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = person.IdNumber }, await PartyVersion(clerk, url), key: $"{TenantCode}:identifier:{person.IdNumber}");
            }

            if ((await clerk.Send(HttpMethod.Get, url)).GetProperty("addresses").GetArrayLength() == 0)
            {
                await clerk.Send(HttpMethod.Post, $"{url}/addresses", new { addressType = "RESIDENTIAL", line1 = "1 Sample Street", line2 = (string?)null, city = "Sampleton", subdivision = (string?)null, postalCode = "0000", country = "ZA", validFrom = "2020-01-01" }, await PartyVersion(clerk, url), key: $"{TenantCode}:address:{person.IdNumber}");
            }

            foreach (var (type, value, primary) in new[] { ("EMAIL", person.Email, true), ("MOBILE", person.Mobile, false) })
            {
                if (!(await clerk.Send(HttpMethod.Get, url)).GetProperty("contactPoints").EnumerateArray().Any(c => c.GetProperty("channelType").GetString() == type))
                {
                    await clerk.Send(HttpMethod.Post, $"{url}/contact-points", new { channelType = type, value, isPrimary = primary }, await PartyVersion(clerk, url), key: $"{TenantCode}:contact:{person.IdNumber}:{type}");
                }
            }
        }

        foreach (var name in SampleParties.Companies)
        {
            await Party(clerk, name, name, null, new { organisation = new { legalName = name, legalFormCode = "PTY" }, confirmNew = true, reason = "Sample party (invented)" });
        }

        await context.Settle();
    }

    /// <summary>The party, found if an earlier run made it, by its identifier, then by its exact name for one stopped before its identifier went on. The key returns the original within the API's 24 hours; the search covers past them.</summary>
    private static async Task<Guid> Party(Persona clerk, string name, string businessKey, string? idNumber, object body)
    {
        const string search = "/api/v1/party/parties/search";
        if (idNumber is not null && (await clerk.Send(HttpMethod.Post, search, new { identifier = new { docType = "NATIONAL_ID", country = "ZA", number = idNumber }, limit = 2 }, idempotent: false)).GetProperty("items") is { } byNumber && byNumber.GetArrayLength() > 0)
        {
            return byNumber[0].GetProperty("id").GetGuid();
        }

        foreach (var party in (await clerk.Send(HttpMethod.Post, search, new { name, limit = 50 }, idempotent: false)).GetProperty("items").EnumerateArray())
        {
            if (party.GetProperty("displayName").GetString() == name)
            {
                return party.GetProperty("id").GetGuid();
            }
        }

        return (await clerk.Send(HttpMethod.Post, "/api/v1/party/parties", body, key: $"{TenantCode}:party:{businessKey}")).GetProperty("id").GetGuid();
    }

    private static async Task<string> PartyVersion(Persona persona, string url) => $"\"{(await persona.Send(HttpMethod.Get, url)).GetProperty("rowVersion").GetInt64()}\"";

    /// <summary>The sample products, each by the real route: registered, validated, simulated against its test case, approved by a second person (invented wording and rates, labelled so).</summary>
    private async Task Products()
    {
        if (_loadersDone)
        {
            return;
        }

        await Static("workflow/static/delegation-and-approval-authority", "AUTH-PRODUCT_PUBLICATION", new { roleCode = "TENANT_ADMIN", transactionType = "PRODUCT_PUBLICATION", limitValue = 0m, delegationAllowed = false });
        await Static("rating/static/rounding-and-currency-precision-rules", "ZAR-2DP", new { ruleCode = "ZAR-2DP", currency = "ZAR", step = 0.01m, method = "HALF_UP", stage = "FINAL_PREMIUM" });
        await Static("documents/static/document-templates", "OFT-TPL-1", new { templateCode = "OFT-TPL-1", language = "en", documentType = "POLICY_SCHEDULE", bodyRef = "sample", mergeFields = Array.Empty<string>() });

        await Static("product-factory/static/coverage-and-benefit-library", "OWN_DAMAGE", new { type = "COVERAGE", name = "Own damage (sample)", basis = "INDEMNITY", measure = "SUM_INSURED", domain = "GENERAL" });
        await RatedProduct("OFT-MOTOR", "General", "GENERAL", "MOTOR", "Motor insurance (sample)", "Motor comprehensive (invented sample)", ["OWN_DAMAGE"], "VEHICLE_GROUP", "vehicle_group", [("G3", 0.03m)], "OFT-MOT", 100_000, "G3", 3050m, "GENERAL_UNDERWRITER");

        await Static("product-factory/static/coverage-and-benefit-library", "BUILDINGS", new { type = "COVERAGE", name = "Buildings (sample)", basis = "INDEMNITY", measure = "SUM_INSURED", domain = "GENERAL" });
        await Static("product-factory/static/coverage-and-benefit-library", "CONTENTS", new { type = "COVERAGE", name = "Contents (sample)", basis = "INDEMNITY", measure = "SUM_INSURED", domain = "GENERAL" });
        await RatedProduct("OFT-HOME", "General", "GENERAL", "PROPERTY", "Household insurance (sample)", "Household (invented sample)", ["BUILDINGS", "CONTENTS"], "CONSTRUCTION", "construction", [("BRICK", 0.002m), ("THATCH", 0.006m)], "OFT-HOM", 1_000_000, "BRICK", 2050m, "GENERAL_UNDERWRITER");

        // Rates by age band next birthday, invented and not actuarial.
        await Static("product-factory/static/coverage-and-benefit-library", "DEATH", new { type = "BENEFIT", name = "Death (sample)", basis = "FIXED_BENEFIT", measure = "SUM_ASSURED", domain = "LIFE" });
        await Static("product-factory/static/coverage-and-benefit-library", "FUNERAL", new { type = "BENEFIT", name = "Funeral (sample)", basis = "FIXED_BENEFIT", measure = "SUM_ASSURED", domain = "LIFE" });
        await Static("rules/static/eligibility-rule-sets", "OFT-LIFE-ADULT", new { rulesetCode = "OFT-LIFE-ELIG", conditions = "age_next >= 18 AND age_next <= 65", outcome = "ELIGIBLE" });
        (string, decimal)[] funeral = [("18-40", 0.002m), ("41-65", 0.005m)];
        (string, decimal)[] term = [("18-40", 0.0004m), ("41-65", 0.0012m)];
        await RatedProduct("OFT-FUNERAL", "Life", "LIFE", "LIFE-FUNERAL", "Funeral (sample)", "Funeral cover (invented sample)", ["FUNERAL"], "AGE_BAND", "age_band", funeral, "OFT-FUN", 30_000, "18-40", 110m, "LIFE_UNDERWRITER", "OFT-LIFE-ELIG");
        await RatedProduct("OFT-TERM", "Life", "LIFE", "LIFE-RISK", "Life risk (sample)", "Term life (invented sample)", ["DEATH"], "AGE_BAND", "age_band", term, "OFT-TRM", 1_000_000, "18-40", 450m, "LIFE_UNDERWRITER", "OFT-LIFE-ELIG");

        await Static("product-factory/static/coverage-and-benefit-library", "MEDICAL", new { type = "BENEFIT", name = "Medical scheme benefits (sample)", basis = "REIMBURSEMENT", measure = "LIMIT", domain = "HEALTH" });
        // Publication needs a passing test case; a member's contribution itself comes from the scheme's contribution rules.
        await RatedProduct("OFT-HEALTH", "Health", "HEALTH", "HEALTH-MED", "Medical scheme (sample)", "Medical scheme (invented sample)", ["MEDICAL"], "PLAN_OPTION", "plan_option", [("ESS", 0.01m), ("PLUS", 0.02m)], "OFT-HLT", 100_000, "ESS", 1050m, "HEALTH_MEMBERSHIP_ADMINISTRATOR");
    }

    /// <summary>A product with one rating factor, one table, a fee of 50, a test case and its schedule template, then published.</summary>
    private async Task RatedProduct(string product, string domain, string line, string family, string familyName, string name, string[] coverages, string factor, string attribute,
        (string Key, decimal Rate)[] rates, string prefix, int sumInsured, string testKey, decimal expected, string underwriter, string? eligibility = null)
    {
        var version = $"{product} v1";
        await Static("product-factory/static/product-taxonomy", family, new { line, familyCode = family, familyName });
        await Static("rating/static/rating-factor-definitions", factor, new { factorCode = factor, dataType = "CODE", sourceAttribute = attribute });
        await Static("rating/static/rating-tables", $"{prefix}-BASE", new { tableCode = $"{prefix}-BASE", dimensions = new[] { factor }, values = rates.Select(r => new { key = new[] { r.Key }, value = r.Rate }).ToArray(), interpolation = "NONE" });
        await Static("rating/static/premium-parameters", $"{prefix}-P1", new { parameterCode = $"{prefix}-BASE", productVersion = version, value = 1m, unit = "FACTOR" });
        await Static("rating/static/premium-parameters", $"{prefix}-P2", new { parameterCode = "POLICY_FEE", productVersion = version, value = 50m, unit = "AMOUNT" });
        var inputs = new Dictionary<string, object> { ["currency"] = "ZAR", ["frequency"] = "MONTHLY", ["sum_insured"] = sumInsured, [attribute] = testKey };
        await Static("rating/static/rating-test-case-library", $"{prefix}-T1", new { caseId = $"{prefix}-T1", productVersion = version, inputs, expectedPremium = expected, tolerance = 0.01m });
        await Static("product-factory/static/product-template-assignments", $"{prefix}-SCH", new { productCode = product, documentType = "POLICY_SCHEDULE", template = "OFT-TPL-1", language = "en" });
        await Static("tenancy/static/tenant-product-availability", $"AVAIL-{family}", new { tenant = TenantCode, productFamily = family, enabled = true });
        await Static("rules/static/authority-matrices", $"AM-{product}", new { roleCode = underwriter, productCode = product, maxSumAssured = 1_000_000m, decisionTypes = new[] { "STD" } });
        await Publish(product, domain, family, name, coverages, eligibility);
    }

    /// <summary>Registers, validates, simulates and submits a version, wherever an earlier run left it; a second person approves it.</summary>
    private async Task Publish(string product, string domain, string family, string name, string[] coverages, string? eligibility)
    {
        var manager = context.Tenant(TenantId, "oftza-product-manager");
        var v1 = $"/api/v1/product-factory/products/{product}/versions/1";
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (!await manager.Exists(v1))
        {
            await manager.Send(HttpMethod.Post, "/api/v1/product-factory/products", new { productCode = product, domain, familyCode = family, name, coverages, effectiveFrom = today }, key: $"{TenantCode}:product:{product}");
        }

        for (var pass = 0; pass < 7; pass++)
        {
            await context.Settle();
            var version = await manager.Send(HttpMethod.Get, v1);
            var etag = $"\"{version.GetProperty("rowVersion").GetInt64()}\"";
            switch (version.GetProperty("status").GetString())
            {
                case "Draft" when eligibility is not null && version.GetProperty("eligibilityRulesetCode").ValueKind == JsonValueKind.Null:
                    await manager.Send(HttpMethod.Put, v1, new { name, coverages, effectiveFrom = today, eligibilityRulesetCode = eligibility }, ifMatch: etag, idempotent: false);
                    break;
                case "Draft":
                    await manager.Send(HttpMethod.Post, $"{v1}/validate", ifMatch: etag, idempotent: false);
                    break;
                case "Validated":
                    await manager.Send(HttpMethod.Post, $"{v1}/simulations", key: $"{TenantCode}:simulation:{product}");
                    await manager.Send(HttpMethod.Post, $"{v1}/submit", new { reason = "Sample product" }, ifMatch: etag, idempotent: false);
                    break;
                case "Submitted" or "PendingApproval":
                    // PRODUCT_MANAGER's template names this approval but holds no approve function, so a second administrator gives it.
                    foreach (var approval in (await _adminChecker!.Send(HttpMethod.Get, "/api/v1/workflow/approvals?status=Requested")).GetProperty("items").EnumerateArray())
                    {
                        await _adminChecker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: $"\"{approval.GetProperty("rowVersion").GetInt64()}\"", idempotent: false);
                    }

                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// The two seed administrators hold only TENANT_ADMIN's template. Two loader users, a maker and its checker, hold the other modules' non-privileged static data functions for the
    /// length of the seed, and are disabled with their roles revoked as its last step (Ade, 2 November).
    /// </summary>
    private async Task Loaders()
    {
        _adminMaker = _maker;
        _adminChecker = _checker;
        // Loading is over once the tenant has left onboarding and its loaders have ended (a scheme's rule waits for activation). Short of that, they are put back below, by the routes an administrator would use.
        if ((await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/tenancy/tenants/{TenantId}")).GetProperty("status").GetString() != "Onboarding" && !await AnyLoaderLive())
        {
            _loadersDone = true;
            return;
        }

        var functions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in (await context.PlatformMaker.Send(HttpMethod.Get, "/api/v1/identity/role-templates")).EnumerateArray())
        {
            var other = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/{summary.GetProperty("code").GetString()}?tenantId={TenantId}");
            functions.UnionWith(other.GetProperty("functions").EnumerateArray()
                .Where(f => f.GetProperty("code").GetString()!.StartsWith("SD_", StringComparison.Ordinal) && !f.GetProperty("privileged").GetBoolean() && f.GetProperty("audiences").EnumerateArray().Any(a => a.GetString() == "STAFF"))
                .Select(f => f.GetProperty("code").GetString()!));
        }

        functions.UnionWith(["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE"]);
        await Static("identity/static/authentication-and-session-policies", "OFTZA-AUTH", new { policyCode = "OFTZA-AUTH", mfaRequiredRoles = Array.Empty<string>(), sessionTimeout = "PT30M", lockoutAttempts = 5 });
        await Static("identity/static/roles-and-permission-sets", LoaderRole, new { roleCode = LoaderRole });
        await Import("identity/static/role-function-assignments", $"{LoaderRole}:{functions.Order(StringComparer.Ordinal).First()}",
            functions.Order(StringComparer.Ordinal).Select(f => (object)new { roleCode = LoaderRole, functionCode = f, code = $"{LoaderRole}:{f}", changeReason = Reason }).ToList());
        // The templates decide most approvals. These four need one role holding approval and the type's own function, which no template combines (BR-114), so a sample role does.
        string[] approving = ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", "WORKFLOW_REJECT", "PARTY_APPROVE_ENHANCED_DD", "PARTY_OVERRIDE_RISK_CLASS", "PARTY_UNBLOCK_DUE_DILIGENCE", "GENERAL_PROPOSE_NON_RENEWAL"];
        await Static("identity/static/roles-and-permission-sets", "OFTZA_APPROVER", new { roleCode = "OFTZA_APPROVER" });
        await Import("identity/static/role-function-assignments", "OFTZA_APPROVER:WORKFLOW_APPROVALS", approving.Select(f => (object)new { roleCode = "OFTZA_APPROVER", functionCode = f, code = $"OFTZA_APPROVER:{f}", changeReason = Reason }).ToList());
        _templates.Add("OFTZA_APPROVER");
        foreach (var type in new[] { "DUE_DILIGENCE_ENHANCED", "DUE_DILIGENCE_RISK_OVERRIDE", "DUE_DILIGENCE_UNBLOCK", "GENERAL_NON_RENEWAL" })
        {
            await Static("workflow/static/delegation-and-approval-authority", $"AUTH-{type}", new { roleCode = "OFTZA_APPROVER", transactionType = type, limitValue = 100_000m, delegationAllowed = false, currency = "ZAR" });
        }

        foreach (var type in new[] { "STATIC_DATA_T1", "STATIC_DATA_T2" })
        {
            await Static("workflow/static/delegation-and-approval-authority", $"AUTH-LOADER-{type}", new { roleCode = LoaderRole, transactionType = type, limitValue = 1m, delegationAllowed = false });
        }

        // The approvals a role template names need authority in force, or a privileged assignment could wait for ever (WFL-004).
        await Static("workflow/static/delegation-and-approval-authority", "AUTH-PRIVILEGED_ROLE", new { roleCode = "TENANT_ADMIN", transactionType = "PRIVILEGED_ROLE", limitValue = 1m, delegationAllowed = false });
        var maker = await LiveLoader(LoaderMaker);
        var checker = await LiveLoader(LoaderChecker);
        await EnsureUser(maker, LoaderRole);
        await EnsureUser(checker, LoaderRole);
        _adminMaker = _maker;
        _adminChecker = _checker;
        _maker = context.Tenant(TenantId, maker);
        _checker = context.Tenant(TenantId, checker);
        // Each signs in, as a person would: a user still Invited can't be disabled, and on a rerun with nothing left to approve the checker would otherwise never call.
        await _maker.Send(HttpMethod.Get, "/api/v1/workflow/approvals?status=Requested");
        await _checker.Send(HttpMethod.Get, "/api/v1/workflow/approvals?status=Requested");
    }

    private async Task<bool> AnyLoaderLive()
    {
        var users = (await _adminMaker!.Send(HttpMethod.Get, "/api/v1/identity/users?limit=200")).GetProperty("items").EnumerateArray();
        return users.Any(u => u.GetProperty("username").GetString()!.StartsWith("oftza-seed-loader-", StringComparison.Ordinal) && u.GetProperty("status").GetString() != "Disabled");
    }

    private async Task EndLoaders()
    {
        foreach (var name in new[] { LoaderMaker, LoaderChecker }.SelectMany(first => LoaderNames(first).Take(50)))
        {
            if (await User(name) is not { } found)
            {
                continue;
            }

            var id = found.GetProperty("id").GetGuid();
            var user = await _adminMaker!.Send(HttpMethod.Get, $"/api/v1/identity/users/{id}");
            if (user.GetProperty("roleAssignments").EnumerateArray().Any(a => a.GetProperty("status").GetString() == "Active"))
            {
                await _adminMaker.Send(HttpMethod.Post, $"/api/v1/identity/users/{id}/revoke-role", new { roleCode = LoaderRole }, await Version(id), idempotent: false);
            }

            if ((await _adminMaker.Send(HttpMethod.Get, $"/api/v1/identity/users/{id}")).GetProperty("status").GetString() != "Disabled")
            {
                await _adminMaker.Send(HttpMethod.Post, $"/api/v1/identity/users/{id}/disable", new { reason = "The seed has finished loading." }, await Version(id), idempotent: false);
            }
        }
    }

    private async Task<JsonElement?> User(string name)
    {
        foreach (var user in (await (_adminMaker ?? _maker).Send(HttpMethod.Get, "/api/v1/identity/users?limit=200")).GetProperty("items").EnumerateArray())
        {
            if (user.GetProperty("username").GetString() == name)
            {
                return user;
            }
        }

        return null;
    }

    private async Task<string> Version(Guid user) => $"\"{(await (_adminMaker ?? _maker).Send(HttpMethod.Get, $"/api/v1/identity/users/{user}")).GetProperty("rowVersion").GetInt64()}\"";

    /// <summary>A user with one role, picked up from wherever an earlier run stopped. A privileged role waits for a second person, as in real use: the second administrator approves it.</summary>
    /// <summary>The loader to use: this name, or if that user is disabled, the next unused one. Disabling is final (IAM-005: no route or transition leads back), so a disabled loader is replaced, never revived.</summary>
    private async Task<string> LiveLoader(string name)
    {
        foreach (var candidate in LoaderNames(name))
        {
            if (await User(candidate) is not { } user || user.GetProperty("status").GetString() != "Disabled")
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unreachable: the names don't end.");
    }

    private static IEnumerable<string> LoaderNames(string name)
    {
        yield return name;
        for (var n = 2; ; n++)
        {
            yield return $"{name}-{n}";
        }
    }

    private async Task EnsureUser(string name, string role)
    {
        var admin = _adminMaker ?? _maker;
        var checker = _adminChecker ?? _checker;
        var user = (await User(name))?.GetProperty("id").GetGuid()
            ?? (await admin.Send(HttpMethod.Post, "/api/v1/identity/users", Person(name, $"Sample {role.Replace('_', ' ').ToLowerInvariant()}"), key: $"{TenantCode}:user:{name}")).GetProperty("id").GetGuid();
        async Task<List<JsonElement>> Assignments() => (await admin.Send(HttpMethod.Get, $"/api/v1/identity/users/{user}")).GetProperty("roleAssignments").EnumerateArray().Where(a => a.GetProperty("roleCode").GetString() == role).ToList();
        var assignments = await Assignments();
        // A revoked assignment stays on record, so it's the live ones that count: an administrator assigns the role again, with the same approvals as the first time.
        if (!assignments.Any(a => a.GetProperty("status").GetString() is "Active" or "PendingApproval"))
        {
            await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/assign-role", new { roleCode = role }, await Version(user), idempotent: false);
            await context.Settle();
            assignments = await Assignments();
        }

        foreach (var assignment in assignments.Where(a => a.GetProperty("status").GetString() == "PendingApproval"))
        {
            await context.Settle();
            await Approve(checker, assignment.GetProperty("id").GetGuid());
            await context.Settle();
        }
    }

    private async Task<Guid> Tenant()
    {
        var tenants = await context.PlatformMaker.Send(HttpMethod.Get, "/api/v1/tenancy/tenants");
        foreach (var existing in tenants.GetProperty("items").EnumerateArray())
        {
            if (existing.GetProperty("tenantCode").GetString() == TenantCode)
            {
                return existing.GetProperty("id").GetGuid();
            }
        }

        var created = await context.PlatformMaker.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new
        {
            tenantCode = TenantCode, legalName = "OFT Insurance South Africa (sample)", isolationProfile = "A", timeZone = "Africa/Johannesburg", sample = true,
        }, key: $"{TenantCode}:tenant");
        return created.GetProperty("id").GetGuid();
    }

    /// <summary>Two platform administrators provide the first two, as for any tenant (OQ-067).</summary>
    private async Task FirstAdministrators()
    {
        var requests = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/tenant-administrators?tenantId={TenantId}");
        var existing = (requests.ValueKind == JsonValueKind.Array ? requests : requests.GetProperty("items")).EnumerateArray().FirstOrDefault();
        Guid request;
        if (existing.ValueKind != JsonValueKind.Undefined)
        {
            if (existing.GetProperty("status").GetString() == "Provisioned")
            {
                return;
            }

            request = existing.GetProperty("id").GetGuid();
        }
        else
        {
            request = (await context.PlatformMaker.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators",
                new { tenantId = TenantId, administrators = new[] { Person(FirstAdministrator, FirstAdministrator), Person(SecondAdministrator, SecondAdministrator) }, functions = await AdministratorFunctions() }, key: $"{TenantCode}:first-administrators")).GetProperty("id").GetGuid();
        }

        await context.Settle();
        await Approve(context.PlatformChecker, request);
        await context.Settle();
    }

    /// <summary>A user as the seed makes one: in INSYS's own realm, Identity creates the Keycloak user and its subject; otherwise the subject is the name, as the development key signs it.</summary>
    private object Person(string name, string displayName) => context.OwnRealm
        ? new { username = name, displayName, idpCode = "KEYCLOAK", externalSubject = (string?)null, email = (string?)$"{name}@example.test", mfaEnrolled = true }
        : new { username = name, displayName, idpCode = "OIDC", externalSubject = (string?)name, email = (string?)null, mfaEnrolled = true };

    /// <summary>Exactly TENANT_ADMIN's template, with what this tenant can hold.</summary>
    private async Task<string[]> AdministratorFunctions()
    {
        var template = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/TENANT_ADMIN?tenantId={TenantId}");
        return [.. template.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).Order(StringComparer.Ordinal)];
    }

    private Task Locale() => _loadersDone ? Task.CompletedTask : Static("tenancy/static/locale-defaults", "LOCALE", new
    {
        tenant = TenantCode, baseCurrency = "ZAR", languages = new[] { "en", "af", "zu", "xh" }, defaultLanguage = "en", timeZone = "Africa/Johannesburg", country = "ZA",
    });

    /// <summary>Every signed-off role template for tenant users (OQ-111), with the functions available to this tenant. The template's privileged functions are only those it names.</summary>
    private async Task Roles()
    {
        foreach (var summary in (await context.PlatformMaker.Send(HttpMethod.Get, "/api/v1/identity/role-templates")).EnumerateArray())
        {
            var template = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/{summary.GetProperty("code").GetString()}?tenantId={TenantId}");
            var code = template.GetProperty("code").GetString()!;
            code = code[..Math.Min(code.Length, 30)];
            if (template.GetProperty("actorType").GetString() == "PLATFORM" || code == "TENANT_ADMIN")
            {
                continue;
            }

            _templates.Add(code);
            await Static("identity/static/roles-and-permission-sets", code, new { roleCode = code });
            var functions = template.GetProperty("functions").EnumerateArray().Where(f => !f.TryGetProperty("available", out var a) || a.ValueKind != JsonValueKind.False).Select(f => f.GetProperty("code").GetString()!).ToList();
            await Import("identity/static/role-function-assignments", $"{code}:{functions[0]}", functions.Select(f => (object)new { roleCode = code, functionCode = f, code = $"{code}:{f}", changeReason = Reason }).ToList());
        }
    }

    /// <summary>An import by the book (BR-016): all its lines drafted, submitted as one change, approved in Workflow by the checker, then put in force together.</summary>
    private async Task Import(string route, string firstCode, IReadOnlyList<object> lines)
    {
        var path = $"/api/v1/{route}";
        var first = $"{path}/{Uri.EscapeDataString(firstCode)}/versions";
        if ((await _maker.Send(HttpMethod.Get, first)).GetArrayLength() == 0)
        {
            await _maker.Send(HttpMethod.Post, $"{path}/imports", new { fileRef = "oftza-seed", lines }, key: $"{TenantCode}:{route}:import:{firstCode}");
        }

        for (var pass = 0; pass < 6; pass++)
        {
            await context.Settle();
            var state = (await _maker.Send(HttpMethod.Get, first))[0];
            var batch = state.GetProperty("importBatchId").GetGuid();
            switch (state.GetProperty("recordStatus").GetString())
            {
                case "DRAFT":
                    await _maker.Send(HttpMethod.Post, $"{path}/imports/{batch}/submit", idempotent: false);
                    break;
                case "SUBMITTED" or "PENDING_APPROVAL":
                    await Approve(_checker, state.GetProperty("changeRequestId").GetGuid());
                    break;
                case "APPROVED" or "VALIDATED":
                    await _maker.Send(HttpMethod.Post, $"{path}/imports/{batch}/activate", idempotent: false);
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Approves what waits on this record, if anything still does.</summary>
    private static async Task Approve(Persona checker, Guid businessRef)
    {
        foreach (var approval in (await checker.Send(HttpMethod.Get, $"/api/v1/workflow/approvals?status=Requested&businessRefId={businessRef}")).GetProperty("items").EnumerateArray())
        {
            await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: $"\"{approval.GetProperty("rowVersion").GetInt64()}\"", idempotent: false);
        }
    }

    /// <summary>One user per role template, named for it, so a demo can sign in as any role.</summary>
    private async Task Personas()
    {
        foreach (var template in _templates)
        {
            await EnsureUser("oftza-" + template.ToLowerInvariant().Replace('_', '-'), template);
        }
    }

    /// <summary>What every onboarding step's readiness asks of a tenant. The sample values are for the business to confirm (OQ-112), in rand.</summary>
    private async Task Configuration()
    {
        if (_loadersDone)
        {
            return;
        }

        await Static("tenancy/static/organisation-structure", "OFTZA-ROOT", new { nodeCode = "OFTZA-ROOT", type = "LEGAL_ENTITY", name = "OFT Insurance South Africa (sample)" });
        await Static("integration/static/channel-definitions", "CH-STF", new { channelCode = "STF", audience = "STAFF", name = "Back office", clientIds = new[] { "insys-web", KeycloakSignIn.ClientId } });
        await Static("finance/static/chart-of-accounts-subledger", "ACC-1100", new { accountCode = "1100", name = "Premium receivable (sample)", type = "ASSET", glAccount = "1100", status = "ACTIVE" });
        await Static("workflow/static/business-calendars", "CAL-ZA", new { calendarCode = "CAL-ZA", workingDays = new[] { "MON", "TUE", "WED", "THU", "FRI" }, hours = "08:00-17:00", holidayCalendar = "ZA", timeZone = "Africa/Johannesburg" });
        await Static("workflow/static/task-types-and-work-queues", "OFTZA-TASKS", new { queueCode = "OFTZA-TASKS", taskTypes = new[] { "REVIEW" }, assignmentRule = "MANUAL_PULL" });
        await Static("workflow/static/workflow-definitions", "COMPLAINT", new { processCode = "COMPLAINT", states = new[] { "HANDLE", "DONE" }, transitions = new[] { new { from = "HANDLE", to = "DONE" } }, domainValidation = "" });
        await Static("workflow/static/case-types", "COMPLAINT", new { caseTypeCode = "COMPLAINT", family = "COMPLAINT", processCode = "COMPLAINT", linkTypes = new[] { "POLICY", "CLAIM" }, requiresCategory = true, reviewCaseTypeCode = "COMPLAINT_REVIEW", appealRoute = "PPR_NFO", ombudSchemeCodes = Array.Empty<string>(), portalAudiences = new[] { "CUSTOMER", "BROKER" } });
        foreach (var (step, duration) in new[] { ("ACKNOWLEDGE", "P2D"), ("RESOLVE", "P42D") })
        {
            await Static("workflow/static/case-timelines", $"COMPLAINT-{step}", new { caseTypeCode = "COMPLAINT", step, duration, basis = "CALENDAR", extensionAllowed = false });
        }

        await Static("billing/static/billing-frequencies", "MONTHLY", new { label = "Monthly", periodsPerYear = 12 });
        await Static("billing/static/collection-methods", "DEBIT_ORDER", new { methodCode = "DEBIT_ORDER", paymentMethod = "DEBIT_ORDER", leadTime = "P2D" });
        await Static("billing/static/document-numbering-schemes", "INV", new { documentType = "INVOICE", prefix = "INV", sequenceLength = 8, resetRule = "NEVER" });
        await Static("billing/static/arrears-ageing-buckets", "B1", new { bucketCode = "B1", daysFrom = 1, daysTo = 30, action = "REMINDER" });
        await Static("billing/static/arrears-ageing-buckets", "B2", new { bucketCode = "B2", daysFrom = 31, daysTo = 9999, action = "LAPSE" });
        foreach (var (method, direction) in new[] { ("DEBIT_ORDER", "COLLECT"), ("EFT", "PAY") })
        {
            await Static("payments/static/payment-methods", method, new { methodCode = method, direction, currencies = new[] { "ZAR" }, countries = new[] { "ZA" } });
        }

        await Static("payments/static/payment-provider-and-bank-register", "SIMULATOR", new { providerCode = "SIMULATOR", type = "PAYMENT_GATEWAY", supportedMethods = new[] { "DEBIT_ORDER", "EFT" }, credentialRef = "vault:simulator" });
        await Static("payments/static/payment-status-mappings", "SIM-PAID", new { providerCode = "SIMULATOR", providerStatus = "PAID", platformStatus = "SUCCEEDED", final = true });
        await Static("claims-framework/static/claim-numbering-schemes", "CLM", new { domain = "GENERAL", prefix = "CLM", sequenceLength = 8 });
        await Static("claims-framework/static/reserve-types", "INDEMNITY", new { category = "INDEMNITY", postingRule = "CLAIM_RESERVE_UP" });
        foreach (var account in new[] { ("1100", "ASSET", "Premium receivable"), ("1200", "ASSET", "Bank"), ("2100", "LIABILITY", "Claims reserve"), ("4000", "INCOME", "Premium"), ("5000", "EXPENSE", "Claims") })
        {
            if (account.Item1 != "1100")
            {
                await Static("finance/static/chart-of-accounts-subledger", $"ACC-{account.Item1}", new { accountCode = account.Item1, name = account.Item3 + " (sample)", type = account.Item2, glAccount = account.Item1, status = "ACTIVE" });
            }
        }

        var rule = 0;
        foreach (var (type, debit, credit) in new[]
                 {
                     ("PREMIUM_DUE", "1100", "4000"), ("PAYMENT_RECEIVED", "1200", "1100"), ("REFUND_PAID", "1100", "1200"), ("CLAIM_RESERVE_UP", "5000", "2100"), ("CLAIM_RESERVE_DOWN", "2100", "5000"),
                     ("CLAIM_PAID", "2100", "1200"), ("CLAIM_PAID_REVERSED", "1200", "2100"), ("RECOVERY_RAISED", "1100", "2100"), ("RECOVERY_OFFSET", "1200", "1100"),
                 })
        {
            await Static("finance/static/posting-rules", $"PR-{++rule}", new { ruleCode = $"PR-{rule}", transactionType = type, debitAccount = debit, creditAccount = credit });
        }

        foreach (var (type, role, limit) in new[] { ("CLAIM_SETTLEMENT", "CLAIMS_MANAGER", 250_000m), ("LIFE_CLAIM", "CLAIMS_MANAGER", 500_000m), ("CLAIM_PAYMENT", "FINANCE_OFFICER", 100_000m), ("REFUND", "FINANCE_OFFICER", 25_000m), ("PAYOUT_RELEASE", "FINANCE_MANAGER", 5_000_000m), ("COMMISSION_STATEMENT", "FINANCE_MANAGER", 5_000_000m) })
        {
            await Static("workflow/static/delegation-and-approval-authority", $"AUTH-{type}-{role}", new { roleCode = role, transactionType = type, limitValue = limit, delegationAllowed = false, currency = "ZAR" });
        }

        await Static("party/static/due-diligence-settings", "GATES", new { gatesEnabled = true, adverseMediaEnabled = false });
        foreach (var (partner, type) in new[] { ("OFT-SCR", "SCREENING"), ("OFT-IDV", "IDENTITY_VERIFICATION") })
        {
            await Static("integration/static/partner-register", partner, new { partnerCode = partner, partnerType = type, legalName = $"{type} (stub)", status = "ACTIVE" });
        }
        foreach (var type in new[] { "CASE_ACKNOWLEDGED", "CASE_UPDATE", "CASE_DECISION" })
        {
            await Static("communications/static/notification-types", $"NT-{type}", new { typeCode = type, category = "SERVICE", channels = new[] { "EMAIL" }, portalVisible = true });
            await Static("communications/static/notification-templates", $"NTT-{type}", new { typeCode = type, channel = "EMAIL", language = "en", subject = "Case {case_number}", body = "Case {case_number} (sample)." });
        }

        await Static("finance/static/gl-export-layouts", "GL", new { layoutCode = "GL", targetSystem = "SAMPLE", format = "CSV", schedule = "MONTHLY" });
        foreach (var point in new[] { "QUOTE", "ISSUE", "RENEWAL", "CLAIM" })
        {
            await Static("integration/static/disclosure-and-notice-texts", $"DISC-{point}", new { noticeType = "POLICY_TERMS", jurisdiction = "ZA", language = "en", text = "Invented sample terms, not legal wording.", textVersion = 1, displayPoint = point });
        }
    }

    /// <summary>A static row by the book: a draft, submitted, approved by the checker, then put in force. A row already there is left as it is.</summary>
    private Task Static(string route, string code, object fields) => Static(_maker, _checker, route, code, fields);

    private async Task Static(Persona maker, Persona checker, string route, string code, object fields)
    {
        var path = $"/api/v1/{route}";
        var row = $"{path}/{Uri.EscapeDataString(code)}/versions";
        if (!await maker.Exists(row) || (await maker.Send(HttpMethod.Get, row)).GetArrayLength() == 0)
        {
            var body = JsonSerializer.SerializeToElement(fields, JsonSerializerOptions.Web).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
            body["code"] = code;
            body["changeReason"] = Reason;
            await maker.Send(HttpMethod.Post, path, body, key: $"{TenantCode}:{route}:{code}");
        }

        // Each pass moves the newest version on from wherever an earlier run left it: draft, submitted, approved. A tier 3 row moves on by itself.
        for (var pass = 0; pass < 6; pass++)
        {
            await context.Settle();
            var state = (await maker.Send(HttpMethod.Get, row))[0];
            var version = $"{row}/{state.GetProperty("version").GetInt32()}";
            var etag = $"\"{state.GetProperty("rowVersion").GetInt64()}\"";
            switch (state.GetProperty("recordStatus").GetString())
            {
                case "DRAFT":
                    await maker.Send(HttpMethod.Post, $"{version}/submit", ifMatch: etag, idempotent: false);
                    break;
                case "SUBMITTED" or "PENDING_APPROVAL":
                    await checker.Send(HttpMethod.Post, $"{version}/approve", idempotent: false);
                    break;
                case "APPROVED" or "VALIDATED":
                    await maker.Send(HttpMethod.Post, $"{version}/activate", ifMatch: etag, idempotent: false);
                    break;
                default:
                    return;
            }
        }
    }
}
