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
using Insys.Payments.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// DE-008 and DE-031 (Ade, 24 November 2026), PAY-005: a failed outbound payment goes to a controlled exception, listed with the payment's reference
/// and the record it paid, and resolved by INSYS once a later attempt pays that record or its module gives the payout up; the exceptions list is
/// paged with a cursor so every exception is reachable. The record is a commission statement reference with no statement behind it, so only Payments acts.
/// </summary>
public sealed class FailedPayoutTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Record = Insys.Distribution.Contracts.DistributionApprovals.StatementBusinessRefType;
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly Today = TestClock.Today;

    private static readonly string[] Permissions =
    [
        Insys.Party.Contracts.PartyPermissions.PartyCreate, Insys.Party.Contracts.PartyPermissions.BankAccountCreate, Insys.Party.Contracts.PartyPermissions.BankAccountVerify,
        PaymentPermissions.PaymentRead, PaymentPermissions.PayoutRead, PaymentPermissions.PayoutCreate, PaymentPermissions.PayoutRelease, PaymentPermissions.CommissionPayoutRead,
        PaymentPermissions.ReconciliationRead, PaymentPermissions.ReconciliationUpdate, PaymentPermissions.CallbackEndpointUpdate,
        Insys.Workflow.Contracts.WorkflowPermissions.ApprovalRead, Insys.Workflow.Contracts.WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _maker = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(Tenant, "payout-maker", Permissions.MakerSide());
        var checker = await db.UserWithPermissions(Tenant, "payout-checker", Permissions.CheckerSide());
        _maker = _factory.ClientFor(Tenant, "payout-maker");
        _checker = _factory.ClientFor(Tenant, "payout-checker");
        var approverRole = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, checker, approverRole);
        await db.AllowBankAccountVerification(Tenant, approverRole, Today.AddDays(-90));
        await db.SeedStatic(Tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{PaymentApprovals.PayoutApprovalType}-FP", Today.AddDays(-90),
            ("role_code", approverRole), ("transaction_type", PaymentApprovals.PayoutApprovalType), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        await db.SeedStatic(Tenant, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", Today.AddDays(-90),
            ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        await db.SeedBillingAndPayments(Tenant, Today.AddDays(-90));
        await db.SeedStatic(Tenant, "payment.ref_payment_methods", "ACTIVE", "EFT", Today.AddDays(-90),
            ("method_code", "EFT"), ("direction", "PAY"), ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AFailedPayout_RaisesOneException_ResolvedWhenALaterAttemptPaysTheRecord_DE008_PAY005()
    {
        var (record, account, payee, failed) = await FailedPayout("62000777001");

        var exception = await OpenFailure(failed);
        exception.GetProperty("paymentRef").GetString().ShouldNotBeNullOrEmpty();
        (exception.GetProperty("businessRefType").GetString(), exception.GetProperty("businessRefId").GetGuid()).ShouldBe((Record, record));

        // A redelivered failure raises nothing more.
        await Publish("payment.PaymentFailed", failed, new PaymentFailed(failed, exception.GetProperty("paymentRef").GetString()!, "OUT", PaymentPurposes.Commission, 41.50m, "ZAR",
            Record, record, "BOUNCED"));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM payment.reconciliation_exception WHERE payment_id = @p", ("p", failed))).ShouldBe(1);

        // The record's module asks again (its PAYOUT_RETRY approval granted): a new payment, paid through a new batch, resolves the failure.
        await Publish("payment.PayoutRequested", record, new PayoutRequested(Record, record, 2, PaymentPurposes.Commission, 41.50m, "ZAR", payee, account, false));
        var retry = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r AND id <> @f", ("r", record), ("f", failed));
        await PayInABatch(retry, "PAID");
        var resolved = await Exception(exception.GetProperty("id").GetGuid());
        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolution").GetString()!.ShouldStartWith("Paid by a later attempt");
    }

    [Fact]
    public async Task AFailedPayoutItsModuleGivesUp_IsResolvedWithTheReason_DE008()
    {
        var (record, _, _, failed) = await FailedPayout("62000777002");
        var exception = await OpenFailure(failed);

        await Publish("payment.PayoutAbandoned", record, new PayoutAbandoned(Record, record, "Refund cancelled; the money stays as credit."));
        var resolved = await Exception(exception.GetProperty("id").GetGuid());
        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolution").GetString().ShouldBe("Payout given up: Refund cancelled; the money stays as credit.");
    }

    [Fact]
    public async Task TheExceptionsList_IsPagedWithACursor_SoEveryExceptionIsReachable_DE031()
    {
        for (var i = 0; i < 3; i++)
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO payment.reconciliation_exception (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, exception_type, amount, status, raised_at)
                VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'UNMATCHED_LINE', 10, 'OPEN', now())
                """, [("t", Tenant), ("id", Guid.CreateVersion7())]);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var response = await _maker.GetAsync($"/api/v1/payments/reconciliation-exceptions?status=Open&limit=1{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var page = await response.Json();
            page.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(1);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        seen.Count.ShouldBeGreaterThanOrEqualTo(3);
        seen.ShouldBeUnique();
        seen.ShouldBe(seen.OrderBy(id => id).ToList());
        (await (await _maker.GetAsync("/api/v1/payments/reconciliation-exceptions?cursor=not-a-cursor")).Code()).ShouldBe("VALIDATION_FAILED");
    }

    /// <summary>A payout for a made-up record, batched, released and bounced by the provider: the record, the account, the payee and the failed payment.</summary>
    private async Task<(Guid Record, Guid Account, Guid Payee, Guid Payment)> FailedPayout(string accountNumber)
    {
        var payee = await _maker.RegisterPerson("Payee " + accountNumber);
        var account = await _maker.AddVerifiedBankAccount(_checker, _factory, payee, accountNumber);
        var record = Guid.CreateVersion7();
        await Publish("payment.PayoutRequested", record, new PayoutRequested(Record, record, 1, PaymentPurposes.Commission, 41.50m, "ZAR", payee, account, false));
        var payment = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @r", ("r", record));
        await PayInABatch(payment, "BOUNCED");
        return (record, account, payee, payment);
    }

    private async Task PayInABatch(Guid payment, string outcome)
    {
        var built = await _maker.Send(HttpMethod.Post, "/api/v1/payments/payout-batches", new { purpose = PaymentPurposes.Commission, currency = "ZAR" }, idempotent: true);
        built.StatusCode.ShouldBe(HttpStatusCode.Created, await built.Content.ReadAsStringAsync());
        var batch = (await built.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        var approval = (await (await _maker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={PaymentApprovals.PayoutBusinessRefType}&businessRefId={batch}"))
            .Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await _checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var released = await _checker.PostAsync($"/api/v1/payments/payout-batches/{batch}/release", null);
        released.StatusCode.ShouldBe(HttpStatusCode.OK, await released.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        (await _maker.SimulatorCallback(_factory, fixture.Database, payment, outcome)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
    }

    private async Task<JsonElement> OpenFailure(Guid payment)
    {
        var id = await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM payment.reconciliation_exception WHERE payment_id = @p AND exception_type = 'PAYMENT_FAILED' AND status = 'OPEN'", ("p", payment));
        var exception = await Exception(id);
        exception.GetProperty("exceptionType").GetString().ShouldBe("PAYMENT_FAILED");
        exception.GetProperty("paymentId").GetGuid().ShouldBe(payment);
        return exception;
    }

    private async Task<JsonElement> Exception(Guid id)
    {
        var response = await _maker.GetAsync($"/api/v1/payments/reconciliation-exceptions/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private async Task Publish<T>(string type, Guid aggregate, T payload)
    {
        var envelope = new EventEnvelope(Guid.CreateVersion7(), Tenant, type, 1, DateTimeOffset.UtcNow, "Payment", aggregate, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(payload, BuildingBlocks.Infrastructure.Persistence.ModuleDbContext.EventJson));
        await _factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
        await _factory.DrainOutbox();
    }
}
