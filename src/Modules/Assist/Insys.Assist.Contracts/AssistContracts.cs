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
namespace Insys.Assist.Contracts;

/// <summary>The permissions of AI Assist's eight functions (SRS §3, addendum §5 and §5.3).</summary>
public static class AssistPermissions
{
    public const string SettingsManage = "assist_settings.manage";
    public const string ExplainRefusal = "assist_explain.refusal";
    public const string ExplainPremium = "assist_explain.premium";
    public const string ProductDraftRun = "assist_product_draft.run";

    /// <summary>AIA-020: read the provenance report of a version AI-1 drafted, as its approver does.</summary>
    public const string ProductDraftProvenanceRead = "assist_product_draft.read_provenance";
    public const string TenantSetupRun = "assist_tenant_setup.run";
    public const string OverlayApprove = "assist_overlay.approve";

    /// <summary>Loading a pack version and recording the partner's review, on the platform (Ade, 15 November). Never approving.</summary>
    public const string OverlayManage = "assist_overlay.manage";

    /// <summary>The fallback's approval (D-19): a tenant's compliance officer approving its own TENANT_ONLY version.</summary>
    public const string OverlayApproveTenant = "assist_overlay.approve_tenant";
}
