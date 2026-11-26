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
using System.Net.Http;
using System.Text.Json;

namespace Insys.Seed;

/// <summary>One tenant's line in the upgrade check: the approval types no role can decide, or why the tenant wasn't checked.</summary>
/// <param name="Undecidable">APPROVAL_AUTHORITY's listed types, empty when every one has a decider; null when the tenant wasn't checked.</param>
public sealed record UpgradeCheckFinding(Guid TenantId, string TenantCode, string Status, IReadOnlyList<string>? Undecidable, string? NotCheckedBecause = null);

/// <summary>
/// The upgrade check (Ade, 26 November 2026): "On an existing tenant, it lists every approval type with no role holding both the authority and the
/// function to decide it. The upgrade stops if the list isn't empty." It adds no rule of its own: as a platform operator it lists the tenants
/// (ListTenants) and reads each one's readiness (GetTenantReadiness), whose APPROVAL_AUTHORITY check (Workflow's ApprovalAuthorityReadiness) is that rule,
/// computed by the API that answers, so by the version that knows the new approval types. Tenant codes and approval types only; no personal data.
/// Which tenants: ACTIVE and SUSPENDED ones (a suspended tenant is reactivated without onboarding again, with what it had); an ONBOARDING one once its
/// SHARED_OPERATIONAL_SETUP step is completed (before that, completing the step runs the same check); never a TERMINATED one or the operator tenant (OQ-205).
/// </summary>
public static class UpgradeCheck
{
    public const string Step = "SHARED_OPERATIONAL_SETUP";
    public const string Check = "APPROVAL_AUTHORITY";

    /// <summary>The exit codes: every tenant checked has a decider for each type; some tenant's list isn't empty; the arguments; the API couldn't be read.</summary>
    public const int Clear = 0, Listed = 1, Usage = 2, Unread = 3;

    public static async Task<IReadOnlyList<UpgradeCheckFinding>> Run(Persona platform, Guid operatorTenant)
    {
        var findings = new List<UpgradeCheckFinding>();
        string? cursor = null;
        do
        {
            var page = await platform.Send(HttpMethod.Get, "/api/v1/tenancy/tenants?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            foreach (var tenant in page.GetProperty("items").EnumerateArray())
            {
                findings.Add(await Tenant(platform, tenant, operatorTenant));
            }

            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return findings;
    }

    private static async Task<UpgradeCheckFinding> Tenant(Persona platform, JsonElement tenant, Guid operatorTenant)
    {
        var id = tenant.GetProperty("id").GetGuid();
        var code = tenant.GetProperty("tenantCode").GetString()!;
        var status = tenant.GetProperty("status").GetString()!;
        // TODO(spec): OQ-205. Readiness counts every approval type with no feature as raisable in the operator tenant too, which ADR-024 keeps out of business.
        if (id == operatorTenant)
        {
            return new(id, code, status, null, "the operator tenant: business modules refuse it (ADR-024); its approvals are the bootstrap's (OQ-205)");
        }

        if (string.Equals(status, "Terminated", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, code, status, null, "terminated");
        }

        return Finding(id, code, status, await platform.Send(HttpMethod.Get, $"/api/v1/tenancy/tenants/{id}/readiness"));
    }

    /// <summary>The tenant's line from its readiness view: APPROVAL_AUTHORITY's missing types, or not checked while onboarding before its step.</summary>
    public static UpgradeCheckFinding Finding(Guid id, string code, string status, JsonElement readiness)
    {
        var step = readiness.GetProperty("steps").EnumerateArray().SingleOrDefault(s => s.GetProperty("step").GetString() == Step);
        if (step.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{code}'s readiness has no {Step} step");
        }

        if (string.Equals(status, "Onboarding", StringComparison.OrdinalIgnoreCase) && !step.GetProperty("completed").GetBoolean())
        {
            return new(id, code, status, null, $"onboarding, before {Step}: completing that step runs this check");
        }

        var check = step.GetProperty("checks").EnumerateArray().SingleOrDefault(c => c.GetProperty("code").GetString() == Check);
        if (check.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{code}'s readiness has no {Check} check");
        }

        return new(id, code, status, [.. check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).Order(StringComparer.Ordinal)]);
    }

    /// <summary>One line per tenant, then the outcome; returns <see cref="Listed"/> when any checked tenant's list isn't empty, else <see cref="Clear"/>.</summary>
    public static int Report(IReadOnlyList<UpgradeCheckFinding> findings, TextWriter output)
    {
        foreach (var finding in findings)
        {
            output.WriteLine(finding.Undecidable is null
                ? $"{finding.TenantCode} {finding.Status}: not checked ({finding.NotCheckedBecause})"
                : $"{finding.TenantCode} {finding.Status}: {(finding.Undecidable.Count == 0 ? "none" : string.Join(", ", finding.Undecidable))}");
        }

        var checkedTenants = findings.Count(f => f.Undecidable is not null);
        var listed = findings.Count(f => f.Undecidable is { Count: > 0 });
        output.WriteLine(listed == 0
            ? $"upgrade-check: clear: in each of the {checkedTenants} tenants checked, every approval type it can raise has a role holding the authority and the function to decide it"
            : $"upgrade-check: listed: {listed} of the {checkedTenants} tenants checked have approval types no role can decide");
        return listed == 0 ? Clear : Listed;
    }

    /// <summary>The check's run as the tool's mode: the report on <paramref name="output"/>, and why it couldn't be read on <paramref name="error"/> (exit <see cref="Unread"/>).</summary>
    public static async Task<int> RunAndReport(Persona platform, Guid operatorTenant, TextWriter output, TextWriter error)
    {
        IReadOnlyList<UpgradeCheckFinding> findings;
        try
        {
            findings = await Run(platform, operatorTenant);
        }
        catch (Exception e) when (e is SeedRefusedException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // The API's refusal (a problem's code and title), or what was missing from its answer: never a token. An unread list isn't an empty one.
            error.WriteLine($"upgrade-check: unread: {e.Message}");
            return Unread;
        }

        return Report(findings, output);
    }
}
