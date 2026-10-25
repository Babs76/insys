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
using FluentValidation;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Payments.Domain.Payments;
using Insys.Workflow.Contracts;

namespace Insys.Payments.Application;

public interface IPaymentRepository
{
    Task<Payment?> Get(Guid id, CancellationToken cancellationToken);

    Task<Payment?> ByIdempotencyKey(string key, CancellationToken cancellationToken);

    Task<Payment?> ByExternalRef(string providerCode, string externalRef, CancellationToken cancellationToken);

    /// <summary>A payment whose reference or external reference is the text, for matching a bank statement line.</summary>
    Task<Payment?> ByReference(string reference, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> AwaitingBatch(string purpose, string currency, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> InBatch(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The payments for one business record, oldest first (GAP-036).</summary>
    Task<IReadOnlyList<Payment>> ForBusinessRef(string businessRefType, Guid businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> PendingSince(DateTimeOffset before, int limit, CancellationToken cancellationToken);

    /// <summary>Payments whose outcome is unknown, waiting for a person's decision (PAY-005).</summary>
    Task<IReadOnlyList<Payment>> Unknown(CancellationToken cancellationToken);

    Task<PayoutBatch?> Batch(Guid id, CancellationToken cancellationToken);

    Task<ReconciliationException?> Exception(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconciliationException>> Exceptions(string? status, int limit, CancellationToken cancellationToken);

    void Add(Payment payment);

    void Add(PayoutBatch batch);

    void Add(ReconciliationException exception);

    void Add(BankStatementLine line);

    /// <summary>Records a provider callback as received (payment.provider_callback), whatever its signature.</summary>
    void RecordCallback(string providerCode, string externalRef, string payloadJson, bool signatureValid, DateTimeOffset receivedAt, bool processed);
}

/// <summary>The Payments module's static data and provider configuration.</summary>
public interface IPaymentCodes
{
    /// <summary>A payment method in force for the direction and currency (payment.ref_payment_methods).</summary>
    Task<bool> MethodUsable(string methodCode, Direction direction, string currency, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The first outbound method in force for the currency, for payouts.</summary>
    Task<string?> PayoutMethod(string currency, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A provider in the register that supports the method (payment.mst_payment_provider_and_bank_register).</summary>
    Task<string?> ProviderFor(string methodCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The platform outcome of a provider status (payment.ref_payment_status_mappings); Unknown when it has no mapping (PAY-005).</summary>
    Task<ProviderOutcome> MapStatus(string providerCode, string providerStatus, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The matching tolerance for bank statement lines (payment.cfg_reconciliation_matching_rules).</summary>
    Task<decimal> MatchingTolerance(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>A payment provider or bank. The platform sends with the payment's idempotency key, so a retry never pays twice (PAY-001).</summary>
public interface IPaymentGateway
{
    bool Handles(string providerCode);

    /// <summary>Sends the payment. <paramref name="payee"/> is the payee's bank account, decrypted, for a payout to one; the gateway never logs or stores it.</summary>
    Task<(ProviderOutcome Outcome, string? ExternalRef, string? Reason)> Send(Payment payment, PayeeAccountDetails? payee, CancellationToken cancellationToken);
}

public sealed record StatusChangeView(PaymentStatus From, PaymentStatus To, string Source, DateTimeOffset ChangedAt);

public sealed record AllocationView(Guid Id, Guid InvoiceId, decimal Amount, string Currency, DateTimeOffset AllocatedAt, Guid? ReversedById);

public sealed record PaymentView(Guid Id, string PaymentRef, string Direction, [property: OneOf(PaymentPurposes.Premium, PaymentPurposes.Refund, PaymentPurposes.ClaimPayout, PaymentPurposes.Recovery, PaymentPurposes.Commission)] string Purpose, string MethodCode, string ProviderCode, decimal Amount, string Currency,
    PaymentStatus Status, string? ExternalRef, string IdempotencyKey, DateOnly? ValueDate, string? BusinessRefType, Guid? BusinessRefId, Guid? PayoutBatchId,
    IReadOnlyList<StatusChangeView> History, IReadOnlyList<AllocationView> Allocations)
{
    public static PaymentView From(Payment p) => new(p.Id, p.PaymentRef, p.Direction.ToString(), p.Purpose, p.MethodCode, p.ProviderCode, p.Amount, p.Currency,
        p.Status, p.ExternalRef, p.IdempotencyKey, p.ValueDate, p.BusinessRefType, p.BusinessRefId, p.PayoutBatchId,
        p.History.OrderBy(h => h.ChangedAt).ThenBy(h => h.Id).Select(h => new StatusChangeView(h.FromStatus, h.ToStatus, h.Source, h.ChangedAt)).ToList(),
        p.Allocations.Select(a => new AllocationView(a.Id, a.InvoiceId, a.Amount, a.Currency, a.AllocatedAt, a.ReversedById)).ToList());
}

/// <param name="PartlyInScope">
/// True when the batch also pays records outside the caller's scope (F103): the count and total are then of the payments
/// in scope only, so no other branch's or product's figures show through the batch.
/// </param>
public sealed record BatchView(Guid Id, string BatchRef, [property: OneOf(PaymentPurposes.Premium, PaymentPurposes.Refund, PaymentPurposes.ClaimPayout, PaymentPurposes.Recovery, PaymentPurposes.Commission)] string Purpose, int PaymentCount, decimal TotalAmount, string Currency, BatchStatus Status, Guid? BuiltBy,
    Guid? ApprovedBy, Guid? ReleasedBy, bool PartlyInScope, LatestRejection? LatestRejection = null)
{
    public static BatchView From(PayoutBatch b) => new(b.Id, b.BatchRef, b.Purpose, b.PaymentCount, b.TotalAmount, b.Currency, b.Status, b.BuiltBy,
        b.ApprovedBy, b.ReleasedBy, false);

    public static BatchView Of(PayoutBatch b, IReadOnlyList<Payment> visible, bool partly, LatestRejection? latestRejection = null) => partly
        ? new(b.Id, b.BatchRef, b.Purpose, visible.Count, visible.Sum(p => p.Amount), b.Currency, b.Status, b.BuiltBy, b.ApprovedBy, b.ReleasedBy, true, latestRejection)
        : From(b) with { LatestRejection = latestRejection };
}

public sealed record ExceptionView(Guid Id, string ExceptionType, Guid? PaymentId, Guid? StatementLineId, decimal Amount, ExceptionStatus Status, Guid? AssignedTo)
{
    public static ExceptionView From(ReconciliationException e) => new(e.Id, e.ExceptionType, e.PaymentId, e.StatementLineId, e.Amount, e.Status, e.AssignedTo);
}

public sealed record StatementLine(DateOnly Date, decimal Amount, string Currency, string Reference);

public sealed record StatementResult(int Matched, int Exceptions);

/// <summary>
/// Command InitiatePayment. A key already used returns the payment made with it (PAY-001). An inbound payment with a
/// provider is sent at once; an outbound one waits for a payout batch.
/// </summary>
/// <param name="BankAccountId">For a payout: the payee's verified bank account (party.bank_account), by id; never its details.</param>
public sealed record InitiatePayment(string IdempotencyKey, Direction Direction, string Purpose, string MethodCode, decimal Amount, string Currency,
    Guid? CounterpartyPartyId, string? BusinessRefType, Guid? BusinessRefId, Guid? BankAccountId = null) : ICommand<PaymentView>;

public sealed record MarkUnknown(Guid PaymentId) : ICommand<PaymentView>;

/// <summary>Command ResolveUnknown: a person decides an unknown outcome from the evidence (PAY-005).</summary>
public sealed record ResolveUnknown(Guid PaymentId, bool Succeeded, string Evidence) : ICommand<PaymentView>;

public sealed record ReversePayment(Guid PaymentId, string Reason) : ICommand<PaymentView>;

public sealed record AllocatePayment(Guid PaymentId) : ICommand<PaymentView>;

public sealed record BuildBatch(string Purpose, string Currency) : ICommand<BatchView>;

public sealed record ReleaseBatch(Guid BatchId) : ICommand<BatchView>;

public sealed record ImportStatement(string AccountRef, IReadOnlyList<StatementLine> Lines) : ICommand<StatementResult>;

public sealed record AssignException(Guid ExceptionId) : ICommand<ExceptionView>;

public sealed record ResolveException(Guid ExceptionId) : ICommand<ExceptionView>;

public sealed record GetPayment(Guid PaymentId) : IQuery<PaymentView>;

public sealed record GetBatch(Guid BatchId) : IQuery<BatchView>;

/// <summary>GAP-036: the payments for a business record (a refund, a claim payment instruction, an invoice), oldest first.</summary>
public sealed record ListPayments(string BusinessRefType, Guid BusinessRefId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PaymentView>>;

/// <summary>GAP-036: the payments in a payout batch.</summary>
public sealed record ListBatchPayments(Guid BatchId) : IQuery<IReadOnlyList<PaymentView>>;

public sealed record ListExceptions(string? Status, int Limit) : IQuery<IReadOnlyList<ExceptionView>>;

public sealed record PendingPayments(DateTimeOffset Before) : IQuery<IReadOnlyList<Guid>>;

public sealed class InitiatePaymentValidator : AbstractValidator<InitiatePayment>
{
    public InitiatePaymentValidator()
    {
        RuleFor(c => c.IdempotencyKey).NotEmpty().MaximumLength(80);
        RuleFor(c => c.Purpose).NotEmpty().MaximumLength(15);
        RuleFor(c => c.MethodCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.BusinessRefType).MaximumLength(30);
    }
}

public sealed class ResolveUnknownValidator : AbstractValidator<ResolveUnknown>
{
    public ResolveUnknownValidator() => RuleFor(c => c.Evidence).NotEmpty().MaximumLength(500);
}

public sealed class ReversePaymentValidator : AbstractValidator<ReversePayment>
{
    public ReversePaymentValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

public sealed class ImportStatementValidator : AbstractValidator<ImportStatement>
{
    public ImportStatementValidator()
    {
        RuleFor(c => c.AccountRef).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Lines).NotEmpty();
        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Currency).Length(3);
            line.RuleFor(l => l.Reference).NotEmpty().MaximumLength(80);
        });
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>Starts payments: one per idempotency key (PAY-001); inbound collections are sent at once, payouts wait for a batch.</summary>
public sealed class PaymentStarter(IPaymentRepository payments, IPaymentCodes codes, IEnumerable<IPaymentGateway> gateways, IBankAccounts bankAccounts,
    IBankAccountDetails bankAccountDetails, ITenantContext tenant, IAuditTrail audit, IBusinessDate businessDate, IClock clock)
{
    public async Task<Result<Payment>> Start(InitiatePayment command, CancellationToken cancellationToken)
    {
        if (await payments.ByIdempotencyKey(command.IdempotencyKey, cancellationToken) is { } existing)
        {
            return existing;
        }

        var day = businessDate.Today;
        if (!await codes.MethodUsable(command.MethodCode, command.Direction, command.Currency, day, cancellationToken))
        {
            return Error.Validation(PaymentErrorCodes.CodeNotInForce, $"{command.MethodCode} is not a {command.Direction} payment method in force for {command.Currency}.");
        }

        if (await codes.ProviderFor(command.MethodCode, day, cancellationToken) is not { } provider)
        {
            return Error.Validation(PaymentErrorCodes.NoProvider, $"No payment provider in the register supports {command.MethodCode}.");
        }

        var payment = Payment.InitiatePayment(Guid.CreateVersion7(), command.IdempotencyKey, command.Direction, command.Purpose, command.MethodCode, provider,
            command.Amount, command.Currency, command.CounterpartyPartyId, command.BusinessRefType, command.BusinessRefId, clock.UtcNow, command.BankAccountId);
        if (payment.IsFailure)
        {
            return payment.Error;
        }

        payments.Add(payment.Value);
        if (command.Direction == Direction.In)
        {
            await Send(payment.Value, cancellationToken);
        }

        return payment;
    }

    /// <summary>
    /// Sends a payment to its provider. Any exception is an unknown outcome, never a success (PAY-005). TODO(spec):
    /// the provider is called inside the transaction; with the idempotency key a retried send cannot pay twice (OQ-058).
    /// </summary>
    public async Task Send(Payment payment, CancellationToken cancellationToken)
    {
        // A payout goes to the bank account it was approved for, and only while that account is the payee's current,
        // verified one; a superseded account fails the payment, and paying again needs a fresh approval (ADR-009).
        PayeeAccountDetails? payee = null;
        if (payment.Direction == Direction.Out && payment.BankAccountId is { } accountId)
        {
            var tenantId = tenant.RequireTenantId();
            if (await bankAccounts.Get(tenantId, accountId, cancellationToken) is not { IsPayable: true }
                || (payee = await bankAccountDetails.ForPayment(tenantId, accountId, cancellationToken)) is null)
            {
                payment.Sent(ProviderOutcome.Failed, null, "The payee's bank account changed after approval", businessDate.Today, clock.UtcNow);
                return;
            }

            audit.Record(new AuditEntry("PAYMENT", "ReadBankAccountForPayment", "BankAccount", accountId.ToString(),
                new Dictionary<string, string?> { ["payment"] = payment.Id.ToString() }));
        }

        var gateway = gateways.FirstOrDefault(g => g.Handles(payment.ProviderCode));
        (ProviderOutcome Outcome, string? ExternalRef, string? Reason) answer;
        try
        {
            answer = gateway is null ? (ProviderOutcome.Unknown, null, "No gateway for the provider") : await gateway.Send(payment, payee, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = (ProviderOutcome.Unknown, null, ex.GetType().Name);
        }

        payment.Sent(answer.Outcome, answer.ExternalRef, answer.Reason, businessDate.Today, clock.UtcNow);
    }
}

internal sealed class InitiatePaymentHandler(PaymentStarter starter, IAuditTrail audit) : ICommandHandler<InitiatePayment, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(InitiatePayment command, CancellationToken cancellationToken)
    {
        var payment = await starter.Start(command, cancellationToken);
        if (payment.IsSuccess)
        {
            audit.Record(new AuditEntry("PAYMENT", "InitiatePayment", nameof(Payment), payment.Value.Id.ToString(),
                new Dictionary<string, string?> { ["purpose"] = command.Purpose, ["direction"] = command.Direction.ToString() }));
        }

        return payment.IsFailure ? payment.Error : PaymentView.From(payment.Value);
    }
}

internal sealed class MarkUnknownHandler(IPaymentRepository payments, IClock clock) : ICommandHandler<MarkUnknown, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(MarkUnknown command, CancellationToken cancellationToken)
    {
        if (await payments.Get(command.PaymentId, cancellationToken) is not { } payment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment was not found.");
        }

        var result = payment.MarkUnknown(clock.UtcNow);
        return result.IsFailure ? result.Error : PaymentView.From(payment);
    }
}

internal sealed class ResolveUnknownHandler(IPaymentRepository payments, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<ResolveUnknown, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(ResolveUnknown command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await payments.Get(command.PaymentId, cancellationToken) is not { } payment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment was not found.");
        }

        audit.Record(new AuditEntry("PAYMENT", "ResolveUnknown", nameof(Payment), payment.Id.ToString(),
            new Dictionary<string, string?> { ["succeeded"] = command.Succeeded.ToString(), ["evidence"] = command.Evidence }));
        var result = payment.ResolveUnknown(command.Succeeded, command.Evidence, businessDate.Today, clock.UtcNow);
        return result.IsFailure ? result.Error : PaymentView.From(payment);
    }
}

internal sealed class ReversePaymentHandler(IPaymentRepository payments, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<ReversePayment, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(ReversePayment command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await payments.Get(command.PaymentId, cancellationToken) is not { } payment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment was not found.");
        }

        audit.Record(new AuditEntry("PAYMENT", "ReversePayment", nameof(Payment), payment.Id.ToString(), new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var result = payment.ReversePayment(command.Reason, businessDate.Today, clock.UtcNow);
        return result.IsFailure ? result.Error : PaymentView.From(payment);
    }
}

/// <summary>Command AllocatePayment: Billing's allocation plan applied to a received payment (BIL-003).</summary>
internal sealed class AllocatePaymentHandler(IPaymentRepository payments, IAllocationPlanner planner, ITenantContext tenant, IBusinessDate businessDate, IClock clock)
    : ICommandHandler<AllocatePayment, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(AllocatePayment command, CancellationToken cancellationToken)
    {
        if (await payments.Get(command.PaymentId, cancellationToken) is not { } payment)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment was not found.");
        }

        var result = await Allocation.Allocate(payment, planner, tenant, clock, businessDate, cancellationToken);
        return result.IsFailure ? result.Error : PaymentView.From(payment);
    }
}

internal static class Allocation
{
    public const string Rule = "OLDEST_FIRST";

    public static async Task<Result> Allocate(Payment payment, IAllocationPlanner planner, ITenantContext tenant, IClock clock, IBusinessDate businessDate,
        CancellationToken cancellationToken)
    {
        if (payment.Direction != Direction.In || payment.Status != PaymentStatus.Succeeded)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Payment {payment.PaymentRef} is {payment.Status}; only a received payment that succeeded is allocated.");
        }

        var remaining = payment.Amount - payment.Allocated;
        if (remaining <= 0 || payment.BusinessRefId is not { } refId)
        {
            return Result.Success();
        }

        var invoice = payment.BusinessRefType == "INVOICE" ? refId : (Guid?)null;
        var account = payment.BusinessRefType == "BILLING_ACCOUNT" ? refId : (Guid?)null;
        if (invoice is null && account is null)
        {
            return Result.Success();
        }

        var plan = await planner.Plan(tenant.RequireTenantId(), account, invoice, remaining, payment.Currency, businessDate.Today, cancellationToken);
        return plan.Count == 0 ? Result.Success() : payment.AllocatePayment(plan.Select(p => (p.InvoiceId, p.Amount)).ToList(), Rule, clock.UtcNow);
    }
}

internal sealed class BuildBatchHandler(IPaymentRepository payments, PaymentScopes scopes, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<BuildBatch, BatchView>
{
    public async Task<Result<BatchView>> Handle(BuildBatch command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        // F103: a batch pays every waiting payout of its purpose, so it is built only by a caller covering them all.
        var waiting = await payments.AwaitingBatch(command.Purpose, command.Currency, cancellationToken);
        if ((await scopes.Of(waiting, cancellationToken)).Partly)
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "Some waiting payouts are for records outside your scope, so you cannot batch them.");
        }

        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), command.Purpose, command.Currency, waiting, user.Value, clock.UtcNow);
        if (batch.IsFailure)
        {
            return batch.Error;
        }

        payments.Add(batch.Value);
        audit.Record(new AuditEntry("PAYMENT", "BuildBatch", nameof(PayoutBatch), batch.Value.Id.ToString()));
        return BatchView.From(batch.Value);
    }
}

internal sealed class ReleaseBatchHandler(IPaymentRepository payments, PaymentScopes scopes, PaymentStarter starter, IExecutionContext execution, IClock clock,
    IAuditTrail audit) : ICommandHandler<ReleaseBatch, BatchView>
{
    public async Task<Result<BatchView>> Handle(ReleaseBatch command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await payments.Batch(command.BatchId, cancellationToken) is not { } batch)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.");
        }

        // F103: releasing pays the whole batch, so the caller covers every payment in it (a batch with none in scope is not found).
        var all = await payments.InBatch(batch.Id, cancellationToken);
        var (visible, partly) = await scopes.Of(all, cancellationToken);
        if (partly)
        {
            return visible.Count == 0 ? Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.")
                : Error.Forbidden(ErrorCodes.OutOfScope, "The batch also pays records outside your scope, so you cannot release it.");
        }

        audit.Record(new AuditEntry("PAYMENT", "ReleaseBatch", nameof(PayoutBatch), batch.Id.ToString()));
        var result = batch.ReleaseBatch(user.Value, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        foreach (var payment in all)
        {
            await starter.Send(payment, cancellationToken);
        }

        return BatchView.From(batch);
    }
}

/// <summary>Matches bank statement lines to payments (PAY-003): by payment or provider reference and amount within tolerance; anything else is an exception.</summary>
internal sealed class ImportStatementHandler(IPaymentRepository payments, IPaymentCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<ImportStatement, StatementResult>
{
    public async Task<Result<StatementResult>> Handle(ImportStatement command, CancellationToken cancellationToken)
    {
        var tolerance = await codes.MatchingTolerance(businessDate.Today, cancellationToken);
        var (matched, exceptions) = (0, 0);
        foreach (var line in command.Lines)
        {
            var statementLine = BankStatementLine.Import(Guid.CreateVersion7(), command.AccountRef, line.Date, line.Amount, line.Currency, line.Reference);
            payments.Add(statementLine);
            var payment = await payments.ByReference(line.Reference, cancellationToken);
            if (payment is not null && payment.Currency == line.Currency && Math.Abs(payment.Amount - Math.Abs(line.Amount)) <= tolerance)
            {
                statementLine.Matched(payment.Id);
                matched++;
            }
            else
            {
                statementLine.Unmatched();
                payments.Add(ReconciliationException.RaiseException(Guid.CreateVersion7(), ReconciliationException.UnmatchedLine, payment?.Id, statementLine.Id,
                    line.Amount, clock.UtcNow));
                exceptions++;
            }
        }

        return new StatementResult(matched, exceptions);
    }
}

internal sealed class AssignExceptionHandler(IPaymentRepository payments, IExecutionContext execution) : ICommandHandler<AssignException, ExceptionView>
{
    public async Task<Result<ExceptionView>> Handle(AssignException command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await payments.Exception(command.ExceptionId, cancellationToken) is not { } exception)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The reconciliation exception was not found.");
        }

        var result = exception.AssignException(user.Value);
        return result.IsFailure ? result.Error : ExceptionView.From(exception);
    }
}

internal sealed class ResolveExceptionHandler(IPaymentRepository payments, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<ResolveException, ExceptionView>
{
    public async Task<Result<ExceptionView>> Handle(ResolveException command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await payments.Exception(command.ExceptionId, cancellationToken) is not { } exception)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The reconciliation exception was not found.");
        }

        // An unknown outcome is resolved on the payment first (ResolveUnknown); the exception then records who closed it.
        if (exception.ExceptionType == ReconciliationException.UnknownOutcome && exception.PaymentId is { } paymentId
            && await payments.Get(paymentId, cancellationToken) is { Status: PaymentStatus.Unknown })
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Resolve the payment's unknown outcome first.");
        }

        audit.Record(new AuditEntry("PAYMENT", "ResolveException", nameof(ReconciliationException), exception.Id.ToString()));
        var result = exception.ResolveException(user.Value);
        return result.IsFailure ? result.Error : ExceptionView.From(exception);
    }
}

