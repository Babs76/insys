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
using Insys.BuildingBlocks.Domain;

namespace Insys.Workflow.Domain.Cases;

// Cases (docs/specs/addenda/cases.md): enquiries, complaints, disputes and appeals. The case holds what it is about and
// its history; the Workflow process its case type names holds where the work is. Free text (the summary, messages, the
// complainant's name and contact, decision reasons) never goes into an event, a log or an exception message (POPIA, §9).

/// <summary>A case's status (§4), stored upper case with underscores in workflow.service_case.status.</summary>
public enum CaseStatus
{
    Received,
    Acknowledged,
    InProgress,
    AwaitingComplainant,
    Decided,
    Closed,
    Withdrawn,
    Reopened,
}

public enum CaseTrigger
{
    Acknowledge,
    Start,
    AwaitComplainant,
    Resume,
    Decide,
    Close,
    CloseAnswered,
    Withdraw,
    Reopen,
}

/// <summary>The steps a case type's timelines are configured for (workflow.cfg_case_timelines.step).</summary>
public static class CaseSteps
{
    public const string Acknowledge = "ACKNOWLEDGE";
    public const string Resolve = "RESOLVE";
    public const string DecideRepresentations = "DECIDE_REPRESENTATIONS";
    public const string NotifyDecision = "NOTIFY_DECISION";
    public const string Review = "REVIEW";
    public const string Redress = "REDRESS";

    public static readonly IReadOnlyList<string> All = [Acknowledge, Resolve, DecideRepresentations, NotifyDecision, Review, Redress];
}

/// <summary>Who raised a case (workflow.service_case.raised_by_audience): staff, or a portal user of one of the three portals.</summary>
public static class CaseAudiences
{
    public const string Staff = "STAFF";
    public const string Customer = "CUSTOMER";
    public const string Broker = "BROKER";
    public const string Provider = "PROVIDER";
}

/// <summary>The rows of workflow.case_event (the case's history, §1).</summary>
public static class CaseEventTypes
{
    public const string Received = "RECEIVED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Assigned = "ASSIGNED";
    public const string Updated = "UPDATED";
    public const string Linked = "LINKED";
    public const string MessageSent = "MESSAGE_SENT";
    public const string MessageReceived = "MESSAGE_RECEIVED";
    public const string Extended = "EXTENDED";
    public const string Escalated = "ESCALATED";
    public const string AwaitingComplainant = "AWAITING_COMPLAINANT";
    public const string Resumed = "RESUMED";
    public const string Decided = "DECIDED";
    public const string RedressRequested = "REDRESS_REQUESTED";
    public const string RedressApproved = "REDRESS_APPROVED";
    public const string RedressRejected = "REDRESS_REJECTED";
    public const string RedressPaid = "REDRESS_PAID";
    public const string ReferredToOmbud = "REFERRED_TO_OMBUD";
    public const string OmbudOutcome = "OMBUD_OUTCOME";
    public const string SharedWithBroker = "SHARED_WITH_BROKER";
    public const string Reopened = "REOPENED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Closed = "CLOSED";
    public const string LegalHoldPlaced = "LEGAL_HOLD_PLACED";
    public const string LegalHoldReleased = "LEGAL_HOLD_RELEASED";
}

/// <summary>An ombud's finding on a referred case (workflow.service_case.ombud_outcome).</summary>
public static class OmbudOutcomes
{
    public static readonly IReadOnlyList<string> All = ["UPHELD", "PARTLY_UPHELD", "REJECTED", "SETTLED", "DISMISSED"];
}

/// <summary>What the rules of a case need from its case type (workflow.cfg_case_types).</summary>
/// <param name="LinkTypes">The record types a case of this type may link (CASE_LINK_NOT_ALLOWED otherwise).</param>
public sealed record CaseTypeRules(string Code, string Family, IReadOnlyCollection<string> LinkTypes, bool RequiresCategory);

/// <summary>Who raised the case: staff, or a portal user and the party, intermediary or provider they act for.</summary>
public sealed record CaseRaiser(string Audience, Guid? UserId, string? IntermediaryCode, string? ProviderId);

