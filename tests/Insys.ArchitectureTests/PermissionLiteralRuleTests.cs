/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Text.RegularExpressions;
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// ADR-026: a permission check names a ...Permissions constant, never a string. EveryPermissionChecked_IsGrantedByAFunction finds
/// checks through those constants, so a literal would escape it, and a misspelt one would refuse everyone.
/// </summary>
public sealed partial class PermissionLiteralRuleTests
{
    [Theory]
    [InlineData("execution.HasPermission(\"health_case.read\")")]
    [InlineData(".RequirePermission( \"health_case.read\")")]
    [InlineData(".RequirePlatformPermission($\"{area}.read\")")]
    [InlineData(".AlsoChecks(\"health_case.read\", \"when\")")]
    [InlineData("PermissionPolicyProvider.Prefix + \"health_case.read\"")]
    [InlineData("new ApprovalRequested(\"T\", id, \"A\", maker, null, null, null, null, \"health_case.read\")")]
    [InlineData("new ApprovalTypeCondition(\"A\", true, \"health_case.read\")")]
    [InlineData("RequiredPermission: \"health_case.read\"")]
    public void TheRule_SeesALiteralInEachKindOfCheck(string code) => Literal(code).ShouldBeTrue();

    [Theory]
    [InlineData("execution.HasPermission(HealthPermissions.ClinicalRead)")]
    [InlineData("new ApprovalRequested(\"T\", id, \"A\", maker, null, null, null, null, PartyPermissions.BankAccountVerify)")]
    [InlineData(".AlsoChecks(PartyPermissions.PartyUpdate, \"the approval is a match (businessRefType PARTY)\")")]
    public void TheRule_LeavesAConstantAlone(string code) => Literal(code).ShouldBeFalse();

    [Fact]
    public void NoPermissionCheck_TakesAStringLiteral_ADR026()
    {
        var root = RepositoryPaths.Root;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            found.AddRange(Matches(text).Select(statement => $"{Path.GetRelativePath(root, file)}: {statement.Split('\n')[0].Trim()}"));
        }

        found.ShouldBeEmpty("name a ...Permissions constant");
    }

    private static bool Literal(string code) => Matches(code).Any();

    private static IEnumerable<string> Matches(string text) =>
        DirectLiteral().Matches(text).Select(m => m.Value).Concat(Approval().Matches(text).Select(m =>
        {
            var end = text.IndexOf(';', m.Index);
            return text[m.Index..(end < 0 ? text.Length : end)];
        }).Where(statement => DottedLiteral().IsMatch(statement)));

    /// <summary>The checks whose first argument is the permission, a policy built from one, and an approval's named argument.</summary>
    [GeneratedRegex(@"(?:\b(?:HasPermission|RequirePermission|RequirePlatformPermission|AlsoChecks)\(\s*|\bPermissionPolicyProvider\.\w*Prefix\s*\+\s*|\bRequiredPermission:\s*)\$?""")]
    private static partial Regex DirectLiteral();

    /// <summary>Where an approval's required permission is given by position.</summary>
    [GeneratedRegex(@"\bnew (?:ApprovalRequested|ApprovalTypeCondition)\(")]
    private static partial Regex Approval();

    [GeneratedRegex(@"""[a-z_]+\.[a-z_.]+""")]
    private static partial Regex DottedLiteral();
}
