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
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Domain.Care;

public static class ChronicStatus
{
    public const string Requested = "REQUESTED";
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
    public const string Ended = "ENDED";
}

public sealed record EntitlementRequest(string? ProductCode, string? ActiveIngredient, int MonthlyQuantity);

/// <summary>The decline's rights notice (OQ-150). Never names the condition; the recipient is the patient, or the principal for a minor.</summary>
public sealed record ChronicRegistrationDeclinedRightsDue(Guid RegistrationId, string RegistrationNumber, Guid RecipientPartyId, string? ProductCode, string? OrgNodeCode, string Currency, DateOnly DecidedOn,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity ChronicEntitlement (health.chronic_entitlement): a medicine allowed under a registration, with its monthly quantity.</summary>
public sealed class ChronicEntitlementItem : Entity<Guid>, ITenantScoped
{
    private ChronicEntitlementItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RegistrationId { get; private set; }

    public string? ProductCode { get; private set; }

    public string? ActiveIngredient { get; private set; }

    public int MonthlyQuantity { get; private set; }

    public long RowVersion { get; private set; }

    internal static ChronicEntitlementItem Create(Guid registrationId, EntitlementRequest request) => new()
    {
        Id = Guid.CreateVersion7(), RegistrationId = registrationId, ProductCode = request.ProductCode, ActiveIngredient = request.ActiveIngredient, MonthlyQuantity = request.MonthlyQuantity,
    };

    public ChronicEntitlement ToTerms() => new(ProductCode, ActiveIngredient, MonthlyQuantity);
}

/// <summary>
/// The ChronicRegistration aggregate (F083 to F085): a member's registered condition, requested by a provider or staff with a supporting note and decided by a
/// person with the clinical function, not the requester (OQ-150, SRS-002). Approved, its claims draw on the chronic benefit category between its dates.
/// Clinical: the condition, diagnosis and note appear only in the audited clinical read (HLT-008), never in events, logs or notices.
/// </summary>
public sealed class ChronicRegistration : AggregateRoot<Guid>
{
    private readonly List<ChronicEntitlementItem> _entitlements = [];

    private ChronicRegistration()
    {
    }

    public string RegistrationNumber { get; private set; } = "";

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public string ConditionCode { get; private set; } = "";

    public string? DiagnosisCode { get; private set; }

    public string SupportingNote { get; private set; } = "";

    public Guid RequestedBy { get; private set; }

    public string? RequestingProviderId { get; private set; }

    public string Status { get; private set; } = ChronicStatus.Requested;

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public DateOnly? ReviewOn { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DeclineReason { get; private set; }

    public IReadOnlyList<ChronicEntitlementItem> Entitlements => _entitlements;

    public bool InForce(DateOnly day) => Status == ChronicStatus.Approved && ValidFrom <= day && (ValidTo is null || day <= ValidTo);

    public static ChronicRegistration Request(Guid id, string number, Guid membershipId, Guid patientPartyId, string conditionCode, string? diagnosisCode, string supportingNote, Guid requestedBy,
        string? requestingProviderId)
    {
        var registration = new ChronicRegistration
        {
            Id = Guard.NotEmpty(id), RegistrationNumber = Guard.MaxLength(number, 25), MembershipId = Guard.NotEmpty(membershipId), PatientPartyId = Guard.NotEmpty(patientPartyId),
            ConditionCode = Guard.MaxLength(conditionCode, 15), DiagnosisCode = diagnosisCode, SupportingNote = Guard.MaxLength(Guard.NotNullOrWhiteSpace(supportingNote), 2000),
            RequestedBy = Guard.NotEmpty(requestedBy), RequestingProviderId = requestingProviderId,
        };
        registration.MarkChanged();
        return registration;
    }

    public Result Approve(DateOnly validFrom, DateOnly? validTo, DateOnly? reviewOn, IReadOnlyList<EntitlementRequest> entitlements, Guid decidedBy, DateTimeOffset now)
    {
        var decidable = Decidable(decidedBy);
        if (decidable.IsFailure)
        {
            return decidable;
        }

        if (validTo < validFrom || entitlements.Any(e => e.MonthlyQuantity <= 0 || (e.ProductCode is null && e.ActiveIngredient is null)))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An approval needs valid dates, and each entitlement a medicine and a monthly quantity above zero.");
        }

        Status = ChronicStatus.Approved;
        ValidFrom = validFrom;
        ValidTo = validTo;
        ReviewOn = reviewOn;
        DecidedBy = decidedBy;
        DecidedAt = now;
        _entitlements.AddRange(entitlements.Select(e => ChronicEntitlementItem.Create(Id, e)));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Declined, with a reason and the rights notice to <paramref name="recipientPartyId"/>.</summary>
    public Result Decline(string reason, Guid decidedBy, Guid recipientPartyId, string? productCode, string? orgNodeCode, string currency, DateOnly day, DateTimeOffset now)
    {
        var decidable = Decidable(decidedBy);
        if (decidable.IsFailure)
        {
            return decidable;
        }

        Status = ChronicStatus.Declined;
        DeclineReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500);
        DecidedBy = decidedBy;
        DecidedAt = now;
        Raise(new ChronicRegistrationDeclinedRightsDue(Id, RegistrationNumber, recipientPartyId, productCode, orgNodeCode, currency, day, now));
        return Result.Success();
    }

    /// <summary>Ends an approved registration from a day: its claims return to day-to-day benefits after it. A correction, not a deletion.</summary>
    public Result End(DateOnly lastDay)
    {
        if (Status != ChronicStatus.Approved || lastDay < ValidFrom)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Registration {RegistrationNumber} is {Status}.");
        }

        Status = ChronicStatus.Ended;
        ValidTo = lastDay;
        MarkChanged();
        return Result.Success();
    }

    private Result Decidable(Guid decidedBy)
    {
        if (Status != ChronicStatus.Requested)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Registration {RegistrationNumber} is {Status}.");
        }

        return decidedBy == RequestedBy ? Error.Conflict(ErrorCodes.MakerCheckerSamePerson, "The person who requested a registration cannot decide it (SRS-002).") : Result.Success();
    }
}