/// <summary>
/// A payment against a scoped business record (an invoice, a billing account or a refund) is shown only to a caller whose
/// scope covers all of that record (F103, OQ-081), so no other branch's or product's figures show through a shared account.
/// </summary>
public sealed class PaymentScopes(IEnumerable<IBusinessRecordScopes> sources, ITenantContext tenant, IExecutionContext execution)
{
    /// <summary>
    /// A payment is shown when its record is in the caller's scope; a commission payout, outside branch scope, only to a
    /// caller holding commission_payout.read.
    /// </summary>
    public async Task<bool> Covers(Payment payment, CancellationToken cancellationToken) =>
        payment.Purpose == PaymentPurposes.Commission
            ? execution.ActorType == ActorType.Service || execution.HasPermission(PaymentPermissions.CommissionPayoutRead)
            : await Covers(payment.BusinessRefType, payment.BusinessRefId, cancellationToken);

    /// <summary>The payments the caller may see, and whether that is only part of them.</summary>
    public async Task<(IReadOnlyList<Payment> Visible, bool Partly)> Of(IReadOnlyList<Payment> payments, CancellationToken cancellationToken)
    {
        var visible = new List<Payment>();
        var seen = new Dictionary<(string?, Guid?, bool), bool>();
        foreach (var payment in payments)
        {
            var key = (payment.BusinessRefType, payment.BusinessRefId, payment.Purpose == PaymentPurposes.Commission);
            if (!seen.TryGetValue(key, out var covered))
            {
                seen[key] = covered = await Covers(payment, cancellationToken);
            }

            if (covered)
            {
                visible.Add(payment);
            }
        }

        return (visible, visible.Count < payments.Count);
    }

