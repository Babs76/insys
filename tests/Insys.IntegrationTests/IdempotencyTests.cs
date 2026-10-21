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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(TestTokens.HostSettings(fixture.Database.AppConnectionString));
        builder.Services.AddInsysInfrastructure(builder.Configuration);
        builder.Services.AddInsysApi();
        builder.Services.AddSingleton<ITenantDirectory, RegisterTenantDirectory>();
        builder.Services.AddModuleDbContext<TestDbContext>(TestDbContext.SchemaName);
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(tenant, "user-1", "widget.create"));
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string key, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/test/widgets") { Content = JsonContent.Create(new WidgetRequest(name)) };
        request.Headers.Add(IdempotencyFilter.HeaderName, key);
        return client.SendAsync(request);
    }

    public sealed record WidgetRequest(string Name);
}
