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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.General.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Retention;

namespace Insys.General.Application;

/// <summary>F026: staff open a retention case for a policy (a cancellation asked for, or another trigger they see).</summary>
public sealed record OpenRetentionCase(Guid PolicyId, [property: OneOf("CANCELLATION_REQUESTED", "NOT_RENEWING", "LAPSE_RISK")] string Trigger, string? Note)
    : ICommand<RetentionCaseView>;

/// <summary>F026: an offer from the tenant's list (general.cfg_retention_offers), within the caller's authority and its uses per policy.</summary>
public sealed record MakeRetentionOffer(Guid CaseId, [property: CodeList(CodeLists.RetentionOffers, "offer_code")] string OfferCode) : ICommand<RetentionCaseView>;

/// <summary>F026: the customer's answer to an offer.</summary>
public sealed record DecideRetentionOffer(Guid CaseId, Guid OfferId, bool Accepted) : ICommand<RetentionCaseView>;

public sealed record CloseRetentionCase(Guid CaseId, [property: OneOf("RETAINED", "LOST", "WITHDRAWN")] string Outcome, string? Note) : ICommand<RetentionCaseView>;

public sealed record GetRetentionCase(Guid CaseId) : IQuery<RetentionCaseView>;

public sealed record ListRetentionCases([property: OneOf("OPEN", "CLOSED")] string? Status, Guid? PolicyId, Guid? BeforeId, int Limit)
    : IQuery<IReadOnlyList<RetentionCaseView>>;

/// <summary>Command: opens a LAPSE_RISK case for a renewal still awaiting acceptance near its end (the daily job).</summary>
public sealed record OpenLapseRiskCase(Guid RenewalId, DateOnly Day) : ICommand<bool>;

public sealed record RetentionOfferView(Guid Id, [property: CodeList(CodeLists.RetentionOffers, "offer_code")] string OfferCode,
    [property: OneOf("DISCOUNT_PERCENT", "EXCESS_CHANGE", "COVER_CHANGE", "PAYMENT_TERMS")] string Kind, decimal? Value, Guid MadeBy, DateTimeOffset MadeAt,
    [property: OneOf("PENDING", "ACCEPTED", "DECLINED")] string Outcome, DateTimeOffset? DecidedAt);

/// <summary>A retention case (F026): what opened it, its offers and their answers, and its outcome.</summary>
public sealed record RetentionCaseView(Guid Id, Guid PolicyId, string PolicyNumber, string ProductCode, Guid? RenewalId,
    [property: OneOf("CANCELLATION_REQUESTED", "NOT_RENEWING", "LAPSE_RISK")] string Trigger, [property: OneOf("OPEN", "CLOSED")] string Status,
    [property: OneOf("RETAINED", "LOST", "WITHDRAWN")] string? Outcome, DateOnly OpenedOn, Guid? OpenedBy, Guid? OwnerId, DateTimeOffset? ClosedAt, Guid? ClosedBy,
    string? Note, IReadOnlyList<RetentionOfferView> Offers, long RowVersion)
{
    public static RetentionCaseView From(RetentionCase c) => new(c.Id, c.PolicyId, c.PolicyNumber, c.ProductCode, c.RenewalId, c.Trigger, c.Status, c.Outcome, c.OpenedOn,
        c.OpenedBy, c.OwnerId, c.ClosedAt, c.ClosedBy, c.Note,
        [.. c.Offers.OrderBy(o => o.MadeAt).Select(o => new RetentionOfferView(o.Id, o.OfferCode, o.Kind, o.Value, o.MadeBy, o.MadeAt, o.Outcome, o.DecidedAt))],
        c.RowVersion);
}

public sealed class OpenRetentionCaseValidator : AbstractValidator<OpenRetentionCase>
{
    public OpenRetentionCaseValidator() => RuleFor(c => c.Note).MaximumLength(500);
}

public sealed class CloseRetentionCaseValidator : AbstractValidator<CloseRetentionCase>
{
    public CloseRetentionCaseValidator() => RuleFor(c => c.Note).MaximumLength(500);
}

