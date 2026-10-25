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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Application;
using Insys.Workflow.Domain.Cases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Insys.Workflow.Api;

/// <param name="ReceivedChannel">How it came in: a communication.ref_channels code, or PORTAL, PHONE, WALK_IN, LETTER, OMBUD.</param>
/// <param name="ReceivedAt">When it was received, if before now; otherwise now.</param>
/// <param name="ComplainantPartyId">The complainant, when a party; otherwise complainantName (and complainantContact).</param>
/// <param name="CategoryCode">workflow/static/case-categories; required when the case type requires a category (PPR 18.5.3).</param>
/// <param name="UpheldAtReceipt">Upheld at once by the person who received it (PPR 18.1): such a complaint is not reportable.</param>
/// <param name="ParentCaseId">For an appeal: the decided case it appeals; the type is that case type's review type.</param>
public sealed record OpenCaseRequest(string CaseTypeCode, string ReceivedChannel, DateTimeOffset? ReceivedAt, Guid? ComplainantPartyId, string? ComplainantName,
    string? ComplainantContact, Guid? OnBehalfOfPartyId, string? CategoryCode, string Summary, bool? UpheldAtReceipt, Guid? ParentCaseId,
    IReadOnlyList<CaseLinkInput>? Links);

public sealed record CaseMessageRequest(string Body);

public sealed record CaseLinkRequest(string LinkType, Guid RecordId, string Reference);

/// <param name="ExtendBy">An ISO 8601 duration (P5D), counted as the step's timeline is (business or calendar days).</param>
/// <param name="Reason">Told to the complainant with the new date (PPR 18.9).</param>
public sealed record ExtendCaseRequest(string ExtendBy, string Reason);

/// <param name="Outcome">ANSWERED for an enquiry; UPHELD, PARTLY_UPHELD or REJECTED otherwise.</param>
/// <param name="Reasons">Required for REJECTED and PARTLY_UPHELD (PPR 18.7.2).</param>
public sealed record DecideCaseRequest(string Outcome, string? Reasons);

public sealed record CaseReasonRequest(string Reason);

/// <param name="RedressType">COMPENSATION or GOODWILL.</param>
/// <param name="BankAccountId">The payee's verified bank account Payments pays into.</param>
public sealed record CaseRedressRequest(string RedressType, decimal Amount, string Currency, Guid PayeePartyId, Guid? BankAccountId);

/// <param name="SchemeCode">One of the ombud schemes the case type names (workflow/static/ombud-schemes).</param>
public sealed record OmbudReferralRequest(string SchemeCode, string? OmbudReference, DateTimeOffset? ReferredAt);

/// <param name="Outcome">UPHELD, PARTLY_UPHELD, REJECTED, SETTLED or DISMISSED.</param>
public sealed record OmbudOutcomeRequest(string Outcome, DateTimeOffset? At);

/// <param name="OnBehalfOfPartyId">A broker's complaint for a client: the client, whose policy (one the broker brokers) the case links.</param>
public sealed record PortalCaseRequest(string CaseTypeCode, string? CategoryCode, string Summary, Guid? OnBehalfOfPartyId, IReadOnlyList<CaseLinkInput>? Links);

public sealed record PortalAppealRequest(string Summary);

/// <summary>Cases (docs/specs/addenda/cases.md §11): staff at /workflow/cases, portal users at /portal/cases.</summary>
internal static class CaseEndpoints
{
    private const string Effects = "the case's events reach Communications (the complainant's notice) and, for a decided dispute or appeal upheld, the claim's "
        + "module (reassessment); read the case again for its own state.";

    public static void Map(IEndpointRouteBuilder api, string tag)
    {
        var cases = api.MapGroup("/workflow/cases").WithTags(tag).RefuseOperatorTenant();
        MapReads(cases);
        MapChanges(cases);
        MapPortal(api.MapGroup("/portal/cases").WithTags(tag).RefuseOperatorTenant());
    }

