/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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

namespace Insys.General.Domain.Claims;

/// <summary>States from domain-model.json (general › LossAssessment), V0012.</summary>
public enum AssessmentStatus
{
    Assigned,
    InProgress,
    Reported,
    Accepted,
}

public enum AssessmentTrigger
{
    AssessorStarts,
    SubmitReport,
    AcceptAssessment,
    FurtherWorkRequested,
}

public sealed record LossAssessed(Guid AssessmentId, Guid ClaimId, decimal AssessedAmount, decimal ExcessApplied, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity RepairEstimate (general.repair_estimate): a repairer's estimate recorded during the assessment.</summary>
public sealed class RepairEstimate : Entity<Guid>, ITenantScoped
{
    private RepairEstimate()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public Guid? LossAssessmentId { get; private set; }

    public Guid RepairerPartyId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>SUBMITTED, ACCEPTED or REJECTED (V0016). TODO(spec): estimate states are not specified (OQ-059).</summary>
    public string Status { get; private set; } = "SUBMITTED";

    public long RowVersion { get; private set; }

    internal static RepairEstimate Create(Guid claimId, Guid assessmentId, Guid repairer, decimal amount) =>
        new() { Id = Guid.CreateVersion7(), ClaimId = claimId, LossAssessmentId = assessmentId, RepairerPartyId = Guard.NotEmpty(repairer), Amount = amount };
}

/// <summary>
/// The LossAssessment aggregate (general.loss_assessment, repair_estimate): an assessor on an approved panel for the
/// line (GEN-007) reports the assessed amount and the excess applied.
/// </summary>
public sealed class LossAssessment : AggregateRoot<Guid>
{
    public static readonly StateMachine<AssessmentStatus, AssessmentTrigger> Lifecycle = new StateMachine<AssessmentStatus, AssessmentTrigger>()
        .Allow(AssessmentStatus.Assigned, AssessmentTrigger.AssessorStarts, AssessmentStatus.InProgress)
        .Allow(AssessmentStatus.InProgress, AssessmentTrigger.SubmitReport, AssessmentStatus.Reported)
        .Allow(AssessmentStatus.Reported, AssessmentTrigger.AcceptAssessment, AssessmentStatus.Accepted)
        .Allow(AssessmentStatus.Reported, AssessmentTrigger.FurtherWorkRequested, AssessmentStatus.InProgress);

    private readonly List<RepairEstimate> _estimates = [];

    private LossAssessment()
    {
    }

    public Guid ClaimId { get; private set; }

    public Guid AssessorPartyId { get; private set; }

    public AssessmentStatus Status { get; private set; }

    public decimal? AssessedAmount { get; private set; }

    public decimal? ExcessApplied { get; private set; }

    public Guid? ReportDocumentId { get; private set; }

    public DateTimeOffset? AssessedAt { get; private set; }

    public Guid? AcceptedBy { get; private set; }

    public IReadOnlyList<RepairEstimate> Estimates => _estimates;

    /// <summary>The currency of the assessed amount, the excess and the estimates: the claim's policy's (BR-033).</summary>
    public string Currency { get; private set; } = "";

    /// <summary>Command AssignAssessor: the assessor is on an approved panel for the claim's line (GEN-007).</summary>
    public static Result<LossAssessment> AssignAssessor(Guid id, Guid claimId, Guid assessorPartyId, bool onApprovedPanel, string currency)
    {
        if (!onApprovedPanel)
        {
            return Error.Validation(GeneralErrorCodes.AssessorNotOnPanel, "The assessor is not on an approved panel for the line (GEN-007).");
        }

        var assessment = new LossAssessment
        {
            Id = Guard.NotEmpty(id), ClaimId = Guard.NotEmpty(claimId), AssessorPartyId = Guard.NotEmpty(assessorPartyId), Status = AssessmentStatus.Assigned,
            Currency = Guard.MaxLength(Guard.NotNullOrWhiteSpace(currency), 3),
        };
        assessment.MarkChanged();
        return assessment;
    }

    /// <summary>Transition "Assessor starts".</summary>
    public Result Start() => Fire(AssessmentTrigger.AssessorStarts);

    /// <summary>Command RecordEstimate: a repairer's estimate, while the assessment is in progress.</summary>
    public Result RecordEstimate(Guid repairerPartyId, decimal amount)
    {
        if (Status != AssessmentStatus.InProgress || amount < 0)
        {
            return Status != AssessmentStatus.InProgress
                ? Error.Conflict(ErrorCodes.InvalidStateTransition, $"Estimates are recorded while the assessment is in progress; it is {Status}.")
                : Error.Validation(ErrorCodes.ValidationFailed, "An estimate is not negative.");
        }

        _estimates.Add(RepairEstimate.Create(ClaimId, Id, repairerPartyId, amount));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command SubmitReport: the assessed amount and the excess applied by the coverage's excess schedule (GEN-008).</summary>
    public Result SubmitReport(decimal assessedAmount, decimal excessApplied, Guid? reportDocumentId, DateTimeOffset now)
    {
        if (assessedAmount < 0 || excessApplied < 0 || excessApplied > assessedAmount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The assessed amount is not negative, and the excess is at most the assessed amount.");
        }

        var result = Fire(AssessmentTrigger.SubmitReport);
        if (result.IsSuccess)
        {
            AssessedAmount = assessedAmount;
            ExcessApplied = excessApplied;
            ReportDocumentId = reportDocumentId;
            AssessedAt = now;
            Raise(new LossAssessed(Id, ClaimId, assessedAmount, excessApplied, now));
        }

        return result;
    }

    public Result AcceptAssessment(Guid acceptedBy)
    {
        var result = Fire(AssessmentTrigger.AcceptAssessment);
        if (result.IsSuccess)
        {
            AcceptedBy = acceptedBy;
        }

        return result;
    }

    /// <summary>Transition "Further work requested".</summary>
    public Result RequestFurtherWork() => Fire(AssessmentTrigger.FurtherWorkRequested);

    private Result Fire(AssessmentTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>States from domain-model.json (general › RecoveryCase).</summary>
public enum RecoveryStatus
{
    Open,
    Pursuing,
    Recovered,
    WrittenOff,
    Closed,
}

public enum RecoveryTrigger
{
    DemandSent,
    RecordRecovery,
    WriteOff,
    CloseRecovery,
}

/// <summary>Money recovered: recorded as a claim financial movement by the Claims Framework (CLM-004).</summary>
public sealed record RecoveryRecorded(Guid RecoveryCaseId, Guid ClaimId, string ClaimNumber, string RecoveryType, decimal Amount, string Currency, string ReserveType,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity SalvageItem (general.salvage_item).</summary>
public sealed class SalvageItem : Entity<Guid>, ITenantScoped
{
    private SalvageItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RecoveryCaseId { get; private set; }

    public string Description { get; private set; } = "";

    public string? DisposalMethod { get; private set; }

    public decimal? Proceeds { get; private set; }

    public long RowVersion { get; private set; }

    internal static SalvageItem Create(Guid caseId, string description) =>
        new() { Id = Guid.CreateVersion7(), RecoveryCaseId = caseId, Description = Guard.MaxLength(Guard.NotNullOrWhiteSpace(description), 200) };

    internal void Dispose(string method, decimal proceeds)
    {
        DisposalMethod = Guard.MaxLength(method, 10);
        Proceeds = proceeds;
    }
}

/// <summary>The RecoveryCase aggregate (general.recovery_case, salvage_item): salvage and third-party recovery (GEN-007).</summary>
public sealed class RecoveryCase : AggregateRoot<Guid>
{
    public static readonly StateMachine<RecoveryStatus, RecoveryTrigger> Lifecycle = new StateMachine<RecoveryStatus, RecoveryTrigger>()
        .Allow(RecoveryStatus.Open, RecoveryTrigger.DemandSent, RecoveryStatus.Pursuing)
        .Allow(RecoveryStatus.Pursuing, RecoveryTrigger.RecordRecovery, RecoveryStatus.Recovered)
        .Allow(RecoveryStatus.Pursuing, RecoveryTrigger.WriteOff, RecoveryStatus.WrittenOff)
        .Allow(RecoveryStatus.Recovered, RecoveryTrigger.CloseRecovery, RecoveryStatus.Closed)
        .Allow(RecoveryStatus.WrittenOff, RecoveryTrigger.CloseRecovery, RecoveryStatus.Closed);

    private readonly List<SalvageItem> _salvage = [];

    private RecoveryCase()
    {
    }

    public Guid ClaimId { get; private set; }

    public string RecoveryType { get; private set; } = "";

    public Guid? CounterpartyPartyId { get; private set; }

    public decimal ExpectedAmount { get; private set; }

    public decimal RecoveredAmount { get; private set; }

    public RecoveryStatus Status { get; private set; }

    public IReadOnlyList<SalvageItem> Salvage => _salvage;

    /// <summary>The currency of the expected and recovered amounts and of the salvage proceeds: the claim's policy's (BR-033).</summary>
    public string Currency { get; private set; } = "";

    public static Result<RecoveryCase> OpenRecovery(Guid id, Guid claimId, string recoveryType, Guid? counterparty, decimal expectedAmount, string currency)
    {
        if (expectedAmount < 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The expected amount is not negative.");
        }

        var recovery = new RecoveryCase
        {
            Id = Guard.NotEmpty(id), ClaimId = Guard.NotEmpty(claimId), RecoveryType = Guard.MaxLength(recoveryType, 15), CounterpartyPartyId = counterparty,
            ExpectedAmount = expectedAmount, Status = RecoveryStatus.Open, Currency = Guard.MaxLength(Guard.NotNullOrWhiteSpace(currency), 3),
        };
        recovery.MarkChanged();
        return recovery;
    }

    /// <summary>Transition "Demand sent".</summary>
    public Result SendDemand() => Fire(RecoveryTrigger.DemandSent);

    public Result AddSalvageItem(string description)
    {
        if (Status is RecoveryStatus.Closed or RecoveryStatus.WrittenOff)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The recovery is {Status}.");
        }

        _salvage.Add(SalvageItem.Create(Id, description));
        MarkChanged();
        return Result.Success();
    }

    public Result DisposeSalvage(Guid itemId, string method, decimal proceeds)
    {
        if (_salvage.SingleOrDefault(s => s.Id == itemId) is not { } item || proceeds < 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The salvage item belongs to this recovery, and its proceeds are not negative.");
        }

        item.Dispose(method, proceeds);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command RecordRecovery: recorded as a claim financial movement by the Claims Framework (CLM-004).</summary>
    public Result RecordRecovery(decimal amount, string claimNumber, string currency, string reserveType, DateTimeOffset now)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A recovery is a positive amount to two decimal places.");
        }

        var result = Fire(RecoveryTrigger.RecordRecovery);
        if (result.IsSuccess)
        {
            RecoveredAmount = amount;
            Raise(new RecoveryRecorded(Id, ClaimId, claimNumber, RecoveryType, amount, currency, reserveType, now));
        }

        return result;
    }

    public Result WriteOff() => Fire(RecoveryTrigger.WriteOff);

    public Result CloseRecovery() => Fire(RecoveryTrigger.CloseRecovery);

    private Result Fire(RecoveryTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
