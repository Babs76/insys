/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using System.Text.Json;
using Insys.Assist.Application;
using Insys.Assist.Contracts;
using Insys.Assist.Domain;
using Insys.Assist.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Assist.Api;

public sealed record AssistSettingsRequest(string Jurisdiction, string InferenceRegion, string? LegalBasis, bool ExplanationsEnabled);

/// <param name="ReviewDocumentId">The signed review document, stored in Documents, kept as evidence on the version (§5.3).</param>
public sealed record PackReviewRequest(string Partner, string ReviewerName, DateOnly ReviewedOn, Guid ReviewDocumentId);

/// <param name="ContentHash">The version's content hash as the approver read it on the review sheet: the approval binds to it (§5.3).</param>
public sealed record PackApprovalRequest(string ContentHash);

/// <param name="Decision">ACCEPTED or REJECTED. A Missing value can't be accepted.</param>
public sealed record SetupDecisionRequest(string Decision, string? Note);

/// <param name="Code">The refusal's error code.</param>
/// <param name="Fields">The names of the fields in error, as the refusal's field errors named them. Only how many there are is sent to a model: the names are the client's (D-15).</param>
/// <param name="RecordType">The record the refusal was about, by type and ID, for the "Based on" link and the audit; never trusted for its data (AIA-039).</param>
public sealed record ExplainRefusalBody(string Code, IReadOnlyList<string>? Fields, string? RecordType, Guid? RecordId, Guid? CorrelationId);

public sealed record ExplainPremiumBody(Guid CalculationId, Guid? CorrelationId);

/// <param name="Reason">WRONG, UNCLEAR, INCOMPLETE or OTHER; needed when the explanation isn't helpful.</param>
/// <param name="Comment">At most 500 characters, kept for evaluation and never sent to a model.</param>
public sealed record ExplanationFeedbackBody(bool Helpful, string? Reason, string? Comment);