/// <summary>The complainant: a party, or a name and contact for someone not yet a party (§1). Personal data: never logged.</summary>
public sealed record Complainant(Guid? PartyId, string? Name, string? Contact, Guid? OnBehalfOfPartyId);

/// <summary>One row of workflow.case_event: who did what and when. The detail never holds the case's own text.</summary>
public sealed class CaseHistoryEntry : Entity<Guid>, ITenantScoped
{
    private CaseHistoryEntry()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CaseId { get; private set; }

    public string EventType { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }

    public string Actor { get; private set; } = "";

    public string? Detail { get; private set; }

    internal static CaseHistoryEntry Write(Guid caseId, string eventType, string actor, DateTimeOffset at, string? detail) =>
        new()
        {
            Id = Guid.CreateVersion7(), CaseId = caseId, EventType = eventType, OccurredAt = at, Actor = Guard.MaxLength(string.IsNullOrWhiteSpace(actor) ? "system" : actor, 80),
            Detail = detail is null ? null : detail.Length <= 2000 ? detail : detail[..2000],
        };
}

/// <summary>A record the case is about (workflow.case_link): identifiers only (ADR-001).</summary>
public sealed class CaseLink : Entity<Guid>, ITenantScoped
{
    private CaseLink()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CaseId { get; private set; }

    public string LinkType { get; private set; } = "";

    public Guid RecordId { get; private set; }

    /// <summary>The record's own number, for display.</summary>
    public string Reference { get; private set; } = "";

    public DateTimeOffset AddedAt { get; private set; }

    public string AddedBy { get; private set; } = "";

    internal static CaseLink Create(Guid caseId, string linkType, Guid recordId, string reference, string actor, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), CaseId = caseId, LinkType = linkType, RecordId = Guard.NotEmpty(recordId),
            Reference = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reference), 60), AddedAt = now, AddedBy = Guard.MaxLength(actor, 80),
        };
}

/// <summary>A message between the complainant (or broker, provider) and staff (workflow.case_message): what the portal shows.</summary>
public sealed class CaseMessage : Entity<Guid>, ITenantScoped
{
    public const string In = "IN";
    public const string Out = "OUT";

    private CaseMessage()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CaseId { get; private set; }

    /// <summary>IN from the complainant's side, OUT from staff.</summary>
    public string Direction { get; private set; } = "";

    public string AuthorAudience { get; private set; } = "";

    public Guid? AuthorId { get; private set; }

    public string Body { get; private set; } = "";

    public DateTimeOffset SentAt { get; private set; }

    internal static CaseMessage Write(Guid caseId, string direction, string audience, Guid? authorId, string body, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), CaseId = caseId, Direction = direction, AuthorAudience = audience, AuthorId = authorId, Body = body, SentAt = now };
}

public sealed record CaseLinkRecord(string LinkType, Guid RecordId, string Reference);

