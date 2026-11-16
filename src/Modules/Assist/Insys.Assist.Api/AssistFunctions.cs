/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Contracts;
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Assist.Api;

/// <summary>
/// AI Assist's eight functions (SRS §3, addendum §5 and §5.3): off by default, in no role template. Five need the AI_ASSIST feature, except the wizard's, whose
/// pack-only path needs only its function (AIA-090), and the three overlay functions, which use no model.
/// <list type="bullet">
/// <item>ASSIST_OVERLAY_MANAGE (platform, privileged): load a pack version and record the partner's review. It never approves (Ade, 15 November).</item>
/// <item>ASSIST_OVERLAY_APPROVE (platform, privileged): the named lead's approval of a version for every tenant.</item>
/// <item>ASSIST_OVERLAY_APPROVE_TENANT (staff, privileged): the fallback, a tenant's compliance officer approving its own version (D-19).</item>
/// </list>
/// Whoever loaded or reviewed a version can't approve it, whatever functions they hold. A tenant loads its own fallback version under ASSIST_TENANT_SETUP_RUN,
/// while it is onboarding only.
/// </summary>
internal static class AssistFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        new FunctionDeclaration("ASSIST_SETTINGS_MANAGE", FunctionKind.Screen, "AI_ASSIST", "Settings", null, [Audiences.Staff], "assist/settings", Features.AiAssist, false,
            ["AssistGetSettings", "AssistGetUsage", "AssistSetSettings"], []),
        Action("ASSIST_EXPLAIN_REFUSAL", Features.AiAssist, AssistPermissions.ExplainRefusal, ["AssistExplainRefusal"]),
        Action("ASSIST_EXPLAIN_PREMIUM", Features.AiAssist, AssistPermissions.ExplainPremium, ["AssistExplainPremium"]),
        Action("ASSIST_PRODUCT_DRAFT_RUN", Features.AiAssist, AssistPermissions.ProductDraftRun),
        Action("ASSIST_TENANT_SETUP_RUN", null, AssistPermissions.TenantSetupRun, ["AssistLoadTenantPack", "AssistListTenantPacks", "AssistGetTenantPack",
            "AssistStartTenantSetup", "AssistGetTenantSetup", "AssistAnswerTenantSetup", "AssistDecideSetupItem", "AssistCheckTenantSetup", "AssistSubmitTenantSetup", "AssistWithdrawTenantSetupSubmission"]),
        new FunctionDeclaration("ASSIST_OVERLAY_APPROVE", FunctionKind.Screen, "AI_ASSIST", "Overlays", null, [Audiences.Platform], "assist/overlays", null, true,
            ["AssistPlatformPacksToApprove", "AssistGetPlatformPackToApprove", "AssistApprovePlatformPack"], [AssistPermissions.OverlayApprove]),
        new FunctionDeclaration("ASSIST_OVERLAY_MANAGE", FunctionKind.Screen, "AI_ASSIST", "Overlays", null, [Audiences.Platform], "assist/overlays/manage", null, true,
            ["AssistLoadPlatformPack", "AssistReviewPlatformPack", "AssistListPlatformPacks", "AssistGetPlatformPack"], [AssistPermissions.OverlayManage]),
        new FunctionDeclaration("ASSIST_OVERLAY_APPROVE_TENANT", FunctionKind.Screen, "AI_ASSIST", "Overlays", null, [Audiences.Staff], "assist/packs/approvals", null, true,
            ["AssistTenantPacksToApprove", "AssistGetTenantPackToApprove", "AssistApproveTenantPack"], [AssistPermissions.OverlayApproveTenant]),
    ];

    private static FunctionDeclaration Action(string code, string? feature, string permission, IReadOnlyList<string>? endpoints = null) =>
        new(code, FunctionKind.Action, "AI_ASSIST", "Settings", "ASSIST_SETTINGS_MANAGE", [Audiences.Staff], null, feature, false, endpoints ?? [], [permission]);
}
