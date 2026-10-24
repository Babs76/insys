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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.Payments.Application;
using Insys.Payments.Contracts;
using Insys.Payments.Domain.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Insys.IntegrationTests;

/// <summary>
/// ADR-023 revision 2: a provider calls its tenant's callback address with no token. The endpoint key gives the tenant,
/// and only a signature keyed to that tenant and provider, over a fresh timestamp and a new callback ID, is applied.
/// Keys rotate with an overlap and can be revoked, and neither the key nor the secret is ever stored in plain text or logged.
/// </summary>
public sealed class ProviderCallbackTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly CapturedLogs _logs = new();
    private WebApplicationFactory<Program> _app = null!;

    public async ValueTask InitializeAsync()
    {
        // The class's own host, with its logs captured; no shared host runs against this database.
        _app = new InsysApiFactory(fixture.Database).WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(_logs)));
        string[] permissions =
        [
            Billing.Contracts.BillingPermissions.AccountCreate, Billing.Contracts.BillingPermissions.AccountUpdate, Billing.Contracts.BillingPermissions.InvoiceCreate,
            PaymentPermissions.PaymentRead, PaymentPermissions.CallbackEndpointRead, PaymentPermissions.CallbackEndpointUpdate,
        ];
        foreach (var tenant in new[] { DatabaseFixture.TenantA, DatabaseFixture.TenantB })
        {
            await fixture.Database.SeedBillingAndPayments(tenant, Today.AddDays(-30));
            await fixture.Database.UserWithPermissions(tenant, "callback-admin", permissions);
        }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task SignedCallback_AtItsTenantsAddress_IsApplied_WithNoToken_ADR023()
    {
        var endpoint = await Issue(DatabaseFixture.TenantA);
        var payment = await PendingPayment(DatabaseFixture.TenantA);

        var response = await Callback(endpoint.CallbackPath, endpoint.SigningSecret, await Body(payment, "PAID"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await Status(payment)).ShouldBe("SUCCEEDED");
    }

    [Fact]
    public async Task AnotherTenantsKeyOrSecret_AnOldTimestamp_OrAnUnknownKey_IsRefused_AndNothingApplied_ADR023()
    {
        var a = await Issue(DatabaseFixture.TenantA);
        var b = await Issue(DatabaseFixture.TenantB);
        var payment = await PendingPayment(DatabaseFixture.TenantA);
        var body = await Body(payment, "PAID");

        // B's address with A's secret, and A's address with B's secret: the key decides the tenant, and the secret must be that tenant's.
        foreach (var (path, secret) in new[] { (b.CallbackPath, a.SigningSecret), (a.CallbackPath, b.SigningSecret) })
        {
            (await Callback(path, secret, body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await Callback(a.CallbackPath, a.SigningSecret, body, sentAt: DateTimeOffset.UtcNow.AddMinutes(-10))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var unknown = await Callback("/api/v1/payments/callbacks/" + CallbackKeys.NewSecret(), a.SigningSecret, body);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await unknown.Code()).ShouldBe(Insys.BuildingBlocks.Domain.ErrorCodes.CallbackSignatureInvalid);

        (await Status(payment)).ShouldBe("PENDING");
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM payment.provider_callback WHERE tenant_id = @t AND NOT signature_valid AND received_at > now() - interval '1 minute'",
            ("t", DatabaseFixture.TenantA))).ShouldBeGreaterThanOrEqualTo(2, "refused callbacks with a known key are recorded in its tenant");
    }

    [Fact]
    public async Task RepeatedCallbackId_IsRefused_AndAppliedOnce_ADR023()
    {
        var endpoint = await Issue(DatabaseFixture.TenantA);
        var payment = await PendingPayment(DatabaseFixture.TenantA);
        var body = await Body(payment, "PAID");
        var id = Guid.CreateVersion7().ToString();

        (await Callback(endpoint.CallbackPath, endpoint.SigningSecret, body, callbackId: id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await Callback(endpoint.CallbackPath, endpoint.SigningSecret, body, callbackId: id);

        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await replay.Code()).ShouldBe(Insys.BuildingBlocks.Domain.ErrorCodes.CallbackReplayed);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM payment.provider_callback WHERE payload ->> 'externalRef' = @r AND signature_valid",
            ("r", (await ExternalRef(payment))!))).ShouldBe(1);
    }

    [Fact]
    public async Task RotatedEndpoint_IsAcceptedUntilItsCutOver_AndARevokedOneAtOnceRefused_ADR023()
    {
        var first = await Issue(DatabaseFixture.TenantB);
        var second = await Issue(DatabaseFixture.TenantB);
        (await Callback(first.CallbackPath, first.SigningSecret, await Body(await PendingPayment(DatabaseFixture.TenantB), "PAID"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Callback(second.CallbackPath, second.SigningSecret, await Body(await PendingPayment(DatabaseFixture.TenantB), "PAID"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.Database.ExecuteAsOwner("UPDATE payment.callback_endpoint SET retires_at = now() - interval '1 second' WHERE id = @id",
            ("id", first.Endpoint.Id));
        (await Callback(first.CallbackPath, first.SigningSecret, await Body(await PendingPayment(DatabaseFixture.TenantB), "PAID"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        (await Client(DatabaseFixture.TenantB).PostAsync($"/api/v1/payments/callback-endpoints/{second.Endpoint.Id}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Callback(second.CallbackPath, second.SigningSecret, await Body(await PendingPayment(DatabaseFixture.TenantB), "PAID"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KeyAndSecret_AreNeverStoredInPlainText_ListedOrLogged_ADR023()
    {
        var endpoint = await Issue(DatabaseFixture.TenantA);
        await Callback(endpoint.CallbackPath, endpoint.SigningSecret, await Body(await PendingPayment(DatabaseFixture.TenantA), "PAID"));
        await Callback(endpoint.CallbackPath, "wrong-secret", await Body(await PendingPayment(DatabaseFixture.TenantA), "PAID"));
        var key = endpoint.EndpointKey;

        (await fixture.Database.ScalarAsOwner<string>("SELECT key_hash FROM payment.callback_endpoint WHERE id = @id", ("id", endpoint.Endpoint.Id)))
            .ShouldBe(CallbackKeys.Hash(key));
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM payment.callback_endpoint
            WHERE position(convert_to(@s, 'UTF8') IN secret_enc) > 0 OR key_hash = @k
            """, ("s", endpoint.SigningSecret), ("k", key))).ShouldBe(0);
        var listed = await (await Client(DatabaseFixture.TenantA).GetAsync("/api/v1/payments/callback-endpoints")).Content.ReadAsStringAsync();
        listed.ShouldNotContain(key);
        listed.ShouldNotContain(endpoint.SigningSecret);
        _logs.Messages.ShouldNotContain(m => m.Contains(key, StringComparison.Ordinal) || m.Contains(endpoint.SigningSecret, StringComparison.Ordinal));
        _logs.Messages.ShouldContain(m => m.Contains(endpoint.Endpoint.Id.ToString(), StringComparison.Ordinal), "a refusal is logged by endpoint ID");
    }

    private HttpClient Client(Guid tenant)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.For(tenant, "callback-admin"));
        return client;
    }

    private async Task<IssuedCallbackEndpoint> Issue(Guid tenant)
    {
        var response = await Client(tenant).PostAsJsonAsync("/api/v1/payments/callback-endpoints", new { providerCode = "SIMULATOR" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<IssuedCallbackEndpoint>(Json))!;
    }

    private async Task<Guid> PendingPayment(Guid tenant)
    {
        var (_, invoice) = await Client(tenant).IssueInvoice(50m, Today);
        await _app.Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>().ProcessOnce(CancellationToken.None);
        return await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
    }

    private Task<string?> ExternalRef(Guid payment) =>
        fixture.Database.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));

    private async Task<string> Body(Guid payment, string status) =>
        JsonSerializer.Serialize(new { externalRef = await ExternalRef(payment), status }, JsonSerializerOptions.Web);

    private Task<string?> Status(Guid payment) => fixture.Database.ScalarAsOwner<string>("SELECT status FROM payment.payment WHERE id = @p", ("p", payment));

    private Task<HttpResponseMessage> Callback(string path, string secret, string body, DateTimeOffset? sentAt = null, string? callbackId = null)
    {
        var timestamp = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var id = callbackId ?? Guid.CreateVersion7().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Callback-Timestamp", timestamp);
        request.Headers.Add("X-Callback-Id", id);
        request.Headers.Add("X-Signature", CallbackKeys.Sign(secret, timestamp, id, body));
        return _app.CreateClient().SendAsync(request);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
