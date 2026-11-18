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
using System.Collections.Concurrent;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>
/// What a table's activated row names for its consumers, beyond its reference: a feature entitlement's tenant and feature (Ade, 18 November 2026). The
/// owning module registers its table at startup, as JobFunctions does, so the building block stays free of any module's types.
/// </summary>
public static class StaticActivationFacts
{
    private static readonly ConcurrentDictionary<string, Func<StaticRecord, (string Tenant, string Feature)?>> Entitlements = new(StringComparer.Ordinal);

    public static void ForEntitlements(string schema, string table, Func<StaticRecord, (string Tenant, string Feature)?> facts) => Entitlements[$"{schema}.{table}"] = facts;

    public static (string Tenant, string Feature)? Entitlement(StaticDataDescriptor descriptor, StaticRecord entity) =>
        Entitlements.TryGetValue($"{descriptor.Schema}.{descriptor.Table}", out var facts) ? facts(entity) : null;
}
