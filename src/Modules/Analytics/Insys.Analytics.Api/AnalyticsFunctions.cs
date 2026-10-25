/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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

namespace Insys.Analytics.Api;

/// <summary>
/// The Analytics module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class AnalyticsFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("ANALYTICS_CLAIMS_INCURRED", "ANALYTICS", "Claims incurred", "analytics/claims-incurred", "AnalyticsClaimsIncurred")
        .Screen("ANALYTICS_PROJECTIONS", "ANALYTICS", "Projections", "analytics/projections")
        .Action("ANALYTICS_REBUILD_PROJECTIONS", "ANALYTICS_PROJECTIONS", "AnalyticsRebuildProjections")
        .Screen("ANALYTICS_DASHBOARD", "ANALYTICS", "Dashboard figures", "analytics/summary", "AnalyticsSummary")
        .Screen("ANALYTICS_WRITTEN_PREMIUM", "ANALYTICS", "Written premium", "analytics/written-premium", "AnalyticsWrittenPremium")
        .Job("ANALYTICS_EVENT_PROCESSING", "ANALYTICS", "Background processing")
            .Functions,
    ];
}