/// <summary>Retention cases (F026, addendum §4.3): opened, offers made within authority, the customer's answers, and the outcome.</summary>
internal sealed class RetentionHandlers(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, ITenantContext tenant,
    Identity.Contracts.IUserRoleQuery roles, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<OpenRetentionCase, RetentionCaseView>,
    ICommandHandler<MakeRetentionOffer, RetentionCaseView>,
    ICommandHandler<DecideRetentionOffer, RetentionCaseView>,
    ICommandHandler<CloseRetentionCase, RetentionCaseView>,
    ICommandHandler<OpenLapseRiskCase, bool>,
    IQueryHandler<GetRetentionCase, RetentionCaseView>,
    IQueryHandler<ListRetentionCases, IReadOnlyList<RetentionCaseView>>
{
    private static readonly Error NotFound = Error.NotFound(ErrorCodes.NotFound, "The retention case was not found.");

    public async Task<Result<RetentionCaseView>> Handle(OpenRetentionCase command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        if (await general.OpenRetentionCase(policy.Id, cancellationToken) is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {policy.PolicyNumber} already has an open retention case.");
        }

        var renewal = command.Trigger == RetentionTriggers.CancellationRequested ? null : await general.LatestRenewal(policy.Id, cancellationToken);
        var opened = RetentionCase.OpenCase(Guid.CreateVersion7(), policy.Id, policy.PolicyNumber, policy.ProductCode, renewal?.Id, command.Trigger, user.Value, command.Note,
            businessDate.Today, clock.UtcNow);
        general.Add(opened);
        audit.Record(new AuditEntry("GENERAL", "OpenRetentionCase", nameof(RetentionCase), opened.Id.ToString(),
            new Dictionary<string, string?> { ["policy"] = policy.Id.ToString(), ["trigger"] = command.Trigger }));
        return RetentionCaseView.From(opened);
    }

    public async Task<Result<RetentionCaseView>> Handle(MakeRetentionOffer command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.RetentionCase(command.CaseId, cancellationToken) is not { } retention)
        {
            return NotFound;
        }

        if (await codes.RetentionOffer(command.OfferCode, businessDate.Today, cancellationToken) is not { } terms)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.OfferCode} is not a retention offer in force.");
        }

        // F026: within the caller's authority: the offer's role, when it names one, is one of the caller's roles.
        if (terms.AuthorityRole is { } role
            && !(await roles.EffectiveRoles(tenant.RequireTenantId(), user.Value, cancellationToken)).Contains(role, StringComparer.Ordinal))
        {
            return Error.Forbidden(GeneralErrorCodes.RetentionOfferAuthority, $"{terms.OfferCode} is offered by holders of the {role} role.");
        }

        var made = retention.MakeOffer(terms, await general.RetentionOfferUses(retention.PolicyId, terms.OfferCode, cancellationToken), user.Value, clock.UtcNow);
        audit.Record(new AuditEntry("GENERAL", "MakeRetentionOffer", nameof(RetentionCase), retention.Id.ToString(),
            new Dictionary<string, string?> { ["offer"] = terms.OfferCode, ["kind"] = terms.Kind }));
        return made.IsFailure ? made.Error : RetentionCaseView.From(retention);
    }

    public Task<Result<RetentionCaseView>> Handle(DecideRetentionOffer command, CancellationToken cancellationToken) =>
        Change(command.CaseId, "DecideRetentionOffer", (c, user) => c.DecideOffer(command.OfferId, command.Accepted, user, clock.UtcNow),
            new Dictionary<string, string?> { ["offer"] = command.OfferId.ToString(), ["accepted"] = command.Accepted.ToString() }, cancellationToken);

    public Task<Result<RetentionCaseView>> Handle(CloseRetentionCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, "CloseRetentionCase", (c, user) => c.Close(command.Outcome, user, command.Note, clock.UtcNow),
            new Dictionary<string, string?> { ["outcome"] = command.Outcome }, cancellationToken);

    public async Task<Result<bool>> Handle(OpenLapseRiskCase command, CancellationToken cancellationToken)
    {
        if (await general.Renewal(command.RenewalId, cancellationToken) is not { AwaitsAcceptance: true } renewal
            || await general.OpenRetentionCase(renewal.PolicyId, cancellationToken) is not null)
        {
            return false;
        }

        general.Add(RetentionCase.OpenCase(Guid.CreateVersion7(), renewal.PolicyId, renewal.PolicyNumber, renewal.ProductCode, renewal.Id, RetentionTriggers.LapseRisk, null,
            null, command.Day, clock.UtcNow));
        return true;
    }

    public async Task<Result<RetentionCaseView>> Handle(GetRetentionCase query, CancellationToken cancellationToken) =>
        await general.RetentionCase(query.CaseId, cancellationToken) is { } retention ? RetentionCaseView.From(retention) : NotFound;

    public async Task<Result<IReadOnlyList<RetentionCaseView>>> Handle(ListRetentionCases query, CancellationToken cancellationToken) =>
        (await general.RetentionCases(query.Status, query.PolicyId, query.BeforeId, query.Limit, cancellationToken)).Select(RetentionCaseView.From).ToList();

    private async Task<Result<RetentionCaseView>> Change(Guid caseId, string action, Func<RetentionCase, Guid, Result> change, Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.RetentionCase(caseId, cancellationToken) is not { } retention)
        {
            return NotFound;
        }

        var result = change(retention, user.Value);
        audit.Record(new AuditEntry("GENERAL", action, nameof(RetentionCase), retention.Id.ToString(), details));
        return result.IsFailure ? result.Error : RetentionCaseView.From(retention);
    }
}

/// <summary>
/// F026: a declined renewal opens a NOT_RENEWING retention case (unless one is open); an accepted discount on a renewal case
/// lowers the renewal's offered premium and accepts it. Any other accepted offer is made by staff (an endorsement mid-term,
/// or the renewal's terms), who close the case.
/// </summary>
public sealed class RetentionPolicies(IGeneralRepository general, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<GeneralRenewalDeclined>,
    IIntegrationEventHandler<GeneralRetentionOfferAccepted>
{
    public async Task Handle(GeneralRenewalDeclined declined, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.OpenRetentionCase(declined.PolicyId, cancellationToken) is not null
            || await general.Renewal(declined.RenewalId, cancellationToken) is not { } renewal)
        {
            return;
        }

        general.Add(RetentionCase.OpenCase(Guid.CreateVersion7(), renewal.PolicyId, renewal.PolicyNumber, renewal.ProductCode, renewal.Id, RetentionTriggers.NotRenewing,
            null, null, businessDate.Today, clock.UtcNow));
    }

    public async Task Handle(GeneralRetentionOfferAccepted accepted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (accepted.Kind != RetentionOfferKinds.DiscountPercent || accepted.RenewalId is not { } renewalId || accepted.Value is not { } percent
            || await general.Renewal(renewalId, cancellationToken) is not { } renewal)
        {
            return;
        }

        // A renewal that moved on (renewed, lapsed, not renewed) is not changed: the case's owner sees the offer accepted and settles it by hand.
        renewal.ApplyRetentionDiscount(percent, accepted.DecidedBy, clock.UtcNow);
    }
}
