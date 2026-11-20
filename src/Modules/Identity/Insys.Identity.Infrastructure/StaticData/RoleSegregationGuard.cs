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
/// SRS-002 (Ade, 20 November 2026): a role's function grant is submitted (alone or as a line of an import) only if it breaks no BLOCK rule in force: the
/// platform baseline and the tenant's own rules in iam.cfg_segregation_of_duties_conflict_rules whose period meets the grant's (OQ-197). Refused when the
/// role's grants in force or on their way, its import's other lines and this one hold both functions of a rule; and when a user holding the role (an
/// assignment active or waiting for approval, whose period meets the grant's) already holds the rule's other function through another of their roles.
/// 422 ROLE_SEGREGATION_OF_DUTIES on functionCode, naming both functions. Checked at submission only: the static data flow has no guard at approval or
/// activation, so a rule put in force after a grant, or two grants submitted separately before either is approved, aren't caught here; the action-time
/// check refuses such a user the release or verify side (OQ-197).
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

        var inForce = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(SegregationOfDutiesConflictRules)).Lifecycle.InForce;
        var from = grant.EffectiveFrom;
        var to = grant.EffectiveTo;
        var tenantRules = await db.Set<SegregationOfDutiesConflictRules>().AsNoTracking()
            .Where(r => inForce.Contains(r.RecordStatus) && r.Conflict == SegregationOfDutiesBaseline.Block && r.FunctionA != null && r.FunctionB != null
                        && (r.EffectiveTo == null || r.EffectiveTo > from) && (to == null || r.EffectiveFrom < to)
                        && (r.FunctionA == grant.FunctionCode || r.FunctionB == grant.FunctionCode))
            .Select(r => new DutyConflictRule(r.FunctionA!, r.FunctionB!, r.Conflict, r.Code))
            .ToListAsync(cancellationToken);
        var rules = SegregationOfDutiesBaseline.WithTenantRules(tenantRules)
            .Where(r => r.Conflict == SegregationOfDutiesBaseline.Block && (r.FunctionA == grant.FunctionCode || r.FunctionB == grant.FunctionCode))
            .Select(r => (Rule: r, Other: r.FunctionA == grant.FunctionCode ? r.FunctionB : r.FunctionA))
            .ToList();
        if (rules.Count == 0)
        {
            return Result.Success();
        }

        var grants = others.Concat(importLines).OfType<RoleFunctionAssignments>().Where(g => g.FunctionCode != grant.FunctionCode).ToList();
        var held = grants.Where(g => g.RoleCode == grant.RoleCode).Select(g => g.FunctionCode).ToHashSet(StringComparer.Ordinal);
        if (rules.FirstOrDefault(r => held.Contains(r.Other)) is { Rule: not null } broken)
        {
            return Refused($"Role {grant.RoleCode} cannot hold both {broken.Rule.FunctionA} and {broken.Rule.FunctionB} "
                           + $"(segregation-of-duties rule {broken.Rule.Code}, SRS-002).");
        }

        // A user holding the role already holds the other side through another role: the grant would give them both.
        var statuses = new[] { AssignmentStatus.Active, AssignmentStatus.PendingApproval };
        var assignments = db.Set<RoleAssignment>().AsNoTracking()
            .Where(a => statuses.Contains(a.Status) && (a.ValidTo == null || a.ValidTo > from) && (to == null || a.ValidFrom < to));
        var holders = assignments.Where(a => a.RoleCode == grant.RoleCode).Select(a => a.UserId);
        var otherRoles = await assignments.Where(a => a.RoleCode != grant.RoleCode && holders.Contains(a.UserId)).Select(a => a.RoleCode).Distinct()
            .ToListAsync(cancellationToken);
        foreach (var role in otherRoles.Order(StringComparer.Ordinal))
        {
            var through = grants.Where(g => g.RoleCode == role).Select(g => g.FunctionCode).ToHashSet(StringComparer.Ordinal);
            if (rules.FirstOrDefault(r => through.Contains(r.Other)) is { Rule: not null } crossing)
            {
                return Refused($"A user holding role {grant.RoleCode} already holds {crossing.Other} through role {role}, so {grant.RoleCode} cannot hold "
                               + $"{grant.FunctionCode} (segregation-of-duties rule {crossing.Rule.Code}, SRS-002).");
            }
        }

        return Result.Success();
    }

    private static Error Refused(string message) =>
        Error.Validation(IdentityErrorCodes.RoleSegregationOfDuties, message, new Dictionary<string, string[]> { ["functionCode"] = [message] });
}
