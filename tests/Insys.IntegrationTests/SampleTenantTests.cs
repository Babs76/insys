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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Communications.Application;
using Insys.Communications.Contracts;
using Insys.Communications.Infrastructure;
using Insys.Communications.Infrastructure.Channels;
using Insys.Payments.Application;
using Insys.Payments.Domain.Payments;
using Insys.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Workstream F: a sample tenant reaches only stubs, even with a real provider connected, and its mark can't be changed by anyone.</summary>
public sealed class SampleTenantTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static WebApplicationFactory<Program>? _host;

    private sealed class RealEmail : Insys.Communications.Application.Channels.INotificationChannel
    {
        public static readonly ConcurrentBag<Guid> Sent = [];

        public string Channel => "EMAIL";

        public Task<Insys.Communications.Application.Channels.ChannelReceipt> Send(Insys.Communications.Application.Channels.OutboundMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message.TenantId);
            return Task.FromResult(Insys.Communications.Application.Channels.ChannelReceipt.Sent("REAL-" + message.CorrelationId));
        }
    }

    private sealed class RealGateway : IPaymentGateway
    {
        public static readonly ConcurrentBag<Guid> Sent = [];

        public bool Handles(string providerCode) => providerCode == "REALPAY";

        public Task<(ProviderOutcome Outcome, string? ExternalRef, string? Reason)> Send(Payment payment, Insys.Party.Contracts.PayeeAccountDetails? payee, CancellationToken cancellationToken)
        {
            Sent.Add(payment.Id);
            return Task.FromResult((ProviderOutcome.Pending, (string?)"REAL-1", (string?)null));
        }
    }

    private WebApplicationFactory<Program> Host() => _host ??= new InsysApiFactory(fixture.Database).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
    {
        s.AddSingleton<Insys.Communications.Application.Channels.INotificationChannel, RealEmail>();
        s.AddSingleton<IPaymentGateway, RealGateway>();
    }));

    private async Task<(Guid Tenant, HttpClient Client)> Tenant(bool sample)
    {
        var code = "ST" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, code);
        if (sample)
        {
            // The test seed makes the tenant before its mark, so it steps round the trigger as the owner. RegisterTenant sets it at creation, tested below.
            await fixture.Database.ExecuteAsOwner("ALTER TABLE tenant.tenant DISABLE TRIGGER sample_mark_is_fixed; UPDATE tenant.tenant SET sample = true WHERE id = @t; ALTER TABLE tenant.tenant ENABLE TRIGGER sample_mark_is_fixed", ("t", tenant));
        }

        await fixture.Database.SeedBillingAndPayments(tenant, From, "REALPAY");
        var subject = $"st-{code}";
        await fixture.Database.UserWithPermissions(tenant, subject, "party.create", "party.read", "party.update", Billing.Contracts.BillingPermissions.AccountCreate, Billing.Contracts.BillingPermissions.AccountUpdate,
            Billing.Contracts.BillingPermissions.InvoiceCreate);
        var host = Host();
        var client = host.CreateDefaultClient(new TestPreconditionsHandler());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(tenant, subject, DateTime.UtcNow));
        return (tenant, client);
    }

    private async Task<Guid> Paid(HttpClient client)
    {
        var (_, invoice) = await client.IssueInvoice(50m, TestClock.Today);
        await Host().Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>().ProcessOnce(CancellationToken.None);
        return await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
    }

    [Fact]
    public async Task ASampleTenantsPayment_ReachesOnlyTheStub_WhileARealTenantsReachesItsProvider()
    {
        var (_, sample) = await Tenant(sample: true);
        var (_, real) = await Tenant(sample: false);
        var samplePayment = await Paid(sample);
        var realPayment = await Paid(real);

        RealGateway.Sent.ShouldNotContain(samplePayment);
        RealGateway.Sent.ShouldContain(realPayment);
        (await fixture.Database.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", samplePayment)))!.ShouldStartWith("SIM-");
    }

    [Fact]
    public async Task ASampleTenantsNotification_ReachesOnlyTheStub_WhileARealTenantsReachesItsProvider()
    {
        var (sampleTenant, sample) = await Tenant(sample: true);
        var (realTenant, real) = await Tenant(sample: false);
        var host = Host();
        foreach (var (tenant, client) in new[] { (sampleTenant, sample), (realTenant, real) })
        {
            await Seed(tenant, "tenant.cfg_locale_defaults", "LOCALE", ("tenant", "S"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
            await Seed(tenant, "communication.cfg_channel_providers", "PRV-EMAIL", ("channel", "EMAIL"), ("provider_code", "MAIL-1"), ("sender_identity", "noreply@insurer.test"), ("enabled", true));
            await Seed(tenant, "communication.cfg_notification_types", NotificationTypes.ClaimStatus, ("type_code", NotificationTypes.ClaimStatus), ("category", "SERVICE"), ("consent_purpose", DBNull.Value), ("channels", new[] { "EMAIL" }), ("portal_visible", false));
            await Seed(tenant, "communication.cfg_notification_templates", "T", ("type_code", NotificationTypes.ClaimStatus), ("channel", "EMAIL"), ("language", "en"), ("subject", "Claim"), ("body", "Your claim {claim_number}."));
            var party = await client.RegisterPerson("Sample");
            (await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/contact-points", new { channelType = "EMAIL", value = "someone@example.test", isPrimary = true }, ifMatch: (await client.GetAsync($"/api/v1/party/parties/{party}")).ETag()))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await host.Services.GetRequiredService<Insys.BuildingBlocks.Infrastructure.Messaging.OutboxProcessor>().ProcessOnce(CancellationToken.None);
            var source = Guid.CreateVersion7();
            await host.Services.GetRequiredService<IEventBus>().Publish(new EventEnvelope(Guid.CreateVersion7(), tenant, "communication.NotificationRequested", 1, DateTimeOffset.UtcNow, "Test", source, Guid.CreateVersion7(), null,
                JsonSerializer.SerializeToElement(new NotificationRequested(NotificationTypes.ClaimStatus, party, "GENERAL_CLAIM", source, new Dictionary<string, string> { ["claim_number"] = "GC-1" }, null), ModuleDbContext.EventJson)), CancellationToken.None);
            await host.Services.GetRequiredService<NotificationSender>().RunForTenant(tenant, CancellationToken.None);
        }

        RealEmail.Sent.ShouldNotContain(sampleTenant);
        RealEmail.Sent.ShouldContain(realTenant);
        host.Services.GetRequiredService<StubChannelOutbox>().Sent("EMAIL").Select(m => m.Message.TenantId).ShouldContain(sampleTenant);
    }

    [Fact]
    public async Task TheMark_IsSetAtCreation_AndNoOneCanChangeIt()
    {
        var (tenant, _) = await Tenant(sample: true);
        foreach (var role in new[] { fixture.Database.OwnerConnectionString, fixture.Database.ConnectionFor("insys_tenant") })
        {
            await using var connection = new Npgsql.NpgsqlConnection(role);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var clear = new Npgsql.NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false); UPDATE tenant.tenant SET sample = false WHERE id = @i", connection);
            clear.Parameters.AddWithValue("t", tenant.ToString());
            clear.Parameters.AddWithValue("i", tenant);
            await Should.ThrowAsync<Npgsql.PostgresException>(() => clear.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        (await fixture.Database.ScalarAsOwner<bool>("SELECT sample FROM tenant.tenant WHERE id = @t", ("t", tenant))).ShouldBeTrue();
        typeof(Insys.Tenancy.Domain.Tenants.Tenant).GetProperty("Sample")!.SetMethod!.IsPublic.ShouldBeFalse();
        typeof(Insys.Tenancy.Domain.Tenants.Tenant).GetMethods().Where(m => m.GetParameters().Any(p => p.Name == "sample") && !m.IsStatic).ShouldBeEmpty("only creation takes it");
    }

    [Fact]
    public async Task ATenantRegisteredAsASample_IsOne_AndAnUnknownTenantCountsAsOne()
    {
        var marks = Host().Services.GetRequiredService<Insys.BuildingBlocks.Application.Context.ISampleTenants>();
        (await marks.IsSample(Guid.CreateVersion7(), CancellationToken.None)).ShouldBeTrue("it fails closed");
        var (real, _) = await Tenant(sample: false);
        (await marks.IsSample(real, CancellationToken.None)).ShouldBeFalse();
        var registered = Insys.Tenancy.Domain.Tenants.Tenant.RegisterTenant(Guid.CreateVersion7(), "OFTZA", "OFT Insurance South Africa", Insys.Tenancy.Domain.Tenants.IsolationProfile.A, "Africa/Johannesburg", DateTimeOffset.UtcNow, sample: true);
        registered.Value.Sample.ShouldBeTrue();
    }

    private Task Seed(Guid tenant, string table, string code, params (string Column, object Value)[] fields) => fixture.Database.SeedStatic(tenant, table, "EFFECTIVE", code, From, fields);
}
