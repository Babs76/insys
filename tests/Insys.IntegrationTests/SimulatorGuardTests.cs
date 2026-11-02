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
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.Payments.Application;
using Insys.Payments.Contracts;
using Insys.Payments.Domain.Payments;
using Insys.Tenancy.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Insys.IntegrationTests;

/// <summary>Row 13: the simulator moves no money, so it is refused outside Development and Testing unless a deployment declares itself non-production.</summary>
public sealed class SimulatorGuardTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string DeclaredKey = "Payments:Simulator:DeclaredNonProduction";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Refuses once the host has started: the class's database names the simulator, which would stop a refusing host starting at all.</summary>
    private sealed class RefusesSimulator : IPaymentProviderPolicy
    {
        public volatile bool Refusing;

        public bool Allows(string providerCode) => !Refusing || providerCode != "SIMULATOR";
    }

    private static Dictionary<string, string?> ProductionLike(bool declared = false) => new()
    {
        ["Authentication:DevelopmentSigningKey"] = "", ["Testing:DailyJobRunner:Enabled"] = "false", [DeclaredKey] = declared ? "true" : "false",
    };

    private async Task NameTheSimulator(Guid tenant) =>
        await fixture.Database.SeedStatic(tenant, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", TestClock.Today.AddDays(-90),
            ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));

    private WebApplicationFactory<Program> Host(RefusesSimulator policy)
    {
        var app = new InsysApiFactory(fixture.Database)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IPaymentProviderPolicy>(policy)));
        _ = app.Server;
        return app;
    }

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenant, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.For(tenant, subject, DateTime.UtcNow));
        return client;
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task AHost_WhoseTenantNamesTheSimulator_RefusesToStart_OutsideDevelopmentAndTesting(string environment)
    {
        await NameTheSimulator(DatabaseFixture.TenantA);

        await using var factory = new InsysApiFactory(fixture.Database, ProductionLike(), environment);
        var refused = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        refused.Message.ShouldContain("SIMULATOR");
        refused.Message.ShouldContain(environment);
    }

    [Fact]
    public async Task Production_CannotDeclareItselfNonProduction()
    {
        await using var factory = new InsysApiFactory(fixture.Database, ProductionLike(declared: true), "Production");

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain(DeclaredKey);
    }

    [Fact]
    public async Task ADemo_DeclaredNonProduction_Starts_WithTheSimulator_ButNeverByDefault()
    {
        await NameTheSimulator(DatabaseFixture.TenantA);

        await using var factory = new InsysApiFactory(fixture.Database, ProductionLike(declared: true), "Demo");
        (await factory.CreateClient().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var undeclared = new InsysApiFactory(fixture.Database, ProductionLike(), "Demo");
        Should.Throw<InvalidOperationException>(() => undeclared.CreateClient()).Message.ShouldContain("SIMULATOR");
    }

    [Fact]
    public async Task SettingATenantsProviderToTheSimulator_IsRefused_WithItsCode()
    {
        var policy = new RefusesSimulator();
        await using var app = Host(policy);
        policy.Refusing = true;
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "sim-admin",
            ["static_data.read", "static_data.create", "static_data.update", PaymentPermissions.CallbackEndpointRead, PaymentPermissions.CallbackEndpointUpdate]);
        var admin = Admin(app, DatabaseFixture.TenantB, "sim-admin");

        var row = await admin.Send(HttpMethod.Post, "/api/v1/payments/static/payment-provider-and-bank-register", new
        {
            code = "SIMULATOR", providerCode = "SIMULATOR", type = "PAYMENT_GATEWAY", supportedMethods = new[] { "EFT" }, credentialRef = "vault:simulator", changeReason = "a test",
        }, idempotent: true);
        row.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await row.Content.ReadAsStringAsync());
        (await row.Code()).ShouldBe(PaymentErrorCodes.ProviderNotAllowed);

        var endpoint = await admin.Send(HttpMethod.Post, "/api/v1/payments/callback-endpoints", new { providerCode = "SIMULATOR" }, idempotent: true);
        endpoint.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await endpoint.Content.ReadAsStringAsync());
        (await endpoint.Code()).ShouldBe(PaymentErrorCodes.ProviderNotAllowed);
    }

    /// <summary>The start-up check reads active tenants only, so a suspended tenant naming the simulator can't come back while it is refused.</summary>
    [Fact]
    public async Task ReactivatingATenant_ThatNamesTheSimulator_IsRefused_WithItsCode()
    {
        var named = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(named, "SIMNAMED");
        await fixture.Database.SeedTenant(other, "SIMOTHER");
        await NameTheSimulator(named);
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'SUSPENDED' WHERE id = ANY(@ids)", ("ids", new[] { named, other }));
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "sim-operator", [TenancyPermissions.Read, TenancyPermissions.Update]);
        var policy = new RefusesSimulator();
        await using var app = Host(policy);
        policy.Refusing = true;
        var client = Admin(app, DatabaseFixture.Operator, "sim-operator");
        async Task<HttpResponseMessage> Reactivate(Guid tenant) => await client.Send(HttpMethod.Post, $"/api/v1/tenancy/tenants/{tenant}/reactivate",
            new { reason = "Back in service" }, ifMatch: (await client.GetAsync($"/api/v1/tenancy/tenants/{tenant}")).ETag());

        var refused = await Reactivate(named);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe(PaymentErrorCodes.ProviderNotAllowed);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", ("t", named))).ShouldBe("SUSPENDED");

        (await Reactivate(other)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        policy.Refusing = false;
        (await Reactivate(named)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>The payment and endpoint are made while the simulator is allowed; the signed callback then arrives when it isn't.</summary>
    [Fact]
    public async Task ASignedCallback_CannotConfirmAPayment_OnAProviderThatIsNotAllowed()
    {
        var tenant = TestTenants.A;
        await fixture.Database.SeedBillingAndPayments(tenant, TestClock.Today.AddDays(-30));
        await fixture.Database.UserWithPermissions(tenant, "sim-callback",
        [
            Billing.Contracts.BillingPermissions.AccountCreate, Billing.Contracts.BillingPermissions.AccountUpdate, Billing.Contracts.BillingPermissions.InvoiceCreate,
            PaymentPermissions.PaymentRead, PaymentPermissions.CallbackEndpointRead, PaymentPermissions.CallbackEndpointUpdate,
        ]);
        var policy = new RefusesSimulator();
        await using var app = Host(policy);
        var client = Admin(app, tenant, "sim-callback");
        var issued = await client.Send(HttpMethod.Post, "/api/v1/payments/callback-endpoints", new { providerCode = "SIMULATOR" }, idempotent: true);
        issued.StatusCode.ShouldBe(HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());
        var endpoint = (await issued.Content.ReadFromJsonAsync<IssuedCallbackEndpoint>(Json))!;
        var (_, invoice) = await client.IssueInvoice(50m, TestClock.Today);
        await app.Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>().ProcessOnce(CancellationToken.None);
        var payment = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        var reference = await fixture.Database.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));

        policy.Refusing = true;
        var body = JsonSerializer.Serialize(new { externalRef = reference, status = "PAID" }, JsonSerializerOptions.Web);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var id = Guid.CreateVersion7().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.CallbackPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Callback-Timestamp", timestamp);
        request.Headers.Add("X-Callback-Id", id);
        request.Headers.Add("X-Signature", CallbackKeys.Sign(endpoint.SigningSecret, timestamp, id, body));

        var response = await app.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        (await response.Code()).ShouldBe(PaymentErrorCodes.ProviderNotAllowed);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM payment.payment WHERE id = @p", ("p", payment))).ShouldBe("PENDING");
    }
}
