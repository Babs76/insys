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
using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.General.Api.StaticData;
using Insys.General.Application;
using Insys.General.Contracts;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Renewals;
using Insys.General.Domain.Underwriting;
using Insys.General.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.General.Api;

public sealed record AttributesRequest(JsonElement Attributes);

/// <param name="ValuationCurrency">The valuation's currency (BR-033), a 3-letter code: required with a valuation, and only with one.</param>
public sealed record RevalueRequest(decimal? Valuation, [property: BuildingBlocks.Domain.CodeList(CodeLists.ValuationBases, "code")] string? ValuationBasis,
    string? ValuationCurrency = null);

public sealed record ReferRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.ReferralReasons, "code")] string ReasonCode);

public sealed record ReferralDecisionRequest(bool Approved, [property: BuildingBlocks.Domain.CodeList(CodeLists.UnderwritingDecisions, "code")] string DecisionCode,
    string[]? SpecialConditions, string? DeclineReason);

public sealed record CancelRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.CancellationReasons, "reason_code")] string ReasonCode, DateOnly EffectiveDate);

/// <param name="IntermediaryCode">B-001: for an INTERMEDIARY endorsement (a broker of record change, with no changes), the intermediary the policy moves to.</param>
public sealed record EndorsementRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.EndorsementTypes, "code")] string EndorsementType, DateOnly EffectiveDate,
    IReadOnlyList<EndorsementChange> Changes, string? IntermediaryCode = null);

/// <param name="DocumentId">The appointment letter, a document filed against the endorsement.</param>
public sealed record AppointmentLetterRequest(Guid DocumentId);

/// <param name="Role">The party's role on the claim, in force in general.cfg_claim_party_roles (OQ-122).</param>
public sealed record PartyRequest(Guid PartyId, [property: BuildingBlocks.Domain.CodeList(CodeLists.ClaimPartyRoles, "code")] string Role, string? InsurerRef);

public sealed record AssessorRequest(Guid AssessorPartyId);

public sealed record SettlementRequestBody(Guid PayeePartyId, Guid BankAccountId,
    [property: BuildingBlocks.Domain.CodeList(CodeLists.ReserveTypes, "code")] string ReserveType,
    [property: BuildingBlocks.Domain.CodeList(CodeLists.ClaimDecisionReasons, "code")] string DecisionCode, string Rationale);

public sealed record DeclineRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.ClaimDecisionReasons, "code")] string DecisionCode, string Rationale);

public sealed record EstimateRequest(Guid RepairerPartyId, decimal Amount);

public sealed record ReportRequest(decimal AssessedAmount, Guid? ReportDocumentId);

public sealed record RecoveryRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.RecoveryTypes, "code")] string RecoveryType, Guid? CounterpartyPartyId,
    decimal ExpectedAmount);

public sealed record SalvageRequest(string Description);

public sealed record DisposalRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.DisposalMethods, "code")] string DisposalMethod, decimal Proceeds);

public sealed record RecoveredRequest(decimal Amount, [property: BuildingBlocks.Domain.CodeList(CodeLists.ReserveTypes, "code")] string? ReserveType);

/// <summary>
/// The General module (§10): risk objects, quotes and underwriting referrals, proposals, policies with their risks and
/// coverages, endorsements, renewals, cancellations, claims, loss assessment and recoveries (GEN-001 to GEN-008).
/// </summary>
public sealed class GeneralModule : IModule
{
    public string Name => "General";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => GeneralFunctions.All;

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    /// <summary>F005: its commands create or change a customer's business, so they are checked against the request's channel.</summary>
    public bool ChannelScoped => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddGeneralInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        GeneralStaticDataEndpoints.MapStaticData(api);
        var general = api.MapGroup("/general").WithTags(Name).ETagsFromRowVersion();
        MapUnderwriting(general);
        MapPolicies(general);
        MapClaims(general);

