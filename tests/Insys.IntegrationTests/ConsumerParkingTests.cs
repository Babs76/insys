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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.Integration.Infrastructure.Parking;
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// ADR-022 for each consumer converted in Wave 0 batch 5, through the whole host: a Life policy issued in a tenant missing
/// the configuration a consumer needs parks that consumer's delivery with the code and item that name it, and once the
/// configuration is published the delivery is retried and resolved.
/// </summary>
public sealed class ConsumerParkingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Billing_ParksAPolicyWhoseCollectionMethodIsNotInForce_AndBillsItOncePublished_ADR022()
    {
        const string consumer = "billing.LifePolicyBilling.LifePolicyIssued";
        var issued = await Issue("STOP_ORDER", "MONTHLY");

        (await Parked(issued, consumer)).ShouldBe($"{PrerequisiteCodes.CollectionMethodNotInForce} STOP_ORDER");

        await Seed("billing.ref_collection_methods", "STOP_ORDER", ("method_code", "STOP_ORDER"), ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await RetryAfterPublishing();
        (await Parked(issued, consumer)).ShouldBe($"{PrerequisiteCodes.BillingFrequencyNotInForce} MONTHLY", "the next missing item is named in turn");

        await Seed("billing.ref_billing_frequencies", "MONTHLY", ("label", "Monthly"), ("periods_per_year", 12));
        await RetryAfterPublishing();
        (await Status(issued, consumer)).ShouldBe("RESOLVED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM billing.billing_account WHERE id = @a", ("a", issued.BillingAccountRef))).ShouldBe(1);
    }

    [Fact]
    public async Task Reinsurance_ParksAPolicyWhoseBillingFrequencyIsNotInForce_AndCedesItOncePublished_ADR022()
    {
        const string consumer = "reinsurance.ReinsurancePolicies.LifePolicyIssued";
        var issued = await Issue("DEBIT_ORDER", "QUARTERLY");

        (await Parked(issued, consumer)).ShouldBe($"{PrerequisiteCodes.BillingFrequencyNotInForce} QUARTERLY");

        await Seed("billing.ref_billing_frequencies", "QUARTERLY", ("label", "Quarterly"), ("periods_per_year", 4));
        await RetryAfterPublishing();
        (await Status(issued, consumer)).ShouldBe("RESOLVED");
    }

    [Fact]
    public async Task Analytics_ParksAPolicyWhoseBillingFrequencyIsNotInForce_AndProjectsItOncePublished_ADR022()
    {
        const string consumer = "analytics.ProjectionPolicies.LifePolicyIssued";
        var issued = await Issue("DEBIT_ORDER", "ANNUAL");

        (await Parked(issued, consumer)).ShouldBe($"{PrerequisiteCodes.BillingFrequencyNotInForce} ANNUAL");

        await Seed("billing.ref_billing_frequencies", "ANNUAL", ("label", "Annual"), ("periods_per_year", 1));
        await RetryAfterPublishing();
        (await Status(issued, consumer)).ShouldBe("RESOLVED");
    }

    [Fact]
    public async Task Documents_ParksAPolicyWhoseProductHasNoScheduleTemplate_NamingTheProductAndDocumentType_ADR022()
    {
        const string consumer = "document.LifeDocumentPolicies.LifePolicyIssued";
        var issued = await Issue("DEBIT_ORDER", "MONTHLY");

        (await Parked(issued, consumer)).ShouldBe($"{PrerequisiteCodes.DocumentTemplateNotInForce} LIFE-NOTPL:POLICY_SCHEDULE");

        // Still missing when retried: parked again, never failed or dead-lettered.
        await RetryAfterPublishing();
        (await Status(issued, consumer)).ShouldBe("PARKED");
        (await fixture.Database.ScalarAsOwner<int>("SELECT attempts FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c",
            ("e", issued.EventId), ("c", consumer))).ShouldBe(2);

        // GAP-046: the operations dashboard counts it by what is missing, and says since when deliveries have been parked.
        await fixture.Database.UserWithPermissions(Tenant, "operations", Insys.Integration.Contracts.IntegrationPermissions.DeadLetterRead);
        var summary = await (await _factory.ClientFor(Tenant, "operations").GetAsync("/api/v1/integration/deliveries/summary")).Json();
        JsonElement Figure(string code) => summary.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("code").GetString() == code);
        Figure("DELIVERIES_PARKED").GetProperty("by").EnumerateArray().Single(p => p.GetProperty("key").GetString() == PrerequisiteCodes.DocumentTemplateNotInForce)
            .GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(1L);
        Figure("OLDEST_PARKED_AT").GetProperty("at").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        Figure("DEAD_LETTERS_OPEN").GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(0L);
    }

    [Fact]
    public async Task Reinsurance_ParksARecalculationOnEndorsement_WhenTheCessionRuleIsNotInForce_ADR022()
    {
        const string consumer = "reinsurance.ReinsurancePolicies.GeneralPolicyEndorsed";
        var policy = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO reinsurance.cession (tenant_id, id, created_at, created_by, treaty_code, source_type, source_id, period_start, period_end, ceded_sum_insured,
              ceded_premium, currency, rule_code, rule_version, calculated_at, product_code, treaty_version, gross_sum_insured, gross_premium, ceded_share)
            VALUES (@t, @id, now(), 'seed', 'QS-TEST', 'GENERAL_POLICY', @p, @from, @to, 250000, 600, 'ZAR', 'QS-RULE', 1, now(), 'GEN-NORULE', 1, 500000, 1200, 0.5)
            """, ("t", Tenant), ("id", Guid.CreateVersion7()), ("p", policy), ("from", Today), ("to", Today.AddDays(364)));
        var endorsed = new Insys.General.Contracts.GeneralPolicyEndorsed(policy, "GEN-ENDORSE-1", Guid.CreateVersion7(), "END-1", Today, 1300m, 100m, "ZAR", "GEN-NORULE", 1,
            [], NewSumInsured: 600_000m);
        var envelope = Envelope(endorsed, "general.GeneralPolicyEndorsed", "GeneralPolicy", policy);

        var outcome = (await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None)).Single(d => d.Consumer == consumer);

        // Before: retried and dead-lettered. Now: parked, naming the product and treaty whose rule is missing.
        outcome.Status.ShouldBe(DeliveryStatus.Parked);
        (await Parked((envelope.EventId, Guid.Empty), consumer)).ShouldBe($"{PrerequisiteCodes.CessionRuleNotInForce} GEN-NORULE:QS-TEST");
    }

    private async Task<(Guid EventId, Guid BillingAccountRef)> Issue(string collectionMethod, string frequency)
    {
        var policy = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var issued = new LifePolicyIssued(policy, $"LIF-{policy.ToString("N")[..8]}", Guid.CreateVersion7(), "LIFE-NOTPL", 1, Guid.CreateVersion7(), Guid.CreateVersion7(),
            account, frequency, collectionMethod, "ZAR", 100m, Today, null, 500_000m);
        var envelope = Envelope(issued, "life.LifePolicyIssued", "LifePolicy", policy);
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None);
        return (envelope.EventId, account);
    }

    private async Task RetryAfterPublishing()
    {
        var activated = new StaticDataVersionActivated(null,
            new StaticRecordRef("billing", "ref_billing_frequencies", "BILLING_FREQUENCIES", "billing", "billing-frequencies", Guid.CreateVersion7(), "ANY", 1, false), Today, null);
        await _factory.Services.GetRequiredService<IEventBus>().Publish(
            Envelope(activated, "sdm.StaticDataVersionActivated", "StaticRecord", Guid.CreateVersion7()), CancellationToken.None);
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(Tenant, CancellationToken.None);
    }

    private Task<string?> Parked((Guid EventId, Guid _) issued, string consumer) =>
        fixture.Database.ScalarAsOwner<string>(
            "SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c AND status = 'PARKED'",
            ("e", issued.EventId), ("c", consumer));

    private Task<string?> Status((Guid EventId, Guid _) issued, string consumer) =>
        fixture.Database.ScalarAsOwner<string>("SELECT status FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c",
            ("e", issued.EventId), ("c", consumer));

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
