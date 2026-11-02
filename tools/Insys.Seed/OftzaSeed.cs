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
        if (submitted.GetProperty("recordStatus").GetString() == "SUBMITTED")
        {
            await checker.Send(HttpMethod.Post, $"{version}/approve");
            await context.Settle();
            state = (await maker.Send(HttpMethod.Get, $"{path}/{code}/versions"))[0];
        }

        await maker.Send(HttpMethod.Post, $"{version}/activate", ifMatch: $"\"{state.GetProperty("rowVersion").GetInt64()}\"");
        await context.Settle();
    }
}
