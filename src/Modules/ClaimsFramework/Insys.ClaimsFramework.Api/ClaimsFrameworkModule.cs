using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.ClaimsFramework.Api.StaticData;
using Insys.ClaimsFramework.Application;
using Insys.ClaimsFramework.Contracts;
using Insys.ClaimsFramework.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ClaimsFramework.Api;

public sealed record EvidenceRequest(string EvidenceType, Guid? DocumentId);

public sealed record ReserveRequest(ReserveAction Action, string ReserveType, decimal Amount, string Currency);

public sealed record DraftInstructionRequest(string InstructionRef, Guid PayeePartyId, decimal Amount, decimal ApprovedAmount, string Currency, string ReserveType);

public sealed record RetryInstructionRequest(Guid PayeePartyId);

/// <summary>
/// The ClaimsFramework module: what the domain claims share (CLM-001): claim numbers (CLM-005), evidence,
/// communications, fraud indicators, reserves and recoveries as financial movements (CLM-004), and claim payment
/// instructions released only after maker-checker approval (SRS-002). The domains own adjudication (CLM-002).
/// </summary>
public sealed class ClaimsFrameworkModule : IModule
{
    public string Name => "ClaimsFramework";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddClaimsFrameworkInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ClaimsFrameworkStaticDataEndpoints.MapStaticData(api);
        var claims = api.MapGroup("/claims").WithTags(Name);

        claims.MapPost("/claims", async (AllocateClaimNumber r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/claims/claims/{view.ClaimNumber}", view)))
            .WithName("ClaimsAllocateNumber")
            .WithSummary("Register a domain claim and allocate its claim number; a repeat returns the same number (CLM-005)")
            .RequirePermission(ClaimsPermissions.ClaimCreate);

        claims.MapGet("/claims/{claimNumber}", async (string claimNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetClaim(claimNumber), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsGet")
            .WithSummary("Get a claim's registration, evidence and fraud indicator hits")
            .RequirePermission(ClaimsPermissions.ClaimRead);

        claims.MapPost("/claims/{claimNumber}/evidence-requests", async (string claimNumber, EvidenceRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RequestEvidence(claimNumber, r.EvidenceType), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsRequestEvidence")
            .WithSummary("Request evidence of a type")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/evidence", async (string claimNumber, EvidenceRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                r.DocumentId is { } document
                    ? (await dispatcher.Send(new RecordEvidence(claimNumber, r.EvidenceType, document), ct)).ToHttp(Results.Ok)
                    : Results.Problem(statusCode: 400, title: "documentId is required."))
            .WithName("ClaimsRecordEvidence")
            .WithSummary("Record evidence received, as a stored document")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/communications", async (string claimNumber, RecordCommunication r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(r with { ClaimNumber = claimNumber }, ct)).ToHttp(Results.Ok))
            .WithName("ClaimsRecordCommunication")
            .WithSummary("Record a communication on a claim")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/fraud-evaluations", async (string claimNumber, JsonElement facts, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new EvaluateFraudIndicators(claimNumber, facts), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsEvaluateFraud")
            .WithSummary("Evaluate the fraud indicators in force against the claim's facts")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapGet("/claims/{claimNumber}/financials", async (string claimNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetFinancials(claimNumber), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsGetFinancials")
            .WithSummary("Get a claim's financial movements and outstanding reserves (CLM-004)")
            .RequirePermission(ClaimsPermissions.FinancialsRead);

        claims.MapPost("/claims/{claimNumber}/reserves", async (string claimNumber, ReserveRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new MoveReserve(claimNumber, r.Action, r.ReserveType, r.Amount, r.Currency), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsMoveReserve")
            .WithSummary("Set or change a reserve, or record a recovery; a reserve never goes below zero")
            .RequirePermission(ClaimsPermissions.FinancialsUpdate);

        claims.MapPost("/claims/{claimNumber}/payment-instructions", async (string claimNumber, DraftInstructionRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new DraftInstruction(claimNumber, r.InstructionRef, r.PayeePartyId, r.Amount, r.ApprovedAmount, r.Currency, r.ReserveType), ct))
                .ToHttp(view => Results.Created($"/api/v1/claims/payment-instructions/{view.Id}", view)))
            .WithName("ClaimsDraftInstruction")
            .WithSummary("Draft a claim payment within the approved amount; a repeat reference returns the same instruction (PAY-001)")
            .RequirePermission(ClaimsPermissions.InstructionCreate);

        claims.MapGet("/payment-instructions/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetInstruction(id), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsGetInstruction")
            .WithSummary("Get a claim payment instruction")
            .RequirePermission(ClaimsPermissions.InstructionRead);

        claims.MapPost("/payment-instructions/{id:guid}/submit", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ChangeInstruction(id, InstructionAction.Submit), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsSubmitInstruction")
            .WithSummary("Submit for a second person's approval in Workflow (SRS-002)")
            .RequirePermission(ClaimsPermissions.InstructionCreate);

        claims.MapPost("/payment-instructions/{id:guid}/release", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ChangeInstruction(id, InstructionAction.Release), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsReleaseInstruction")
            .WithSummary("Release an approved instruction to Payments, within the outstanding reserve")
            .RequirePermission(ClaimsPermissions.InstructionRelease);

        claims.MapPost("/payment-instructions/{id:guid}/retry", async (Guid id, RetryInstructionRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryInstruction(id, r.PayeePartyId), ct)).ToHttp(Results.Ok))
            .WithName("ClaimsRetryInstruction")
            .WithSummary("Release a failed instruction again to corrected payee details, as a new payment")
            .RequirePermission(ClaimsPermissions.InstructionRelease);
    }
}
