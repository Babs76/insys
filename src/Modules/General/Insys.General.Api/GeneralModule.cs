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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.General.Api.StaticData;
using Insys.General.Application;
using Insys.General.Contracts;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.General.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.General.Api;

public sealed record AttributesRequest(JsonElement Attributes);

public sealed record RevalueRequest(decimal? Valuation, string? ValuationBasis);

public sealed record ReferRequest(string ReasonCode);

public sealed record ReferralDecisionRequest(bool Approved, string DecisionCode, string[]? SpecialConditions, string? DeclineReason);

public sealed record CancelRequest(string ReasonCode, DateOnly EffectiveDate);

public sealed record EndorsementRequest(string EndorsementType, DateOnly EffectiveDate, IReadOnlyList<EndorsementChange> Changes);

public sealed record PartyRequest(Guid PartyId, string Role, string? InsurerRef);

public sealed record AssessorRequest(Guid AssessorPartyId);

public sealed record SettlementRequestBody(Guid PayeePartyId, Guid BankAccountId, string ReserveType, string DecisionCode, string Rationale);

public sealed record DeclineRequest(string DecisionCode, string Rationale);

public sealed record EstimateRequest(Guid RepairerPartyId, decimal Amount);

public sealed record ReportRequest(decimal AssessedAmount, Guid? ReportDocumentId);

public sealed record RecoveryRequest(string RecoveryType, Guid? CounterpartyPartyId, decimal ExpectedAmount);

public sealed record SalvageRequest(string Description);

public sealed record DisposalRequest(string DisposalMethod, decimal Proceeds);

public sealed record RecoveredRequest(decimal Amount, string? ReserveType);

