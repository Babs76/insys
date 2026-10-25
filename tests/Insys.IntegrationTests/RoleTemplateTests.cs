/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.IntegrationTests;

/// <summary>
/// OQ-111: the default role templates, expanded against the function catalogue as built. Every template passes every
/// segregation-of-duties rule, holds no privileged function it does not name, and the addendum is generated from them.
/// To accept a change to the addendum, run with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
public sealed class RoleTemplateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private async Task<IReadOnlyList<CatalogueFunction>> Catalogue()
    {
        await fixture.Host();
        return FunctionRegistry.All();
    }

    [Fact]
    public async Task EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111()
    {
        var catalogue = await Catalogue();
        var breaches = (from template in RoleTemplates.All
                        let codes = RoleTemplates.Expand(template, catalogue).Select(f => f.Code).ToHashSet(StringComparer.Ordinal)
                        from rule in RoleTemplates.SodRules
                        where codes.Contains(rule.A) && codes.Contains(rule.B)
                        select $"{template.Code} holds {rule.A} and {rule.B}").ToList();
        breaches.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoTemplate_HoldsAPrivilegedFunction_ItDoesNotName_OQ111()
    {
        var catalogue = await Catalogue();
        var privileged = RoleTemplates.PrivilegedCodes(catalogue);
        var unnamed = (from template in RoleTemplates.All
                       from function in RoleTemplates.Expand(template, catalogue)
                       where privileged.Contains(function.Code) && !(template.PrivilegedGrants ?? []).Contains(function.Code)
                       select $"{template.Code}: {function.Code}").ToList();
        unnamed.ShouldBeEmpty();

        // Every function the review names exists, and the catalogue's own flags are all in the review.
        var codes = catalogue.Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        RoleTemplates.Privileged.Select(p => p.Function).Where(c => !codes.Contains(c)).ShouldBeEmpty();
        catalogue.Where(f => f.Function.Privileged).Select(f => f.Function.Code).ShouldBe(
            RoleTemplates.Privileged.Where(p => p.Flagged).Select(p => p.Function), ignoreOrder: true);
    }

    [Fact]
    public async Task PrivilegedWork_SitsInItsOwnTemplates_NotEverydayOnes_OQ111()
    {
        var catalogue = await Catalogue();
        // The read-only auditors may see the screens; nothing else outside the template may hold them.
        string[] Holders(string code) =>
            [.. RoleTemplates.All.Where(t => !t.ScreensOnly && RoleTemplates.Expand(t, catalogue).Any(f => f.Code == code)).Select(t => t.Code)];

        foreach (var code in new[] { "PARTY_ANONYMISE", "PARTY_MERGE_PARTIES", "DOCUMENTS_DISPOSE", "DOCUMENTS_PLACE_LEGAL_HOLD", "DOCUMENTS_RELEASE_LEGAL_HOLD",
                     "PARTY_PLACE_SEARCH_LOG_HOLD", "PARTY_RELEASE_SEARCH_LOG_HOLD" })
        {
            Holders(code).ShouldBe(["DATA_PROTECTION_OFFICER"], code);
        }

        foreach (var code in new[] { "PAYMENT_CALLBACK_ENDPOINTS", "PAYMENTS_ISSUE_CALLBACK_ENDPOINT", "PAYMENTS_REVOKE_CALLBACK_ENDPOINT",
                     "COMMUNICATION_CALLBACK_ENDPOINTS", "COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT", "COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT" })
        {
            Holders(code).ShouldBe(["INTEGRATION_ADMINISTRATOR"], code);
        }

        var analyst = RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == "OPERATIONS_ANALYST"), catalogue).Select(f => f.Code).ToList();
        analyst.ShouldNotContain("WORKFLOW_APPROVE");
        analyst.ShouldNotContain("WORKFLOW_REJECT");
        analyst.ShouldNotContain("DISCARD_PARKED_DELIVERY");
        analyst.ShouldNotContain("DISCARD_MESSAGE");
        Holders("TERMINATE_TENANT").ShouldBe(["PLATFORM_ADMIN"]);
    }

    /// <summary>Only the audit roles hold a function that needs audit read; the party search log is for audit roles only (decided 25 October 2026).</summary>
    [Fact]
    public async Task OnlyAuditRoles_HoldAFunctionThatReadsAuditEvents_OQ111()
    {
        var catalogue = await Catalogue();
        var auditRead = catalogue.Where(f => f.Permissions.Contains(RoleTemplates.AuditRead)).Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        auditRead.ShouldContain("PARTY_SEARCH_LOG");
        (from template in RoleTemplates.All
         where !RoleTemplates.AuditRoles.Contains(template.Code)
         from function in RoleTemplates.Expand(template, catalogue)
         where auditRead.Contains(function.Code)
         select $"{template.Code}: {function.Code}").ShouldBeEmpty();

        string[] Holders(string code) => [.. RoleTemplates.All.Where(t => RoleTemplates.Expand(t, catalogue).Any(f => f.Code == code)).Select(t => t.Code)];
        Holders("PARTY_SEARCH_LOG").ShouldBe(["COMPLIANCE_OFFICER", "DATA_PROTECTION_OFFICER", "HEALTH_AUDITOR"], ignoreOrder: true);
    }

    [Fact]
    public async Task Addendum_IsGeneratedFromTheTemplates_OQ111()
    {
        var expected = RoleTemplates.Document(await Catalogue());
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(RoleTemplates.FilePath, expected);
        }

        (await File.ReadAllTextAsync(RoleTemplates.FilePath)).ShouldBe(expected,
            "The role templates changed. Run with INSYS_UPDATE_SNAPSHOTS=1 and commit docs/specs/addenda/role-templates.md.");
    }
}
