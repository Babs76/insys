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

namespace Insys.Identity.Domain.Users;

/// <summary>A rule of the platform baseline: its code, the side a user may keep (A) and the side refused to one holding both (B), and why.</summary>
public sealed record BaselineDutyRule(string Code, string FunctionA, string FunctionB, string Why);

/// <summary>
/// SRS-002 (Ade, 20 November 2026, OQ-197): the role-templates addendum's three segregation-of-duties rules are a platform baseline every tenant has,
/// always in force as BLOCK, which no tenant can retire or weaken. A tenant adds its own rules in iam.cfg_segregation_of_duties_conflict_rules; wherever
/// rules are read (assigning a role, granting a function to a role, the action-time check and the access-review listing), the baseline is combined with them.
/// </summary>
public static class SegregationOfDutiesBaseline
{
    public const string Block = "BLOCK";

    /// <summary>The one source of the baseline: RoleTemplates.SodRules and the OFTZA seed's rows are these pairs. Function B is the release or verify side.</summary>
    public static readonly IReadOnlyList<BaselineDutyRule> Rules =
    [
        new("SOD_CLAIM_PAYMENT", "CLAIMS_DRAFT_INSTRUCTION", "CLAIMS_RELEASE_INSTRUCTION", "Who prepares a claim payment does not release it"),
        new("SOD_PAYOUT_BATCH", "PAYMENTS_BUILD_BATCH", "PAYMENTS_RELEASE_BATCH", "Who builds a payout batch does not release it"),
        new("SOD_PAYEE_ACCOUNT", "PARTY_ADD_BANK_ACCOUNT", "PARTY_BANK_ACCOUNT_VERIFY", "Who adds a payee account does not verify it"),
    ];

    /// <summary>The baseline rule on this pair of functions, in either order; null when the pair isn't one.</summary>
    public static BaselineDutyRule? For(string? functionA, string? functionB) =>
        Rules.FirstOrDefault(r => (r.FunctionA == functionA && r.FunctionB == functionB) || (r.FunctionA == functionB && r.FunctionB == functionA));

    /// <summary>
    /// The rules in force: the baseline, as BLOCK in its own order, and the tenant's rules on other pairs. A tenant's row on a baseline pair, in either
    /// order or as WARN, adds nothing: the baseline decides (and which side is refused).
    /// </summary>
    public static IReadOnlyList<DutyConflictRule> WithTenantRules(IEnumerable<DutyConflictRule> tenantRules) =>
    [
        .. Rules.Select(r => new DutyConflictRule(r.FunctionA, r.FunctionB, Block, r.Code)),
        .. tenantRules.Where(r => For(r.FunctionA, r.FunctionB) is null),
    ];

    /// <summary>
    /// The BLOCK rules a user holding these functions holds both sides of. Each refuses its function B, the release or verify side, and leaves function A
    /// usable (OQ-197). TODO(spec): for a tenant's own rule, the order of its row's pair decides which side is refused (function B); SRS-002 doesn't say.
    /// </summary>
    public static IReadOnlyList<DutyConflictRule> HeldBothSides(IReadOnlyCollection<string> functions, IEnumerable<DutyConflictRule> rules) =>
        rules.Where(r => r.Conflict == Block && functions.Contains(r.FunctionA) && functions.Contains(r.FunctionB)).ToList();
}
