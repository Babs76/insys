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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.General.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Renewals;

namespace Insys.General.Application;

/// <summary>Wave 4 §4.4: staff record the customer's acceptance of an offered renewal.</summary>
public sealed record AcceptRenewal(Guid RenewalId) : ICommand<RenewalView>;

/// <summary>Wave 4 §4.4: staff record the customer's decline; the policy expires at its period end.</summary>
public sealed record DeclineRenewal(Guid RenewalId) : ICommand<RenewalView>;

/// <summary>Wave 4 §4.6: the member or the broker servicing the policy accepts its renewal invitation.</summary>
public sealed record PortalAcceptRenewal(Guid RenewalId) : ICommand<PortalRenewalView>;

public sealed record PortalDeclineRenewal(Guid RenewalId) : ICommand<PortalRenewalView>;

public sealed record ListPortalRenewals(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalRenewalView>>;

public sealed record GetPortalRenewal(Guid RenewalId) : IQuery<PortalRenewalView>;

/// <summary>
/// Wave 4 §4.3 (F025): a person proposes not renewing a policy, with a reason (general.cfg_non_renewal_reasons). A reason that
/// requires approval goes to a second person (Workflow approval GENERAL_NON_RENEWAL); otherwise it is decided at once.
/// </summary>
public sealed record ProposeNonRenewal(Guid PolicyId, [property: CodeList(CodeLists.NonRenewalReasons, "reason_code")] string ReasonCode) : ICommand<RenewalView>;

/// <summary>
/// A renewal as the member or broker sees it (§4.6): the invitation's terms and the answer, never the review, the claims
/// factor or a referral. A renewal the insurer stopped says only that it will not be renewed, and when cover ends.
/// </summary>
public sealed record PortalRenewalView(Guid Id, Guid PolicyId, string PolicyNumber, DateOnly PeriodEnd,
    [property: OneOf("OFFERED", "ACCEPTED", "RENEWED", "DECLINED_BY_CUSTOMER", "NOT_RENEWED_BY_INSURER", "LAPSED")] string Status, string Currency,
    decimal CurrentPremium, decimal? OfferedPremium, bool NeedsAcceptance, DateOnly? CoverEndsOn, long RowVersion)
{
    public static PortalRenewalView From(Renewal r) => new(r.Id, r.PolicyId, r.PolicyNumber, r.PeriodEnd, Upper(r.Status), r.Currency, r.CurrentPremium,
        r.Status == RenewalStatus.NotRenewedByInsurer ? null : r.OfferedPremium, r.NeedsAcceptance,
        r.Status == RenewalStatus.NotRenewedByInsurer ? r.CoverExtendedTo ?? r.PeriodEnd : null, r.RowVersion);

    private static string Upper(RenewalStatus status) => System.Text.RegularExpressions.Regex.Replace(status.ToString(), "(?<!^)([A-Z])", "_$1").ToUpperInvariant();
}

public sealed class ProposeNonRenewalValidator : AbstractValidator<ProposeNonRenewal>
{
    public ProposeNonRenewalValidator() => RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(20);
}

/// <summary>The customer's answer to a renewal, recorded by staff or through a portal (§4.4, §4.6), and an insurer's non-renewal (§4.3).</summary>
internal sealed class RenewalResponseHandlers(IGeneralRepository general, IGeneralPortalQueries portal, IGeneralCodes codes, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<AcceptRenewal, RenewalView>,
    ICommandHandler<DeclineRenewal, RenewalView>,
    ICommandHandler<PortalAcceptRenewal, PortalRenewalView>,
    ICommandHandler<PortalDeclineRenewal, PortalRenewalView>,
    ICommandHandler<ProposeNonRenewal, RenewalView>,
    IQueryHandler<ListPortalRenewals, IReadOnlyList<PortalRenewalView>>,
    IQueryHandler<GetPortalRenewal, PortalRenewalView>
{
    private static readonly Error NotFound = Error.NotFound(ErrorCodes.NotFound, "The renewal was not found.");

    public Task<Result<RenewalView>> Handle(AcceptRenewal command, CancellationToken cancellationToken) =>
        Staff(command.RenewalId, "AcceptRenewal", (r, user) => r.Accept(user, RenewalAcceptance.Staff, clock.UtcNow), cancellationToken);

    public Task<Result<RenewalView>> Handle(DeclineRenewal command, CancellationToken cancellationToken) =>
        Staff(command.RenewalId, "DeclineRenewal", (r, user) => r.Decline(user, RenewalAcceptance.Staff, clock.UtcNow), cancellationToken);

    public Task<Result<PortalRenewalView>> Handle(PortalAcceptRenewal command, CancellationToken cancellationToken) =>
        Portal(command.RenewalId, "PortalAcceptRenewal", (r, user, channel) => r.Accept(user, channel, clock.UtcNow), cancellationToken);

    public Task<Result<PortalRenewalView>> Handle(PortalDeclineRenewal command, CancellationToken cancellationToken) =>
        Portal(command.RenewalId, "PortalDeclineRenewal", (r, user, channel) => r.Decline(user, channel, clock.UtcNow), cancellationToken);

    public async Task<Result<IReadOnlyList<PortalRenewalView>>> Handle(ListPortalRenewals query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalRenewalView>>((await portal.Renewals(Related.Require(execution), query.AfterId, query.Limit, cancellationToken))
            .Select(PortalRenewalView.From).ToList());

    public async Task<Result<PortalRenewalView>> Handle(GetPortalRenewal query, CancellationToken cancellationToken) =>
        await Visible(query.RenewalId, cancellationToken) is { } renewal ? PortalRenewalView.From(renewal) : NotFound;

    public async Task<Result<RenewalView>> Handle(ProposeNonRenewal command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Policy(command.PolicyId, cancellationToken) is not { Status: Domain.Policies.PolicyStatus.InForce } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found in force.");
        }

        var today = businessDate.Today;
        if (await codes.NonRenewalReason(command.ReasonCode, today, cancellationToken) is not { } reason)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a non-renewal reason in force.");
        }

        // The renewal of the current period, opened at the current terms when the product's renewal is not yet on record.
        var renewal = await general.RenewalOfPeriod(policy.Id, policy.PeriodEnd, cancellationToken);
        if (renewal is null)
        {
            renewal = Renewal.OpenOffered(Guid.CreateVersion7(), policy.Id, policy.PolicyNumber, policy.ProductCode, policy.PeriodEnd, policy.AnnualPremium,
                policy.SumInsuredOn(policy.PeriodEnd), policy.Currency, today, clock.UtcNow);
            general.Add(renewal);
        }

        var requested = renewal.RequestNonRenewal(reason.ReasonCode, reason.RequiresApproval, user.Value, clock.UtcNow);
        if (requested.IsFailure)
        {
            return requested.Error;
        }

        audit.Record(new AuditEntry("GENERAL", "ProposeNonRenewal", nameof(Renewal), renewal.Id.ToString(),
            new Dictionary<string, string?> { ["policy"] = policy.Id.ToString(), ["reason"] = reason.ReasonCode, ["approval"] = reason.RequiresApproval.ToString() }));
        if (!reason.RequiresApproval)
        {
            var decided = NonRenewals.Decide(renewal, policy, approved: true, user.Value, reason, today, clock.UtcNow);
            if (decided.IsFailure)
            {
                return decided.Error;
            }
        }

        return RenewalView.From(renewal);
    }

    private async Task<Result<RenewalView>> Staff(Guid renewalId, string action, Func<Renewal, Guid, Result> respond, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Renewal(renewalId, cancellationToken) is not { } renewal)
        {
            return NotFound;
        }

        var result = respond(renewal, user.Value);
        audit.Record(new AuditEntry("GENERAL", action, nameof(Renewal), renewal.Id.ToString(), new Dictionary<string, string?> { ["policy"] = renewal.PolicyId.ToString() }));
        return result.IsFailure ? result.Error : RenewalView.From(renewal);
    }

    private async Task<Result<PortalRenewalView>> Portal(Guid renewalId, string action, Func<Renewal, Guid, string, Result> respond, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await Visible(renewalId, cancellationToken) is not { } shown || await general.Renewal(shown.Id, cancellationToken) is not { } renewal)
        {
            return NotFound;
        }

        var who = Related.Require(execution);
        var result = respond(renewal, user.Value, who.Kind == ExternalSubject.Party ? RenewalAcceptance.Portal : RenewalAcceptance.Broker);
        audit.Record(new AuditEntry("GENERAL", action, nameof(Renewal), renewal.Id.ToString(), new Dictionary<string, string?> { ["policy"] = renewal.PolicyId.ToString() }));
        return result.IsFailure ? result.Error : PortalRenewalView.From(renewal);
    }

    /// <summary>A renewal of the caller's policy that the portal shows (§4.6); any other is not found.</summary>
    private async Task<Renewal?> Visible(Guid renewalId, CancellationToken cancellationToken)
    {
        if (await general.Renewal(renewalId, cancellationToken) is not { } renewal || await general.Policy(renewal.PolicyId, cancellationToken) is not { } policy
            || !Related.Policy(Related.Require(execution), policy))
        {
            return null;
        }

        var shown = renewal.Status is not (RenewalStatus.Reviewing or RenewalStatus.Referred or RenewalStatus.NotEligible)
                    && (policy.RenewalInvitedOn is not null || renewal.Status != RenewalStatus.Offered);
        return shown ? renewal : null;
    }
}

