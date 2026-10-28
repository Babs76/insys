/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rules
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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Rules.Contracts;

public static class RulesPermissions
{
    public const string DecisionRead = "rule_decision.read";
    public const string DecisionCreate = "rule_decision.create";
}

/// <summary>A rule set was evaluated. The inputs stay in the decision log; they are not in the event (INT-007).</summary>
[IntegrationEvent("rules.RuleDecisionRecorded")]
public sealed record RuleDecisionRecorded(Guid DecisionId, string RulesetCode, int RulesetVersion, string SubjectType, Guid SubjectId, string Outcome,
    IReadOnlyList<string> FiredRules) : IIntegrationEvent;

/// <summary>An underwriting authority row (rules.cfg_authority_matrices): what a role may decide, up to a total sum assured, for a product or all.</summary>
public sealed record UnderwritingAuthority(string RoleCode, string? ProductCode, decimal MaxSumAssured, IReadOnlyList<string> DecisionTypes);

/// <summary>
/// Public query interface: the underwriting authority in force for roles and a product (§7.4), read on Rules' own
/// connection for the current tenant.
/// </summary>
public interface IUnderwritingAuthorities
{
    Task<IReadOnlyList<UnderwritingAuthority>> For(Guid tenantId, IReadOnlyCollection<string> roleCodes, string productCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Every role's underwriting authority in force for the product (its own rows, and rows for all products): who may decide its referrals.</summary>
    Task<IReadOnlyList<UnderwritingAuthority>> ForProduct(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Choosing among underwriting authorities (§7.4).</summary>
public static class ReferralRouting
{
    /// <summary>
    /// The role to send a referral to: of the roles whose authority covers the sum insured with at least one decision type,
    /// the one with the smallest such authority (the most junior who may decide it), then by role code. Null when none does.
    /// </summary>
    public static string? RouteTo(IEnumerable<UnderwritingAuthority> authorities, decimal sumInsured) =>
        authorities.Where(a => a.MaxSumAssured >= sumInsured && a.DecisionTypes.Count > 0)
            .OrderBy(a => a.MaxSumAssured).ThenBy(a => a.RoleCode, StringComparer.Ordinal).Select(a => a.RoleCode).FirstOrDefault();
}
