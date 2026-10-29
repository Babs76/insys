/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
namespace Insys.BuildingBlocks.Application;

// Onboarding readiness (docs/specs/addenda/onboarding-readiness.md, TEN-008): each module answers for the configuration it
// owns, in the tenant being onboarded and on its business date; Tenancy runs a step's checks before completing it.

/// <param name="Step">The onboarding step the check belongs to, for example PAYMENTS_AND_DISTRIBUTION.</param>
/// <param name="Code">The check's code, for example COLLECTION_METHODS.</param>
/// <param name="What">What must be in force, in words.</param>
/// <param name="Missing">What is missing, when not ready: table names, codes or types, never personal data.</param>
public sealed record ReadinessResult(string Step, string Code, bool Ready, string What, IReadOnlyList<string> Missing)
{
    public static ReadinessResult Of(string step, string code, string what, IReadOnlyList<string> missing) => new(step, code, missing.Count == 0, what, missing);
}

/// <summary>
/// What a check is run for: the tenant, its business date (ADR-025), its locale defaults as far as they are set, and the
/// step being completed (null for every step, as the readiness view asks).
/// </summary>
public sealed record ReadinessContext(Guid TenantId, DateOnly Day, string? BaseCurrency, string? DefaultLanguage, string? Step = null);

/// <summary>A module's readiness checks. Results with the same step and code from several modules are combined by Tenancy.</summary>
public interface IReadinessCheck
{
    /// <summary>The onboarding steps this check answers for; it is run only for them.</summary>
    IReadOnlyCollection<string> Steps { get; }

    Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Whether a tenant can raise an approval type now, for approval types whose raising depends on more than a feature (a
/// switched-on gate), and the permission its checker needs besides authority. Onboarding readiness (APPROVAL_AUTHORITY)
/// asks for authority over every type a tenant can raise, so no approval sits undecidable.
/// </summary>
public sealed record ApprovalTypeCondition(string ApprovalType, bool Raisable, string? RequiredPermission = null);

public interface IApprovalTypeConditions
{
    Task<IReadOnlyList<ApprovalTypeCondition>> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

