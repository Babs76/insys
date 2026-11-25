/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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

namespace Insys.StaticData.Api;

/// <summary>
/// The Static Data module's own functions (ADR-026), in PLATFORM › Static Data: change requests and imports, and the
/// governance settings. The tables' own functions are per catalogue module, under Configuration (StaticDataFunctions).
/// </summary>
internal static class StaticDataModuleFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap(Audiences.StaffAndPlatform, null)
            .Screen("STATIC_DATA_CHANGE_REQUESTS", "STATIC_DATA", "Maker-checker", "static-data/change-requests", "ListChangeRequests", "GetChangeRequest",
                "ListImportBatches", "GetSetupChangeSet")
            // OQ-180 (Ade, 17 November): abandoning an approved SETUP change set, asked by one person and approved or declined by another. In no role template.
            .Action("STATIC_DATA_SETUP_ABANDON", "STATIC_DATA_CHANGE_REQUESTS", ["RequestSetupAbandon", "ApproveSetupAbandon", "DeclineSetupAbandon"], privileged: true)
            .Screen("STATIC_DATA_GOVERNANCE", "STATIC_DATA", "Three maintenance tiers", "static-data/settings", "GetGovernanceSettings")
            .Action("STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER", "STATIC_DATA_GOVERNANCE", "ChangeTier2MakerChecker", privileged: true)
            // DE-016, OQ-204: shortening it lets others discard a maker's draft sooner, so it is privileged as the Tier 2 switch is. TODO(spec): confirm (OQ-204).
            .Action("STATIC_DATA_CHANGE_STALE_DRAFT_DAYS", "STATIC_DATA_GOVERNANCE", "ChangeStaleDraftDiscardDays", privileged: true)
            .Functions,
    ];
}
