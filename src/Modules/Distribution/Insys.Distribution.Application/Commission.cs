/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.Distribution.Domain;
using Insys.Distribution.Domain.Commission;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Distribution.Application;

public interface IDistributionRepository
{
    /// <summary>B-009: what the intermediary still owes in the currency, oldest first.</summary>
    Task<IReadOnlyList<CommissionRecovery>> RecoveriesOwed(string intermediaryCode, string currency, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionRecovery>> RecoveriesOn(Guid statementId, CancellationToken cancellationToken);

    Task<bool> HasRecovery(Guid entitlementId, CancellationToken cancellationToken);

    /// <summary>B-009: every recovery, newest first; with owedOnly, only what's still owed.</summary>
    Task<IReadOnlyList<CommissionRecovery>> Recoveries(string? intermediaryCode, bool owedOnly, Guid? beforeId, int limit, CancellationToken cancellationToken);

    void Add(CommissionRecovery recovery);

    Task<CommissionEntitlement?> Entitlement(Guid id, CancellationToken cancellationToken);

    Task<CommissionEntitlement?> EntitlementFor(string sourceType, Guid sourceId, string intermediaryCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionEntitlement>> EntitlementsForSource(string sourceType, Guid sourceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionEntitlement>> EntitlementsForPolicy(string sourceDomain, Guid policyRef, CancellationToken cancellationToken);

    Task<bool> HasEntitlement(string sourceDomain, Guid policyRef, string intermediaryCode, CancellationToken cancellationToken);

    /// <summary>Payable entitlements not on a statement, and clawbacks to recover, for an intermediary in a currency.</summary>
    Task<(IReadOnlyList<CommissionEntitlement> Payable, IReadOnlyList<CommissionEntitlement> ToRecover)> Due(string intermediaryCode, string currency,
        CancellationToken cancellationToken);

    /// <summary>Entitlements on a statement, or whose clawback is recovered on it.</summary>
    Task<IReadOnlyList<CommissionEntitlement>> OnStatement(Guid statementId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionEntitlement>> List(string? intermediaryCode, Guid? policyRef, EntitlementStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken, Guid? statementId = null);

    /// <summary>
    /// BR-058: statements by intermediary, period and status, by ID after <paramref name="afterId"/>; with <paramref name="carriedOnOrBefore"/>, only
    /// carried-forward statements carried on or before the day with nothing awaiting approval (those old enough to invoice).
    /// </summary>
    Task<IReadOnlyList<CommissionStatement>> Statements(string? intermediaryCode, string? periodCode, StatementStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken, DateOnly? carriedOnOrBefore = null);

    /// <summary>The intermediary's carried-forward statements in the currency with something still to absorb and no action awaiting approval, oldest first.</summary>
    Task<IReadOnlyList<CommissionStatement>> CarriedForward(string intermediaryCode, string currency, CancellationToken cancellationToken);

    /// <summary>The carried-forward statements part of whose amount a later statement takes.</summary>
    Task<IReadOnlyList<CommissionStatement>> CarriedOn(Guid statementId, CancellationToken cancellationToken);

    Task<CommissionStatement?> Statement(Guid id, CancellationToken cancellationToken);

    Task<CommissionStatement?> Statement(string intermediaryCode, string periodCode, CancellationToken cancellationToken);

    void Add(CommissionEntitlement entitlement);

    void Add(CommissionStatement statement);

    /// <summary>B-010: asks Communications for a notice, in this transaction.</summary>
    void Request(Communications.Contracts.NotificationRequested notice, DateTimeOffset now);
}

/// <summary>An intermediary in the register, in force.</summary>
/// <param name="ContactPartyId">Who is told of a broker of record change (B-010), if not <paramref name="PartyId"/>.</param>
public sealed record IntermediaryInfo(string IntermediaryCode, string Status, Guid? PartyId, Guid? ContactPartyId = null);

/// <summary>The Distribution module's static data in force.</summary>
public interface IDistributionCodes
{
    /// <summary>B-009: RECOVER, unless the tenant's setting in force says KEEP.</summary>
    Task<string> PaidCommissionOnBrokerChange(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// After how many days a carried-forward statement's amount may be invoiced (Ade, 24 November 2026): the tenant's
    /// clawback_invoice_after_days on its commission settings (commission.cfg_broker_change_settings) in force, or 90.
    /// </summary>
    Task<int> ClawbackInvoiceAfterDays(DateOnly day, CancellationToken cancellationToken);

    Task<IntermediaryInfo?> Intermediary(string intermediaryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The plan the intermediary earns on the product (cfg_intermediary_product_permissions.plan_code, OQ-062).</summary>
    Task<string?> PlanCodeFor(string intermediaryCode, string productCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The plan version in force on the day (DST-002).</summary>
    Task<CommissionPlan?> Plan(string planCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A plan version, whatever its status now (the version an entitlement recorded, DST-003).</summary>
    Task<CommissionPlan?> PlanVersion(string planCode, int version, CancellationToken cancellationToken);
}

public sealed record ClawbackView(Guid Id, string Reason, decimal Amount, Guid? StatementId);

/// <param name="Rate">The plan's rate: a percentage of the basis amount when <paramref name="RateUnit"/> is PERCENT, an amount when AMOUNT (BR-059).</param>
/// <param name="RateBasis">The plan's basis: PREMIUM (a percentage of the invoice's net premium), FIXED (an amount per invoice) or POLICY_COUNT (an amount per policy).</param>
/// <param name="RateUnit">PERCENT for a PREMIUM basis, AMOUNT otherwise.</param>
public sealed record EntitlementView(Guid Id, string IntermediaryCode, string SourceType, Guid SourceId, string SourceDomain, Guid PolicyRef, string ProductCode,
    string PlanCode, int PlanVersion, decimal BasisAmount, decimal Rate, decimal Amount, string Currency, DateOnly DueDate, EntitlementStatus Status, Guid? StatementId,
    ClawbackView? Clawback, long RowVersion,
    [property: OneOf(CommissionBases.Premium, CommissionBases.Fixed, CommissionBases.PolicyCount)] string? RateBasis = null,
    [property: OneOf("PERCENT", "AMOUNT")] string? RateUnit = null)
{
    public static EntitlementView From(CommissionEntitlement e) => new(e.Id, e.IntermediaryCode, e.SourceType, e.SourceId, e.SourceDomain, e.PolicyRef, e.ProductCode,
        e.PlanCode, e.PlanVersion, e.BasisAmount, e.Rate, e.Amount, e.Currency, e.DueDate, e.Status, e.StatementId,
        e.Clawback is { } c ? new ClawbackView(c.Id, c.Reason, c.Amount, c.StatementId) : null, e.RowVersion,
        e.RateBasis is { Length: > 0 } basis ? basis : null,
        e.RateBasis is { Length: > 0 } unit ? unit == CommissionBases.Premium ? "PERCENT" : "AMOUNT" : null);
}

/// <param name="ApprovalId">The Workflow approval the statement was last submitted for (BR-058): the one that approved it once Approved.</param>
/// <param name="Reference">The statement's reference (intermediary/period, e.g. BRK-1/2026-10), typed to confirm invoicing its clawback.</param>
/// <param name="OpeningAmount">The opening line: what the statement takes of the intermediary's earlier carried-forward statements, already in its gross.</param>
/// <param name="CarriedOn">The business date a negative statement was carried forward.</param>
/// <param name="CarriedAmount">What a carried-forward statement carries: its negative net as a positive amount.</param>
/// <param name="CarrySettledAmount">What later statements, paid or closed, have absorbed of it.</param>
/// <param name="CarryOnStatementId">The later statement taking part of it while that statement waits for approval and payment.</param>
/// <param name="CarriedOwed">What a carried-forward statement still carries that no later statement takes: what an invoice would ask for.</param>
/// <param name="CarriedDays">How many days it has been carried, to the tenant's business date.</param>
/// <param name="FailedPaymentRef">DE-008: the reference of the failed payout, typed to confirm its retry.</param>
/// <param name="PayoutAttempt">1 for the first payout; each granted retry is the next attempt.</param>
/// <param name="PendingAction">PAYOUT_RETRY or COMMISSION_CLAWBACK_INVOICE while that action waits for a second person.</param>
public sealed record StatementView(Guid Id, string IntermediaryCode, string PeriodCode, string Currency, decimal GrossAmount, decimal TaxAmount, decimal NetAmount,
    StatementStatus Status, bool ApprovalPending, Guid? ApprovedBy, Guid? PayeePartyId, Guid? BankAccountId, Guid? PaymentRef, DateOnly? PaidOn, long RowVersion,
    LatestRejection? LatestRejection = null, Guid? ApprovalId = null, string Reference = "", decimal OpeningAmount = 0m, DateOnly? CarriedOn = null,
    decimal CarriedAmount = 0m, decimal CarrySettledAmount = 0m, Guid? CarryOnStatementId = null, decimal CarriedOwed = 0m, int? CarriedDays = null,
    DateOnly? InvoicedOn = null, decimal? InvoicedAmount = null, string? FailedPaymentRef = null, int PayoutAttempt = 1,
    [property: OneOf(StatementActions.PayoutRetry, StatementActions.ClawbackInvoice)] string? PendingAction = null, string? PendingReason = null)
{
    public static StatementView From(CommissionStatement s, DateOnly? today = null) => new(s.Id, s.IntermediaryCode, s.PeriodCode, s.Currency, s.GrossAmount, s.TaxAmount,
        s.NetAmount, s.Status, s.ApprovalPending, s.ApprovedBy, s.PayeePartyId, s.BankAccountId, s.PaymentRef, s.PaidOn, s.RowVersion, null, s.ApprovalId, s.Reference,
        s.OpeningAmount, s.CarriedOn, s.CarriedAmount, s.CarrySettledAmount, s.CarryOnStatementId, s.CarriedOwed,
        s.CarriedOn is { } on && today is { } day ? day.DayNumber - on.DayNumber : null, s.InvoicedOn, s.InvoicedAmount, s.FailedPaymentRef, s.PayoutAttempt,
        s.PendingAction, s.PendingReason);
}

/// <summary>Command PrepareStatement for an intermediary, a period (yyyy-MM) and a currency; preparing a draft again recalculates it.</summary>
public sealed record PrepareStatement(string IntermediaryCode, string PeriodCode, string Currency) : ICommand<StatementView>;

/// <summary>Command ClawBack, by a person (a policy cancelled or lapsed claws back through events).</summary>
public sealed record ClawBackCommission(Guid EntitlementId, string Reason) : ICommand<EntitlementView>;

public sealed record GetEntitlement(Guid EntitlementId) : IQuery<EntitlementView>;

/// <param name="StatementId">A statement's entitlements: those on it, and those whose clawback it recovers (BR-059).</param>
public sealed record ListEntitlements(string? IntermediaryCode, Guid? PolicyRef, EntitlementStatus? Status, Guid? AfterId, int Limit, Guid? StatementId = null)
    : IQuery<IReadOnlyList<EntitlementView>>;

/// <summary>
/// BR-058: commission statements by intermediary, period and status, oldest first. <paramref name="InvoiceDue"/>: only carried-forward
/// statements carried for at least the tenant's days (90 unless set), with nothing awaiting approval: those whose clawback may be invoiced.
/// </summary>
public sealed record ListStatements(string? IntermediaryCode, string? PeriodCode, StatementStatus? Status, Guid? AfterId, int Limit, bool InvoiceDue = false)
    : IQuery<IReadOnlyList<StatementView>>;

/// <summary>Command RetryStatementPayout (DE-008): the failed payout's reference, typed by the requester; a second person approves the retry (PAYOUT_RETRY).</summary>
public sealed record RetryStatementPayout(Guid StatementId, string PaymentRef) : ICommand<StatementView>;

/// <summary>
/// Command InvoiceStatementClawback (Ade, 24 November 2026): the statement's reference, typed by the requester, and why; a second person
/// approves invoicing the intermediary for what the statement still carries (COMMISSION_CLAWBACK_INVOICE).
/// </summary>
public sealed record InvoiceStatementClawback(Guid StatementId, string Reference, string Reason) : ICommand<StatementView>;

public sealed record GetStatement(Guid StatementId) : IQuery<StatementView>;

public sealed record IntermediaryCheckView(string IntermediaryCode, string ProductCode, bool Permitted, string? Code, string? Message);

/// <summary>May the intermediary sell the product today (DST-001)?</summary>
public sealed record CheckIntermediary(string IntermediaryCode, string ProductCode) : IQuery<IntermediaryCheckView>;

public sealed class PrepareStatementValidator : AbstractValidator<PrepareStatement>
{
    public PrepareStatementValidator()
    {
        RuleFor(c => c.IntermediaryCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.PeriodCode).Must(CommissionStatement.IsPeriod).WithMessage("The period is a month, yyyy-MM.");
        RuleFor(c => c.Currency).Length(3);
    }
}

public sealed class ClawBackCommissionValidator : AbstractValidator<ClawBackCommission>
{
    public ClawBackCommissionValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(20);
}

public sealed class RetryStatementPayoutValidator : AbstractValidator<RetryStatementPayout>
{
    public RetryStatementPayoutValidator() => RuleFor(c => c.PaymentRef).NotEmpty().MaximumLength(25);
}

public sealed class InvoiceStatementClawbackValidator : AbstractValidator<InvoiceStatementClawback>
{
    public InvoiceStatementClawbackValidator()
    {
        RuleFor(c => c.Reference).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>
/// DE-008: asks a second person to approve paying a statement's failed payout again (PAYOUT_RETRY, amount the net). The
/// failed payment's reference must be typed; once granted, Payments makes the next attempt's payout, paid in a released batch.
/// </summary>
internal sealed class RetryStatementPayoutHandler(IDistributionRepository repository, IExecutionContext execution, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) : ICommandHandler<RetryStatementPayout, StatementView>
{
    public async Task<Result<StatementView>> Handle(RetryStatementPayout command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await repository.Statement(command.StatementId, cancellationToken) is not { } statement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The commission statement was not found.");
        }

        var result = statement.RequestPayoutRetry(command.PaymentRef, user.Value, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("COMMISSION", "RetryStatementPayout", nameof(CommissionStatement), statement.Id.ToString(),
            new Dictionary<string, string?> { ["paymentRef"] = statement.FailedPaymentRef, ["attempt"] = (statement.PayoutAttempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return StatementView.From(statement, businessDate.Today);
    }
}

/// <summary>
/// Asks a second person to approve invoicing the intermediary for what a carried-forward statement still carries
/// (COMMISSION_CLAWBACK_INVOICE, Ade, 24 November 2026): only once the intermediary is terminated in the register, or the
/// amount has been carried for the tenant's days (90 unless set). The statement's reference must be typed.
/// </summary>
internal sealed class InvoiceStatementClawbackHandler(IDistributionRepository repository, IDistributionCodes codes, IExecutionContext execution, IBusinessDate businessDate,
    IClock clock, IAuditTrail audit) : ICommandHandler<InvoiceStatementClawback, StatementView>
{
    public const string Terminated = "TERMINATED";

    public async Task<Result<StatementView>> Handle(InvoiceStatementClawback command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await repository.Statement(command.StatementId, cancellationToken) is not { } statement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The commission statement was not found.");
        }

        var today = businessDate.Today;
        var terminated = (await codes.Intermediary(statement.IntermediaryCode, today, cancellationToken))?.Status == Terminated;
        var result = statement.RequestClawbackInvoice(command.Reference, command.Reason, terminated, await codes.ClawbackInvoiceAfterDays(today, cancellationToken), today,
            user.Value, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("COMMISSION", "InvoiceStatementClawback", nameof(CommissionStatement), statement.Id.ToString(),
            new Dictionary<string, string?> { ["reference"] = statement.Reference, ["reason"] = command.Reason, ["intermediaryTerminated"] = terminated ? "true" : "false" }));
        return StatementView.From(statement, today);
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>
/// Command PrepareStatement (DST-004): the intermediary's payable entitlements and clawbacks to recover, to its
/// party's verified bank account in the currency (OQ-062 item 7, ADR-013), for a second person's approval in Workflow.
/// The statement and the entitlements it takes change in one transaction, so no entitlement is on two statements.
/// </summary>
internal sealed class PrepareStatementHandler(IDistributionRepository repository, IDistributionCodes codes, IBankAccounts bankAccounts, ITenantContext tenant,
    IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<PrepareStatement, StatementView>
{
    public async Task<Result<StatementView>> Handle(PrepareStatement command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await codes.Intermediary(command.IntermediaryCode, businessDate.Today, cancellationToken) is not { PartyId: { } partyId }
            || await bankAccounts.Current(tenant.RequireTenantId(), partyId, command.Currency, cancellationToken) is not { IsPayable: true } account)
        {
            return Error.Validation(DistributionErrorCodes.IntermediaryNotPayable,
                $"Intermediary {command.IntermediaryCode} has no party with a verified bank account in {command.Currency}.");
        }

        var statement = await repository.Statement(command.IntermediaryCode, command.PeriodCode, cancellationToken);
        if (statement is not null && statement.Currency != command.Currency)
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, $"The {command.PeriodCode} statement of {command.IntermediaryCode} is in {statement.Currency}.");
        }

        if (statement is null)
        {
            statement = CommissionStatement.Open(Guid.CreateVersion7(), command.IntermediaryCode, command.PeriodCode, command.Currency);
            repository.Add(statement);
        }

        foreach (var recovery in await repository.RecoveriesOn(statement.Id, cancellationToken))
        {
            recovery.Release();
        }

        foreach (var carried in await repository.CarriedOn(statement.Id, cancellationToken))
        {
            carried.ReleaseCarry();
        }

        var current = await repository.OnStatement(statement.Id, cancellationToken);
        var (payable, toRecover) = await repository.Due(command.IntermediaryCode, command.Currency, cancellationToken);
        audit.Record(new AuditEntry("COMMISSION", "PrepareStatement", nameof(CommissionStatement), statement.Id.ToString(),
            new Dictionary<string, string?> { ["intermediary"] = command.IntermediaryCode, ["period"] = command.PeriodCode }));

        // Lines released from this draft count again when still due.
        var again = current.Where(e => e.Status == EntitlementStatus.Payable && e.StatementId == statement.Id).ToList();
        var recoverAgain = current.Where(e => e.Clawback?.StatementId == statement.Id).ToList();
        var prepared = statement.PrepareStatement(payable.Concat(again).DistinctBy(e => e.Id).ToList(), toRecover.Concat(recoverAgain).DistinctBy(e => e.Id).ToList(),
            current, new StatementPayee(partyId, account.Id, account.AccountNumberHash), user.Value, clock.UtcNow, await repository.RecoveriesOwed(command.IntermediaryCode, command.Currency, cancellationToken),
            (await repository.CarriedForward(command.IntermediaryCode, command.Currency, cancellationToken)).Where(c => c.Id != statement.Id).ToList());
        return prepared.IsFailure ? prepared.Error : StatementView.From(statement, businessDate.Today);
    }
}

public static class BrokerChangePaidCommission
{
    public const string Recover = "RECOVER";
    public const string Keep = "KEEP";
}

/// <summary>B-009: a broker's debt from a broker of record change, for finance.</summary>
public sealed record CommissionRecoveryView(Guid Id, Guid EntitlementId, string IntermediaryCode, Guid PolicyRef, string Currency, decimal Amount, decimal RecoveredAmount, decimal OnStatementAmount, decimal Owed, Guid? StatementId)
{
    public static CommissionRecoveryView From(CommissionRecovery r) => new(r.Id, r.EntitlementId, r.IntermediaryCode, r.PolicyRef, r.Currency, r.Amount, r.RecoveredAmount, r.OnStatementAmount, r.Owed, r.StatementId);
}

public sealed record ListCommissionRecoveries(string? IntermediaryCode, bool OwedOnly, Guid? BeforeId, int Limit) : IQuery<IReadOnlyList<CommissionRecoveryView>>;

internal sealed class ListCommissionRecoveriesHandler(IDistributionRepository repository) : IQueryHandler<ListCommissionRecoveries, IReadOnlyList<CommissionRecoveryView>>
{
    public async Task<Result<IReadOnlyList<CommissionRecoveryView>>> Handle(ListCommissionRecoveries query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<CommissionRecoveryView>>((await repository.Recoveries(query.IntermediaryCode, query.OwedOnly, query.BeforeId, query.Limit, cancellationToken)).Select(CommissionRecoveryView.From).ToList());
}

internal sealed class ClawBackCommissionHandler(IDistributionRepository repository, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<ClawBackCommission, EntitlementView>
{
    public async Task<Result<EntitlementView>> Handle(ClawBackCommission command, CancellationToken cancellationToken)
    {
        if (await repository.Entitlement(command.EntitlementId, cancellationToken) is not { } entitlement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The commission entitlement was not found.");
        }

        audit.Record(new AuditEntry("COMMISSION", "ClawBack", nameof(CommissionEntitlement), entitlement.Id.ToString(),
            new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var result = entitlement.ClawBack(command.Reason, businessDate.Today, clock.UtcNow);
        return result.IsFailure ? result.Error : EntitlementView.From(entitlement);
    }
}

internal sealed class DistributionQueries(IDistributionRepository repository, IDistributionCodes codes, IIntermediaries intermediaries, ITenantContext tenant,
    IBusinessDate businessDate, IApprovalHistory approvals) :
    IQueryHandler<GetEntitlement, EntitlementView>,
    IQueryHandler<ListEntitlements, IReadOnlyList<EntitlementView>>,
    IQueryHandler<ListStatements, IReadOnlyList<StatementView>>,
    IQueryHandler<GetStatement, StatementView>,
    IQueryHandler<CheckIntermediary, IntermediaryCheckView>
{
    public async Task<Result<EntitlementView>> Handle(GetEntitlement query, CancellationToken cancellationToken) =>
        await repository.Entitlement(query.EntitlementId, cancellationToken) is { } e ? EntitlementView.From(e) : Error.NotFound(ErrorCodes.NotFound, "The commission entitlement was not found.");

    public async Task<Result<IReadOnlyList<EntitlementView>>> Handle(ListEntitlements query, CancellationToken cancellationToken) =>
        (await repository.List(query.IntermediaryCode, query.PolicyRef, query.Status, query.AfterId, Math.Clamp(query.Limit, 1, 200), cancellationToken, query.StatementId))
        .Select(EntitlementView.From).ToList();

    public async Task<Result<IReadOnlyList<StatementView>>> Handle(ListStatements query, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        DateOnly? carriedBy = query.InvoiceDue ? today.AddDays(-await codes.ClawbackInvoiceAfterDays(today, cancellationToken)) : null;
        return (await repository.Statements(query.IntermediaryCode, query.PeriodCode, query.Status, query.AfterId, query.Limit, cancellationToken, carriedBy))
            .Select(s => StatementView.From(s, today)).ToList();
    }

    /// <summary>The statement, with how its latest approval ended without being granted, if one did (OQ-113).</summary>
    public async Task<Result<StatementView>> Handle(GetStatement query, CancellationToken cancellationToken) =>
        await repository.Statement(query.StatementId, cancellationToken) is { } s
            ? StatementView.From(s, businessDate.Today) with { LatestRejection = await approvals.Latest(DistributionApprovals.StatementBusinessRefType, s.Id, cancellationToken) }
            : Error.NotFound(ErrorCodes.NotFound, "The commission statement was not found.");

    public async Task<Result<IntermediaryCheckView>> Handle(CheckIntermediary query, CancellationToken cancellationToken)
    {
        var refusal = await intermediaries.CanSell(tenant.RequireTenantId(), query.IntermediaryCode, query.ProductCode, IntermediaryTransactions.NewBusiness, businessDate.Today,
            cancellationToken);
        return new IntermediaryCheckView(query.IntermediaryCode, query.ProductCode, refusal is null, refusal?.Code, refusal?.Message);
    }
}

/// <summary>
/// Commission's policies (domain-model.json: commission consumes; process "General quote to cash": AccrueCommission;
/// "Missed premium to lapse": ClawBack). InvoiceIssued accrues at the plan version in force on the issue date; a fully
/// paid invoice (InvoiceSettled) makes its commission payable (OQ-062 item 4: not each partial PaymentAllocated); a
/// cancelled or lapsed policy claws back by the plan's rule; Workflow's decision approves a statement; the payout's
/// success pays it.
/// </summary>
public sealed class CommissionPolicies(IDistributionRepository repository, IDistributionCodes codes, IEnumerable<IPolicyDistribution> policies, ITenantContext tenant,
    IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<InvoiceSettled>,
    IIntegrationEventHandler<GeneralPolicyCancelled>,
    IIntegrationEventHandler<LifePolicyLapsed>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentFailed>
{
    public async Task Handle(InvoiceIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (issued.NetAmount <= 0 || policies.FirstOrDefault(p => p.SourceDomain == issued.SourceDomain) is not { } domain
            || await domain.For(tenant.RequireTenantId(), issued.PolicyRef, cancellationToken) is not { IntermediaryCode: { Length: > 0 } intermediary } policy
            || await repository.EntitlementFor(CommissionEntitlement.InvoiceSource, issued.InvoiceId, intermediary, cancellationToken) is not null)
        {
            return;
        }

        // Commission is never skipped for want of configuration (Ade, 18 November 2026): a permission naming no plan parks, and a broker who earns none
        // has a plan at rate 0.
        var planCode = await codes.PlanCodeFor(intermediary, policy.ProductCode, issued.IssueDate, cancellationToken)
                       ?? throw new PrerequisiteMissingException(PrerequisiteCodes.CommissionPlanNotAssigned, $"{intermediary}:{policy.ProductCode}");

        // A plan named but not in force is missing configuration (ADR-022): the delivery parks, and is retried when the plan is published.
        var plan = await codes.Plan(planCode, issued.IssueDate, cancellationToken)
                   ?? throw new PrerequisiteMissingException(PrerequisiteCodes.CommissionPlanNotInForce, planCode);
        var first = !await repository.HasEntitlement(issued.SourceDomain, issued.PolicyRef, intermediary, cancellationToken);
        var source = new CommissionSource(issued.SourceDomain, issued.InvoiceId, issued.PolicyRef, policy.ProductCode, issued.NetAmount, issued.Currency, issued.IssueDate,
            issued.DueDate);
        var accrued = CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), intermediary, source, plan, first, clock.UtcNow);
        if (accrued.IsSuccess)
        {
            repository.Add(accrued.Value);
        }
        else if (accrued.Error.Code != DistributionErrorCodes.NothingToAccrue)
        {
            throw new InvalidOperationException($"Commission on invoice {issued.InvoiceNumber} was not accrued: {accrued.Error.Message}");
        }
    }

    public async Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var entitlement in await repository.EntitlementsForSource(CommissionEntitlement.InvoiceSource, settled.InvoiceId, cancellationToken))
        {
            if (entitlement.Status == EntitlementStatus.Accrued)
            {
                entitlement.MarkPayable();
            }
        }
    }

    public Task Handle(GeneralPolicyCancelled cancelled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        ClawBack(GeneralRatingSubjects.SourceDomain, cancelled.PolicyId, cancelled.EffectiveDate, "POLICY_CANCELLED", cancellationToken);

    public Task Handle(LifePolicyLapsed lapsed, EventEnvelope envelope, CancellationToken cancellationToken) =>
        ClawBack(LifeSubjects.SourceDomain, lapsed.PolicyId, lapsed.LapseDate, "POLICY_LAPSED", cancellationToken);

    /// <summary>
    /// The statement's approval was granted: it is approved and its net amount is paid to the intermediary. One already
    /// approved by this approval is a redelivery; one that will not take it (it changed after it was submitted: another
    /// subject, no longer awaiting approval) parks the delivery (APPROVAL_NOT_APPLIED, ADR-022, D-5), never only logged.
    /// </summary>
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != DistributionApprovals.StatementBusinessRefType || await repository.Statement(granted.BusinessRefId, cancellationToken) is not { } statement)
        {
            return;
        }

        if (granted.ApprovalType != DistributionApprovals.StatementApprovalType)
        {
            await DecideAction(statement, true, granted.ApprovalType, granted.ApprovalId, granted.SubjectHash, PrerequisiteCodes.ApprovalNotApplied);
            return;
        }

        if (statement.GrantedBy(granted.ApprovalId))
        {
            return;
        }

        if (statement.ApproveStatement(true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, clock.UtcNow,
                await repository.OnStatement(statement.Id, cancellationToken), businessDate.Today).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
        }

        // B-009 and the carry-forward: with nothing to pay (closed nil, or carried forward), no payout will settle it, so what it took is absorbed now.
        if (statement.NetAmount <= 0)
        {
            await SettleTaken(statement.Id, cancellationToken);
        }
    }

    /// <summary>
    /// The statement's approval ended without being granted, whether rejected, expired or withdrawn: it returns to draft and
    /// its entitlements and clawback recoveries are released for the next prepared statement. A statement that will not
    /// take the ending (another subject, not awaiting approval) parks the delivery (ADR-022, D-5); one already returned to
    /// draft by this approval is a redelivery.
    /// </summary>
    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != DistributionApprovals.StatementBusinessRefType || await repository.Statement(rejected.BusinessRefId, cancellationToken) is not { } statement)
        {
            return;
        }

        if (rejected.ApprovalType != DistributionApprovals.StatementApprovalType)
        {
            await DecideAction(statement, false, rejected.ApprovalType, rejected.ApprovalId, rejected.SubjectHash, PrerequisiteCodes.ApprovalEndingNotApplied);
            return;
        }

        if (statement.EndedBy(rejected.ApprovalId))
        {
            return;
        }

        var onStatement = await repository.OnStatement(statement.Id, cancellationToken);
        if (statement.ApproveStatement(false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, clock.UtcNow, onStatement).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }

        foreach (var recovery in await repository.RecoveriesOn(statement.Id, cancellationToken))
        {
            recovery.Release();
        }

        foreach (var carried in await repository.CarriedOn(statement.Id, cancellationToken))
        {
            carried.ReleaseCarry();
        }
    }

    /// <summary>"Payout settled": the statement and its payable entitlements are paid, and what it took of earlier debts is absorbed.</summary>
    public async Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (succeeded.BusinessRefType == DistributionApprovals.StatementBusinessRefType && succeeded.BusinessRefId is { } id
            && await repository.Statement(id, cancellationToken) is { Status: StatementStatus.Approved } statement)
        {
            statement.PayoutSettled(succeeded.PaymentId, succeeded.ValueDate, await repository.OnStatement(statement.Id, cancellationToken));
            await SettleTaken(statement.Id, cancellationToken);
        }
    }

    /// <summary>
    /// DE-008: the statement's payout failed. It is PaymentFailed with the failed payment's reference, until a second person
    /// grants a retry; its entitlements and what it took of earlier debts stay on it. A failure of a statement that is not
    /// waiting for its payout (already failed with this reference, or paid) changes nothing.
    /// </summary>
    public async Task Handle(PaymentFailed failed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (failed.BusinessRefType == DistributionApprovals.StatementBusinessRefType && failed.BusinessRefId is { } id && failed.Purpose == PaymentPurposes.Commission
            && await repository.Statement(id, cancellationToken) is { Status: StatementStatus.Approved or StatementStatus.PaymentFailed } statement)
        {
            statement.PayoutFailed(failed.PaymentRef);
        }
    }

    /// <summary>
    /// Workflow's decision of a statement's action (PAYOUT_RETRY, COMMISSION_CLAWBACK_INVOICE). One already decided by this
    /// approval is a redelivery; one the statement will not take (no such action waiting, or another subject) parks (D-5).
    /// </summary>
    private Task DecideAction(CommissionStatement statement, bool approved, string approvalType, Guid approvalId, string? subjectHash, string parkCode)
    {
        if (statement.DecideAction(approved, approvalType, approvalId, subjectHash, businessDate.Today, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(parkCode, $"{approvalType}:{approvalId}");
        }

        return Task.CompletedTask;
    }

    /// <summary>What a decided statement took of the intermediary's earlier debts (B-009 recoveries, carried-forward statements) is absorbed.</summary>
    private async Task SettleTaken(Guid statementId, CancellationToken cancellationToken)
    {
        foreach (var recovery in await repository.RecoveriesOn(statementId, cancellationToken))
        {
            recovery.Settle();
        }

        foreach (var carried in await repository.CarriedOn(statementId, cancellationToken))
        {
            carried.SettleCarry();
        }
    }

    /// <summary>
    /// The plan's clawback rule (OQ-062 item 5): UNEARNED (the default) claws back each entitlement on the policy whose
    /// invoice falls due on or after the date; NONE claws back nothing.
    /// </summary>
    private async Task ClawBack(string sourceDomain, Guid policyRef, DateOnly from, string reason, CancellationToken cancellationToken)
    {
        foreach (var entitlement in await repository.EntitlementsForPolicy(sourceDomain, policyRef, cancellationToken))
        {
            if (entitlement.Status is not (EntitlementStatus.Accrued or EntitlementStatus.Payable) || entitlement.DueDate < from)
            {
                continue;
            }

            var plan = await codes.PlanVersion(entitlement.PlanCode, entitlement.PlanVersion, cancellationToken);
            if ((plan?.ClawbackRule ?? ClawbackRules.Unearned) == ClawbackRules.None)
            {
                continue;
            }

            entitlement.ClawBack(reason, businessDate.Today, clock.UtcNow);
        }
    }
}

/// <summary>
/// B-001 (OQ-135): a broker of record change splits commission pro rata at the effective date. On each of the outgoing broker's
/// invoices whose cover runs past the date (an instalment's cover is its frequency's months from its due date, to the period
/// end), the outgoing broker keeps the days before the date and the incoming broker accrues the days from it, on its own plan.
/// Commission already paid is not recovered (a paid entitlement has no clawback); it is logged for a person.
/// </summary>
public sealed class BrokerOfRecordCommission(IDistributionRepository repository, IDistributionCodes codes, IPolicyArrears billing, ITenantContext tenant, IClock clock,
    ILogger<BrokerOfRecordCommission> logger) : IIntegrationEventHandler<GeneralPolicyIntermediaryChanged>
{
    public const string Reason = "BROKER_CHANGED";

    public async Task Handle(GeneralPolicyIntermediaryChanged changed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (changed.PreviousIntermediaryCode is not { Length: > 0 } outgoing)
        {
            return; // direct business: nothing earned to split
        }

        const string domain = GeneralRatingSubjects.SourceDomain;
        // Missing configuration parks the split (ADR-022), never a guessed period or a skipped plan: the frequency's periods, and the incoming broker's plan.
        var periods = changed.BillingFrequency is { } frequency
            ? await billing.PeriodsPerYear(tenant.RequireTenantId(), frequency, changed.EffectiveDate, cancellationToken)
              ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, frequency)
            : 1;
        var planCode = await codes.PlanCodeFor(changed.IntermediaryCode, changed.ProductCode, changed.EffectiveDate, cancellationToken)
                       ?? throw new PrerequisiteMissingException(PrerequisiteCodes.CommissionPlanNotAssigned, $"{changed.IntermediaryCode}:{changed.ProductCode}");
        var plan = await codes.Plan(planCode, changed.EffectiveDate, cancellationToken)
                   ?? throw new PrerequisiteMissingException(PrerequisiteCodes.CommissionPlanNotInForce, planCode);

        var first = !await repository.HasEntitlement(domain, changed.PolicyId, changed.IntermediaryCode, cancellationToken);
        foreach (var entitlement in await repository.EntitlementsForPolicy(domain, changed.PolicyId, cancellationToken))
        {
            if (entitlement.IntermediaryCode != outgoing || entitlement.SourceType != CommissionEntitlement.InvoiceSource
                || entitlement.Status == EntitlementStatus.ClawedBack)
            {
                continue;
            }

            var coverEnd = Min(entitlement.DueDate.AddMonths(12 / Math.Max(periods, 1)).AddDays(-1), changed.PeriodEnd);
            if (coverEnd < changed.EffectiveDate)
            {
                continue;
            }

            var from = changed.EffectiveDate > entitlement.DueDate ? changed.EffectiveDate : entitlement.DueDate;
            var share = Math.Round((decimal)(coverEnd.DayNumber - from.DayNumber + 1) / (coverEnd.DayNumber - entitlement.DueDate.DayNumber + 1), 6);
            var kept = 1m - share;
            var settled = entitlement.Status is EntitlementStatus.Payable or EntitlementStatus.Paid;
            var basis = entitlement.BasisAmount; // the invoice's net premium, before the outgoing broker's share is cut
            var recovering = false;
            if (entitlement.Status == EntitlementStatus.Paid)
            {
                // B-009: by the tenant's setting, the unearned days are recovered from the outgoing broker and go to the incoming one, or stay where they are.
                recovering = await codes.PaidCommissionOnBrokerChange(changed.EffectiveDate, cancellationToken) == BrokerChangePaidCommission.Recover;
                var owed = Math.Round(entitlement.Amount * share, 2, MidpointRounding.AwayFromZero);
                if (recovering && owed > 0 && !await repository.HasRecovery(entitlement.Id, cancellationToken))
                {
                    repository.Add(CommissionRecovery.Raised(Guid.CreateVersion7(), entitlement, owed, Reason, changed.EffectiveDate, clock.UtcNow));
                }
                else if (!recovering)
                {
                    logger.LogWarning("Commission {Entitlement} of {Intermediary} on policy {Policy} is paid: its share after the broker change ({Share:P2}) stays with it, by the tenant's setting",
                        entitlement.Id, outgoing, changed.PolicyNumber, share);
                }
            }
            else if (kept <= 0)
            {
                entitlement.ClawBack(Reason, changed.EffectiveDate, clock.UtcNow);
            }
            else if (entitlement.StatementId is null)
            {
                entitlement.Share(kept, Reason, changed.EffectiveDate, clock.UtcNow);
            }
            else
            {
                // On a statement: clawed back (recovered on the next one), and the share the outgoing broker keeps accrued again.
                entitlement.ClawBack(Reason, changed.EffectiveDate, clock.UtcNow);
                if (await codes.PlanVersion(entitlement.PlanCode, entitlement.PlanVersion, cancellationToken) is { } outgoingPlan
                    && await repository.EntitlementFor(CommissionEntitlement.BrokerShareSource, entitlement.SourceId, outgoing, cancellationToken) is null)
                {
                    Accrue(outgoing, outgoingPlan, entitlement, basis * kept, false, settled, CommissionEntitlement.BrokerShareSource);
                }
            }

            if (plan is not null && (entitlement.Status != EntitlementStatus.Paid || recovering)
                && await repository.EntitlementFor(CommissionEntitlement.InvoiceSource, entitlement.SourceId, changed.IntermediaryCode, cancellationToken) is null)
            {
                Accrue(changed.IntermediaryCode, plan, entitlement, basis * share, first, settled, CommissionEntitlement.InvoiceSource);
                first = false;
            }
        }
    }

    private void Accrue(string intermediary, CommissionPlan plan, CommissionEntitlement from, decimal netAmount, bool first, bool payable, string sourceType)
    {
        var source = new CommissionSource(from.SourceDomain, from.SourceId, from.PolicyRef, from.ProductCode, Math.Round(netAmount, 2, MidpointRounding.AwayFromZero),
            from.Currency, from.DueDate, from.DueDate);
        var accrued = CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), intermediary, source, plan, first, clock.UtcNow, sourceType);
        if (accrued.IsFailure)
        {
            return; // nothing to accrue (a zero share)
        }

        if (payable)
        {
            accrued.Value.MarkPayable();
        }

        repository.Add(accrued.Value);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}

/// <summary>
/// B-010: once a broker of record change is approved, the incoming and the outgoing broker's contact parties are told, each
/// with its role. An intermediary with no contact party is told through its own party; with neither, it is logged for a person.
/// </summary>
public sealed class BrokerChangeNotices(IDistributionRepository repository, IDistributionCodes codes, IBusinessDate businessDate, IClock clock,
    ILogger<BrokerChangeNotices> logger) : IIntegrationEventHandler<GeneralBrokerChangeApproved>
{
    public async Task Handle(GeneralBrokerChangeApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await Tell(approved, approved.IncomingIntermediaryCode, "INCOMING", cancellationToken);
        if (approved.OutgoingIntermediaryCode is { Length: > 0 } outgoing && outgoing != approved.IncomingIntermediaryCode)
        {
            await Tell(approved, outgoing, "OUTGOING", cancellationToken);
        }
    }

    private async Task Tell(GeneralBrokerChangeApproved approved, string intermediaryCode, string role, CancellationToken cancellationToken)
    {
        var intermediary = await codes.Intermediary(intermediaryCode, businessDate.Today, cancellationToken);
        if ((intermediary?.ContactPartyId ?? intermediary?.PartyId) is not { } recipient)
        {
            logger.LogWarning("Intermediary {Intermediary} has no contact party: it is not told of the broker change on policy {Policy}", intermediaryCode, approved.PolicyNumber);
            return;
        }

        repository.Request(new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.BrokerChangeNotice, recipient, "GENERAL_POLICY",
            approved.PolicyId, new Dictionary<string, string>
            {
                ["policy_number"] = approved.PolicyNumber, ["product_code"] = approved.ProductCode,
                ["effective_date"] = approved.EffectiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["incoming_intermediary"] = approved.IncomingIntermediaryCode, ["outgoing_intermediary"] = approved.OutgoingIntermediaryCode ?? "",
                ["broker_role"] = role,
            }, approved.ProductCode), clock.UtcNow);
    }
}
