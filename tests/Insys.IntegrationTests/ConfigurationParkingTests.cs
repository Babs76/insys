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
