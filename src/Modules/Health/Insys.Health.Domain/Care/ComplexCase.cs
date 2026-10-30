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

namespace Insys.Health.Domain.Care;

public static class ComplexCaseStatus
{
    public const string Open = "OPEN";
    public const string Active = "ACTIVE";
    public const string OnHold = "ON_HOLD";
    public const string Closed = "CLOSED";
}

public static class ComplexCaseOrigin
{
    public const string Membership = "MEMBERSHIP";
    public const string Utilisation = "UTILISATION";
    public const string HighCost = "HIGH_COST";
}

public static class CarePlanItemKind
{
    public const string Goal = "GOAL";
    public const string Activity = "ACTIVITY";
}

public static class CarePlanItemStatus
{
    public const string Open = "OPEN";
    public const string Done = "DONE";
    public const string Missed = "MISSED";
    public const string Changed = "CHANGED";
}

/// <summary>A care plan's activity is past its date (F037). References only: its text is clinical and stays here (HLT-008).</summary>
public sealed record CareActivityOverdue(Guid CaseId, string CaseNumber, Guid ActivityId, Guid? CaseManagerId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>One person's time as a case's manager (F035): who, from when to when, who assigned them and why. Audited, kept.</summary>
public sealed class ComplexCaseAssignment : Entity<Guid>, ITenantScoped
{
    private ComplexCaseAssignment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ComplexCaseId { get; private set; }

    public Guid CaseManagerId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public Guid AssignedBy { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    internal static ComplexCaseAssignment Create(Guid caseId, Guid manager, Guid by, string? reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), ComplexCaseId = caseId, CaseManagerId = manager, AssignedAt = now, AssignedBy = by, Reason = reason,
    };

    internal void End(DateTimeOffset now) => EndedAt = now;
}

/// <summary>A goal or a dated activity of a care plan (F036, F037).</summary>
public sealed class CarePlanItem : Entity<Guid>, ITenantScoped
{
    private CarePlanItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CarePlanId { get; private set; }

    public string Kind { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string? Responsible { get; private set; }

    public DateOnly? DueOn { get; private set; }

    public string Status { get; private set; } = CarePlanItemStatus.Open;

    public string? ProgressNote { get; private set; }

    public bool OverdueRaised { get; private set; }

    internal static CarePlanItem Create(Guid planId, CarePlanItemInput input) => new()
    {
        Id = Guid.CreateVersion7(), CarePlanId = planId, Kind = input.Kind, Description = Guard.MaxLength(Guard.NotNullOrWhiteSpace(input.Description), 1000),
        Responsible = input.Responsible is null ? null : Guard.MaxLength(input.Responsible, 120), DueOn = input.DueOn,
    };

    internal void Record(string status, string? note)
    {
        Status = status;
        ProgressNote = note is null ? null : Guard.MaxLength(note, 1000);
    }

    internal void MarkOverdueRaised() => OverdueRaised = true;
}

public sealed record CarePlanItemInput(string Kind, string Description, string? Responsible, DateOnly? DueOn);

/// <summary>One version of a case's care plan. Changing a plan keeps the earlier version (F036).</summary>
public sealed class CarePlan : Entity<Guid>, ITenantScoped
{
    private readonly List<CarePlanItem> _items = [];

    private CarePlan()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ComplexCaseId { get; private set; }

    public int PlanVersion { get; private set; }

    public bool IsCurrent { get; private set; }

    public string? ChangeReason { get; private set; }

    public IReadOnlyList<CarePlanItem> Items => _items;

    internal static CarePlan Create(Guid caseId, int version, string? reason, IReadOnlyList<CarePlanItemInput> items)
    {
        var plan = new CarePlan { Id = Guid.CreateVersion7(), ComplexCaseId = caseId, PlanVersion = version, IsCurrent = true, ChangeReason = reason };
        plan._items.AddRange(items.Select(i => CarePlanItem.Create(plan.Id, i)));
        return plan;
    }

    internal void Supersede() => IsCurrent = false;
}

/// <summary>A contact about the case (F036). Clinical, restricted. Shared with another provider, it names the member's consent.</summary>
public sealed class CareCoordinationNote : Entity<Guid>, ITenantScoped
{
    private CareCoordinationNote()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ComplexCaseId { get; private set; }

    public string ContactType { get; private set; } = "";

    public string? ContactRef { get; private set; }

    public string Note { get; private set; } = "";

    public string? SharedWithProviderId { get; private set; }

    public string? ConsentReference { get; private set; }

    public Guid WrittenBy { get; private set; }

    public DateTimeOffset WrittenAt { get; private set; }

    internal static CareCoordinationNote Create(Guid caseId, string contactType, string? contactRef, string note, string? sharedWith, string? consent, Guid by, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), ComplexCaseId = caseId, ContactType = contactType, ContactRef = contactRef is null ? null : Guard.MaxLength(contactRef, 120),
        Note = Guard.MaxLength(Guard.NotNullOrWhiteSpace(note), 2000), SharedWithProviderId = sharedWith, ConsentReference = consent, WrittenBy = by, WrittenAt = now,
    };
}

/// <summary>
/// The ComplexCase aggregate (health.complex_case; F034 to F037, OQ-140). It holds the clinical record of coordinating a member's
/// care: it makes no clinical decision and authorises nothing (addendum §4.4). Its current case manager reads the clinical detail
/// by being assigned, and loses that when reassigned (OQ-143, addition 2). Never closed automatically.
/// </summary>
public sealed class ComplexCase : AggregateRoot<Guid>
{
    private readonly List<ComplexCaseAssignment> _assignments = [];
    private readonly List<CarePlan> _plans = [];
    private readonly List<CareCoordinationNote> _notes = [];

    private ComplexCase()
    {
    }

    public string CaseNumber { get; private set; } = "";

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public string OpenedFrom { get; private set; } = ComplexCaseOrigin.Membership;

    public Guid? SourceId { get; private set; }

    public string ConditionCategory { get; private set; } = "";

    public string Status { get; private set; } = ComplexCaseStatus.Open;

    public Guid? CaseManagerId { get; private set; }

    public string? CloseReason { get; private set; }

    public IReadOnlyList<ComplexCaseAssignment> Assignments => _assignments;

    public IReadOnlyList<CarePlan> Plans => _plans;

    public IReadOnlyList<CareCoordinationNote> Notes => _notes;

    public CarePlan? CurrentPlan => _plans.SingleOrDefault(p => p.IsCurrent);

    public static ComplexCase Open(Guid id, string caseNumber, Guid membershipId, Guid patientPartyId, string openedFrom, Guid? sourceId, string conditionCategory)
    {
        var opened = new ComplexCase
        {
            Id = Guard.NotEmpty(id), CaseNumber = Guard.MaxLength(caseNumber, 25), MembershipId = Guard.NotEmpty(membershipId), PatientPartyId = Guard.NotEmpty(patientPartyId),
            OpenedFrom = openedFrom, SourceId = sourceId, ConditionCategory = Guard.MaxLength(Guard.NotNullOrWhiteSpace(conditionCategory), 120),
        };
        opened.MarkChanged();
        return opened;
    }

    /// <summary>F035: the first assignment makes the case active. A later one needs a reason and ends the previous manager's assignment.</summary>
    public Result AssignManager(Guid manager, Guid by, string? reason, DateTimeOffset now)
    {
        if (Status == ComplexCaseStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is closed.");
        }

        if (CaseManagerId == manager)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"That person already manages case {CaseNumber}.");
        }

        if (CaseManagerId is not null && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Reassigning a case needs a reason.");
        }

        _assignments.SingleOrDefault(a => a.EndedAt is null)?.End(now);
        _assignments.Add(ComplexCaseAssignment.Create(Id, manager, by, reason is null ? null : Guard.MaxLength(reason, 500), now));
        CaseManagerId = manager;
        if (Status == ComplexCaseStatus.Open)
        {
            Status = ComplexCaseStatus.Active;
        }

        MarkChanged();
        return Result.Success();
    }

    public Result Hold() => Move(ComplexCaseStatus.Active, ComplexCaseStatus.OnHold);

    public Result Resume() => Move(ComplexCaseStatus.OnHold, ComplexCaseStatus.Active);

    private Result Move(string from, string to)
    {
        if (Status != from)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is {Status}.");
        }

        Status = to;
        MarkChanged();
        return Result.Success();
    }

    public Result Close(string reason)
    {
        if (Status == ComplexCaseStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is closed.");
        }

        CloseReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500);
        Status = ComplexCaseStatus.Closed;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>F036: sets the plan. A change makes a new version and keeps the last; it needs a reason, and unfinished activities carry only if restated.</summary>
    public Result SetPlan(IReadOnlyList<CarePlanItemInput> items, string? reason)
    {
        if (Status == ComplexCaseStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is closed.");
        }

        if (items.Count == 0 || items.Any(i => i.Kind is not (CarePlanItemKind.Goal or CarePlanItemKind.Activity) || (i.Kind == CarePlanItemKind.Activity && i.DueOn is null)))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A plan has goals and dated activities.");
        }

        if (CurrentPlan is { } current)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Error.Validation(ErrorCodes.ValidationFailed, "Changing a plan needs a reason.");
            }

            current.Supersede();
        }

        _plans.Add(CarePlan.Create(Id, _plans.Count + 1, reason is null ? null : Guard.MaxLength(reason, 500), items));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>F037: progress on an item of the current plan: done, missed or changed.</summary>
    public Result RecordProgress(Guid itemId, string status, string? note)
    {
        if (status is not (CarePlanItemStatus.Done or CarePlanItemStatus.Missed or CarePlanItemStatus.Changed)
            || CurrentPlan?.Items.SingleOrDefault(i => i.Id == itemId) is not { } item)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The current plan has no such item.");
        }

        item.Record(status, note);
        MarkChanged();
        return Result.Success();
    }

    public Result AddNote(string contactType, string? contactRef, string note, string? sharedWithProviderId, string? consentReference, Guid by, DateTimeOffset now)
    {
        if (Status == ComplexCaseStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Case {CaseNumber} is closed.");
        }

        if (sharedWithProviderId is not null && string.IsNullOrWhiteSpace(consentReference))
        {
            return Error.Validation(HealthErrorCodes.CareConsentRequired, "A note shared with another provider names the member's consent.");
        }

        _notes.Add(CareCoordinationNote.Create(Id, contactType, contactRef, note, sharedWithProviderId, consentReference, by, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>F037: each open activity of the current plan past its date raises one task, once. A case on hold or closed raises none.</summary>
    public int RaiseOverdue(DateOnly today, DateTimeOffset now)
    {
        if (Status != ComplexCaseStatus.Active || CurrentPlan is not { } plan)
        {
            return 0;
        }

        var raised = 0;
        foreach (var item in plan.Items.Where(i => i.Kind == CarePlanItemKind.Activity && i.Status == CarePlanItemStatus.Open && !i.OverdueRaised && i.DueOn < today))
        {
            item.MarkOverdueRaised();
            Raise(new CareActivityOverdue(Id, CaseNumber, item.Id, CaseManagerId, now));
            raised++;
        }

        MarkChanged();
        return raised;
    }
}

public static class ReferralStatus
{
    public const string Requested = "REQUESTED";
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
    public const string Used = "USED";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// The ClinicalReferral aggregate (F038). Recorded only in this wave: it does not affect a claim (OQ-144). Its reason is clinical
/// and is read through the audited clinical route.
/// </summary>
public sealed class ClinicalReferral : AggregateRoot<Guid>
{
    private ClinicalReferral()
    {
    }

    public string ReferralNumber { get; private set; } = "";

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public string ReferringProviderId { get; private set; } = "";

    public string? ReferredProviderId { get; private set; }

    public string? ReferredSpecialty { get; private set; }

    public string ClinicalReason { get; private set; } = "";

    /// <summary>The member's consent to the receiving provider reading the reason, as for a shared care note. Without it, only the referring provider and the clinical function may.</summary>
    public string? ConsentReference { get; private set; }

    public DateTimeOffset? ConsentWithdrawnAt { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly ValidTo { get; private set; }

    public int VisitsAllowed { get; private set; }

    public int VisitsUsed { get; private set; }

    public Guid? ComplexCaseId { get; private set; }

    public Guid? PreauthorisationId { get; private set; }

    public string Status { get; private set; } = ReferralStatus.Requested;

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DeclineReason { get; private set; }

    public static Result<ClinicalReferral> Request(Guid id, string number, Guid membershipId, Guid patientPartyId, string referringProviderId, string? referredProviderId,
        string? referredSpecialty, string clinicalReason, DateOnly validFrom, DateOnly validTo, int visitsAllowed, Guid? complexCaseId, Guid? preauthorisationId, string? consentReference = null)
    {
        if (referredProviderId is null && string.IsNullOrWhiteSpace(referredSpecialty))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A referral is to a provider or a specialty.");
        }

        if (validTo < validFrom || visitsAllowed <= 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A referral needs valid dates and at least one visit.");
        }

        var referral = new ClinicalReferral
        {
            Id = Guard.NotEmpty(id), ReferralNumber = Guard.MaxLength(number, 25), MembershipId = Guard.NotEmpty(membershipId), PatientPartyId = Guard.NotEmpty(patientPartyId),
            ReferringProviderId = Guard.MaxLength(referringProviderId, 15), ReferredProviderId = referredProviderId, ReferredSpecialty = referredSpecialty,
            ClinicalReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(clinicalReason), 1000), ValidFrom = validFrom, ValidTo = validTo, VisitsAllowed = visitsAllowed,
            ComplexCaseId = complexCaseId, PreauthorisationId = preauthorisationId, ConsentReference = string.IsNullOrWhiteSpace(consentReference) ? null : Guard.MaxLength(consentReference, 120),
        };
        referral.MarkChanged();
        return referral;
    }

    /// <summary>The member's consent recorded after the referral was made. A referral past its life takes none.</summary>
    public Result RecordConsent(string consentReference)
    {
        if (Status is ReferralStatus.Declined or ReferralStatus.Expired)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Referral {ReferralNumber} is {Status}.");
        }

        ConsentReference = Guard.MaxLength(Guard.NotNullOrWhiteSpace(consentReference), 120);
        ConsentWithdrawnAt = null;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The member withdrew consent: the receiving provider's access to the reason ends at once. The referring provider's does not depend on it.</summary>
    public Result WithdrawConsent(DateTimeOffset now)
    {
        if (ConsentReference is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Referral {ReferralNumber} names no consent.");
        }

        ConsentReference = null;
        ConsentWithdrawnAt = now;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Whether a provider may read the reason: the one who wrote it, or the one it is referred to with the member's consent (Ade, 30 October 2026).</summary>
    public bool ReasonOpenTo(string providerId) => providerId == ReferringProviderId || (providerId == ReferredProviderId && ConsentReference is not null);

    public bool Concerns(string providerId) => providerId == ReferringProviderId || providerId == ReferredProviderId;

    public Result Decide(bool approved, string? reason, Guid by, DateTimeOffset now)
    {
        if (Status != ReferralStatus.Requested)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Referral {ReferralNumber} is {Status}.");
        }

        if (!approved && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Declining a referral needs a reason.");
        }

        Status = approved ? ReferralStatus.Approved : ReferralStatus.Declined;
        DeclineReason = approved || reason is null ? null : Guard.MaxLength(reason, 500);
        (DecidedBy, DecidedAt) = (by, now);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>A visit under the referral, within its dates. USED when none is left.</summary>
    public Result RecordVisit(DateOnly day)
    {
        if (Status != ReferralStatus.Approved || day < ValidFrom || day > ValidTo)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Referral {ReferralNumber} is {Status}, valid {ValidFrom:yyyy-MM-dd} to {ValidTo:yyyy-MM-dd}.");
        }

        VisitsUsed++;
        if (VisitsUsed == VisitsAllowed)
        {
            Status = ReferralStatus.Used;
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>The daily job: a referral still waiting or approved past its date expires.</summary>
    public Result Expire(DateOnly today)
    {
        if (Status is not (ReferralStatus.Requested or ReferralStatus.Approved) || ValidTo >= today)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Referral {ReferralNumber} is {Status}, valid to {ValidTo:yyyy-MM-dd}.");
        }

        Status = ReferralStatus.Expired;
        MarkChanged();
        return Result.Success();
    }
}
