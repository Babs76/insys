/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Finance.Contracts;

public static class FinancePermissions
{
    public const string JournalRead = "journal.read";
    public const string TaxReturnRead = "tax_return.read";
    public const string TaxReturnCreate = "tax_return.create";

    /// <summary>A second person approves or rejects a tax return (SRS-002).</summary>
    public const string TaxReturnApprove = "tax_return.approve";
    public const string JournalReverse = "journal.reverse";
    public const string PeriodRead = "accounting_period.read";
    public const string PeriodUpdate = "accounting_period.update";
    public const string PostingExceptionRead = "posting_exception.read";
    public const string GlExportRead = "gl_export.read";
    public const string GlExportCreate = "gl_export.create";
    public const string GlExportUpdate = "gl_export.update";
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

    /// <summary>The tax included in an invoice (OQ-058): debit premium income, credit the tax code's account.</summary>
    public const string PremiumTax = "PREMIUM_TAX";

    /// <summary>The tax on a credit note: debit the tax code's account, credit premium income.</summary>
    public const string PremiumTaxCredited = "PREMIUM_TAX_CREDITED";
    public const string PaymentReceived = "PAYMENT_RECEIVED";
    public const string PaymentReversed = "PAYMENT_REVERSED";
    public const string RefundPaid = "REFUND_PAID";
    public const string ClaimReserveIncrease = "CLAIM_RESERVE_UP";
    public const string ClaimReserveDecrease = "CLAIM_RESERVE_DOWN";
    public const string ClaimPaid = "CLAIM_PAID";

    /// <summary>A paid Health claim reversed: debit an overpayment receivable, credit the account CLAIM_PAID debits. Not the bank: no cash moved.</summary>
    public const string ClaimPaidReversed = "CLAIM_PAID_REVERSED";

    /// <summary>A credit note or finding raised an overpayment (Wave 5a §4.7): debit the receivable, credit claims.</summary>
    public const string ProviderRecoveryRaised = "RECOVERY_RAISED";

    /// <summary>An offset took part of a recovery from a remittance: the bank paid that much less than CLAIM_PAID credited. Debit the bank, credit the receivable.</summary>
    public const string ProviderRecoveryOffset = "RECOVERY_OFFSET";

    /// <summary>An offset taken in error is repaid in a run. Debit the receivable, credit the bank.</summary>
    public const string ProviderRecoveryRepaid = "RECOVERY_REPAID";

    /// <summary>A second person approved a write-off. Debit the write-off expense, credit the receivable.</summary>
    public const string ProviderRecoveryWrittenOff = "RECOVERY_WRITTEN_OFF";

    /// <summary>A recovery raised in error. Debit claims, credit the receivable.</summary>
    public const string ProviderRecoveryReversed = "RECOVERY_REVERSED";

    /// <summary>A recovery's invoice was paid: the receipt credited receivables, so debit those and credit the overpayment receivable.</summary>
    public const string ProviderRecoverySettled = "RECOVERY_SETTLED";
    public const string ClaimRecovery = "CLAIM_RECOVERY";
    public const string CommissionAccrued = "COMMISSION_ACCRUED";
    public const string CommissionClawedBack = "COMMISSION_CLAWBACK";
    public const string CommissionPaid = "COMMISSION_PAID";

    /// <summary>Compensation or goodwill paid on a case (OQ-104, PPR 18.8.3).</summary>
    public const string RedressPaid = "REDRESS_PAID";
    public const string PremiumCeded = "PREMIUM_CEDED";
    public const string ReinsuranceRecoverable = "RI_RECOVERABLE";
    public const string ReinsuranceRecovered = "RI_RECOVERED";

    /// <summary>
    /// A non-cash premium receipt (OQ-137): the policy's arrears paid from a claim's settlement. It clears the premium
    /// receivable; CLAIM_PAID posts the claim's gross, so with this the bank side is the net paid. The tenant's posting rule
    /// names the accounts; finance to confirm.
    /// </summary>
    public const string PremiumOffset = "PREMIUM_OFFSET";
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

/// <summary>
/// The business reference types journals carry (GAP-037): an open set, listed as known values on the journals lookup.
/// They match what Finance's posting policies write.
/// </summary>
public static class JournalReferenceTypes
{
    public static readonly IReadOnlyList<string> Known = ["INVOICE", "PAYMENT", "CLAIM_MOVEMENT", "MEDICAL_CLAIM", "COMMISSION", "CESSION", "RI_RECOVERY"];
}
