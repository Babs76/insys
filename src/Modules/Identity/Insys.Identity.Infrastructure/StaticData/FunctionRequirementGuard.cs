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
using Insys.Identity.Domain.Access;
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Infrastructure.StaticData;

/// <summary>
/// OQ-190 (Ade, 19 November 2026), through <see cref="FunctionRequirements"/>, Identity's one rule: a role's grant of a function that needs another is
/// submitted (alone or as a line of an import) only while the role holds that other function, in force or on its way, for as long as the grant runs. A grant
/// another one needs can't end, by a version with an end date or by deprecating or retiring it, while that one stays. 422 ASSIST_RUN_NEEDS_REVIEW.
/// </summary>
internal sealed class FunctionRequirementGuard : IStaticSetGuard, IStaticRemovalGuard
{
    public string Schema => "iam";

    public string Table => "cfg_role_function_assignments";

    /// <summary>A grant submitted: what it needs must be held while it runs; and if it ends, nothing on the role that needs it may outlast it.</summary>
    public Result Check(StaticRecord proposed, IReadOnlyList<StaticRecord> others)
    {
        if (proposed is not RoleFunctionAssignments grant)
        {
            return Result.Success();
        }

        var role = Grants(others, grant.RoleCode);
        if (FunctionRequirements.Requires.TryGetValue(grant.FunctionCode, out var needs)
            && !role.Any(g => g.FunctionCode == needs && Covers(g.EffectiveTo, grant.EffectiveTo)))
        {
            return Refused(grant.FunctionCode, needs);
        }

        if (grant.EffectiveTo is { } ends)
        {
            foreach (var dependent in FunctionRequirements.NeededBy(grant.FunctionCode))
            {
                if (role.Any(g => g.FunctionCode == dependent && !Covers(ends, g.EffectiveTo)))
                {
                    return Refused(dependent, grant.FunctionCode);
                }
            }
        }

        return Result.Success();
    }

    /// <summary>A grant deprecated or retired: refused while the role keeps a grant that needs it.</summary>
    Result IStaticRemovalGuard.Check(StaticRecord leaving, IReadOnlyList<StaticRecord> others)
    {
        if (leaving is not RoleFunctionAssignments grant)
        {
            return Result.Success();
        }

        var role = Grants(others, grant.RoleCode);
        var dependent = FunctionRequirements.NeededBy(grant.FunctionCode).FirstOrDefault(d => role.Any(g => g.FunctionCode == d));
        return dependent is null ? Result.Success() : Refused(dependent, grant.FunctionCode);
    }

    private static List<RoleFunctionAssignments> Grants(IReadOnlyList<StaticRecord> others, string roleCode) =>
        others.OfType<RoleFunctionAssignments>().Where(g => g.RoleCode == roleCode).ToList();

    // Whether a grant ending at 'held' lasts at least as long as one ending at 'needed' (null: no end).
    private static bool Covers(DateOnly? held, DateOnly? needed) => held is null || (needed is not null && held >= needed);

    private static Error Refused(string function, string needs) =>
        Error.Validation(IdentityErrorCodes.AssistRunNeedsReview, FunctionRequirements.Message(function, needs),
            new Dictionary<string, string[]> { ["functionCode"] = [FunctionRequirements.Message(function, needs)] });
}
