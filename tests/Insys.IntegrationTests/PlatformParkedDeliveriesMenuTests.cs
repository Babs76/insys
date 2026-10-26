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
using Insys.Integration.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-011: the operator's count of parked deliveries per tenant is a PLATFORM screen of its own (PLATFORM_PARKED_DELIVERIES),
/// so the operator's menu offers it; a tenant user's menu never does, and keeps the staff screen.
/// </summary>
public sealed class PlatformParkedDeliveriesMenuTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string PlatformScreen = "PLATFORM_PARKED_DELIVERIES";
    private const string StaffScreen = "INTEGRATION_PARKED_DELIVERIES";
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "parked-operator", IntegrationPermissions.DeadLetterRead);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "parked-staff", IntegrationPermissions.DeadLetterRead);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task OperatorMenu_OffersTheParkedDeliveriesByTenantScreen_AndTheOperatorCanUseIt_BR011()
    {
        var menu = await Menu(DatabaseFixture.Operator, "parked-operator");

        menu.GetProperty("audience").GetString().ShouldBe("PLATFORM");
        var screens = Screens(menu);
        screens.ShouldContainKey(PlatformScreen);
        screens[PlatformScreen].ShouldBe("integration/parked-deliveries/by-tenant");
        screens.ShouldNotContainKey(StaffScreen);

        (await _factory.ClientFor(DatabaseFixture.Operator, "parked-operator").GetAsync("/api/v1/integration/parked-deliveries/by-tenant"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantUserMenu_NeverOffersThePlatformScreen_AndKeepsTheStaffOne_BR011()
    {
        var menu = await Menu(DatabaseFixture.TenantA, "parked-staff");

        menu.GetProperty("audience").GetString().ShouldBe("STAFF");
        var screens = Screens(menu);
        screens.ShouldNotContainKey(PlatformScreen);
        screens.ShouldContainKey(StaffScreen);
    }

    [Fact]
    public async Task PlatformScreen_IsInTheCatalogue_ForThePlatformAudienceOnly_BR011()
    {
        (await fixture.Database.ScalarAsOwner<string>("""
                SELECT array_to_string(audiences, ',') || '|' || sub_domain_code || '|' || route_key FROM iam.ref_function_catalogue
                WHERE tenant_id IS NULL AND function_code = @f AND record_status = 'ACTIVE' AND effective_to IS NULL
                """, ("f", PlatformScreen)))
            .ShouldBe("PLATFORM|INTEGRATION_OPERATIONS|integration/parked-deliveries/by-tenant");
    }

    private async Task<JsonElement> Menu(Guid tenant, string subject)
    {
        var response = await _factory.ClientFor(tenant, subject).GetAsync("/api/v1/identity/me/menu");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    /// <summary>Every screen in the menu, by function code, with its route key.</summary>
    private static Dictionary<string, string?> Screens(JsonElement menu) =>
        menu.GetProperty("domains").EnumerateArray()
            .SelectMany(d => d.GetProperty("subDomains").EnumerateArray())
            .SelectMany(s => s.GetProperty("screens").EnumerateArray())
            .ToDictionary(s => s.GetProperty("functionCode").GetString()!, s => s.GetProperty("routeKey").GetString());
}
