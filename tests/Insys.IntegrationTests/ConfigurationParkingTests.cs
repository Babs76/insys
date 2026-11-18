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
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// The seed batch's sweep (Ade, 18 November 2026): each consumer that answered missing configuration by failing, retrying and dead-lettering now parks
/// with a code and item that name it (ADR-022), so it is retried when the configuration is published.
/// </summary>
public sealed class ConfigurationParkingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);
    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ATemplateNamingAMergeFieldTheEventDoesNotGive_Parks_ADR022()
    {
        await fixture.Database.SeedStatic(Tenant, "document.ref_document_categories", "ACTIVE", "POLICY_SCHEDULE", From, ("category_code", "POLICY_SCHEDULE"),
            ("label", "Policy schedule"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.SeedStatic(Tenant, "document.cfg_document_templates", "EFFECTIVE", "TPL-MF", From, ("template_code", "TPL-MF"), ("language", "en"),
            ("document_type", "POLICY_SCHEDULE"), ("body_ref", "doc://templates/mf/v1"), ("merge_fields", new[] { "policy_number", "not_an_event_field" }));
        await fixture.Database.SeedStatic(Tenant, "product.cfg_product_template_assignments", "EFFECTIVE", "TA-MF", From, ("product_code", "LIFE-MF"),
            ("document_type", "POLICY_SCHEDULE"), ("template", "TPL-MF"), ("language", "en"));
        var policy = Guid.CreateVersion7();
        var issued = new Insys.Life.Contracts.LifePolicyIssued(policy, $"LIF-{policy.ToString("N")[..8]}", Guid.CreateVersion7(), "LIFE-MF", 1, Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), "MONTHLY", "DEBIT_ORDER", "ZAR", 100m, TestClock.Today, null, 500_000m);

        (await Deliver(issued, "life.LifePolicyIssued", "LifePolicy", policy, "document.LifeDocumentPolicies.LifePolicyIssued"))
            .ShouldBe($"{PrerequisiteCodes.DocumentTemplateFieldUnavailable} TPL-MF:en");
    }

    [Fact]
    public async Task LifeArrears_WithNoGraceRuleForThePremiumFrequency_Park_ADR022()
    {
        var policy = await _rows.Insert(Tenant, "life.policy", ("premium_frequency", "QUARTERLY"), ("status", "IN_FORCE"), ("policy_number", $"LGR{Random.Shared.Next(100_000, 999_999)}"));
        var settled = new Insys.Billing.Contracts.InvoiceSettled(Guid.CreateVersion7(), Guid.CreateVersion7(), Insys.Life.Contracts.LifeSubjects.SourceDomain, policy);

        (await Deliver(settled, "billing.InvoiceSettled", "Invoice", settled.InvoiceId, "life.LifeEventPolicies.InvoiceSettled"))
            .ShouldBe($"{PrerequisiteCodes.GraceRuleNotInForce} QUARTERLY");
    }

    [Fact]
    public async Task Commission_OnAPlanNamedButNotInForce_Parks_ADR022()
    {
        var policy = await _rows.Insert(Tenant, "life.policy", ("product_code", "LIFE-CP"), ("intermediary_code", "BRK-CP"), ("status", "IN_FORCE"),
            ("policy_number", $"LCP{Random.Shared.Next(100_000, 999_999)}"));
        await fixture.Database.SeedStatic(Tenant, "commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-CP", From, ("intermediary", "BRK-CP"),
            ("product", "LIFE-CP"), ("transactions_allowed", Array.Empty<string>()), ("plan_code", "PLAN-GONE"));
        var invoice = Guid.CreateVersion7();
        var issued = new Insys.Billing.Contracts.InvoiceIssued(invoice, "INV-CP", Guid.CreateVersion7(), Guid.CreateVersion7(), "EFT", Insys.Life.Contracts.LifeSubjects.SourceDomain,
            policy, "LIFE-CP v1", TestClock.Today, TestClock.Today.AddDays(30), "ZAR", 100m, 0m, 100m);

        (await Deliver(issued, "billing.InvoiceIssued", "Invoice", invoice, "commission.CommissionPolicies.InvoiceIssued"))
            .ShouldBe($"{PrerequisiteCodes.CommissionPlanNotInForce} PLAN-GONE");
    }

    [Fact]
    public async Task AReturnWhoseMappingIsInvalid_ParksOnTheFinalClose_ADR022()
    {
        await fixture.Database.SeedStatic(Tenant, "reporting.cfg_regulatory_return_definitions", "EFFECTIVE", "RET-BAD", From, ("return_code", "RET-BAD"),
            ("regulator", "Sample regulator"), ("field_mapping", JsonDocument.Parse("[]")), ("frequency", "MONTHLY"));
        var closed = new Insys.Finance.Contracts.AccountingPeriodClosed(Guid.CreateVersion7(), "2026-10", true);

        (await Deliver(closed, "finance.AccountingPeriodClosed", "AccountingPeriod", closed.PeriodId, "reporting.ReturnPolicies.AccountingPeriodClosed"))
            .ShouldBe($"{PrerequisiteCodes.ReturnMappingInvalid} RET-BAD");
    }

    [Fact]
    public async Task AnAuthorisationsReservation_OnAPlanOptionNotInForce_Parks_ADR022()
    {
        var principal = Guid.CreateVersion7();
        var membership = await _rows.Insert(Tenant, "health.membership", ("status", "ACTIVE"), ("plan_code", "PLN-GONE"), ("option_code", "OPT-GONE"),
            ("principal_party_id", principal), ("member_number", $"CP{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var approved = new Insys.Health.Contracts.PreauthorisationApproved(Guid.CreateVersion7(), "AUTH-CP", membership, principal, TestClock.Today, TestClock.Today.AddDays(30),
            [new Insys.Health.Contracts.ReservationChange(Guid.CreateVersion7(), "HOSPITAL", 1000m)]);

        (await Deliver(approved, "health.PreauthorisationApproved", "Preauthorisation", approved.PreauthorisationId, "health.HealthEventPolicies.PreauthorisationApproved"))
            .ShouldBe($"{PrerequisiteCodes.PlanOptionNotInForce} PLN-GONE/OPT-GONE");
    }

    // Ade, 18 November 2026: what was skipped or dropped for want of configuration now parks, so nothing missing is lost without trace.

    [Fact]
    public async Task ACollectionWhoseMethodIsNotInForceForTheCurrency_Parks_RatherThanGoingUncollected_ADR022()
    {
        await fixture.Database.SeedStatic(Tenant, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "COLLECTOR", From, ("provider_code", "COLLECTOR"),
            ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "DEBIT_ORDER" }), ("credential_ref", "vault:collector"));
        var invoice = Guid.CreateVersion7();
        var issued = new Insys.Billing.Contracts.InvoiceIssued(invoice, "INV-COL", Guid.CreateVersion7(), Guid.CreateVersion7(), "DEBIT_ORDER", "GENERAL", Guid.CreateVersion7(),
            "GEN v1", TestClock.Today, TestClock.Today.AddDays(30), "ZAR", 100m, 0m, 100m);

        (await Deliver(issued, "billing.InvoiceIssued", "Invoice", invoice, "payment.PaymentPolicies.InvoiceIssued"))
            .ShouldBe($"{Insys.Payments.Application.PaymentPrerequisites.CollectionMethodNotInForce} DEBIT_ORDER:ZAR");
    }

    [Fact]
    public async Task GeneralArrears_WithNoGraceRuleForTheProductAndFrequency_Park_ADR022()
    {
        var policy = await _rows.Insert(Tenant, "general.policy", ("product_code", "GEN-NOGR"), ("billing_frequency", "MONTHLY"), ("status", "IN_FORCE"),
            ("policy_number", $"GNG{Random.Shared.Next(100_000, 999_999)}"));
        var settled = new Insys.Billing.Contracts.InvoiceSettled(Guid.CreateVersion7(), Guid.CreateVersion7(), "GENERAL", policy);

        (await Deliver(settled, "billing.InvoiceSettled", "Invoice", settled.InvoiceId, "general.GeneralGrace.Grace.InvoiceSettled"))
            .ShouldBe($"{PrerequisiteCodes.GraceRuleNotInForce} GEN-NOGR:MONTHLY");
    }

    [Fact]
    public async Task HealthArrears_WithNoContributionGraceRule_Park_ADR022()
    {
        var membership = await _rows.Insert(Tenant, "health.membership", ("status", "ACTIVE"), ("principal_party_id", Guid.CreateVersion7()),
            ("member_number", $"HG{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var settled = new Insys.Billing.Contracts.InvoiceSettled(Guid.CreateVersion7(), Guid.CreateVersion7(), "HEALTH", membership);

        (await Deliver(settled, "billing.InvoiceSettled", "Invoice", settled.InvoiceId, "health.HealthGrace.Grace.InvoiceSettled"))
            .ShouldBe($"{PrerequisiteCodes.ContributionGraceRuleNotInForce} DEFAULT");
    }

    [Fact]
    public async Task Commission_ForAnIntermediaryWithNoPlanOnTheProduct_Parks_ADR022()
    {
        var policy = await _rows.Insert(Tenant, "life.policy", ("product_code", "LIFE-NP"), ("intermediary_code", "BRK-NP"), ("status", "IN_FORCE"),
            ("policy_number", $"LNP{Random.Shared.Next(100_000, 999_999)}"));
        await fixture.Database.SeedStatic(Tenant, "commission.cfg_intermediary_product_permissions", "EFFECTIVE", "PERM-NP", From, ("intermediary", "BRK-NP"),
            ("product", "LIFE-NP"), ("transactions_allowed", Array.Empty<string>()));
        var invoice = Guid.CreateVersion7();
        var issued = new Insys.Billing.Contracts.InvoiceIssued(invoice, "INV-NP", Guid.CreateVersion7(), Guid.CreateVersion7(), "EFT", Insys.Life.Contracts.LifeSubjects.SourceDomain,
            policy, "LIFE-NP v1", TestClock.Today, TestClock.Today.AddDays(30), "ZAR", 100m, 0m, 100m);

        (await Deliver(issued, "billing.InvoiceIssued", "Invoice", invoice, "commission.CommissionPolicies.InvoiceIssued"))
            .ShouldBe($"{PrerequisiteCodes.CommissionPlanNotAssigned} BRK-NP:LIFE-NP");
    }

    [Fact]
    public async Task ABrokerChange_OnAFrequencyNotInForce_ParksRatherThanGuessingOnePeriod_ADR022()
    {
        var policy = Guid.CreateVersion7();
        var changed = new Insys.General.Contracts.GeneralPolicyIntermediaryChanged(policy, "GP-BC", "GEN-BC", "BRK-OUT", "BRK-IN", TestClock.Today, TestClock.Today.AddDays(200),
            "FORTNIGHTLY");

        (await Deliver(changed, "general.GeneralPolicyIntermediaryChanged", "GeneralPolicy", policy, "commission.BrokerOfRecordCommission"))
            .ShouldBe($"{PrerequisiteCodes.BillingFrequencyNotInForce} FORTNIGHTLY");
    }

    [Fact]
    public async Task AWebhookWhoseSigningKeyIsMissing_WaitsPending_WithoutSpendingAnAttempt_ADR022()
    {
        await fixture.Database.SeedStatic(Tenant, "integration.cfg_webhook_subscriptions", "EFFECTIVE", "WH-NOKEY", From, ("subscription_id", "WH-NOKEY"),
            ("partner_code", "NOKEY-SYS"), ("event_types", new[] { "test.WebhookWait" }), ("endpoint_url", "https://partner.example/hooks"), ("signing_key_ref", "vault-missing"));
        var envelope = new EventEnvelope(Guid.CreateVersion7(), Tenant, "test.WebhookWait", 1, DateTimeOffset.UtcNow, "Test", Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(new { reference = "R-1" }));
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None);
        await _factory.Services.GetRequiredService<Insys.Integration.Infrastructure.Webhooks.WebhookJobs>().Run(CancellationToken.None);

        (await fixture.Database.ScalarAsOwner<string>(
                "SELECT status || ' ' || attempt || ' ' || split_part(error, ':', 1) FROM integration.webhook_delivery WHERE tenant_id = @t AND event_id = @e", ("t", Tenant), ("e", envelope.EventId)))
            .ShouldBe($"PENDING 0 {Insys.Integration.Application.Webhooks.WebhookWaits.SigningKeyNotAvailable}");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.dead_letter_message WHERE tenant_id = @t AND message_id = @e", ("t", Tenant), ("e", envelope.EventId.ToString())))
            .ShouldBe(0L);
    }

    private async Task<string?> Deliver<T>(T integrationEvent, string type, string aggregateType, Guid aggregateId, string consumer)
    {
        var envelope = new EventEnvelope(Guid.CreateVersion7(), Tenant, type, 1, DateTimeOffset.UtcNow, aggregateType, aggregateId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web));
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None);
        return await fixture.Database.ScalarAsOwner<string>(
            "SELECT status || ' ' || coalesce(prerequisite_code || ' ' || prerequisite_item, '') FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c",
            ("e", envelope.EventId), ("c", consumer)) is { } row && row.StartsWith("PARKED ", StringComparison.Ordinal) ? row["PARKED ".Length..] : await Status(envelope.EventId, consumer);
    }

    private async Task<string?> Status(Guid eventId, string consumer) =>
        "not parked: " + (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c", ("e", eventId), ("c", consumer))
                          ?? "no ledger row");
}
