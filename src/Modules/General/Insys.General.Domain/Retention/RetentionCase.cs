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

namespace Insys.General.Domain.Retention;

/// <summary>What opened a retention case (F026, addendum §4.3).</summary>
public static class RetentionTriggers
{
    public const string CancellationRequested = "CANCELLATION_REQUESTED";
    public const string NotRenewing = "NOT_RENEWING";
    public const string LapseRisk = "LAPSE_RISK";
}

public static class RetentionOutcomes
{
    public const string Retained = "RETAINED";
    public const string Lost = "LOST";
    public const string Withdrawn = "WITHDRAWN";
}

/// <summary>The kinds of retention offer (general.cfg_retention_offers.kind).</summary>
public static class RetentionOfferKinds
{
    public const string DiscountPercent = "DISCOUNT_PERCENT";
    public const string ExcessChange = "EXCESS_CHANGE";
    public const string CoverChange = "COVER_CHANGE";
    public const string PaymentTerms = "PAYMENT_TERMS";
}

public static class RetentionOfferOutcomes
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Declined = "DECLINED";
}

/// <summary>A retention offer the tenant allows (general.cfg_retention_offers).</summary>
public sealed record RetentionOfferTerms(string OfferCode, string? ProductCode, string Kind, decimal? Value, int MaxUsesPerPolicy, string? AuthorityRole);