public sealed record CaseOpened(Guid CaseId, string CaseNumber, string CaseTypeCode, string Family, Guid? ComplainantPartyId, IReadOnlyList<CaseLinkRecord> Links,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CaseAcknowledged(Guid CaseId, string CaseNumber, string Family, Guid? ComplainantPartyId, DateTimeOffset? ExpectedBy, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <param name="Reason">A code (EXTENDED, OVERDUE), never free text.</param>
public sealed record CaseUpdated(Guid CaseId, string CaseNumber, string Family, Guid? ComplainantPartyId, string Reason, DateTimeOffset? ExpectedBy, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record CaseEscalated(Guid CaseId, string CaseNumber, string Family, int Level, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CaseDecided(Guid CaseId, string CaseNumber, string CaseTypeCode, string Family, string Outcome, Guid? ComplainantPartyId, IReadOnlyList<CaseLinkRecord> Links,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CaseReferredToOmbud(Guid CaseId, string CaseNumber, string SchemeCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CaseClosed(Guid CaseId, string CaseNumber, string Family, string? Outcome, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CaseReopened(Guid CaseId, string CaseNumber, string Family, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The ServiceCase aggregate (workflow.service_case, §1 and §4): RECEIVED → ACKNOWLEDGED → IN_PROGRESS ⇄
/// AWAITING_COMPLAINANT → DECIDED → CLOSED; WITHDRAWN from any open status; a closed case REOPENED → IN_PROGRESS; an
/// enquiry may close straight from RECEIVED as ANSWERED. A closed or withdrawn case takes no change but a legal hold.
/// </summary>
public sealed class ServiceCase : AggregateRoot<Guid>
{
    public static readonly StateMachine<CaseStatus, CaseTrigger> Lifecycle = new StateMachine<CaseStatus, CaseTrigger>()
        .Allow(CaseStatus.Received, CaseTrigger.Acknowledge, CaseStatus.Acknowledged)
        .Allow(CaseStatus.Acknowledged, CaseTrigger.Start, CaseStatus.InProgress)
        .Allow(CaseStatus.Reopened, CaseTrigger.Start, CaseStatus.InProgress)
        .Allow(CaseStatus.InProgress, CaseTrigger.AwaitComplainant, CaseStatus.AwaitingComplainant)
        .Allow(CaseStatus.AwaitingComplainant, CaseTrigger.Resume, CaseStatus.InProgress)
        .Allow(CaseStatus.InProgress, CaseTrigger.Decide, CaseStatus.Decided)
        .Allow(CaseStatus.Decided, CaseTrigger.Close, CaseStatus.Closed)
        .Allow(CaseStatus.Received, CaseTrigger.CloseAnswered, CaseStatus.Closed)
        .Allow(CaseStatus.Received, CaseTrigger.Withdraw, CaseStatus.Withdrawn)
        .Allow(CaseStatus.Acknowledged, CaseTrigger.Withdraw, CaseStatus.Withdrawn)
        .Allow(CaseStatus.InProgress, CaseTrigger.Withdraw, CaseStatus.Withdrawn)
        .Allow(CaseStatus.AwaitingComplainant, CaseTrigger.Withdraw, CaseStatus.Withdrawn)
        .Allow(CaseStatus.Reopened, CaseTrigger.Withdraw, CaseStatus.Withdrawn)
        .Allow(CaseStatus.Closed, CaseTrigger.Reopen, CaseStatus.Reopened);

    private static readonly string[] Families = ["ENQUIRY", "COMPLAINT", "DISPUTE", "APPEAL"];
    private readonly List<CaseLink> _links = [];
    private readonly List<CaseHistoryEntry> _history = [];
    private readonly List<CaseMessage> _messages = [];

    private ServiceCase()
    {
    }

    public string CaseNumber { get; private set; } = "";

    public string CaseTypeCode { get; private set; } = "";

    public string Family { get; private set; } = "";

    public CaseStatus Status { get; private set; }

    public string? Outcome { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string ReceivedChannel { get; private set; } = "";

    public Guid? ComplainantPartyId { get; private set; }

    public string? ComplainantName { get; private set; }

    public string? ComplainantContact { get; private set; }

    public Guid? OnBehalfOfPartyId { get; private set; }

    public Guid? RaisedByUserId { get; private set; }

    public string RaisedByAudience { get; private set; } = CaseAudiences.Staff;

    public string? RaisedByIntermediaryCode { get; private set; }

    public string? RaisedByProviderId { get; private set; }

    /// <summary>OQ-108: staff marked the case visible to the broker of its linked records, which did not raise it.</summary>
    public bool SharedWithBroker { get; private set; }

    public string? CategoryCode { get; private set; }

    /// <summary>The complaint's text: personal data, never logged or put in events.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Upheld at once by the person who received it (PPR 18.1): such a complaint is not reportable.</summary>
    public bool UpheldAtReceipt { get; private set; }

    /// <summary>Computed when a complaint closes (§6.2); null until then, and for other families.</summary>
    public bool? Reportable { get; private set; }

    public string? DecisionReasons { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The case whose decision this appeal is about, so the chain is one history.</summary>
    public Guid? ParentCaseId { get; private set; }

    public Guid? ProcessInstanceId { get; private set; }

    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>When the current step is due (from the case type's timelines); null when no timeline applies.</summary>
    public DateTimeOffset? ExpectedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>OQ-106: set at closure; the case is disposed of after it unless held.</summary>
    public DateOnly? RetainUntil { get; private set; }

    /// <summary>Retired by V0068 (Ade, 28 October 2026): a case is held by a record hold with scope CASE; always false.</summary>
    public bool LegalHold { get; private set; }

    public string? OmbudSchemeCode { get; private set; }

    public string? OmbudReference { get; private set; }

    public DateTimeOffset? OmbudReferredAt { get; private set; }

    public string? OmbudOutcome { get; private set; }

    public DateTimeOffset? OmbudOutcomeAt { get; private set; }

    public IReadOnlyList<CaseLink> Links => _links;

    /// <summary>History rows written since the case was loaded; the repository stores them (case_event is append-only).</summary>
    public IReadOnlyList<CaseHistoryEntry> NewHistory => _history;

    /// <summary>Messages written since the case was loaded (case_message is append-only).</summary>
    public IReadOnlyList<CaseMessage> NewMessages => _messages;

    public bool IsOpen => Status is not (CaseStatus.Closed or CaseStatus.Withdrawn);

    /// <summary>The step whose timeline the case is now on: acknowledging while received, otherwise resolving (an appeal's review).</summary>
    public string CurrentStep => Status == CaseStatus.Received ? CaseSteps.Acknowledge : ResolutionStep(Family);

    /// <summary>The step a case of the family is resolved under: REVIEW for an appeal (PPR 18.6), RESOLVE for the rest.</summary>
    public static string ResolutionStep(string family) => family == "APPEAL" ? CaseSteps.Review : CaseSteps.Resolve;

    /// <summary>Command OpenCase (RECEIVED).</summary>
    public static Result<ServiceCase> Open(Guid id, string caseNumber, CaseTypeRules type, DateTimeOffset receivedAt, string receivedChannel, Complainant complainant,
        CaseRaiser raiser, string? categoryCode, string summary, bool upheldAtReceipt, Guid? parentCaseId, DateTimeOffset? expectedBy, string actor, DateTimeOffset now)
    {
        if (!Families.Contains(type.Family))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"Case type {type.Code} has no family a case can have.");
        }

        if (complainant.PartyId is null && string.IsNullOrWhiteSpace(complainant.Name))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A case needs its complainant: a party, or a name and contact.");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A case needs a summary of what it is about.");
        }

        // PPR 18.5.3: a complaint (and any type that asks for one) has a category.
        if (type.RequiresCategory && string.IsNullOrWhiteSpace(categoryCode))
        {
            return Error.Validation(CaseErrorCodes.CategoryRequired, $"A case of type {type.Code} needs a category (PPR 18.5.3).");
        }

        var serviceCase = new ServiceCase
        {
            Id = Guard.NotEmpty(id),
            CaseNumber = Guard.MaxLength(Guard.NotNullOrWhiteSpace(caseNumber), 25),
            CaseTypeCode = Guard.MaxLength(type.Code, 30),
            Family = type.Family,
            Status = CaseStatus.Received,
            ReceivedAt = receivedAt,
            ReceivedChannel = Guard.MaxLength(Guard.NotNullOrWhiteSpace(receivedChannel), 20),
            ComplainantPartyId = complainant.PartyId,
            ComplainantName = complainant.Name is null ? null : Guard.MaxLength(complainant.Name.Trim(), 200),
            ComplainantContact = complainant.Contact is null ? null : Guard.MaxLength(complainant.Contact.Trim(), 200),
            OnBehalfOfPartyId = complainant.OnBehalfOfPartyId,
            RaisedByAudience = raiser.Audience,
            RaisedByUserId = raiser.UserId,
            RaisedByIntermediaryCode = raiser.IntermediaryCode is null ? null : Guard.MaxLength(raiser.IntermediaryCode, 30),
            RaisedByProviderId = raiser.ProviderId is null ? null : Guard.MaxLength(raiser.ProviderId, 40),
            CategoryCode = string.IsNullOrWhiteSpace(categoryCode) ? null : Guard.MaxLength(categoryCode, 30),
            Summary = summary.Trim(),
            UpheldAtReceipt = upheldAtReceipt,
            ParentCaseId = parentCaseId,
            ExpectedBy = expectedBy,
        };
        serviceCase.Record(CaseEventTypes.Received, actor, now, $"{type.Code} via {serviceCase.ReceivedChannel}");
        return serviceCase;
    }

    /// <summary>Announces the opened case (workflow.CaseOpened) once its first links are added, so the event carries them.</summary>
    public void AnnounceOpened(DateTimeOffset now)
    {
        if (DomainEvents.OfType<CaseOpened>().Any())
        {
            return;
        }

        Raise(new CaseOpened(Id, CaseNumber, CaseTypeCode, Family, ComplainantPartyId, LinkRecords(), now));
    }

    /// <summary>The process the case type names, started as the case opened.</summary>
    public void RunBy(Guid processInstanceId) => ProcessInstanceId = processInstanceId;

    /// <summary>F103: the product and node of the first linked record that has them; a case with none is tenant-wide.</summary>
    public void PlaceAt(string? productCode, string? orgNodeCode)
    {
        if (ProductCode is not null || OrgNodeCode is not null || (productCode is null && orgNodeCode is null))
        {
            return;
        }

        ProductCode = productCode is null ? null : Guard.MaxLength(productCode, 15);
        OrgNodeCode = orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
        MarkChanged();
    }

    /// <summary>Command LinkRecord: only the record types the case type allows (CASE_LINK_NOT_ALLOWED).</summary>
    public Result LinkRecord(CaseTypeRules type, string linkType, Guid recordId, string reference, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (!type.LinkTypes.Contains(linkType))
        {
            return Error.Validation(CaseErrorCodes.LinkNotAllowed, $"A case of type {CaseTypeCode} cannot link a {linkType} record.");
        }

        if (_links.Any(l => l.LinkType == linkType && l.RecordId == recordId))
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord,"The case already links this record.");
        }

        _links.Add(CaseLink.Create(Id, linkType, recordId, reference, actor, now));
        Record(CaseEventTypes.Linked, actor, now, $"{linkType} {reference}");
        return Result.Success();
    }

    /// <summary>Command Acknowledge (§7: CASE_ACKNOWLEDGED). The acknowledgement is the confirmation (OQ-107).</summary>
    public Result Acknowledge(DateTimeOffset? expectedBy, string actor, DateTimeOffset now)
    {
        var fired = Fire(CaseTrigger.Acknowledge);
        if (fired.IsFailure)
        {
            return fired;
        }

        AcknowledgedAt = now;
        ExpectedBy = expectedBy;
        Record(CaseEventTypes.Acknowledged, actor, now, null);
        Raise(new CaseAcknowledged(Id, CaseNumber, Family, ComplainantPartyId, ExpectedBy, now));
        return Result.Success();
    }

    /// <summary>Work starts (from acknowledged, or on a reopened case).</summary>
    public Result Start(string actor, DateTimeOffset now)
    {
        var fired = Fire(CaseTrigger.Start);
        if (fired.IsSuccess)
        {
            Record(CaseEventTypes.Updated, actor, now, "IN_PROGRESS");
        }

        return fired;
    }

    /// <summary>The case waits for the complainant (IN_PROGRESS → AWAITING_COMPLAINANT), or resumes.</summary>
    public Result AwaitComplainant(bool waiting, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        var fired = Fire(waiting ? CaseTrigger.AwaitComplainant : CaseTrigger.Resume);
        if (fired.IsSuccess)
        {
            Record(waiting ? CaseEventTypes.AwaitingComplainant : CaseEventTypes.Resumed, actor, now, null);
        }

        return fired;
    }

    /// <summary>
    /// Command ExtendTimeline: the application has checked the step allows it and the new date is within its maximum
    /// extension; the case records the new date, the reason (told to the complainant, PPR 18.9) and tells Communications.
    /// </summary>
    public Result Extend(string step, string extension, DateTimeOffset newExpectedBy, string reason, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An extension needs a reason, which the complainant is told (PPR 18.9).");
        }

        if (Status is CaseStatus.Decided)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A decided case has no timeline to extend.");
        }

        ExpectedBy = newExpectedBy;
        MarkChanged();
        Record(CaseEventTypes.Extended, actor, now, $"{step} {extension}: {reason.Trim()}");
        Raise(new CaseUpdated(Id, CaseNumber, Family, ComplainantPartyId, "EXTENDED", ExpectedBy, now));
        return Result.Success();
    }

    /// <summary>Command Escalate: to the next level, recorded and told to Communications.</summary>
    public Result Escalate(int level, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        MarkChanged();
        Record(CaseEventTypes.Escalated, actor, now, $"LEVEL {level}");
        Raise(new CaseEscalated(Id, CaseNumber, Family, level, now));
        return Result.Success();
    }

    /// <summary>
    /// Command Decide (§4). A rejection (and a partial upholding) needs reasons; an enquiry is ANSWERED, the other
    /// families UPHELD, PARTLY_UPHELD or REJECTED. An appeal is decided by someone other than the person who decided the
    /// case appealed (PPR 18.6: CASE_DECIDER_SAME_PERSON).
    /// </summary>
    /// <param name="appealedDecidedBy">For an appeal, who decided the case it appeals.</param>
    public Result Decide(string outcome, string? reasons, Guid deciderId, Guid? appealedDecidedBy, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        var allowed = Family == "ENQUIRY" ? new[] { "ANSWERED" } : ["UPHELD", "PARTLY_UPHELD", "REJECTED"];
        if (!allowed.Contains(outcome))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"A {Family.ToLowerInvariant()} is decided {string.Join(", ", allowed)}.");
        }

        if (outcome is "REJECTED" or "PARTLY_UPHELD" && string.IsNullOrWhiteSpace(reasons))
        {
            return Error.Validation(CaseErrorCodes.ReasonsRequired, "A rejection needs its reasons, which the complainant is told (PPR 18.7.2).");
        }

        if (deciderId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Cases are decided by identified users.");
        }

        if (Family == "APPEAL" && appealedDecidedBy is { } first && first == deciderId)
        {
            return Error.Conflict(CaseErrorCodes.DeciderSamePerson, "An appeal is decided by someone other than the person who decided the case appealed (PPR 18.6).");
        }

        if (Status == CaseStatus.Acknowledged || Status == CaseStatus.Reopened)
        {
            var started = Fire(CaseTrigger.Start);
            if (started.IsFailure)
            {
                return started;
            }
        }

        var fired = Fire(CaseTrigger.Decide);
        if (fired.IsFailure)
        {
            return fired;
        }

        Outcome = outcome;
        DecisionReasons = string.IsNullOrWhiteSpace(reasons) ? null : reasons.Trim();
        DecidedBy = deciderId;
        DecidedAt = now;
        ExpectedBy = null;
        Record(CaseEventTypes.Decided, deciderId.ToString(), now, outcome);
        Raise(new CaseDecided(Id, CaseNumber, CaseTypeCode, Family, outcome, ComplainantPartyId, LinkRecords(), now));
        return Result.Success();
    }

    /// <summary>
    /// Command Close: a decided case, or an enquiry answered straight from RECEIVED. <paramref name="reportable"/> is
    /// computed by the application for a complaint (§6.2); <paramref name="retainUntil"/> from the retention in force (OQ-106).
    /// A case referred to an ombud stays open for the ombud's outcome.
    /// </summary>
    public Result Close(bool? reportable, DateOnly retainUntil, Guid? closedBy, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (OmbudReferredAt is not null && OmbudOutcome is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A case referred to an ombud stays open for the ombud's outcome.");
        }

        if (Status == CaseStatus.Received && Family == "ENQUIRY")
        {
            var answered = Fire(CaseTrigger.CloseAnswered);
            if (answered.IsFailure)
            {
                return answered;
            }

            Outcome = "ANSWERED";
            DecidedBy = closedBy;
            DecidedAt = now;
        }
        else
        {
            var fired = Fire(CaseTrigger.Close);
            if (fired.IsFailure)
            {
                return fired;
            }
        }

        ClosedAt = now;
        ExpectedBy = null;
        Reportable = Family == "COMPLAINT" ? reportable : null;
        RetainUntil = retainUntil;
        Record(CaseEventTypes.Closed, actor, now, Reportable is { } r ? (r ? "REPORTABLE" : "NOT_REPORTABLE") : null);
        Raise(new CaseClosed(Id, CaseNumber, Family, Outcome, now));
        return Result.Success();
    }

    /// <summary>Command Withdraw: the complainant no longer pursues it, from any open status; kept as a closed case is (OQ-106).</summary>
    public Result Withdraw(string reason, DateOnly retainUntil, string actor, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict(CaseErrorCodes.Closed, "The case is closed.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A withdrawal needs a reason.");
        }

        var fired = Fire(CaseTrigger.Withdraw);
        if (fired.IsFailure)
        {
            return fired;
        }

        Outcome = "WITHDRAWN";
        DecidedAt = now;
        ClosedAt = now;
        ExpectedBy = null;
        RetainUntil = retainUntil;
        Record(CaseEventTypes.Withdrawn, actor, now, reason.Trim());
        Raise(new CaseClosed(Id, CaseNumber, Family, Outcome, now));
        return Result.Success();
    }

    /// <summary>
    /// Command Reopen (OQ-109: privileged): a closed case is worked again. Its decision is cleared (the history keeps it),
    /// and it is no longer due for disposal.
    /// </summary>
    public Result Reopen(string reason, DateTimeOffset? expectedBy, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A reopening needs a reason.");
        }

        var fired = Fire(CaseTrigger.Reopen);
        if (fired.IsFailure)
        {
            return fired;
        }

        Outcome = null;
        DecisionReasons = null;
        DecidedBy = null;
        DecidedAt = null;
        ClosedAt = null;
        Reportable = null;
        RetainUntil = null;
        ExpectedBy = expectedBy;
        Record(CaseEventTypes.Reopened, actor, now, reason.Trim());
        Raise(new CaseReopened(Id, CaseNumber, Family, now));
        return Result.Success();
    }

    /// <summary>Command ReferToOmbud: a decided case the complainant took to an ombud; it stays open for the outcome (§4).</summary>
    public Result ReferToOmbud(string schemeCode, string? reference, DateTimeOffset referredAt, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (Status != CaseStatus.Decided)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a decided case is referred to an ombud: the ombud requires the insurer's own process first.");
        }

        if (OmbudReferredAt is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The case is already referred to an ombud.");
        }

        OmbudSchemeCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(schemeCode), 20);
        OmbudReference = string.IsNullOrWhiteSpace(reference) ? null : Guard.MaxLength(reference.Trim(), 60);
        OmbudReferredAt = referredAt;
        MarkChanged();
        Record(CaseEventTypes.ReferredToOmbud, actor, now, OmbudSchemeCode);
        Raise(new CaseReferredToOmbud(Id, CaseNumber, OmbudSchemeCode, now));
        return Result.Success();
    }

    /// <summary>Command RecordOmbudOutcome: the ombud's finding on the referral.</summary>
    public Result RecordOmbudOutcome(string outcome, DateTimeOffset at, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (OmbudReferredAt is null || OmbudOutcome is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The case has no ombud referral waiting for an outcome.");
        }

        if (!OmbudOutcomes.All.Contains(outcome))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"An ombud's outcome is one of {string.Join(", ", OmbudOutcomes.All)}.");
        }

        OmbudOutcome = outcome;
        OmbudOutcomeAt = at;
        MarkChanged();
        Record(CaseEventTypes.OmbudOutcome, actor, now, outcome);
        return Result.Success();
    }

    /// <summary>OQ-108: staff let the broker of the case's records see it, though the broker did not raise it.</summary>
    public Result ShareWithBroker(string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (SharedWithBroker)
        {
            return Result.Success();
        }

        SharedWithBroker = true;
        MarkChanged();
        Record(CaseEventTypes.SharedWithBroker, actor, now, null);
        return Result.Success();
    }

    /// <summary>A message on the case: OUT from staff, IN from the complainant's side; while the case is open (§5).</summary>
    public Result AddMessage(string direction, string audience, Guid? authorId, string body, string actor, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A message needs a body.");
        }

        _messages.Add(CaseMessage.Write(Id, direction, audience, authorId, body.Trim(), now));
        MarkChanged();
        Record(direction == CaseMessage.Out ? CaseEventTypes.MessageSent : CaseEventTypes.MessageReceived, actor, now, null);
        return Result.Success();
    }

    /// <summary>The timer job found the current step past its date: the complainant is told of the delay, once per date (§7: CASE_UPDATE).</summary>
    public Result MarkOverdue(string actor, DateTimeOffset now)
    {
        if (!IsOpen || ExpectedBy is not { } due || due > now)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The case is not overdue.");
        }

        MarkChanged();
        Record(CaseEventTypes.Updated, actor, now, OverdueDetail(CurrentStep, due));
        Raise(new CaseUpdated(Id, CaseNumber, Family, ComplainantPartyId, "OVERDUE", due, now));
        return Result.Success();
    }

    /// <summary>The history detail that marks a step overdue at a date, so the timer job tells the complainant once.</summary>
    public static string OverdueDetail(string step, DateTimeOffset due) => $"OVERDUE {step} {due.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";

    /// <summary>A redress step on the case, recorded in its history (the redress itself is its own record).</summary>
    public void RecordRedress(string eventType, string actor, DateTimeOffset now, string? detail)
    {
        MarkChanged();
        Record(eventType, actor, now, detail);
    }

    public IReadOnlyList<CaseLinkRecord> LinkRecords() => _links.Select(l => new CaseLinkRecord(l.LinkType, l.RecordId, l.Reference)).ToList();

    /// <summary>Changes to a closed or withdrawn case are refused (CASE_CLOSED, 409).</summary>
    public Result EnsureOpen() => IsOpen ? Result.Success() : Error.Conflict(CaseErrorCodes.Closed, "The case is closed; reopen it first.");

    private void Record(string eventType, string actor, DateTimeOffset now, string? detail) => _history.Add(CaseHistoryEntry.Write(Id, eventType, actor, now, detail));

    private Result Fire(CaseTrigger trigger)
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

