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
using Insys.Integration.Infrastructure.Parking;
using Insys.Life.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// DE-007 (Ade, 24 November 2026): discarding a dead letter needs the message ID typed and a reason, and releases what it held back (ADR-022
/// decision 3): its event's dead-lettered deliveries are discarded in the same transaction, so the events waiting behind them go on. A money
/// message's discard is approved by a second person first (Workflow, DEAD_LETTER_DISCARD; SRS-002). Each test runs in a tenant of its own.
/// </summary>
public sealed class DeadLetterDiscardTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Consumer = "document.LifeDocumentPolicies.LifePolicyIssued";
    private const string Reason = "Superseded by a corrected event; nothing is lost by discarding it.";

    [Fact]
    public async Task Discard_OfANonMoneyMessage_DiscardsItsDeadLetteredDelivery_AndReleasesTheDeliveryWaitingBehindIt_DE007_ADR022()
    {
        var world = await World.Create(fixture);
        var held = await world.HeldBack("outbox:life", "life.LifePolicyIssued");

        var discard = await world.Discard(world.Operator, held);
        discard.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discard.Content.ReadAsStringAsync());

        var view = await world.View(held.DeadLetterId);
        view.GetProperty("status").GetString().ShouldBe("Discarded");
        view.GetProperty("discardReason").GetString().ShouldBe(Reason);
        view.GetProperty("resolvedBy").GetGuid().ShouldBe(world.OperatorId);
        (await world.LedgerRow(held.BlockerEvent)).ShouldBe($"DISCARDED|{world.OperatorId}|{Reason}|resolved");
        (await world.LedgerRow(held.WaitingEvent)).ShouldBe("WAITING||");
        await world.Factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND target_type = 'DeadLetter' AND target_id = @id AND operation = 'DiscardMessage' AND outcome = 'SUCCESS'
            """, ("t", world.Tenant), ("id", held.DeadLetterId.ToString()))).ShouldBe(1L, "audited (IAM-006)");

        // The parking job finds the record no longer blocked and delivers the event that waited behind the discarded one.
        var pass = await world.Factory.Services.GetRequiredService<ParkingJob>().RunForTenant(world.Tenant, CancellationToken.None);
        pass.Released.ShouldBe(1);
        (await world.LedgerRow(held.WaitingEvent)).ShouldStartWith("PARKED", Case.Sensitive, "delivered, and parked for the tenant's missing document configuration");
    }

    /// <summary>The message ID with its last character changed to one it is not, so the typed reference never matches by chance.</summary>
    private static string Mistyped(string messageRef) => messageRef[..^1] + (messageRef[^1] == '0' ? '1' : '0');

    [Fact]
    public async Task Discard_WithAMistypedMessageReference_IsRefused_AndChangesNothing_DE007()
    {
        var world = await World.Create(fixture);
        var held = await world.HeldBack("outbox:life", "life.LifePolicyIssued");

        var discard = await world.Discard(world.Operator, held, messageRef: Mistyped(held.BlockerEvent.ToString()));
        discard.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await discard.Content.ReadAsStringAsync());
        (await discard.Code()).ShouldBe("TYPED_REFERENCE_MISMATCH");

        var view = await world.View(held.DeadLetterId);
        view.GetProperty("status").GetString().ShouldBe("DeadLettered");
        view.GetProperty("rowVersion").GetInt64().ShouldBe(1);
        (await world.LedgerRow(held.BlockerEvent)).ShouldBe("DEAD_LETTERED||");
    }

    [Fact]
    public async Task Discard_OfAMoneyMessage_WaitsForASecondPerson_NotTheRequester_AndOnceGrantedIsDiscardedAndReleased_SRS002_DE007()
    {
        var world = await World.Create(fixture);
        var held = await world.HeldBack("outbox:payment", "payment.PaymentFailed");

        var discard = await world.Discard(world.Operator, held);
        discard.StatusCode.ShouldBe(HttpStatusCode.Accepted, await discard.Content.ReadAsStringAsync());
        var view = await world.View(held.DeadLetterId);
        view.GetProperty("status").GetString().ShouldBe("DiscardRequested");
        view.GetProperty("discardRequestedBy").GetGuid().ShouldBe(world.OperatorId);
        view.GetProperty("discardRequestedAt").ValueKind.ShouldBe(JsonValueKind.String);
        view.GetProperty("discardReason").GetString().ShouldBe(Reason);
        (await world.LedgerRow(held.BlockerEvent)).ShouldBe("DEAD_LETTERED||", "nothing is released before the approval");

        // The list filters by the derived status, and shows the request.
        var listed = await (await world.Operator.GetAsync("/api/v1/integration/dead-letters?status=DiscardRequested&limit=200")).Json();
        var row = listed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == held.DeadLetterId);
        row.GetProperty("discardRequestedBy").GetGuid().ShouldBe(world.OperatorId);
        (await (await world.Operator.GetAsync("/api/v1/integration/dead-letters?status=DeadLettered&limit=200")).Json())
            .GetProperty("items").EnumerateArray().ShouldNotContain(i => i.GetProperty("id").GetGuid() == held.DeadLetterId);

        // While it waits, neither a replay nor a second request.
        var etag = (await world.Operator.GetAsync($"/api/v1/integration/dead-letters/{held.DeadLetterId}")).ETag();
        var replay = await world.Operator.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{held.DeadLetterId}/replay", ifMatch: etag);
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await replay.Content.ReadAsStringAsync()).ShouldContain("second person");
        (await world.Discard(world.Operator, held)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var approval = await world.Approval(held.DeadLetterId);
        (await world.Decide(world.Operator, approval, "approve")).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await world.Decide(world.Checker, approval, "approve")).ShouldBeNull();
        await world.Factory.DrainOutbox();

        view = await world.View(held.DeadLetterId);
        view.GetProperty("status").GetString().ShouldBe("Discarded");
        view.GetProperty("resolvedBy").GetGuid().ShouldBe(world.OperatorId);
        view.GetProperty("discardApprovedBy").GetGuid().ShouldBe(world.CheckerId);
        (await world.LedgerRow(held.BlockerEvent)).ShouldBe($"DISCARDED|{world.OperatorId}|{Reason}|resolved");
        (await world.Factory.Services.GetRequiredService<ParkingJob>().RunForTenant(world.Tenant, CancellationToken.None)).Released.ShouldBe(1);
        (await world.LedgerRow(held.WaitingEvent)).ShouldNotStartWith("WAITING");
    }

    [Fact]
    public async Task Discard_OfAMoneyMessage_Rejected_ClearsTheRequest_AndStaysDeadLettered_OQ113_DE007()
    {
        var world = await World.Create(fixture);
        var held = await world.HeldBack("outbox:billing", "billing.InvoiceIssued");
        (await world.Discard(world.Operator, held)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var approval = await world.Approval(held.DeadLetterId);
        (await world.Decide(world.Checker, approval, "reject")).ShouldBeNull();
        await world.Factory.DrainOutbox();

        var view = await world.View(held.DeadLetterId);
        view.GetProperty("status").GetString().ShouldBe("DeadLettered");
        view.GetProperty("discardRequestedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("discardReason").ValueKind.ShouldBe(JsonValueKind.Null);
        (await world.LedgerRow(held.BlockerEvent)).ShouldBe("DEAD_LETTERED||");
        (await world.LedgerRow(held.WaitingEvent)).ShouldBe("WAITING||");
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM integration.parked_delivery WHERE tenant_id = @t AND prerequisite_code = 'APPROVAL_ENDING_NOT_APPLIED'", ("t", world.Tenant)))
            .ShouldBe(0L);
    }

    /// <param name="DeadLetterId">The dead letter of the blocker's event.</param>
    /// <param name="BlockerEvent">The event the dead letter holds; its delivery is dead-lettered in the ledger.</param>
    /// <param name="WaitingEvent">A later event for the same record and consumer, waiting behind it.</param>
    private sealed record Held(Guid DeadLetterId, Guid BlockerEvent, Guid WaitingEvent);

    /// <summary>A tenant of its own with an operator (who also holds approval authority, so only maker-checker refuses them) and a checker.</summary>
    private sealed class World
    {
        public required TestDatabase Db { get; init; }

        public required InsysApiFactory Factory { get; init; }

        public required Guid Tenant { get; init; }

        public required HttpClient Operator { get; init; }

        public required HttpClient Checker { get; init; }

        public required Guid OperatorId { get; init; }

        public required Guid CheckerId { get; init; }

        public static async Task<World> Create(DatabaseFixture fixture)
        {
            var factory = await fixture.Host();
            var db = fixture.Database;
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenant = Guid.CreateVersion7();
            await db.SeedTenant(tenant, "DD" + suffix.ToUpperInvariant());
            string[] permissions =
                [IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove];
            var operatorId = await db.UserWithPermissions(tenant, $"dl-operator-{suffix}", permissions);
            var checkerId = await db.UserWithPermissions(tenant, $"dl-checker-{suffix}", permissions);
            var approver = await db.RoleWithPermissions(tenant);
            await db.AssignRole(tenant, operatorId, approver);
            await db.AssignRole(tenant, checkerId, approver);
            await db.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-DLD-{suffix}", TestClock.Today.AddYears(-1),
                ("role_code", approver), ("transaction_type", "DEAD_LETTER_DISCARD"), ("limit_value", 0m), ("delegation_allowed", false));
            return new World
            {
                Db = db, Factory = factory, Tenant = tenant, OperatorId = operatorId, CheckerId = checkerId,
                Operator = factory.ClientFor(tenant, $"dl-operator-{suffix}"), Checker = factory.ClientFor(tenant, $"dl-checker-{suffix}"),
            };
        }

        /// <summary>As the outbox leaves a failed delivery: dead-lettered in the ledger with its dead letter, and a later event for the record waiting behind it.</summary>
        public async Task<Held> HeldBack(string source, string eventType)
        {
            var record = Guid.CreateVersion7();
            var blocker = Guid.CreateVersion7();
            var waiting = Guid.CreateVersion7();
            // The waiting delivery is a real policy issue for the document consumer: released, it runs (and parks, the tenant having no document configuration).
            var issued = new LifePolicyIssued(record, $"LIF-{record.ToString("N")[..8]}", Guid.CreateVersion7(), "LIFE-DLD", 1, Guid.CreateVersion7(), Guid.CreateVersion7(),
                Guid.CreateVersion7(), "MONTHLY", "DEBIT_ORDER", "ZAR", 100m, TestClock.Today, null, 500_000m);
            var payload = JsonSerializer.SerializeToElement(issued, JsonSerializerOptions.Web);
            var envelope = new EventEnvelope(blocker, Tenant, eventType, 1, DateTimeOffset.UtcNow.AddMinutes(-10), "LifePolicy", record, Guid.CreateVersion7(), null, payload);
            foreach (var (eventId, status, at) in new[] { (blocker, "DEAD_LETTERED", "now() - interval '10 minutes'"), (waiting, "WAITING", "now() - interval '5 minutes'") })
            {
                await Db.ExecuteAsOwner($"""
                    INSERT INTO integration.parked_delivery (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, consumer, event_id, event_type,
                        event_version, occurred_at, aggregate_type, aggregate_id, correlation_id, payload, status, ledger_owned, attempts, failures, escalation_level)
                    VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @c, @e, @type, 1, {at}, 'LifePolicy', @p, gen_random_uuid(), @payload::jsonb,
                        @status, true, @attempts, @attempts, 0)
                    """, ("t", Tenant), ("c", Consumer), ("e", eventId), ("type", eventType), ("p", record), ("payload", payload.GetRawText()), ("status", status),
                    ("attempts", status == "WAITING" ? 0 : 10));
            }

            var deadLetter = Guid.CreateVersion7();
            await Db.ExecuteAsOwner("""
                INSERT INTO integration.dead_letter_message
                  (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source, message_id, payload, error, first_failed_at, attempts)
                VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', @source, @e::text, @payload::jsonb, 'System.TimeoutException', now(), 10)
                """, ("t", Tenant), ("id", deadLetter), ("source", source), ("e", blocker), ("payload", JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web)));
            return new Held(deadLetter, blocker, waiting);
        }

        public async Task<HttpResponseMessage> Discard(HttpClient client, Held held, string? messageRef = null)
        {
            var etag = (await client.GetAsync($"/api/v1/integration/dead-letters/{held.DeadLetterId}")).ETag();
            return await client.Send(HttpMethod.Post, $"/api/v1/integration/dead-letters/{held.DeadLetterId}/discard",
                new { messageRef = messageRef ?? held.BlockerEvent.ToString(), reason = Reason }, ifMatch: etag);
        }

        public async Task<JsonElement> View(Guid deadLetter)
        {
            var response = await Operator.GetAsync($"/api/v1/integration/dead-letters/{deadLetter}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.Json();
        }

        /// <summary>The ledger row's status, discarded_by and discard_reason, and whether it is resolved.</summary>
        public async Task<string> LedgerRow(Guid eventId) =>
            (await Db.ScalarAsOwner<string>("""
                SELECT status || '|' || coalesce(discarded_by, '') || '|' || coalesce(discard_reason, '') || CASE WHEN resolved_at IS NULL THEN '' ELSE '|resolved' END
                FROM integration.parked_delivery WHERE tenant_id = @t AND event_id = @e
                """, ("t", Tenant), ("e", eventId)))!;

        /// <summary>Workflow's approval of the dead letter's discard, once the request is relayed.</summary>
        public async Task<Guid> Approval(Guid deadLetter)
        {
            await Factory.DrainOutbox();
            var approval = await Db.ScalarAsOwner<Guid?>("""
                SELECT id FROM workflow.approval
                WHERE tenant_id = @t AND business_ref_type = 'DEAD_LETTER' AND business_ref_id = @d AND approval_type = 'DEAD_LETTER_DISCARD' AND status = 'REQUESTED'
                """, ("t", Tenant), ("d", deadLetter));
            approval.ShouldNotBeNull("no DEAD_LETTER_DISCARD approval waits for the dead letter");
            (await Db.ScalarAsOwner<Guid>("SELECT maker_id FROM workflow.approval WHERE id = @a", ("a", approval.Value))).ShouldBe(OperatorId);
            return approval.Value;
        }

        /// <summary>Decides the approval; null when it was accepted, else the refusal's code.</summary>
        public async Task<string?> Decide(HttpClient client, Guid approval, string decision)
        {
            var read = await client.GetAsync($"/api/v1/workflow/approvals/{approval}");
            read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
            var ifMatch = $"\"{(await read.Json()).GetProperty("rowVersion").GetInt64()}\"";
            var response = await client.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/{decision}",
                decision == "reject" ? new { reason = "Pay it instead" } : null, ifMatch: ifMatch);
            return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Code() ?? response.StatusCode.ToString();
        }
    }
}
