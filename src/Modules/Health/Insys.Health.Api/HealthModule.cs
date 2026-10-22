using System.Text.Json;
using Insys.BuildingBlocks.Api;
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

public sealed record DependantRequest(Guid PartyId, string DependantType, bool Student, DateOnly StartDate);

public sealed record DependantRemovalRequest(DateOnly LastDay);

public sealed record PlanChangeRequest(string PlanCode, string OptionCode, DateOnly EffectiveDate, string Reason);

public sealed record MembershipStatusRequest(DateOnly EffectiveDate, string Reason);

public sealed record AuthorisationDecisionRequest(IReadOnlyList<LineAmount> Lines, DateOnly ValidFrom, string? Conditions);

public sealed record AuthorisationRejectionRequest(string Reason);

public sealed record AuthorisationChangeRequest(Guid LineId, decimal AuthorisedAmount);

public sealed record PendReviewRequest(IReadOnlyList<LineReview> Reviews);

public sealed record ReasonRequest(string Reason);

public sealed record CaseOutcomeRequest(string Outcome);

/// <summary>
/// The Health module (§11): schemes, memberships and dependants, eligibility, preauthorisation, medical claims and
/// their adjudication (ADR-012, proposed), benefit accumulators, utilisation and fraud cases, and provider payment runs
/// (HLT-001 to HLT-009).
/// </summary>
public sealed class HealthModule : IModule
{
    public string Name => "Health";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddHealthInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        HealthStaticDataEndpoints.MapStaticData(api);
        var health = api.MapGroup("/health").WithTags(Name);
        MapMembers(health);
        MapCare(health);
        MapRuns(health);
    }

    private static void MapMembers(RouteGroupBuilder health)
    {
        health.MapPost("/schemes", async (RegisterHealthScheme r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/schemes/{v.Id}", v)))
            .WithName("HealthRegisterScheme").WithSummary("Register a sponsor's scheme").RequirePermission(HealthPermissions.SchemeCreate).RequireIdempotencyKey();
        health.MapGet("/schemes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetHealthScheme(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetScheme").WithSummary("Get a scheme").RequirePermission(HealthPermissions.SchemeRead);
        foreach (var (route, action) in new[] { ("renew", SchemeAction.Renew), ("terminate", SchemeAction.Terminate) })
        {
            health.MapPost($"/schemes/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeHealthScheme(id, action), ct)).ToHttp(Results.Ok))
                .WithName($"HealthScheme{action}").WithSummary($"{action} the scheme").RequirePermission(HealthPermissions.SchemeUpdate);
        }

        health.MapPost("/memberships", async (EnrolHealthMember r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/memberships/{v.Id}", v)))
            .WithName("HealthEnrolMember").WithSummary("Enrol a principal member on a plan option; waiting periods are fixed now (HLT-002)")
            .RequirePermission(HealthPermissions.MembershipCreate).RequireIdempotencyKey();
        health.MapGet("/memberships/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetMembership(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetMembership").WithSummary("Get a membership").RequirePermission(HealthPermissions.MembershipRead);
        health.MapGet("/memberships/{id:guid}/accumulators", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetAccumulators(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetAccumulators").WithSummary("The membership's benefit accumulators: limit, used, reserved").RequirePermission(HealthPermissions.AccumulatorRead);
        health.MapPost("/memberships/{id:guid}/dependants", async (Guid id, DependantRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AddHealthDependant(id, r.PartyId, r.DependantType, r.Student, r.StartDate), ct)).ToHttp(Results.Ok))
            .WithName("HealthAddDependant").WithSummary("Add a dependant within the type's age limit").RequirePermission(HealthPermissions.MembershipUpdate)
            .RequireIdempotencyKey();
        health.MapPost("/memberships/{id:guid}/dependants/{dependantId:guid}/remove", async (Guid id, Guid dependantId, DependantRemovalRequest r, IDispatcher d,
                CancellationToken ct) => (await d.Send(new RemoveHealthDependant(id, dependantId, r.LastDay), ct)).ToHttp(Results.Ok))
            .WithName("HealthRemoveDependant").WithSummary("Remove a dependant from a date").RequirePermission(HealthPermissions.MembershipUpdate);
        health.MapPost("/memberships/{id:guid}/plan", async (Guid id, PlanChangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeHealthPlan(id, r.PlanCode, r.OptionCode, r.EffectiveDate, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("HealthChangePlan").WithSummary("Change the plan option from a date").RequirePermission(HealthPermissions.MembershipUpdate);
        foreach (var (route, action) in new[] { ("suspend", MembershipAction.Suspend), ("reinstate", MembershipAction.Reinstate), ("terminate", MembershipAction.Terminate) })
        {
            health.MapPost($"/memberships/{{id:guid}}/{route}", async (Guid id, MembershipStatusRequest r, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeMembershipStatus(id, action, r.EffectiveDate, r.Reason), ct)).ToHttp(Results.Ok))
                .WithName($"HealthMembership{action}").WithSummary($"{action} the membership").RequirePermission(HealthPermissions.MembershipUpdate);
        }

        health.MapPost("/eligibility", async (CheckEligibility r, IDispatcher d, CancellationToken ct) => (await d.Send(r, ct)).ToHttp(Results.Ok))
            .WithName("HealthCheckEligibility").WithSummary("An authorised member-eligibility enquiry; the decision is recorded (HLT-003)")
            .RequirePermission(HealthPermissions.EligibilityCheck);
    }

    private static void MapCare(RouteGroupBuilder health)
    {
        health.MapPost("/preauthorisations", async (RequestAuthorisation r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/preauthorisations/{v.Id}", v)))
            .WithName("HealthRequestAuthorisation").WithSummary("Request a preauthorisation for an eligible member (HLT-005)")
            .RequirePermission(HealthPermissions.PreauthCreate).RequireIdempotencyKey();
        health.MapGet("/preauthorisations/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAuthorisation(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetAuthorisation").WithSummary("Get a preauthorisation").RequirePermission(HealthPermissions.PreauthRead);
        health.MapPost("/preauthorisations/{id:guid}/review", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewAuthorisation(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthReviewAuthorisation").WithSummary("Start clinical review (HLT-008)").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/approve", async (Guid id, AuthorisationDecisionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveAuthorisation(id, r.Lines, r.ValidFrom, r.Conditions), ct)).ToHttp(Results.Ok))
            .WithName("HealthApproveAuthorisation").WithSummary("Approve within the available benefit; the amount is reserved (ADR-012, h)")
            .RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/reject", async (Guid id, AuthorisationRejectionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RejectAuthorisation(id, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("HealthRejectAuthorisation").WithSummary("Reject the authorisation").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/modify", async (Guid id, AuthorisationChangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ModifyAuthorisation(id, r.LineId, r.AuthorisedAmount), ct)).ToHttp(Results.Ok))
            .WithName("HealthModifyAuthorisation").WithSummary("Change an approved line's amount and its reservation").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/conditions-met", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConfirmConditionsMet(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthConfirmConditionsMet").WithSummary("A conditional approval's conditions are met; it becomes Approved").RequirePermission(HealthPermissions.PreauthReview);
        health.MapPost("/preauthorisations/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CancelAuthorisation(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthCancelAuthorisation").WithSummary("Cancel; the reservation is released").RequirePermission(HealthPermissions.PreauthReview);

        health.MapPost("/claims", async (SubmitMedicalClaim r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/claims/{v.Id}", v)))
            .WithName("HealthSubmitClaim").WithSummary("Submit a provider or member claim (HLT-006)").RequirePermission(HealthPermissions.ClaimCreate).RequireIdempotencyKey();
        health.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetClaim").WithSummary("Get a claim").RequirePermission(HealthPermissions.ClaimRead);
        health.MapPost("/claims/{id:guid}/validate", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ValidateMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthValidateClaim").WithSummary("Validate membership, cover and codes (HLT-007)").RequirePermission(HealthPermissions.ClaimUpdate);
        health.MapPost("/claims/{id:guid}/adjudicate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AdjudicateMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthAdjudicateClaim").WithSummary("Adjudicate the lines (ADR-012); safe to repeat after CONCURRENCY_CONFLICT").RequirePermission(HealthPermissions.ClaimUpdate);
        health.MapPost("/claims/{id:guid}/review", async (Guid id, PendReviewRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewPendedClaim(id, r.Reviews), ct)).ToHttp(Results.Ok))
            .WithName("HealthReviewPendedClaim").WithSummary("Accept or reject pended lines, and adjudicate again").RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/approve", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new ApproveMedicalClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthApproveClaim").WithSummary("Approve the adjudicated claim for payment").RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/reject", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RejectMedicalClaim(id, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("HealthRejectClaim").WithSummary("Reject a validated claim").RequirePermission(HealthPermissions.ClaimApprove);
        health.MapPost("/claims/{id:guid}/reverse", async (Guid id, ReasonRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReverseMedicalClaim(id, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("HealthReverseClaim").WithSummary("Reverse a paid claim; its benefit use is given back").RequirePermission(HealthPermissions.ClaimApprove);

        health.MapPost("/utilisation-cases", async (OpenUtilisationCase r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/utilisation-cases/{v.Id}", v)))
            .WithName("HealthOpenUtilisationCase").WithSummary("Open a utilisation case").RequirePermission(HealthPermissions.CaseUpdate).RequireIdempotencyKey();
        health.MapGet("/utilisation-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetUtilisationCase(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetUtilisationCase").WithSummary("Get a utilisation case").RequirePermission(HealthPermissions.CaseRead);
        health.MapPost("/utilisation-cases/{id:guid}/review", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReviewUtilisationCase(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthReviewUtilisationCase").WithSummary("Start reviewing").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapPost("/utilisation-cases/{id:guid}/conclude", async (Guid id, CaseOutcomeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConcludeUtilisationCase(id, r.Outcome), ct)).ToHttp(Results.Ok))
            .WithName("HealthConcludeUtilisationCase").WithSummary("Conclude, by the reviewer").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapGet("/fraud-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetFraudCase(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetFraudCase").WithSummary("Get a fraud case").RequirePermission(HealthPermissions.CaseRead);
        health.MapPost("/fraud-cases/{id:guid}/investigate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new InvestigateFraudCase(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthInvestigateFraudCase").WithSummary("Start investigating").RequirePermission(HealthPermissions.CaseUpdate);
        health.MapPost("/fraud-cases/{id:guid}/conclude", async (Guid id, CaseOutcomeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ConcludeFraudCase(id, r.Outcome), ct)).ToHttp(Results.Ok))
            .WithName("HealthConcludeFraudCase").WithSummary("Conclude, by the investigator").RequirePermission(HealthPermissions.CaseUpdate);
    }

    private static void MapRuns(RouteGroupBuilder health)
    {
        health.MapPost("/payment-runs", async (OpenPaymentRun r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/health/payment-runs/{v.Id}", v)))
            .WithName("HealthOpenRun").WithSummary("Open a provider payment run").RequirePermission(HealthPermissions.RunUpdate).RequireIdempotencyKey();
        health.MapGet("/payment-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPaymentRun(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthGetRun").WithSummary("Get a payment run").RequirePermission(HealthPermissions.RunRead);
        health.MapPost("/payment-runs/{id:guid}/cut-off", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CutOffPaymentRun(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthCutOffRun").WithSummary("Cut off the run").RequirePermission(HealthPermissions.RunUpdate);
        health.MapPost("/payment-runs/{id:guid}/calculate", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new CalculatePaymentRun(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthCalculateRun").WithSummary("Calculate remittances; approval is requested in Workflow (HLT-009)").RequirePermission(HealthPermissions.RunUpdate);
        health.MapPost("/payment-runs/{id:guid}/release", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReleasePaymentRun(id), ct)).ToHttp(Results.Ok))
            .WithName("HealthReleaseRun").WithSummary("Release the approved run to Payments").RequirePermission(HealthPermissions.RunUpdate);
    }
}
