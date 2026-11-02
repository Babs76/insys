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
public sealed class OftzaSeed(SeedContext context)
{
    public const string TenantCode = "OFTZA";
    public const string FirstAdministrator = "oftza-admin-1";
    public const string SecondAdministrator = "oftza-admin-2";

    private const string Reason = "OFTZA sample data (invented)";
    private Persona _maker = null!;
    private Persona _checker = null!;
    private readonly List<string> _templates = [];

    public Guid TenantId { get; private set; }

    public async Task Run()
    {
        TenantId = await Tenant();
        foreach (var feature in new[] { "LIFE", "GENERAL", "HEALTH", "REINSURANCE", "BROKER_PORTAL", "PROVIDER_PORTAL", "CUSTOMER_PORTAL" })
        {
            await Static(context.PlatformMaker, context.PlatformChecker, "tenancy/static/feature-entitlements/global", $"{TenantCode}:{feature}", new { tenant = TenantCode, feature, enabled = true });
        }

        await FirstAdministrators();
        _maker = context.Tenant(TenantId, FirstAdministrator);
        _checker = context.Tenant(TenantId, SecondAdministrator);
        await Locale();
        await Roles();
        await Configuration();
        await Personas();
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
        });
        return created.GetProperty("id").GetGuid();
    }

    /// <summary>Two platform administrators provide the first two, as for any tenant (OQ-067).</summary>
    private async Task FirstAdministrators()
    {
        var requests = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/tenant-administrators?tenantId={TenantId}");
        if ((requests.ValueKind == JsonValueKind.Array ? requests : requests.GetProperty("items")).GetArrayLength() > 0)
        {
            return;
        }

        object Person(string name) => new { username = name, displayName = name, idpCode = "OIDC", externalSubject = name, mfaEnrolled = true };
        var request = await context.PlatformMaker.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators",
            new { tenantId = TenantId, administrators = new[] { Person(FirstAdministrator), Person(SecondAdministrator) }, functions = await AdministratorFunctions() });
        await context.Settle();
        var approvals = await context.PlatformChecker.Send(HttpMethod.Get, $"/api/v1/workflow/approvals?businessRefId={request.GetProperty("id").GetGuid()}");
        var approval = approvals.GetProperty("items")[0];
        await context.PlatformChecker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: $"\"{approval.GetProperty("rowVersion").GetInt64()}\"");
        await context.Settle();
    }

    /// <summary>TENANT_ADMIN's template, and every module's static data functions that aren't privileged, as these two load the tenant's configuration: the template's own privileged ones are all it holds (Ade, 2 November).</summary>
    private async Task<string[]> AdministratorFunctions()
    {
        var template = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/TENANT_ADMIN?tenantId={TenantId}");
        var functions = template.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var summary in (await context.PlatformMaker.Send(HttpMethod.Get, "/api/v1/identity/role-templates")).EnumerateArray())
        {
            var other = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/{summary.GetProperty("code").GetString()}?tenantId={TenantId}");
            functions.UnionWith(other.GetProperty("functions").EnumerateArray()
                .Where(f => f.GetProperty("code").GetString()!.StartsWith("SD_", StringComparison.Ordinal) && !f.GetProperty("privileged").GetBoolean() && f.GetProperty("audiences").EnumerateArray().Any(a => a.GetString() == "STAFF"))
                .Select(f => f.GetProperty("code").GetString()!));
        }

        return [.. functions.Order(StringComparer.Ordinal)];
    }

    private Task Locale() => Static("tenancy/static/locale-defaults", "LOCALE", new
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
        if ((await _maker.Send(HttpMethod.Get, $"{path}/{Uri.EscapeDataString(firstCode)}/versions")).GetArrayLength() > 0)
        {
            return;
        }

        var batch = (await _maker.Send(HttpMethod.Post, $"{path}/imports", new { fileRef = "oftza-seed", lines })).GetProperty("batchId").GetGuid();
        var change = (await _maker.Send(HttpMethod.Post, $"{path}/imports/{batch}/submit")).GetProperty("changeRequestId").GetGuid();
        await context.Settle();
        var approval = (await _checker.Send(HttpMethod.Get, $"/api/v1/workflow/approvals?businessRefId={change}")).GetProperty("items")[0];
        await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval.GetProperty("id").GetGuid()}/approve", ifMatch: $"\"{approval.GetProperty("rowVersion").GetInt64()}\"");
        await context.Settle();
        await _maker.Send(HttpMethod.Post, $"{path}/imports/{batch}/activate");
        await context.Settle();
    }

    /// <summary>One user per role template, named for it, so a demo can sign in as any role.</summary>
    private async Task Personas()
    {
        var users = await _maker.Send(HttpMethod.Get, "/api/v1/identity/users?limit=200");
        var known = users.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("username").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var template in _templates)
        {
            var name = "oftza-" + template.ToLowerInvariant().Replace('_', '-');
            if (known.Contains(name))
            {
                continue;
            }

            var user = await _maker.Send(HttpMethod.Post, "/api/v1/identity/users", new { username = name, displayName = $"Sample {template}", idpCode = "OIDC", externalSubject = name, mfaEnrolled = true });
            await _maker.Send(HttpMethod.Post, $"/api/v1/identity/users/{user.GetProperty("id").GetGuid()}/assign-role",
                new { roleCode = template, validFrom = "2026-01-01", validTo = (string?)null, scopeProducts = (string[]?)null, scopeBranches = (string[]?)null },
                ifMatch: $"\"{(await _maker.Send(HttpMethod.Get, $"/api/v1/identity/users/{user.GetProperty("id").GetGuid()}")).GetProperty("rowVersion").GetInt64()}\"");
        }
    }

    /// <summary>What every onboarding step's readiness asks of a tenant. The sample values are for the business to confirm (OQ-112), in rand.</summary>
    private async Task Configuration()
    {
        await Static("tenancy/static/organisation-structure", "OFTZA-ROOT", new { nodeCode = "OFTZA-ROOT", type = "LEGAL_ENTITY", name = "OFT Insurance South Africa (sample)" });
        await Static("identity/static/authentication-and-session-policies", "OFTZA-AUTH", new { policyCode = "OFTZA-AUTH", mfaRequiredRoles = Array.Empty<string>(), sessionTimeout = "PT30M", lockoutAttempts = 5 });
        await Static("integration/static/channel-definitions", "CH-STF", new { channelCode = "STF", audience = "STAFF", name = "Back office", clientIds = new[] { "insys-web" } });
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
        foreach (var (method, direction) in new[] { ("COLLECT", "COLLECT"), ("PAY", "PAY") })
        {
            await Static("payments/static/payment-methods", method, new { methodCode = method, direction, currencies = new[] { "ZAR" }, countries = new[] { "ZA" } });
        }

        await Static("payments/static/payment-provider-and-bank-register", "SIMULATOR", new { providerCode = "SIMULATOR", type = "PAYMENT_GATEWAY", supportedMethods = new[] { "COLLECT", "PAY" }, credentialRef = "vault:simulator" });
        await Static("payments/static/payment-status-mappings", "SIM-PAID", new { providerCode = "SIMULATOR", providerStatus = "PAID", platformStatus = "SUCCEEDED", final = true });
        await Static("claims-framework/static/claim-numbering-schemes", "CLM", new { domain = "GENERAL", prefix = "CLM", sequenceLength = 8 });
        await Static("claims-framework/static/reserve-types", "INDEMNITY", new { category = "INDEMNITY", postingRule = "CLAIM_RESERVE_UP" });
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
        if (await maker.Exists($"{path}/{code}/versions") && (await maker.Send(HttpMethod.Get, $"{path}/{code}/versions")).GetArrayLength() > 0)
        {
            return;
        }

        var body = JsonSerializer.SerializeToElement(fields, JsonSerializerOptions.Web).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        body["code"] = code;
        body["changeReason"] = Reason;
        var draft = await maker.Send(HttpMethod.Post, path, body);
        var version = $"{path}/{code}/versions/{draft.GetProperty("version").GetInt32()}";
        var submitted = await maker.Send(HttpMethod.Post, $"{version}/submit", ifMatch: $"\"{draft.GetProperty("rowVersion").GetInt64()}\"");
        await context.Settle();
        var state = submitted;
        if (submitted.GetProperty("recordStatus").GetString() is "SUBMITTED" or "PENDING_APPROVAL")
        {
            await checker.Send(HttpMethod.Post, $"{version}/approve");
            await context.Settle();
            state = (await maker.Send(HttpMethod.Get, $"{path}/{code}/versions"))[0];
        }

        await maker.Send(HttpMethod.Post, $"{version}/activate", ifMatch: $"\"{state.GetProperty("rowVersion").GetInt64()}\"");
        await context.Settle();
    }
}
