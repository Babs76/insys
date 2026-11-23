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
/// Two demonstration roles for the OFTZA sample only (Ade, 23 November 2026), made with the Preview's accounts (--accounts true) and never in a
/// template or any real tenant's setup. DEMO_MAKER holds every staff function OFTZA can hold except the release or verify side of each
/// segregation-of-duties baseline rule; DEMO_CHECKER every one except the draft, build or add side. Each holds one side of each rule, so the SoD
/// grant guard and the baseline let them through; each has approval authority for every approval type but a legal hold's release, so either can approve the other's work,
/// and maker-checker still stops anyone approving their own. demouser01 and demouser02 hold one each (PreviewAccounts).
/// </summary>
public sealed partial class OftzaSeed
{
    public const string DemoMaker = "DEMO_MAKER";
    public const string DemoChecker = "DEMO_CHECKER";

    /// <summary>
    /// The approval types OFTZA can raise: Workflow's list (the approvalType enum of docs/api/openapi-core.json at ca1bf2b), without
    /// TENANT_ADMINISTRATOR, which only the operator tenant raises, and without LEGAL_HOLD_RELEASE, which only the compliance officer approves
    /// (Ade, 25 October 2026; kept so for the demo roles, Ade, 23 November 2026, option (a)): 26 types.
    /// </summary>
    public static readonly IReadOnlyList<string> DemoApprovalTypes =
    [
        "BANK_ACCOUNT_VERIFICATION", "CASE_REDRESS", "CLAIM_PAYMENT", "CLAIM_SETTLEMENT", "COMMISSION_STATEMENT", "DUE_DILIGENCE_ENHANCED", "DUE_DILIGENCE_RISK_OVERRIDE",
        "DUE_DILIGENCE_UNBLOCK", "ENDORSEMENT", "EXTERNAL_SUBJECT_LINK", "GENERAL_NON_RENEWAL", "LIFE_CLAIM", "LIFE_UNDERWRITING",
        "PARTY_MATCH_RESOLUTION", "PAYOUT_RELEASE", "PERIOD_REOPEN", "PRIVILEGED_ROLE", "PRODUCT_PUBLICATION", "PROVIDER_CREDIT_NOTE", "PROVIDER_PAYMENT_RUN",
        "PROVIDER_RECOVERY_FINDING", "PROVIDER_RECOVERY_WRITE_OFF", "REFUND", "STATIC_DATA_T1", "STATIC_DATA_T2", "UNKNOWN_PAYMENT_RESOLUTION",
    ];

    /// <summary>The types whose authority rows the seed already writes without a currency (no amount is approved).</summary>
    private static readonly HashSet<string> NoAmount = new(["STATIC_DATA_T1", "STATIC_DATA_T2", "PRIVILEGED_ROLE"], StringComparer.Ordinal);

    /// <summary>AI-1's run needs its review on the same role (FunctionRequirements, OQ-190): a run without it is left out.</summary>
    private static readonly (string Function, string Needs)[] Requirements = [("ASSIST_PRODUCT_DRAFT_RUN", "ASSIST_PRODUCT_DRAFT_REVIEW")];

    private readonly Dictionary<string, List<string>> _demoFunctions = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _demoLeftOut = new(StringComparer.Ordinal);

    /// <summary>Each demo role's functions, as DemoRoles() granted them.</summary>
    public IReadOnlyDictionary<string, List<string>> DemoFunctions => _demoFunctions;

    /// <summary>Staff functions in the catalogue left off both demo roles, with why.</summary>
    public IReadOnlyDictionary<string, string> DemoLeftOut => _demoLeftOut;

    /// <summary>
    /// The two roles, granted through the normal static data import (BR-016), so the SoD grant guard and the baseline apply. Their functions come from
    /// the function catalogue as the seed reads it for the loader role: every template's functions, read for OFTZA, staff ones only (no portal, platform
    /// or job function), and only those OFTZA can hold now (FunctionHolding: not disabled, its feature entitled). Privileged functions are included,
    /// so assigning either role takes a PRIVILEGED_ROLE approval by a second administrator (EnsureUser).
    /// </summary>
    private async Task DemoRoles()
    {
        var staff = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var summary in (await context.PlatformMaker.Send(HttpMethod.Get, "/api/v1/identity/role-templates")).EnumerateArray())
        {
            var template = await context.PlatformMaker.Send(HttpMethod.Get, $"/api/v1/identity/role-templates/{summary.GetProperty("code").GetString()}?tenantId={TenantId}");
            foreach (var function in template.GetProperty("functions").EnumerateArray())
            {
                var code = function.GetProperty("code").GetString()!;
                if (!function.GetProperty("audiences").EnumerateArray().Any(a => a.GetString() == "STAFF"))
                {
                    continue;
                }

                if (function.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False)
                {
                    var why = function.TryGetProperty("unavailableReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : "not available";
                    _demoLeftOut[code] = $"OFTZA can't hold it ({why})";
                    continue;
                }

                staff.Add(code);
            }
        }

        foreach (var (role, excluded) in new[]
                 {
                     (DemoMaker, SegregationOfDuties.Select(r => r.FunctionB).ToArray()),
                     (DemoChecker, SegregationOfDuties.Select(r => r.FunctionA).ToArray()),
                 })
        {
            var functions = staff.Except(excluded, StringComparer.Ordinal).ToList();
            foreach (var (function, needs) in Requirements.Where(r => functions.Contains(r.Function) && !functions.Contains(r.Needs)))
            {
                functions.Remove(function);
                _demoLeftOut[function] = $"FunctionRequirements: it needs {needs}, which OFTZA can't hold";
            }

            await Static("identity/static/roles-and-permission-sets", role, new { roleCode = role });
            await Import("identity/static/role-function-assignments", $"{role}:{functions[0]}",
                functions.Select(f => (object)new { roleCode = role, functionCode = f, code = $"{role}:{f}", changeReason = Reason }).ToList());
            _demoFunctions[role] = functions;
            context.Progress?.Invoke($"seed: {role} holds {functions.Count} staff functions (left out of both: {_demoLeftOut.Count})");
        }

        foreach (var (function, why) in _demoLeftOut)
        {
            context.Progress?.Invoke($"seed: left out of the demo roles: {function}: {why}");
        }
    }

    /// <summary>
    /// Approval authority for both roles on every approval type OFTZA can raise, with a limit above any sample amount (100,000,000, in rand where an
    /// amount is approved) and no delegation; one import, decided by a second person like any other. Made while the loaders hold the static data
    /// authority, before activation.
    /// </summary>
    private Task DemoAuthority() => Import("workflow/static/delegation-and-approval-authority", $"AUTH-{DemoApprovalTypes[0]}-{DemoMaker}",
        [.. new[] { DemoMaker, DemoChecker }.SelectMany(role => DemoApprovalTypes.Select(type => (object)new
        {
            roleCode = role, transactionType = type, limitValue = 100_000_000m, delegationAllowed = false, currency = NoAmount.Contains(type) ? null : "ZAR",
            code = $"AUTH-{type}-{role}", changeReason = Reason,
        }))]);
}