/// <summary>AI Assist (Wave 7, docs/specs/addenda/ai-assist.md): the AI prepares and a person decides. INSYS works unchanged without it.</summary>
public sealed class AssistModule : IModule
{
    public string Name => "Assist";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => AssistFunctions.All;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAssistInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var assist = api.MapGroup("/assist").WithTags(Name);
        assist.MapGet("/settings", async (IDispatcher d, HttpContext http, CancellationToken ct) => (await d.Query(new GetAssistSettings(), ct)).ToHttp(view =>
            {
                if (view.RowVersion > 0)
                {
                    Concurrency.SetETag(http, view.RowVersion);
                }

                return Results.Ok(view);
            }))
            .Produces<AssistSettingsView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("AssistGetSettings").WithSummary("The tenant's AI Assist settings: jurisdiction, where inference runs, legal basis (AIA-050)")
            .RequirePermission(AssistPermissions.SettingsManage);
        assist.MapGet("/usage", async (string? month, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAssistUsage(month), ct)).ToHttp(Results.Ok))
            .Produces<AssistUsageView>(StatusCodes.Status200OK)
            .WithName("AssistGetUsage").WithSummary("A month's use against the cap: tokens and runs (addendum §6). Billing is outside INSYS")
            .RequirePermission(AssistPermissions.SettingsManage);
        assist.MapPut("/settings", async (AssistSettingsRequest r, IDispatcher d, HttpContext http, CancellationToken ct) =>
                Concurrency.OptionalExpectedVersion(http.Request) is { IsSuccess: true } expected
                    ? (await d.Send(new SetAssistSettings(r.Jurisdiction, r.InferenceRegion, r.LegalBasis, r.ExplanationsEnabled, expected.Value), ct)).ToHttp(view =>
                    {
                        Concurrency.SetETag(http, view.RowVersion);
                        return Results.Ok(view);
                    })
                    : Results.BadRequest())
            .Produces<AssistSettingsView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("AssistSetSettings").WithSummary("Set them. If-Match is needed once they exist")
            .RequirePermission(AssistPermissions.SettingsManage);

        var explanations = assist.MapGroup("/explanations");
        explanations.MapPost("/refusals", async (ExplainRefusalBody b, Explainer explainer, CancellationToken ct) =>
                (await explainer.Refusal(new ExplainRefusalRequest(b.Code, b.Fields, b.RecordType, b.RecordId, b.CorrelationId), ct)).ToHttp(Results.Ok))
            .Produces<ExplanationView>(StatusCodes.Status200OK)
            .WithName("AssistExplainRefusal")
            .WithSummary("Explain a refusal the caller received (AIA-039): its code, the names of its fields in error, the record and the correlation ID")
            .WithDescription("Built from the error catalogue's wording for the code. The screen's messages, the fields' names and the record are never sent to a model: the record is kept for basedOn and the audit. When no explanation can be shown, outcome says why and facts hold the plain refusal (AIA-033, P8).")
            .RequirePermission(AssistPermissions.ExplainRefusal)
            .RequireIdempotencyKey();
        explanations.MapPost("/premiums", async (ExplainPremiumBody b, Explainer explainer, CancellationToken ct) =>
                (await explainer.Premium(new ExplainPremiumRequest(b.CalculationId, b.CorrelationId), ct)).ToHttp(Results.Ok))
            .Produces<ExplanationView>(StatusCodes.Status200OK)
            .WithName("AssistExplainPremium")
            .WithSummary("Explain a recorded premium calculation (AIA-032), which the caller must be able to read")
            .WithDescription("Built only from the recorded calculation's product and lines (step, configured factor, value used, amount with its currency); not its inputs, and for Health not its factor values (AIA-038). When no explanation can be shown, facts hold the plain breakdown (AIA-033, P8).")
            .RequirePermission(AssistPermissions.ExplainPremium)
            .AlsoChecks(Rating.Contracts.RatingPermissions.CalculationRead, "explaining a calculation needs reading it")
            .RequireIdempotencyKey();
        explanations.MapPost("/{id:guid}/feedback", async (Guid id, ExplanationFeedbackBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GiveExplanationFeedback(id, b.Helpful, b.Reason, b.Comment), ct)).ToHttp(feedback => Results.Created($"/api/v1/assist/explanations/{id}/feedback/{feedback}", new ExplanationFeedbackView(feedback))))
            .Produces<ExplanationFeedbackView>(StatusCodes.Status201Created)
            .WithName("AssistGiveExplanationFeedback")
            .WithSummary("Mark an explanation you were shown helpful, or unhelpful or wrong with a reason (AIA-037)")
            .WithDescription("Only the user an explanation was shown to marks it, so the route needs no function of its own: anyone else gets 404.")
            .RequireSignedInUser()
            .RequireIdempotencyKey();

        MapPacks(api);
        MapTenantSetups(api);
    }

    /// <summary>
    /// AI-3's wizard, its pack-only path (AI Assist SRS §5A.3 and §5A.5; AIA-090): the questionnaire, the draft assembled from the packs with each value flagged
    /// Pack or Missing, and the reviewer's decision on each. Only while the tenant is onboarding (AIA-086). Submitting it as the SETUP change set is batch 7.7.
    /// </summary>
    private void MapTenantSetups(IEndpointRouteBuilder api)
    {
        var setups = api.MapGroup("/assist/tenant-setups").WithTags(Name);
        setups.MapPost("/", async (SetupAnswers answers, IDispatcher d, HttpContext http, CancellationToken ct) => (await d.Send(new StartTenantSetup(answers), ct)).ToHttp(view =>
            {
                Concurrency.SetETag(http, view.RowVersion);
                return Results.Created($"/api/v1/assist/tenant-setups/{view.Id}", view);
            }))
            .Produces<SetupRunView>(StatusCodes.Status201Created).ReturnsETag()
            .WithName("AssistStartTenantSetup")
            .WithSummary("Start the wizard with the questionnaire's answers: the draft is assembled from the packs, each value Pack or Missing (AIA-082). Onboarding only")
            .RequirePermission(AssistPermissions.TenantSetupRun).RequireIdempotencyKey();
        setups.MapGet("/{id:guid}", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) => (await d.Query(new GetTenantSetup(id), ct)).ToHttp(view =>
            {
                Concurrency.SetETag(http, view.RowVersion);
                return Results.Ok(view);
            }))
            .Produces<SetupRunView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistGetTenantSetup").WithSummary("The setup: its answers, the pack versions used (AIA-089), and every value with its flag and decision")
            .RequirePermission(AssistPermissions.TenantSetupRun);
        setups.MapPut("/{id:guid}/answers", async (Guid id, SetupAnswers answers, IDispatcher d, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                return expected.IsFailure ? Problems.For(expected.Error) : (await d.Send(new AnswerTenantSetup(id, answers, expected.Value), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                });
            })
            .Produces<SetupRunView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistAnswerTenantSetup")
            .WithSummary("Change the answers: a new answers version, and only the values they change are re-drafted (AIA-081). Needs If-Match")
            .RequirePermission(AssistPermissions.TenantSetupRun);
        setups.MapPost("/{id:guid}/items/{itemId:guid}/decision", async (Guid id, Guid itemId, SetupDecisionRequest r, IDispatcher d, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                return expected.IsFailure ? Problems.For(expected.Error) : (await d.Send(new DecideSetupItem(id, itemId, r.Decision, r.Note, expected.Value), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                });
            })
            .Produces<SetupItemView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistDecideSetupItem").WithSummary("Accept or reject a drafted value; a Missing one can't be accepted. Needs the value's If-Match")
            .RequirePermission(AssistPermissions.TenantSetupRun);
        setups.MapGet("/{id:guid}/completeness", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new CheckTenantSetup(id), ct)).ToHttp(Results.Ok))
            .Produces<SetupCompletenessView>(StatusCodes.Status200OK)
            .WithName("AssistCheckTenantSetup")
            .WithSummary("The completeness check before submission (AIA-085): every value decided, none Missing, regulated values from approved overlays, and each accepted value as its table's import would take it, saving nothing")
            .RequirePermission(AssistPermissions.TenantSetupRun);
        setups.MapPost("/{id:guid}/submit", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                if (expected.IsFailure)
                {
                    return Problems.For(expected.Error);
                }

                // Two steps (AIA-086): the check and the lock commit first, so a submission that stops part-way is resumed by submitting again.
                var begun = await d.Send(new BeginSetupSubmission(id, expected.Value), ct);
                return begun.IsFailure ? begun.ToHttp(Results.Ok) : (await d.Send(new CompleteSetupSubmission(id), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                });
            })
            .Produces<SetupSubmissionView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistSubmitTenantSetup")
            .WithSummary("Submit the setup as one SETUP change set (AIA-086): one import batch per table, in dependency order, for one approval by a second person. Only whoever ran the wizard submits; resumable. Needs If-Match")
            .WithDescription("Refused with ASSIST_SETUP_NOT_READY while the completeness check has blockers, and with ASSIST_TENANT_LIVE once the tenant is live. "
                + "The set's approval is a STATIC_DATA_T1 approval of business reference type STATIC_DATA_SETUP; its batches are IMPORT change requests under /static-data/change-requests. "
                + "Once every batch is in force the setup is APPLIED; a set not approved discards its drafts and returns the setup to DRAFT.")
            .RequirePermission(AssistPermissions.TenantSetupRun).RequireIdempotencyKey();
        setups.MapPost("/{id:guid}/submission/withdraw", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                return expected.IsFailure ? Problems.For(expected.Error) : (await d.Send(new WithdrawSetupSubmission(id, expected.Value), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                });
            })
            .Produces<SetupSubmissionView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistWithdrawTenantSetupSubmission")
            .WithSummary("Withdraw a submission that stopped part-way (SUBMITTING): the batches loaded so far are discarded and the setup is a draft again. Submitter only. Needs If-Match")
            .RequirePermission(AssistPermissions.TenantSetupRun);
    }

    /// <summary>
    /// The starter packs (ai-assist.md §5.2 and §5.3, D-18 and D-19; Ade, 15 November). The platform loads a version for every tenant and records the partner's
    /// review (ASSIST_OVERLAY_MANAGE); the named lead approves it (ASSIST_OVERLAY_APPROVE). A tenant still onboarding loads its own version
    /// (ASSIST_TENANT_SETUP_RUN), which its compliance officer approves (ASSIST_OVERLAY_APPROVE_TENANT). Whoever loaded or reviewed a version can't approve it,
    /// whatever they hold. Each approval names the content hash it approves, and every step is audited.
    /// </summary>
    private void MapPacks(IEndpointRouteBuilder api)
    {
        var platform = api.MapGroup("/assist/overlays").WithTags(Name);
        platform.MapPost("/", async (JsonElement file, IDispatcher d, HttpContext http, CancellationToken ct) => Loaded(await d.Send(new LoadStarterPack(PackScopes.AllTenants, file), ct), http, "overlays"))
            .Produces<StarterPackView>(StatusCodes.Status201Created).ReturnsETag()
            .WithName("AssistLoadPlatformPack").WithSummary("Load a pack file as a new version for every tenant, DRAFT, with each row's marks and their source (D-20)")
            .RequirePlatformPermission(AssistPermissions.OverlayManage).RequireIdempotencyKey();
        platform.MapGet("/", async (string? packCode, IDispatcher d, CancellationToken ct) => (await d.Query(new ListStarterPacks(packCode), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<StarterPackView>>(StatusCodes.Status200OK)
            .WithName("AssistListPlatformPacks").WithSummary("The pack versions, newest first").RequirePlatformPermission(AssistPermissions.OverlayManage);
        platform.MapGet("/{id:guid}", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) => Sheet(await d.Query(new GetStarterPack(id), ct), http))
            .Produces<StarterPackSheet>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistGetPlatformPack").WithSummary("A version and its review sheet: every row, its class, both marks and their source").RequirePlatformPermission(AssistPermissions.OverlayManage);
        platform.MapPost("/{id:guid}/review", async (Guid id, PackReviewRequest r, IDispatcher d, HttpContext http, CancellationToken ct) => await WithVersion(http, expected =>
                d.Send(new MarkStarterPackReviewed(id, r.Partner, r.ReviewerName, r.ReviewedOn, r.ReviewDocumentId, expected), ct)))
            .Produces<StarterPackView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistReviewPlatformPack").WithSummary("Record the local partner's review, with its signed document as evidence: REVIEWED. Needs If-Match")
            .RequirePlatformPermission(AssistPermissions.OverlayManage);
        platform.MapGet("/awaiting-approval", async (IDispatcher d, CancellationToken ct) => (await d.Query(new ListStarterPacks(null, AwaitingApproval: true), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<StarterPackView>>(StatusCodes.Status200OK)
            .WithName("AssistPlatformPacksToApprove").WithSummary("The REVIEWED versions awaiting the lead's approval").RequirePlatformPermission(AssistPermissions.OverlayApprove);
        platform.MapGet("/{id:guid}/approval", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) => Sheet(await d.Query(new GetStarterPack(id), ct), http))
            .Produces<StarterPackSheet>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistGetPlatformPackToApprove").WithSummary("A version's review sheet, for the approver to confirm its rows and marks").RequirePlatformPermission(AssistPermissions.OverlayApprove);
        platform.MapPost("/{id:guid}/approve", async (Guid id, PackApprovalRequest r, IDispatcher d, HttpContext http, CancellationToken ct) => await WithVersion(http, expected =>
                d.Send(new ApproveStarterPack(id, r.ContentHash, PackScopes.AllTenants, expected), ct)))
            .Produces<StarterPackView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistApprovePlatformPack")
            .WithSummary("Approve a REVIEWED version for every tenant, naming its content hash. Refused to whoever loaded or reviewed it. Needs If-Match")
            .RequirePlatformPermission(AssistPermissions.OverlayApprove);

        var tenant = api.MapGroup("/assist/packs").WithTags(Name);
        tenant.MapPost("/", async (JsonElement file, IDispatcher d, HttpContext http, CancellationToken ct) => Loaded(await d.Send(new LoadStarterPack(PackScopes.TenantOnly, file), ct), http, "packs"))
            .Produces<StarterPackView>(StatusCodes.Status201Created).ReturnsETag()
            .WithName("AssistLoadTenantPack")
            .WithSummary("Load a pack file as this tenant's own version (TENANT_ONLY, the fallback). Only while the tenant is onboarding, else 409 ASSIST_TENANT_LIVE")
            .RequirePermission(AssistPermissions.TenantSetupRun).RequireIdempotencyKey();
        tenant.MapGet("/", async (string? packCode, IDispatcher d, CancellationToken ct) => (await d.Query(new ListStarterPacks(packCode), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<StarterPackView>>(StatusCodes.Status200OK)
            .WithName("AssistListTenantPacks").WithSummary("The versions this tenant may use: those for every tenant, and its own").RequirePermission(AssistPermissions.TenantSetupRun);
        tenant.MapGet("/{id:guid}", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) => Sheet(await d.Query(new GetStarterPack(id), ct), http))
            .Produces<StarterPackSheet>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistGetTenantPack").WithSummary("A version and its review sheet").RequirePermission(AssistPermissions.TenantSetupRun);
        tenant.MapGet("/awaiting-approval", async (IDispatcher d, CancellationToken ct) => (await d.Query(new ListStarterPacks(null, AwaitingApproval: true), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<StarterPackView>>(StatusCodes.Status200OK)
            .WithName("AssistTenantPacksToApprove").WithSummary("This tenant's own versions awaiting its compliance officer's approval").RequirePermission(AssistPermissions.OverlayApproveTenant);
        tenant.MapGet("/{id:guid}/approval", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) => Sheet(await d.Query(new GetStarterPack(id), ct), http))
            .Produces<StarterPackSheet>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistGetTenantPackToApprove").WithSummary("A version's review sheet, for the approver to confirm").RequirePermission(AssistPermissions.OverlayApproveTenant);
        tenant.MapPost("/{id:guid}/approve", async (Guid id, PackApprovalRequest r, IDispatcher d, HttpContext http, CancellationToken ct) => await WithVersion(http, expected =>
                d.Send(new ApproveStarterPack(id, r.ContentHash, PackScopes.TenantOnly, expected), ct)))
            .Produces<StarterPackView>(StatusCodes.Status200OK).ReturnsETag()
            .WithName("AssistApproveTenantPack")
            .WithSummary("Approve this tenant's own version for its use only (D-19), naming its content hash. Refused to whoever loaded it. Needs If-Match")
            .RequirePermission(AssistPermissions.OverlayApproveTenant);
    }

    private static IResult Loaded(BuildingBlocks.Domain.Result<StarterPackView> result, HttpContext http, string route) => result.ToHttp(view =>
    {
        Concurrency.SetETag(http, view.RowVersion);
        return Results.Created($"/api/v1/assist/{route}/{view.Id}", view);
    });

    private static IResult Sheet(BuildingBlocks.Domain.Result<StarterPackSheet> result, HttpContext http) => result.ToHttp(sheet =>
    {
        Concurrency.SetETag(http, sheet.Pack.RowVersion);
        return Results.Ok(sheet);
    });

    private static async Task<IResult> WithVersion(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<StarterPackView>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        return expected.IsFailure ? Problems.For(expected.Error) : (await send(expected.Value)).ToHttp(view =>
        {
            Concurrency.SetETag(http, view.RowVersion);
            return Results.Ok(view);
        });
    }
}