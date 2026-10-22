using Insys.BuildingBlocks.Contracts;

namespace Insys.Finance.Contracts;

public static class FinancePermissions
{
    public const string JournalRead = "journal.read";
    public const string JournalReverse = "journal.reverse";
    public const string PeriodRead = "accounting_period.read";
    public const string PeriodUpdate = "accounting_period.update";
    public const string PostingExceptionRead = "posting_exception.read";
}

public static class FinanceApprovals
{
    public const string PeriodBusinessRefType = "ACCOUNTING_PERIOD";
    public const string ReopenApprovalType = "PERIOD_REOPEN";
}

/// <summary>
/// The transaction types Finance posts the business events it consumes as. Posting rules
/// (finance.cfg_posting_rules) name these codes; they must exist in finance.ref_transaction_types (OQ-058).
/// </summary>
public static class TransactionTypes
{
    public const string PremiumDue = "PREMIUM_DUE";
    public const string PremiumCredited = "PREMIUM_CREDITED";
    public const string PaymentReceived = "PAYMENT_RECEIVED";
    public const string PaymentReversed = "PAYMENT_REVERSED";
    public const string RefundPaid = "REFUND_PAID";
    public const string ClaimReserveIncrease = "CLAIM_RESERVE_UP";
    public const string ClaimReserveDecrease = "CLAIM_RESERVE_DOWN";
    public const string ClaimPaid = "CLAIM_PAID";
    public const string ClaimRecovery = "CLAIM_RECOVERY";
    public const string CommissionAccrued = "COMMISSION_ACCRUED";
    public const string CommissionClawedBack = "COMMISSION_CLAWBACK";
    public const string CommissionPaid = "COMMISSION_PAID";
    public const string PremiumCeded = "PREMIUM_CEDED";
    public const string ReinsuranceRecoverable = "RI_RECOVERABLE";
    public const string ReinsuranceRecovered = "RI_RECOVERED";
}

[IntegrationEvent("finance.SubledgerEntryPosted")]
public sealed record SubledgerEntryPosted(Guid JournalId, string JournalNumber, DateOnly AccountingDate, string TransactionType, decimal Amount, string Currency,
    string BusinessRefType, Guid BusinessRefId, string PostingRuleCode, int PostingRuleVersion) : IIntegrationEvent;

[IntegrationEvent("finance.JournalReversed")]
public sealed record JournalReversed(Guid JournalId, Guid ReversalJournalId, DateOnly AccountingDate) : IIntegrationEvent;

/// <summary>A business event could not be posted; it waits in finance.posting_exception for an operator (FIN-007).</summary>
[IntegrationEvent("finance.PostingFailed")]
public sealed record PostingFailed(Guid ExceptionId, Guid SourceEventId, string EventType, string Reason) : IIntegrationEvent;

[IntegrationEvent("finance.AccountingPeriodClosed")]
public sealed record AccountingPeriodClosed(Guid PeriodId, string PeriodCode, bool Final) : IIntegrationEvent;

/// <summary>
/// Public query interface: an account's movement in an accounting period (debits less credits of the journal lines
/// dated in it, reversals included), read on Finance's own connection, for regulatory returns (OQ-064).
/// </summary>
public interface ILedgerBalances
{
    Task<decimal> PeriodMovement(Guid tenantId, string accountCode, string periodCode, CancellationToken cancellationToken);
}
