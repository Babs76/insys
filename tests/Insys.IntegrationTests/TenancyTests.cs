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
using Insys.Tenancy.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>Tenant onboarding, suspension, reactivation and termination as defined workflows (TEN-008), for platform operators only (TEN-007).</summary>
public sealed class TenancyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string OperatorAdmin = "tenancy-operator";
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin,
            TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Onboarding_ThenActivation_ThenSuspension_BlocksTheTenantsUsers_TEN008()
    {
        var client = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
        var registered = await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = "ACME", legalName = "Acme Insurance Ltd", isolationProfile = "B" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/tenancy/tenants/{tenantId}";

        var tenant = await client.GetAsync(url);
        var body = await tenant.Json();
        body.GetProperty("status").GetString().ShouldBe("Onboarding");
        body.GetProperty("onboardingStep").GetString().ShouldBe("PLATFORM_REFERENCE_DATA");

        var early = await client.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: tenant.ETag());
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await early.Code()).ShouldBe("ONBOARDING_INCOMPLETE");

        var outOfOrder = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step = "PRODUCT_FACTORY" }, ifMatch: tenant.ETag());
        (await outOfOrder.Code()).ShouldBe("ONBOARDING_STEP_OUT_OF_ORDER");

        var etag = tenant.ETag();
        foreach (var step in body.GetProperty("onboardingSteps").EnumerateArray().Select(s => s.GetString()))
        {
            var completed = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step }, ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            etag = completed.ETag();
        }

        (await client.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var active = await client.GetAsync(url);
        (await active.Json()).GetProperty("status").GetString().ShouldBe("Active");

        // A user of the new tenant can act while it is active.
        await fixture.Database.UserWithPermissions(tenantId, "acme-clerk", IntegrationPermissions.DeadLetterRead);
        var clerk = _factory.ClientFor(tenantId, "acme-clerk");
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.Send(HttpMethod.Post, $"{url}/suspend", new { reason = "Unpaid fees" }, ifMatch: active.ETag())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM tenant.outbox_event WHERE tenant_id = @t AND event_type = 'tenant.TenantSuspended'",
            ("t", tenantId))).ShouldBe(1);

        // Identity consumes TenantSuspended and drops the tenant's cached permissions (iam consumes TenantSuspended).
        await _factory.DrainOutbox();
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var history = (await (await client.GetAsync(url)).Json()).GetProperty("statusHistory").EnumerateArray()
            .Select(h => $"{h.GetProperty("fromStatus").GetString()}->{h.GetProperty("toStatus").GetString()}").ToList();
        history.ShouldBe(["Onboarding->Active", "Active->Suspended"]);
    }

    [Fact]
    public async Task TenantAdministration_IsForThePlatformOperatorOnly_TEN007()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "tenant-a-admin",
            TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update);
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "tenant-a-admin");

        (await client.GetAsync("/api/v1/tenancy/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = "EVIL", legalName = "Evil", isolationProfile = "A" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TenantCode_IsUnique_TEN001()
    {
        var client = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
        (await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = "UNIQ", legalName = "One", isolationProfile = "A" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = "UNIQ", legalName = "Two", isolationProfile = "A" }, idempotent: true);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Code()).ShouldBe("TENANT_CODE_TAKEN");
    }

    [Fact]
    public async Task ChangeIsolationProfile_RequiresAnApprovedMigration()
    {
        var client = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
        var registered = await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = "MIGR", legalName = "Migrating", isolationProfile = "A" },
            idempotent: true);
        var url = $"/api/v1/tenancy/tenants/{(await registered.Json()).GetProperty("id").GetGuid()}";

        var refused = await client.Send(HttpMethod.Post, $"{url}/change-isolation-profile", new { isolationProfile = "C", approvedMigrationRef = "" }, ifMatch: "\"1\"");
        (await refused.Code()).ShouldBe("DATA_MIGRATION_REQUIRED");
        (await client.Send(HttpMethod.Post, $"{url}/change-isolation-profile", new { isolationProfile = "C", approvedMigrationRef = "MIG-42" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
