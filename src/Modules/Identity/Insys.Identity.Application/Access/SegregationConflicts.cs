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

using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Application.Access;

/// <summary>
/// A user whose role assignments in effect give both sides of a BLOCK segregation-of-duties rule in force (SRS-002, OQ-197): the rule, its two functions, the
/// function refused to them (B, the release or verify side), whether the rule is the platform baseline, and the roles giving each side. The user's name comes
/// from the display-name lookup (BR-021).
/// </summary>
public sealed record SegregationConflictView(Guid UserId, string Rule, bool Baseline, string FunctionA, string FunctionB, string RefusedFunction,
    IReadOnlyList<string> RolesGivingA, IReadOnlyList<string> RolesGivingB);

/// <summary>
/// Query ListSegregationConflicts (Ade, 20 November 2026, OQ-197): every user of the tenant holding both sides of a BLOCK rule in force today, the baseline's
/// and the tenant's, for an access review. Read before the deploy that enforces the baseline on an existing tenant: it lists who will be refused.
/// </summary>
public sealed record ListSegregationConflicts : IQuery<IReadOnlyList<SegregationConflictView>>;

/// <summary>The users holding both sides of a BLOCK rule in force on the day, from their role assignments in effect; one row per user and rule.</summary>
public sealed class SegregationConflictFinder(IAccessRepository access, IRolePolicyReader policy)
{
    public async Task<IReadOnlyList<SegregationConflictView>> InForce(DateOnly day, CancellationToken cancellationToken)
    {
        var rules = (await policy.ConflictRules(day, cancellationToken)).Where(r => r.Conflict == SegregationOfDutiesBaseline.Block).ToList();
        var holdings = await access.EffectiveAssignments(day, cancellationToken);
        if (rules.Count == 0 || holdings.Count == 0)
        {
            return [];
        }

        var roles = (await policy.Roles(holdings.Select(h => h.RoleCode), day, cancellationToken)).ToDictionary(r => r.RoleCode, StringComparer.Ordinal);
        var conflicts = new List<SegregationConflictView>();
        foreach (var user in holdings.GroupBy(h => h.UserId).OrderBy(g => g.Key))
        {
            var held = user.Select(h => h.RoleCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(code => roles.GetValueOrDefault(code)).OfType<RoleFacts>().ToList();
            var functions = held.SelectMany(r => r.Functions).ToHashSet(StringComparer.Ordinal);
            foreach (var rule in SegregationOfDutiesBaseline.HeldBothSides(functions, rules))
            {
                conflicts.Add(new SegregationConflictView(user.Key, rule.Code ?? "", SegregationOfDutiesBaseline.For(rule.FunctionA, rule.FunctionB) is not null,
                    rule.FunctionA, rule.FunctionB, rule.FunctionB,
                    [.. held.Where(r => r.Functions.Contains(rule.FunctionA)).Select(r => r.RoleCode)],
                    [.. held.Where(r => r.Functions.Contains(rule.FunctionB)).Select(r => r.RoleCode)]));
            }
        }

        return conflicts;
    }
}

internal sealed class ListSegregationConflictsHandler(SegregationConflictFinder finder, IBusinessDate businessDate)
    : IQueryHandler<ListSegregationConflicts, IReadOnlyList<SegregationConflictView>>
{
    public async Task<Result<IReadOnlyList<SegregationConflictView>>> Handle(ListSegregationConflicts query, CancellationToken cancellationToken) =>
        Result.Success(await finder.InForce(businessDate.Today, cancellationToken));
}