/// <summary>
/// The General module (§10): risk objects, quotes and underwriting referrals, proposals, policies with their risks and
/// coverages, endorsements, renewals, cancellations, claims, loss assessment and recoveries (GEN-001 to GEN-008).
/// </summary>
public sealed class GeneralModule : IModule
{
    public string Name => "General";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddGeneralInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        GeneralStaticDataEndpoints.MapStaticData(api);
        var general = api.MapGroup("/general").WithTags(Name);
        MapUnderwriting(general);
        MapPolicies(general);
        MapClaims(general);
    }

    private static void MapUnderwriting(RouteGroupBuilder general)
    {
        general.MapPost("/risk-objects", async (RegisterRiskObject r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/risk-objects/{v.Id}", v)))
            .Produces<RiskObjectView>(StatusCodes.Status201Created)
            .WithName("GeneralRegisterRiskObject").WithSummary("Register a risk object; its attributes conform to its type's schema (GEN-005)")
            .RequirePermission(GeneralPermissions.RiskCreate).RequireIdempotencyKey();
        general.MapGet("/risk-objects/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRiskObject(id), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRiskObject").WithSummary("Get a risk object").RequirePermission(GeneralPermissions.RiskRead);
        general.MapPut("/risk-objects/{id:guid}/attributes", async (Guid id, AttributesRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new UpdateRiskAttributes(id, r.Attributes), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .WithName("GeneralUpdateRiskAttributes").WithSummary("Update a risk object's attributes, against the schema version in force")
            .RequirePermission(GeneralPermissions.RiskUpdate);
        general.MapPost("/risk-objects/{id:guid}/revalue", async (Guid id, RevalueRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevalueRiskObject(id, r.Valuation, r.ValuationBasis), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .WithName("GeneralRevalueRiskObject").WithSummary("Revalue a risk object, with a valuation basis").RequirePermission(GeneralPermissions.RiskUpdate);
        general.MapPost("/risk-objects/{id:guid}/deactivate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeactivateRiskObject(id), ct)).ToHttp(Results.Ok))
            .Produces<RiskObjectView>(StatusCodes.Status200OK)
            .WithName("GeneralDeactivateRiskObject").WithSummary("Mark a risk object no longer insured").RequirePermission(GeneralPermissions.RiskUpdate);

        general.MapPost("/quotes", async (CreateQuote r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/quotes/{v.Id}", v)))
            .Produces<QuoteView>(StatusCodes.Status201Created)
            .WithName("GeneralCreateQuote").WithSummary("Create a quote, or (fromQuoteId) the next version of one")
            .WithDescription("Rate it through Rating (POST /api/v1/rating/calculations with subjectType GENERAL_QUOTE and the quote's id); the recorded calculation is attached to the quote (PRD-008).")
            .RequirePermission(GeneralPermissions.QuoteCreate).RequireIdempotencyKey();
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
                .WithName($"GeneralQuote{action}").WithSummary(summary).RequirePermission(GeneralPermissions.QuoteUpdate);
        }

        general.MapPost("/quotes/{id:guid}/refer", async (Guid id, ReferRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReferQuote(id, r.ReasonCode), ct)).ToHttp(Results.Ok))
            .Produces<QuoteView>(StatusCodes.Status200OK)
            .WithName("GeneralReferQuote").WithSummary("Refer a rated quote to an underwriter").RequirePermission(GeneralPermissions.QuoteUpdate);
        general.MapPost("/quotes/{id:guid}/referral-decision", async (Guid id, ReferralDecisionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideReferral(id, r.Approved, r.DecisionCode, r.SpecialConditions ?? [], r.DeclineReason), ct)).ToHttp(Results.Ok))
            .Produces<QuoteView>(StatusCodes.Status200OK)
            .WithName("GeneralDecideReferral").WithSummary("An underwriter's decision on a referred quote").RequirePermission(GeneralPermissions.QuoteUnderwrite);

        general.MapPost("/proposals", async (SubmitProposal r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/proposals/{v.Id}", v)))
            .Produces<ProposalView>(StatusCodes.Status201Created)
            .WithName("GeneralSubmitProposal").WithSummary("Submit a proposal, with declarations, on an accepted quote version")
            .RequirePermission(GeneralPermissions.QuoteUpdate).RequireIdempotencyKey();
        general.MapGet("/proposals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetProposal(id), ct)).ToHttp(Results.Ok))
            .Produces<ProposalView>(StatusCodes.Status200OK)
            .WithName("GeneralGetProposal").WithSummary("Get a proposal").RequirePermission(GeneralPermissions.QuoteRead);
        general.MapPost("/proposals/{id:guid}/accept", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new AcceptProposal(id), ct)).ToHttp(Results.Ok))
            .Produces<ProposalView>(StatusCodes.Status200OK)
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
                + "and Analytics follow; each needs its tenant configuration (docs/runbooks/dead-letters-and-webhooks.md).")
            .RequirePermission(GeneralPermissions.PolicyCreate).RequireIdempotencyKey();
        general.MapGet("/policies/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPolicy(id), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .WithName("GeneralGetPolicy").WithSummary("Get a policy with its risks, coverages and cancellations").RequirePermission(GeneralPermissions.PolicyRead);
        general.MapPost("/policies/{id:guid}/renew", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RenewPolicy(id), ct)).ToHttp(v => Results.Created($"/api/v1/general/policies/{v.Id}", v)))
            .Produces<PolicyView>(StatusCodes.Status201Created)
            .WithName("GeneralRenewPolicy").WithSummary("Renew a policy whose product does not re-rate at renewal (GEN-004)")
            .RequirePermission(GeneralPermissions.PolicyCreate).RequireIdempotencyKey();
        general.MapPost("/policies/{id:guid}/cancel", async (Guid id, CancelRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelPolicy(id, r.ReasonCode, r.EffectiveDate), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .WithName("GeneralCancelPolicy").WithSummary("Cancel a policy; the refund follows the reason's refund rule (GEN-004)")
            .RequirePermission(GeneralPermissions.PolicyCancel);
        general.MapPost("/policies/{id:guid}/reinstate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReinstatePolicy(id), ct)).ToHttp(Results.Ok))
            .Produces<PolicyView>(StatusCodes.Status200OK)
            .WithName("GeneralReinstatePolicy").WithSummary("Reinstate a cancelled policy where permitted").RequirePermission(GeneralPermissions.PolicyCancel);

        general.MapPost("/policies/{id:guid}/endorsements", async (Guid id, EndorsementRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DraftEndorsement(id, r.EndorsementType, r.EffectiveDate, r.Changes), ct))
                .ToHttp(v => Results.Created($"/api/v1/general/endorsements/{v.Id}", v)))
            .Produces<EndorsementView>(StatusCodes.Status201Created)
            .WithName("GeneralDraftEndorsement").WithSummary("Draft an endorsement effective within the policy period (GEN-003)")
            .WithDescription("Rate it through Rating (subjectType GENERAL_ENDORSEMENT) with the endorsed policy's inputs; the premium is the new annual premium.")
            .RequirePermission(GeneralPermissions.EndorsementCreate).RequireIdempotencyKey();
        general.MapGet("/endorsements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetEndorsement(id), ct)).ToHttp(Results.Ok))
            .Produces<EndorsementView>(StatusCodes.Status200OK)
            .WithName("GeneralGetEndorsement").WithSummary("Get an endorsement").RequirePermission(GeneralPermissions.EndorsementRead);
        general.MapPost("/endorsements/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SubmitEndorsement(id), ct)).ToHttp(Results.Ok))
            .Produces<EndorsementView>(StatusCodes.Status200OK)
            .WithName("GeneralSubmitEndorsement").WithSummary("Submit a rated endorsement for a second person's approval in Workflow (SRS-002)")
            .RequirePermission(GeneralPermissions.EndorsementCreate);
    }

    private static void MapClaims(RouteGroupBuilder general)
    {
        general.MapPost("/claims", async (NotifyLoss r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/general/claims/{v.Id}", v)))
            .Produces<ClaimView>(StatusCodes.Status201Created)
            .WithName("GeneralNotifyLoss").WithSummary("Notify a loss (first notification)").RequirePermission(GeneralPermissions.ClaimCreate).RequireIdempotencyKey();
        general.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralGetClaim").WithSummary("Get a claim").RequirePermission(GeneralPermissions.ClaimRead);
        general.MapPost("/claims/{id:guid}/register", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RegisterClaim(id), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralRegisterClaim").WithSummary("Register a claim: the cover is validated (GEN-006) and a claim number allocated (CLM-005)")
            .RequirePermission(GeneralPermissions.ClaimCreate);
        general.MapPost("/claims/{id:guid}/parties", async (Guid id, PartyRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddClaimParty(id, r.PartyId, r.Role, r.InsurerRef), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralAddClaimParty").WithSummary("Add a party to a claim").RequirePermission(GeneralPermissions.ClaimUpdate);
        general.MapPost("/claims/{id:guid}/assessor", async (Guid id, AssessorRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AppointAssessor(id, r.AssessorPartyId), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralAppointAssessor").WithSummary("Appoint an assessor on an approved panel (GEN-007)").RequirePermission(GeneralPermissions.ClaimUpdate);
        general.MapPost("/claims/{id:guid}/settlement", async (Guid id, SettlementRequestBody r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RequestSettlement(id, r.PayeePartyId, r.BankAccountId, r.ReserveType, r.DecisionCode, r.Rationale), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralRequestSettlement").WithSummary("Approve a settlement: excess and limits are applied (GEN-008); a second person approves it in Workflow (SRS-002)")
            .RequirePermission(GeneralPermissions.ClaimDecide);
        general.MapPost("/claims/{id:guid}/decline", async (Guid id, DeclineRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeclineClaim(id, r.DecisionCode, r.Rationale), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("GeneralDeclineClaim").WithSummary("Decline a claim, with the decision's rationale (CLM-003)").RequirePermission(GeneralPermissions.ClaimDecide);
        foreach (var (action, route) in new[] { (ClaimAction.Close, "close"), (ClaimAction.Reopen, "reopen") })
        {
            general.MapPost($"/claims/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ChangeClaim(id, action), ct)).ToHttp(Results.Ok))
                .Produces<ClaimView>(StatusCodes.Status200OK)
                .WithName($"GeneralClaim{action}").WithSummary($"{action} a claim").RequirePermission(GeneralPermissions.ClaimUpdate);
        }

        general.MapGet("/claims/{id:guid}/assessments", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new ListAssessments(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<AssessmentView>>(StatusCodes.Status200OK)
            .WithName("GeneralListAssessments").WithSummary("List a claim's loss assessments").RequirePermission(GeneralPermissions.ClaimRead);
        general.MapGet("/assessments/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAssessment(id), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .WithName("GeneralGetAssessment").WithSummary("Get a loss assessment").RequirePermission(GeneralPermissions.ClaimRead);
        foreach (var (action, route) in new[] { (AssessmentAction.Start, "start"), (AssessmentAction.Accept, "accept"), (AssessmentAction.RequestFurtherWork, "further-work") })
        {
            general.MapPost($"/assessments/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeAssessment(id, action), ct)).ToHttp(Results.Ok))
                .Produces<AssessmentView>(StatusCodes.Status200OK)
                .WithName($"GeneralAssessment{action}").WithSummary($"{action} a loss assessment").RequirePermission(GeneralPermissions.AssessmentUpdate);
        }

        general.MapPost("/assessments/{id:guid}/estimates", async (Guid id, EstimateRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordEstimate(id, r.RepairerPartyId, r.Amount), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .WithName("GeneralRecordEstimate").WithSummary("Record a panel repairer's estimate").RequirePermission(GeneralPermissions.AssessmentUpdate);
        general.MapPost("/assessments/{id:guid}/report", async (Guid id, ReportRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SubmitReport(id, r.AssessedAmount, r.ReportDocumentId), ct)).ToHttp(Results.Ok))
            .Produces<AssessmentView>(StatusCodes.Status200OK)
            .WithName("GeneralSubmitReport").WithSummary("Submit the assessment report; the coverage's excess is applied (GEN-008)")
            .RequirePermission(GeneralPermissions.AssessmentUpdate);

        general.MapPost("/claims/{id:guid}/recoveries", async (Guid id, RecoveryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new OpenRecovery(id, r.RecoveryType, r.CounterpartyPartyId, r.ExpectedAmount), ct))
                .ToHttp(v => Results.Created($"/api/v1/general/recoveries/{v.Id}", v)))
            .Produces<RecoveryView>(StatusCodes.Status201Created)
            .WithName("GeneralOpenRecovery").WithSummary("Open a salvage or third-party recovery (GEN-007)")
            .RequirePermission(GeneralPermissions.RecoveryUpdate).RequireIdempotencyKey();
        general.MapGet("/recoveries/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralGetRecovery").WithSummary("Get a recovery").RequirePermission(GeneralPermissions.ClaimRead);
        foreach (var (action, route) in new[] { (RecoveryAction.SendDemand, "demand"), (RecoveryAction.WriteOff, "write-off"), (RecoveryAction.Close, "close") })
        {
            general.MapPost($"/recoveries/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeRecovery(id, action), ct)).ToHttp(Results.Ok))
                .Produces<RecoveryView>(StatusCodes.Status200OK)
                .WithName($"GeneralRecovery{action}").WithSummary($"{action} on a recovery").RequirePermission(GeneralPermissions.RecoveryUpdate);
        }

        general.MapPost("/recoveries/{id:guid}/salvage", async (Guid id, SalvageRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddSalvageItem(id, r.Description), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralAddSalvage").WithSummary("Add a salvage item").RequirePermission(GeneralPermissions.RecoveryUpdate);
        general.MapPost("/recoveries/{id:guid}/salvage/{itemId:guid}/dispose", async (Guid id, Guid itemId, DisposalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DisposeSalvage(id, itemId, r.DisposalMethod, r.Proceeds), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralDisposeSalvage").WithSummary("Record a salvage item's disposal and proceeds").RequirePermission(GeneralPermissions.RecoveryUpdate);
        general.MapPost("/recoveries/{id:guid}/recovered", async (Guid id, RecoveredRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RecordRecovery(id, r.Amount, r.ReserveType), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("GeneralRecordRecovery").WithSummary("Record money recovered; it becomes a claim financial movement (CLM-004)")
            .RequirePermission(GeneralPermissions.RecoveryUpdate).RequireIdempotencyKey();
    }
}