    public async Task<bool> Covers(string? businessRefType, Guid? businessRefId, CancellationToken cancellationToken)
    {
        if (execution.CurrentScope.IsUnrestricted || businessRefType is null || businessRefId is not { } id
            || sources.FirstOrDefault(s => s.Covers(businessRefType)) is not { } source)
        {
            return true;
        }

        var scopes = await source.ScopesOf(tenant.RequireTenantId(), businessRefType, id, cancellationToken);
        return scopes is { Count: > 0 } && scopes.All(s => execution.CurrentScope.Allows(s.ProductCode, s.OrgNodeCode));
    }
}

/// <summary>
/// The Payments dashboard figure PAYMENTS_UNKNOWN (GAP-046): payments whose outcome is unknown and waits for a person's
/// decision (PAY-005), the count and amount per currency, and since when the oldest has waited; only those the caller may see.
/// </summary>
public sealed record PaymentsSummaryQuery : IQuery<DashboardSummary>;

/// <summary>The Payments dashboard figure codes (docs/specs/addenda/dashboard-figures.md).</summary>
public static class PaymentFigures
{
    public const string PaymentsUnknown = "PAYMENTS_UNKNOWN";
}

internal sealed class PaymentsSummaryHandler(IPaymentRepository payments, PaymentScopes scopes, IClock clock) : IQueryHandler<PaymentsSummaryQuery, DashboardSummary>
{
    public const string PaymentsUnknown = PaymentFigures.PaymentsUnknown;

