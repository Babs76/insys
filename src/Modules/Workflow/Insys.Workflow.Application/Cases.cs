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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Cases;
using Insys.Workflow.Domain.Processes;
using Microsoft.Extensions.Logging;

namespace Insys.Workflow.Application;

// Cases (docs/specs/addenda/cases.md, signed off with OQ-101 to OQ-110): enquiries, complaints, disputes and appeals.
// Free text (summary, messages, complainant name and contact, reasons) is never logged, audited in detail or put in an
// event or an exception message (§9).

/// <summary>The privileged functions a handler requires (OQ-109), as TENANT_SWITCH_OFF_MONEY_JOBS is required (OQ-098).</summary>
public static class CaseFunctions
{
    /// <summary>Deciding a case of family APPEAL (403 FUNCTION_NOT_GRANTED without it).</summary>
    public const string DecideAppeal = "CASE_DECIDE_APPEAL";

    /// <summary>The JOB function of the case timeline (overdue) and retention jobs.</summary>
    public const string Timelines = "CASE_TIMELINES";
}

/// <summary>The dashboard figures cases serve (§8, docs/specs/addenda/dashboard-figures.md).</summary>
public static class CaseFigures
{
    public const string CasesOpen = "CASES_OPEN";
    public const string CasesOverdue = "CASES_OVERDUE";
    public const string ComplaintsReceived = "COMPLAINTS_RECEIVED";
    public const string ComplaintsUpheld = "COMPLAINTS_UPHELD";
    public const string ComplaintsRejected = "COMPLAINTS_REJECTED";
    public const string ComplaintsReferredToOmbud = "COMPLAINTS_REFERRED_TO_OMBUD";
    public const string RedressPaid = "REDRESS_PAID";

    public static readonly string[] All = [CasesOpen, CasesOverdue, ComplaintsReceived, ComplaintsUpheld, ComplaintsRejected, ComplaintsReferredToOmbud, RedressPaid];
}

/// <summary>A case type in force (workflow.cfg_case_types).</summary>
public sealed record CaseTypeConfig(string Code, string Family, string ProcessCode, string? DefaultQueueCode, IReadOnlyList<string> LinkTypes,
    IReadOnlyList<string> PortalAudiences, bool RequiresCategory, string? ReviewCaseTypeCode, string AppealRoute, IReadOnlyList<string> OmbudSchemeCodes)
{
    public CaseTypeRules Rules => new(Code, Family, LinkTypes, RequiresCategory);
}

/// <summary>A step's timeline in force (workflow.cfg_case_timelines).</summary>
public sealed record CaseTimeline(string Step, string Duration, string Basis, string? CalendarCode, bool ExtensionAllowed, string? MaxExtension);

/// <summary>An ombud scheme in force (workflow.ref_ombud_schemes): what a rejection notice and portal decision show (PPR 18.9-18.10).</summary>
public sealed record OmbudSchemeInfo(string Code, string Name, string? Contact, string ReferralInformation);

