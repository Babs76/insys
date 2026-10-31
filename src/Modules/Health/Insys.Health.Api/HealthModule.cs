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
using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Health.Api.StaticData;
using Insys.Health.Application;
using Insys.Health.Contracts;
using Insys.Health.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Health.Api;

public sealed record DependantRequest(Guid PartyId, [property: BuildingBlocks.Domain.CodeList(HealthCodeLists.DependantTypes, "type_code")] string DependantType, bool Student, DateOnly StartDate);

public sealed record DependantRemovalRequest(DateOnly LastDay);

/// <summary>BR-069: the membership list's filters, with the member number in the body so it stays out of addresses and logs.</summary>
public sealed record MembershipSearchRequest(Guid? PrincipalPartyId, string? MemberNumber, Guid? SchemeId, Domain.Members.MembershipStatus? Status, string? Cursor,
    int? Limit);

/// <summary>BR-072: recorded eligibility decisions, by member number (in the body, BR-069) and dependant suffix.</summary>
public sealed record EligibilityDecisionSearchRequest(string? MemberNumber, string? DependantSuffix, string? Cursor, int? Limit);

public sealed record PlanChangeRequest([property: BuildingBlocks.Domain.CodeList(HealthCodeLists.PlansAndOptions, "plan_code")] string PlanCode, [property: BuildingBlocks.Domain.CodeList(HealthCodeLists.PlansAndOptions, "option_code")] string OptionCode, DateOnly EffectiveDate, string Reason);

public sealed record MembershipStatusRequest(DateOnly EffectiveDate, string Reason);

public sealed record AuthorisationDecisionRequest(IReadOnlyList<LineAmount> Lines, DateOnly ValidFrom, string? Conditions);

public sealed record AuthorisationRejectionRequest(string Reason);

public sealed record AuthorisationChangeRequest(Guid LineId, decimal AuthorisedAmount);

public sealed record PendReviewRequest(IReadOnlyList<LineReview> Reviews);

public sealed record ReasonRequest(string Reason);

/// <param name="ReasonCode">A partial approval's reason (OQ-102): a claim decision reason of type PARTIAL, required when some lines were declined.</param>
/// <param name="ReasonCode">Required only for a partial approval (OQ-102): a claim adjudicated with some lines rejected while the rest are
/// paid. A decision reason of type PARTIAL from claims/static/claim-decision-reason-codes; 422 HEALTH_RATIONALE_REQUIRED without one.</param>
public sealed record ApproveClaimRequest(string? ReasonCode = null);

public sealed record ProviderQualityReviewRequest(int Score, string Reason);

public sealed record ReferralConsentRequest(string ConsentReference, string Reason);

public sealed record PortalReferralRequest(string MemberNumber, string? DependantSuffix, string? ReferredProviderId, string? ReferredSpecialty, string ClinicalReason, DateOnly ValidFrom, DateOnly ValidTo, int VisitsAllowed, string? ConsentReference = null);

public sealed record AssignCaseManagerRequest(Guid CaseManagerId, string? Reason);

public sealed record CarePlanRequest(IReadOnlyList<Domain.Care.CarePlanItemInput> Items, string? Reason);

public sealed record CareProgressRequest([property: BuildingBlocks.Domain.OneOf("DONE", "MISSED", "CHANGED")] string Status, string? Note);

public sealed record CareNoteRequest([property: BuildingBlocks.Domain.OneOf("PROVIDER", "MEMBER", "INTERNAL")] string ContactType, string? ContactRef, string Note, string? SharedWithProviderId, string? ConsentReference);

public sealed record DecideReferralRequest(bool Approved, string? Reason);

public sealed record CaseOutcomeRequest(string Outcome);

/// <summary>
/// The Health module (§11): schemes, memberships and dependants, eligibility, preauthorisation, medical claims and
/// their adjudication (ADR-012, proposed), benefit accumulators, utilisation and fraud cases, and provider payment runs
/// (HLT-001 to HLT-009).
/// </summary>
public sealed class HealthModule : IModule
{
    public string Name => "Health";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => HealthFunctions.All;

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    /// <summary>F005: its commands create or change a customer's business, so they are checked against the request's channel.</summary>
    public bool ChannelScoped => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddHealthInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        HealthStaticDataEndpoints.MapStaticData(api);
        var health = api.MapGroup("/health").WithTags(Name).ETagsFromRowVersion();
        MapLists(health);
        MapMembers(health);
        MapCare(health);
        MapRuns(health);
        MapProviders(health);
        MapComplexCases(health);