    public async Task<Result<DashboardSummary>> Handle(PaymentsSummaryQuery query, CancellationToken cancellationToken)
    {
        var (visible, _) = await scopes.Of(await payments.Unknown(cancellationToken), cancellationToken);
        var amounts = visible.GroupBy(p => p.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new MoneyAmount(g.Key, g.Sum(p => p.Amount))).ToList();
        return new DashboardSummary(clock.UtcNow,
            [new DashboardFigure(PaymentsUnknown, Count: visible.Count, Amounts: amounts, At: visible.Count == 0 ? null : visible.Min(p => p.InitiatedAt))]);
    }
}

internal sealed class GetPaymentHandler(IPaymentRepository payments, PaymentScopes scopes) : IQueryHandler<GetPayment, PaymentView>
{
    public async Task<Result<PaymentView>> Handle(GetPayment query, CancellationToken cancellationToken) =>
        await payments.Get(query.PaymentId, cancellationToken) is { } p && await scopes.Covers(p, cancellationToken)
            ? PaymentView.From(p)
            : Error.NotFound(ErrorCodes.NotFound, "The payment was not found.");
}

internal sealed class ListPaymentsHandler(IPaymentRepository payments, PaymentScopes scopes)
    : IQueryHandler<ListPayments, IReadOnlyList<PaymentView>>, IQueryHandler<ListBatchPayments, IReadOnlyList<PaymentView>>
{
    public async Task<Result<IReadOnlyList<PaymentView>>> Handle(ListPayments query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PaymentView>>((await scopes.Of(await payments.ForBusinessRef(query.BusinessRefType, query.BusinessRefId, query.AfterId,
            query.Limit, cancellationToken), cancellationToken)).Visible.Select(PaymentView.From).ToList());

    /// <summary>The batch's payments in the caller's scope; a batch with none in scope is not found (F103).</summary>
    public async Task<Result<IReadOnlyList<PaymentView>>> Handle(ListBatchPayments query, CancellationToken cancellationToken)
    {
        if (await payments.Batch(query.BatchId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.");
        }

        var all = await payments.InBatch(query.BatchId, cancellationToken);
        var (visible, _) = await scopes.Of(all, cancellationToken);
        return visible.Count == 0 && all.Count > 0
            ? Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.")
            : Result.Success<IReadOnlyList<PaymentView>>(visible.Select(PaymentView.From).ToList());
    }
}

public sealed class ListPaymentsValidator : AbstractValidator<ListPayments>
{
    public ListPaymentsValidator()
    {
        RuleFor(q => q.BusinessRefType).NotEmpty().MaximumLength(30);
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
    }
}

/// <summary>A batch, with the count and total of the payments in the caller's scope; one with none in scope is not found (F103).</summary>
internal sealed class GetBatchHandler(IPaymentRepository payments, PaymentScopes scopes, IApprovalHistory approvals) : IQueryHandler<GetBatch, BatchView>
{
    public async Task<Result<BatchView>> Handle(GetBatch query, CancellationToken cancellationToken)
    {
        if (await payments.Batch(query.BatchId, cancellationToken) is not { } batch)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.");
        }

        var all = await payments.InBatch(batch.Id, cancellationToken);
        var (visible, partly) = await scopes.Of(all, cancellationToken);
        if (visible.Count == 0 && all.Count > 0)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payout batch was not found.");
        }

        return BatchView.Of(batch, visible, partly, await approvals.Latest(PaymentApprovals.PayoutBusinessRefType, batch.Id, cancellationToken));
    }
}

