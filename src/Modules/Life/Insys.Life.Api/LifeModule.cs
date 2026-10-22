using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Life.Api.StaticData;
using Insys.Life.Application;
using Insys.Life.Contracts;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;
using Insys.Life.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Life.Api;

public sealed record EvidenceRequest(string EvidenceType, bool Mandatory);

public sealed record EvidenceReceivedRequest(Guid DocumentId);

public sealed record WaiverRequest(string Reason);

public sealed record CaseReferralRequest(string ToRole);

public sealed record CaseDecisionRequest(UnderwritingOutcome Outcome, string DecisionCode, string Reason, IReadOnlyList<DecisionItemRequest>? Items);

public sealed record BeneficiariesRequest(DateOnly EffectiveDate, IReadOnlyList<BeneficiaryNomination> Nominations);

public sealed record AlterationRequest(string AlterationCode, DateOnly EffectiveDate, string? IntermediaryCode);

public sealed record ReinstatementRequest(Guid? EvidenceRef);

public sealed record LoanRequest(decimal Principal, string RateCode);

public sealed record RepaymentRequest(decimal Amount, Guid? PaymentRef);

public sealed record AssessmentRequest(IReadOnlyList<BenefitLineRequest> Lines);

public sealed record ClaimApprovalRequest(IReadOnlyList<PayeeInput> Payees, string ReserveType, string DecisionReason, string Rationale);

public sealed record ClaimDeclineRequest(string DecisionReason, string Rationale);

public sealed record MemberRequest(Guid PartyId, string CategoryCode, decimal? AnnualSalary, DateOnly JoinedOn, JsonElement? Facts);

public sealed record MemberChangeRequest(string CategoryCode, decimal? AnnualSalary, JsonElement? Facts);

public sealed record MemberLeaveRequest(DateOnly LeftOn);

public sealed record SchemeTerminationRequest(DateOnly On);

