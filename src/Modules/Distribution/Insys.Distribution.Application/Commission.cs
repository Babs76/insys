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
        CancellationToken cancellationToken);

    Task<CommissionStatement?> Statement(Guid id, CancellationToken cancellationToken);

    Task<CommissionStatement?> Statement(string intermediaryCode, string periodCode, CancellationToken cancellationToken);

    void Add(CommissionEntitlement entitlement);

    void Add(CommissionStatement statement);
}

/// <summary>An intermediary in the register, in force.</summary>
public sealed record IntermediaryInfo(string IntermediaryCode, string Status, Guid? PartyId);

/// <summary>The Distribution module's static data in force.</summary>
public interface IDistributionCodes
{
    Task<IntermediaryInfo?> Intermediary(string intermediaryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The plan the intermediary earns on the product (cfg_intermediary_product_permissions.plan_code, OQ-062).</summary>
    Task<string?> PlanCodeFor(string intermediaryCode, string productCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The plan version in force on the day (DST-002).</summary>
    Task<CommissionPlan?> Plan(string planCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A plan version, whatever its status now (the version an entitlement recorded, DST-003).</summary>
    Task<CommissionPlan?> PlanVersion(string planCode, int version, CancellationToken cancellationToken);
}

public sealed record ClawbackView(Guid Id, string Reason, decimal Amount, Guid? StatementId);

public sealed record EntitlementView(Guid Id, string IntermediaryCode, string SourceType, Guid SourceId, string SourceDomain, Guid PolicyRef, string ProductCode,
    string PlanCode, int PlanVersion, decimal BasisAmount, decimal Rate, decimal Amount, string Currency, DateOnly DueDate, EntitlementStatus Status, Guid? StatementId,
    ClawbackView? Clawback, long RowVersion)
{
    public static EntitlementView From(CommissionEntitlement e) => new(e.Id, e.IntermediaryCode, e.SourceType, e.SourceId, e.SourceDomain, e.PolicyRef, e.ProductCode,
        e.PlanCode, e.PlanVersion, e.BasisAmount, e.Rate, e.Amount, e.Currency, e.DueDate, e.Status, e.StatementId,
        e.Clawback is { } c ? new ClawbackView(c.Id, c.Reason, c.Amount, c.StatementId) : null, e.RowVersion);
}

public sealed record StatementView(Guid Id, string IntermediaryCode, string PeriodCode, string Currency, decimal GrossAmount, decimal TaxAmount, decimal NetAmount,
    StatementStatus Status, bool ApprovalPending, Guid? ApprovedBy, Guid? PayeePartyId, Guid? BankAccountId, Guid? PaymentRef, DateOnly? PaidOn, long RowVersion)
{
    public static StatementView From(CommissionStatement s) => new(s.Id, s.IntermediaryCode, s.PeriodCode, s.Currency, s.GrossAmount, s.TaxAmount, s.NetAmount,
        s.Status, s.ApprovalPending, s.ApprovedBy, s.PayeePartyId, s.BankAccountId, s.PaymentRef, s.PaidOn, s.RowVersion);
}

/// <summary>Command PrepareStatement for an intermediary, a period (yyyy-MM) and a currency; preparing a draft again recalculates it.</summary>
public sealed record PrepareStatement(string IntermediaryCode, string PeriodCode, string Currency) : ICommand<StatementView>;

/// <summary>Command ClawBack, by a person (a policy cancelled or lapsed claws back through events).</summary>
public sealed record ClawBackCommission(Guid EntitlementId, string Reason) : ICommand<EntitlementView>;

public sealed record GetEntitlement(Guid EntitlementId) : IQuery<EntitlementView>;

public sealed record ListEntitlements(string? IntermediaryCode, Guid? PolicyRef, EntitlementStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<EntitlementView>>;

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
    IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<PrepareStatement, StatementView>
{
    public async Task<Result<StatementView>> Handle(PrepareStatement command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await codes.Intermediary(command.IntermediaryCode, clock.UtcToday, cancellationToken) is not { PartyId: { } partyId }
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

        var current = await repository.OnStatement(statement.Id, cancellationToken);
        var (payable, toRecover) = await repository.Due(command.IntermediaryCode, command.Currency, cancellationToken);
        audit.Record(new AuditEntry("COMMISSION", "PrepareStatement", nameof(CommissionStatement), statement.Id.ToString(),
            new Dictionary<string, string?> { ["intermediary"] = command.IntermediaryCode, ["period"] = command.PeriodCode }));

        // Lines released from this draft count again when still due.
        var again = current.Where(e => e.Status == EntitlementStatus.Payable && e.StatementId == statement.Id).ToList();
        var recoverAgain = current.Where(e => e.Clawback?.StatementId == statement.Id).ToList();
        var prepared = statement.PrepareStatement(payable.Concat(again).DistinctBy(e => e.Id).ToList(), toRecover.Concat(recoverAgain).DistinctBy(e => e.Id).ToList(),
            current, new StatementPayee(partyId, account.Id, account.AccountNumberHash), user.Value, clock.UtcNow);
        return prepared.IsFailure ? prepared.Error : StatementView.From(statement);
    }
}

internal sealed class ClawBackCommissionHandler(IDistributionRepository repository, IClock clock, IAuditTrail audit) : ICommandHandler<ClawBackCommission, EntitlementView>
{
    public async Task<Result<EntitlementView>> Handle(ClawBackCommission command, CancellationToken cancellationToken)
    {
        if (await repository.Entitlement(command.EntitlementId, cancellationToken) is not { } entitlement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The commission entitlement was not found.");
        }

        audit.Record(new AuditEntry("COMMISSION", "ClawBack", nameof(CommissionEntitlement), entitlement.Id.ToString(),
            new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var result = entitlement.ClawBack(command.Reason, clock.UtcToday, clock.UtcNow);
        return result.IsFailure ? result.Error : EntitlementView.From(entitlement);
    }
}

internal sealed class DistributionQueries(IDistributionRepository repository, IIntermediaries intermediaries, ITenantContext tenant, IClock clock) :
    IQueryHandler<GetEntitlement, EntitlementView>,
    IQueryHandler<ListEntitlements, IReadOnlyList<EntitlementView>>,
    IQueryHandler<GetStatement, StatementView>,
    IQueryHandler<CheckIntermediary, IntermediaryCheckView>
{
    public async Task<Result<EntitlementView>> Handle(GetEntitlement query, CancellationToken cancellationToken) =>
        await repository.Entitlement(query.EntitlementId, cancellationToken) is { } e ? EntitlementView.From(e) : Error.NotFound(ErrorCodes.NotFound, "The commission entitlement was not found.");

    public async Task<Result<IReadOnlyList<EntitlementView>>> Handle(ListEntitlements query, CancellationToken cancellationToken) =>
        (await repository.List(query.IntermediaryCode, query.PolicyRef, query.Status, query.AfterId, Math.Clamp(query.Limit, 1, 200), cancellationToken))
        .Select(EntitlementView.From).ToList();

    public async Task<Result<StatementView>> Handle(GetStatement query, CancellationToken cancellationToken) =>
        await repository.Statement(query.StatementId, cancellationToken) is { } s ? StatementView.From(s) : Error.NotFound(ErrorCodes.NotFound, "The commission statement was not found.");

    public async Task<Result<IntermediaryCheckView>> Handle(CheckIntermediary query, CancellationToken cancellationToken)
    {
        var refusal = await intermediaries.CanSell(tenant.RequireTenantId(), query.IntermediaryCode, query.ProductCode, IntermediaryTransactions.NewBusiness, clock.UtcToday,
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
    IClock clock, ILogger<CommissionPolicies> logger) :
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<InvoiceSettled>,
    IIntegrationEventHandler<GeneralPolicyCancelled>,
    IIntegrationEventHandler<LifePolicyLapsed>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>
{
    public async Task Handle(InvoiceIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (issued.NetAmount <= 0 || policies.FirstOrDefault(p => p.SourceDomain == issued.SourceDomain) is not { } domain
            || await domain.For(tenant.RequireTenantId(), issued.PolicyRef, cancellationToken) is not { IntermediaryCode: { Length: > 0 } intermediary } policy
            || await repository.EntitlementFor(CommissionEntitlement.InvoiceSource, issued.InvoiceId, intermediary, cancellationToken) is not null)
        {
            return;
        }

        if (await codes.PlanCodeFor(intermediary, policy.ProductCode, issued.IssueDate, cancellationToken) is not { } planCode)
        {
            logger.LogWarning("No commission plan for intermediary {Intermediary} on product {Product}; invoice {InvoiceNumber} earns none",
                intermediary, policy.ProductCode, issued.InvoiceNumber);
            return;
        }

        // A plan named but not in force is a configuration error: retried, then dead-lettered for a person.
        var plan = await codes.Plan(planCode, issued.IssueDate, cancellationToken)
                   ?? throw new InvalidOperationException($"Commission plan {planCode} is not in force on {issued.IssueDate:yyyy-MM-dd}.");
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

    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, cancellationToken);

    /// <summary>"Payout settled": the statement and its payable entitlements are paid.</summary>
    public async Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (succeeded.BusinessRefType == DistributionApprovals.StatementBusinessRefType && succeeded.BusinessRefId is { } id
            && await repository.Statement(id, cancellationToken) is { Status: StatementStatus.Approved } statement)
        {
            statement.PayoutSettled(succeeded.PaymentId, succeeded.ValueDate, await repository.OnStatement(statement.Id, cancellationToken));
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

            entitlement.ClawBack(reason, clock.UtcToday, clock.UtcNow);
        }
    }

    private async Task Decide(string businessRefType, Guid statementId, bool approved, Guid approvalId, Guid checkerId, string? subjectHash,
        CancellationToken cancellationToken)
    {
        if (businessRefType == DistributionApprovals.StatementBusinessRefType && await repository.Statement(statementId, cancellationToken) is { } statement)
        {
            var decided = statement.ApproveStatement(approved, approvalId, checkerId, subjectHash, clock.UtcNow);
            if (decided.IsFailure)
            {
                logger.LogWarning("Approval {ApprovalId} not applied to statement {StatementId}: {Code}", approvalId, statementId, decided.Error.Code);
            }
        }
    }
}
