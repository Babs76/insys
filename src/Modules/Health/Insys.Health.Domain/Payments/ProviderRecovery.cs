/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain.Payments;

public static class RecoverySources
{
    public const string Reversal = "REVERSAL";
    public const string CreditNote = "CREDIT_NOTE";
    public const string Finding = "FINDING";
}

/// <summary>How a recovery is collected: deducted from runs, invoiced through Billing, or as a person decides (a member's, OQ-145).</summary>
public static class RecoveryBases
{
    public const string Offset = "OFFSET";
    public const string Invoice = "INVOICE";
    public const string Person = "PERSON";
}

public static class RecoveryStatuses
{
    public const string Proposed = "PROPOSED";
    public const string Open = "OPEN";
    public const string Invoiced = "INVOICED";
    public const string Recovered = "RECOVERED";
    public const string WrittenOff = "WRITTEN_OFF";
    public const string Reversed = "REVERSED";
    public const string Rejected = "REJECTED";
}

public static class RecoveryDebtors
{
    public const string Provider = "PROVIDER";
    public const string Member = "MEMBER";
}

public static class OffsetStatuses
{
    public const string Reserved = "RESERVED";
    public const string Taken = "TAKEN";
    public const string Released = "RELEASED";
}

public static class CreditNoteStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

