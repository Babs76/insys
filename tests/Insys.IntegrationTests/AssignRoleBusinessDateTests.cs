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
using Insys.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Front-end request BR-040 (GAP-038, ADR-025): a role's version is in force by the tenant's business date, not the UTC
/// date. Each host runs on a fixed clock, so the real time-zone conversion is used (no test date). Tenant A is in
/// Africa/Johannesburg (UTC+2), tenant B in Africa/Nairobi (UTC+3).
/// </summary>
public sealed class AssignRoleBusinessDateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly October31 = new(2026, 10, 31);
    private static readonly DateOnly November1 = new(2026, 11, 1);
    private static readonly DateOnly November2 = new(2026, 11, 2);

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        foreach (var (tenant, zone) in new[] { (DatabaseFixture.TenantA, "Africa/Johannesburg"), (DatabaseFixture.TenantB, "Africa/Nairobi") })
        {
            await db.SeedStatic(tenant, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-" + zone[7..10].ToUpperInvariant(), From,
                ("tenant", zone[7..10]), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", zone), ("country", "ZA"));
            await db.UserWithFunctions(tenant, "dated-admin", "IDENTITY_USERS", "ASSIGN_ROLE");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 23:30 UTC on 31 October is 01:30 on 1 November in Johannesburg: a role in force from 1 November is assigned and in
    /// effect, where the UTC date (still the 31st) refused it; one from 2 November is not yet in force.
    /// </summary>
    [Fact]
    public async Task At2330Utc_AJohannesburgTenant_AssignsARoleInForceFromItsOwnToday_BR040()
    {
        await using var host = Host(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero));
        var today = await DatedRole(DatabaseFixture.TenantA, November1, null);
        var tomorrow = await DatedRole(DatabaseFixture.TenantA, November2, null);

        var user = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dated-user-2330");
        (await Assign(host, DatabaseFixture.TenantA, user, today)).ShouldBe("204");
        (await Assign(host, DatabaseFixture.TenantA, user, tomorrow)).ShouldBe("ROLE_NOT_FOUND");

        // In effect for the user too: the permission resolver and /me judge the assignment by the same business date.
        var me = await (await host.ClientFor(DatabaseFixture.TenantA, "dated-user-2330").GetAsync("/api/v1/identity/me")).Json();
        me.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("roleCode").GetString() == today).GetProperty("inEffect").GetBoolean().ShouldBeTrue();
    }

    /// <summary>00:30 UTC on 1 November is 02:30 in Johannesburg: a role ending on 1 November is no longer in force, one from it is.</summary>
    [Fact]
    public async Task At0030Utc_AJohannesburgTenant_JudgesStartAndEndByItsOwnToday_BR040()
    {
        await using var host = Host(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero));
        var started = await DatedRole(DatabaseFixture.TenantA, November1, null);
        var ended = await DatedRole(DatabaseFixture.TenantA, From, November1);

        var user = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dated-user-0030");
        (await Assign(host, DatabaseFixture.TenantA, user, started)).ShouldBe("204");
        (await Assign(host, DatabaseFixture.TenantA, user, ended)).ShouldBe("ROLE_NOT_FOUND");
    }

    /// <summary>
    /// 21:30 UTC on 31 October: 00:30 on 1 November in Nairobi (UTC+3), still 23:30 on the 31st in Johannesburg. The same
    /// role, in force from 1 November, is assigned in the UTC+3 tenant and refused in the UTC+2 one.
    /// </summary>
    [Fact]
    public async Task At2130Utc_AUtcPlus3Tenant_IsAlreadyOnTheNextDay_AndAUtcPlus2TenantIsNot_BR040()
    {
        await using var host = Host(new DateTimeOffset(2026, 10, 31, 21, 30, 0, TimeSpan.Zero));
        var nairobi = await DatedRole(DatabaseFixture.TenantB, November1, null);
        var johannesburg = await DatedRole(DatabaseFixture.TenantA, November1, null);
        var beforeMidnight = await DatedRole(DatabaseFixture.TenantA, October31, November1);

        var b = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "dated-user-eat");
        var a = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dated-user-sast");
        (await Assign(host, DatabaseFixture.TenantB, b, nairobi)).ShouldBe("204");
        (await Assign(host, DatabaseFixture.TenantA, a, johannesburg)).ShouldBe("ROLE_NOT_FOUND");
        (await Assign(host, DatabaseFixture.TenantA, a, beforeMidnight)).ShouldBe("204");
    }

    private InsysApiFactory Host(DateTimeOffset instant) => new(fixture.Database, time: new FakeTimeProvider(instant));

    /// <summary>A role granting the dead letters screen, its one version in force from <paramref name="from"/> until <paramref name="to"/>.</summary>
    private async Task<string> DatedRole(Guid tenant, DateOnly from, DateOnly? to)
    {
        var role = "DATED_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, effective_to, record_status, created_by, change_reason, role_code)
            VALUES (@t, @role, 1, @from, @to, 'EFFECTIVE', 'seed', 'BR-040', @role);
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            VALUES (@t, @role || ':INTEGRATION_DEAD_LETTERS', 1, @grantFrom, 'EFFECTIVE', 'seed', 'BR-040', @role, 'INTEGRATION_DEAD_LETTERS');
            """, ("t", tenant), ("role", role), ("from", from), ("to", (object?)to ?? DBNull.Value), ("grantFrom", From));
        return role;
    }

    /// <summary>"204", or the refusal's code.</summary>
    private static async Task<string> Assign(InsysApiFactory host, Guid tenant, Guid userId, string role)
    {
        var admin = host.ClientFor(tenant, "dated-admin");
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        user.StatusCode.ShouldBe(HttpStatusCode.OK, await user.Content.ReadAsStringAsync());
        var assigned = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = role }, ifMatch: user.ETag());
        return assigned.StatusCode == HttpStatusCode.NoContent ? "204" : (await assigned.Code())!;
    }
}
