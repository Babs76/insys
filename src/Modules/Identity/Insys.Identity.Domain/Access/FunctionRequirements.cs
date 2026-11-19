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
namespace Insys.Identity.Domain.Access;

/// <summary>
/// Functions a role may hold only together with another (Ade, 19 November 2026, OQ-190): a role granted ASSIST_PRODUCT_DRAFT_RUN must also hold
/// ASSIST_PRODUCT_DRAFT_REVIEW, which reads a run and decides its values, or its holder could start a run and never review it. Identity's one rule, used by
/// every path that sets a role's functions: a grant submitted (alone or in an import), a grant ended or retired, and the first administrators' functions.
/// The function catalogue has no "requires" field, so the pairs are kept here.
/// </summary>
public static class FunctionRequirements
{
    public const string AssistRun = "ASSIST_PRODUCT_DRAFT_RUN";
    public const string AssistReview = "ASSIST_PRODUCT_DRAFT_REVIEW";

    /// <summary>Each function that needs another, with the function it needs.</summary>
    public static readonly IReadOnlyDictionary<string, string> Requires = new Dictionary<string, string>(StringComparer.Ordinal) { [AssistRun] = AssistReview };

    /// <summary>The functions of a set held without what they need, each with the function missing.</summary>
    public static IReadOnlyList<(string Function, string Needs)> Missing(IEnumerable<string> functions)
    {
        var held = functions.ToHashSet(StringComparer.Ordinal);
        return Requires.Where(r => held.Contains(r.Key) && !held.Contains(r.Value)).Select(r => (r.Key, r.Value)).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The functions that need this one: they can't stay on a role once it goes.</summary>
    public static IReadOnlyList<string> NeededBy(string function) =>
        Requires.Where(r => r.Value == function).Select(r => r.Key).OrderBy(f => f, StringComparer.Ordinal).ToList();

    public static string Message(string function, string needs) =>
        $"A role holding {function} must also hold {needs}: it reads a run and decides its values, so a run can always be reviewed (OQ-190).";
}