internal sealed class ListExceptionsHandler(IPaymentRepository payments) : IQueryHandler<ListExceptions, IReadOnlyList<ExceptionView>>
{
    public async Task<Result<IReadOnlyList<ExceptionView>>> Handle(ListExceptions query, CancellationToken cancellationToken) =>
        (await payments.Exceptions(query.Status, query.Limit, cancellationToken)).Select(ExceptionView.From).ToList();
}

internal sealed class PendingPaymentsHandler(IPaymentRepository payments) : IQueryHandler<PendingPayments, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(PendingPayments query, CancellationToken cancellationToken) =>
        Result.Success(await payments.PendingSince(query.Before, 500, cancellationToken));
}

/// <summary>
/// Payments' policies (domain-model.json: payments consumes): collect issued invoices whose method a provider supports;
/// pay approved refunds and released claim payments through payout batches; approve batches from Workflow; allocate
/// received payments; raise a reconciliation exception for every unknown outcome (PAY-005); settle a batch when all its
/// payments are final.
/// </summary>
/// <summary>Configuration a payout needs, named when its delivery is parked (ADR-022 decision 7, GAP-038).</summary>
public static class PaymentPrerequisites
{
    /// <summary>No payout method in force for the currency (the item).</summary>
    public const string PayoutMethodNotInForce = "PAYOUT_METHOD_NOT_IN_FORCE";