/// <summary>Deciding an insurer's non-renewal (F025, OQ-132), at once or on its Workflow approval.</summary>
internal static class NonRenewals
{
    /// <summary>
    /// The decision: approved, the notice goes to the policyholder with the reason's wording; when the notice runs past the
    /// period end, the policy's cover is extended, pro rata, until it runs out (OQ-132 (b)). Rejected, nothing changes.
    /// </summary>
    public static Result Decide(Renewal renewal, Domain.Policies.GeneralPolicy policy, bool approved, Guid by, NonRenewalReason reason, DateOnly today, DateTimeOffset now)
    {
        var decided = renewal.DecideNonRenewal(approved, by, reason.NoticeDays, reason.CustomerWording, policy.PolicyholderPartyId, policy.OrgNodeCode, today, now);
        return decided.IsFailure || !approved || renewal.CoverExtendedTo is not { } extendedTo ? decided : policy.ExtendForNotice(extendedTo, now);
    }
}

/// <summary>
/// Wave 4 §4.4, ON_PAYMENT: the renewal policy's first premium, paid, is the customer's acceptance. Grace ending unpaid lapses
/// it (RunDue CancelForNonPayment).
/// </summary>
public sealed class RenewalPayments(IGeneralRepository general, IClock clock) : IIntegrationEventHandler<InvoiceSettled>
{
    public async Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (settled.SourceDomain == GeneralRatingSubjects.SourceDomain && await general.RenewalIssuedAs(settled.PolicyRef, cancellationToken) is
                { Acceptance: RenewalAcceptance.OnPayment } renewal)
        {
            renewal.AcceptedByPayment(clock.UtcNow);
        }
    }
}
