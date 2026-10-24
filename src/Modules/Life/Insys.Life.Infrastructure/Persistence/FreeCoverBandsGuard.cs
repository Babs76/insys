/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Life.Domain;
using Insys.Life.Infrastructure.StaticData;
using NpgsqlTypes;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>
/// A tenant's free cover bands (life.cfg_free_cover_limits) must not overlap and must leave no gaps: from 1 member up,
/// each band starts where the one below ends (GAP-043). Above the highest band no limit applies, and a member then needs
/// evidence for any cover (fail safe), so the bands are built up from 1, lowest first.
/// </summary>
internal sealed class FreeCoverBandsGuard : IStaticSetGuard
{
    public string Schema => "life";

    public string Table => "cfg_free_cover_limits";

    public Result Check(StaticRecord proposed, IReadOnlyList<StaticRecord> others)
    {
        var bands = others.Append(proposed).OfType<FreeCoverLimits>()
            .Select(b => (b.Code, Range: Canonical(NpgsqlRange<int>.Parse(b.SizeBand))))
            .OrderBy(b => b.Range.LowerBoundInfinite ? int.MinValue : b.Range.LowerBound)
            .ToList();
        if (bands.FirstOrDefault() is { } lowest && (lowest.Range.LowerBoundInfinite || lowest.Range.LowerBound != 1))
        {
            return Invalid($"The lowest band ({lowest.Code}) must start at 1 member.");
        }

        for (var i = 1; i < bands.Count; i++)
        {
            var (belowCode, below) = bands[i - 1];
            var (code, band) = bands[i];
            if (below.UpperBoundInfinite || band.LowerBound < below.UpperBound)
            {
                return Invalid($"Bands {belowCode} and {code} overlap.");
            }

            if (band.LowerBound > below.UpperBound)
            {
                return Invalid($"Between bands {belowCode} and {code}, {below.UpperBound} to {band.LowerBound - 1} members have no band.");
            }
        }

        return Result.Success();
    }

    /// <summary>[a,b) form, as PostgreSQL stores an int4range: an inclusive upper bound b] is b+1).</summary>
    private static NpgsqlRange<int> Canonical(NpgsqlRange<int> r) =>
        new(r.LowerBoundInfinite ? 0 : r.LowerBoundIsInclusive ? r.LowerBound : r.LowerBound + 1, true, r.LowerBoundInfinite,
            r.UpperBoundInfinite ? 0 : r.UpperBoundIsInclusive ? r.UpperBound + 1 : r.UpperBound, false, r.UpperBoundInfinite);

    private static Result Invalid(string message) =>
        Error.Validation(LifeErrorCodes.FreeCoverBandsInvalid, $"{message} Free cover bands must not overlap or leave gaps (GAP-043).");
}
