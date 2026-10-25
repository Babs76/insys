/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using System.ComponentModel.DataAnnotations;
using FluentValidation;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Cases;

namespace Insys.Workflow.Application;

// Portal users (§5, external-user access): the user's approved link sets what they may raise and see. Out of it a read is
// not found and an action refused (403 OUT_OF_SCOPE). Portal views show the number, type, status, dates, messages, the
// decision with reasons, and the escalation and ombud details; never internal notes, assignees, queues, tasks or others'
// details.

public sealed record PortalCaseLinkView([property: AllowedValues("POLICY", "CLAIM", "MEMBERSHIP", "PAYMENT", "INVOICE", "BILLING_ACCOUNT", "PARTY", "PROVIDER", "REMITTANCE", "PAYMENT_RUN", "INTERMEDIARY")]
    string LinkType, Guid RecordId, string Reference);

public sealed record PortalCaseMessageView([property: AllowedValues("IN", "OUT")] string Direction, string Body, DateTimeOffset SentAt);

/// <param name="ReviewOptions">How to ask for a review of the decision, once decided (PPR 18.7.2).</param>
/// <param name="OmbudDetails">The ombud schemes the case may go to, once decided (PPR 18.10).</param>
/// <param name="EscalationLevel">How many times the case has been escalated (0: not escalated).</param>
public sealed record PortalCaseView(Guid Id, string CaseNumber, string CaseTypeCode, [property: AllowedValues("ENQUIRY", "COMPLAINT", "DISPUTE", "APPEAL")] string Family,
    CaseStatus Status, [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "WITHDRAWN", "ANSWERED")] string? Outcome, DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpectedBy, string? CategoryCode, string Summary, string? DecisionReasons, DateTimeOffset? DecidedAt, string? ReviewOptions, string? OmbudDetails,
    int EscalationLevel, string? OmbudSchemeCode, string? OmbudReference, DateTimeOffset? OmbudReferredAt,
    [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "SETTLED", "DISMISSED")] string? OmbudOutcome, Guid? ParentCaseId, IReadOnlyList<PortalCaseLinkView> Links,
    IReadOnlyList<PortalCaseMessageView> Messages);

public sealed record PortalCaseSummaryView(Guid Id, string CaseNumber, string CaseTypeCode, [property: AllowedValues("ENQUIRY", "COMPLAINT", "DISPUTE", "APPEAL")] string Family,
    CaseStatus Status, [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "WITHDRAWN", "ANSWERED")] string? Outcome, DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpectedBy);