/// <summary>
/// The Life module (§9): proposals and quotation, underwriting cases, policies with their benefits, beneficiaries and
/// premium schedule, lapse and reinstatement, policy loans, claims and group schemes (LIF-001 to LIF-008).
/// TODO(spec): surrender, paid-up and maturity values, loan interest and loaded premiums wait for OQ-052; automatic
/// underwriting outcomes wait for OQ-051.
/// </summary>
public sealed class LifeModule : IModule
{
    public string Name => "Life";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddLifeInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        LifeStaticDataEndpoints.MapStaticData(api);
        var life = api.MapGroup("/life").WithTags(Name);
        MapProposals(life);
        MapPolicies(life);
        MapClaims(life);
        MapSchemes(life);
    }

    private static void MapProposals(RouteGroupBuilder life)
    {
        life.MapPost("/proposals", async (CaptureProposal r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/life/proposals/{v.Id}", v)))
            .WithName("LifeCaptureProposal").WithSummary("Capture a proposal on a Life product version on sale (LIF-001)")
            .RequirePermission(LifePermissions.ProposalCreate).RequireIdempotencyKey();
        life.MapGet("/proposals/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetLifeProposal(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetProposal").WithSummary("Get a proposal").RequirePermission(LifePermissions.ProposalRead);
        life.MapPost("/proposals/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new SubmitLifeProposal(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeSubmitProposal").WithSummary("Submit a quoted, eligible proposal; its content is frozen from then (LIF-001)")
            .RequirePermission(LifePermissions.ProposalUpdate);
        life.MapPost("/proposals/{id:guid}/withdraw", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new WithdrawLifeProposal(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeWithdrawProposal").WithSummary("Withdraw a quoted or submitted proposal").RequirePermission(LifePermissions.ProposalUpdate);
        life.MapGet("/proposals/{id:guid}/underwriting-case", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetProposalCase(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetProposalCase").WithSummary("Get the proposal's underwriting case").RequirePermission(LifePermissions.UnderwritingRead);

        life.MapGet("/underwriting-cases/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetUnderwritingCase(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetUnderwritingCase").WithSummary("Get an underwriting case").RequirePermission(LifePermissions.UnderwritingRead);
        life.MapPost("/underwriting-cases/{id:guid}/evidence", async (Guid id, EvidenceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RequestEvidence(id, r.EvidenceType, r.Mandatory), ct)).ToHttp(Results.Ok))
            .WithName("LifeRequestEvidence").WithSummary("Request evidence; the case waits for it").RequirePermission(LifePermissions.UnderwritingUpdate);
        life.MapPost("/underwriting-cases/{id:guid}/evidence/{requirementId:guid}/received", async (Guid id, Guid requirementId, EvidenceReceivedRequest r, IDispatcher d,
                CancellationToken ct) => (await d.Send(new RecordEvidence(id, requirementId, r.DocumentId), ct)).ToHttp(Results.Ok))
            .WithName("LifeRecordEvidence").WithSummary("Record evidence received").RequirePermission(LifePermissions.UnderwritingUpdate);
        life.MapPost("/underwriting-cases/{id:guid}/evidence/{requirementId:guid}/waive", async (Guid id, Guid requirementId, WaiverRequest r, IDispatcher d,
                CancellationToken ct) => (await d.Send(new WaiveEvidence(id, requirementId, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("LifeWaiveEvidence").WithSummary("Waive evidence, by someone with that authority").RequirePermission(LifePermissions.UnderwritingWaive);
        foreach (var (route, action) in new[] { ("start-assessment", CaseAction.StartAssessment), ("answer-referral", CaseAction.AnswerReferral) })
        {
            life.MapPost($"/underwriting-cases/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new ChangeCase(id, action), ct)).ToHttp(Results.Ok))
                .WithName($"LifeCase{action}").WithSummary(action == CaseAction.StartAssessment ? "Start assessment when no evidence is needed" : "Return a referred case to assessment")
                .RequirePermission(LifePermissions.UnderwritingUpdate);
        }

        life.MapPost("/underwriting-cases/{id:guid}/refer", async (Guid id, CaseReferralRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReferUnderwritingCase(id, r.ToRole), ct)).ToHttp(Results.Ok))
            .WithName("LifeReferCase").WithSummary("Refer the case").RequirePermission(LifePermissions.UnderwritingUpdate);
        life.MapPost("/underwriting-cases/{id:guid}/decision", async (Guid id, CaseDecisionRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DecideUnderwritingCase(id, r.Outcome, r.DecisionCode, r.Reason, r.Items), ct)).ToHttp(Results.Ok))
            .WithName("LifeDecideCase").WithSummary("Propose the decision and terms; a second person approves them in Workflow within authority (SRS-002)")
            .RequirePermission(LifePermissions.UnderwritingDecide);
    }

    private static void MapPolicies(RouteGroupBuilder life)
    {
        life.MapPost("/policies", async (IssueLifePolicy r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/life/policies/{v.Id}", v)))
            .WithName("LifeIssuePolicy").WithSummary("Issue a policy from an accepted proposal, on its product and rating versions (LIF-003)")
            .RequirePermission(LifePermissions.PolicyCreate).RequireIdempotencyKey();
        life.MapGet("/policies/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetLifePolicy(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetPolicy").WithSummary("Get a policy").RequirePermission(LifePermissions.PolicyRead);
        life.MapPost("/policies/{id:guid}/beneficiaries", async (Guid id, BeneficiariesRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeBeneficiaries(id, r.EffectiveDate, r.Nominations), ct)).ToHttp(Results.Ok))
            .WithName("LifeChangeBeneficiaries").WithSummary("Change beneficiaries from a date; shares total 100%").RequirePermission(LifePermissions.PolicyUpdate);
        life.MapPost("/policies/{id:guid}/alterations", async (Guid id, AlterationRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AlterLifePolicy(id, r.AlterationCode, r.EffectiveDate, r.IntermediaryCode), ct)).ToHttp(Results.Ok))
            .WithName("LifeAlterPolicy").WithSummary("Alter the policy, checked against its state and the alteration type (LIF-004)").RequirePermission(LifePermissions.PolicyUpdate);
        life.MapPost("/policies/{id:guid}/reinstate", async (Guid id, ReinstatementRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ReinstateLifePolicy(id, r.EvidenceRef), ct)).ToHttp(Results.Ok))
            .WithName("LifeReinstatePolicy").WithSummary("Reinstate a lapsed policy within the product's window, with evidence where required (LIF-005)")
            .RequirePermission(LifePermissions.PolicyUpdate);
        foreach (var (route, action) in new[] { ("surrender", PolicyValueAction.Surrender), ("paid-up", PolicyValueAction.MakePaidUp), ("mature", PolicyValueAction.Mature) })
        {
            life.MapPost($"/policies/{{id:guid}}/{route}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                    (await d.Send(new RequestPolicyValue(id, action), ct)).ToHttp(Results.Ok))
                .WithName($"LifePolicy{action}").WithSummary($"{action}: refused until actuarial defines the value calculation (OQ-052)")
                .RequirePermission(LifePermissions.PolicyUpdate);
        }

        life.MapPost("/policies/{id:guid}/loans", async (Guid id, LoanRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new IssuePolicyLoan(id, r.Principal, r.RateCode), ct)).ToHttp(v => Results.Created($"/api/v1/life/loans/{v.Id}", v)))
            .WithName("LifeIssueLoan").WithSummary("Issue a policy loan within the product's share of the cash value (LIF-007)")
            .RequirePermission(LifePermissions.LoanCreate).RequireIdempotencyKey();
        life.MapGet("/loans/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPolicyLoan(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetLoan").WithSummary("Get a policy loan").RequirePermission(LifePermissions.LoanRead);
        life.MapPost("/loans/{id:guid}/repayments", async (Guid id, RepaymentRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RepayPolicyLoan(id, r.Amount, r.PaymentRef), ct)).ToHttp(Results.Ok))
            .WithName("LifeRepayLoan").WithSummary("Record a loan repayment").RequirePermission(LifePermissions.LoanUpdate).RequireIdempotencyKey();
    }

    private static void MapClaims(RouteGroupBuilder life)
    {
        life.MapPost("/claims", async (NotifyLifeClaim r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/life/claims/{v.Id}", v)))
            .WithName("LifeNotifyClaim").WithSummary("Notify a claim on an insured life of a policy").RequirePermission(LifePermissions.ClaimCreate).RequireIdempotencyKey();
        life.MapGet("/claims/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetLifeClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetClaim").WithSummary("Get a claim").RequirePermission(LifePermissions.ClaimRead);
        life.MapPost("/claims/{id:guid}/register", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RegisterLifeClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeRegisterClaim").WithSummary("Register the claim: policy status, insured life, benefit, exclusions and claimant are checked (LIF-008)")
            .RequirePermission(LifePermissions.ClaimUpdate);
        life.MapPost("/claims/{id:guid}/assessment", async (Guid id, AssessmentRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AssessLifeClaim(id, r.Lines), ct)).ToHttp(Results.Ok))
            .WithName("LifeAssessClaim").WithSummary("Assess the benefits claimed").RequirePermission(LifePermissions.ClaimUpdate);
        life.MapPost("/claims/{id:guid}/approval", async (Guid id, ClaimApprovalRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ApproveLifeClaim(id, r.Payees, r.ReserveType, r.DecisionReason, r.Rationale), ct)).ToHttp(Results.Ok))
            .WithName("LifeApproveClaim").WithSummary("Propose approval and payees; a second person approves in Workflow within authority (SRS-002, CLM-003)")
            .RequirePermission(LifePermissions.ClaimDecide);
        life.MapPost("/claims/{id:guid}/decline", async (Guid id, ClaimDeclineRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeclineLifeClaim(id, r.DecisionReason, r.Rationale), ct)).ToHttp(Results.Ok))
            .WithName("LifeDeclineClaim").WithSummary("Decline the claim with its reason and rationale (CLM-003)").RequirePermission(LifePermissions.ClaimDecide);
        life.MapPost("/claims/{id:guid}/close", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new CloseLifeClaim(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeCloseClaim").WithSummary("Close a settled or declined claim").RequirePermission(LifePermissions.ClaimUpdate);
    }

    private static void MapSchemes(RouteGroupBuilder life)
    {
        life.MapPost("/group-schemes", async (SetUpGroupScheme r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(v => Results.Created($"/api/v1/life/group-schemes/{v.Id}", v)))
            .WithName("LifeSetUpScheme").WithSummary("Set up a group scheme on a Life product").RequirePermission(LifePermissions.SchemeCreate).RequireIdempotencyKey();
        life.MapGet("/group-schemes/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetGroupScheme(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeGetScheme").WithSummary("Get a group scheme").RequirePermission(LifePermissions.SchemeRead);
        life.MapPost("/group-schemes/{id:guid}/members", async (Guid id, MemberRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new EnrolGroupMember(id, r.PartyId, r.CategoryCode, r.AnnualSalary, r.JoinedOn, r.Facts), ct)).ToHttp(Results.Ok))
            .WithName("LifeEnrolMember").WithSummary("Enrol a member in a category; cover above the free cover limit needs evidence")
            .RequirePermission(LifePermissions.SchemeUpdate).RequireIdempotencyKey();
        life.MapPut("/group-schemes/{id:guid}/members/{memberId:guid}", async (Guid id, Guid memberId, MemberChangeRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeGroupMember(id, memberId, r.CategoryCode, r.AnnualSalary, r.Facts), ct)).ToHttp(Results.Ok))
            .WithName("LifeChangeMember").WithSummary("Change a member's category or salary").RequirePermission(LifePermissions.SchemeUpdate);
        life.MapPost("/group-schemes/{id:guid}/members/{memberId:guid}/evidence-accepted", async (Guid id, Guid memberId, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AcceptMemberEvidence(id, memberId), ct)).ToHttp(Results.Ok))
            .WithName("LifeAcceptMemberEvidence").WithSummary("Accept a member's underwriting evidence").RequirePermission(LifePermissions.UnderwritingDecide);
        life.MapPost("/group-schemes/{id:guid}/members/{memberId:guid}/leave", async (Guid id, Guid memberId, MemberLeaveRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RemoveGroupMember(id, memberId, r.LeftOn), ct)).ToHttp(Results.Ok))
            .WithName("LifeRemoveMember").WithSummary("Remove a member from a date").RequirePermission(LifePermissions.SchemeUpdate);
        life.MapPost("/group-schemes/{id:guid}/renew", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RenewGroupScheme(id), ct)).ToHttp(Results.Ok))
            .WithName("LifeRenewScheme").WithSummary("Renew the scheme").RequirePermission(LifePermissions.SchemeUpdate);
        life.MapPost("/group-schemes/{id:guid}/terminate", async (Guid id, SchemeTerminationRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new TerminateGroupScheme(id, r.On), ct)).ToHttp(Results.Ok))
            .WithName("LifeTerminateScheme").WithSummary("Terminate the scheme; its members leave").RequirePermission(LifePermissions.SchemeUpdate);
    }
}
