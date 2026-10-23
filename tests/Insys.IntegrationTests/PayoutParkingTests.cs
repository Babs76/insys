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
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.Integration.Infrastructure.Parking;
using Insys.Payments.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// GAP-038, ADR-022 decision 7, through the whole host: a released refund whose currency has no payout method in force
/// is parked, not skipped; it is escalated to a Workflow task on the money schedule; and once a payout method is
/// published the payment is created exactly once.
/// </summary>
public sealed class PayoutParkingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Consumer = "payment.PaymentPolicies.RefundApproved";
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await Seed("payment.mst_payment_provider_and_bank_register", "SIMULATOR", ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"),
            ("supported_methods", new[] { "EFT" }), ("credential_ref", "vault:simulator"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Payout_WithNoPayoutMethodInForce_IsParkedAndEscalated_ThenPaidOnceWhenAMethodIsPublished_GAP038()
    {
        var refund = Guid.CreateVersion7();
        var approved = Envelope(new RefundApproved(refund, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 125.50m, "BWP"),
            "billing.RefundApproved", "Refund", refund);
        var bus = _factory.Services.GetRequiredService<IEventBus>();

        // Before: skipped with a log line, and the refund never paid. Now: parked, naming the currency.
        (await bus.Deliver(approved, CancellationToken.None)).Single(d => d.Consumer == Consumer).Status.ShouldBe(DeliveryStatus.Parked);
        (await Payments(refund)).ShouldBe(0);
        (await fixture.Database.ScalarAsOwner<string>(
            "SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c AND status = 'PARKED'",
            ("e", approved.EventId), ("c", Consumer))).ShouldBe($"{PaymentPrerequisites.PayoutMethodNotInForce} BWP");

        // A money consumer: the first escalation is 4 business hours after parking, a Workflow task for the configuration owner role.
        var job = _factory.Services.GetRequiredService<ParkingJob>();
        await job.RunForTenant(Tenant, CancellationToken.None);
        var gap = await fixture.Database.ScalarAsOwner<double>(
            "SELECT extract(epoch FROM next_escalation_at - parked_at) FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c", ("e", approved.EventId), ("c", Consumer));
        gap.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(4).TotalSeconds);
        gap.ShouldBeLessThan(TimeSpan.FromDays(5).TotalSeconds);

        await fixture.Database.ExecuteAsOwner("UPDATE integration.parked_delivery SET next_escalation_at = now() - interval '1 minute' WHERE event_id = @e AND consumer = @c",
            ("e", approved.EventId), ("c", Consumer));
        (await job.RunForTenant(Tenant, CancellationToken.None)).Escalated.ShouldBe(1);
        await _factory.DrainOutbox();
        var parkedId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c",
            ("e", approved.EventId), ("c", Consumer));
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT t.queue_code FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id
            WHERE p.process_code = 'PARKED_DELIVERY' AND p.business_ref_type = 'PARKED_DELIVERY' AND p.business_ref_id = @id AND t.status = 'OPEN'
            """, ("id", parkedId))).ShouldBe("TENANT_ADMIN");

        // A payout method for the currency is published: the parked payout is retried and the payment created, once.
        await Seed("payment.ref_payment_methods", "EFT", ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "BWP" }), ("countries", new[] { "BW" }));
        await bus.Publish(Envelope(new StaticDataVersionActivated(null,
                new StaticRecordRef("payment", "ref_payment_methods", "PAYMENT_METHODS", "payments", "payment-methods", Guid.CreateVersion7(), "EFT", 1, false), Today, null),
            "sdm.StaticDataVersionActivated", "StaticRecord", Guid.CreateVersion7()), CancellationToken.None);
        (await job.RunForTenant(Tenant, CancellationToken.None)).Resolved.ShouldBe(1);
        (await Payments(refund)).ShouldBe(1);

        // Retried again, and the event replayed: still one payment.
        await job.RunForTenant(Tenant, CancellationToken.None);
        await bus.Publish(approved, CancellationToken.None);
        await bus.DeliverTo(approved, Consumer, CancellationToken.None);
        (await Payments(refund)).ShouldBe(1);
    }

    private Task<long> Payments(Guid refund) =>
        fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment WHERE idempotency_key = @k", ("k", $"REFUND:{refund:N}"));

    private static EventEnvelope Envelope<T>(T integrationEvent, string type, string aggregateType, Guid aggregateId) =>
        new(Guid.CreateVersion7(), Tenant, type, 1, DateTimeOffset.UtcNow, aggregateType, aggregateId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web));

    private Task Seed(string table, string code, params (string Column, object Value)[] fields)
    {
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var values = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        return fixture.Database.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, effective_to, record_status, created_by, change_reason, {columns})
            VALUES (@t, @code, 1, @from, NULL, 'ACTIVE', 'seed', 'test seed', {values})
            ON CONFLICT DO NOTHING
            """,
            [("t", Tenant), ("code", code), ("from", Today.AddDays(-30)), .. fields.Select((f, i) => ($"p{i}", f.Value))]);
    }
}
