/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
namespace Insys.BuildingBlocks.Infrastructure.StaticData;

public enum StaticScope
{
    /// <summary>Platform-global: everyone reads, only platform administrators write.</summary>
    G,

    /// <summary>Global baseline plus tenant rows.</summary>
    GT,

    /// <summary>Tenant-owned.</summary>
    T,
}

/// <summary>The two lifecycles in the static data specification §4, mapped to the stored record_status codes.</summary>
public sealed record StaticLifecycle(
    string Name, string Draft, string[] Submitted, string Approved, string Active, string Deprecated, string Retired, string[] InForce)
{
    /// <summary>Reference and master data: Draft → Pending approval → Approved → Active → Deprecated → Retired.</summary>
    public static readonly StaticLifecycle Reference = new("reference", "DRAFT", ["PENDING_APPROVAL"], "APPROVED", "ACTIVE", "DEPRECATED", "RETIRED",
        ["ACTIVE", "DEPRECATED"]);

    /// <summary>
    /// Configuration items (§7.7): Draft → Validated → Submitted → Approved → Published → Effective → Withdrawn or Retired.
    /// Submit records Validated then Submitted; activate publishes. TODO(spec): who moves Published to Effective.
    /// </summary>
    public static readonly StaticLifecycle Configuration = new("configuration", "DRAFT", ["VALIDATED", "SUBMITTED"], "APPROVED", "PUBLISHED", "WITHDRAWN", "RETIRED",
        ["PUBLISHED", "EFFECTIVE"]);

    public string PendingApproval => Submitted[^1];

    public bool IsInForce(string status) => InForce.Contains(status);
}

/// <summary>A static data table as the generator describes it (static-data-catalogue.json plus the V0002 DDL).</summary>
/// <param name="EntityCode">The code used in sdm.change_request (max 40 characters).</param>
/// <param name="ModuleRoute">The owning module's route segment, for example "identity".</param>
/// <param name="EntityRoute">The entity's route segment, for example "roles-and-permission-sets".</param>
public sealed record StaticDataDescriptor(
    string Schema,
    string Table,
    string EntityCode,
    string EntityName,
    string ModuleRoute,
    string EntityRoute,
    StaticScope Scope,
    int Tier,
    StaticLifecycle Lifecycle,
    Type EntityType,
    Type InputType)
{
    public bool AllowsTenantRows => Scope != StaticScope.G;

    public bool AllowsGlobalRows => Scope != StaticScope.T;

    /// <summary>Tier 1 always, and Tier 2 by default, need a checker other than the maker (SDM-003).</summary>
    public bool RequiresDifferentChecker => Tier <= 2;
}

public static class StaticQueries
{
    /// <summary>Versions in force on <paramref name="day"/>: an in-force status and an effective period covering the day.</summary>
    public static IQueryable<TEntity> InForce<TEntity>(this IQueryable<TEntity> rows, StaticLifecycle lifecycle, DateOnly day)
        where TEntity : StaticRecord
    {
        var statuses = lifecycle.InForce;
        return rows.Where(r => statuses.Contains(r.RecordStatus) && r.EffectiveFrom <= day && (r.EffectiveTo == null || r.EffectiveTo > day));
    }
}
