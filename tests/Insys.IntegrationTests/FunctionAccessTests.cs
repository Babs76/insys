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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.Integration.Infrastructure.Parking;
using Insys.Life.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Function-based access (ADR-026, addenda/function-catalogue.md §2–§4, §7) through the whole host: the menu, the order
/// of refusals, cache eviction on activation, privileged functions, JOB functions, and the conversion of permission sets
/// with no change in what anyone can do. Each test uses a tenant of its own, so its settings touch no other test.
/// </summary>
public sealed class FunctionAccessTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Today = TestClock.Today;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Menu_ShowsTheScreensTheUserHolds_WithTheirActions_AndNoActionWithoutItsScreen_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var action = await ChildOf("LIFE_POLICIES");
        var orphan = await ChildOf("PARTIES");
        await fixture.Database.UserWithFunctions(tenant, "menu-user", "LIFE_POLICIES", action, orphan);

        var menu = await Menu(tenant, "menu-user");

        menu.GetProperty("audience").GetString().ShouldBe("STAFF");
        var domains = menu.GetProperty("domains").EnumerateArray().ToList();
        domains.Select(d => d.GetProperty("code").GetString()).ShouldBe(["LIFE"], "an action shows only under a screen the user holds");
        var screen = domains[0].GetProperty("subDomains").EnumerateArray().Single().GetProperty("screens").EnumerateArray().Single();
        screen.GetProperty("functionCode").GetString().ShouldBe("LIFE_POLICIES");
        screen.GetProperty("routeKey").GetString().ShouldBe("life/policies");
        screen.GetProperty("labelKey").GetString().ShouldBe("function.LIFE_POLICIES");
        screen.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("functionCode").GetString()).ShouldBe([action]);
    }

    [Fact]
    public async Task DisabledFunction_IsRefusedWhenCalled_AndMissingFromTheMenu_ADR026()
    {
        var (tenant, _) = await NewTenant();
        await fixture.Database.UserWithFunctions(tenant, "disabled-user", "PARTIES");
        await Disable(tenant, "PARTIES");

        var response = await _factory.ClientFor(tenant, "disabled-user").GetAsync($"/api/v1/party/parties/{Guid.CreateVersion7()}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Code()).ShouldBe("FUNCTION_DISABLED");
        (await Menu(tenant, "disabled-user")).GetProperty("domains").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task FunctionOfAFeatureTheTenantIsNotEntitledTo_IsRefused_AndMissingFromTheMenu_OQ091()
    {
        var (tenant, code) = await NewTenant();
        await fixture.Database.UserWithFunctions(tenant, "unentitled-user", "LIFE_POLICIES");
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_feature_entitlements SET record_status = 'RETIRED' WHERE tenant = @c AND feature = 'LIFE'",
            ("c", code));

        var response = await _factory.ClientFor(tenant, "unentitled-user").GetAsync("/api/v1/life/policies");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Code()).ShouldBe("FEATURE_NOT_ENTITLED");
        (await Menu(tenant, "unentitled-user")).GetProperty("domains").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task PermissionHeldThroughAnotherFunction_DoesNotGrantTheOperationsFunction_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var portal = await RequiredPermissions("BROKER_PORTAL_BILLING_INVOICES");
        (await RequiredPermissions("BILLING_INVOICES")).ShouldBeSubsetOf(portal, "the broker portal's invoices need the staff screen's permission");
        await fixture.Database.UserWithFunctions(tenant, "portal-only", "BROKER_PORTAL_BILLING_INVOICES");

        var response = await _factory.ClientFor(tenant, "portal-only").GetAsync($"/api/v1/billing/invoices/{Guid.CreateVersion7()}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
    }

    [Fact]
    public async Task WhileOnboarding_TheMenuShowsOnlyFunctionsOpenThen_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var closed = await fixture.Database.ScalarAsOwner<string>("""
            SELECT function_code FROM iam.ref_function_catalogue
            WHERE tenant_id IS NULL AND parent_function_code = 'LIFE_POLICIES' AND NOT allowed_while_onboarding AND record_status = 'ACTIVE' AND effective_to IS NULL
            ORDER BY function_code LIMIT 1
            """);
        closed.ShouldNotBeNull("Life policy actions are business transactions");
        await fixture.Database.UserWithFunctions(tenant, "onboarding-user", "LIFE_POLICIES", closed);
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ONBOARDING' WHERE id = @t", ("t", tenant));

        var screen = (await Menu(tenant, "onboarding-user")).GetProperty("domains").EnumerateArray().Single().GetProperty("subDomains").EnumerateArray().Single()
            .GetProperty("screens").EnumerateArray().Single();

        screen.GetProperty("functionCode").GetString().ShouldBe("LIFE_POLICIES", "reading is open while Onboarding");
        screen.GetProperty("actions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task AnActivatedGrant_ChangesTheMenuAtOnce_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var user = await fixture.Database.UserWithFunctions(tenant, "evict-user", "LIFE_POLICIES");
        (await Menu(tenant, "evict-user")).GetProperty("domains").GetArrayLength().ShouldBe(1);

        var role = await fixture.Database.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE tenant_id = @t AND user_id = @u",
            ("t", tenant), ("u", user));
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            VALUES (@t, @r || ':PARTIES', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @r, 'PARTIES')
            """, ("t", tenant), ("r", role!));
        await Activated(tenant, "iam", "cfg_role_function_assignments");

        (await Menu(tenant, "evict-user")).GetProperty("domains").EnumerateArray().Select(d => d.GetProperty("code").GetString())
            .ShouldBe(["CUSTOMERS", "LIFE"], ignoreOrder: true);
    }

    [Fact]
    public async Task ARoleHoldingAPrivilegedFunction_IsPrivileged_AndNeedsMfaEnrolment_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var privileged = await fixture.Database.ScalarAsOwner<string>(
            "SELECT function_code FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND privileged AND record_status = 'ACTIVE' AND effective_to IS NULL LIMIT 1");
        privileged.ShouldNotBeNull();
        var role = await fixture.Database.RoleWithFunctions(tenant, privileged);
        await fixture.Database.UserWithPermissions(tenant, "mfa-admin", "user.read", "user.update");
        var userId = await fixture.Database.UserWithPermissions(tenant, "no-mfa");
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_account SET mfa_enrolled = false WHERE id = @u", ("u", userId));

        var admin = _factory.ClientFor(tenant, "mfa-admin");
        var roles = await (await admin.GetAsync("/api/v1/identity/roles")).Json();
        roles.EnumerateArray().Single(r => r.GetProperty("roleCode").GetString() == role).GetProperty("privileged").GetBoolean().ShouldBeTrue();
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        var refused = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = role }, ifMatch: user.ETag());

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Code()).ShouldBe("MFA_REQUIRED");
    }

    [Fact]
    public async Task AConsumerWhoseJobFunctionIsDisabled_ParksItsDelivery_UntilTheFunctionIsEnabled_ADR026()
    {
        const string consumer = "analytics.ProjectionPolicies.LifePolicyIssued";
        var (tenant, _) = await NewTenant();
        await Disable(tenant, "ANALYTICS_EVENT_PROCESSING");

        var policy = Guid.CreateVersion7();
        var issued = new LifePolicyIssued(policy, $"LIF-{policy.ToString("N")[..8]}", Guid.CreateVersion7(), "LIFE-JOB", 1, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "MONTHLY", "DEBIT_ORDER", "ZAR", 100m, Today, null, 500_000m);
        var envelope = Envelope(tenant, issued, "life.LifePolicyIssued", "LifePolicy", policy);
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None);

        (await Parked(envelope.EventId, consumer)).ShouldBe($"{PrerequisiteCodes.FunctionDisabled} ANALYTICS_EVENT_PROCESSING");

        await fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_tenant_function_availability SET record_status = 'RETIRED' WHERE tenant_id = @t", ("t", tenant));
        await Activated(tenant, "tenant", "cfg_tenant_function_availability");
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(tenant, CancellationToken.None);

        (await Parked(envelope.EventId, consumer)).ShouldNotBe($"{PrerequisiteCodes.FunctionDisabled} ANALYTICS_EVENT_PROCESSING",
            "enabled again, the delivery is retried; it parks only on what the new tenant still lacks");
    }

    [Fact]
    public async Task SwitchingOffABillingOrPaymentsDailyJob_IsAPrivilegedFunction_OQ098()
    {
        const string url = "/api/v1/tenancy/static/tenant-function-availability";
        var (tenant, _) = await NewTenant();
        await fixture.Database.UserWithPermissions(tenant, "config-maker", "static_data.read", "static_data.create", "static_data.update");
        var maker = _factory.ClientFor(tenant, "config-maker");

        async Task<HttpResponseMessage> Submit(string function)
        {
            var created = await maker.Send(HttpMethod.Post, url, new { code = function, functionCode = function, enabled = false, changeReason = "Switch off" },
                idempotent: true);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return await maker.Send(HttpMethod.Post, $"{url}/{function}/versions/1/submit", ifMatch: created.ETag());
        }

        var refused = await Submit("BILLING_DAILY_JOBS");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
        (await Submit("NOTIFICATION_SENDING")).IsSuccessStatusCode.ShouldBeTrue("only the Billing and Payments daily jobs need the privileged function");

        var role = await fixture.Database.ScalarAsOwner<string>(
            "SELECT role_code FROM iam.user_role_assignment a JOIN iam.user_account u ON u.id = a.user_id AND u.tenant_id = a.tenant_id WHERE a.tenant_id = @t AND u.external_subject = 'config-maker'",
            ("t", tenant));
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            VALUES (@t, @r || ':MONEY', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @r, 'TENANT_SWITCH_OFF_MONEY_JOBS')
            """, ("t", tenant), ("r", role!));
        await Activated(tenant, "iam", "cfg_role_function_assignments");
        (await Submit("PAYMENTS_DAILY_JOBS")).IsSuccessStatusCode.ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<bool>(
                "SELECT privileged FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND function_code = 'TENANT_SWITCH_OFF_MONEY_JOBS' AND record_status = 'ACTIVE'"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task AJobFunctionSwitchedOff_ShowsInTheOperationsView_AndInHealthDetail_OQ098()
    {
        var (tenant, code) = await NewTenant();
        await Disable(tenant, "NOTIFICATION_SENDING");
        await fixture.Database.UserWithPermissions(tenant, "ops", Insys.Integration.Contracts.IntegrationPermissions.DeadLetterRead);

        var summary = await (await _factory.ClientFor(tenant, "ops").GetAsync("/api/v1/integration/deliveries/summary")).Json();
        var off = summary.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("code").GetString() == "JOB_FUNCTIONS_OFF");
        off.GetProperty("count").GetInt64().ShouldBe(1L);
        off.GetProperty("by")[0].GetProperty("key").GetString().ShouldBe("NOTIFICATION_SENDING");

        var health = (await (await _factory.CreateClient().GetAsync("/health")).Json()).GetProperty("checks").GetProperty("job-functions");
        health.GetProperty("status").GetString().ShouldBe("Healthy");
        health.GetProperty("data").GetProperty(code).EnumerateArray().Select(j => j.GetString()).ShouldBe(["NOTIFICATION_SENDING"]);
    }

    /// <summary>
    /// §7: every permission set of a role, as the release finds it, gives the same permissions once converted to functions;
    /// segregation-of-duties rules and channel rows are converted with it. The sets are the platform's own (the operator's
    /// PLATFORM_ADMIN, a first tenant administrator, the onboarding seeds) and every permission on its own, except static
    /// data create and update, which one function needs together.
    /// </summary>
    [Fact]
    public async Task ConvertingPermissionSets_KeepsEveryRolesPermissions_AndConvertsSodAndChannelRows_ADR026()
    {
        var (tenant, _) = await NewTenant();
        var all = (await fixture.Database.ScalarAsOwner<string[]>("""
            SELECT array_agg(DISTINCT p ORDER BY p) FROM iam.ref_function_catalogue c CROSS JOIN LATERAL unnest(c.required_permissions) AS p
            WHERE c.tenant_id IS NULL AND c.record_status = 'ACTIVE' AND c.effective_to IS NULL
            """))!;
        var sets = new Dictionary<string, string[]>
        {
            ["PLATFORM_ADMIN"] = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "user.update", "static_data.read", "static_data.create",
                "static_data.update", "static_data.approve", "change_request.read", "audit_event.read", "dead_letter.read", "dead_letter.update"],
            ["TENANT_ADMIN"] = ["dead_letter.read", "user.create", "user.read", "user.update"],
            ["CLERK"] = ["dead_letter.read"],
            ["UNDERWRITER"] = ["general_quote.create", "general_quote.read"],
            ["STATIC_MAINTAINER"] = ["static_data.create", "static_data.update"],
        };
        foreach (var permission in all.Where(p => p is not ("static_data.create" or "static_data.update")))
        {
            sets[$"ONLY_{sets.Count}"] = [permission];
        }

        foreach (var (role, permissions) in sets)
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, permissions)
                VALUES (@t, @r, 1, current_date - 1, 'EFFECTIVE', 'seed', 'before', @r, @p)
                """, ("t", tenant), ("r", role), ("p", permissions));
        }

        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_segregation_of_duties_conflict_rules
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, permission_a, permission_b, conflict)
            VALUES (@t, 'SOD_QUOTE', 1, current_date - 1, 'EFFECTIVE', 'seed', 'before', 'general_quote.create', 'dead_letter.update', 'BLOCK');
            INSERT INTO channel.cfg_channel_transaction_permissions
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, channel_code, role_code, transaction_type, allowed)
            VALUES (@t, 'CP_QUOTE', 1, current_date - 1, 'EFFECTIVE', 'seed', 'before', 'STF', 'UNDERWRITER', 'general_quote.create', true);
            """, ("t", tenant));

        await fixture.Database.AdoptFunctionCatalogue();

        var lost = new List<string>();
        foreach (var (role, permissions) in sets)
        {
            var after = (await fixture.Database.ScalarAsOwner<string[]>("""
                SELECT coalesce(array_agg(DISTINCT p ORDER BY p), ARRAY[]::text[]) FROM iam.cfg_role_function_assignments g
                JOIN iam.ref_function_catalogue c ON c.tenant_id IS NULL AND c.function_code = g.function_code AND c.record_status = 'ACTIVE' AND c.effective_to IS NULL
                CROSS JOIN LATERAL unnest(c.required_permissions) AS p
                WHERE g.tenant_id = @t AND g.role_code = @r
                """, ("t", tenant), ("r", role)))!;
            if (!after.Order(StringComparer.Ordinal).SequenceEqual(permissions.Distinct().Order(StringComparer.Ordinal)))
            {
                lost.Add($"{role}: before [{string.Join(", ", permissions)}], after [{string.Join(", ", after)}]");
            }
        }

        lost.ShouldBeEmpty();
        (await fixture.Database.ScalarAsOwner<long>(
                "SELECT count(*) FROM iam.cfg_roles_and_permission_sets WHERE tenant_id = @t AND record_status = 'EFFECTIVE' AND permissions IS NOT NULL", ("t", tenant)))
            .ShouldBe(0L, "every role in force holds functions now");

        // The SoD rule pairs every function needing one permission with every function needing the other.
        var pairs = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM iam.cfg_segregation_of_duties_conflict_rules r
            JOIN iam.ref_function_catalogue a ON a.tenant_id IS NULL AND a.function_code = r.function_a AND 'general_quote.create' = ANY(a.required_permissions)
            JOIN iam.ref_function_catalogue b ON b.tenant_id IS NULL AND b.function_code = r.function_b AND 'dead_letter.update' = ANY(b.required_permissions)
            WHERE r.tenant_id = @t AND r.record_status = 'EFFECTIVE' AND a.record_status = 'ACTIVE' AND b.record_status = 'ACTIVE'
            """, ("t", tenant));
        pairs.ShouldBeGreaterThan(0L);
        (await fixture.Database.ScalarAsOwner<long>(
                "SELECT count(*) FROM iam.cfg_segregation_of_duties_conflict_rules WHERE tenant_id = @t AND record_status = 'EFFECTIVE' AND function_a IS NULL",
                ("t", tenant)))
            .ShouldBe(0L);

        // The channel row allows every ACTION that needs the transaction's permission, for the same role.
        var expected = await fixture.Database.ScalarAsOwner<string[]>("""
            SELECT array_agg(function_code ORDER BY function_code) FROM iam.ref_function_catalogue
            WHERE tenant_id IS NULL AND function_kind = 'ACTION' AND 'general_quote.create' = ANY(required_permissions) AND record_status = 'ACTIVE' AND effective_to IS NULL
            """);
        (await fixture.Database.ScalarAsOwner<string[]>("""
                SELECT array_agg(function_code ORDER BY function_code) FROM channel.cfg_channel_transaction_permissions
                WHERE tenant_id = @t AND record_status = 'EFFECTIVE' AND role_code = 'UNDERWRITER' AND allowed
                """, ("t", tenant)))
            .ShouldBe(expected);
    }

    private async Task<(Guid Id, string Code)> NewTenant()
    {
        var id = Guid.CreateVersion7();
        var code = "F" + id.ToString("N")[^8..].ToUpperInvariant();
        await fixture.Database.SeedTenant(id, code);
        return (id, code);
    }

    private async Task<JsonElement> Menu(Guid tenant, string subject)
    {
        var response = await _factory.ClientFor(tenant, subject).GetAsync("/api/v1/identity/me/menu");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var menu = await response.Json();
        DocumentedSchema.ResponseProblems(menu, "core", "GetMyMenu").ShouldBeEmpty();
        return menu;
    }

    private async Task<string> ChildOf(string screen) =>
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT function_code FROM iam.ref_function_catalogue
            WHERE tenant_id IS NULL AND parent_function_code = @s AND record_status = 'ACTIVE' AND effective_to IS NULL ORDER BY function_code LIMIT 1
            """, ("s", screen)))!;

    private async Task<string[]> RequiredPermissions(string function) =>
        (await fixture.Database.ScalarAsOwner<string[]>(
            "SELECT required_permissions FROM iam.ref_function_catalogue WHERE tenant_id IS NULL AND function_code = @f AND record_status = 'ACTIVE' AND effective_to IS NULL",
            ("f", function)))!;

    private Task Disable(Guid tenant, string function) =>
        fixture.Database.ExecuteAsOwner("""
            INSERT INTO tenant.cfg_tenant_function_availability (tenant_id, code, version, effective_from, record_status, created_by, change_reason, function_code, enabled)
            VALUES (@t, @f, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @f, false)
            """, ("t", tenant), ("f", function));

    private Task Activated(Guid tenant, string schema, string table)
    {
        var activated = new StaticDataVersionActivated(null, new StaticRecordRef(schema, table, table.ToUpperInvariant(), schema, table, Guid.CreateVersion7(), "ANY", 1, false),
            Today, null);
        return _factory.Services.GetRequiredService<IEventBus>().Publish(
            Envelope(tenant, activated, "sdm.StaticDataVersionActivated", "StaticRecord", Guid.CreateVersion7()), CancellationToken.None);
    }

    private Task<string?> Parked(Guid eventId, string consumer) =>
        fixture.Database.ScalarAsOwner<string>(
            "SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c AND status = 'PARKED'",
            ("e", eventId), ("c", consumer));

    private static EventEnvelope Envelope<T>(Guid tenant, T integrationEvent, string type, string aggregateType, Guid aggregateId) =>
        new(Guid.CreateVersion7(), tenant, type, 1, DateTimeOffset.UtcNow, aggregateType, aggregateId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web));
}
