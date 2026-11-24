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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Distribution.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Through the whole host (Ade, 24 November 2026): a commission statement approved with a negative net is carried forward and, once
/// owed for the tenant's days, invoiced to the intermediary after a second person's approval (OQ-062); a statement whose payout failed
/// is retried with the failed payment's reference typed, after a second person's approval, as a new payout attempt (DE-008).
/// </summary>
public sealed class CommissionStatementOutcomesTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Today = TestClock.Today;
    private static readonly DateOnly From = Today.AddYears(-2);
    private static readonly string Month = Today.ToString("yyyy-MM");

    private static readonly string[] Permissions =
    [
        PartyPermissions.PartyCreate, PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify,
        DistributionPermissions.CommissionRead, DistributionPermissions.StatementRead, DistributionPermissions.StatementCreate, DistributionPermissions.StatementUpdate,
        PaymentPermissions.PaymentRead, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private Guid _tenant;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;
    private Guid _party;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _tenant = Guid.CreateVersion7();
        var db = fixture.Database;
        await db.SeedTenant(_tenant, "CS" + suffix.ToUpperInvariant());
        var maker = await db.UserWithPermissions(_tenant, $"maker-{suffix}", Permissions.MakerSide());
        var checker = await db.UserWithPermissions(_tenant, $"checker-{suffix}", Permissions.CheckerSide());
        var approverRole = await db.RoleWithPermissions(_tenant);
        await db.AssignRole(_tenant, checker, approverRole);
        await db.AssignRole(_tenant, maker, await db.RoleWithPermissions(_tenant));
        foreach (var type in new[] { DistributionApprovals.StatementApprovalType, PaymentApprovals.PayoutRetryApprovalType, DistributionApprovals.ClawbackInvoiceApprovalType,
                     "BANK_ACCOUNT_VERIFICATION" })
        {
            await Seed("workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", ("role_code", approverRole), ("transaction_type", type),
                ("limit_value", 100_000_000m), ("delegation_allowed", false));
        }

        _maker = _factory.ClientFor(_tenant, $"maker-{suffix}");
        _checker = _factory.ClientFor(_tenant, $"checker-{suffix}");
        _party = await _maker.RegisterPerson("Broker");
        await _maker.AddVerifiedBankAccount(_checker, _factory, _party, "8800" + Random.Shared.Next(100_000, 999_999));
        await Seed("commission.mst_intermediary_register", "ACTIVE", "BRK-1", ("intermediary_code", "BRK-1"), ("type", "BROKER"), ("legal_name", "BRK-1"),
            ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-1" })), ("status", "ACTIVE"), ("hierarchy_node", "NODE-1"), ("party_id", _party));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ANegativeStatement_IsCarriedForward_ThenInvoicedOnceOwedForTheTenantsDays_WithItsReferenceTyped_AndASecondPerson_OQ062()
    {
        // 25.00 payable against a 100.00 clawback of commission on an earlier statement: the net is -75.00.
        await Entitlement("PAYABLE", 25m);
        await ClawedBackOnAnEarlierStatement(100m);
        var statement = await Prepare();
        (await Statement(statement)).GetProperty("netAmount").GetDecimal().ShouldBe(-75m);
        await Decide(DistributionApprovals.StatementApprovalType, statement, approve: true);

        var carried = await Statement(statement);
        (carried.GetProperty("status").GetString(), carried.GetProperty("carriedAmount").GetDecimal(), carried.GetProperty("carriedOn").GetString(),
            carried.GetProperty("carriedOwed").GetDecimal()).ShouldBe(("CarriedForward", 75m, Today.ToString("yyyy-MM-dd"), 75m));
        (await Outbox("commission.CommissionStatementApproved", statement)).ShouldBe(0, "nothing is paid");
        var reference = carried.GetProperty("reference").GetString()!;
        reference.ShouldBe($"BRK-1/{Month}");
        (await List("?status=CarriedForward")).ShouldContain(statement);
        (await List("?invoiceDue=true")).ShouldNotContain(statement);

        // Not yet owed for 90 days, and the intermediary is active: refused; a mistyped reference is refused too, and nothing changes.
        (await Problem(await Invoice(statement, reference))).ShouldBe((HttpStatusCode.UnprocessableEntity, "CLAWBACK_INVOICE_NOT_DUE"));
        await fixture.Database.ExecuteAsOwner("UPDATE commission.commission_statement SET carried_on = carried_on - 90 WHERE tenant_id = @t AND id = @id",
            ("t", _tenant), ("id", statement));
        (await List("?invoiceDue=true")).ShouldContain(statement);
        (await Problem(await Invoice(statement, "BRK-1/1999-01"))).ShouldBe((HttpStatusCode.UnprocessableEntity, "TYPED_REFERENCE_MISMATCH"));
        (await Statement(statement)).GetProperty("pendingAction").ValueKind.ShouldBe(JsonValueKind.Null);

        // Asked, then rejected: it stays carried.
        (await Invoice(statement, reference)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Statement(statement)).GetProperty("pendingAction").GetString().ShouldBe(DistributionApprovals.ClawbackInvoiceApprovalType);
        (await List("?invoiceDue=true")).ShouldNotContain(statement, "it awaits a decision");
        await Decide(DistributionApprovals.ClawbackInvoiceApprovalType, statement, approve: false);
        var rejected = await Statement(statement);
        (rejected.GetProperty("status").GetString(), rejected.GetProperty("pendingAction").ValueKind).ShouldBe(("CarriedForward", JsonValueKind.Null));

        // Asked again and granted: the carried amount is closed as invoiced, and Billing is asked to invoice the intermediary's party.
        (await Invoice(statement, reference)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var approval = await Decide(DistributionApprovals.ClawbackInvoiceApprovalType, statement, approve: true);
        (await (await _checker.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("amount").GetDecimal().ShouldBe(75m);
        var invoiced = await Statement(statement);
        (invoiced.GetProperty("status").GetString(), invoiced.GetProperty("invoicedAmount").GetDecimal(), invoiced.GetProperty("carriedOwed").GetDecimal())
            .ShouldBe(("Invoiced", 75m, 0m));
        var requested = await fixture.Database.ScalarAsOwner<string>("""
            SELECT payload::text FROM commission.outbox_event WHERE tenant_id = @t AND event_type = 'commission.CommissionClawbackInvoiceRequested' AND aggregate_id = @id
            """, ("t", _tenant), ("id", statement));
        using var payload = JsonDocument.Parse(requested!);
        (payload.RootElement.GetProperty("payerPartyId").GetGuid(), payload.RootElement.GetProperty("amount").GetDecimal(), payload.RootElement.GetProperty("reference").GetString())
            .ShouldBe((_party, 75m, reference));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'InvoiceStatementClawback' AND target_id = @id",
            ("t", _tenant), ("id", statement.ToString()))).ShouldBe(2L);
    }

    [Fact]
    public async Task AFailedPayout_IsPaymentFailed_AndRetriedWithItsPaymentRefTyped_AfterASecondPerson_AsTheNextAttempt_DE008()
    {
        await Entitlement("PAYABLE", 40m);
        var statement = await Prepare();
        await Decide(DistributionApprovals.StatementApprovalType, statement, approve: true);
        (await Statement(statement)).GetProperty("status").GetString().ShouldBe("Approved");

        var failed = new PaymentFailed(Guid.CreateVersion7(), "PAY-CS-0001", "OUT", PaymentPurposes.Commission, 40m, "ZAR", DistributionApprovals.StatementBusinessRefType,
            statement, "Account closed");
        await Deliver(failed, "payment.PaymentFailed", failed.PaymentId);
        await Deliver(failed, "payment.PaymentFailed", failed.PaymentId); // redelivered: nothing changes
        var view = await Statement(statement);
        (view.GetProperty("status").GetString(), view.GetProperty("failedPaymentRef").GetString(), view.GetProperty("payoutAttempt").GetInt32())
            .ShouldBe(("PaymentFailed", "PAY-CS-0001", 1));

        (await Problem(await Retry(statement, "PAY-CS-9999"))).ShouldBe((HttpStatusCode.UnprocessableEntity, "TYPED_REFERENCE_MISMATCH"));
        (await Statement(statement)).GetProperty("pendingAction").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _maker.Send(HttpMethod.Post, $"/api/v1/distribution/statements/{statement}/retry-payout", new { paymentRef = "PAY-CS-0001" }, ifMatch: "\"999\""))
            .StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        // Rejected: it stays failed. Asked again and granted: approved again, and Payments asked for attempt 2's payout.
        (await Retry(statement, "PAY-CS-0001")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Decide(PaymentApprovals.PayoutRetryApprovalType, statement, approve: false);
        (await Statement(statement)).GetProperty("status").GetString().ShouldBe("PaymentFailed");
        (await Retry(statement, "PAY-CS-0001")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var approval = await Decide(PaymentApprovals.PayoutRetryApprovalType, statement, approve: true);
        (await (await _checker.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("amount").GetDecimal().ShouldBe(40m);
        var retried = await Statement(statement);
        (retried.GetProperty("status").GetString(), retried.GetProperty("payoutAttempt").GetInt32(), retried.GetProperty("pendingAction").ValueKind)
            .ShouldBe(("Approved", 2, JsonValueKind.Null));
        var requested = await fixture.Database.ScalarAsOwner<string>("""
            SELECT payload::text FROM commission.outbox_event WHERE tenant_id = @t AND event_type = 'payment.PayoutRequested' AND aggregate_id = @id
            """, ("t", _tenant), ("id", statement));
        using var payload = JsonDocument.Parse(requested!);
        (payload.RootElement.GetProperty("attempt").GetInt32(), payload.RootElement.GetProperty("purpose").GetString(), payload.RootElement.GetProperty("amount").GetDecimal(),
            payload.RootElement.GetProperty("gated").GetBoolean(), payload.RootElement.GetProperty("payeePartyId").GetGuid())
            .ShouldBe((2, PaymentPurposes.Commission, 40m, false, _party));
    }

    [Fact]
    public async Task AStatementWithANetOfZero_ClosesNil_AndACarriedAmount_IsTheNextStatementsOpeningLine_OQ062()
    {
        await Entitlement("PAYABLE", 30m);
        await ClawedBackOnAnEarlierStatement(50m);
        var carried = await Prepare("2026-01");
        await Decide(DistributionApprovals.StatementApprovalType, carried, approve: true);
        (await Statement(carried)).GetProperty("carriedAmount").GetDecimal().ShouldBe(20m);

        // The next statement has 20.00 of commission: the carried 20.00 is its opening line, so it closes nil, and the carried statement is settled.
        await Entitlement("PAYABLE", 20m);
        var next = await Prepare("2026-02");
        var prepared = await Statement(next);
        (prepared.GetProperty("openingAmount").GetDecimal(), prepared.GetProperty("netAmount").GetDecimal()).ShouldBe((20m, 0m));
        (await Statement(carried)).GetProperty("carryOnStatementId").GetGuid().ShouldBe(next);
        await Decide(DistributionApprovals.StatementApprovalType, next, approve: true);
        (await Statement(next)).GetProperty("status").GetString().ShouldBe("ClosedNil");
        var settled = await Statement(carried);
        (settled.GetProperty("status").GetString(), settled.GetProperty("carrySettledAmount").GetDecimal()).ShouldBe(("SettledByCarryForward", 20m));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM commission.commission_entitlement WHERE tenant_id = @t AND status = 'PAYABLE'", ("t", _tenant)))
            .ShouldBe(0L, "every line is settled once");
    }

    private Task Seed(string table, string status, string code, params (string Column, object Value)[] fields) =>
        fixture.Database.SeedStatic(_tenant, table, status, code, From, fields);

    private Task Entitlement(string status, decimal amount, Guid? statementId = null, Guid? id = null) =>
        fixture.Database.ExecuteAsOwner("""
            INSERT INTO commission.commission_entitlement (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, intermediary_code, source_type,
                                                           source_id, policy_ref, plan_code, plan_version, basis_amount, rate, amount, currency, status, product_code,
                                                           source_domain, due_date, statement_id)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'BRK-1', 'INVOICE', gen_random_uuid(), gen_random_uuid(), 'PLAN-10', 1, @a * 10, 10, @a, 'ZAR',
                    @s, 'AE-GMOT', 'GENERAL', @d, @st)
            """, ("t", _tenant), ("id", id ?? Guid.CreateVersion7()), ("a", amount), ("s", status), ("d", Today), ("st", (object?)statementId ?? DBNull.Value));

    /// <summary>An entitlement put on an approved statement, then clawed back before it was paid: its clawback is recovered on the next statement.</summary>
    private async Task ClawedBackOnAnEarlierStatement(decimal amount)
    {
        var earlier = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO commission.commission_statement (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, intermediary_code, period_code, currency,
                                                         gross_amount, tax_amount, net_amount, status)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'BRK-1', @p, 'ZAR', @a, 0, @a, 'APPROVED')
            """, ("t", _tenant), ("id", earlier), ("p", $"20{Random.Shared.Next(10, 24)}-{Random.Shared.Next(1, 12):00}"), ("a", amount));
        var entitlement = Guid.CreateVersion7();
        await Entitlement("CLAWED_BACK", amount, earlier, entitlement);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO commission.clawback (tenant_id, id, created_at, created_by, entitlement_id, reason, amount) VALUES (@t, gen_random_uuid(), now(), 'seed', @e, 'POLICY_CANCELLED', @a)
            """, ("t", _tenant), ("e", entitlement), ("a", amount));
    }

    private async Task<Guid> Prepare(string? period = null)
    {
        var response = await _maker.Send(HttpMethod.Post, "/api/v1/distribution/statements", new { intermediaryCode = "BRK-1", periodCode = period ?? Month, currency = "ZAR" },
            idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Statement(Guid id)
    {
        var response = await _maker.GetAsync($"/api/v1/distribution/statements/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private async Task<IReadOnlyList<Guid>> List(string query)
    {
        var page = await (await _maker.GetAsync($"/api/v1/distribution/statements{query}&intermediaryCode=BRK-1&limit=200")).Json();
        return [.. page.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())];
    }

    private async Task<string> ETag(Guid id) => $"\"{(await Statement(id)).GetProperty("rowVersion").GetInt64()}\"";

    private async Task<HttpResponseMessage> Invoice(Guid id, string reference)
    {
        var response = await _maker.Send(HttpMethod.Post, $"/api/v1/distribution/statements/{id}/invoice-clawback", new { reference, reason = "Owed by the broker" },
            ifMatch: await ETag(id));
        await _factory.DrainOutbox();
        return response;
    }

    private async Task<HttpResponseMessage> Retry(Guid id, string paymentRef)
    {
        var response = await _maker.Send(HttpMethod.Post, $"/api/v1/distribution/statements/{id}/retry-payout", new { paymentRef }, ifMatch: await ETag(id));
        await _factory.DrainOutbox();
        return response;
    }

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Json()).TryGetProperty("code", out var code) ? code.GetString() : await response.Content.ReadAsStringAsync());

    /// <summary>The checker decides the statement's waiting approval of the type in Workflow; returns the approval.</summary>
    private async Task<Guid> Decide(string approvalType, Guid statement, bool approve)
    {
        await _factory.DrainOutbox();
        var approval = await fixture.Database.ScalarAsOwner<Guid>("""
            SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = 'COMMISSION_STATEMENT' AND business_ref_id = @i AND approval_type = @a
              AND status = 'REQUESTED'
            """, ("t", _tenant), ("i", statement), ("a", approvalType));
        approval.ShouldNotBe(Guid.Empty, $"no {approvalType} approval is waiting");
        var rowVersion = (await (await _checker.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("rowVersion").GetInt64();
        var decided = approve
            ? await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: $"\"{rowVersion}\"")
            : await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Not now" }, ifMatch: $"\"{rowVersion}\"");
        decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        return approval;
    }

    private Task<long> Outbox(string type, Guid aggregate) =>
        fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM commission.outbox_event WHERE tenant_id = @t AND event_type = @e AND aggregate_id = @id",
            ("t", _tenant), ("e", type), ("id", aggregate));

    private async Task Deliver<T>(T integrationEvent, string type, Guid aggregateId)
    {
        var envelope = new EventEnvelope(Guid.CreateVersion7(), _tenant, type, 1, DateTimeOffset.UtcNow, "Payment", aggregateId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web));
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None);
    }
}
