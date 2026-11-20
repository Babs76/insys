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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Insys.Identity.Infrastructure.StaticData;

/// <summary>
/// SRS-002 (Ade, 20 November 2026): a role's function grant is submitted (alone or as a line of an import) only if the role's grants in force or on their way,
/// its import's other lines and this one don't hold both functions of a BLOCK rule of the tenant's iam.cfg_segregation_of_duties_conflict_rules whose period
/// meets the grant's. 422 ROLE_SEGREGATION_OF_DUTIES on functionCode, naming both functions. Checked at submission only: a rule put in force after a grant,
/// or two grants submitted separately before either is approved, aren't caught here (OQ-197).
/// </summary>
internal sealed class RoleSegregationGuard : IStaticSubmissionGuard
{
    public string Schema => "iam";

    public string Table => "cfg_role_function_assignments";

    public async Task<Result> Check(ModuleDbContext db, StaticRecord proposed, IReadOnlyList<StaticRecord> others, IReadOnlyList<StaticRecord> importLines,
        CancellationToken cancellationToken)
    {
        if (proposed is not RoleFunctionAssignments grant)
        {
            return Result.Success();
        }

        var held = others.Concat(importLines).OfType<RoleFunctionAssignments>().Where(g => g.RoleCode == grant.RoleCode && g.FunctionCode != grant.FunctionCode)
            .Select(g => g.FunctionCode).ToHashSet(StringComparer.Ordinal);
        if (held.Count == 0)
        {
            return Result.Success();
        }

        var inForce = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(SegregationOfDutiesConflictRules)).Lifecycle.InForce;
        var from = grant.EffectiveFrom;
        var to = grant.EffectiveTo;
        var rules = await db.Set<SegregationOfDutiesConflictRules>().AsNoTracking()
            .Where(r => inForce.Contains(r.RecordStatus) && r.Conflict == "BLOCK" && r.FunctionA != null && r.FunctionB != null
                        && (r.EffectiveTo == null || r.EffectiveTo > from) && (to == null || r.EffectiveFrom < to)
                        && (r.FunctionA == grant.FunctionCode || r.FunctionB == grant.FunctionCode))
            .ToListAsync(cancellationToken);
        var broken = rules.FirstOrDefault(r => held.Contains(r.FunctionA == grant.FunctionCode ? r.FunctionB! : r.FunctionA!));
        if (broken is null)
        {
            return Result.Success();
        }

        var message = $"Role {grant.RoleCode} cannot hold both {broken.FunctionA} and {broken.FunctionB} (segregation-of-duties rule {broken.Code}, SRS-002).";
        return Error.Validation(IdentityErrorCodes.RoleSegregationOfDuties, message, new Dictionary<string, string[]> { ["functionCode"] = [message] });
    }
}
