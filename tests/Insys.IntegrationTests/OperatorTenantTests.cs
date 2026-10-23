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
using System.Net.Http.Json;
using Insys.BuildingBlocks.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>ADR-024: business modules refuse the platform operator tenant, reads and changes alike; platform modules stay open to it.</summary>
public sealed class OperatorTenantTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly string[] PlatformModules = ["Tenancy", "Identity", "Static data", "Audit", "Integration", "Workflow"];
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        string[] permissions = [Party.Contracts.PartyPermissions.PartyRead, Party.Contracts.PartyPermissions.PartyCreate, Integration.Contracts.IntegrationPermissions.DeadLetterRead];
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "operator-business", permissions);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "tenant-business", permissions);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void EveryBusinessModuleEndpoint_RefusesTheOperatorTenant_AndNoPlatformModuleEndpointDoes_ADR024()
    {
        var endpoints = _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v", StringComparison.Ordinal) == true)
            .Select(e => (Route: e.RoutePattern.RawText!, Tags: e.Metadata.GetOrderedMetadata<ITagsMetadata>().SelectMany(t => t.Tags).ToList(),
                Refused: e.Metadata.GetMetadata<OperatorTenantRefusedMetadata>() is not null))
            .ToList();
        endpoints.ShouldNotBeEmpty();

        // The Static data module's own routes (and its shared approval routes) are platform routes under /static-data and /{module}/static.
        static bool IsPlatform((string Route, List<string> Tags, bool Refused) e) =>
            e.Tags.Any(t => PlatformModules.Any(m => t == m || t == $"{m} static data"))
            || e.Route.Contains("/static-data/", StringComparison.Ordinal) || e.Route.Contains("/{module}/static/", StringComparison.Ordinal);
        endpoints.Where(e => !e.Refused && !IsPlatform(e)).Select(e => e.Route).ShouldBeEmpty("business module endpoints that do not refuse the operator tenant");
        endpoints.Where(e => e.Refused && IsPlatform(e)).Select(e => e.Route).ShouldBeEmpty("platform module endpoints that refuse the operator tenant");
    }

    [Fact]
    public async Task OperatorTenant_IsRefusedByABusinessModule_ForReadsAndChanges_WithAStableCode_ADR024()
    {
        var operatorClient = _factory.ClientFor(DatabaseFixture.Operator, "operator-business");

        var read = await operatorClient.PostAsJsonAsync("/api/v1/party/parties/search", new { name = "Smith" });
        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.Code()).ShouldBe("OPERATOR_TENANT_NOT_BUSINESS");

        var change = await operatorClient.PostAsJsonAsync("/api/v1/party/parties", new { partyType = "Person", firstName = "Op", lastName = "Erator" });
        change.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await change.Code()).ShouldBe("OPERATOR_TENANT_NOT_BUSINESS");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", ("t", DatabaseFixture.Operator))).ShouldBe(0);
    }

    [Fact]
    public async Task OperatorTenant_StillUsesThePlatformModules_AndABusinessTenantTheBusinessOnes_ADR024()
    {
        (await _factory.ClientFor(DatabaseFixture.Operator, "operator-business").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _factory.ClientFor(DatabaseFixture.Operator, "operator-business").GetAsync("/api/v1/integration/parked-deliveries")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _factory.ClientFor(DatabaseFixture.TenantA, "tenant-business").PostAsJsonAsync("/api/v1/party/parties/search", new { name = "Smith" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
