/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Infrastructure.StaticData;

/// <summary>
/// SRS-002 (Ade, 20 November 2026, OQ-197): the platform baseline can't be retired or weakened by a tenant. A tenant's row on a baseline pair (in either order)
/// is submitted only as BLOCK with no end date, and a version of such a row can't move it to another pair; deprecating or retiring one is refused.
/// 422 SEGREGATION_BASELINE_PROTECTED. The baseline is in force whatever the tenant's rows say (SegregationOfDutiesBaseline); this keeps the tenant's
/// table from saying otherwise.
/// </summary>
internal sealed class SegregationBaselineGuard : IStaticChangeGuard, IStaticRemovalGuard
{
    public string Schema => "iam";

    public string Table => "cfg_segregation_of_duties_conflict_rules";

    /// <summary>A version submitted: on a baseline pair, BLOCK with no end date; a new version of a baseline row stays on its pair.</summary>
    public Result Check(StaticRecord proposed, StaticRecord? current)
    {
        if (proposed is not SegregationOfDutiesConflictRules rule)
        {
            return Result.Success();
        }

        if (SegregationOfDutiesBaseline.For(rule.FunctionA, rule.FunctionB) is { } baseline)
        {
            if (rule.Conflict != SegregationOfDutiesBaseline.Block)
            {
                return Refused("conflict", $"{baseline.FunctionA} and {baseline.FunctionB} are a platform baseline rule ({baseline.Code}): it stays BLOCK (SRS-002).");
            }

            if (rule.EffectiveTo is not null)
            {
                return Refused("effectiveTo", $"{baseline.FunctionA} and {baseline.FunctionB} are a platform baseline rule ({baseline.Code}): it has no end date (SRS-002).");
            }
        }
        else if (current is SegregationOfDutiesConflictRules before && SegregationOfDutiesBaseline.For(before.FunctionA, before.FunctionB) is { } was)
        {
            return Refused("functionA", $"Rule {before.Code} is on the platform baseline pair {was.FunctionA} and {was.FunctionB} ({was.Code}): a new version keeps that pair (SRS-002).");
        }

        return Result.Success();
    }

    /// <summary>A row on a baseline pair deprecated or retired: refused.</summary>
    Result IStaticRemovalGuard.Check(StaticRecord leaving, IReadOnlyList<StaticRecord> others) =>
        leaving is SegregationOfDutiesConflictRules rule && SegregationOfDutiesBaseline.For(rule.FunctionA, rule.FunctionB) is { } baseline
            ? Refused("recordStatus", $"Rule {rule.Code} is the platform baseline rule {baseline.Code} ({baseline.FunctionA} and {baseline.FunctionB}): it can't be "
                                      + "deprecated or retired (SRS-002).")
            : Result.Success();

    private static Error Refused(string field, string message) =>
        Error.Validation(IdentityErrorCodes.SegregationBaselineProtected, message, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The rule in words, for the table's operations (the contract states it).</summary>
    public const string Rules = "A row on a platform baseline pair (CLAIMS_DRAFT_INSTRUCTION and CLAIMS_RELEASE_INSTRUCTION, PAYMENTS_BUILD_BATCH and "
        + "PAYMENTS_RELEASE_BATCH, PARTY_ADD_BANK_ACCOUNT and PARTY_BANK_ACCOUNT_VERIFY, in either order) is submitted only as BLOCK with no end date, a new "
        + "version of one keeps its pair, and it can't be deprecated or retired: 422 SEGREGATION_BASELINE_PROTECTED (SRS-002, OQ-197).";
}
