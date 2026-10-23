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
using Insys.Integration.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// GET /api/v1/identity/me (GAP-003): the signed-in user's profile, tenant, languages, role assignments and effective
/// permissions with their scopes; any user of the tenant may read their own, and nobody else.
/// </summary>
public sealed class MeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        foreach (var (code, name) in new[] { ("en", "English"), ("zu", "isiZulu") })
        {
            await fixture.Database.SeedGlobalStatic("refdata.ref_languages", "ACTIVE", code, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), ("name", name));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Me_ReturnsTheProfileTenantLanguages_AndEffectivePermissionsWithTheirScopes()
    {
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "me-clerk", IntegrationPermissions.DeadLetterRead);
        var scopedRole = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, IntegrationPermissions.WebhookRead, IntegrationPermissions.DeadLetterRead);
        await fixture.Database.AssignRole(DatabaseFixture.TenantA, userId, scopedRole);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-MOTOR'] WHERE user_id = @u AND role_code = @r",
            ("u", userId), ("r", scopedRole));

        var response = await _factory.ClientFor(DatabaseFixture.TenantA, "me-clerk").GetAsync("/api/v1/identity/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.ETag.ShouldNotBeNull();
        var me = await response.Json();
        me.GetProperty("userId").GetGuid().ShouldBe(userId);
        me.GetProperty("username").GetString().ShouldBe("me-clerk");
        var tenant = me.GetProperty("tenant");
        (tenant.GetProperty("id").GetGuid(), tenant.GetProperty("tenantCode").GetString(), tenant.GetProperty("isPlatformOperator").GetBoolean())
            .ShouldBe((DatabaseFixture.TenantA, "TENA", false));
        me.GetProperty("language").GetProperty("request").GetString().ShouldBe("en");
        me.GetProperty("roles").GetArrayLength().ShouldBe(2);
        me.GetProperty("roles").EnumerateArray().ShouldAllBe(r => r.GetProperty("inEffect").GetBoolean());

        var permissions = me.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("permission").GetString()!);
        permissions.Keys.ShouldBe([IntegrationPermissions.DeadLetterRead, IntegrationPermissions.WebhookRead]);
        permissions[IntegrationPermissions.DeadLetterRead].GetProperty("scopes").GetArrayLength().ShouldBe(2, "held unscoped and through the scoped role");
        var webhookScope = permissions[IntegrationPermissions.WebhookRead].GetProperty("scopes").EnumerateArray().Single();
        (webhookScope.GetProperty("roleCode").GetString(), webhookScope.GetProperty("products")[0].GetString()).ShouldBe((scopedRole, "GEN-MOTOR"));
    }

    [Fact]
    public async Task AnyUserOfTheTenant_ReadsTheirOwn_ButATokenForAnotherTenant_OrNone_IsRefused()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "me-nobody");

        (await _factory.ClientFor(DatabaseFixture.TenantA, "me-nobody").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _factory.ClientFor(DatabaseFixture.TenantB, "me-nobody").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PreferredLanguage_IsSetByTheUser_FromTheRegister()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "me-linguist");
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "me-linguist");

        var etag = (await client.GetAsync("/api/v1/identity/me")).ETag();
        (await client.Send(HttpMethod.Put, "/api/v1/identity/me/preferred-language", new { language = "zu" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var me = await client.GetAsync("/api/v1/identity/me");
        (await me.Json()).GetProperty("language").GetProperty("preferred").GetString().ShouldBe("zu");

        (await (await client.Send(HttpMethod.Put, "/api/v1/identity/me/preferred-language", new { language = "qq" }, ifMatch: me.ETag())).Code()).ShouldBe("VALIDATION_FAILED");
    }
}
