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
using System.Text.Json;
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

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
            await fixture.Database.SeedGlobalStatic("refdata.ref_languages", "ACTIVE", code, TestClock.Today.AddDays(-1), ("name", name));
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
        me.GetProperty("scopesEnforced").GetBoolean().ShouldBeTrue("F103: scopes are enforced");
        DocumentedSchema.ResponseProblems(me, "core", "GetMe").ShouldBeEmpty("GAP-031: the response matches its documented schema");
        me.GetProperty("userId").GetGuid().ShouldBe(userId);
        me.GetProperty("username").GetString().ShouldBe("me-clerk");
        var tenant = me.GetProperty("tenant");
        (tenant.GetProperty("id").GetGuid(), tenant.GetProperty("tenantCode").GetString(), tenant.GetProperty("isPlatformOperator").GetBoolean())
            .ShouldBe((DatabaseFixture.TenantA, "TENA", false));

        // ADR-025: the tenant's time zone and today's business date in it, for defaulting dates (this tenant has none, so the default).
        tenant.GetProperty("timeZone").GetString().ShouldBe(TestClock.DefaultTimeZone);
        DateOnly.Parse(tenant.GetProperty("businessDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(TestClock.Today);
        me.GetProperty("language").GetProperty("request").GetString().ShouldBe("en");
        me.GetProperty("roles").GetArrayLength().ShouldBe(2);
        me.GetProperty("roles").EnumerateArray().ShouldAllBe(r => r.GetProperty("inEffect").GetBoolean());

        var permissions = me.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("permission").GetString()!);
        permissions.Keys.ShouldBe([IntegrationPermissions.DeadLetterRead, IntegrationPermissions.WebhookRead]);
        permissions[IntegrationPermissions.DeadLetterRead].GetProperty("scopes").GetArrayLength().ShouldBe(2, "held unscoped and through the scoped role");
        var webhookScope = permissions[IntegrationPermissions.WebhookRead].GetProperty("scopes").EnumerateArray().Single();
        (webhookScope.GetProperty("roleCode").GetString(), webhookScope.GetProperty("products")[0].GetString()).ShouldBe((scopedRole, "GEN-MOTOR"));
    }

    /// <summary>
    /// Front-end request (18 November 2026): each role has a label to show instead of its code, in /identity/me and the roles list: the role's own label
    /// in the caller's languages, else its role template's name, else null. The template fallback's rule is RoleLabelTests'; here, that the host has the names.
    /// </summary>
    [Fact]
    public async Task EachRole_HasALabel_ItsOwnInTheCallersLanguage_ElseItsTemplatesName()
    {
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "me-labels", IntegrationPermissions.DeadLetterRead);
        var labelled = "R_LBL_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, labels)
            VALUES (@t, @role, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', @role, '{"en":"Claims clerk","zu":"Umabhalane wezimangalo"}'::jsonb)
            """, ("t", DatabaseFixture.TenantA), ("role", labelled));
        await fixture.Database.AssignRole(DatabaseFixture.TenantA, userId, labelled);

        string? LabelOf(JsonElement roles, string code) => roles.EnumerateArray().Single(r => r.GetProperty("roleCode").GetString() == code) is var r
            && r.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
        var me = await (await _factory.ClientFor(DatabaseFixture.TenantA, "me-labels").GetAsync("/api/v1/identity/me")).Json();
        DocumentedSchema.ResponseProblems(me, "core", "GetMe").ShouldBeEmpty();
        LabelOf(me.GetProperty("roles"), labelled).ShouldBe("Claims clerk", "the role's own label, in English for an English caller");

        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "role-reader", IdentityPermissions.UserRead);
        var roles = await (await _factory.ClientFor(DatabaseFixture.TenantA, "role-reader").GetAsync("/api/v1/identity/roles")).Json();
        LabelOf(roles, labelled).ShouldBe("Claims clerk");

        var template = Insys.Identity.Api.RoleTemplates.All.First();
        _factory.Services.GetRequiredService<Insys.Identity.Application.Users.IRoleTemplateNames>().NameOf(template.Code).ShouldBe(template.Name,
            "the host knows the role templates' names, for a role with no label of its own");
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

    /// <summary>
    /// F103: now that scopes are enforced, a role is assigned with product or organisation scopes, and scoped assignments are
    /// listed for review.
    /// </summary>
    [Fact]
    public async Task ScopedRoleAssignments_AreAccepted_AndListedForReview_F103()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "scope-admin", IdentityPermissions.UserRead, IdentityPermissions.UserUpdate);
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "scope-target");
        var role = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, IntegrationPermissions.DeadLetterRead);
        var admin = _factory.ClientFor(DatabaseFixture.TenantA, "scope-admin");
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");

        var assigned = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role",
            new { roleCode = role, scopeProducts = new[] { "GEN-MOTOR" }, scopeBranches = new[] { "JHB" } }, ifMatch: user.ETag());
        assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent, await assigned.Content.ReadAsStringAsync());
        var scoped = (await (await admin.GetAsync("/api/v1/identity/role-assignments/scoped")).Json()).EnumerateArray()
            .Single(a => a.GetProperty("userId").GetGuid() == userId);
        (scoped.GetProperty("roleCode").GetString(), scoped.GetProperty("scopeProducts")[0].GetString(), scoped.GetProperty("scopeBranches")[0].GetString())
            .ShouldBe((role, "GEN-MOTOR", "JHB"));
    }
}
