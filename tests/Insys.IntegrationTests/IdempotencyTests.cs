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
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.IntegrationTests.TestModule;
using Insys.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>The Idempotency-Key filter and store through the real API pipeline (INT-003, PAY-001, DBA-008).</summary>
public sealed class IdempotencyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(TestTokens.HostSettings(fixture.Database));
        builder.Services.AddInsysInfrastructure(builder.Configuration);
        builder.Services.AddInsysApi();
        builder.Services.AddSingleton<IPrincipalResolver, WidgetMakers>();
        builder.Services.AddModuleDbContext<TestDbContext>(TestDbContext.SchemaName, "TestModule");
        builder.Services.AddModuleHandlers<TestDbContext>(typeof(TestDbContext).Assembly);
        builder.Services.AddDomainEventMapper<WidgetEventMapper>(TestDbContext.SchemaName);

        _app = builder.Build();
        _app.UseInsysApi();
        var v1 = _app.MapInsysV1();
        v1.MapPost("/test/widgets", async (WidgetRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CreateWidget(request.Name), ct))
                    .ToHttp(id => Results.Created($"/api/v1/test/widgets/{id}", new { id })))
            .RequirePermission("widget.create")
            .RequireIdempotencyKey();
        await _app.StartAsync();
        await _app.Services.GetRequiredService<EndpointWarmup>().Completed;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Create_WithoutIdempotencyKey_Is422_INT003()
    {
        var response = await Client(DatabaseFixture.TenantA).PostAsJsonAsync("/api/v1/test/widgets", new WidgetRequest("no-key"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("IDEMPOTENCY_KEY_REQUIRED");
    }

    [Fact]
    public async Task Replay_WithSameKeyAndRequest_ReturnsOriginalResponse_AndRunsOnce_INT003()
    {
        var key = Guid.NewGuid().ToString();
        var client = Client(DatabaseFixture.TenantA);

        var first = await Post(client, key, "once");
        var second = await Post(client, key, "once");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Location.ShouldBe(first.Headers.Location);
        (await second.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        second.Headers.GetValues(IdempotencyFilter.ReplayedHeader).ShouldBe(["true"]);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.widget WHERE name = 'once'")).ShouldBe(1);
    }

    [Fact]
    public async Task Reuse_WithSameKeyAndDifferentRequest_Is422_INT003()
    {
        var key = Guid.NewGuid().ToString();
        var client = Client(DatabaseFixture.TenantA);
        (await Post(client, key, "original")).StatusCode.ShouldBe(HttpStatusCode.Created);

        var reused = await Post(client, key, "different");

        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await reused.Content.ReadAsStringAsync()).ShouldContain("IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task ConcurrentRequests_WithTheSameKey_RunTheOperationOnce_INT003()
    {
        var key = Guid.NewGuid().ToString();
        var client = Client(DatabaseFixture.TenantA);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(client, key, "parallel")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.widget WHERE name = 'parallel'")).ShouldBe(1);
    }

    [Fact]
    public async Task Keys_AreScopedPerTenant_TEN004()
    {
        var key = Guid.NewGuid().ToString();

        (await Post(Client(DatabaseFixture.TenantA), key, "per-tenant")).StatusCode.ShouldBe(HttpStatusCode.Created);
        var otherTenant = await Post(Client(DatabaseFixture.TenantB), key, "per-tenant");

        otherTenant.StatusCode.ShouldBe(HttpStatusCode.Created);
        otherTenant.Headers.Contains(IdempotencyFilter.ReplayedHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task FailedValidation_IsStored_AndReplayed_INT003()
    {
        var key = Guid.NewGuid().ToString();
        var client = Client(DatabaseFixture.TenantA);

        (await Post(client, key, "")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var replay = await Post(client, key, "");

        replay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        replay.Headers.GetValues(IdempotencyFilter.ReplayedHeader).ShouldBe(["true"]);
    }

    private HttpClient Client(Guid tenant)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(tenant, "user-1"));
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string key, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/test/widgets") { Content = JsonContent.Create(new WidgetRequest(name)) };
        request.Headers.Add(IdempotencyFilter.HeaderName, key);
        return client.SendAsync(request);
    }

    public sealed record WidgetRequest(string Name);

    /// <summary>Grants widget.create to every subject: this host has no Identity module, and these tests are about the filter.</summary>
    private sealed class WidgetMakers : IPrincipalResolver
    {
        public Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedPrincipal?>(new ResolvedPrincipal(Guid.CreateVersion7(), new HashSet<string> { "widget.create" }));
    }
}
