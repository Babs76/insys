/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Tenancy.Api;

/// <summary>
/// The Tenancy module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class TenancyFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Platform], null)
        .Screen("TENANTS", "TENANTS", "Tenant lifecycle", "tenancy/tenants", "ListTenants", "GetTenant", "GetTenantReadiness")
        .Action("REGISTER_TENANT", "TENANTS", "RegisterTenant")
        .Action("COMPLETE_ONBOARDING_STEP", "TENANTS", "CompleteOnboardingStep")
        .Action("RECORD_SMOKE_RUN", "TENANTS", "RecordSmokeRun")
        .Action("APPROVE_SMOKE_RUN", "TENANTS", "ApproveSmokeRun")
        .Action("ACTIVATE_TENANT", "TENANTS", "ActivateTenant")
        .Action("SUSPEND_TENANT", "TENANTS", "SuspendTenant", privileged: true)
        .Action("REACTIVATE_TENANT", "TENANTS", "ReactivateTenant")
        .Action("TERMINATE_TENANT", "TENANTS", "TerminateTenant", privileged: true)
        .Action("CHANGE_ISOLATION_PROFILE", "TENANTS", "ChangeIsolationProfile", privileged: true)
            .Functions,

        // BR-012: a tenant's staff read their own tenant's onboarding readiness.
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("TENANT_ONBOARDING_READINESS", "CONFIG_TENANCY", "Tenant Management", "tenancy/readiness", "GetOwnTenantReadiness")
            .Functions,

        // OQ-098: a privileged function checked by a rule, not an operation: submitting a tenant function availability row that
        // switches off the Billing or Payments daily job. It is an action on the tenant management configuration screen.
        new FunctionDeclaration("TENANT_SWITCH_OFF_MONEY_JOBS", FunctionKind.Action, "CONFIG_TENANCY", "Tenant Management", "SD_TENANT_MANAGEMENT_VIEW",
            Audiences.StaffAndPlatform, null, null, Privileged: true, [], []),
    ];
}