public static class CaseErrorCodes
{
    /// <summary>An appeal is decided by someone other than the person who decided the case appealed (PPR 18.6).</summary>
    [ErrorCode(409, "error.workflow.case_decider_same_person")]
    public const string DeciderSamePerson = "CASE_DECIDER_SAME_PERSON";

    /// <summary>The step's timeline does not allow an extension, or not that long.</summary>
    [ErrorCode(422, "error.workflow.case_timeline_not_extendable")]
    public const string TimelineNotExtendable = "CASE_TIMELINE_NOT_EXTENDABLE";

    /// <summary>The case type does not allow links to records of that type.</summary>
    [ErrorCode(422, "error.workflow.case_link_not_allowed")]
    public const string LinkNotAllowed = "CASE_LINK_NOT_ALLOWED";

    /// <summary>The case is closed or withdrawn: it takes no change until reopened.</summary>
    [ErrorCode(409, "error.workflow.case_closed")]
    public const string Closed = "CASE_CLOSED";

    /// <summary>The case type requires a category (PPR 18.5.3).</summary>
    [ErrorCode(422, "error.workflow.case_category_required")]
    public const string CategoryRequired = "CASE_CATEGORY_REQUIRED";

    /// <summary>A rejection or partial upholding needs its reasons (PPR 18.7.2).</summary>
    [ErrorCode(422, "error.workflow.case_reasons_required")]
    public const string ReasonsRequired = "CASE_REASONS_REQUIRED";

    /// <summary>The case type is not in force, or may not be raised here.</summary>
    [ErrorCode(422, "error.workflow.case_type_not_in_force")]
    public const string TypeNotInForce = "CASE_TYPE_NOT_IN_FORCE";
}