    private static void MapReads(RouteGroupBuilder cases)
    {
        cases.MapGet("/", async (string? family, string? type, string? status, string? category, bool? overdue, string? linkType, Guid? linkId, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                CaseStatus? parsed = null;
                if (status is not null)
                {
                    if (!Enum.TryParse<CaseStatus>(status.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var value))
                    {
                        return Problems.For(ErrorCodes.ValidationFailed, "status is not a case status.", StatusCodes.Status422UnprocessableEntity);
                    }

                    parsed = value;
                }

                var size = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListCases(new CaseFilter(family, type, parsed, category, overdue, linkType, linkId), afterId, size), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<CaseSummaryView>(items, items.Count == size ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<CaseSummaryView>>(StatusCodes.Status200OK)
            .WithName("WorkflowListCases")
            .WithSummary("List cases: by family, type, status, category, overdue, or a linked record (linkType, linkId)")
            .WithDescription("Within the caller's scope (F103): a case is in scope when its product and node are, and a case with no scoped record is tenant-wide. "
                + "A Health-linked case (a membership, provider, remittance or payment run, or raised by a provider) is listed only to callers with medical_claim.read. "
                + "status is a case status in any case (InProgress or IN_PROGRESS); overdue means open and past its expected date. Oldest first by ID, cursor paged.")
            .AlsoChecks(Health.Contracts.HealthPermissions.ClaimRead, "a case is Health-linked: without it the case is left out")
            .RequirePermission(CasePermissions.Read);

        cases.MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new CasesSummary(), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("WorkflowCasesSummary")
            .WithSummary("Case figures (§8): open by family and status, overdue, complaints received, upheld, rejected, referred to an ombud, redress paid per currency")
            .WithDescription("CASES_OPEN is broken down by FAMILY:STATUS; COMPLAINTS_UPHELD by UPHELD and PARTLY_UPHELD. Complaint figures count every complaint the "
                + "caller may see (F103, and Health-linked cases only with medical_claim.read); REDRESS_PAID is the paid redress per currency.")
            .RequirePermission(CasePermissions.Read);

        cases.MapGet("/complaints-report", async (DateOnly from, DateOnly to, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetComplaintsReport(from, to), ct)).ToHttp(Results.Ok))
            .Produces<ComplaintsReport>(StatusCodes.Status200OK)
            .WithName("WorkflowComplaintsReport")
            .WithSummary("The PPR 18.8.3 complaints report: complaints received from..to (business dates), per category")
            .WithDescription("Per category: received, upheld, partly upheld, rejected (with the reasons given), escalated, referred to an ombud with the ombuds' "
                + "outcomes, compensation and goodwill approved or paid (number and amount per currency), outstanding, reportable (§6.2) and within their timelines "
                + "(never overdue). The format of the return to the Authority is open (OQ-105).")
            .RequirePermission(CasePermissions.ReportRead);

        cases.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetCase(id), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<CaseView>(StatusCodes.Status200OK)
            .WithName("WorkflowGetCase")
            .WithSummary("A case with its links, history, messages and redress")
            .AlsoChecks(Health.Contracts.HealthPermissions.ClaimRead, "the case is Health-linked: without it the case is not found")
            .RequirePermission(CasePermissions.Read);

        cases.MapGet("/{id:guid}/redress/{redressId:guid}", async (Guid id, Guid redressId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetCaseRedress(id, redressId), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<CaseRedressView>(StatusCodes.Status200OK)
            .WithName("WorkflowGetCaseRedress")
            .WithSummary("A redress on a case, with how its approval last ended without being granted (latestRejection, OQ-113)")
            .RequirePermission(CasePermissions.Read);
    }

    private static void MapChanges(RouteGroupBuilder cases)
    {
        cases.MapPost("/", async (OpenCaseRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var opened = await dispatcher.Send(new OpenCase(r.CaseTypeCode, r.ReceivedChannel, r.ReceivedAt, r.ComplainantPartyId, r.ComplainantName, r.ComplainantContact,
                    r.OnBehalfOfPartyId, r.CategoryCode, r.Summary, r.UpheldAtReceipt == true, r.ParentCaseId, r.Links), ct);
                return opened.IsFailure
                    ? Problems.For(opened.Error)
                    : (await dispatcher.Query(new GetCase(opened.Value), ct)).ToHttp(view =>
                    {
                        Concurrency.SetETag(http, view.RowVersion);
                        return Results.Created($"/api/v1/workflow/cases/{view.Id}", view);
                    });
            })
            .Produces<CaseView>(StatusCodes.Status201Created)
            .WithName("WorkflowOpenCase")
            .WithSummary("Open (receive) a case: numbered, its case type's process started, its first timeline as the expected date")
            .WithDescription("422 CASE_TYPE_NOT_IN_FORCE for a type not in force; CASE_CATEGORY_REQUIRED when the type requires a category; CASE_LINK_NOT_ALLOWED "
                + "for a link type the case type does not allow. A case raised by a person is never refused for missing timelines: it opens without an "
                + "expected date. An appeal names parentCaseId, a decided case whose type's review type is this type.")
            .WithEventualEffects("workflow.CaseOpened reaches Communications and other modules; the case type's process has its first task in its queue.")
            .RequirePermission(CasePermissions.Create)
            .RequireIdempotencyKey();

        Versioned(cases, "/{id:guid}/acknowledge", "WorkflowAcknowledgeCase", "Acknowledge the case to the complainant (CASE_ACKNOWLEDGED, OQ-107)",
            CasePermissions.Update, (id, v) => new AcknowledgeCase(id, v),
            "The expected date becomes the resolution step's (RESOLVE; REVIEW for an appeal) from receipt.")
            .WithEventualEffects("workflow.CaseAcknowledged: Communications sends CASE_ACKNOWLEDGED.");
        Versioned<CaseMessageRequest>(cases, "/{id:guid}/messages", "WorkflowAddCaseMessage", "Send the complainant a message on the case",
            CasePermissions.Update, (id, r, v) => new AddCaseMessage(id, r.Body, v), "While the case is open; the portal shows it (§5).");
        Versioned<CaseLinkRequest>(cases, "/{id:guid}/links", "WorkflowLinkCaseRecord", "Link a record to the case",
            CasePermissions.Update, (id, r, v) => new LinkCaseRecord(id, r.LinkType, r.RecordId, r.Reference, v),
            "422 CASE_LINK_NOT_ALLOWED for a link type the case type does not allow. An invoice, billing account or remittance is checked with its module, "
            + "and places the case in that record's product and node (F103).");
        Versioned<ExtendCaseRequest>(cases, "/{id:guid}/extend", "WorkflowExtendCaseTimeline", "Extend the current step's timeline, with a reason",
            CasePermissions.Update, (id, r, v) => new ExtendCaseTimeline(id, r.ExtendBy, r.Reason, v),
            "422 CASE_TIMELINE_NOT_EXTENDABLE when the step's timeline does not allow an extension, or the new date would be more than its maxExtension after "
            + "the step's own date.")
            .WithEventualEffects("workflow.CaseUpdated (EXTENDED): Communications tells the complainant the new date (CASE_UPDATE).");
        Versioned(cases, "/{id:guid}/escalate", "WorkflowEscalateCase", "Escalate the case to the next level", CasePermissions.Update, (id, v) => new EscalateCase(id, v), null)
            .WithEventualEffects("workflow.CaseEscalated reaches Communications.");
        Versioned(cases, "/{id:guid}/start", "WorkflowStartCase", "Start work on an acknowledged or reopened case (IN_PROGRESS)", CasePermissions.Update,
            (id, v) => new ProgressCase(id, CaseWork.Start, v), null);
        Versioned(cases, "/{id:guid}/await-complainant", "WorkflowCaseAwaitComplainant", "Wait for the complainant (AWAITING_COMPLAINANT)", CasePermissions.Update,
            (id, v) => new ProgressCase(id, CaseWork.AwaitComplainant, v), null);
        Versioned(cases, "/{id:guid}/resume", "WorkflowResumeCase", "Resume work after the complainant answered (IN_PROGRESS)", CasePermissions.Update,
            (id, v) => new ProgressCase(id, CaseWork.Resume, v), null);
        Versioned<DecideCaseRequest>(cases, "/{id:guid}/decide", "WorkflowDecideCase", "Decide the case, with reasons", CasePermissions.Decide,
            (id, r, v) => new DecideCase(id, r.Outcome, r.Reasons, v),
            "An appeal is decided only by a holder of the privileged function CASE_DECIDE_APPEAL (403 FUNCTION_NOT_GRANTED, OQ-109) and by someone other than "
            + "the person who decided the case appealed (409 CASE_DECIDER_SAME_PERSON, PPR 18.6). 422 CASE_REASONS_REQUIRED for a rejection without reasons.")
            .WithEventualEffects(Effects);
        Versioned(cases, "/{id:guid}/close", "WorkflowCloseCase", "Close a decided case (or answer an enquiry straight from RECEIVED)", CasePermissions.Update,
            (id, v) => new CloseCase(id, v),
            "A complaint's reportable flag is computed now (§6.2); retainUntil is set from the family's retention (5 years when none is in force, OQ-106). A case "
            + "referred to an ombud stays open for the ombud's outcome.")
            .WithEventualEffects("workflow.CaseClosed reaches Communications.");
        Versioned<CaseReasonRequest>(cases, "/{id:guid}/withdraw", "WorkflowWithdrawCase", "Withdraw the case, with a reason", CasePermissions.Update,
            (id, r, v) => new WithdrawCase(id, r.Reason, v), "From any open status; kept as a closed case is (OQ-106).")
            .WithEventualEffects("workflow.CaseClosed (outcome WITHDRAWN).");
        Versioned<CaseReasonRequest>(cases, "/{id:guid}/reopen", "WorkflowReopenCase", "Reopen a closed case, with a reason (privileged, OQ-109)", CasePermissions.Update,
            (id, r, v) => new ReopenCase(id, r.Reason, v), "Its decision is cleared (the history keeps it) and its retention date removed.")
            .WithEventualEffects("workflow.CaseReopened reaches Communications.");
        Versioned<OmbudReferralRequest>(cases, "/{id:guid}/ombud-referral", "WorkflowReferCaseToOmbud", "Record that a decided case was referred to an ombud",
            CasePermissions.Update, (id, r, v) => new ReferCaseToOmbud(id, r.SchemeCode, r.OmbudReference, r.ReferredAt, v),
            "The scheme is one the case type names; the case stays open for the ombud's outcome.")
            .WithEventualEffects("workflow.CaseReferredToOmbud reaches Communications.");
        Versioned<OmbudOutcomeRequest>(cases, "/{id:guid}/ombud-outcome", "WorkflowRecordCaseOmbudOutcome", "Record the ombud's outcome on the referral",
            CasePermissions.Update, (id, r, v) => new RecordCaseOmbudOutcome(id, r.Outcome, r.At, v), null);
        Versioned(cases, "/{id:guid}/share-with-broker", "WorkflowShareCaseWithBroker", "Let the broker of the case's policies see it (OQ-108)", CasePermissions.Update,
            (id, v) => new ShareCaseWithBroker(id, v), "A broker sees the cases it raised; a shared case is also seen by the broker of a policy the case links.");
        Versioned<CaseReasonRequest>(cases, "/{id:guid}/legal-hold", "WorkflowPlaceCaseLegalHold", "Place a legal hold on the case (privileged)", CasePermissions.Update,
            (id, r, v) => new HoldCase(id, true, r.Reason, v), "Open or closed: a held case is not disposed of after its retention date (OQ-106).");
        Versioned<CaseReasonRequest>(cases, "/{id:guid}/legal-hold/release", "WorkflowReleaseCaseLegalHold", "Release the case's legal hold (privileged)",
            CasePermissions.Update, (id, r, v) => new HoldCase(id, false, r.Reason, v), null);

        cases.MapPost("/{id:guid}/redress", async (Guid id, CaseRedressRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var recorded = await dispatcher.Send(new RecordCaseRedress(id, r.RedressType, r.Amount, r.Currency, r.PayeePartyId, r.BankAccountId), ct);
                return recorded.IsFailure
                    ? Problems.For(recorded.Error)
                    : (await dispatcher.Query(new GetCaseRedress(id, recorded.Value), ct)).ToHttp(view =>
                    {
                        Concurrency.SetETag(http, view.RowVersion);
                        return Results.Created($"/api/v1/workflow/cases/{id}/redress/{view.Id}", view);
                    });
            })
            .Produces<CaseRedressView>(StatusCodes.Status201Created)
            .WithName("WorkflowRecordCaseRedress")
            .WithSummary("Record compensation or goodwill on an open case, for a second person's approval (privileged, OQ-104, OQ-109)")
            .WithDescription("Recorded REQUESTED with an approval of type CASE_REDRESS for its amount; approved by someone else (APPROVED) it is paid through "
                + "Payments (PAID); rejected, expired or withdrawn it is REJECTED.")
            .WithEventualEffects("the approval appears in Workflow's approvals; once approved, workflow.CaseRedressApproved reaches Payments, which pays it; read the "
                + "redress again for its status.")
            .RequirePermission(CasePermissions.Redress)
            .RequireIdempotencyKey();
    }

    private static void MapPortal(RouteGroupBuilder portal)
    {
        string[] all = [ExternalSubject.Party, ExternalSubject.Intermediary, ExternalSubject.Provider];
        portal.MapGet("/", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var size = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListPortalCases(afterId, size), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<PortalCaseSummaryView>(items, items.Count == size ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<PortalCaseSummaryView>>(StatusCodes.Status200OK)
            .WithName("PortalListCases")
            .WithSummary("The portal user's cases: a member's own, a provider's own, a broker's raised or shared with it (OQ-108)")
            .RequirePermission(CasePermissions.Read).ForPortal(all);

        portal.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetPortalCase(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalCaseView>(StatusCodes.Status200OK)
            .WithName("PortalGetCase")
            .WithSummary("A case of the portal user: status, dates, messages, the decision with reasons and the review and ombud options")
            .RequirePermission(CasePermissions.Read).ForPortal(all);

        portal.MapPost("/", async (PortalCaseRequest r, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var raised = await dispatcher.Send(new RaisePortalCase(r.CaseTypeCode, r.CategoryCode, r.Summary, r.OnBehalfOfPartyId, r.Links), ct);
                return raised.IsFailure
                    ? Problems.For(raised.Error)
                    : (await dispatcher.Query(new GetPortalCase(raised.Value), ct)).ToHttp(view => Results.Created($"/api/v1/portal/cases/{view.Id}", view));
            })
            .Produces<PortalCaseView>(StatusCodes.Status201Created)
            .WithName("PortalRaiseCase")
            .WithSummary("Raise a case: a case type whose portal audiences name this portal, about records of the user's own")
            .WithDescription("403 OUT_OF_SCOPE when a linked record is not the user's (§5). A broker's complaint for a client gives onBehalfOfPartyId and links "
                + "the client's policy that the broker brokers.")
            .WithEventualEffects("workflow.CaseOpened reaches Communications.")
            .RequirePermission(CasePermissions.Create).RequireIdempotencyKey().ForPortal(all);

        portal.MapPost("/{id:guid}/messages", async (Guid id, CaseMessageRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AddPortalCaseMessage(id, r.Body), ct)).ToHttp(_ => Results.NoContent()))
            .Produces(StatusCodes.Status204NoContent)
            .WithName("PortalAddCaseMessage")
            .WithSummary("Send a message on one's own open case")
            .RequirePermission(CasePermissions.Create).ForPortal(all);

        portal.MapPost("/{id:guid}/appeal", async (Guid id, PortalAppealRequest r, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var appealed = await dispatcher.Send(new AppealPortalCase(id, r.Summary), ct);
                return appealed.IsFailure
                    ? Problems.For(appealed.Error)
                    : (await dispatcher.Query(new GetPortalCase(appealed.Value), ct)).ToHttp(view => Results.Created($"/api/v1/portal/cases/{view.Id}", view));
            })
            .Produces<PortalCaseView>(StatusCodes.Status201Created)
            .WithName("PortalAppealCase")
            .WithSummary("Appeal one's own decided case: an internal review of its type's review type (PPR 18.6)")
            .WithEventualEffects("workflow.CaseOpened reaches Communications.")
            .RequirePermission(CasePermissions.Create).RequireIdempotencyKey().ForPortal(all);
    }

    private static RouteHandlerBuilder Versioned(RouteGroupBuilder group, string pattern, string name, string summary, string permission,
        Func<Guid, long, ICommand<long>> command, string? description)
    {
        var builder = group.MapPost(pattern, (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Send(http, version => dispatcher.Send(command(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName(name)
            .WithSummary(summary)
            .RequirePermission(permission);
        return description is null ? builder : builder.WithDescription(description);
    }

    private static RouteHandlerBuilder Versioned<TRequest>(RouteGroupBuilder group, string pattern, string name, string summary, string permission,
        Func<Guid, TRequest, long, ICommand<long>> command, string? description)
    {
        var builder = group.MapPost(pattern, (Guid id, TRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Send(http, version => dispatcher.Send(command(id, request, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName(name)
            .WithSummary(summary)
            .RequirePermission(permission);
        return description is null ? builder : builder.WithDescription(description);
    }

    private static async Task<IResult> Send(HttpContext http, Func<long, Task<Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }
}
