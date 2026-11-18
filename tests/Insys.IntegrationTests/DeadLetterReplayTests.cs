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
using System.Text.Json;
using Insys.BuildingBlocks.Contracts;
using Insys.Integration.Contracts;
using Insys.Life.Contracts;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// The runbook's procedure for a delivery dead-lettered for missing configuration before it parked (docs/runbooks/dead-letters-and-webhooks.md):
/// configure what was missing, then replay the dead letter, and the consumer that failed does its work and its ledger row is resolved.
/// </summary>
public sealed class DeadLetterReplayTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private const string Consumer = "document.LifeDocumentPolicies.LifePolicyIssued";

    [Fact]
    public async Task ADeliveryDeadLetteredForMissingConfiguration_IsDoneByReplayOnceConfigured_ADR022()
    {
        var factory = await fixture.Host();
        var from = TestClock.Today.AddDays(-30);
        var policy = Guid.CreateVersion7();
        var issued = new LifePolicyIssued(policy, $"LIF-{policy.ToString("N")[..8]}", Guid.CreateVersion7(), "LIFE-DLQ", 1, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "MONTHLY", "DEBIT_ORDER", "ZAR", 100m, TestClock.Today, null, 500_000m);
        var envelope = new EventEnvelope(Guid.CreateVersion7(), Tenant, "life.LifePolicyIssued", 1, DateTimeOffset.UtcNow, "LifePolicy", policy, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(issued, JsonSerializerOptions.Web));

        // As an older delivery was left: failed for want of the document category, retried until dead-lettered by the ledger.
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO integration.parked_delivery (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, consumer, event_id, event_type, event_version,
                occurred_at, aggregate_type, aggregate_id, correlation_id, payload, status, ledger_owned, attempts, failures, escalation_level)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @c, @e, 'life.LifePolicyIssued', 1, now(), 'LifePolicy', @p, gen_random_uuid(), @payload::jsonb,
                'DEAD_LETTERED', true, 10, 10, 0)
            """, ("t", Tenant), ("c", Consumer), ("e", envelope.EventId), ("p", policy), ("payload", envelope.Payload.GetRawText()));
        var deadLetter = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO integration.dead_letter_message
              (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source, message_id, payload, error, first_failed_at, attempts)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', @source, @e::text, @payload::jsonb, 'ATTEMPT_LIMIT_REACHED', now(), 10)
            """, ("t", Tenant), ("id", deadLetter), ("source", $"ledger:{Consumer}"), ("e", envelope.EventId),
            ("payload", JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web)));

        // 1. Configure what was missing: the category, with the template and its assignment to the product.
        await fixture.Database.SeedStatic(Tenant, "document.ref_document_categories", "ACTIVE", "POLICY_SCHEDULE", from, ("category_code", "POLICY_SCHEDULE"),
            ("label", "Policy schedule"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.SeedStatic(Tenant, "document.cfg_document_templates", "EFFECTIVE", "TPL-DLQ", from, ("template_code", "TPL-DLQ"), ("language", "en"),
            ("document_type", "POLICY_SCHEDULE"), ("body_ref", "doc://templates/dlq/v1"), ("merge_fields", Array.Empty<string>()));
        await fixture.Database.SeedStatic(Tenant, "product.cfg_product_template_assignments", "EFFECTIVE", "TA-DLQ", from, ("product_code", "LIFE-DLQ"),
            ("document_type", "POLICY_SCHEDULE"), ("template", "TPL-DLQ"), ("language", "en"));

        // 2. Find the dead letter, then replay it with its ETag, as an identified operator.
        await fixture.Database.UserWithPermissions(Tenant, "dlq-operator", IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate);
        var operatorClient = factory.ClientFor(Tenant, "dlq-operator");
        var found = await operatorClient.GetAsync($"/api/v1/integration/dead-letters/{deadLetter}");
        found.StatusCode.ShouldBe(HttpStatusCode.OK, await found.Content.ReadAsStringAsync());
        var replay = await operatorClient.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{deadLetter}/replay", ifMatch: found.ETag());
        replay.StatusCode.ShouldBe(HttpStatusCode.NoContent, await replay.Content.ReadAsStringAsync());

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM integration.parked_delivery WHERE event_id = @e AND consumer = @c", ("e", envelope.EventId), ("c", Consumer)))
            .ShouldBe("RESOLVED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM document.document WHERE tenant_id = @t AND business_ref_id = @p", ("t", Tenant), ("p", policy)))
            .ShouldBe(1L, "the schedule the failed delivery should have made");
    }
}