        MapRecoveries(health);
        MapPortal(api.MapGroup("/portal/health").WithTags(Name));
    }

    /// <summary>Wave 5a §4.3 to §4.5 (F034 to F038): complex cases and referrals. Their views hold no clinical detail; the clinical routes are audited with a purpose (HLT-008).</summary>
    private static void MapComplexCases(RouteGroupBuilder health)
    {
        health.MapPost("/complex-cases", async (OpenComplexCase r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/complex-cases/{v.Id}", v)))
            .Produces<ComplexCaseView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthOpenComplexCase").WithSummary("Open a complex medical case (F034)").RequirePermission(HealthPermissions.ComplexCaseUpdate).RequireIdempotencyKey();
        health.MapGet("/complex-cases", async (Guid? membershipId, Guid? caseManagerId, string? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListComplexCases(membershipId, caseManagerId, status, after, size), ct), c => c.Id))
            .Produces<CursorPage<ComplexCaseView>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListComplexCases").WithSummary("Complex cases, by membership, case manager or status, within your scope; no clinical detail").RequirePermission(HealthPermissions.ComplexCaseRead);
        health.MapGet("/complex-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetComplexCase(id), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK)
            .WithName("HealthGetComplexCase").WithSummary("A complex case, with who has managed it; no clinical detail").RequirePermission(HealthPermissions.ComplexCaseRead);
        health.MapGet("/complex-cases/{id:guid}/clinical", async (Guid id, string? purpose, IDispatcher d, CancellationToken ct) => (await d.Send(new ReadComplexCaseClinical(id, purpose), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseClinicalView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthReadComplexCaseClinical").WithSummary("A case's condition, care plans and notes: for its current manager or the clinical function, with a purpose; every read is audited (OQ-143)")
            .RequirePermission(HealthPermissions.ComplexCaseRead);
        health.MapPost("/complex-cases/{id:guid}/case-manager", async (Guid id, AssignCaseManagerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AssignCaseManager(id, r.CaseManagerId, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthAssignCaseManager").WithSummary("Assign or reassign the case manager, who must hold the function; reassigning needs a reason and ends the last manager's access (F035)")
            .RequirePermission(HealthPermissions.ComplexCaseUpdate);
        foreach (var (route, action, name) in new[] { ("hold", "HOLD", "HealthHoldComplexCase"), ("resume", "RESUME", "HealthResumeComplexCase") })
        {
            health.MapPost($"/complex-cases/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ChangeComplexCaseStatus(id, action), ct)).ToHttp(Results.Ok))
                .Produces<ComplexCaseView>(StatusCodes.Status200OK).TakesIfMatch().WithName(name).WithSummary($"Case status: {route}").RequirePermission(HealthPermissions.ComplexCaseUpdate);
        }

        health.MapPost("/complex-cases/{id:guid}/close", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) => (await d.Send(new CloseComplexCase(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthCloseComplexCase").WithSummary("Close a case with a reason; it never closes by itself").RequirePermission(HealthPermissions.ComplexCaseUpdate);
        health.MapPost("/complex-cases/{id:guid}/care-plan", async (Guid id, CarePlanRequest r, IDispatcher d, CancellationToken ct) => (await d.Send(new SetCarePlan(id, r.Items, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthSetCarePlan").WithSummary("Set the care plan; a change needs a reason and keeps the earlier version (F036)").RequirePermission(HealthPermissions.ComplexCaseUpdate);
        health.MapPost("/complex-cases/{id:guid}/care-plan/items/{itemId:guid}/progress", async (Guid id, Guid itemId, CareProgressRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordCareProgress(id, itemId, r.Status, r.Note), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).TakesIfMatch()
            .WithName("HealthRecordCareProgress").WithSummary("Mark a plan item done, missed or changed (F037)").RequirePermission(HealthPermissions.ComplexCaseUpdate);
        health.MapPost("/complex-cases/{id:guid}/notes", async (Guid id, CareNoteRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddCareNote(id, r.ContactType, r.ContactRef, r.Note, r.SharedWithProviderId, r.ConsentReference), ct)).ToHttp(Results.Ok))
            .Produces<ComplexCaseView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthAddCareNote").WithSummary("Add a coordination note; one shared with another provider names the member's consent (F036)").RequirePermission(HealthPermissions.ComplexCaseUpdate);

        health.MapPost("/referrals", async (RequestClinicalReferral r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/referrals/{v.Id}", v)))
            .Produces<ClinicalReferralView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthRequestReferral").WithSummary("Record a clinical referral (F038); it does not affect claims (OQ-144)").RequirePermission(HealthPermissions.ReferralCreate).RequireIdempotencyKey();
        health.MapGet("/referrals", async (Guid? membershipId, string? providerId, string? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListClinicalReferrals(membershipId, providerId, status, after, size), ct), r => r.Id))
            .Produces<CursorPage<ClinicalReferralView>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListReferrals").WithSummary("Referrals, by membership, referring provider or status; no clinical reason").RequirePermission(HealthPermissions.ReferralRead);
        health.MapGet("/referrals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetClinicalReferral(id), ct)).ToHttp(Results.Ok))
            .Produces<ClinicalReferralView>(StatusCodes.Status200OK).WithName("HealthGetReferral").WithSummary("A referral; no clinical reason").RequirePermission(HealthPermissions.ReferralRead);
        health.MapGet("/referrals/{id:guid}/clinical", async (Guid id, string? purpose, IDispatcher d, CancellationToken ct) => (await d.Send(new ReadReferralClinical(id, purpose), ct)).ToHttp(Results.Ok))
            .Produces<ReferralClinicalView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthReadReferralClinical").WithSummary("A referral's clinical reason, for the clinical function, with a purpose; every read is audited").RequirePermission(HealthPermissions.ClinicalRead);
        health.MapPost("/referrals/{id:guid}/decide", async (Guid id, DecideReferralRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideClinicalReferral(id, r.Approved, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ClinicalReferralView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthDecideReferral").WithSummary("Approve a referral, or decline it with a reason").RequirePermission(HealthPermissions.ReferralDecide);
        health.MapPost("/referrals/{id:guid}/consent", async (Guid id, ReferralConsentRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordReferralConsent(id, r.ConsentReference, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ClinicalReferralView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthRecordReferralConsent").WithSummary("Record the member's consent after the referral was made, with a reason: the receiving provider may then read its reason")
            .RequirePermission(HealthPermissions.ReferralConsent);
        health.MapPost("/referrals/{id:guid}/consent/withdraw", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new WithdrawReferralConsent(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ClinicalReferralView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("HealthWithdrawReferralConsent").WithSummary("Withdraw the member's consent, with a reason: the receiving provider's access to the reason ends at once")
            .RequirePermission(HealthPermissions.ReferralConsent);
        health.MapPost("/referrals/{id:guid}/visits", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RecordReferralVisit(id), ct)).ToHttp(Results.Ok))
            .Produces<ClinicalReferralView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthRecordReferralVisit").WithSummary("Record a visit under an approved referral; it is USED when none is left").RequirePermission(HealthPermissions.ReferralCreate);
    }

    /// <summary>Wave 5a §4.1, §4.2: provider measures, quality reviews and statements (F028 to F030). Staff only: providers do not see their measures yet (OQ-141).</summary>
    private static void MapProviders(RouteGroupBuilder health)
    {
        health.MapGet("/providers/{providerId}/measures", async (string providerId, string? monthKey, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListProviderMeasures(providerId, monthKey, after, size), ct), m => m.Id))
            .Produces<CursorPage<ProviderMeasureView>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListProviderMeasures").WithSummary("A provider's measures by month, with whether each was judged and breached (F028)")
            .RequirePermission(HealthPermissions.ProviderRead);
        health.MapGet("/providers/{providerId}/quality-reviews", async (string providerId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListProviderQualityReviews(providerId, after, size), ct), r => r.Id))
            .Produces<CursorPage<ProviderQualityReviewView>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListProviderQualityReviews").WithSummary("A provider's quality reviews (F029)").RequirePermission(HealthPermissions.ProviderRead);
        health.MapPost("/providers/{providerId}/quality-reviews", async (string providerId, ProviderQualityReviewRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordProviderQualityReview(providerId, r.Score, r.Reason), ct)).ToHttp(v => Results.Created($"/api/v1/health/providers/{providerId}/quality-reviews/{v.Id}", v)))
            .Produces<ProviderQualityReviewView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthRecordProviderQualityReview").WithSummary("Record a scored review of a provider, 1 to 5, with a reason (F029)")
            .RequirePermission(HealthPermissions.ProviderUpdate).RequireIdempotencyKey();
        health.MapGet("/provider-statements", async (string? providerId, string? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListProviderStatements(providerId, status, after, size), ct), s => s.Id))
            .Produces<CursorPage<ProviderStatementSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListProviderStatements").WithSummary("Providers' statements, by provider or status (F030)").RequirePermission(HealthPermissions.ProviderRead);
        health.MapGet("/provider-statements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetProviderStatement(id), ct)).ToHttp(Results.Ok))
            .Produces<ProviderStatementView>(StatusCodes.Status200OK)
            .WithName("HealthGetProviderStatement").WithSummary("A statement with each line's match result and resolution (F030)").RequirePermission(HealthPermissions.ProviderRead);
        health.MapPost("/provider-statements", async (SubmitProviderStatement r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/provider-statements/{v.Id}", v)))
            .Produces<ProviderStatementView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthSubmitProviderStatement").WithSummary("Enter a provider's statement; each line is matched at once against its paid claims (F030)")
            .RequirePermission(HealthPermissions.ProviderUpdate).RequireIdempotencyKey();
        health.MapPost("/provider-statements/import", async (ImportProviderStatement r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/provider-statements/{v.Id}", v)))
            .Produces<ProviderStatementView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthImportProviderStatement").WithSummary("The same from CSV text in the fixed layout: a header claim_number,amount, then one line per claim (OQ-142)")
            .RequirePermission(HealthPermissions.ProviderUpdate).RequireIdempotencyKey();
        health.MapPost("/provider-statements/{id:guid}/lines/{lineId:guid}/resolve", async (Guid id, Guid lineId, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ResolveStatementLine(id, lineId, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ProviderStatementView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthResolveStatementLine").WithSummary("Resolve an exception with a reason; the statement is RECONCILED when none is open. It changes no payment (F030)")
            .RequirePermission(HealthPermissions.ProviderUpdate);
    }

    private static void MapMembers(RouteGroupBuilder health)
    {
        health.MapPost("/schemes", async (RegisterHealthScheme r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/schemes/{v.Id}", v)))
            .Produces<SchemeView>(StatusCodes.Status201Created)
            .WithName("HealthRegisterScheme").WithSummary("Register a sponsor's scheme").RequirePermission(HealthPermissions.SchemeCreate).RequireIdempotencyKey();
        health.MapGet("/schemes", async (string? schemeNumber, string? sponsorCode, Domain.Members.SchemeStatus? status, string? cursor, int? limit, IDispatcher d,
                CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListHealthSchemes(schemeNumber, sponsorCode, status, after, size), ct), s => s.Id))
            .Produces<CursorPage<SchemeSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListSchemes").WithSummary("List schemes, by number, sponsor or status (BR-067)").RequirePermission(HealthPermissions.SchemeRead);
        health.MapGet("/schemes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetHealthScheme(id), ct)).ToHttp(Results.Ok))
            .Produces<SchemeView>(StatusCodes.Status200OK)
            .WithName("HealthGetScheme").WithSummary("Get a scheme").RequirePermission(HealthPermissions.SchemeRead);
        foreach (var (route, action) in new[] { ("renew", SchemeAction.Renew), ("terminate", SchemeAction.Terminate) })
        {
            health.MapPost($"/schemes/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeHealthScheme(id, action), ct)).ToHttp(Results.Ok))
                .Produces<SchemeView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"HealthScheme{action}").WithSummary($"{action} the scheme").RequirePermission(HealthPermissions.SchemeUpdate);
        }

        health.MapPost("/memberships", async (EnrolHealthMember r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/memberships/{v.Id}", v)))
            .Produces<MembershipView>(StatusCodes.Status201Created)
            .WithName("HealthEnrolMember").WithSummary("Enrol a principal member on a plan option; waiting periods are fixed now (HLT-002)")
            .RequirePermission(HealthPermissions.MembershipCreate).RequireIdempotencyKey();
        health.MapGet("/memberships/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetMembership(id), ct)).ToHttp(Results.Ok))
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .WithName("HealthGetMembership").WithSummary("Get a membership").RequirePermission(HealthPermissions.MembershipRead);
        health.MapGet("/memberships/{id:guid}/accumulators", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetAccumulators(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<AccumulatorView>>(StatusCodes.Status200OK)
            .WithName("HealthGetAccumulators").WithSummary("The membership's benefit accumulators: limit, used, reserved").RequirePermission(HealthPermissions.AccumulatorRead);
        health.MapPost("/memberships/{id:guid}/dependants", async (Guid id, DependantRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddHealthDependant(id, r.PartyId, r.DependantType, r.Student, r.StartDate), ct)).ToHttp(Results.Ok))
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .WithName("HealthAddDependant").WithSummary("Add a dependant within the type's age limit").RequirePermission(HealthPermissions.MembershipUpdate)
            .RequireIdempotencyKey();
        health.MapPost("/memberships/{id:guid}/dependants/{dependantId:guid}/remove", async (Guid id, Guid dependantId, DependantRemovalRequest r, IDispatcher d,
                CancellationToken ct) => (await d.Send(new RemoveHealthDependant(id, dependantId, r.LastDay), ct)).ToHttp(Results.Ok))
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthRemoveDependant").WithSummary("Remove a dependant from a date").RequirePermission(HealthPermissions.MembershipUpdate);
        health.MapPost("/memberships/{id:guid}/plan", async (Guid id, PlanChangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeHealthPlan(id, r.PlanCode, r.OptionCode, r.EffectiveDate, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<MembershipView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthChangePlan").WithSummary("Change the plan option from a date").RequirePermission(HealthPermissions.MembershipUpdate);
        foreach (var (route, action) in new[] { ("suspend", MembershipAction.Suspend), ("reinstate", MembershipAction.Reinstate), ("terminate", MembershipAction.Terminate) })
        {
            health.MapPost($"/memberships/{{id:guid}}/{route}", async (Guid id, MembershipStatusRequest r, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeMembershipStatus(id, action, r.EffectiveDate, r.Reason), ct)).ToHttp(Results.Ok))
                .Produces<MembershipView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"HealthMembership{action}").WithSummary($"{action} the membership").RequirePermission(HealthPermissions.MembershipUpdate);
        }

        health.MapPost("/eligibility-decisions/search", async (EligibilityDecisionSearchRequest r, IDispatcher d, CancellationToken ct) =>
                await Page(r.Cursor, r.Limit, (after, size) => d.Query(new ListEligibilityDecisions(r.MemberNumber, r.DependantSuffix, after, size), ct), e => e.Id))
            .Produces<CursorPage<EligibilityDecisionSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListEligibilityDecisions")
            .WithSummary("Find recorded eligibility decisions, by member number and dependant, for memberships in your scope (BR-072, HLT-003)")
            .RequirePermission(HealthPermissions.MembershipRead);
        health.MapPost("/eligibility", async (CheckEligibility r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(Results.Ok))
            .Produces<EligibilityView>(StatusCodes.Status200OK)
            .WithName("HealthCheckEligibility").WithSummary("An authorised member-eligibility enquiry; the decision is recorded (HLT-003)")
            .RequirePermission(HealthPermissions.EligibilityCheck);
    }

    private static void MapCare(RouteGroupBuilder health)
    {
        health.MapPost("/preauthorisations", async (RequestAuthorisation r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/preauthorisations/{v.Id}", v)))
            .Produces<PreauthorisationView>(StatusCodes.Status201Created)
            .WithName("HealthRequestAuthorisation").WithSummary("Request a preauthorisation for an eligible member (HLT-005)")
            .RequirePermission(HealthPermissions.PreauthCreate).RequireIdempotencyKey();
        health.MapGet("/preauthorisations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAuthorisation(id), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .WithName("HealthGetAuthorisation").WithSummary("Get a preauthorisation").RequirePermission(HealthPermissions.PreauthRead);
        health.MapGet("/preauthorisations/{id:guid}/clinical", async (Guid id,
                [System.ComponentModel.Description("Why the diagnosis codes are read (up to 200 characters), recorded with the read (HLT-008). Required.")] string? purpose,
                IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReadAuthorisationClinical(id, purpose), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationClinicalView>(StatusCodes.Status200OK)
            .WithName("HealthGetAuthorisationClinical")
            .WithSummary("A preauthorisation's diagnosis codes, for clinical review only; each read is audited with its purpose (BR-072, HLT-008)")
            .WithDescription("Every read leaves one audit entry (operation ReadClinical): who, when, which preauthorisation and the purpose. A read with no purpose (422 HEALTH_PURPOSE_REQUIRED), or of a preauthorisation not found or outside your scope (404), is audited as DENIED; one without the permission (403) is audited as an access denial.")
            .RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/review", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewAuthorisation(id), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthReviewAuthorisation").WithSummary("Start clinical review (HLT-008)").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/approve", async (Guid id, AuthorisationDecisionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveAuthorisation(id, r.Lines, r.ValidFrom, r.Conditions), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthApproveAuthorisation").WithSummary("Approve within the available benefit; the amount is reserved (ADR-012, h)")
            .RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/reject", async (Guid id, AuthorisationRejectionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RejectAuthorisation(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthRejectAuthorisation").WithSummary("Reject the authorisation").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/modify", async (Guid id, AuthorisationChangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ModifyAuthorisation(id, r.LineId, r.AuthorisedAmount), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthModifyAuthorisation").WithSummary("Change an approved line's amount and its reservation").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/conditions-met", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConfirmConditionsMet(id), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthConfirmConditionsMet").WithSummary("A conditional approval's conditions are met; it becomes Approved").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelAuthorisation(id), ct)).ToHttp(Results.Ok))
            .Produces<PreauthorisationView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthCancelAuthorisation").WithSummary("Cancel; the reservation is released").RequirePermission(HealthPermissions.PreauthReview);

        health.MapPost("/claims", async (SubmitMedicalClaim r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/claims/{v.Id}", v)))
            .Produces<MedicalClaimView>(StatusCodes.Status201Created)
            .WithName("HealthSubmitClaim").WithSummary("Submit a provider or member claim (HLT-006)").RequirePermission(HealthPermissions.ClaimCreate).RequireIdempotencyKey();
        health.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .WithName("HealthGetClaim").WithSummary("Get a claim").RequirePermission(HealthPermissions.ClaimRead);
        health.MapGet("/claims/{id:guid}/clinical", async (Guid id,
                [System.ComponentModel.Description("Why the claim's codes, diagnoses and prescriptions are read (up to 200 characters), recorded with the read (HLT-008). Required.")] string? purpose,
                IDispatcher d, CancellationToken ct) => (await d.Send(new ReadMedicalClaimClinical(id, purpose), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimClinicalView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthReadClaimClinical").WithSummary("A claim's codes, diagnoses and prescriptions, for the clinical function, with a purpose; every read is audited")
            .RequirePermission(HealthPermissions.ClinicalRead);
        health.MapPost("/claims/{id:guid}/validate", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ValidateMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthValidateClaim").WithSummary("Validate membership, cover and codes (HLT-007)").RequirePermission(HealthPermissions.ClaimUpdate);
        health.MapPost("/claims/{id:guid}/adjudicate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AdjudicateMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthAdjudicateClaim").WithSummary("Adjudicate the lines (ADR-012); safe to repeat after CONCURRENCY_CONFLICT").RequirePermission(HealthPermissions.ClaimUpdate);
        health.MapPost("/claims/{id:guid}/review", async (Guid id, PendReviewRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewPendedClaim(id, r.Reviews), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthReviewPendedClaim").WithSummary("Accept or reject pended lines, and adjudicate again").RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/approve", async (Guid id, ApproveClaimRequest? r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveMedicalClaim(id, r?.ReasonCode), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthApproveClaim").WithSummary("Approve the adjudicated claim for payment")
            .WithDescription("The body is optional. A claim adjudicated with some lines rejected while the rest are paid is a partial approval (OQ-102): it needs "
                + "reasonCode, a decision reason of type PARTIAL (claims/static/claim-decision-reason-codes), else 422 HEALTH_RATIONALE_REQUIRED; the claim's "
                + "outcome is then PARTLY_APPROVED and the member is sent CLAIM_DECISION_RIGHTS. Paying at the tariff, or less a co-payment, is the plan's terms "
                + "and is not partial.")
            .RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/reject", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RejectMedicalClaim(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthRejectClaim").WithSummary("Reject a validated claim").RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/reverse", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseMedicalClaim(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<MedicalClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthReverseClaim").WithSummary("Reverse a paid claim; its benefit use is given back").RequirePermission(HealthPermissions.ClaimApprove);

        health.MapPost("/utilisation-cases", async (OpenUtilisationCase r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/utilisation-cases/{v.Id}", v)))
            .Produces<UtilisationCaseView>(StatusCodes.Status201Created)
            .WithName("HealthOpenUtilisationCase").WithSummary("Open a utilisation case").RequirePermission(HealthPermissions.CaseUpdate).RequireIdempotencyKey();
        health.MapGet("/utilisation-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetUtilisationCase(id), ct)).ToHttp(Results.Ok))
            .Produces<UtilisationCaseView>(StatusCodes.Status200OK)
            .WithName("HealthGetUtilisationCase").WithSummary("Get a utilisation case").RequirePermission(HealthPermissions.CaseRead);
        health.MapPost("/utilisation-cases/{id:guid}/review", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewUtilisationCase(id), ct)).ToHttp(Results.Ok))
            .Produces<UtilisationCaseView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthReviewUtilisationCase").WithSummary("Start reviewing").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapPost("/utilisation-cases/{id:guid}/conclude", async (Guid id, CaseOutcomeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConcludeUtilisationCase(id, r.Outcome), ct)).ToHttp(Results.Ok))
            .Produces<UtilisationCaseView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthConcludeUtilisationCase").WithSummary("Conclude, by the reviewer").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapGet("/fraud-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetFraudCase(id), ct)).ToHttp(Results.Ok))
            .Produces<FraudCaseView>(StatusCodes.Status200OK)
            .WithName("HealthGetFraudCase").WithSummary("Get a fraud case").RequirePermission(HealthPermissions.CaseRead);
        health.MapPost("/fraud-cases/{id:guid}/investigate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new InvestigateFraudCase(id), ct)).ToHttp(Results.Ok))
            .Produces<FraudCaseView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthInvestigateFraudCase").WithSummary("Start investigating").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapPost("/fraud-cases/{id:guid}/conclude", async (Guid id, CaseOutcomeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConcludeFraudCase(id, r.Outcome), ct)).ToHttp(Results.Ok))
            .Produces<FraudCaseView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthConcludeFraudCase").WithSummary("Conclude, by the investigator").RequirePermission(HealthPermissions.CaseUpdate);
    }

    private static void MapRuns(RouteGroupBuilder health)
    {
        health.MapPost("/payment-runs", async (OpenPaymentRun r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/payment-runs/{v.Id}", v)))
            .Produces<PaymentRunView>(StatusCodes.Status201Created)
            .WithName("HealthOpenRun").WithSummary("Open a provider payment run").RequirePermission(HealthPermissions.RunUpdate).RequireIdempotencyKey();
        health.MapGet("/payment-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPaymentRun(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentRunView>(StatusCodes.Status200OK)
            .WithName("HealthGetRun").WithSummary("Get a payment run").RequirePermission(HealthPermissions.RunRead);
        health.MapPost("/payment-runs/{id:guid}/cut-off", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CutOffPaymentRun(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentRunView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthCutOffRun").WithSummary("Cut off the run").RequirePermission(HealthPermissions.RunUpdate);
        health.MapPost("/payment-runs/{id:guid}/calculate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CalculatePaymentRun(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentRunView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthCalculateRun").WithSummary("Calculate remittances; approval is requested in Workflow (HLT-009)").RequirePermission(HealthPermissions.RunUpdate);
        health.MapPost("/payment-runs/{id:guid}/release", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReleasePaymentRun(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentRunView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("HealthReleaseRun").WithSummary("Release the approved run to Payments").RequirePermission(HealthPermissions.RunUpdate);
    }

    /// <summary>GAP-040: Health's lists, by party, number and status, with cursor paging; fraud cases by the claim they are about.</summary>
    private static void MapLists(RouteGroupBuilder health)
    {
        health.MapGet("/memberships", async (Guid? principalPartyId, string? memberNumber, Guid? schemeId, Domain.Members.MembershipStatus? status, string? cursor, int? limit,
                IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListMemberships(principalPartyId, memberNumber, schemeId, status, after, size), ct), m => m.Id))
            .Produces<CursorPage<MembershipSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListMemberships").WithSummary("List memberships, by principal member, number, scheme or status (GAP-040)")
            .RequirePermission(HealthPermissions.MembershipRead);
        health.MapPost("/memberships/search", async (MembershipSearchRequest r, IDispatcher d, CancellationToken ct) =>
                await Page(r.Cursor, r.Limit, (after, size) => d.Query(new ListMemberships(r.PrincipalPartyId, r.MemberNumber, r.SchemeId, r.Status, after, size), ct),
                    m => m.Id))
            .Produces<CursorPage<MembershipSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthSearchMemberships")
            .WithSummary("Find memberships as the list does, with the member number in the body rather than the address (BR-069)")
            .RequirePermission(HealthPermissions.MembershipRead);
        health.MapGet("/preauthorisations", async (Guid? membershipId, Guid? patientPartyId, string? authNumber, Domain.Care.PreauthStatus? status, string? cursor, int? limit,
                IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPreauthorisations(membershipId, patientPartyId, authNumber, status, after, size), ct), p => p.Id))
            .Produces<CursorPage<PreauthorisationSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListAuthorisations").WithSummary("List preauthorisations, by membership, patient, number or status (GAP-040)")
            .RequirePermission(HealthPermissions.PreauthRead);
        health.MapGet("/claims", async (Guid? membershipId, Guid? patientPartyId, string? claimNumber, Domain.Care.MedicalClaimStatus? status, string? cursor, int? limit,
                IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListMedicalClaims(membershipId, patientPartyId, claimNumber, status, after, size), ct), c => c.Id))
            .Produces<CursorPage<MedicalClaimSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListClaims").WithSummary("List medical claims, by membership, patient, number or status (GAP-040)")
            .RequirePermission(HealthPermissions.ClaimRead);
        health.MapGet("/utilisation-cases", async (Guid? membershipId, Guid? claimId, Domain.Care.UtilisationStatus? status, string? cursor, int? limit, IDispatcher d,
                CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListUtilisationCases(membershipId, claimId, status, after, size), ct), c => c.Id))
            .Produces<CursorPage<UtilisationCaseSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListUtilisationCases").WithSummary("List utilisation cases, by membership, claim or status (GAP-040)")
            .RequirePermission(HealthPermissions.CaseRead);
        health.MapGet("/fraud-cases", async (string? claimNumber, Domain.Care.FraudStatus? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListFraudCases(claimNumber, status, after, size), ct), c => c.Id))
            .Produces<CursorPage<FraudCaseSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListFraudCases").WithSummary("Find fraud cases, by the medical claim's number or status (GAP-040)")
            .RequirePermission(HealthPermissions.CaseRead);
        health.MapGet("/payment-runs", async (string? runCode, Domain.Payments.RunStatus? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPaymentRuns(runCode, status, after, size), ct), r => r.Id))
            .Produces<CursorPage<PaymentRunSummary>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("HealthListRuns").WithSummary("List provider payment runs, by code or status (GAP-040)")
            .RequirePermission(HealthPermissions.RunRead);
    }

    private static async Task<IResult> Page<T>(string? cursor, int? limit, Func<Guid?, int, Task<BuildingBlocks.Domain.Result<IReadOnlyList<T>>>> query, Func<T, Guid> id)
    {
        if (!Cursor.TryDecode(cursor, out var after))
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
        }

        var size = Cursor.ClampLimit(limit);
        return (await query(after, size)).ToHttp(items => Results.Ok(new CursorPage<T>(items, items.Count == size ? Cursor.Encode(id(items[^1])) : null)));
    }

    /// <summary>
    /// The member and provider portals (external-user access): a member's memberships; a provider's own claims,
    /// preauthorisations and remittances. A provider submits only as itself.
    /// </summary>
    /// <summary>Wave 5a §4.6, §4.7: credit notes and overpayment recoveries.</summary>
    private static void MapRecoveries(RouteGroupBuilder health)
    {
        health.MapGet("/provider-recoveries", async (string? providerId, string? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListProviderRecoveries(providerId, status, after, size), ct), r => r.Id))
            .Produces<CursorPage<ProviderRecoveryView>>(StatusCodes.Status200OK)
            .WithName("HealthListRecoveries").WithSummary("Overpayments owed by providers and members (F049)").RequirePermission(HealthPermissions.RecoveryRead);
        health.MapGet("/provider-recoveries/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetProviderRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<ProviderRecoveryView>(StatusCodes.Status200OK)
            .WithName("HealthGetRecovery").WithSummary("A recovery, with its offsets").RequirePermission(HealthPermissions.RecoveryRead);
        health.MapPost("/provider-recoveries", async (ProposeRecoveryFinding r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/provider-recoveries/{v.Id}", v)))
            .Produces<ProviderRecoveryView>(StatusCodes.Status201Created)
            .WithName("HealthProposeRecoveryFinding").WithSummary("A person's overpayment finding; a second person approves it before anything is owed").RequirePermission(HealthPermissions.RecoveryManage).RequireIdempotencyKey();
        health.MapPost("/provider-recoveries/{id:guid}/invoice", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new InvoiceProviderRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<ProviderRecoveryView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthInvoiceRecovery").WithSummary("A person's decision: the balance is invoiced through Billing").RequirePermission(HealthPermissions.RecoveryManage);
        health.MapPost("/provider-recoveries/{id:guid}/write-off", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) => (await d.Send(new RequestRecoveryWriteOff(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ProviderRecoveryView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthWriteOffRecovery").WithSummary("Ask to write off the balance, with a reason; a second person approves").RequirePermission(HealthPermissions.RecoveryManage);
        health.MapPost("/provider-recoveries/{id:guid}/reverse", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) => (await d.Send(new ReverseProviderRecovery(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ProviderRecoveryView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthReverseRecovery").WithSummary("A recovery raised in error is reversed; an offset already taken is repaid in the next run").RequirePermission(HealthPermissions.RecoveryManage);
        health.MapPost("/provider-recoveries/{id:guid}/hold", async (Guid id, RecoveryHoldRequest r, IDispatcher d, CancellationToken ct) => (await d.Send(new HoldRecoveryForDispute(id, r.CaseId), ct)).ToHttp(Results.Ok))
            .Produces<ProviderRecoveryView>(StatusCodes.Status200OK).TakesIfMatch()
            .WithName("HealthHoldRecovery").WithSummary("Hold a recovery from offsets for a dispute case; the case closing releases it (OQ-146)").RequirePermission(HealthPermissions.RecoveryManage);
        health.MapGet("/provider-credit-notes", async (string? providerId, string? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListCreditNotes(providerId, status, after, size), ct), n => n.Id))
            .Produces<CursorPage<ProviderCreditNoteView>>(StatusCodes.Status200OK)
            .WithName("HealthListCreditNotes").WithSummary("Providers' credit notes (F048)").RequirePermission(HealthPermissions.CreditNoteRead);
        health.MapGet("/provider-credit-notes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetCreditNote(id), ct)).ToHttp(Results.Ok))
            .Produces<ProviderCreditNoteView>(StatusCodes.Status200OK)
            .WithName("HealthGetCreditNote").WithSummary("A provider's credit note").RequirePermission(HealthPermissions.CreditNoteRead);
        health.MapPost("/provider-credit-notes", async (SubmitCreditNote r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/provider-credit-notes/{v.Id}", v)))
            .Produces<ProviderCreditNoteView>(StatusCodes.Status201Created)
            .WithName("HealthSubmitCreditNote").WithSummary("Record a provider's credit note; a second person approves it, and it then raises a recovery").RequirePermission(HealthPermissions.CreditNoteCreate).RequireIdempotencyKey();
        health.MapGet("/remittances/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRemittanceDetail(id), ct)).ToHttp(Results.Ok))
            .Produces<RemittanceDetailView>(StatusCodes.Status200OK)
            .WithName("HealthGetRemittance").WithSummary("A remittance: gross, offsets with the original claim, its reversal and its replacement, and the net").RequirePermission(HealthPermissions.RunRead);
    }

    private static void MapPortal(RouteGroupBuilder portal)
    {
        const string member = BuildingBlocks.Application.Context.ExternalSubject.Party, provider = BuildingBlocks.Application.Context.ExternalSubject.Provider;
        portal.MapGet("/memberships", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalMemberships(after, size), ct), m => m.Id))
            .Produces<CursorPage<PortalMembershipView>>(StatusCodes.Status200OK)
            .WithName("PortalHealthListMemberships").WithSummary("The member's memberships (principal or dependant), by the caller's link only (BR-091)")
            .RequirePermission(HealthPermissions.MembershipRead).ForPortal(member);
        portal.MapGet("/memberships/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalMembership(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalMembershipView>(StatusCodes.Status200OK)
            .WithName("PortalHealthGetMembership").WithSummary("The member's membership (principal or dependant)").RequirePermission(HealthPermissions.MembershipRead)
            .ForPortal(member);
        portal.MapGet("/claims", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalMedicalClaims(after, size), ct), c => c.Id))
            .Produces<CursorPage<PortalMedicalClaimView>>(StatusCodes.Status200OK)
            .WithName("PortalHealthListClaims")
            .WithSummary("The provider's own claims; for a member, their own claims and, as principal, every claim of their membership")
            .RequirePermission(HealthPermissions.ClaimRead).ForPortal(provider, member);
        portal.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalMedicalClaimView>(StatusCodes.Status200OK)
            .WithName("PortalHealthGetClaim").WithSummary("A claim of the provider's, or of the member's (as patient, or as principal of the membership)")
            .RequirePermission(HealthPermissions.ClaimRead).ForPortal(provider, member);
        portal.MapPost("/claims", async (PortalMedicalClaimRequest r, IDispatcher d, IExecutionContext execution, CancellationToken ct) =>
                {
                    var patient = await d.Query(new PortalPatientOf(r.MemberNumber, r.DependantSuffix), ct);
                    if (patient.IsFailure)
                    {
                        return patient.ToHttp(_ => Results.Ok());
                    }

                    var submitted = await d.Send(new SubmitMedicalClaim(Domain.Care.ClaimSources.Provider, execution.External!.ProviderId, patient.Value.MembershipId,
                        patient.Value.PatientPartyId, r.ServiceDate, "PORTAL", r.Currency, r.PreauthorisationId, r.Lines, r.ReplacesClaimId), ct);
                    return submitted.IsFailure
                        ? submitted.ToHttp(_ => Results.Ok())
                        : (await d.Query(new GetPortalMedicalClaim(submitted.Value.Id), ct)).ToHttp(v => Results.Created($"/api/v1/portal/health/claims/{v.Id}", v));
                })
            .Produces<PortalMedicalClaimView>(StatusCodes.Status201Created)
            .WithName("PortalHealthSubmitClaim").WithSummary("A provider submits a claim, as itself").RequirePermission(HealthPermissions.ClaimCreate)
            .RequireIdempotencyKey().ForPortal(provider);
        portal.MapGet("/preauthorisations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetPortalPreauthorisation(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalPreauthorisationView>(StatusCodes.Status200OK)
            .WithName("PortalHealthGetPreauthorisation").WithSummary("A preauthorisation of the provider's").RequirePermission(HealthPermissions.PreauthRead).ForPortal(provider);
        // Wave 5a §7: a provider's own referrals: it is the referring provider, whatever a request says (external-user access §3).
        portal.MapPost("/referrals", async (PortalReferralRequest r, IDispatcher d, IExecutionContext execution, CancellationToken ct) =>
                {
                    var patient = await d.Query(new PortalPatientOf(r.MemberNumber, r.DependantSuffix), ct);
                    return patient.IsFailure
                        ? patient.ToHttp(_ => Results.Ok())
                        : (await d.Send(new RequestClinicalReferral(patient.Value.MembershipId, patient.Value.PatientPartyId, execution.External!.ProviderId!, r.ReferredProviderId, r.ReferredSpecialty,
                            r.ClinicalReason, r.ValidFrom, r.ValidTo, r.VisitsAllowed, null, null, r.ConsentReference), ct)).ToHttp(v => Results.Created($"/api/v1/portal/health/referrals/{v.Id}", v));
                })
            .Produces<ClinicalReferralView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PortalHealthRequestReferral").WithSummary("Refer a member, as yourself (F038); name the member's consent for the receiving provider to read the reason")
            .RequirePermission(HealthPermissions.ReferralCreate).ForPortal(provider).RequireIdempotencyKey();
        portal.MapGet("/referrals", async (string? status, string? cursor, int? limit, IDispatcher d, IExecutionContext execution, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListClinicalReferrals(null, execution.External!.ProviderId!, status, after, size), ct), r => r.Id))
            .Produces<CursorPage<ClinicalReferralView>>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PortalHealthListReferrals").WithSummary("Referrals you made or received; no clinical reason").RequirePermission(HealthPermissions.ReferralRead).ForPortal(provider);
        portal.MapGet("/referrals/{id:guid}/clinical", async (Guid id, string? purpose, IDispatcher d, CancellationToken ct) => (await d.Send(new ReadPortalReferralClinical(id, purpose), ct)).ToHttp(Results.Ok))
            .Produces<ReferralClinicalView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PortalHealthReadReferralClinical").WithSummary("A referral's reason, with a purpose: for the provider who wrote it, or the one it names with the member's consent; every read is audited")
            .RequirePermission(HealthPermissions.ReferralRead).ForPortal(provider);

        portal.MapPost("/preauthorisations", async (PortalPreauthorisationRequest r, IDispatcher d, IExecutionContext execution, CancellationToken ct) =>
                {
                    var patient = await d.Query(new PortalPatientOf(r.MemberNumber, r.DependantSuffix), ct);
                    if (patient.IsFailure)
                    {
                        return patient.ToHttp(_ => Results.Ok());
                    }

                    var requested = await d.Send(new RequestAuthorisation(patient.Value.MembershipId, patient.Value.PatientPartyId, execution.External!.ProviderId!,
                        r.DiagnosisCodes, r.ServiceType, r.Lines), ct);
                    return requested.IsFailure
                        ? requested.ToHttp(_ => Results.Ok())
                        : (await d.Query(new GetPortalPreauthorisation(requested.Value.Id), ct)).ToHttp(v => Results.Created($"/api/v1/portal/health/preauthorisations/{v.Id}", v));
                })
            .Produces<PortalPreauthorisationView>(StatusCodes.Status201Created)
            .WithName("PortalHealthRequestAuthorisation").WithSummary("A provider requests a preauthorisation, as itself").RequirePermission(HealthPermissions.PreauthCreate)
            .RequireIdempotencyKey().ForPortal(provider);
        portal.MapGet("/remittances/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalRemittance(id), ct)).ToHttp(Results.Ok))
            .Produces<RemittanceDetailView>(StatusCodes.Status200OK)
            .WithName("PortalHealthGetRemittance").WithSummary("The provider's remittance: gross, offsets with the original claim, its reversal and its replacement, and the net").RequirePermission(HealthPermissions.RunRead).ForPortal(provider);
        portal.MapGet("/recoveries", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalRecoveries(after, size), ct), r => r.Id))
            .Produces<CursorPage<PortalRecoveryView>>(StatusCodes.Status200OK)
            .WithName("PortalHealthListRecoveries").WithSummary("What the provider owes, and what has been deducted").RequirePermission(HealthPermissions.RecoveryRead).ForPortal(provider);
        portal.MapGet("/credit-notes", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalCreditNotes(after, size), ct), n => n.Id))
            .Produces<CursorPage<ProviderCreditNoteView>>(StatusCodes.Status200OK)
            .WithName("PortalHealthListCreditNotes").WithSummary("The provider's credit notes").RequirePermission(HealthPermissions.CreditNoteRead).ForPortal(provider);
        portal.MapPost("/credit-notes", async (SubmitPortalCreditNote r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/portal/health/credit-notes/{v.Id}", v)))
            .Produces<ProviderCreditNoteView>(StatusCodes.Status201Created)
            .WithName("PortalHealthSubmitCreditNote").WithSummary("A provider submits a credit note on its own claim or remittance, as itself").RequirePermission(HealthPermissions.CreditNoteCreate)
            .RequireIdempotencyKey().ForPortal(provider);
        portal.MapGet("/remittances", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalRemittances(after, size), ct), r => r.Id))
            .Produces<CursorPage<PortalRemittanceView>>(StatusCodes.Status200OK)
            .WithName("PortalHealthListRemittances").WithSummary("The provider's remittances").RequirePermission(HealthPermissions.RunRead).ForPortal(provider);
    }
}

public sealed record RecoveryHoldRequest(Guid CaseId);

public sealed record PortalMedicalClaimRequest(string MemberNumber, string? DependantSuffix, DateOnly ServiceDate, string Currency, Guid? PreauthorisationId,
    IReadOnlyList<Domain.Care.ClaimLineRequest> Lines, Guid? ReplacesClaimId = null);

public sealed record PortalPreauthorisationRequest(string MemberNumber, string? DependantSuffix, [property: BuildingBlocks.Domain.CodeList(HealthCodeLists.Diagnoses, "code")] IReadOnlyList<string> DiagnosisCodes,
    [property: BuildingBlocks.Domain.CodeList(HealthCodeLists.AuthorisationServiceTypes, "service_type")] string ServiceType,
    IReadOnlyList<Domain.Care.PreauthLineRequest> Lines);