public interface ICaseConfiguration
{
    Task<CaseTypeConfig?> Type(string caseTypeCode, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseTimeline>> Timelines(string caseTypeCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The case type's query window (workflow.cfg_case_reportability), or null.</summary>
    Task<string?> QueryWindow(string caseTypeCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>How long a closed case of the family is kept (workflow.cfg_case_retention), or null for the default.</summary>
    Task<string?> RetainFor(string family, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<OmbudSchemeInfo>> OmbudSchemes(IReadOnlyCollection<string> schemeCodes, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the category is in force for the tenant (global or its own).</summary>
    Task<bool> CategoryExists(string categoryCode, DateOnly day, CancellationToken cancellationToken);
}

public interface ICaseRepository
{
    /// <summary>The case with its links, or null.</summary>
    Task<ServiceCase?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>The tenant's next case number, from workflow.case_number_counter in this transaction ("CASE-000001").</summary>
    Task<string> NextCaseNumber(CancellationToken cancellationToken);

    void Add(ServiceCase serviceCase);

    void Remove(ServiceCase serviceCase);

    Task<CaseRedress?> Redress(Guid id, CancellationToken cancellationToken);

    void Add(CaseRedress redress);

    Task<int> CountHistory(Guid caseId, string eventType, CancellationToken cancellationToken);

    /// <summary>Open cases past their expected date that the complainant has not been told of yet.</summary>
    Task<IReadOnlyList<Guid>> Overdue(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Closed cases past their retention date and not under a legal hold (OQ-106).</summary>
    Task<IReadOnlyList<Guid>> DueForDisposal(DateOnly today, int limit, CancellationToken cancellationToken);
}

public sealed record CaseLinkView([property: AllowedValues("POLICY", "CLAIM", "MEMBERSHIP", "PAYMENT", "INVOICE", "BILLING_ACCOUNT", "PARTY", "PROVIDER", "REMITTANCE", "PAYMENT_RUN", "INTERMEDIARY")]
    string LinkType, Guid RecordId, string Reference, DateTimeOffset AddedAt);

/// <param name="Detail">What happened, in codes and references (never the case's own text, except a reason staff gave for the step).</param>
public sealed record CaseHistoryView(string EventType, DateTimeOffset OccurredAt, string Actor, string? Detail);

public sealed record CaseMessageView(Guid Id, [property: AllowedValues("IN", "OUT")] string Direction, [property: AllowedValues("STAFF", "CUSTOMER", "BROKER", "PROVIDER")] string AuthorAudience,
    string Body, DateTimeOffset SentAt);

/// <param name="LatestRejection">How the redress's approval last ended without being granted (OQ-113), or null.</param>
public sealed record CaseRedressView(Guid Id, Guid CaseId, [property: AllowedValues("COMPENSATION", "GOODWILL")] string RedressType, decimal Amount, string Currency,
    Guid PayeePartyId, Guid? BankAccountId, RedressStatus Status, Guid RequestedBy, DateTimeOffset RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    DateTimeOffset? PaidAt, Guid? PaymentId, long RowVersion, LatestRejection? LatestRejection = null)
{
    public static CaseRedressView Of(CaseRedress r, LatestRejection? latest = null) =>
        new(r.Id, r.CaseId, r.RedressType, r.Amount, r.Currency, r.PayeePartyId, r.BankAccountId, r.Status, r.RequestedBy, r.RequestedAt, r.DecidedBy, r.DecidedAt,
            r.PaidAt, r.PaymentId, r.RowVersion, latest);
}

/// <summary>A case as staff see it (§1, §4). Overdue: open and past its expected date.</summary>
public sealed record CaseView(Guid Id, string CaseNumber, string CaseTypeCode, [property: AllowedValues("ENQUIRY", "COMPLAINT", "DISPUTE", "APPEAL")] string Family,
    CaseStatus Status, [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "WITHDRAWN", "ANSWERED")] string? Outcome, DateTimeOffset ReceivedAt,
    string ReceivedChannel, Guid? ComplainantPartyId, string? ComplainantName, string? ComplainantContact, Guid? OnBehalfOfPartyId,
    [property: AllowedValues("STAFF", "CUSTOMER", "BROKER", "PROVIDER")] string RaisedByAudience, Guid? RaisedByUserId, string? RaisedByIntermediaryCode,
    string? RaisedByProviderId, bool SharedWithBroker, string? CategoryCode, string Summary, bool UpheldAtReceipt, bool? Reportable, string? DecisionReasons,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, Guid? ParentCaseId, Guid? ProcessInstanceId, string? ProductCode, string? OrgNodeCode, DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ExpectedBy, bool Overdue, DateTimeOffset? ClosedAt, DateOnly? RetainUntil, bool LegalHold, string? OmbudSchemeCode, string? OmbudReference,
    DateTimeOffset? OmbudReferredAt, [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "SETTLED", "DISMISSED")] string? OmbudOutcome,
    DateTimeOffset? OmbudOutcomeAt, IReadOnlyList<CaseLinkView> Links, IReadOnlyList<CaseHistoryView> History, IReadOnlyList<CaseMessageView> Messages,
    IReadOnlyList<CaseRedressView> Redress, long RowVersion);

/// <summary>A case in a list: no free text.</summary>
public sealed record CaseSummaryView(Guid Id, string CaseNumber, string CaseTypeCode, [property: AllowedValues("ENQUIRY", "COMPLAINT", "DISPUTE", "APPEAL")] string Family,
    CaseStatus Status, [property: AllowedValues("UPHELD", "PARTLY_UPHELD", "REJECTED", "WITHDRAWN", "ANSWERED")] string? Outcome, string? CategoryCode,
    DateTimeOffset ReceivedAt, DateTimeOffset? ExpectedBy, bool Overdue, Guid? ComplainantPartyId, Guid? ParentCaseId, long RowVersion);

/// <summary>The list filters of GET /workflow/cases (§11).</summary>
public sealed record CaseFilter(string? Family, string? CaseTypeCode, CaseStatus? Status, string? CategoryCode, bool? Overdue, string? LinkType, Guid? LinkId);

/// <summary>Case reading for staff: within the caller's scope (F103) and, for a Health-linked case, with medical_claim.read.</summary>
public interface ICaseQueries
{
    Task<IReadOnlyList<CaseSummaryView>> List(CaseFilter filter, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<CaseView?> Get(Guid id, CancellationToken cancellationToken);

    Task<BuildingBlocks.Application.DashboardSummary> Summary(CancellationToken cancellationToken);

    Task<ComplaintsReport> ComplaintsReport(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>Money by currency (amounts in different currencies are never added together).</summary>
public sealed record CurrencyAmount(string Currency, long Count, decimal Amount);

/// <summary>One category's PPR 18.8.3 figures over the period.</summary>
public sealed record ComplaintCategoryFigures(string CategoryCode, string? PprCategory, long Received, long Upheld, long PartlyUpheld, long Rejected,
    IReadOnlyList<string> RejectionReasons, long Escalated, long ReferredToOmbud, IReadOnlyList<BuildingBlocks.Application.FigurePart> OmbudOutcomes,
    IReadOnlyList<CurrencyAmount> Compensation, IReadOnlyList<CurrencyAmount> Goodwill, long Outstanding, long Reportable, long WithinTimelines);

/// <summary>The complaints report (§8, PPR 18.8.3): complaints received in the period, per category. The Authority's format is open (OQ-105).</summary>
public sealed record ComplaintsReport(DateOnly From, DateOnly To, DateTimeOffset AsOf, IReadOnlyList<ComplaintCategoryFigures> Categories);

// ---------------------------------------------------------------------------------------------------------------------
// Commands and queries
// ---------------------------------------------------------------------------------------------------------------------

public sealed record CaseLinkInput(string LinkType, Guid RecordId, string Reference);

/// <param name="ReceivedAt">When it was received, if earlier than now (a letter, a phone call taken earlier).</param>
/// <param name="ParentCaseId">For an appeal: the case whose decision it appeals; its type is that case type's review type.</param>
public sealed record OpenCase(string CaseTypeCode, string ReceivedChannel, DateTimeOffset? ReceivedAt, Guid? ComplainantPartyId, string? ComplainantName,
    string? ComplainantContact, Guid? OnBehalfOfPartyId, string? CategoryCode, string Summary, bool UpheldAtReceipt, Guid? ParentCaseId,
    IReadOnlyList<CaseLinkInput>? Links) : ICommand<Guid>;

public sealed record AcknowledgeCase(Guid CaseId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AddCaseMessage(Guid CaseId, string Body, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record LinkCaseRecord(Guid CaseId, string LinkType, Guid RecordId, string Reference, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <param name="ExtendBy">An ISO 8601 duration, counted as the step's timeline is (business or calendar).</param>
public sealed record ExtendCaseTimeline(Guid CaseId, string ExtendBy, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record EscalateCase(Guid CaseId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public enum CaseWork
{
    Start,
    AwaitComplainant,
    Resume,
}

public sealed record ProgressCase(Guid CaseId, CaseWork Work, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record DecideCase(Guid CaseId, string Outcome, string? Reasons, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record CloseCase(Guid CaseId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record WithdrawCase(Guid CaseId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record ReopenCase(Guid CaseId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record ReferCaseToOmbud(Guid CaseId, string SchemeCode, string? OmbudReference, DateTimeOffset? ReferredAt, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RecordCaseOmbudOutcome(Guid CaseId, string Outcome, DateTimeOffset? At, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record ShareCaseWithBroker(Guid CaseId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record HoldCase(Guid CaseId, bool Hold, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RecordCaseRedress(Guid CaseId, string RedressType, decimal Amount, string Currency, Guid PayeePartyId, Guid? BankAccountId) : ICommand<Guid>;

public sealed record ListCases(CaseFilter Filter, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<CaseSummaryView>>;

public sealed record GetCase(Guid CaseId) : IQuery<CaseView>;

public sealed record GetCaseRedress(Guid CaseId, Guid RedressId) : IQuery<CaseRedressView>;

public sealed record CasesSummary : IQuery<BuildingBlocks.Application.DashboardSummary>;

public sealed record GetComplaintsReport(DateOnly From, DateOnly To) : IQuery<ComplaintsReport>;

/// <summary>The timeline job: the overdue cases of the tenant now, and the closed cases due for disposal today.</summary>
public sealed record CasesDue(DateTimeOffset Now) : IQuery<CasesDueView>;

public sealed record CasesDueView(IReadOnlyList<Guid> Overdue, IReadOnlyList<Guid> ForDisposal);

public sealed record MarkCaseOverdue(Guid CaseId) : ICommand<bool>;

/// <summary>Disposes of a closed case past its retention (OQ-106), unless it is under a legal hold.</summary>
public sealed record DisposeCase(Guid CaseId) : ICommand<bool>;

public sealed class OpenCaseValidator : AbstractValidator<OpenCase>
{
    public OpenCaseValidator()
    {
        RuleFor(c => c.CaseTypeCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.ReceivedChannel).NotEmpty().MaximumLength(20);
        RuleFor(c => c.Summary).NotEmpty().MaximumLength(20_000);
        RuleFor(c => c.ComplainantName).MaximumLength(200);
        RuleFor(c => c.ComplainantContact).MaximumLength(200);
        RuleFor(c => c.CategoryCode).MaximumLength(30);
        RuleForEach(c => c.Links).ChildRules(l =>
        {
            l.RuleFor(x => x.LinkType).NotEmpty().MaximumLength(20);
            l.RuleFor(x => x.RecordId).NotEmpty();
            l.RuleFor(x => x.Reference).NotEmpty().MaximumLength(60);
        });
    }
}

public sealed class AddCaseMessageValidator : AbstractValidator<AddCaseMessage>
{
    public AddCaseMessageValidator() => RuleFor(c => c.Body).NotEmpty().MaximumLength(20_000);
}

public sealed class LinkCaseRecordValidator : AbstractValidator<LinkCaseRecord>
{
    public LinkCaseRecordValidator()
    {
        RuleFor(c => c.LinkType).NotEmpty().MaximumLength(20);
        RuleFor(c => c.RecordId).NotEmpty();
        RuleFor(c => c.Reference).NotEmpty().MaximumLength(60);
    }
}

public sealed class ExtendCaseTimelineValidator : AbstractValidator<ExtendCaseTimeline>
{
    public ExtendCaseTimelineValidator()
    {
        RuleFor(c => c.ExtendBy).NotEmpty().Must(IsoDurations.IsValid).WithMessage("An ISO 8601 duration, such as P5D.");
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1_000);
    }
}

public sealed class DecideCaseValidator : AbstractValidator<DecideCase>
{
    public DecideCaseValidator()
    {
        RuleFor(c => c.Outcome).NotEmpty().MaximumLength(14);
        RuleFor(c => c.Reasons).MaximumLength(10_000);
    }
}

public sealed class WithdrawCaseValidator : AbstractValidator<WithdrawCase>
{
    public WithdrawCaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1_000);
}

public sealed class ReopenCaseValidator : AbstractValidator<ReopenCase>
{
    public ReopenCaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1_000);
}

public sealed class HoldCaseValidator : AbstractValidator<HoldCase>
{
    public HoldCaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1_000);
}

public sealed class ReferCaseToOmbudValidator : AbstractValidator<ReferCaseToOmbud>
{
    public ReferCaseToOmbudValidator()
    {
        RuleFor(c => c.SchemeCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.OmbudReference).MaximumLength(60);
    }
}

public sealed class RecordCaseRedressValidator : AbstractValidator<RecordCaseRedress>
{
    public RecordCaseRedressValidator()
    {
        RuleFor(c => c.RedressType).NotEmpty();
        RuleFor(c => c.Amount).GreaterThan(0);
        RuleFor(c => c.Currency).NotEmpty().Length(3);
        RuleFor(c => c.PayeePartyId).NotEmpty();
    }
}

public sealed class GetComplaintsReportValidator : AbstractValidator<GetComplaintsReport>
{
    public GetComplaintsReportValidator() => RuleFor(c => c.To).GreaterThanOrEqualTo(c => c.From);
}

// ---------------------------------------------------------------------------------------------------------------------
// Shared services
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Case timelines and dates (ADR-025): a BUSINESS step on its calendar (business days and hours, IBusinessTime), a
/// CALENDAR step in calendar time; retention from the closure's business date; reportability at closure (§6.2).
/// </summary>
public sealed class CaseTimes(ICaseConfiguration configuration, Contracts.IBusinessTime businessTime, ITenantContext tenant, IBusinessDate businessDate)
{
    /// <summary>OQ-106: five years after closure when no retention is in force for the family.</summary>
    public const string DefaultRetention = "P5Y";

    public Task<IReadOnlyList<CaseTimeline>> Timelines(string caseTypeCode, CancellationToken cancellationToken) =>
        configuration.Timelines(caseTypeCode, businessDate.Today, cancellationToken);

    public Task<DateTimeOffset> Add(CaseTimeline timeline, DateTimeOffset start, string duration, CancellationToken cancellationToken) =>
        timeline.Basis == "BUSINESS"
            ? businessTime.Add(tenant.RequireTenantId(), timeline.CalendarCode, start, duration, cancellationToken)
            : Task.FromResult(IsoDurations.Add(start, duration));

    /// <summary>When the step is due for a case received (or reopened) at <paramref name="start"/>, or null when the type has no timeline for it.</summary>
    public async Task<DateTimeOffset?> Due(IReadOnlyList<CaseTimeline> timelines, string step, DateTimeOffset start, CancellationToken cancellationToken) =>
        timelines.FirstOrDefault(t => t.Step == step) is { } timeline ? await Add(timeline, start, timeline.Duration, cancellationToken) : null;

    /// <summary>The date a case closed today is kept until (OQ-106).</summary>
    public async Task<DateOnly> RetainUntil(string family, CancellationToken cancellationToken)
    {
        var retainFor = await configuration.RetainFor(family, businessDate.Today, cancellationToken);
        return IsoDurations.Add(businessDate.Today, IsoDurations.IsValid(retainFor) ? retainFor! : DefaultRetention);
    }

    /// <summary>
    /// §6.2 (PPR 18.1): a complaint is not reportable when upheld by the person who received it, or upheld through the
    /// ordinary query process within the case type's query window (business time from receipt); everything else is.
    /// </summary>
    public async Task<bool?> Reportable(ServiceCase serviceCase, CancellationToken cancellationToken)
    {
        if (serviceCase.Family != CaseFamilies.Complaint)
        {
            return null;
        }

        if (serviceCase.Outcome != CaseOutcomes.Upheld)
        {
            return true;
        }

        if (serviceCase.UpheldAtReceipt)
        {
            return false;
        }

        var window = await configuration.QueryWindow(serviceCase.CaseTypeCode, businessDate.Today, cancellationToken);
        if (!IsoDurations.IsValid(window) || serviceCase.DecidedAt is not { } decided)
        {
            return true;
        }

        var calendar = (await Timelines(serviceCase.CaseTypeCode, cancellationToken)).FirstOrDefault(t => t.Step == CaseSteps.Resolve)?.CalendarCode;
        var end = await businessTime.Add(tenant.RequireTenantId(), calendar, serviceCase.ReceivedAt, window!, cancellationToken);
        return decided > end;
    }
}

/// <summary>Who may read and change a case (§9): staff within their F103 scope, and Health-linked cases only with medical_claim.read.</summary>
public sealed class CaseAccess(IExecutionContext execution)
{
    /// <summary>The links that make a case Health's: a membership, a provider, a remittance or a provider payment run.</summary>
    public static readonly IReadOnlyList<string> HealthLinkTypes = [CaseLinkTypes.Membership, CaseLinkTypes.Provider, CaseLinkTypes.Remittance, CaseLinkTypes.PaymentRun];

    public static bool IsHealth(ServiceCase c) => c.RaisedByProviderId is not null || c.Links.Any(l => HealthLinkTypes.Contains(l.LinkType));

    public bool MayReadHealth => execution.HasPermission(Health.Contracts.HealthPermissions.ClaimRead);

    public bool InScope(ServiceCase c) => (c.ProductCode is null && c.OrgNodeCode is null) || execution.CurrentScope.Allows(c.ProductCode, c.OrgNodeCode);

    /// <summary>Loads a case for a staff change: not found when unreadable, 403 OUT_OF_SCOPE outside the scope, 412 when stale.</summary>
    public async Task<Result<ServiceCase>> Load(ICaseRepository cases, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var c = await cases.Get(id, cancellationToken);
        if (c is null || (IsHealth(c) && !MayReadHealth))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The case was not found.");
        }

        if (!InScope(c))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "The case is outside your scope.");
        }

        return expectedVersion is { } expected && c.RowVersion != expected
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The case has changed since it was read. Read it again and retry.")
            : c;
    }

    public string Actor => execution.ActorId;

    public Guid UserId => Guid.TryParse(execution.ActorId, out var id) && execution.ActorType == ActorType.User ? id : Guid.Empty;
}

/// <summary>
/// Opens a case: its number from the tenant's counter, its timelines (ACKNOWLEDGE, else the resolution step) as the
/// expected date, and its case type's process started on the case (§1), so tasks, queues, SLAs and escalation run it.
/// A case raised by a person is never refused for missing timelines; one raised from an event parks (ADR-022).
/// </summary>
public sealed class CaseOpening(ICaseRepository cases, ICaseConfiguration configuration, CaseTimes times, IWorkflowConfiguration workflow, IProcessRepository processes,
    IEnumerable<BuildingBlocks.Contracts.IBusinessRecordScopes> recordScopes, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit,
    ILogger<CaseOpening> logger)
{
    public const string BusinessRefType = CaseApprovals.CaseBusinessRefType;

    /// <summary>The queue routing gives a task type no queue maps (WorkflowOptions.DefaultQueue).</summary>
    private const string UnroutedQueue = "DEFAULT";

    public async Task<Result<ServiceCase>> Open(CaseTypeConfig type, string channel, DateTimeOffset? receivedAt, Complainant complainant, CaseRaiser raiser, string? categoryCode,
        string summary, bool upheldAtReceipt, Guid? parentCaseId, IReadOnlyList<CaseLinkInput> links, string actor, bool raisedFromEvent, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var received = receivedAt is { } at && at < now ? at : now;
        if (categoryCode is { Length: > 0 } category && !await configuration.CategoryExists(category, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"Category {category} is not in force.");
        }

        var timelines = await times.Timelines(type.Code, cancellationToken);
        if (timelines.Count == 0 && raisedFromEvent)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.CaseTimelineNotConfigured, type.Code);
        }

        var expectedBy = await times.Due(timelines, CaseSteps.Acknowledge, received, cancellationToken)
                         ?? await times.Due(timelines, ServiceCase.ResolutionStep(type.Family), received, cancellationToken);
        var opened = ServiceCase.Open(Guid.CreateVersion7(), await cases.NextCaseNumber(cancellationToken), type.Rules, received, channel, complainant, raiser, categoryCode,
            summary, upheldAtReceipt, parentCaseId, expectedBy, actor, now);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var serviceCase = opened.Value;
        foreach (var link in links)
        {
            var linked = await Link(serviceCase, type, link, actor, cancellationToken);
            if (linked.IsFailure)
            {
                return linked.Error;
            }
        }

        var definition = await workflow.Definition(type.ProcessCode, null, businessDate.Today, cancellationToken);
        if (definition.IsSuccess)
        {
            var route = await workflow.Routing(businessDate.Today, now, cancellationToken);
            var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), definition.Value, BusinessRefType, serviceCase.Id,
                taskType => route(taskType) is var r && r.QueueCode == UnroutedQueue && type.DefaultQueueCode is { } queue ? r with { QueueCode = queue } : r, now);
            if (process.IsSuccess)
            {
                processes.Add(process.Value);
                serviceCase.RunBy(process.Value.Id);
            }
        }
        else
        {
            // The readiness check names the gap; the case still opens, so a complaint is never lost (§3).
            logger.LogWarning("Case type {CaseType} names process {Process}, which is not in force; the case opens without one", type.Code, type.ProcessCode);
        }

        serviceCase.AnnounceOpened(now);
        cases.Add(serviceCase);
        audit.Record(new AuditEntry("CASE", "OpenCase", nameof(ServiceCase), serviceCase.Id.ToString(),
            new Dictionary<string, string?> { ["caseType"] = type.Code, ["number"] = serviceCase.CaseNumber, ["audience"] = raiser.Audience }));
        return serviceCase;
    }

    /// <summary>Links a record: only types the case type allows; the record's product and node (F103) when its module tells them.</summary>
    public async Task<Result> Link(ServiceCase serviceCase, CaseTypeConfig type, CaseLinkInput link, string actor, CancellationToken cancellationToken)
    {
        var linked = serviceCase.LinkRecord(type.Rules, link.LinkType, link.RecordId, link.Reference, actor, clock.UtcNow);
        if (linked.IsFailure)
        {
            return linked;
        }

        // TODO(spec): §1 checks a link through the record's module's public query; only the record types a module answers
        // for (IBusinessRecordScopes: invoices, billing accounts, remittances) are checked and placed (F103) today.
        if (ScopeRefType(link.LinkType) is { } refType && recordScopes.FirstOrDefault(s => s.Covers(refType)) is { } source)
        {
            var scopes = await source.ScopesOf(tenant.RequireTenantId(), refType, link.RecordId, cancellationToken);
            if (scopes is null)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"The {link.LinkType} record was not found.");
            }

            if (scopes.FirstOrDefault(s => s.ProductCode is not null || s.OrgNodeCode is not null) is { } first)
            {
                serviceCase.PlaceAt(first.ProductCode, first.OrgNodeCode);
            }
        }

        return Result.Success();
    }

    private static string? ScopeRefType(string linkType) => linkType switch
    {
        CaseLinkTypes.Invoice => "INVOICE",
        CaseLinkTypes.BillingAccount => "BILLING_ACCOUNT",
        CaseLinkTypes.Remittance => "HEALTH_REMITTANCE",
        _ => null,
    };
}

// ---------------------------------------------------------------------------------------------------------------------
// Staff handlers
// ---------------------------------------------------------------------------------------------------------------------

internal sealed class CaseCommandHandlers(ICaseRepository cases, ICaseConfiguration configuration, CaseOpening opening, CaseTimes times, CaseAccess access,
    ExecutionScope scope, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<OpenCase, Guid>,
    ICommandHandler<AcknowledgeCase, long>,
    ICommandHandler<AddCaseMessage, long>,
    ICommandHandler<LinkCaseRecord, long>,
    ICommandHandler<ExtendCaseTimeline, long>,
    ICommandHandler<EscalateCase, long>,
    ICommandHandler<ProgressCase, long>,
    ICommandHandler<DecideCase, long>,
    ICommandHandler<CloseCase, long>,
    ICommandHandler<WithdrawCase, long>,
    ICommandHandler<ReopenCase, long>,
    ICommandHandler<ReferCaseToOmbud, long>,
    ICommandHandler<RecordCaseOmbudOutcome, long>,
    ICommandHandler<ShareCaseWithBroker, long>,
    ICommandHandler<HoldCase, long>,
    ICommandHandler<RecordCaseRedress, Guid>
{
    private DateOnly Today => businessDate.Today;

    public async Task<Result<Guid>> Handle(OpenCase command, CancellationToken cancellationToken)
    {
        if (await configuration.Type(command.CaseTypeCode, Today, cancellationToken) is not { } type)
        {
            return Error.Validation(CaseErrorCodes.TypeNotInForce, $"Case type {command.CaseTypeCode} is not in force.");
        }

        if (command.ParentCaseId is { } parentId)
        {
            var parent = await access.Load(cases, parentId, null, cancellationToken);
            if (parent.IsFailure)
            {
                return parent.Error;
            }

            var appealable = await Appealable(parent.Value, type, cancellationToken);
            if (appealable.IsFailure)
            {
                return appealable.Error;
            }
        }
        else if (type.Family == CaseFamilies.Appeal)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An appeal names the case whose decision it appeals (parentCaseId).");
        }

        var opened = await opening.Open(type, command.ReceivedChannel, command.ReceivedAt,
            new Complainant(command.ComplainantPartyId, command.ComplainantName, command.ComplainantContact, command.OnBehalfOfPartyId),
            new CaseRaiser(CaseAudiences.Staff, access.UserId == Guid.Empty ? null : access.UserId, null, null), command.CategoryCode, command.Summary,
            command.UpheldAtReceipt, command.ParentCaseId, command.Links ?? [], access.Actor, raisedFromEvent: false, cancellationToken);
        return opened.IsFailure ? opened.Error : opened.Value.Id;
    }

    /// <summary>An appeal is of the appealed case's review type, about a decided case (PPR 18.6).</summary>
    internal async Task<Result> Appealable(ServiceCase parent, CaseTypeConfig appealType, CancellationToken cancellationToken)
    {
        if (parent.DecidedAt is null || parent.Outcome is null or CaseOutcomes.Withdrawn)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a decided case is appealed.");
        }

        var parentType = await configuration.Type(parent.CaseTypeCode, Today, cancellationToken);
        return parentType?.ReviewCaseTypeCode == appealType.Code
            ? Result.Success()
            : Error.Validation(CaseErrorCodes.TypeNotInForce, $"Case {parent.CaseNumber} is appealed as its type's review type, not {appealType.Code}.");
    }

    public Task<Result<long>> Handle(AcknowledgeCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Acknowledge", async c =>
        {
            var timelines = await times.Timelines(c.CaseTypeCode, cancellationToken);
            return c.Acknowledge(await times.Due(timelines, ServiceCase.ResolutionStep(c.Family), c.ReceivedAt, cancellationToken), access.Actor, clock.UtcNow);
        }, cancellationToken);

    public Task<Result<long>> Handle(AddCaseMessage command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "AddMessage", c =>
            Task.FromResult(c.AddMessage(CaseMessage.Out, CaseAudiences.Staff, access.UserId == Guid.Empty ? null : access.UserId, command.Body, access.Actor, clock.UtcNow)),
            cancellationToken);

    public Task<Result<long>> Handle(LinkCaseRecord command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "LinkRecord", async c =>
            await configuration.Type(c.CaseTypeCode, Today, cancellationToken) is { } type
                ? await opening.Link(c, type, new CaseLinkInput(command.LinkType, command.RecordId, command.Reference), access.Actor, cancellationToken)
                : Error.Validation(CaseErrorCodes.TypeNotInForce, $"Case type {c.CaseTypeCode} is not in force."), cancellationToken);

    /// <summary>
    /// §4: a timeline is extended only if its step allows it, by at most its maximum extension from the step's own date,
    /// with a reason; the complainant is told the new date (PPR 18.9).
    /// </summary>
    public Task<Result<long>> Handle(ExtendCaseTimeline command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "ExtendTimeline", async c =>
        {
            var open = c.EnsureOpen();
            if (open.IsFailure)
            {
                return open;
            }

            var step = c.CurrentStep;
            var timelines = await times.Timelines(c.CaseTypeCode, cancellationToken);
            if (timelines.FirstOrDefault(t => t.Step == step) is not { ExtensionAllowed: true } timeline || !IsoDurations.IsValid(timeline.MaxExtension)
                || c.ExpectedBy is not { } current)
            {
                return Error.Validation(CaseErrorCodes.TimelineNotExtendable, $"The {step} timeline of {c.CaseTypeCode} does not allow an extension.");
            }

            var original = await times.Add(timeline, c.ReceivedAt, timeline.Duration, cancellationToken);
            var limit = await times.Add(timeline, original, timeline.MaxExtension!, cancellationToken);
            var extended = await times.Add(timeline, current, command.ExtendBy, cancellationToken);
            if (extended > limit)
            {
                return Error.Validation(CaseErrorCodes.TimelineNotExtendable,
                    $"The {step} timeline may be extended by at most {timeline.MaxExtension} in all.");
            }

            return c.Extend(step, command.ExtendBy, extended, command.Reason, access.Actor, clock.UtcNow);
        }, cancellationToken);

    public Task<Result<long>> Handle(EscalateCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Escalate", async c =>
            c.Escalate(await cases.CountHistory(c.Id, CaseEventTypes.Escalated, cancellationToken) + 1, access.Actor, clock.UtcNow), cancellationToken);

    public Task<Result<long>> Handle(ProgressCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, command.Work.ToString(), c => Task.FromResult(command.Work switch
        {
            CaseWork.Start => c.EnsureOpen() is { IsFailure: true } closed ? closed : c.Start(access.Actor, clock.UtcNow),
            CaseWork.AwaitComplainant => c.AwaitComplainant(true, access.Actor, clock.UtcNow),
            _ => c.AwaitComplainant(false, access.Actor, clock.UtcNow),
        }), cancellationToken);

    /// <summary>
    /// Decide (§4). An appeal is decided only with the privileged function CASE_DECIDE_APPEAL (OQ-109; 403
    /// FUNCTION_NOT_GRANTED), and by someone other than the person who decided the case appealed (CASE_DECIDER_SAME_PERSON).
    /// </summary>
    public Task<Result<long>> Handle(DecideCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Decide", async c =>
        {
            Guid? appealedDecidedBy = null;
            if (c.Family == CaseFamilies.Appeal)
            {
                if (scope.Functions is { } functions && !functions.Usable.Contains(CaseFunctions.DecideAppeal))
                {
                    return Error.Forbidden(ErrorCodes.FunctionNotGranted, $"Deciding an appeal needs the privileged function {CaseFunctions.DecideAppeal} (OQ-109).");
                }

                if (c.ParentCaseId is { } parentId)
                {
                    appealedDecidedBy = (await cases.Get(parentId, cancellationToken))?.DecidedBy;
                }
            }

            return c.Decide(command.Outcome, command.Reasons, access.UserId, appealedDecidedBy, clock.UtcNow);
        }, cancellationToken, details: new Dictionary<string, string?> { ["outcome"] = command.Outcome });

    public Task<Result<long>> Handle(CloseCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Close", async c =>
        {
            var open = c.EnsureOpen();
            if (open.IsFailure)
            {
                return open;
            }

            return c.Close(await times.Reportable(c, cancellationToken), await times.RetainUntil(c.Family, cancellationToken),
                access.UserId == Guid.Empty ? null : access.UserId, access.Actor, clock.UtcNow);
        }, cancellationToken);

    public Task<Result<long>> Handle(WithdrawCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Withdraw", async c =>
            c.Withdraw(command.Reason, await times.RetainUntil(c.Family, cancellationToken), access.Actor, clock.UtcNow), cancellationToken);

    public Task<Result<long>> Handle(ReopenCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "Reopen", async c =>
        {
            var timelines = await times.Timelines(c.CaseTypeCode, cancellationToken);
            var now = clock.UtcNow;
            return c.Reopen(command.Reason, await times.Due(timelines, ServiceCase.ResolutionStep(c.Family), now, cancellationToken), access.Actor, now);
        }, cancellationToken);

    public Task<Result<long>> Handle(ReferCaseToOmbud command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "ReferToOmbud", async c =>
        {
            var type = await configuration.Type(c.CaseTypeCode, Today, cancellationToken);
            if (type is null || !type.OmbudSchemeCodes.Contains(command.SchemeCode))
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Case type {c.CaseTypeCode} does not name ombud scheme {command.SchemeCode}.");
            }

            var now = clock.UtcNow;
            return c.ReferToOmbud(command.SchemeCode, command.OmbudReference, command.ReferredAt is { } at && at <= now ? at : now, access.Actor, now);
        }, cancellationToken, details: new Dictionary<string, string?> { ["scheme"] = command.SchemeCode });

    public Task<Result<long>> Handle(RecordCaseOmbudOutcome command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "RecordOmbudOutcome", c =>
        {
            var now = clock.UtcNow;
            return Task.FromResult(c.RecordOmbudOutcome(command.Outcome, command.At is { } at && at <= now ? at : now, access.Actor, now));
        }, cancellationToken, details: new Dictionary<string, string?> { ["outcome"] = command.Outcome });

    public Task<Result<long>> Handle(ShareCaseWithBroker command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, "ShareWithBroker", c => Task.FromResult(c.ShareWithBroker(access.Actor, clock.UtcNow)), cancellationToken);

    public Task<Result<long>> Handle(HoldCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.ExpectedVersion, command.Hold ? "PlaceLegalHold" : "ReleaseLegalHold",
            c => Task.FromResult(c.HoldForLegalReasons(command.Hold, command.Reason, access.Actor, clock.UtcNow)), cancellationToken);

    /// <summary>OQ-104: redress is recorded REQUESTED and asks Workflow for a second person's approval (CASE_REDRESS), with its amount.</summary>
    public async Task<Result<Guid>> Handle(RecordCaseRedress command, CancellationToken cancellationToken)
    {
        var loaded = await access.Load(cases, command.CaseId, null, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var redress = CaseRedress.Request(Guid.CreateVersion7(), loaded.Value, command.RedressType, command.Amount, command.Currency, command.PayeePartyId,
            command.BankAccountId, access.UserId, clock.UtcNow);
        if (redress.IsFailure)
        {
            return redress.Error;
        }

        cases.Add(redress.Value);
        audit.Record(new AuditEntry("CASE", "RecordRedress", nameof(CaseRedress), redress.Value.Id.ToString(),
            new Dictionary<string, string?> { ["case"] = loaded.Value.Id.ToString(), ["type"] = command.RedressType, ["currency"] = redress.Value.Currency }));
        return redress.Value.Id;
    }

    private async Task<Result<long>> Change(Guid caseId, long expectedVersion, string operation, Func<ServiceCase, Task<Result>> change, CancellationToken cancellationToken,
        Dictionary<string, string?>? details = null)
    {
        var loaded = await access.Load(cases, caseId, expectedVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        audit.Record(new AuditEntry("CASE", operation, nameof(ServiceCase), caseId.ToString(), details));
        var result = await change(loaded.Value);
        return result.IsFailure ? result.Error : loaded.Value.RowVersion + 1;
    }
}

internal sealed class CaseQueryHandlers(ICaseQueries queries, ICaseRepository cases, CaseAccess access, IApprovalHistory approvals, IBusinessDate businessDate) :
    IQueryHandler<ListCases, IReadOnlyList<CaseSummaryView>>,
    IQueryHandler<GetCase, CaseView>,
    IQueryHandler<GetCaseRedress, CaseRedressView>,
    IQueryHandler<CasesSummary, BuildingBlocks.Application.DashboardSummary>,
    IQueryHandler<GetComplaintsReport, ComplaintsReport>,
    IQueryHandler<CasesDue, CasesDueView>
{
    public async Task<Result<IReadOnlyList<CaseSummaryView>>> Handle(ListCases query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.Filter, query.AfterId, query.Limit, cancellationToken));

    public async Task<Result<CaseView>> Handle(GetCase query, CancellationToken cancellationToken) =>
        await queries.Get(query.CaseId, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    public async Task<Result<CaseRedressView>> Handle(GetCaseRedress query, CancellationToken cancellationToken)
    {
        var loaded = await access.Load(cases, query.CaseId, null, cancellationToken);
        if (loaded.IsFailure || await cases.Redress(query.RedressId, cancellationToken) is not { } redress || redress.CaseId != query.CaseId)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The redress was not found.");
        }

        return CaseRedressView.Of(redress, await approvals.Latest(CaseApprovals.RedressBusinessRefType, redress.Id, cancellationToken));
    }

    public async Task<Result<BuildingBlocks.Application.DashboardSummary>> Handle(CasesSummary query, CancellationToken cancellationToken) =>
        await queries.Summary(cancellationToken);

    public async Task<Result<ComplaintsReport>> Handle(GetComplaintsReport query, CancellationToken cancellationToken) =>
        await queries.ComplaintsReport(query.From, query.To, cancellationToken);

    public async Task<Result<CasesDueView>> Handle(CasesDue query, CancellationToken cancellationToken) =>
        new CasesDueView(await cases.Overdue(query.Now, 500, cancellationToken),
            await cases.DueForDisposal(businessDate.Today, 500, cancellationToken));
}

/// <summary>The CASE_TIMELINES job's work on one case (a service identity, one case per transaction).</summary>
internal sealed class CaseJobHandlers(ICaseRepository cases, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<MarkCaseOverdue, bool>,
    ICommandHandler<DisposeCase, bool>
{
    public const string Actor = "case-timelines";

    public async Task<Result<bool>> Handle(MarkCaseOverdue command, CancellationToken cancellationToken) =>
        await cases.Get(command.CaseId, cancellationToken) is { } c && c.MarkOverdue(Actor, clock.UtcNow).IsSuccess;

    public async Task<Result<bool>> Handle(DisposeCase command, CancellationToken cancellationToken)
    {
        if (await cases.Get(command.CaseId, cancellationToken) is not { IsOpen: false, LegalHold: false, RetainUntil: { } until } c || until >= businessDate.Today)
        {
            return false;
        }

        audit.Record(new AuditEntry("CASE", "DisposeCase", nameof(ServiceCase), c.Id.ToString(),
            new Dictionary<string, string?> { ["number"] = c.CaseNumber, ["retainUntil"] = until.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }));
        cases.Remove(c);
        return true;
    }
}