        MapPortal(api.MapGroup("/portal/general").WithTags(Name));
    }

    private static void MapUnderwriting(RouteGroupBuilder general)
    {
        general.MapPost("/risk-objects", async (RegisterRiskObject r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/risk-objects/{v.Id}", v)))
            .Produces<RiskObjectView>(StatusCodes.Status201Created)
            .WithName("GeneralRegisterRiskObject").WithSummary("Register a risk object; its attributes conform to its type's schema (GEN-005)")
            .RequirePermission(GeneralPermissions.RiskCreate).RequireIdempotencyKey();
        general.MapGet("/risk-objects", async (string? objectType, string? riskZone, Guid? policyId, Guid? ownerPartyId, string? cursor, int? limit, IDispatcher d,
                    CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListRiskObjects(objectType, riskZone, policyId, after, size, ownerPartyId), ct), r => r.Id))
            .Produces<CursorPage<RiskObjectView>>(StatusCodes.Status200OK)
            .WithName("GeneralListRiskObjects").WithSummary("List risk objects by type, risk zone, the policy insuring them (BR-034) or their owner (BR-092)")
            .RequirePermission(GeneralPermissions.RiskRead);
        general.MapGet("/risk-objects/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRiskObject(id), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRiskObject").WithSummary("Get a risk object").RequirePermission(GeneralPermissions.RiskRead);
        general.MapPut("/risk-objects/{id:guid}/attributes", async (Guid id, AttributesRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateRiskAttributes(id, r.Attributes), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralUpdateRiskAttributes").WithSummary("Update a risk object's attributes, against the schema version in force")
            .RequirePermission(GeneralPermissions.RiskUpdate);
        general.MapPost("/risk-objects/{id:guid}/revalue", async (Guid id, RevalueRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevalueRiskObject(id, r.Valuation, r.ValuationBasis, r.ValuationCurrency), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralRevalueRiskObject").WithSummary("Revalue a risk object, with a valuation basis").RequirePermission(GeneralPermissions.RiskUpdate);
        general.MapPost("/risk-objects/{id:guid}/owner", async (Guid id, RiskOwnerRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeRiskObjectOwner(id, r.OwnerPartyId), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralChangeRiskObjectOwner").WithSummary("Set, change or clear the client a risk object belongs to (BR-092); audited")
            .WithDescription("A broker sees the item while it services the owner, so a new owner moves it to the new owner's broker. The same owner again is 409 "
                + "INVALID_STATE_TRANSITION; an owner who is not a party of the tenant is 422.")
            .RequirePermission(GeneralPermissions.RiskUpdate);
        general.MapPost("/risk-objects/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateRiskObject(id), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralDeactivateRiskObject").WithSummary("Mark a risk object no longer insured").RequirePermission(GeneralPermissions.RiskUpdate);

        general.MapPost("/quotes", async (CreateQuote r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/quotes/{v.Id}", v)))
            .Produces<QuoteView>(StatusCodes.Status201Created)
            .WithName("GeneralCreateQuote").WithSummary("Create a quote, or (fromQuoteId) the next version of one")
            .WithEventualEffects("The quote reads Rated (with its premium) once General handles Rating's PremiumCalculated for it, after the calculation is "
                + "requested (POST /rating/calculations); read the quote again (BR-038). A new version of a quote priced as it is made (pricedOnCreation, a "
                + "broker's portal quote) is priced the same way, with no request (BR-095).")
            .WithDescription("Rate it through Rating (POST /api/v1/rating/calculations with subjectType GENERAL_QUOTE and the quote's id); the recorded calculation is attached to the quote (PRD-008). "
                + "A General product's line is its class code, or its family code when it has no class; panels, perils and approved lines are matched on it.")
            .RequirePermission(GeneralPermissions.QuoteCreate).RequireIdempotencyKey();
        general.MapGet("/quotes", async (Guid? proposerPartyId, string? quoteNumber, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListQuotes(proposerPartyId, quoteNumber, after, size), ct), q => q.Id))
            .Produces<CursorPage<QuoteSummary>>(StatusCodes.Status200OK)
            .WithName("GeneralListQuotes").WithSummary("List quotes, by proposer or quote number (GAP-032)").RequirePermission(GeneralPermissions.QuoteRead);
        general.MapGet("/quotes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetQuote(id), ct)).ToHttp(Results.Ok))
            .Produces<QuoteView>(StatusCodes.Status200OK)
            .WithName("GeneralGetQuote").WithSummary("Get a quote version with its risks and referrals").RequirePermission(GeneralPermissions.QuoteRead);
        foreach (var (action, route, summary) in new[]
                 {
                     (QuoteAction.Issue, "issue", "Issue a rated quote, valid for the product's validity period"),
                     (QuoteAction.Accept, "accept", "Accept an issued quote within its validity period (GEN-001)"),
                     (QuoteAction.Reject, "reject", "Reject an issued quote"),
                 })
        {
            general.MapPost($"/quotes/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ChangeQuote(id, action), ct)).ToHttp(Results.Ok))
                .Produces<QuoteView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"GeneralQuote{action}").WithSummary(summary).RequirePermission(GeneralPermissions.QuoteUpdate);
        }

        general.MapPost("/quotes/{id:guid}/refer", async (Guid id, ReferRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReferQuote(id, r.ReasonCode), ct)).ToHttp(Results.Ok))
            .Produces<QuoteView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralReferQuote").WithSummary("Refer a rated quote to an underwriter").RequirePermission(GeneralPermissions.QuoteUpdate);
        general.MapPost("/quotes/{id:guid}/referral-decision", async (Guid id, ReferralDecisionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideReferral(id, r.Approved, r.DecisionCode, r.SpecialConditions ?? [], r.DeclineReason), ct)).ToHttp(Results.Ok))
            .Produces<QuoteView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralDecideReferral").WithSummary("An underwriter's decision on a referred quote")
            .WithDescription("The underwriter who referred the quote cannot decide it (403 MAKER_CHECKER_SAME_PERSON, BR-037); a referral raised by rules has no referrer. Decided under the tenant's underwriting authority (rules.cfg_authority_matrices: the product, the quote's total sum insured and the decision code), or 403 GENERAL_UW_AUTHORITY_EXCEEDED.").RequirePermission(GeneralPermissions.QuoteUnderwrite);

        general.MapPost("/proposals", async (SubmitProposal r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/proposals/{v.Id}", v)))
            .Produces<ProposalView>(StatusCodes.Status201Created)
            .WithName("GeneralSubmitProposal").WithSummary("Submit a proposal, with declarations, on an accepted quote version")
            .RequirePermission(GeneralPermissions.QuoteUpdate).RequireIdempotencyKey();
        general.MapGet("/proposals", async (Guid? quoteId, ProposalStatus? status, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListProposals(quoteId, status, after, size), ct), p => p.Id))
            .Produces<CursorPage<ProposalView>>(StatusCodes.Status200OK)
            .WithName("GeneralListProposals").WithSummary("List proposals by quote or status, through their quotes in the caller's scope (BR-034)")
            .RequirePermission(GeneralPermissions.QuoteRead);
        general.MapGet("/proposals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetProposal(id), ct)).ToHttp(Results.Ok))
            .Produces<ProposalView>(StatusCodes.Status200OK)
            .WithName("GeneralGetProposal").WithSummary("Get a proposal").RequirePermission(GeneralPermissions.QuoteRead);
        general.MapPost("/proposals/{id:guid}/accept", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new AcceptProposal(id), ct)).ToHttp(Results.Ok))
            .Produces<ProposalView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralAcceptProposal").WithSummary("Accept a proposal").RequirePermission(GeneralPermissions.QuoteUnderwrite);
    }

    private static void MapPolicies(RouteGroupBuilder general)
    {
        general.MapPost("/policies", async (IssuePolicy r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/policies/{v.Id}", v)))
            .Produces<PolicyView>(StatusCodes.Status201Created)
            .WithName("GeneralIssuePolicy").WithSummary("Issue a policy from an accepted quote version")
            .WithDescription("Billing opens the account (if new) and the premium schedule; Documents generates the schedule and certificate.")
            .WithEventualEffects("Billing (account, premium schedule, first invoice), Documents (policy schedule, certificate), Reinsurance (cession) "
                + "and Analytics follow; each needs its tenant configuration (docs/runbooks/dead-letters-and-webhooks.md). The policy read lists the "
                + "invoices, documents and cessions as they arrive (effects, BR-038).")
            .RequirePermission(GeneralPermissions.PolicyCreate).RequireIdempotencyKey();
        general.MapGet("/policies", async (Guid? policyholderPartyId, string? policyNumber, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPolicies(policyholderPartyId, policyNumber, after, size), ct), p => p.Id))
            .Produces<CursorPage<PolicySummary>>(StatusCodes.Status200OK)
            .WithName("GeneralListPolicies").WithSummary("List policies, by policyholder or policy number (GAP-032)").RequirePermission(GeneralPermissions.PolicyRead);
        general.MapGet("/policies/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPolicy(id), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .WithName("GeneralGetPolicy").WithSummary("Get a policy with its risks, coverages and cancellations").RequirePermission(GeneralPermissions.PolicyRead);
        general.MapPost("/policies/{id:guid}/renew", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RenewPolicy(id), ct)).ToHttp(v => Results.Created($"/api/v1/general/policies/{v.Id}", v)))
            .Produces<PolicyView>(StatusCodes.Status201Created)
            .WithName("GeneralRenewPolicy").WithSummary("Renew a policy whose product does not re-rate at renewal (GEN-004)")
            .WithDescription("Only an In force policy is renewed; an Issued one, not yet in force, never is (BR-037).")
            .RequirePermission(GeneralPermissions.PolicyCreate).RequireIdempotencyKey();
        general.MapPost("/policies/{id:guid}/cancel", async (Guid id, CancelRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelPolicy(id, r.ReasonCode, r.EffectiveDate), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralCancelPolicy").WithSummary("Cancel a policy; the refund follows the reason's refund rule (GEN-004)")
            .WithDescription("An In force policy is cancelled from any date in its period. An Issued policy, not yet in force, is cancelled only from inception: "
                + "effectiveDate is its period start, otherwise 422 OUTSIDE_POLICY_PERIOD (BR-037).")
            .RequirePermission(GeneralPermissions.PolicyCancel);
        general.MapPost("/policies/{id:guid}/reinstate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReinstatePolicy(id), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralReinstatePolicy").WithSummary("Reinstate a cancelled policy where permitted")
            .WithDescription("Reinstated before its period starts (a policy cancelled from inception), it is Issued again; otherwise In force (BR-037).").RequirePermission(GeneralPermissions.PolicyCancel);

        general.MapPost("/policies/{id:guid}/endorsements", async (Guid id, EndorsementRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DraftEndorsement(id, r.EndorsementType, r.EffectiveDate, r.Changes, r.IntermediaryCode), ct))
                .ToHttp(v => Results.Created($"/api/v1/general/endorsements/{v.Id}", v)))
            .Produces<EndorsementView>(StatusCodes.Status201Created)
            .WithName("GeneralDraftEndorsement").WithSummary("Draft an endorsement effective within the policy period (GEN-003)")
            .WithEventualEffects("The endorsement reads Rated (with its new annual premium) once General handles Rating's PremiumCalculated for it; read it "
                + "again (BR-038).")
            .WithDescription("Rate it through Rating (subjectType GENERAL_ENDORSEMENT) with the endorsed policy's inputs; the premium is the new annual premium.")
            .RequirePermission(GeneralPermissions.EndorsementCreate).RequireIdempotencyKey();
        general.MapGet("/policies/{id:guid}/endorsements", async (Guid id, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPolicyEndorsements(id, after, size), ct), e => e.Id))
            .Produces<CursorPage<EndorsementView>>(StatusCodes.Status200OK)
            .WithName("GeneralListPolicyEndorsements").WithSummary("A policy's endorsements; 404 for a policy outside the caller's scope (BR-034)")
            .RequirePermission(GeneralPermissions.EndorsementRead);
        general.MapGet("/endorsements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetEndorsement(id), ct)).ToHttp(Results.Ok))
            .Produces<EndorsementView>(StatusCodes.Status200OK)
            .WithName("GeneralGetEndorsement").WithSummary("Get an endorsement").RequirePermission(GeneralPermissions.EndorsementRead);
        general.MapPost("/endorsements/{id:guid}/appointment-letter", async (Guid id, AppointmentLetterRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordAppointmentLetter(id, r.DocumentId), ct)).ToHttp(Results.Ok))
            .Produces<EndorsementView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralRecordAppointmentLetter")
            .WithSummary("Record the policyholder's broker appointment letter on a broker of record change, before it is submitted (B-001, Wave 4 addition 3)")
            .WithDescription("documentId is a document filed against the endorsement (businessRefType GENERAL_ENDORSEMENT). An INTERMEDIARY endorsement without it "
                + "is refused at submission with 422 APPOINTMENT_LETTER_REQUIRED.")
            .RequirePermission(GeneralPermissions.EndorsementCreate);
        general.MapPost("/endorsements/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SubmitEndorsement(id), ct)).ToHttp(Results.Ok))
            .Produces<EndorsementView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralSubmitEndorsement").WithSummary("Submit a rated endorsement for a second person's approval in Workflow (SRS-002)")
            .RequirePermission(GeneralPermissions.EndorsementCreate).HasApprovalFlow();
    }

    private static void MapClaims(RouteGroupBuilder general)
    {
        general.MapPost("/claims", async (NotifyLoss r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/claims/{v.Id}", v)))
            .Produces<ClaimView>(StatusCodes.Status201Created)
            .WithName("GeneralNotifyLoss").WithSummary("Notify a loss (first notification)").RequirePermission(GeneralPermissions.ClaimCreate).RequireIdempotencyKey();
        general.MapGet("/claims", async (Guid? policyId, string? claimNumber, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListClaims(policyId, claimNumber, after, size), ct), c => c.Id))
            .Produces<CursorPage<ClaimSummary>>(StatusCodes.Status200OK)
            .WithName("GeneralListClaims").WithSummary("List claims, by policy or claim number (GAP-032)").RequirePermission(GeneralPermissions.ClaimRead);
        general.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralGetClaim").WithSummary("Get a claim").RequirePermission(GeneralPermissions.ClaimRead);
        general.MapPost("/claims/{id:guid}/register", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RegisterClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralRegisterClaim").WithSummary("Register a claim: the cover is validated (GEN-006) and a claim number allocated (CLM-005)")
            .RequirePermission(GeneralPermissions.ClaimCreate);
        general.MapPost("/claims/{id:guid}/parties", async (Guid id, PartyRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddClaimParty(id, r.PartyId, r.Role, r.InsurerRef), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralAddClaimParty").WithSummary("Add a party to a claim").RequirePermission(GeneralPermissions.ClaimUpdate);
        general.MapPost("/claims/{id:guid}/assessor", async (Guid id, AssessorRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AppointAssessor(id, r.AssessorPartyId), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralAppointAssessor").WithSummary("Appoint an assessor on an approved panel (GEN-007)")
            .WithEventualEffects("The loss assessment is created from the AssessorAppointed event; it appears in GET /general/claims/{id}/assessments (BR-038).").RequirePermission(GeneralPermissions.ClaimUpdate);
        general.MapPost("/claims/{id:guid}/settlement", async (Guid id, SettlementRequestBody r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RequestSettlement(id, r.PayeePartyId, r.BankAccountId, r.ReserveType, r.DecisionCode, r.Rationale), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralRequestSettlement").WithSummary("Approve a settlement: excess and limits are applied (GEN-008); a second person approves it in Workflow (SRS-002)")
            .RequirePermission(GeneralPermissions.ClaimDecide).HasApprovalFlow();
        general.MapPost("/claims/{id:guid}/decline", async (Guid id, DeclineRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeclineClaim(id, r.DecisionCode, r.Rationale), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralDeclineClaim").WithSummary("Decline a claim, with the decision's rationale (CLM-003)").RequirePermission(GeneralPermissions.ClaimDecide);
        foreach (var (action, route) in new[] { (ClaimAction.Close, "close"), (ClaimAction.Reopen, "reopen") })
        {
            general.MapPost($"/claims/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ChangeClaim(id, action), ct)).ToHttp(Results.Ok))
                .Produces<ClaimView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"GeneralClaim{action}").WithSummary($"{action} a claim").RequirePermission(GeneralPermissions.ClaimUpdate);
        }

        general.MapGet("/claims/{id:guid}/assessments", async (Guid id, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListAssessments(id, null, null, after, size), ct), a => a.Id))
            .Produces<CursorPage<AssessmentView>>(StatusCodes.Status200OK)
            .WithName("GeneralListAssessments").WithSummary("List a claim's loss assessments; 404 for a claim outside the caller's scope").RequirePermission(GeneralPermissions.ClaimRead);
        general.MapGet("/assessments", async (Domain.Claims.AssessmentStatus? status, Guid? assessorPartyId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListAssessments(null, status, assessorPartyId, after, size), ct), a => a.Id))
            .Produces<CursorPage<AssessmentView>>(StatusCodes.Status200OK)
            .WithName("GeneralListAllAssessments").WithSummary("List loss assessments by status or assessor, on claims in the caller's scope (BR-034)")
            .RequirePermission(GeneralPermissions.ClaimRead);
        general.MapGet("/assessments/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAssessment(id), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .WithName("GeneralGetAssessment").WithSummary("Get a loss assessment").RequirePermission(GeneralPermissions.ClaimRead);
        foreach (var (action, route) in new[] { (AssessmentAction.Start, "start"), (AssessmentAction.Accept, "accept"), (AssessmentAction.RequestFurtherWork, "further-work") })
        {
            general.MapPost($"/assessments/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeAssessment(id, action), ct)).ToHttp(Results.Ok))
                .Produces<AssessmentView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"GeneralAssessment{action}").WithSummary($"{action} a loss assessment").RequirePermission(GeneralPermissions.AssessmentUpdate);
        }

        general.MapPost("/assessments/{id:guid}/estimates", async (Guid id, EstimateRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordEstimate(id, r.RepairerPartyId, r.Amount), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralRecordEstimate").WithSummary("Record a panel repairer's estimate").RequirePermission(GeneralPermissions.AssessmentUpdate);
        general.MapPost("/assessments/{id:guid}/report", async (Guid id, ReportRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SubmitReport(id, r.AssessedAmount, r.ReportDocumentId), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralSubmitReport").WithSummary("Submit the assessment report; the coverage's excess is applied (GEN-008)")
            .RequirePermission(GeneralPermissions.AssessmentUpdate);

        general.MapPost("/claims/{id:guid}/recoveries", async (Guid id, RecoveryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpenRecovery(id, r.RecoveryType, r.CounterpartyPartyId, r.ExpectedAmount), ct))
                .ToHttp(v => Results.Created($"/api/v1/general/recoveries/{v.Id}", v)))
            .Produces<RecoveryView>(StatusCodes.Status201Created)
            .WithName("GeneralOpenRecovery").WithSummary("Open a salvage or third-party recovery (GEN-007)")
            .RequirePermission(GeneralPermissions.RecoveryUpdate).RequireIdempotencyKey();
        // Wave 4 §4.2: renewal reviews, and the underwriter's decision on a referred or ineligible one.
        general.MapGet("/renewals", async (RenewalStatus? status, Guid? policyId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListRenewals(status, policyId, after, size), ct), r => r.Id))
            .Produces<CursorPage<RenewalView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GeneralListRenewals")
            .WithSummary("Renewal reviews, newest first: by status (REFERRED for the underwriting work list) and policy (Wave 4 §4.2)")
            .RequirePermission(GeneralPermissions.PolicyRead);
        general.MapGet("/renewals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<RenewalView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRenewal")
            .WithSummary("A renewal review: the claims history and risk changes it read, each check's result, the re-rating and the offered premium (Wave 4 §4.2)")
            .RequirePermission(GeneralPermissions.PolicyRead);
        general.MapPost("/renewals/{id:guid}/decision", async (Guid id, DecideRenewalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideRenewal(id, r.Offer, r.DecisionCode, r.LoadingPct), ct)).ToHttp(Results.Ok))
            .Produces<RenewalView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .TakesIfMatch()
            .WithName("GeneralDecideRenewal")
            .WithSummary("Decide a referred or ineligible renewal (F019): offer it, with any loading in per cent, or not, within your underwriting authority")
            .RequirePermission(GeneralPermissions.QuoteUnderwrite);
        // Wave 4 §4.3, §4.4: the customer's answer recorded by staff, and an insurer's non-renewal.
        general.MapPost("/renewals/{id:guid}/accept", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new AcceptRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<RenewalView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .TakesIfMatch()
            .WithName("GeneralAcceptRenewal")
            .WithSummary("Record the customer's acceptance of an offered renewal (Wave 4 §4.4, channel STAFF)")
            .WithDescription("Only an OFFERED renewal is accepted (409 GENERAL_RENEWAL_NOT_OFFERED otherwise). An ON_ACCEPTANCE renewal, or one on worse terms "
                + "than the tenant's threshold, renews at the period end only once accepted.")
            .RequirePermission(GeneralPermissions.RenewalRespond);
        general.MapPost("/renewals/{id:guid}/decline", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new DeclineRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<RenewalView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .TakesIfMatch()
            .WithName("GeneralDeclineRenewal")
            .WithSummary("Record the customer's decline of a renewal (Wave 4 §4.4): the policy expires at its period end")
            .RequirePermission(GeneralPermissions.RenewalRespond);
        // F026: retention cases.
        general.MapGet("/retention-cases", async (string? status, Guid? policyId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListRetentionCases(status, policyId, after, size), ct), c => c.Id))
            .Produces<CursorPage<RetentionCaseView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GeneralListRetentionCases").WithSummary("Retention cases, newest first, by status (OPEN, CLOSED) and policy, within your scope (F026)")
            .RequirePermission(GeneralPermissions.RetentionRead);
        general.MapGet("/retention-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRetentionCase(id), ct)).ToHttp(Results.Ok))
            .Produces<RetentionCaseView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRetentionCase").WithSummary("A retention case: what opened it, its offers and answers, and its outcome (F026)")
            .RequirePermission(GeneralPermissions.RetentionRead);
        general.MapPost("/retention-cases", async (RetentionCaseRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpenRetentionCase(r.PolicyId, r.Trigger, r.Note), ct)).ToHttp(v => Results.Created($"/api/v1/general/retention-cases/{v.Id}", v)))
            .Produces<RetentionCaseView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("GeneralOpenRetentionCase")
            .WithSummary("Open a retention case for a policy (F026): when the policyholder asks to cancel, or another reason to try to keep it")
            .WithDescription("A cancellation is processed as asked (CancelPolicy); the case only records whether the customer changed their mind. A declined "
                + "renewal, and a renewal still awaiting acceptance the tenant's lapse_risk_days before its end, open a case themselves. One case is open per policy.")
            .RequirePermission(GeneralPermissions.RetentionUpdate).RequireIdempotencyKey();
        general.MapPost("/retention-cases/{id:guid}/offers", async (Guid id, RetentionOfferRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new MakeRetentionOffer(id, r.OfferCode), ct)).ToHttp(Results.Ok))
            .Produces<RetentionCaseView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .TakesIfMatch().WithName("GeneralMakeRetentionOffer")
            .WithSummary("Make an offer from the tenant's list (general.cfg_retention_offers), within your authority (F026)")
            .WithDescription("403 RETENTION_OFFER_NOT_PERMITTED when the offer names a role you do not hold; 409 RETENTION_OFFER_USED_UP when it has "
                + "been made on the policy as often as it may be.")
            .RequirePermission(GeneralPermissions.RetentionUpdate);
        general.MapPost("/retention-cases/{id:guid}/offers/{offerId:guid}/answer", async (Guid id, Guid offerId, RetentionAnswerRequest r, IDispatcher d,
                CancellationToken ct) => (await d.Send(new DecideRetentionOffer(id, offerId, r.Accepted), ct)).ToHttp(Results.Ok))
            .Produces<RetentionCaseView>(StatusCodes.Status200OK)
            .WithEventualEffects("an accepted DISCOUNT_PERCENT on a case with a renewal lowers the renewal's offered premium by the per cent and records it as "
                + "accepted (channel STAFF); any other accepted offer is made by staff (an endorsement mid-term, or the renewal's terms).")
            .TakesIfMatch().WithName("GeneralAnswerRetentionOffer").WithSummary("Record the customer's answer to an offer (F026)")
            .RequirePermission(GeneralPermissions.RetentionUpdate);
        general.MapPost("/retention-cases/{id:guid}/outcome", async (Guid id, RetentionOutcomeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CloseRetentionCase(id, r.Outcome, r.Note), ct)).ToHttp(Results.Ok))
            .Produces<RetentionCaseView>(StatusCodes.Status200OK)
            .TakesIfMatch().WithName("GeneralCloseRetentionCase").WithSummary("Close a retention case: RETAINED, LOST or WITHDRAWN (F026); its Workflow task closes")
            .RequirePermission(GeneralPermissions.RetentionUpdate);
        general.MapPost("/policies/{id:guid}/non-renewal", async (Guid id, NonRenewalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ProposeNonRenewal(id, r.ReasonCode), ct)).ToHttp(Results.Ok))
            .Produces<RenewalView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithEventualEffects("with a reason that requires approval, a second person approves it in Workflow (GENERAL_NON_RENEWAL); granted, the notice is "
                + "sent and nonRenewal.status becomes DECIDED; rejected, REJECTED and the renewal goes on as before.")
            .WithName("GeneralProposeNonRenewal")
            .WithSummary("Propose not renewing a policy, with a reason (Wave 4 §4.3, F025)")
            .WithDescription("The reason (general.cfg_non_renewal_reasons) gives the notice days, the customer's wording, and whether a second person approves. "
                + "Once decided, the policyholder gets NON_RENEWAL_NOTICE with the wording, and the renewal is NOT_RENEWED_BY_INSURER: the policy expires at its "
                + "period end. A decision less than the notice before the period end extends cover, pro rata, until the notice runs out (OQ-132 (b)); the "
                + "extra days are billed.")
            .RequirePermission(GeneralPermissions.PolicyNonRenew).RequireIdempotencyKey();
        general.MapGet("/aggregation-alerts", async (string? zone, string? peril, Guid? zoneReference, DateOnly? from, DateOnly? to, string? cursor, int? limit,
                IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListAggregationAlerts(zone, peril, zoneReference, from, to, after, size), ct), a => a.Id))
            .Produces<CursorPage<AggregationAlertView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GeneralListAggregationAlerts")
            .WithSummary("Aggregation alerts: each zone and peril at or above its alert level, once a day (GAP-051, F062), oldest first")
            .WithDescription("Filter by zone, peril, zoneReference (the AGGREGATION_ZONE reference its Workflow review task names) and from/to days. "
                + "Alerts cover the whole tenant: a capacity limit counts every policy, so they are not narrowed by the reader's scope.")
            .RequirePermission(GeneralPermissions.AggregationAlertRead);
        general.MapGet("/aggregation-alerts/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAggregationAlert(id), ct)).ToHttp(Results.Ok))
            .Produces<AggregationAlertView>(StatusCodes.Status200OK)
            .WithName("GeneralGetAggregationAlert").WithSummary("Get an aggregation alert (GAP-051)").RequirePermission(GeneralPermissions.AggregationAlertRead);
        general.MapGet("/claims/{id:guid}/recoveries", async (Guid id, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListClaimRecoveries(id, after, size), ct), r => r.Id))
            .Produces<CursorPage<RecoveryView>>(StatusCodes.Status200OK)
            .WithName("GeneralListClaimRecoveries").WithSummary("A claim's recoveries, with their salvage (BR-032); 404 for a claim outside the caller's scope")
            .RequirePermission(GeneralPermissions.ClaimRead);
        general.MapGet("/recoveries/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRecovery").WithSummary("Get a recovery").RequirePermission(GeneralPermissions.ClaimRead);
        foreach (var (action, route) in new[] { (RecoveryAction.SendDemand, "demand"), (RecoveryAction.WriteOff, "write-off"), (RecoveryAction.Close, "close") })
        {
            general.MapPost($"/recoveries/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeRecovery(id, action), ct)).ToHttp(Results.Ok))
                .Produces<RecoveryView>(StatusCodes.Status200OK)
                .TakesIfMatch()
                .WithName($"GeneralRecovery{action}").WithSummary($"{action} on a recovery").RequirePermission(GeneralPermissions.RecoveryUpdate);
        }

        general.MapPost("/recoveries/{id:guid}/salvage", async (Guid id, SalvageRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddSalvageItem(id, r.Description), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralAddSalvage").WithSummary("Add a salvage item").RequirePermission(GeneralPermissions.RecoveryUpdate);
        general.MapPost("/recoveries/{id:guid}/salvage/{itemId:guid}/dispose", async (Guid id, Guid itemId, DisposalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DisposeSalvage(id, itemId, r.DisposalMethod, r.Proceeds), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("GeneralDisposeSalvage").WithSummary("Record a salvage item's disposal and proceeds").RequirePermission(GeneralPermissions.RecoveryUpdate);
        general.MapPost("/recoveries/{id:guid}/recovered", async (Guid id, RecoveredRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordRecovery(id, r.Amount, r.ReserveType), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralRecordRecovery").WithSummary("Record money recovered; it becomes a claim financial movement (CLM-004)")
            .RequirePermission(GeneralPermissions.RecoveryUpdate).RequireIdempotencyKey();
    }

    /// <summary>
    /// The member and broker portals (external-user access): records within the caller's relationship only, in portal
    /// views. A quote is made under the broker's own code; a claim only on the member's own policy.
    /// </summary>
    private static void MapPortal(RouteGroupBuilder portal)
    {
        const string party = ExternalSubject.Party, broker = ExternalSubject.Intermediary;
        portal.MapGet("/policies", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalPolicies(after, size), ct), p => p.Id))
            .Produces<CursorPage<PortalPolicyView>>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListPolicies").WithSummary("The member's or broker's policies").RequirePermission(GeneralPermissions.PolicyRead).ForPortal(party, broker);
        portal.MapGet("/policies/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalPolicy(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalPolicyView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralGetPolicy").WithSummary("A policy of the member or broker").RequirePermission(GeneralPermissions.PolicyRead).ForPortal(party, broker);
        portal.MapGet("/policies/{id:guid}/loss-codes", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetPortalLossCodes(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalLossCodesView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListLossCodes").WithSummary("The perils and loss causes a member can report a loss under on a policy of theirs (BR-093)")
            .WithDescription("The perils of the policy's product line in force today (general.ref_peril_codes), and the loss causes of those perils "
                + "(general.ref_loss_cause_codes), each with its label: the codes PortalGeneralNotifyLoss takes as perilCode and causeCode. Another's policy is 404.")
            .RequirePermission(GeneralPermissions.ClaimRead).ForPortal(party);
        // Wave 4 §4.6: the renewal invitation, and the member's or broker's answer.
        portal.MapGet("/renewals", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalRenewals(after, size), ct), r => r.Id))
            .Produces<CursorPage<PortalRenewalView>>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListRenewals").WithSummary("The renewals of the member's or broker's policies, once invited (Wave 4 §4.6)")
            .RequirePermission(GeneralPermissions.PolicyRead).ForPortal(party, broker);
        portal.MapGet("/renewals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalRenewalView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralGetRenewal").WithSummary("A renewal of the member's or broker's policy: the offered terms and the answer").RequirePermission(GeneralPermissions.PolicyRead)
            .ForPortal(party, broker).ETagsFromRowVersion();
        portal.MapPost("/renewals/{id:guid}/accept", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PortalAcceptRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalRenewalView>(StatusCodes.Status200OK)
            .TakesIfMatch().WithName("PortalGeneralAcceptRenewal").WithSummary("Accept the renewal invitation (channel PORTAL for the member, BROKER for the broker)")
            .RequirePermission(GeneralPermissions.RenewalRespond).ForPortal(party, broker).ETagsFromRowVersion();
        portal.MapPost("/renewals/{id:guid}/decline", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new PortalDeclineRenewal(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalRenewalView>(StatusCodes.Status200OK)
            .TakesIfMatch().WithName("PortalGeneralDeclineRenewal").WithSummary("Decline the renewal: the policy is not renewed")
            .RequirePermission(GeneralPermissions.RenewalRespond).ForPortal(party, broker).ETagsFromRowVersion();
        portal.MapGet("/quotes", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalQuotes(after, size), ct), q => q.Id))
            .Produces<CursorPage<PortalQuoteView>>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListQuotes").WithSummary("The broker's quotes").RequirePermission(GeneralPermissions.QuoteRead).ForPortal(broker);
        portal.MapGet("/quotes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalQuote(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalQuoteView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralGetQuote").WithSummary("A quote of the broker").RequirePermission(GeneralPermissions.QuoteRead).ForPortal(broker);
        portal.MapPost("/quotes", async (PortalQuoteRequest r, IDispatcher d, CancellationToken ct) =>
                {
                    var allowed = await d.Query(new PortalQuoteAllowed(r.ProposerPartyId, r.Risks.Select(x => x.RiskObjectId).ToList()), ct);
                    if (allowed.IsFailure)
                    {
                        return allowed.ToHttp(_ => Results.Ok());
                    }

                    var created = await d.Send(new CreateQuote(r.ProductCode, null, allowed.Value.ProposerPartyId, allowed.Value.IntermediaryCode, r.Currency, r.PeriodStart,
                        r.PeriodEnd, r.Risks, null, Frequency: r.Frequency), ct);
                    return created.IsFailure
                        ? created.ToHttp(_ => Results.Ok())
                        : (await d.Query(new GetPortalQuote(created.Value.Id), ct)).ToHttp(v => Results.Created($"/api/v1/portal/general/quotes/{v.Id}", v));
                })
            .Produces<PortalQuoteView>(StatusCodes.Status201Created)
            .WithName("PortalGeneralCreateQuote").WithSummary("A broker's quote, under its own code, for a client it services or registered")
            .WithEventualEffects("The quote is priced as it is made (BR-095): it reads Rated with its premium once Rating has priced it; read it again "
                + "(GET /portal/general/quotes/{id}). A quote Rating cannot price stays Draft with ratingIssue (for example RATING_INPUT_MISSING); one over a zone "
                + "and peril limit reads Referred, with no premium, until an underwriter decides it.")
            .WithDescription("The broker never sets or changes the price: the premium is Rating's, from the quote's risk objects' attributes and sum insured. "
                + "Later versions of the quote are priced the same way. Each risk object quoted on belongs to the client (BR-092); another is 403 OUT_OF_SCOPE.")
            .RequirePermission(GeneralPermissions.QuoteCreate).RequireIdempotencyKey().ForPortal(broker);
        portal.MapGet("/risk-objects", async (Guid? clientPartyId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                clientPartyId is not { } client
                    ? Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "clientPartyId is required.", StatusCodes.Status422UnprocessableEntity)
                    : await Page(cursor, limit, (after, size) => d.Query(new ListPortalRiskObjects(client, after, size), ct), r => r.Id))
            .Produces<CursorPage<PortalRiskObjectView>>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListRiskObjects").WithSummary("A client's insured items, for a broker that services the client now (BR-092)")
            .WithDescription("A client the broker does not service now (its live policy, or with none its latest quote, is another intermediary's) is 404.")
            .RequirePermission(GeneralPermissions.RiskRead).ForPortal(broker);
        portal.MapGet("/risk-objects/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalRiskObject(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalRiskObjectView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralGetRiskObject").WithSummary("An insured item of a client the broker services now (BR-092)")
            .RequirePermission(GeneralPermissions.RiskRead).ForPortal(broker);
        portal.MapPost("/risk-objects", async (PortalRiskObjectRequest r, IDispatcher d, CancellationToken ct) =>
                {
                    var allowed = await d.Query(new PortalClientAllowed(r.ClientPartyId), ct);
                    if (allowed.IsFailure)
                    {
                        return allowed.ToHttp(_ => Results.Ok());
                    }

                    var registered = await d.Send(new RegisterRiskObject(r.ObjectType, r.Attributes, r.Valuation, r.ValuationBasis, r.Subdivision, r.PostalCode, r.RiskZone,
                        r.ValuationCurrency, allowed.Value), ct);
                    return registered.IsFailure
                        ? registered.ToHttp(_ => Results.Ok())
                        : (await d.Query(new GetPortalRiskObject(registered.Value.Id), ct)).ToHttp(v => Results.Created($"/api/v1/portal/general/risk-objects/{v.Id}", v));
                })
            .Produces<PortalRiskObjectView>(StatusCodes.Status201Created)
            .WithName("PortalGeneralRegisterRiskObject").WithSummary("A broker registers an insured item for a client it services (BR-092)")
            .WithDescription("The item belongs to the client (ownerPartyId), so the broker can quote on it and list it while it services the client. Its attributes conform "
                + "to its type's schema (GEN-005), and a valuation names its currency.")
            .RequirePermission(GeneralPermissions.RiskCreate).RequireIdempotencyKey().ForPortal(broker);
        portal.MapGet("/claims", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalClaims(after, size), ct), c => c.Id))
            .Produces<CursorPage<PortalClaimView>>(StatusCodes.Status200OK)
            .WithName("PortalGeneralListClaims").WithSummary("The claims on the member's or broker's policies, by the caller's link only (BR-091)")
            .RequirePermission(GeneralPermissions.ClaimRead).ForPortal(party, broker);
        portal.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalClaimView>(StatusCodes.Status200OK)
            .WithName("PortalGeneralGetClaim").WithSummary("A claim on a policy of the member or broker").RequirePermission(GeneralPermissions.ClaimRead).ForPortal(party, broker);
        portal.MapPost("/claims", async (PortalClaimRequest r, IDispatcher d, CancellationToken ct) =>
                {
                    var allowed = await d.Query(new PortalClaimAllowed(r.PolicyId), ct);
                    if (allowed.IsFailure)
                    {
                        return allowed.ToHttp(_ => Results.Ok());
                    }

                    var notified = await d.Send(new NotifyLoss(r.PolicyId, r.RiskObjectId, r.CoverageCode, r.LossDate, r.CauseCode, r.PerilCode, null, r.Description), ct);
                    return notified.IsFailure
                        ? notified.ToHttp(_ => Results.Ok())
                        : (await d.Query(new GetPortalClaim(notified.Value.Id), ct)).ToHttp(v => Results.Created($"/api/v1/portal/general/claims/{v.Id}", v));
                })
            .Produces<PortalClaimView>(StatusCodes.Status201Created)
            .WithName("PortalGeneralNotifyLoss").WithSummary("A member notifies a loss on a policy of theirs")
            .RequirePermission(GeneralPermissions.ClaimCreate).RequireIdempotencyKey().ForPortal(party);
    }

    /// <summary>A cursor page of a list query (GAP-032).</summary>
    private static async Task<IResult> Page<T>(string? cursor, int? limit, Func<Guid?, int, Task<BuildingBlocks.Domain.Result<IReadOnlyList<T>>>> query, Func<T, Guid> id)
    {
        if (!Cursor.TryDecode(cursor, out var after))
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
        }

        var size = Cursor.ClampLimit(limit);
        return (await query(after, size)).ToHttp(items => Results.Ok(new CursorPage<T>(items, items.Count == size ? Cursor.Encode(id(items[^1])) : null)));
    }
}

/// <param name="OwnerPartyId">The client the item belongs to; null clears it.</param>
public sealed record RiskOwnerRequest(Guid? OwnerPartyId);

/// <param name="ClientPartyId">The client the item belongs to: one the broker services, or one it registered.</param>
public sealed record PortalRiskObjectRequest(Guid ClientPartyId, [property: BuildingBlocks.Domain.CodeList(CodeLists.RiskObjectTypes, "object_type")] string ObjectType,
    JsonElement Attributes, decimal? Valuation, [property: BuildingBlocks.Domain.CodeList(CodeLists.ValuationBases, "code")] string? ValuationBasis, string? ValuationCurrency,
    string? Subdivision, string? PostalCode, [property: BuildingBlocks.Domain.CodeList(CodeLists.RiskZones, "zone_code")] string? RiskZone);

/// <param name="Frequency">
/// The billing frequency the client wants to pay in: when the product allows it, the quote shows each instalment beside the
/// annual premium; otherwise it shows the annual premium, labelled ANNUAL.
/// </param>
public sealed record PortalQuoteRequest(string ProductCode, Guid ProposerPartyId, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<RiskRequest> Risks,
    [property: BuildingBlocks.Domain.CodeList(CodeLists.BillingFrequencies, "code")] string? Frequency = null);

public sealed record PortalClaimRequest(Guid PolicyId, Guid? RiskObjectId, string CoverageCode, DateOnly LossDate,
    [property: BuildingBlocks.Domain.CodeList(CodeLists.LossCauses, "cause_code")] string CauseCode, [property: BuildingBlocks.Domain.CodeList(CodeLists.Perils, "peril_code")] string PerilCode,
    string? Description);

/// <summary>An underwriter's decision on a renewal (F019): offer it or not, with an underwriting decision code and any loading in per cent.</summary>
/// <param name="ReasonCode">An insurer's non-renewal reason in force (general.cfg_non_renewal_reasons).</param>
public sealed record NonRenewalRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.NonRenewalReasons, "reason_code")] string ReasonCode);

public sealed record RetentionCaseRequest(Guid PolicyId, [property: BuildingBlocks.Domain.OneOf("CANCELLATION_REQUESTED", "NOT_RENEWING", "LAPSE_RISK")] string Trigger,
    string? Note);

public sealed record RetentionOfferRequest([property: BuildingBlocks.Domain.CodeList(CodeLists.RetentionOffers, "offer_code")] string OfferCode);

public sealed record RetentionAnswerRequest(bool Accepted);

public sealed record RetentionOutcomeRequest([property: BuildingBlocks.Domain.OneOf("RETAINED", "LOST", "WITHDRAWN")] string Outcome, string? Note);

public sealed record DecideRenewalRequest(bool Offer, [property: BuildingBlocks.Domain.CodeList(CodeLists.UnderwritingDecisions, "code")] string DecisionCode,
    decimal? LoadingPct = null);

