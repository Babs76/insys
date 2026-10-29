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

// Dashboard figures (docs/specs/addenda/dashboard-figures.md, GAP-046): each module that holds the data serves its own
// summary, in this one shape, so the front end lays out panels and never adds rows up itself. A figure's code names its
// definition in the addendum.

/// <summary>An amount in one currency. Amounts in different currencies are never added together.</summary>
public sealed record MoneyAmount(string Currency, decimal Amount);

/// <summary>A count for one key of a breakdown, for example one status.</summary>
public sealed record FigurePart(string Key, long Count);

/// <summary>
/// The figure codes the modules serve (docs/specs/addenda/dashboard-figures.md), declared by each module as it registers,
/// so the OpenAPI documents give DashboardFigure.code as an enum.
/// </summary>
public static class DashboardFigureCodes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Codes = new(StringComparer.Ordinal);

    public static void Declare(params string[] codes)
    {
        foreach (var code in codes)
        {
            Codes[code] = 0;
        }
    }

    public static IReadOnlyList<string> All => Codes.Keys.Order(StringComparer.Ordinal).ToList();
}

/// <summary>One figure: a count, amounts per currency, a breakdown, a moment or a rate, as its definition says.</summary>
/// <param name="Rate">A ratio from 0 to 1 the server computed (four decimals), or null when it has no denominator (Wave 4 F027).</param>
public sealed record DashboardFigure(string Code, long? Count = null, IReadOnlyList<MoneyAmount>? Amounts = null, IReadOnlyList<FigurePart>? By = null,
    DateTimeOffset? At = null, decimal? Rate = null);

/// <param name="AsOf">When the figures were computed.</param>
/// <param name="RefreshedAt">For figures from a projection, its last update.</param>
/// <param name="NotIncluded">What the figures leave out, for the panel to say so (for example HEALTH).</param>
public sealed record DashboardSummary(DateTimeOffset AsOf, IReadOnlyList<DashboardFigure> Figures, DateTimeOffset? RefreshedAt = null, IReadOnlyList<string>? NotIncluded = null);