/// <summary>Cases in an external user's relationship, filtered in the query so paging stays correct (OQ-108 for brokers).</summary>
public interface ICasePortalQueries
{
    /// <param name="brokeredPolicies">For a broker: the policies it brokers, whose shared cases it sees.</param>
    Task<IReadOnlyList<ServiceCase>> List(ExternalSubject who, IReadOnlyCollection<Guid> brokeredPolicies, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseMessage>> Messages(Guid caseId, CancellationToken cancellationToken);
}

/// <param name="OnBehalfOfPartyId">A broker raising a complaint for a client (§5): the client, whose policy it links.</param>
public sealed record RaisePortalCase(string CaseTypeCode, string? CategoryCode, string Summary, Guid? OnBehalfOfPartyId, IReadOnlyList<CaseLinkInput>? Links) : ICommand<Guid>;

public sealed record AddPortalCaseMessage(Guid CaseId, string Body) : ICommand<bool>;

/// <summary>An appeal of the user's own decided case, as its type's review type (PPR 18.6).</summary>
public sealed record AppealPortalCase(Guid CaseId, string Summary) : ICommand<Guid>;

public sealed record ListPortalCases(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalCaseSummaryView>>;

public sealed record GetPortalCase(Guid CaseId) : IQuery<PortalCaseView>;

public sealed class RaisePortalCaseValidator : AbstractValidator<RaisePortalCase>
{
    public RaisePortalCaseValidator()
    {
        RuleFor(c => c.CaseTypeCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Summary).NotEmpty().MaximumLength(20_000);
        RuleFor(c => c.CategoryCode).MaximumLength(30);
    }
}

public sealed class AddPortalCaseMessageValidator : AbstractValidator<AddPortalCaseMessage>
{
    public AddPortalCaseMessageValidator() => RuleFor(c => c.Body).NotEmpty().MaximumLength(20_000);
}

public sealed class AppealPortalCaseValidator : AbstractValidator<AppealPortalCase>
{
    public AppealPortalCaseValidator() => RuleFor(c => c.Summary).NotEmpty().MaximumLength(20_000);
}

/// <summary>
/// The text a decision shows (§7): the review open to the complainant and the ombud schemes the case type names, from
/// workflow.ref_ombud_schemes. The wording of the notices themselves is the tenant's templates'.
/// </summary>
public sealed class CaseNoticeTexts(ICaseConfiguration configuration, CaseTimes times, IBusinessDate businessDate)
{
    public async Task<(string? ReviewOptions, string? OmbudDetails)> For(CaseTypeConfig? type, CancellationToken cancellationToken)
    {
        if (type is null)
        {
            return (null, null);
        }

        string? review = null;
        if (type.ReviewCaseTypeCode is { } reviewType)
        {
            var timeline = (await times.Timelines(reviewType, cancellationToken)).FirstOrDefault(t => t.Step == CaseSteps.Review);
            review = timeline is null
                ? $"You may ask for an internal review of this decision ({reviewType})."
                : $"You may ask for an internal review of this decision ({reviewType}); a review is decided within {timeline.Duration} ({timeline.Basis.ToLowerInvariant()} time).";
        }

        var schemes = await configuration.OmbudSchemes(type.OmbudSchemeCodes, businessDate.Today, cancellationToken);
        var ombud = schemes.Count == 0
            ? null
            : string.Join("\n", schemes.Select(s => $"{s.Name}{(string.IsNullOrWhiteSpace(s.Contact) ? "" : $" ({s.Contact})")}: {s.ReferralInformation}"));
        return (review, ombud);
    }

    /// <summary>The date representations are decided by (DECIDE_REPRESENTATIONS), from the decision, or null when the type has none.</summary>
    public async Task<DateTimeOffset?> RepresentationsDeadline(ServiceCase serviceCase, CancellationToken cancellationToken) =>
        await times.Due(await times.Timelines(serviceCase.CaseTypeCode, cancellationToken), CaseSteps.DecideRepresentations, serviceCase.DecidedAt ?? serviceCase.ReceivedAt,
            cancellationToken);
}

/// <summary>Who an external user is to a case (§5, OQ-108).</summary>
public static class PortalCaseRelation
{
    public static bool Sees(ExternalSubject who, ServiceCase c, IReadOnlyCollection<Guid> brokeredPolicies) => who.Kind switch
    {
        ExternalSubject.Party => who.PartyId is { } party && (c.ComplainantPartyId == party || c.OnBehalfOfPartyId == party),
        ExternalSubject.Intermediary => who.Brokers(c.RaisedByIntermediaryCode)
                                        || (c.SharedWithBroker && c.Links.Any(l => l.LinkType == CaseLinkTypes.Policy && brokeredPolicies.Contains(l.RecordId))),
        ExternalSubject.Provider => who.ProviderId is { } provider && c.RaisedByProviderId == provider,
        _ => false,
    };

    /// <summary>The business reference types under which other modules answer whether a linked record is the user's (IPortalRecordRelations).</summary>
    public static IReadOnlyList<string> RelationRefTypes(string linkType) => linkType switch
    {
        CaseLinkTypes.Policy => ["GENERAL_POLICY", "LIFE_POLICY"],
        CaseLinkTypes.Invoice => ["INVOICE"],
        CaseLinkTypes.Remittance => ["HEALTH_REMITTANCE"],
        _ => [],
    };
}

internal sealed class CasePortalHandlers(ICaseRepository cases, ICasePortalQueries queries, ICaseConfiguration configuration, CaseOpening opening, CaseNoticeTexts notices,
    IEnumerable<IPortalRecordRelations> relations, IEnumerable<IBrokeredPolicies> brokered, IExecutionContext execution, ITenantContext tenant, IBusinessDate businessDate,
    IClock clock) :
    IQueryHandler<ListPortalCases, IReadOnlyList<PortalCaseSummaryView>>,
    IQueryHandler<GetPortalCase, PortalCaseView>,
    ICommandHandler<RaisePortalCase, Guid>,
    ICommandHandler<AddPortalCaseMessage, bool>,
    ICommandHandler<AppealPortalCase, Guid>
{
    private static readonly Error NotFound = Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    private ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    private Guid? UserId => Guid.TryParse(execution.ActorId, out var id) ? id : null;

    public async Task<Result<IReadOnlyList<PortalCaseSummaryView>>> Handle(ListPortalCases query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalCaseSummaryView>>((await queries.List(Who, await Brokered(cancellationToken), query.AfterId, query.Limit, cancellationToken))
            .Select(c => new PortalCaseSummaryView(c.Id, c.CaseNumber, c.CaseTypeCode, c.Family, c.Status, c.Outcome, c.ReceivedAt, c.ExpectedBy)).ToList());

    public async Task<Result<PortalCaseView>> Handle(GetPortalCase query, CancellationToken cancellationToken)
    {
        if (await Own(query.CaseId, cancellationToken) is not { } c)
        {
            return NotFound;
        }

        var decided = c.DecidedAt is not null && c.Outcome is not null and not CaseOutcomes.Withdrawn;
        var (review, ombud) = decided ? await notices.For(await configuration.Type(c.CaseTypeCode, businessDate.Today, cancellationToken), cancellationToken) : (null, null);
        var messages = await queries.Messages(c.Id, cancellationToken);
        return new PortalCaseView(c.Id, c.CaseNumber, c.CaseTypeCode, c.Family, c.Status, c.Outcome, c.ReceivedAt, c.ExpectedBy, c.CategoryCode, c.Summary,
            decided ? c.DecisionReasons : null, decided ? c.DecidedAt : null, review, ombud, await cases.CountHistory(c.Id, CaseEventTypes.Escalated, cancellationToken),
            c.OmbudSchemeCode, c.OmbudReference, c.OmbudReferredAt, c.OmbudOutcome, c.ParentCaseId,
            c.Links.Select(l => new PortalCaseLinkView(l.LinkType, l.RecordId, l.Reference)).ToList(),
            messages.Select(m => new PortalCaseMessageView(m.Direction, m.Body, m.SentAt)).ToList());
    }

    /// <summary>
    /// §5: only a case type whose portal audiences name the user's portal; every link within the user's relationship (a
    /// type no module answers for is refused); a broker's client complaint links a policy it brokers.
    /// </summary>
    public async Task<Result<Guid>> Handle(RaisePortalCase command, CancellationToken cancellationToken)
    {
        var who = Who;
        if (await configuration.Type(command.CaseTypeCode, businessDate.Today, cancellationToken) is not { } type || !type.PortalAudiences.Contains(who.ChannelAudience))
        {
            return Error.Validation(CaseErrorCodes.TypeNotInForce, $"Case type {command.CaseTypeCode} cannot be raised in this portal.");
        }

        if (type.Family == CaseFamilies.Appeal)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An appeal is raised on the case it appeals (POST /portal/cases/{id}/appeal).");
        }

        var links = command.Links ?? [];
        foreach (var link in links)
        {
            if (!await Related(who, link, cancellationToken))
            {
                return Error.Forbidden(ErrorCodes.OutOfScope, $"The {link.LinkType} record is not one of yours.");
            }
        }

        Complainant complainant;
        if (who.Kind == ExternalSubject.Intermediary && command.OnBehalfOfPartyId is { } client)
        {
            if (!links.Any(l => l.LinkType == CaseLinkTypes.Policy))
            {
                return Error.Forbidden(ErrorCodes.OutOfScope, "A complaint for a client links the client's policy that you broker.");
            }

            complainant = new Complainant(client, null, null, client);
        }
        else
        {
            complainant = Complainant(who);
        }

        var raiser = new CaseRaiser(who.ChannelAudience, UserId, who.Kind == ExternalSubject.Intermediary ? who.IntermediaryCode : null,
            who.Kind == ExternalSubject.Provider ? who.ProviderId : null);
        var opened = await opening.Open(type, "PORTAL", null, complainant, raiser, command.CategoryCode, command.Summary, false, null, links, execution.ActorId,
            raisedFromEvent: false, cancellationToken);
        return opened.IsFailure ? opened.Error : opened.Value.Id;
    }

    public async Task<Result<bool>> Handle(AddPortalCaseMessage command, CancellationToken cancellationToken)
    {
        if (await Own(command.CaseId, cancellationToken) is not { } c)
        {
            return NotFound;
        }

        var added = c.AddMessage(CaseMessage.In, Who.ChannelAudience, UserId, command.Body, execution.ActorId, clock.UtcNow);
        return added.IsFailure ? added.Error : true;
    }

    public async Task<Result<Guid>> Handle(AppealPortalCase command, CancellationToken cancellationToken)
    {
        var who = Who;
        if (await Own(command.CaseId, cancellationToken) is not { } parent)
        {
            return NotFound;
        }

        if (parent.DecidedAt is null || parent.Outcome is null or CaseOutcomes.Withdrawn or CaseOutcomes.Answered)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a decided case is appealed.");
        }

        var parentType = await configuration.Type(parent.CaseTypeCode, businessDate.Today, cancellationToken);
        if (parentType?.ReviewCaseTypeCode is not { } reviewCode || await configuration.Type(reviewCode, businessDate.Today, cancellationToken) is not { } reviewType
            || !reviewType.PortalAudiences.Contains(who.ChannelAudience))
        {
            return Error.Validation(CaseErrorCodes.TypeNotInForce, "This case's decision cannot be appealed in this portal.");
        }

        var raiser = new CaseRaiser(who.ChannelAudience, UserId, who.Kind == ExternalSubject.Intermediary ? who.IntermediaryCode : null,
            who.Kind == ExternalSubject.Provider ? who.ProviderId : null);
        var complainant = new Complainant(parent.ComplainantPartyId, parent.ComplainantPartyId is null ? parent.ComplainantName : null,
            parent.ComplainantPartyId is null ? parent.ComplainantContact : null, parent.OnBehalfOfPartyId);
        var links = parent.Links.Where(l => reviewType.LinkTypes.Contains(l.LinkType)).Select(l => new CaseLinkInput(l.LinkType, l.RecordId, l.Reference)).ToList();
        var opened = await opening.Open(reviewType, "PORTAL", null, complainant, raiser, parent.CategoryCode, command.Summary, false, parent.Id, links, execution.ActorId,
            raisedFromEvent: false, cancellationToken);
        return opened.IsFailure ? opened.Error : opened.Value.Id;
    }

    private async Task<ServiceCase?> Own(Guid caseId, CancellationToken cancellationToken) =>
        await cases.Get(caseId, cancellationToken) is { } c && PortalCaseRelation.Sees(Who, c, await Brokered(cancellationToken)) ? c : null;

    private async Task<IReadOnlyCollection<Guid>> Brokered(CancellationToken cancellationToken)
    {
        if (Who.Kind != ExternalSubject.Intermediary || Who.IntermediaryCodes.Count == 0)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var domain in brokered)
        {
            ids.AddRange(await domain.PolicyIds(tenant.RequireTenantId(), Who.IntermediaryCodes, cancellationToken));
        }

        return ids;
    }

    private async Task<bool> Related(ExternalSubject who, CaseLinkInput link, CancellationToken cancellationToken)
    {
        if (link.LinkType == CaseLinkTypes.Party)
        {
            return who.Kind == ExternalSubject.Party && who.PartyId == link.RecordId;
        }

        foreach (var refType in PortalCaseRelation.RelationRefTypes(link.LinkType))
        {
            foreach (var relation in relations.Where(r => r.Covers(refType)))
            {
                if (await relation.Related(tenant.RequireTenantId(), who, refType, link.RecordId, cancellationToken))
                {
                    return true;
                }
            }
        }

        // TODO(spec): claims, memberships, payments and provider records have no portal relation in their modules yet, so
        // a portal user cannot link them (a member's dispute of a declined claim needs the claim modules to answer).
        return false;
    }

    private static Complainant Complainant(ExternalSubject who) => who.Kind switch
    {
        ExternalSubject.Party => new Complainant(who.PartyId, null, null, null),
        ExternalSubject.Intermediary => new Complainant(null, $"Intermediary {who.IntermediaryCode}", null, null),
        _ => new Complainant(null, $"Provider {who.ProviderId}", null, null),
    };
}