    /// <summary>No provider in the register supports the payout method (the item).</summary>
    public const string ProviderNotInForce = "PAYOUT_PROVIDER_NOT_IN_FORCE";
}

public sealed class PaymentPolicies(IPaymentRepository payments, PaymentStarter starter, IPaymentCodes codes, IAllocationPlanner planner, ITenantContext tenant,
    IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<RefundApproved>,
    IIntegrationEventHandler<ClaimPaymentInstructed>,
    IIntegrationEventHandler<Health.Contracts.ProviderPaymentRunReleased>,
    IIntegrationEventHandler<Distribution.Contracts.CommissionStatementApproved>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<PaymentReceived>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<Contracts.PaymentFailed>,
    IIntegrationEventHandler<Contracts.PaymentOutcomeUnknown>
{
    public async Task Handle(InvoiceIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // Only methods a provider collects (a debit order, say); otherwise the payer pays and the bank statement shows it.
        if (await codes.ProviderFor(issued.PaymentMethod, businessDate.Today, cancellationToken) is null)
        {
            return;
        }

        await starter.Start(new InitiatePayment($"INVOICE:{issued.InvoiceId:N}", Direction.In, PaymentPurposes.Premium, issued.PaymentMethod, issued.TotalAmount,
            issued.Currency, issued.PayerPartyId, "INVOICE", issued.InvoiceId), cancellationToken);
    }

    public Task Handle(RefundApproved approved, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Payout($"REFUND:{approved.RefundId:N}", PaymentPurposes.Refund, approved.Amount, approved.Currency, approved.PayerPartyId, BillingApprovals.RefundBusinessRefType,
            approved.RefundId, approved.BankAccountId, cancellationToken);

    public Task Handle(ClaimPaymentInstructed instructed, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Payout($"CLAIM_PAYMENT:{instructed.InstructionId:N}:{instructed.Attempt}", PaymentPurposes.ClaimPayout, instructed.Amount, instructed.Currency, instructed.PayeePartyId,
            ClaimsApprovals.InstructionBusinessRefType, instructed.InstructionId, instructed.BankAccountId, cancellationToken);

    /// <summary>A released provider payment run: each remittance is paid to the provider's verified account through a payout batch (HLT-009).</summary>
    public async Task Handle(Health.Contracts.ProviderPaymentRunReleased released, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var remittance in released.Remittances)
        {
            await Payout($"REMITTANCE:{remittance.RemittanceId:N}", PaymentPurposes.ClaimPayout, remittance.Amount, released.Currency, remittance.ProviderPartyId,
                Health.Contracts.HealthReferences.RemittanceRefType, remittance.RemittanceId, remittance.BankAccountId, cancellationToken);
        }
    }

    /// <summary>An approved commission statement is paid to the intermediary's verified account through a payout batch (DST-004).</summary>
    public Task Handle(Distribution.Contracts.CommissionStatementApproved approved, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Payout($"COMMISSION:{approved.StatementId:N}", PaymentPurposes.Commission, approved.NetAmount, approved.Currency, approved.PayeePartyId,
            Distribution.Contracts.DistributionApprovals.StatementBusinessRefType, approved.StatementId, approved.BankAccountId, cancellationToken);

    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType == PaymentApprovals.PayoutBusinessRefType && await payments.Batch(granted.BusinessRefId, cancellationToken) is { } batch)
        {
            batch.ApproveBatch(granted.CheckerId);
        }
    }

    /// <summary>
    /// The batch's approval ended without being granted, whether rejected, expired or withdrawn (D-1): the batch ends as
    /// Rejected and its unsent payments are released for the next batch. A batch already Rejected was handled by an earlier
    /// delivery; one that has moved on (approved, released) cannot take the ending, and the delivery parks (ADR-022, D-5).
    /// </summary>
    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != PaymentApprovals.PayoutBusinessRefType || await payments.Batch(rejected.BusinessRefId, cancellationToken) is not { } batch
            || batch.Status == BatchStatus.Rejected)
        {
            return;
        }

        var inBatch = await payments.InBatch(batch.Id, cancellationToken);
        var released = batch.RejectBatch(inBatch);
        if (released.IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }

        foreach (var payment in inBatch.Where(p => released.Value.Contains(p.Id)))
        {
            payment.LeaveBatch(batch.Id);
        }
    }

    public async Task Handle(PaymentReceived received, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await payments.Get(received.PaymentId, cancellationToken) is { } payment)
        {
            await Allocation.Allocate(payment, planner, tenant, clock, businessDate, cancellationToken);
        }
    }

    public Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken) => Settle(succeeded.PaymentId, cancellationToken);

    public Task Handle(Contracts.PaymentFailed failed, EventEnvelope envelope, CancellationToken cancellationToken) => Settle(failed.PaymentId, cancellationToken);

    public Task Handle(Contracts.PaymentOutcomeUnknown unknown, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        payments.Add(ReconciliationException.RaiseException(Guid.CreateVersion7(), ReconciliationException.UnknownOutcome, unknown.PaymentId, null, unknown.Amount,
            clock.UtcNow));
        return Task.CompletedTask;
    }

    private async Task Payout(string key, string purpose, decimal amount, string currency, Guid? payee, string businessRefType, Guid businessRefId, Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        // GAP-038, ADR-022 decision 7: a payout that cannot start is parked, visible and escalated, never skipped. It is
        // retried when a payout method or provider is published, and the payment is then created once (its idempotency key).
        if (await codes.PayoutMethod(currency, businessDate.Today, cancellationToken) is not { } method)
        {
            throw new PrerequisiteMissingException(PaymentPrerequisites.PayoutMethodNotInForce, currency);
        }

        var started = await starter.Start(new InitiatePayment(key, Direction.Out, purpose, method, amount, currency, payee, businessRefType, businessRefId, bankAccountId),
            cancellationToken);
        if (started.IsFailure)
        {
            throw started.Error.Code switch
            {
                PaymentErrorCodes.CodeNotInForce => new PrerequisiteMissingException(PaymentPrerequisites.PayoutMethodNotInForce, currency),
                PaymentErrorCodes.NoProvider => new PrerequisiteMissingException(PaymentPrerequisites.ProviderNotInForce, method),
                _ => new InvalidOperationException($"The payout could not start: {started.Error.Code}."),
            };
        }
    }

    private async Task Settle(Guid paymentId, CancellationToken cancellationToken)
    {
        if (await payments.Get(paymentId, cancellationToken) is { PayoutBatchId: { } batchId } && await payments.Batch(batchId, cancellationToken) is { } batch)
        {
            batch.Settle((await payments.InBatch(batchId, cancellationToken)).All(p => p.IsFinal));
        }
    }
}