public sealed record RetentionCaseOpened(Guid CaseId, Guid PolicyId, string PolicyNumber, string ProductCode, string Trigger, Guid? RenewalId, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record RetentionOfferMade(Guid CaseId, Guid OfferId, Guid PolicyId, string ProductCode, string Kind, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The customer accepted an offer: a discount on a renewal changes its terms; any other change is made by staff (an endorsement, or the renewal's terms).</summary>
public sealed record RetentionOfferAccepted(Guid CaseId, Guid OfferId, Guid PolicyId, string ProductCode, string Kind, decimal? Value, Guid? RenewalId, Guid DecidedBy,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RetentionCaseClosed(Guid CaseId, Guid PolicyId, string ProductCode, string Outcome, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An offer made on a retention case, and the customer's answer.</summary>
public sealed class RetentionOffer : Entity<Guid>, ITenantScoped
{
    private RetentionOffer()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RetentionCaseId { get; private set; }

    public string OfferCode { get; private set; } = "";

    public string Kind { get; private set; } = "";

    public decimal? Value { get; private set; }

    public Guid MadeBy { get; private set; }

    public DateTimeOffset MadeAt { get; private set; }

    public string Outcome { get; private set; } = RetentionOfferOutcomes.Pending;

    public DateTimeOffset? DecidedAt { get; private set; }

    internal static RetentionOffer Create(Guid caseId, RetentionOfferTerms terms, Guid madeBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), RetentionCaseId = caseId, OfferCode = Guard.MaxLength(terms.OfferCode, 20), Kind = terms.Kind, Value = terms.Value,
        MadeBy = Guard.NotEmpty(madeBy), MadeAt = now,
    };

    internal void Decide(bool accepted, DateTimeOffset now) => (Outcome, DecidedAt) = (accepted ? RetentionOfferOutcomes.Accepted : RetentionOfferOutcomes.Declined, now);
}

/// <summary>
/// The RetentionCase aggregate (general.retention_case, retention_offer; addendum §4.3, F026): opened when the policyholder
/// asks to cancel, declines a renewal, or a renewal is at risk of lapsing. Staff record offers within their authority; an
/// accepted offer changes the renewal (a discount) or is made by staff; the case closes RETAINED, LOST or WITHDRAWN. A
/// cancellation is processed as asked: the case only records whether the customer changed their mind.
/// </summary>
public sealed class RetentionCase : AggregateRoot<Guid>
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";

    private readonly List<RetentionOffer> _offers = [];

    private RetentionCase()
    {
    }

    public Guid PolicyId { get; private set; }

    public string PolicyNumber { get; private set; } = "";

    public string ProductCode { get; private set; } = "";

    public Guid? RenewalId { get; private set; }

    public string Trigger { get; private set; } = "";

    public string Status { get; private set; } = Open;

    public string? Outcome { get; private set; }

    public DateOnly OpenedOn { get; private set; }

    public Guid? OpenedBy { get; private set; }

    public Guid? OwnerId { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public string? Note { get; private set; }

    public IReadOnlyList<RetentionOffer> Offers => _offers;

    /// <param name="openedBy">The person who opened it, or null when the platform did (a declined renewal, a lapse risk).</param>
    public static RetentionCase OpenCase(Guid id, Guid policyId, string policyNumber, string productCode, Guid? renewalId, string trigger, Guid? openedBy, string? note,
        DateOnly today, DateTimeOffset now)
    {
        if (trigger is not (RetentionTriggers.CancellationRequested or RetentionTriggers.NotRenewing or RetentionTriggers.LapseRisk))
        {
            throw new ArgumentException($"{trigger} is not a retention trigger.", nameof(trigger));
        }

        var opened = new RetentionCase
        {
            Id = Guard.NotEmpty(id), PolicyId = Guard.NotEmpty(policyId), PolicyNumber = Guard.MaxLength(policyNumber, 30), ProductCode = Guard.MaxLength(productCode, 15),
            RenewalId = renewalId, Trigger = trigger, OpenedOn = today, OpenedBy = openedBy, OwnerId = openedBy, Note = note is null ? null : Guard.MaxLength(note, 500),
        };
        opened.Raise(new RetentionCaseOpened(opened.Id, policyId, opened.PolicyNumber, opened.ProductCode, trigger, renewalId, now));
        opened.MarkChanged();
        return opened;
    }

    /// <summary>
    /// An offer from the tenant's list, for this product, by a person within its authority (the caller checks the role), at
    /// most its uses per policy (<paramref name="usedOnPolicy"/>: the times it was made on the policy before, on any case).
    /// </summary>
    public Result<Guid> MakeOffer(RetentionOfferTerms terms, int usedOnPolicy, Guid by, DateTimeOffset now)
    {
        if (Status != Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The retention case is closed.");
        }

        if (terms.ProductCode is { } product && product != ProductCode)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{terms.OfferCode} is not offered on {ProductCode}.");
        }

        if (usedOnPolicy >= terms.MaxUsesPerPolicy)
        {
            return Error.Conflict(GeneralErrorCodes.RetentionOfferUsedUp, $"{terms.OfferCode} has been offered on policy {PolicyNumber} {usedOnPolicy} time(s), its most.");
        }

        var offer = RetentionOffer.Create(Id, terms, by, now);
        _offers.Add(offer);
        OwnerId ??= by;
        Raise(new RetentionOfferMade(Id, offer.Id, PolicyId, ProductCode, offer.Kind, now));
        MarkChanged();
        return offer.Id;
    }

    /// <summary>The customer's answer to a pending offer.</summary>
    public Result DecideOffer(Guid offerId, bool accepted, Guid by, DateTimeOffset now)
    {
        if (Status != Open || _offers.SingleOrDefault(o => o.Id == offerId) is not { Outcome: RetentionOfferOutcomes.Pending } offer)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No such offer waits for an answer on an open case.");
        }

        offer.Decide(accepted, now);
        if (accepted)
        {
            Raise(new RetentionOfferAccepted(Id, offer.Id, PolicyId, ProductCode, offer.Kind, offer.Value, RenewalId, by, now));
        }

        MarkChanged();
        return Result.Success();
    }

    public Result Close(string outcome, Guid by, string? note, DateTimeOffset now)
    {
        if (Status != Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The retention case is already closed.");
        }

        if (outcome is not (RetentionOutcomes.Retained or RetentionOutcomes.Lost or RetentionOutcomes.Withdrawn))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "outcome is RETAINED, LOST or WITHDRAWN.");
        }

        (Status, Outcome, ClosedBy, ClosedAt) = (Closed, outcome, by, now);
        if (note is not null)
        {
            Note = Guard.MaxLength(note, 500);
        }

        Raise(new RetentionCaseClosed(Id, PolicyId, ProductCode, outcome, now));
        MarkChanged();
        return Result.Success();
    }
}
