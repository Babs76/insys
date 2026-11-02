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
/// AI Assist's six functions (SRS §3, addendum §5.3): off by default, in no role template. Five need the AI_ASSIST feature, except the wizard's, whose pack-only
/// path needs only its function (AIA-090). ASSIST_OVERLAY_APPROVE is privileged, and only the operator tenant holds it.
/// </summary>
internal static class AssistFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        new FunctionDeclaration("ASSIST_SETTINGS_MANAGE", FunctionKind.Screen, "AI_ASSIST", "Settings", null, [Audiences.Staff], "assist/settings", Features.AiAssist, false,
            ["AssistGetSettings", "AssistSetSettings"], []),
        Action("ASSIST_EXPLAIN_REFUSAL", Features.AiAssist, AssistPermissions.ExplainRefusal),
        Action("ASSIST_EXPLAIN_PREMIUM", Features.AiAssist, AssistPermissions.ExplainPremium),
        Action("ASSIST_PRODUCT_DRAFT_RUN", Features.AiAssist, AssistPermissions.ProductDraftRun),
        Action("ASSIST_TENANT_SETUP_RUN", null, AssistPermissions.TenantSetupRun),
        new FunctionDeclaration("ASSIST_OVERLAY_APPROVE", FunctionKind.Screen, "AI_ASSIST", "Overlays", null, [Audiences.Platform], "assist/overlays", null, true, [], [AssistPermissions.OverlayApprove]),
    ];

    private static FunctionDeclaration Action(string code, string? feature, string permission) =>
        new(code, FunctionKind.Action, "AI_ASSIST", "Settings", "ASSIST_SETTINGS_MANAGE", [Audiences.Staff], null, feature, false, [], [permission]);
}