public sealed record ProviderRecoveryRaised(Guid RecoveryId, string RecoveryNumber, string DebtorKind, string? ProviderId, string Source, string? ClaimNumber, decimal Amount,
    string Currency, DateOnly RaisedOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Business references only: it asks for the notice to the provider (§4.7).</summary>
public sealed record ProviderRecoveryNoticeDue(Guid RecoveryId, string RecoveryNumber, Guid PartyId, string? ClaimNumber, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryInvoiceRequested(Guid RecoveryId, string RecoveryNumber, Guid PayerPartyId, decimal Amount, string Currency, DateOnly DueOn, string? CollectionMethod, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryOffsetTaken(Guid OffsetId, Guid RecoveryId, Guid RunId, Guid RemittanceId, decimal Amount, string Currency, DateOnly TakenOn, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryRepaid(Guid RecoveryId, Guid RunId, decimal Amount, string Currency, DateOnly RepaidOn, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryWrittenOff(Guid RecoveryId, string RecoveryNumber, string? ProviderId, decimal Amount, string Currency, DateOnly WrittenOffOn, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryReversed(Guid RecoveryId, string RecoveryNumber, string? ProviderId, string Source, decimal Amount, string Currency, DateOnly ReversedOn, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoverySettled(Guid RecoveryId, decimal Amount, string Currency, DateOnly SettledOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Uncollected for the tenant's period, or a member's overpayment: a person decides (§4.7, OQ-139, OQ-145).</summary>
public sealed record ProviderRecoveryReviewDue(Guid RecoveryId, string RecoveryNumber, string DebtorKind, string? ProviderId, decimal Balance, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryWriteOffRequested(Guid RecoveryId, string RecoveryNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderRecoveryFindingProposed(Guid RecoveryId, string RecoveryNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProviderCreditNoteSubmitted(Guid CreditNoteId, string CreditNoteNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>One deduction of a recovery from a remittance. Reserved when the run is calculated; taken when it is released; released when its approval ends.</summary>
public sealed class ProviderRecoveryOffset : Entity<Guid>, ITenantScoped
{
    private ProviderRecoveryOffset()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RecoveryId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid RemittanceId { get; private set; }

    public decimal Amount { get; private set; }

    public string Status { get; private set; } = OffsetStatuses.Reserved;

    internal static ProviderRecoveryOffset Create(Guid recoveryId, Guid runId, Guid remittanceId, decimal amount) => new()
    {
        Id = Guid.CreateVersion7(), RecoveryId = recoveryId, RunId = runId, RemittanceId = remittanceId, Amount = amount,
    };

    internal void Take() => Status = OffsetStatuses.Taken;

    internal void Release() => Status = OffsetStatuses.Released;
}

/// <summary>
/// The ProviderRecovery aggregate (health.provider_recovery, provider_recovery_offset): one amount owed, by a provider or a member.
/// Its balance only falls by offsets and settlement, and it is never dropped: it ends recovered, written off with a second person
/// and a reason, or reversed when raised in error (§4.7).
/// </summary>
public sealed class ProviderRecovery : AggregateRoot<Guid>
{
    private readonly List<ProviderRecoveryOffset> _offsets = [];

    private ProviderRecovery()
    {
    }

    public string RecoveryNumber { get; private set; } = "";

    public string DebtorKind { get; private set; } = RecoveryDebtors.Provider;

    public string? ProviderId { get; private set; }

    public Guid? DebtorPartyId { get; private set; }

    public string Source { get; private set; } = "";

    public Guid? SourceId { get; private set; }

    public Guid? ClaimId { get; private set; }

    public string? ClaimNumber { get; private set; }

    public decimal OriginalAmount { get; private set; }

    public decimal Balance { get; private set; }

    public string Currency { get; private set; } = "";

    public string Basis { get; private set; } = RecoveryBases.Offset;

    public string Status { get; private set; } = RecoveryStatuses.Open;

    public string Reason { get; private set; } = "";

    public Guid? RaisedBy { get; private set; }

    public DateOnly RaisedOn { get; private set; }

    public DateOnly? ReviewDueOn { get; private set; }

    public bool InReview { get; private set; }

    public Guid? HeldByCaseId { get; private set; }

    public Guid? InvoiceId { get; private set; }

    public string? WriteOffReason { get; private set; }

    public Guid? WriteOffRequestedBy { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public decimal RepayAmount { get; private set; }

    public Guid? RepaidInRunId { get; private set; }

    public DateOnly? ClosedOn { get; private set; }

    public IReadOnlyList<ProviderRecoveryOffset> Offsets => _offsets;

    public decimal Taken => _offsets.Where(o => o.Status == OffsetStatuses.Taken).Sum(o => o.Amount);

    public bool Offsettable => Status == RecoveryStatuses.Open && Basis == RecoveryBases.Offset && HeldByCaseId is null && WriteOffRequestedBy is null && Balance > 0;

    public string WriteOffHash => ApprovalSubject.Hash(["HEALTH_RECOVERY_WRITE_OFF", Id, Balance, Currency]);

    public string FindingHash => ApprovalSubject.Hash(["HEALTH_RECOVERY_FINDING", Id, OriginalAmount, Currency, ProviderId]);

    /// <summary>
    /// Raised by a reversal or an approved credit note. The agreement's offset flag decides (addition 1): set, it is deducted from
    /// runs, and goes to a person if uncollected by <paramref name="reviewDueOn"/>; not set, it is invoiced at once. A member's goes
    /// straight to a person (OQ-145).
    /// </summary>
    public static ProviderRecovery Raise(Guid id, string number, string debtorKind, string? providerId, Guid? partyId, string source, Guid? sourceId, Guid? claimId, string? claimNumber,
        decimal amount, string currency, bool offsetAllowed, string reason, DateOnly today, DateOnly reviewDueOn, string? collectionMethod, DateTimeOffset now)
    {
        var recovery = New(id, number, debtorKind, providerId, partyId, source, sourceId, claimId, claimNumber, amount, currency, reason, today);
        recovery.Open(offsetAllowed, today, reviewDueOn, collectionMethod, now);
        return recovery;
    }

    /// <summary>A person's finding: nothing is owed until a second person approves it (§4.7).</summary>
    public static ProviderRecovery ProposeFinding(Guid id, string number, string providerId, Guid? partyId, decimal amount, string currency, string reason, Guid by, DateOnly today, DateTimeOffset now)
    {
        var recovery = New(id, number, RecoveryDebtors.Provider, providerId, partyId, RecoverySources.Finding, id, null, null, amount, currency, reason, today);
        recovery.Status = RecoveryStatuses.Proposed;
        recovery.RaisedBy = by;
        recovery.Raise(new ProviderRecoveryFindingProposed(id, number, by, amount, currency, recovery.FindingHash, now));
        return recovery;
    }

    private static ProviderRecovery New(Guid id, string number, string debtorKind, string? providerId, Guid? partyId, string source, Guid? sourceId, Guid? claimId, string? claimNumber,
        decimal amount, string currency, string reason, DateOnly today)
    {
        var recovery = new ProviderRecovery
        {
            Id = id, RecoveryNumber = Guard.MaxLength(number, 25), DebtorKind = debtorKind, ProviderId = providerId, DebtorPartyId = partyId, Source = source, SourceId = sourceId,
            ClaimId = claimId, ClaimNumber = claimNumber, OriginalAmount = amount, Balance = amount, Currency = currency, Reason = Guard.MaxLength(reason, 500), RaisedOn = today,
        };
        recovery.MarkChanged();
        return recovery;
    }

    private void Open(bool offsetAllowed, DateOnly today, DateOnly reviewDueOn, string? collectionMethod, DateTimeOffset now)
    {
        Status = RecoveryStatuses.Open;
        Raise(new ProviderRecoveryRaised(Id, RecoveryNumber, DebtorKind, ProviderId, Source, ClaimNumber, OriginalAmount, Currency, today, now));
        if (DebtorPartyId is { } party && DebtorKind == RecoveryDebtors.Provider)
        {
            Raise(new ProviderRecoveryNoticeDue(Id, RecoveryNumber, party, ClaimNumber, OriginalAmount, Currency, now));
        }

        if (DebtorKind == RecoveryDebtors.Member)
        {
            Basis = RecoveryBases.Person;
            InReview = true;
            Raise(new ProviderRecoveryReviewDue(Id, RecoveryNumber, DebtorKind, ProviderId, Balance, Currency, now));
        }
        else if (offsetAllowed)
        {
            Basis = RecoveryBases.Offset;
            ReviewDueOn = reviewDueOn;
        }
        else
        {
            Basis = RecoveryBases.Invoice;
            RequestInvoice(today, collectionMethod, now);
        }
    }

    /// <summary>Workflow's decision on a finding: approved, the recovery is raised; otherwise it ends, nothing owed.</summary>
    public Result DecideFinding(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, bool offsetAllowed, DateOnly today, DateOnly reviewDueOn, string? collectionMethod, DateTimeOffset now)
    {
        if (Status != RecoveryStatuses.Proposed || subjectHash != FindingHash)
        {
            return Error.Conflict(HealthErrorCodes.ApprovalSubjectChanged, $"Recovery {RecoveryNumber} is {Status}.");
        }

        ApprovalId = approvalId;
        DecidedBy = checkerId;
        if (approved)
        {
            Open(offsetAllowed, today, reviewDueOn, collectionMethod, now);
        }
        else
        {
            Status = RecoveryStatuses.Rejected;
            Balance = 0;
            ClosedOn = today;
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>Deducts up to the amount from a calculated remittance, oldest first by the caller; the balance is reserved until the run ends one way or the other.</summary>
    public decimal ReserveOffset(Guid runId, Guid remittanceId, decimal available)
    {
        var amount = Math.Min(Balance, available);
        if (amount <= 0 || !Offsettable)
        {
            return 0;
        }

        _offsets.Add(ProviderRecoveryOffset.Create(Id, runId, remittanceId, amount));
        Balance -= amount;
        MarkChanged();
        return amount;
    }

    /// <summary>The run was released: its offsets are taken. A balance of nil is recovered. A redelivery changes nothing.</summary>
    public void TakeOffsets(Guid runId, DateOnly today, DateTimeOffset now)
    {
        foreach (var offset in _offsets.Where(o => o.RunId == runId && o.Status == OffsetStatuses.Reserved))
        {
            offset.Take();
            Raise(new ProviderRecoveryOffsetTaken(offset.Id, Id, runId, offset.RemittanceId, offset.Amount, Currency, today, now));
        }

        if (Balance == 0 && Status == RecoveryStatuses.Open)
        {
            Status = RecoveryStatuses.Recovered;
            ClosedOn = today;
        }

        MarkChanged();
    }

    /// <summary>The run's approval ended without being granted: what it reserved goes back on the balance.</summary>
    public void ReleaseOffsets(Guid runId)
    {
        foreach (var offset in _offsets.Where(o => o.RunId == runId && o.Status == OffsetStatuses.Reserved))
        {
            offset.Release();
            Balance += offset.Amount;
        }

        MarkChanged();
    }

    /// <summary>Uncollected for the tenant's period: it goes to a person, once. A disputed one waits for its dispute.</summary>
    public Result SendToReview(DateTimeOffset now)
    {
        if (Status != RecoveryStatuses.Open || InReview || HeldByCaseId is not null)
        {
            return Result.Success();
        }

        InReview = true;
        Raise(new ProviderRecoveryReviewDue(Id, RecoveryNumber, DebtorKind, ProviderId, Balance, Currency, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>OQ-146: a disputed recovery is held from offsets until the dispute ends. Only the case that holds it releases it.</summary>
    public void HoldForDispute(Guid caseId)
    {
        if (Status == RecoveryStatuses.Open && HeldByCaseId is null)
        {
            HeldByCaseId = caseId;
            MarkChanged();
        }
    }

    public void ReleaseHold(Guid caseId)
    {
        if (HeldByCaseId == caseId)
        {
            HeldByCaseId = null;
            MarkChanged();
        }
    }

    /// <summary>A person's decision: the balance is invoiced through Billing (§4.7, OQ-145).</summary>
    public Result Invoice(DateOnly today, string? collectionMethod, DateTimeOffset now)
    {
        if (Status != RecoveryStatuses.Open || Balance <= 0 || HeldByCaseId is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Recovery {RecoveryNumber} is {Status}{(HeldByCaseId is null ? "" : ", held by a dispute")}.");
        }

        if (DebtorPartyId is null)
        {
            return Error.Validation(HealthErrorCodes.ProviderNotPayable, "The recovery's debtor has no party to invoice.");
        }

        if (_offsets.Any(o => o.Status == OffsetStatuses.Reserved))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Part of the recovery is in a payment run still being decided.");
        }

        RequestInvoice(today, collectionMethod, now);
        MarkChanged();
        return Result.Success();
    }

    private void RequestInvoice(DateOnly today, string? collectionMethod, DateTimeOffset now)
    {
        Status = RecoveryStatuses.Invoiced;
        InReview = false;
        if (DebtorPartyId is { } party)
        {
            Raise(new ProviderRecoveryInvoiceRequested(Id, RecoveryNumber, party, Balance, Currency, today, collectionMethod, now));
        }
    }

    public void InvoiceIssued(Guid invoiceId)
    {
        InvoiceId ??= invoiceId;
        MarkChanged();
    }

    /// <summary>The invoice was paid: the balance is recovered. A redelivery changes nothing.</summary>
    public void InvoiceSettled(DateOnly today, DateTimeOffset now)
    {
        if (Status == RecoveryStatuses.Invoiced)
        {
            Raise(new ProviderRecoverySettled(Id, Balance, Currency, today, now));
            Balance = 0;
            Status = RecoveryStatuses.Recovered;
            ClosedOn = today;
            MarkChanged();
        }
    }

    /// <summary>Asks for a second person's approval; the reason is required (addition 3). The balance stays owed until it is granted.</summary>
    public Result RequestWriteOff(string reason, Guid by, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(HealthErrorCodes.ReasonRequired, "A write-off needs a reason.");
        }

        if (Status != RecoveryStatuses.Open || Balance <= 0 || WriteOffRequestedBy is not null || _offsets.Any(o => o.Status == OffsetStatuses.Reserved))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Recovery {RecoveryNumber} cannot be written off now.");
        }

        WriteOffReason = Guard.MaxLength(reason, 500);
        WriteOffRequestedBy = by;
        Raise(new ProviderRecoveryWriteOffRequested(Id, RecoveryNumber, by, Balance, Currency, WriteOffHash, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Workflow's decision: granted, the balance is written off; otherwise it stays owed and the request is cleared.</summary>
    public Result DecideWriteOff(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateOnly today, DateTimeOffset now)
    {
        if (Status != RecoveryStatuses.Open || WriteOffRequestedBy is null || subjectHash != WriteOffHash)
        {
            return Error.Conflict(HealthErrorCodes.ApprovalSubjectChanged, $"Recovery {RecoveryNumber} has no write-off waiting.");
        }

        ApprovalId = approvalId;
        DecidedBy = checkerId;
        if (approved)
        {
            Raise(new ProviderRecoveryWrittenOff(Id, RecoveryNumber, ProviderId, Balance, Currency, today, now));
            Balance = 0;
            Status = RecoveryStatuses.WrittenOff;
            ClosedOn = today;
        }
        else
        {
            WriteOffReason = null;
            WriteOffRequestedBy = null;
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>A recovery raised in error is reversed, not edited. An offset already taken is repaid in the provider's next run (§4.7).</summary>
    public Result Reverse(string reason, DateOnly today, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(HealthErrorCodes.ReasonRequired, "Reversing a recovery needs a reason.");
        }

        if (Status is not (RecoveryStatuses.Open or RecoveryStatuses.Recovered) || InvoiceId is not null || _offsets.Any(o => o.Status == OffsetStatuses.Reserved))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Recovery {RecoveryNumber} is {Status}; one invoiced, or part in a run being decided, cannot be reversed.");
        }

        RepayAmount = Taken;
        Balance = 0;
        Status = RecoveryStatuses.Reversed;
        Reason = Guard.MaxLength($"{Reason} | Reversed: {reason}", 500);
        ClosedOn = today;
        Raise(new ProviderRecoveryReversed(Id, RecoveryNumber, ProviderId, Source, OriginalAmount, Currency, today, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The amount to repay in this run, once.</summary>
    public decimal ClaimRepayment(Guid runId)
    {
        if (RepayAmount <= 0 || RepaidInRunId is not null)
        {
            return 0;
        }

        RepaidInRunId = runId;
        MarkChanged();
        return RepayAmount;
    }

    public void RepaymentReleased(Guid runId, DateOnly today, DateTimeOffset now)
    {
        if (RepaidInRunId == runId)
        {
            Raise(new ProviderRecoveryRepaid(Id, runId, RepayAmount, Currency, today, now));
            MarkChanged();
        }
    }

    public void RepaymentNotReleased(Guid runId)
    {
        if (RepaidInRunId == runId)
        {
            RepaidInRunId = null;
            MarkChanged();
        }
    }
}

/// <summary>The ProviderCreditNote aggregate (health.provider_credit_note): a provider's credit note against a claim or remittance. A second person approves it, and it then raises a recovery (§4.6).</summary>
public sealed class ProviderCreditNote : AggregateRoot<Guid>
{
    private ProviderCreditNote()
    {
    }

    public string CreditNoteNumber { get; private set; } = "";

    public string ProviderId { get; private set; } = "";

    public string ProviderReference { get; private set; } = "";

    public Guid? ClaimId { get; private set; }

    public Guid? RemittanceId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public string Status { get; private set; } = CreditNoteStatuses.Submitted;

    public string Reason { get; private set; } = "";

    public Guid SubmittedBy { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? RecoveryId { get; private set; }

    public string SubjectHash => ApprovalSubject.Hash(["HEALTH_CREDIT_NOTE", Id, ProviderId, Amount, Currency]);

    public static Result<ProviderCreditNote> Submit(Guid id, string number, string providerId, string reference, Guid? claimId, Guid? remittanceId, decimal amount, string currency, string reason,
        Guid by, DateTimeOffset now)
    {
        if (amount <= 0 || (claimId is null && remittanceId is null) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(reference))
        {
            return Error.Validation(HealthErrorCodes.CreditNoteInvalid, "A credit note has an amount above zero, the provider's number, a reason, and the claim or remittance it credits.");
        }

        var note = new ProviderCreditNote
        {
            Id = id, CreditNoteNumber = Guard.MaxLength(number, 25), ProviderId = Guard.MaxLength(providerId, 15), ProviderReference = Guard.MaxLength(reference, 40), ClaimId = claimId,
            RemittanceId = remittanceId, Amount = amount, Currency = currency, Reason = Guard.MaxLength(reason, 500), SubmittedBy = by,
        };
        note.Raise(new ProviderCreditNoteSubmitted(id, note.CreditNoteNumber, by, amount, currency, note.SubjectHash, now));
        note.MarkChanged();
        return note;
    }

    public bool DecidedByApproval(Guid approvalId) => ApprovalId == approvalId;

    public Result Decide(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, Guid? recoveryId, DateTimeOffset now)
    {
        if (Status != CreditNoteStatuses.Submitted || subjectHash != SubjectHash)
        {
            return Error.Conflict(HealthErrorCodes.ApprovalSubjectChanged, $"Credit note {CreditNoteNumber} is {Status}.");
        }

        Status = approved ? CreditNoteStatuses.Approved : CreditNoteStatuses.Rejected;
        ApprovalId = approvalId;
        DecidedBy = checkerId;
        DecidedAt = now;
        RecoveryId = recoveryId;
        MarkChanged();
        return Result.Success();
    }
}
