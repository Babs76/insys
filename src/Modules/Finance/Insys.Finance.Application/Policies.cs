using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Finance.Contracts;
using Insys.Finance.Domain.Ledger;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.Finance.Application;

/// <summary>
/// Finance's PostBusinessEvent policies (domain-model.json: finance consumes). Each consumed event is posted as a
/// transaction type (<see cref="TransactionTypes"/>); the posting rule in force on the accounting date decides the
/// accounts. The source event's ID is the journal's source event (FIN-001), and the inbox makes it one journal per
/// event (DBA-007). Unknown payment outcomes are never posted: Payments raises no received or succeeded event for
/// them (PAY-005).
/// </summary>
public sealed class PostingPolicies(EventPoster poster) :
    IIntegrationEventHandler<InvoiceIssued>,
    IIntegrationEventHandler<CreditNoteIssued>,
    IIntegrationEventHandler<PaymentReceived>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentReversed>,
    IIntegrationEventHandler<ClaimReserveChanged>,
    IIntegrationEventHandler<ClaimPaymentCompleted>,
    IIntegrationEventHandler<Health.Contracts.MedicalClaimSettled>,
    IIntegrationEventHandler<ClaimRecoveryRecorded>,
    IIntegrationEventHandler<Distribution.Contracts.CommissionAccrued>,
    IIntegrationEventHandler<Distribution.Contracts.CommissionClawedBack>,
    IIntegrationEventHandler<Reinsurance.Contracts.CessionCalculated>,
    IIntegrationEventHandler<Reinsurance.Contracts.RecoveryBilled>,
    IIntegrationEventHandler<Reinsurance.Contracts.RecoverySettled>
{
    /// <summary>
    /// Premium ceded by a cession, or the increase on a recalculation (process "General quote to cash": PostBusinessEvent for
    /// cession). TODO(spec): a recalculation that cedes less needs a return of ceded premium, which has no transaction type (OQ-063).
    /// </summary>
    public Task Handle(Reinsurance.Contracts.CessionCalculated e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        e.PremiumChange <= 0
            ? Task.CompletedTask
            : Post(envelope, e.SourceDomain, TransactionTypes.PremiumCeded, DateOnly.FromDateTime(envelope.OccurredAt.UtcDateTime), e.PremiumChange, e.Currency, "CESSION",
                e.CessionId, new() { ["domain"] = e.SourceDomain, ["treaty"] = e.TreatyCode, ["rule"] = e.RuleCode }, cancellationToken);

    /// <summary>A recovery billed to the reinsurer is recoverable (REI-003).</summary>
    public Task Handle(Reinsurance.Contracts.RecoveryBilled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.SourceDomain, TransactionTypes.ReinsuranceRecoverable, e.BilledOn, e.Amount, e.Currency, "RI_RECOVERY", e.RecoveryId,
            new() { ["domain"] = e.SourceDomain, ["treaty"] = e.TreatyCode }, cancellationToken);

    /// <summary>What the reinsurer paid on a recovery (REI-003).</summary>
    public Task Handle(Reinsurance.Contracts.RecoverySettled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        e.Amount <= 0
            ? Task.CompletedTask
            : Post(envelope, e.SourceDomain, TransactionTypes.ReinsuranceRecovered, e.SettledOn, e.Amount, e.Currency, "RI_RECOVERY", e.RecoveryId,
                new() { ["domain"] = e.SourceDomain, ["treaty"] = e.TreatyCode }, cancellationToken);

    public Task Handle(InvoiceIssued e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.SourceDomain, TransactionTypes.PremiumDue, e.IssueDate, e.TotalAmount, e.Currency, "INVOICE", e.InvoiceId,
            new() { ["domain"] = e.SourceDomain, ["product_version"] = e.ProductVersionRef }, cancellationToken);

    public Task Handle(CreditNoteIssued e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.SourceDomain, TransactionTypes.PremiumCredited, e.IssueDate, e.TotalAmount, e.Currency, "INVOICE", e.CreditNoteId,
            new() { ["domain"] = e.SourceDomain, ["reason"] = e.ReasonCode }, cancellationToken);

    public Task Handle(PaymentReceived e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, "SHARED", TransactionTypes.PaymentReceived, e.ValueDate, e.Amount, e.Currency, "PAYMENT", e.PaymentId,
            new() { ["purpose"] = e.Purpose }, cancellationToken);

    /// <summary>Outbound payments are posted by their purpose's owner (claims payments by ClaimPaymentCompleted); refunds and commission are posted here.</summary>
    public Task Handle(PaymentSucceeded e, EventEnvelope envelope, CancellationToken cancellationToken) => e.Purpose switch
    {
        PaymentPurposes.Refund => Post(envelope, "SHARED", TransactionTypes.RefundPaid, e.ValueDate, e.Amount, e.Currency, "PAYMENT", e.PaymentId,
            new() { ["purpose"] = e.Purpose }, cancellationToken),
        PaymentPurposes.Commission => Post(envelope, "SHARED", TransactionTypes.CommissionPaid, e.ValueDate, e.Amount, e.Currency, "PAYMENT", e.PaymentId,
            new() { ["purpose"] = e.Purpose }, cancellationToken),
        _ => Task.CompletedTask,
    };

    /// <summary>Commission accrued on an invoice (process "General quote to cash": PostBusinessEvent for commission).</summary>
    public Task Handle(Distribution.Contracts.CommissionAccrued e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.SourceDomain, TransactionTypes.CommissionAccrued, e.AccruedOn, e.Amount, e.Currency, "COMMISSION", e.EntitlementId,
            new() { ["domain"] = e.SourceDomain, ["intermediary"] = e.IntermediaryCode, ["plan"] = e.PlanCode }, cancellationToken);

    /// <summary>A clawback reverses the commission it references (process "Missed premium to lapse": PostBusinessEvent, clawback).</summary>
    public Task Handle(Distribution.Contracts.CommissionClawedBack e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.SourceDomain, TransactionTypes.CommissionClawedBack, e.ClawedBackOn, e.Amount, e.Currency, "COMMISSION", e.EntitlementId,
            new() { ["domain"] = e.SourceDomain, ["intermediary"] = e.IntermediaryCode, ["reason"] = e.Reason }, cancellationToken);

    public Task Handle(PaymentReversed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, "SHARED", TransactionTypes.PaymentReversed, e.ReversedOn, e.Amount, e.Currency, "PAYMENT", e.PaymentId,
            new() { ["purpose"] = e.Purpose, ["direction"] = e.Direction }, cancellationToken);

    public Task Handle(ClaimReserveChanged e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        e.Change == 0
            ? Task.CompletedTask
            : Post(envelope, e.Domain, e.Change > 0 ? TransactionTypes.ClaimReserveIncrease : TransactionTypes.ClaimReserveDecrease, e.MovementDate, Math.Abs(e.Change),
                e.Currency, "CLAIM_MOVEMENT", e.MovementId, new() { ["domain"] = e.Domain, ["reserve_type"] = e.ReserveType }, cancellationToken);

    public Task Handle(ClaimPaymentCompleted e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.Domain, TransactionTypes.ClaimPaid, e.PaidOn, e.Amount, e.Currency, "CLAIM_MOVEMENT", e.MovementId,
            new() { ["domain"] = e.Domain, ["reserve_type"] = e.ReserveType }, cancellationToken);

    /// <summary>A Health claim paid by a provider payment run (process "Health care to provider payment": PostBusinessEvent, claims paid).</summary>
    public Task Handle(Health.Contracts.MedicalClaimSettled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        e.Amount <= 0
            ? Task.CompletedTask
            : Post(envelope, Health.Contracts.HealthReferences.SourceDomain, TransactionTypes.ClaimPaid, e.PaidOn, e.Amount, e.Currency, "MEDICAL_CLAIM", e.ClaimId,
                new() { ["domain"] = Health.Contracts.HealthReferences.SourceDomain, ["reserve_type"] = "HEALTH" }, cancellationToken);

    public Task Handle(ClaimRecoveryRecorded e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Post(envelope, e.Domain, TransactionTypes.ClaimRecovery, e.MovementDate, e.Amount, e.Currency, "CLAIM_MOVEMENT", e.MovementId,
            new() { ["domain"] = e.Domain, ["reserve_type"] = e.ReserveType }, cancellationToken);

    private Task Post(EventEnvelope envelope, string sourceDomain, string transactionType, DateOnly accountingDate, decimal amount, string currency,
        string businessRefType, Guid businessRefId, Dictionary<string, object?> variables, CancellationToken cancellationToken)
    {
        variables["currency"] = currency;
        variables["source_domain"] = sourceDomain;
        return poster.Post(new BusinessEvent(envelope.EventId, sourceDomain.Length <= 10 ? sourceDomain : sourceDomain[..10], transactionType, accountingDate,
            DateOnly.FromDateTime(envelope.OccurredAt.UtcDateTime), amount, currency, businessRefType, businessRefId, variables), envelope.EventType, cancellationToken);
    }
}

/// <summary>Workflow's decision on reopening a soft-closed period (ADR-009).</summary>
public sealed class PeriodReopenPolicy(IAccountingPeriods periods) : IIntegrationEventHandler<ApprovalGranted>, IIntegrationEventHandler<ApprovalRejected>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.CheckerId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid periodId, bool approved, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType == FinanceApprovals.PeriodBusinessRefType && await periods.Get(periodId, cancellationToken) is { } period)
        {
            period.DecideReopen(approved, checkerId);
        }
    }
}
