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
using System.Net;
using Insys.BuildingBlocks.Api.Functions;
using Insys.Identity.Api;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Wave 7, batch 7.1 (docs/specs/addenda/ai-assist.md): AI Assist's settings and its functions, off by default.</summary>
public sealed class AssistTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Settings = "/api/v1/assist/settings";

    private async Task<(Guid Tenant, HttpClient Client)> Tenant(bool entitled)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "AS" + suffix);
        if (entitled)
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
                VALUES (NULL, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', @tenant, 'AI_ASSIST', true)
                """, ("code", $"AS{suffix}:AI_ASSIST"), ("tenant", "AS" + suffix));
        }

        var subject = $"assist-admin-{suffix}";
        await fixture.Database.UserWithFunctions(tenant, subject, "ASSIST_SETTINGS_MANAGE");
        return (tenant, (await fixture.Host()).ClientFor(tenant, subject));
    }

    [Fact]
    public async Task AFunctionHolder_WithoutTheFeature_IsRefused_AIA001()
    {
        var (_, client) = await Tenant(entitled: false);
        var refused = await client.GetAsync(Settings);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("FEATURE_NOT_ENTITLED");
    }

    [Fact]
    public async Task Settings_RecordTheJurisdiction_WhereInferenceRuns_AndTheLegalBasis_AIA050()
    {
        var (_, client) = await Tenant(entitled: true);
        var none = await (await client.GetAsync(Settings)).Json();
        (none.GetProperty("residencyAllowsInference").GetBoolean(), none.GetProperty("explanationsEnabled").GetBoolean()).ShouldBe((false, false), "off until set");

        var offshore = await client.Send(HttpMethod.Put, Settings, new { jurisdiction = "ng", inferenceRegion = "global", explanationsEnabled = true });
        offshore.StatusCode.ShouldBe(HttpStatusCode.OK, await offshore.Content.ReadAsStringAsync());
        var view = await offshore.Json();
        (view.GetProperty("jurisdiction").GetString(), view.GetProperty("inferenceRegion").GetString()).ShouldBe(("NG", "GLOBAL"));
        view.GetProperty("residencyAllowsInference").GetBoolean().ShouldBeFalse("outside the jurisdiction with no basis");

        var etag = offshore.ETag();
        var based = await client.Send(HttpMethod.Put, Settings, new { jurisdiction = "NG", inferenceRegion = "GLOBAL", legalBasis = "NDPA 2023 s41", explanationsEnabled = true }, ifMatch: etag);
        based.StatusCode.ShouldBe(HttpStatusCode.OK, await based.Content.ReadAsStringAsync());
        (await based.Json()).GetProperty("residencyAllowsInference").GetBoolean().ShouldBeTrue();

        (await client.Send(HttpMethod.Put, Settings, new { jurisdiction = "NG", inferenceRegion = "NG", explanationsEnabled = true }, ifMatch: etag)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed);
        (await client.Send(HttpMethod.Put, Settings, new { jurisdiction = "NGA", inferenceRegion = "NG", explanationsEnabled = true })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task SixFunctions_AreInTheCatalogue_InNoRoleTemplate_AndOverlayApprovalIsPrivileged_ForThePlatformOnly()
    {
        var functions = new[] { "ASSIST_SETTINGS_MANAGE", "ASSIST_EXPLAIN_REFUSAL", "ASSIST_EXPLAIN_PREMIUM", "ASSIST_PRODUCT_DRAFT_RUN", "ASSIST_TENANT_SETUP_RUN", "ASSIST_OVERLAY_APPROVE" };
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND function_code = ANY(@f)", ("f", functions))).ShouldBe(6L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND function_code LIKE 'ASSIST%'")).ShouldBe(6L, "and no others");
        await fixture.Host();
        var catalogue = FunctionRegistry.All();
        RoleTemplates.All.SelectMany(t => RoleTemplates.Expand(t, catalogue)).Select(f => f.Code).Where(c => c.StartsWith("ASSIST", StringComparison.Ordinal)).ShouldBeEmpty();
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT privileged::text || ':' || array_to_string(audiences, ',') FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND function_code = 'ASSIST_OVERLAY_APPROVE'
            """)).ShouldBe("true:PLATFORM");
    }
}
