/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Contracts;
using Insys.Payments.Application;
using Insys.Payments.Domain.Payments;
using Insys.Payments.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Payments.Infrastructure.Persistence;

internal sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public Task<Payment?> Get(Guid id, CancellationToken cancellationToken) => Payments().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Payment?> ByIdempotencyKey(string key, CancellationToken cancellationToken) => Payments().SingleOrDefaultAsync(p => p.IdempotencyKey == key, cancellationToken);

    public Task<Payment?> ByExternalRef(string providerCode, string externalRef, CancellationToken cancellationToken) =>
        Payments().FirstOrDefaultAsync(p => p.ProviderCode == providerCode && p.ExternalRef == externalRef, cancellationToken);

    public Task<Payment?> ByReference(string reference, CancellationToken cancellationToken) =>
        Payments().FirstOrDefaultAsync(p => p.PaymentRef == reference || p.ExternalRef == reference, cancellationToken);

    public async Task<IReadOnlyList<Payment>> AwaitingBatch(string purpose, string currency, CancellationToken cancellationToken) =>
        await Payments().Where(p => p.Direction == Direction.Out && p.Status == PaymentStatus.Initiated && p.PayoutBatchId == null && p.Purpose == purpose
            && p.Currency == currency).OrderBy(p => p.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Payment>> ForBusinessRef(string businessRefType, Guid businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await Payments().Where(p => p.BusinessRefType == businessRefType && p.BusinessRefId == businessRefId && (afterId == null || p.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Payment>> InBatch(Guid batchId, CancellationToken cancellationToken) =>
        await Payments().Where(p => p.PayoutBatchId == batchId).OrderBy(p => p.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PendingSince(DateTimeOffset before, int limit, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Pending && p.InitiatedAt < before).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public Task<PayoutBatch?> Batch(Guid id, CancellationToken cancellationToken) => db.Batches.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<ReconciliationException?> Exception(Guid id, CancellationToken cancellationToken) => db.Exceptions.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReconciliationException>> Exceptions(string? status, int limit, CancellationToken cancellationToken)
    {
        var query = db.Exceptions.AsNoTracking();
        if (status is not null && Enum.TryParse<ExceptionStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(e => e.Status == parsed);
        }

        return await query.OrderBy(e => e.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public void Add(Payment payment) => db.Payments.Add(payment);

    public void Add(PayoutBatch batch) => db.Batches.Add(batch);

    public void Add(ReconciliationException exception) => db.Exceptions.Add(exception);

    public void Add(BankStatementLine line) => db.StatementLines.Add(line);

    public void RecordCallback(string providerCode, string externalRef, string payloadJson, bool signatureValid, DateTimeOffset receivedAt, bool processed) =>
        db.Callbacks.Add(ProviderCallback.Create(providerCode, externalRef, payloadJson, signatureValid, receivedAt, processed));

    private IQueryable<Payment> Payments() => db.Payments.Include(p => p.History).Include(p => p.Allocations).AsSplitQuery();
}

internal sealed class PaymentCodes(PaymentsDbContext db) : IPaymentCodes
{
    public Task<bool> MethodUsable(string methodCode, Direction direction, string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var wanted = direction == Direction.In ? "COLLECT" : "PAY";
        return InForce<PaymentMethods>(day).AnyAsync(m => m.MethodCode == methodCode && (m.Direction == wanted || m.Direction == "BOTH") && m.Currencies.Contains(currency),
            cancellationToken);
    }

    public Task<string?> PayoutMethod(string currency, DateOnly day, CancellationToken cancellationToken) =>
        InForce<PaymentMethods>(day).Where(m => (m.Direction == "PAY" || m.Direction == "BOTH") && m.Currencies.Contains(currency)).OrderBy(m => m.MethodCode)
            .Select(m => (string?)m.MethodCode).FirstOrDefaultAsync(cancellationToken);

    public Task<string?> ProviderFor(string methodCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<PaymentProviderAndBankRegister>(day).Where(p => p.SupportedMethods.Contains(methodCode)).OrderBy(p => p.ProviderCode)
            .Select(p => (string?)p.ProviderCode).FirstOrDefaultAsync(cancellationToken);

    public async Task<ProviderOutcome> MapStatus(string providerCode, string providerStatus, DateOnly day, CancellationToken cancellationToken)
    {
        var mapping = await InForce<PaymentStatusMappings>(day).Where(m => m.ProviderCode == providerCode && m.ProviderStatus == providerStatus)
            .Select(m => new { m.PlatformStatus, m.Final }).FirstOrDefaultAsync(cancellationToken);
        return mapping switch
        {
            { PlatformStatus: "SUCCEEDED", Final: true } => ProviderOutcome.Succeeded,
            { PlatformStatus: "FAILED", Final: true } => ProviderOutcome.Failed,
            { PlatformStatus: "PENDING" or "INITIATED" } => ProviderOutcome.Pending,
            _ => ProviderOutcome.Unknown,
        };
    }

    public async Task<decimal> MatchingTolerance(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ReconciliationMatchingRules>(day).OrderBy(r => r.Tolerance).Select(r => (decimal?)r.Tolerance).FirstOrDefaultAsync(cancellationToken) ?? 0m;

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(PaymentsStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>
/// A stand-in for a payment provider, for development and tests (provider code SIMULATOR). It accepts every payment
/// as pending, with an external reference, and the outcome arrives by callback at the tenant's
/// callback address (ADR-023); an amount ending in .13 is rejected and one ending in .99 gets no answer (an unknown outcome).
/// TODO(spec): real provider integrations are not specified (OQ-058).
/// </summary>
internal sealed class SimulatorGateway : IPaymentGateway
{
    public const string Code = "SIMULATOR";

    public bool Handles(string providerCode) => providerCode == Code;

    /// <summary>The simulator does not use the payee's account details; a real adapter would send them to its provider, and nowhere else.</summary>
    public Task<(ProviderOutcome Outcome, string? ExternalRef, string? Reason)> Send(Payment payment, PayeeAccountDetails? payee, CancellationToken cancellationToken)
    {
        var cents = (int)((payment.Amount * 100) % 100);
        return Task.FromResult(cents switch
        {
            13 => (ProviderOutcome.Failed, (string?)null, (string?)"Declined by the simulator"),
            99 => (ProviderOutcome.Unknown, (string?)null, (string?)"No answer from the simulator"),
            _ => (ProviderOutcome.Pending, (string?)("SIM-" + payment.IdempotencyKey.GetHashCode().ToString("X8")), (string?)null),
        });
    }
}

internal sealed class PaymentsEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        PaymentConfirmed { Direction: Direction.In } e => new Contracts.PaymentReceived(e.PaymentId, e.PaymentRef, e.Purpose, e.Amount, e.Currency, e.ValueDate,
            e.CounterpartyPartyId, e.BusinessRefType, e.BusinessRefId),
        PaymentConfirmed e => new Contracts.PaymentSucceeded(e.PaymentId, e.PaymentRef, e.Purpose, e.Amount, e.Currency, e.ValueDate, e.BusinessRefType, e.BusinessRefId),
        Domain.Payments.PaymentFailed e => new Contracts.PaymentFailed(e.PaymentId, e.PaymentRef, Upper(e.Direction), e.Purpose, e.Amount, e.Currency, e.BusinessRefType,
            e.BusinessRefId, e.Reason),
        Domain.Payments.PaymentOutcomeUnknown e => new Contracts.PaymentOutcomeUnknown(e.PaymentId, e.PaymentRef, Upper(e.Direction), e.Purpose, e.Amount, e.Currency),
        Domain.Payments.PaymentInitiated e => new Contracts.PaymentInitiated(e.PaymentId, e.PaymentRef, Upper(e.Direction), e.Purpose, e.Amount, e.Currency,
            e.BusinessRefType, e.BusinessRefId),
        Domain.Payments.PaymentAllocated e => new Contracts.PaymentAllocated(e.PaymentId, e.AllocationId, e.InvoiceId, e.Amount, e.Currency),
        Domain.Payments.PaymentReversed e => new Contracts.PaymentReversed(e.PaymentId, e.PaymentRef, Upper(e.Direction), e.Purpose, e.Amount, e.Currency, e.ReversedOn,
            e.Allocations.Select(a => new Contracts.ReversedAllocation(a.AllocationId, a.InvoiceId, a.Amount)).ToList(), e.Reason),
        Domain.Payments.PayoutReleased e => new Contracts.PayoutReleased(e.BatchId, e.BatchRef, e.Purpose, e.PaymentCount, e.TotalAmount, e.Currency),
        Domain.Payments.ReconciliationExceptionRaised e => new Contracts.ReconciliationExceptionRaised(e.ExceptionId, e.ExceptionType, e.PaymentId, e.StatementLineId, e.Amount),
        PayoutBatchBuilt e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PaymentApprovals.PayoutBusinessRefType, e.BatchId,
            Contracts.PaymentApprovals.PayoutApprovalType, e.BuiltBy, e.TotalAmount, e.Currency, $"Release payout batch {e.BatchRef}"),
        _ => null,
    };

    private static string Upper(Direction direction) => direction.ToString().ToUpperInvariant();
}

internal sealed class CallbackEndpoints(PaymentsDbContext db) : ICallbackEndpoints
{
    public Task<CallbackEndpoint?> Get(Guid id, CancellationToken cancellationToken) => db.CallbackEndpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CallbackEndpoint>> All(CancellationToken cancellationToken) =>
        await db.CallbackEndpoints.AsNoTracking().OrderBy(e => e.ProviderCode).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CallbackEndpoint>> Active(string providerCode, CancellationToken cancellationToken) =>
        await db.CallbackEndpoints.Where(e => e.ProviderCode == providerCode && e.Status == CallbackEndpointStatus.Active).ToListAsync(cancellationToken);

    public void Add(CallbackEndpoint endpoint) => db.CallbackEndpoints.Add(endpoint);

    public async Task<bool> TryRecordReceipt(Guid endpointId, string callbackId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payment.callback_receipt (tenant_id, id, endpoint_id, callback_id, received_at)
            VALUES ({db.CurrentTenantId}, {Guid.CreateVersion7()}, {endpointId}, {callbackId}, {now})
            ON CONFLICT (tenant_id, endpoint_id, callback_id) DO NOTHING
            """, cancellationToken) == 1;

    public Task PurgeReceipts(Guid endpointId, DateTimeOffset before, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM payment.callback_receipt WHERE tenant_id = {db.CurrentTenantId} AND endpoint_id = {endpointId} AND received_at < {before}", cancellationToken);
}

/// <summary>The key-hash lookup across tenants, through payment.resolve_callback_endpoint (V0033), on the Payments role.</summary>
internal sealed class CallbackEndpointResolver([FromKeyedServices(PaymentsDbContext.SchemaName)] Npgsql.NpgsqlDataSource dataSource) : ICallbackEndpointResolver
{
    public async Task<(Guid TenantId, Guid EndpointId)?> Resolve(string keyHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT tenant_id, endpoint_id FROM payment.resolve_callback_endpoint(@hash)", connection, transaction);
        command.Parameters.AddWithValue("hash", keyHash);
        (Guid, Guid)? found = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                found = (reader.GetGuid(0), reader.GetGuid(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return found;
    }
}
