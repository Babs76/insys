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
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.Identity.Api;
using Insys.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    /// <summary>The data protection officer finds the party to anonymise or merge (Ade, 20 November 2026): the party search, and the screen it sits under.</summary>
    [Fact]
    public async Task DataProtectionOfficer_SearchesParties_ToFindThoseItAnonymisesOrMerges_OQ111()
    {
        var catalogue = await Catalogue();
        var officer = RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == "DATA_PROTECTION_OFFICER"), catalogue).Select(f => f.Code).ToList();
        officer.ShouldContain("PARTY_SEARCH_PARTIES");
        officer.ShouldContain(catalogue.Single(f => f.Function.Code == "PARTY_SEARCH_PARTIES").Function.Parent!);
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

    /// <summary>
    /// BR-088: the API publishes the templates from the same source the tests and the addendum read, so they cannot drift.
    /// The list and every template, functions with their audiences and privileged flags, are exactly that source.
    /// </summary>
    [Fact]
    public async Task TheApi_ReturnsExactlyTheTemplateSource_BR088()
    {
        var factory = await fixture.Host();
        var catalogue = FunctionRegistry.All();
        var json = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "templates-a", IdentityPermissions.UserRead);
        var reader = factory.ClientFor(DatabaseFixture.TenantA, "templates-a");

        async Task<JsonNode?> Body(string url)
        {
            var response = await reader.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }

        JsonNode.DeepEquals(await Body("/api/v1/identity/role-templates"), JsonSerializer.SerializeToNode(RoleTemplates.Summaries(catalogue), json))
            .ShouldBeTrue("the list is the source's templates, in order");
        foreach (var template in RoleTemplates.All)
        {
            JsonNode.DeepEquals(await Body($"/api/v1/identity/role-templates/{template.Code}"), JsonSerializer.SerializeToNode(RoleTemplates.View(template.Code, catalogue), json))
                .ShouldBeTrue($"{template.Code} is the source's template");
        }

        (await reader.GetAsync("/api/v1/identity/role-templates/NO_SUCH_TEMPLATE")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The first-administrator request is prefilled from TENANT_ADMIN, whose privileged functions say so.
        var admin = (await Body("/api/v1/identity/role-templates/TENANT_ADMIN"))!["functions"]!.AsArray();
        admin.Single(f => f!["code"]!.GetValue<string>() == "ASSIGN_ROLE")!["privileged"]!.GetValue<bool>().ShouldBeTrue();
        admin.Single(f => f!["code"]!.GetValue<string>() == "IDENTITY_USERS")!["privileged"]!.GetValue<bool>().ShouldBeFalse();
    }

    /// <summary>
    /// BR-088: read for a tenant, a template marks each function available or not for it (enablement, then feature
    /// entitlement), so the console prefills only functions the tenant can hold. A tenant entitled to GENERAL only, with
    /// one base function switched off, reads the auditor's template: Life and Health screens are not entitled, the switched
    /// off one is disabled, General and the base platform are available. Its TENANT_ADMIN prefill is whole.
    /// </summary>
    [Fact]
    public async Task ATemplateReadForATenantEntitledToGeneralOnly_MarksEachFunctionAvailableOrNot_BR088()
    {
        var factory = await fixture.Host();
        var catalogue = FunctionRegistry.All();
        var json = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var target = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at) VALUES (@id, 'BR088GEN', 'General only', 'A', 'ACTIVE', now());
            INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
            VALUES (NULL, 'BR088GEN:GENERAL', 1, DATE '2000-01-01', 'EFFECTIVE', 'seed', 'test seed', 'BR088GEN', 'GENERAL', true)
            """, ("id", target));
        var auditor = RoleTemplates.All.Single(t => t.Code == "AUDITOR");
        var switchedOff = RoleTemplates.Expand(auditor, catalogue).First(f => f.Feature is null && f.SubDomain != "IDENTITY_ACCESS").Code;
        await fixture.Database.SeedStatic(target, "tenant.cfg_tenant_function_availability", "EFFECTIVE", "BR088-OFF", new DateOnly(2000, 1, 1),
            ("function_code", switchedOff), ("enabled", false));

        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "templates-op", IdentityPermissions.UserRead);
        var platform = factory.ClientFor(DatabaseFixture.Operator, "templates-op");
        var response = await platform.GetAsync($"/api/v1/identity/role-templates/AUDITOR?tenantId={target}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        string? Refusal(FunctionDeclaration f) =>
            f.Code == switchedOff ? "FUNCTION_DISABLED" : f.Feature is { } feature && feature != "GENERAL" ? "FEATURE_NOT_ENTITLED" : null;
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(RoleTemplates.View("AUDITOR", catalogue, target, Refusal), json)).ShouldBeTrue("availability is the tenant's");
        body["tenantId"]!.GetValue<Guid>().ShouldBe(target);
        var functions = body["functions"]!.AsArray().Select(f => (Code: f!["code"]!.GetValue<string>(), Feature: f["feature"]?.GetValue<string>(),
            Available: f["available"]!.GetValue<bool>(), Reason: f["unavailableReason"]?.GetValue<string>())).ToList();
        functions.ShouldContain(f => f.Feature == "GENERAL" && f.Available);
        functions.ShouldContain(f => f.Feature == "LIFE" && !f.Available && f.Reason == "FEATURE_NOT_ENTITLED");
        functions.ShouldContain(f => f.Feature == "HEALTH" && !f.Available && f.Reason == "FEATURE_NOT_ENTITLED");
        functions.Single(f => f.Code == switchedOff).Reason.ShouldBe("FUNCTION_DISABLED");
        functions.Where(f => f.Feature is null && f.Code != switchedOff).ShouldAllBe(f => f.Available && f.Reason == null);

        // The first-administrator prefill: every TENANT_ADMIN function is base platform, so all of it is available.
        var admin = JsonNode.Parse(await (await platform.GetAsync($"/api/v1/identity/role-templates/TENANT_ADMIN?tenantId={target}")).Content.ReadAsStringAsync())!;
        admin["functions"]!.AsArray().ShouldAllBe(f => f!["available"]!.GetValue<bool>());

        // Read with no tenant, nothing is marked; another tenant's availability is the operator's to read; an unknown tenant is 404.
        (await (await platform.GetAsync("/api/v1/identity/role-templates/AUDITOR")).Content.ReadAsStringAsync()).ShouldContain("\"available\":null");
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "templates-a2", IdentityPermissions.UserRead);
        var tenantA = factory.ClientFor(DatabaseFixture.TenantA, "templates-a2");
        (await tenantA.GetAsync($"/api/v1/identity/role-templates/AUDITOR?tenantId={target}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenantA.GetAsync($"/api/v1/identity/role-templates/AUDITOR?tenantId={DatabaseFixture.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.GetAsync($"/api/v1/identity/role-templates/AUDITOR?tenantId={Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>BR-088: the templates are read under a function the tenant and platform administrator templates both hold.</summary>
    [Fact]
    public async Task TheTemplatesScreen_IsHeldByTheTenantAndPlatformAdministrators_BR088()
    {
        var catalogue = await Catalogue();
        string[] holders = [.. RoleTemplates.All.Where(t => !t.ScreensOnly && RoleTemplates.Expand(t, catalogue).Any(f => f.Code == "IDENTITY_ROLE_TEMPLATES")).Select(t => t.Code)];
        holders.ShouldBe(["TENANT_ADMIN", "PLATFORM_ADMIN"], ignoreOrder: true);
    }

    /// <summary>
    /// Proposal A (signed 3 November 2026): the underwriters rate, evaluate the rules and record the acknowledgement, as single functions; changing rates and rules
    /// stays with the product manager, and no template gains an approval.
    /// </summary>
    [Fact]
    public async Task Underwriters_RateEvaluateAndAcknowledge_ButDoNotChangeRatesOrRules_OQ111()
    {
        var catalogue = await Catalogue();
        HashSet<string> Codes(string template) => RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == template), catalogue).Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        string[] quoting = ["RATING_CALCULATIONS", "RATING_CALCULATE_PREMIUM", "RULE_DECISIONS", "RULES_EVALUATE_RULE_SET", "INTEGRATION_DISCLOSURES", "INTEGRATION_ACKNOWLEDGE_DISCLOSURE"];
        foreach (var template in new[] { "GENERAL_UNDERWRITER", "LIFE_UNDERWRITER" })
        {
            var codes = Codes(template);
            quoting.Where(c => !codes.Contains(c)).ShouldBeEmpty(template);
            codes.Where(c => catalogue.Single(f => f.Function.Code == c).Function.SubDomain is "RATING" or "RULES" && !quoting.Contains(c)).ShouldBeEmpty($"{template} changes no rate or rule");
            codes.ShouldNotContain("RATING_SIMULATE_PREMIUM");
        }

        var membership = Codes("HEALTH_MEMBERSHIP_ADMINISTRATOR");
        membership.ShouldContain("INTEGRATION_ACKNOWLEDGE_DISCLOSURE");
        membership.ShouldNotContain("RATING_CALCULATE_PREMIUM");
        RoleTemplates.All.Single(t => t.Code == "GENERAL_UNDERWRITER").Approves.ShouldBe(["ENDORSEMENT", "GENERAL_NON_RENEWAL"]); // DE-002 (Ade, 24 November 2026)
        RoleTemplates.All.Single(t => t.Code == "LIFE_UNDERWRITER").Approves.ShouldBe(["LIFE_UNDERWRITING"]);
    }

    /// <summary>
    /// Proposal B (signed 3 November 2026): a line configuration manager maintains and approves Life, General and Health configuration and decides others'
    /// changes in the inbox, and nothing else; the static data engine refuses an approval by the change's maker.
    /// </summary>
    [Fact]
    public async Task LineConfigurationManager_HoldsTheThreeLinesConfiguration_AndNothingElse_OQ111()
    {
        var catalogue = await Catalogue();
        var functions = RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == "LINE_CONFIGURATION_MANAGER"), catalogue);
        // DE-015: the change requests screen (STATIC_DATA) comes with maintaining static data.
        functions.Select(f => f.SubDomain).Distinct().Order(StringComparer.Ordinal).ShouldBe(["CONFIG_GENERAL", "CONFIG_HEALTH", "CONFIG_LIFE", "STATIC_DATA", "WORKFLOW"]);
        functions.Where(f => f.SubDomain == "STATIC_DATA").Select(f => f.Code).ShouldBe([RoleTemplates.ChangeRequestsScreen]);
        functions.Where(f => f.SubDomain == "WORKFLOW").Select(f => f.Code).ShouldBe(["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", "WORKFLOW_REJECT"], ignoreOrder: true);
        foreach (var line in new[] { "CONFIG_LIFE", "CONFIG_GENERAL", "CONFIG_HEALTH" })
        {
            var inLine = catalogue.Where(f => f.Function.SubDomain == line && f.Function.Kind != FunctionKind.Job && f.Function.Audiences.Contains("STAFF") && !f.Function.Privileged).Select(f => f.Function.Code);
            inLine.Where(c => functions.All(f => f.Code != c)).ShouldBeEmpty($"every function of {line}");
            functions.ShouldContain(f => f.SubDomain == line && f.Code.EndsWith("_APPROVE", StringComparison.Ordinal), $"approves {line}");
        }

        RoleTemplates.All.Single(t => t.Code == "LINE_CONFIGURATION_MANAGER").Approves.ShouldBe(["STATIC_DATA_T1", "STATIC_DATA_T2"]);
    }

    private static readonly string[] Inbox = ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", "WORKFLOW_REJECT"];

    private static HashSet<string> Codes(string template, IReadOnlyList<CatalogueFunction> catalogue) =>
        RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == template), catalogue).Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// DE-001, DE-006, DE-044 (Ade, 24 November 2026): every template that approves anything but static data decides it in the Workflow inbox, so holds the
    /// approvals screen with approve and reject. Static data changes are decided on their own route (static_data.approve).
    /// </summary>
    [Fact]
    public async Task EveryTemplateThatApprovesAWorkflowApproval_HoldsTheInbox_DE001()
    {
        var catalogue = await Catalogue();
        (from template in RoleTemplates.All
         where (template.Approves ?? []).Any(a => !a.StartsWith("STATIC_DATA_", StringComparison.Ordinal))
         let codes = Codes(template.Code, catalogue)
         from function in Inbox
         where !codes.Contains(function)
         select $"{template.Code} lacks {function}").ShouldBeEmpty();

        foreach (var code in new[] { "LIFE_UNDERWRITER", "GENERAL_UNDERWRITER", "PRODUCT_MANAGER", "FINANCE_OFFICER", "DISTRIBUTION_ADMINISTRATOR", "REINSURANCE_OFFICER",
                     "INTEGRATION_ADMINISTRATOR", "HEALTH_CLAIMS_ASSESSOR", "DATA_PROTECTION_OFFICER" })
        {
            Codes(code, catalogue).IsSupersetOf(Inbox).ShouldBeTrue($"{code} holds the inbox");
        }

        // The finance officer builds payout batches: its inbox decides none of them, as only the finance manager has authority for PAYOUT_RELEASE, and
        // Workflow refuses the maker as checker in any case (SRS-002).
        RoleTemplates.All.Where(t => (t.Approves ?? []).Contains("PAYOUT_RELEASE")).Select(t => t.Code).ShouldBe(["FINANCE_MANAGER"]);
    }

    /// <summary>DE-015 (Ade, 24 November 2026): a rule, not a list: every template that maintains or approves any static data holds the change requests screen.</summary>
    [Fact]
    public async Task EveryTemplateThatMaintainsOrApprovesStaticData_HoldsTheChangeRequests_DE015()
    {
        var catalogue = await Catalogue();
        var changing = RoleTemplates.All.Where(t => RoleTemplates.Expand(t, catalogue).Any(f => RoleTemplates.ChangesStaticData(f.Code))).ToList();
        changing.Select(t => t.Code).ShouldBe(["TENANT_ADMIN", "PRODUCT_MANAGER", "CLAIMS_MANAGER", "LINE_CONFIGURATION_MANAGER", "FINANCE_OFFICER", "FINANCE_MANAGER",
            "DISTRIBUTION_ADMINISTRATOR", "REINSURANCE_OFFICER", "COMPLIANCE_OFFICER", "INTEGRATION_ADMINISTRATOR", "PLATFORM_STATIC_DATA_STEWARD"], ignoreOrder: true);
        changing.Where(t => !Codes(t.Code, catalogue).Contains(RoleTemplates.ChangeRequestsScreen)).Select(t => t.Code).ShouldBeEmpty();
    }

    /// <summary>
    /// DE-002 (Ade, 24 November 2026): the approval types no template could approve now each have one, and its checker holds what deciding needs; the
    /// enhanced due diligence approval stays in no template (onboarding readiness requires authority for it, OnboardingReadinessTests).
    /// </summary>
    [Fact]
    public async Task TheApprovalTypesNoTemplateCouldApprove_EachHaveATemplate_DE002()
    {
        var catalogue = await Catalogue();
        string[] Approvers(string type) => [.. RoleTemplates.All.Where(t => (t.Approves ?? []).Contains(type)).Select(t => t.Code)];
        Approvers("GENERAL_NON_RENEWAL").ShouldBe(["GENERAL_UNDERWRITER"]);
        Approvers("PARTY_MATCH_RESOLUTION").ShouldBe(["DATA_PROTECTION_OFFICER"]);
        Approvers("PROVIDER_CREDIT_NOTE").ShouldBe(["FINANCE_MANAGER"]);
        Approvers("PROVIDER_RECOVERY_WRITE_OFF").ShouldBe(["FINANCE_MANAGER"]);
        Approvers("PROVIDER_RECOVERY_FINDING").ShouldBe(["HEALTH_CLAIMS_ASSESSOR"]);
        Approvers("DUE_DILIGENCE_ENHANCED").ShouldBeEmpty();

        // The checker of a non-renewal needs general_policy.non_renew, and of a party match party.update (the approval's RequiredPermission).
        bool Grants(string template, string permission) =>
            Codes(template, catalogue).Any(c => catalogue.Single(f => f.Function.Code == c).Permissions.Contains(permission));
        Grants("GENERAL_UNDERWRITER", "general_policy.non_renew").ShouldBeTrue();
        Grants("DATA_PROTECTION_OFFICER", "party.update").ShouldBeTrue();
        Codes("DATA_PROTECTION_OFFICER", catalogue).ShouldContain("PARTY_RESOLVE_MATCH");
        Codes("HEALTH_CLAIMS_ASSESSOR", catalogue).ShouldContain("HEALTH_RECOVERIES");
    }

    /// <summary>DE-003, DE-004, DE-005, DE-019, DE-021 (Ade, 24 November 2026): the functions each dead end needed, where Ade put them.</summary>
    [Fact]
    public async Task TheDeadEndsFunctions_AreInTheTemplatesAdeChose_DE003_DE004_DE005_DE021()
    {
        var catalogue = await Catalogue();
        string[] InSubDomain(string subDomain) =>
            [.. catalogue.Where(f => f.Function.SubDomain == subDomain && f.Function.Kind != FunctionKind.Job && f.Function.Audiences.Contains("STAFF") && !f.Function.Privileged)
                .Select(f => f.Function.Code)];
        void Holds(string template, params string[] functions) => functions.Where(c => !Codes(template, catalogue).Contains(c)).ShouldBeEmpty(template);

        Holds("GENERAL_UNDERWRITER", InSubDomain("GENERAL_RENEWALS"));
        Holds("HEALTH_CLAIMS_ASSESSOR", "HEALTH_RECORD_PROVIDER_QUALITY_REVIEW", "HEALTH_PROVIDER_PERFORMANCE");
        Holds("COMPLIANCE_OFFICER", [.. InSubDomain("CONFIG_PARTY"), .. InSubDomain("CONFIG_AUDIT"), "DOCUMENTS"]);
        Holds("CLAIMS_MANAGER", InSubDomain("CONFIG_CLAIMS_FRAMEWORK"));
        RoleTemplates.All.Single(t => t.Code == "CLAIMS_MANAGER").Approves!.ShouldContain("STATIC_DATA_T1");
        RoleTemplates.All.Single(t => t.Code == "CLAIMS_MANAGER").Approves!.ShouldContain("STATIC_DATA_T2");
        Holds("TENANT_ADMIN", [.. InSubDomain("CONFIG_COMMUNICATIONS"), .. InSubDomain("CONFIG_DOCUMENTS"), .. InSubDomain("CONFIG_ANALYTICS")]);
        Holds("FINANCE_MANAGER", "COMMISSION_STATEMENTS", "CLAIM_PAYMENT_INSTRUCTIONS", "SERVICE_COMPLAINTS");

        // Screens only: the finance manager gains no commission, claims or case action, and the compliance officer no document action (the legal hold
        // functions stay the data protection officer's).
        Codes("FINANCE_MANAGER", catalogue).Select(c => catalogue.Single(f => f.Function.Code == c).Function)
            .Where(f => f.Kind == FunctionKind.Action && f.SubDomain is "COMMISSION" or "CLAIM_FINANCIALS" or "CUSTOMER_SERVICE").Select(f => f.Code).ShouldBeEmpty();
        Codes("COMPLIANCE_OFFICER", catalogue).Where(c => catalogue.Single(f => f.Function.Code == c).Function is { SubDomain: "DOCUMENTS", Kind: FunctionKind.Action })
            .ShouldBeEmpty();
    }

    /// <summary>DE-058 (Ade, 24 November 2026): data-subject requests are the data protection officer's alone; the read-only auditor may see the screen.</summary>
    [Fact]
    public async Task DataSubjectRequests_AreTheDataProtectionOfficersAlone_DE058()
    {
        var catalogue = await Catalogue();
        var dataSubject = catalogue.Select(f => f.Function).Where(f => f.Code.StartsWith("PARTY_", StringComparison.Ordinal) && f.Code.Contains("DATA_SUBJECT", StringComparison.Ordinal)
            && f.Audiences.Contains("STAFF")).Select(f => f.Code).ToList();
        dataSubject.ShouldBe(RoleTemplates.DataSubjectRequests, ignoreOrder: true);
        foreach (var code in dataSubject)
        {
            RoleTemplates.All.Where(t => !t.ScreensOnly && Codes(t.Code, catalogue).Contains(code)).Select(t => t.Code).ShouldBe(["DATA_PROTECTION_OFFICER"], code);
        }
    }

    [Fact]
    public async Task Addendum_IsGeneratedFromTheTemplates_OQ111()
    {
        var expected = RoleTemplateAddendum.Document(await Catalogue());
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(RoleTemplateAddendum.FilePath, expected);
        }

        (await File.ReadAllTextAsync(RoleTemplateAddendum.FilePath)).ShouldBe(expected,
            "The role templates changed. Run with INSYS_UPDATE_SNAPSHOTS=1 and commit docs/specs/addenda/role-templates.md.");
    }
}
