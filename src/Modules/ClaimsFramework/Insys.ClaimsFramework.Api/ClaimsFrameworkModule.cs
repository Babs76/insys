/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

/// <param name="Reason">Why the reserve moves; required (OQ-079: an assessor adjusts a claim's reserve with a reason).</param>
public sealed record ReserveRequest(ReserveAction Action, string ReserveType, decimal Amount, string Currency, string Reason);

public sealed record DraftInstructionRequest(string InstructionRef, Guid PayeePartyId, Guid BankAccountId, decimal Amount, decimal ApprovedAmount, string Currency,
    string ReserveType);

public sealed record PayeeRequest(Guid PayeePartyId, Guid BankAccountId);

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
        AuditTargetTypes.Register(nameof(Domain.Claims.ClaimFinancials)); // an aggregate kept as movements, not an IAggregateRoot
        ClaimsFrameworkStaticDataEndpoints.MapStaticData(api);
        var claims = api.MapGroup("/claims").WithTags(Name);

        claims.MapPost("/claims", async (AllocateClaimNumber r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/claims/claims/{view.ClaimNumber}", view)))
            .Produces<ClaimView>(StatusCodes.Status201Created)
            .WithName("ClaimsAllocateNumber")
            .WithSummary("Register a domain claim and allocate its claim number; a repeat returns the same number (CLM-005)")
            .RequirePermission(ClaimsPermissions.ClaimCreate);

        claims.MapGet("/claims/{claimNumber}", async (string claimNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetClaim(claimNumber), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("ClaimsGet")
            .WithSummary("Get a claim's registration, evidence and fraud indicator hits")
            .RequirePermission(ClaimsPermissions.ClaimRead);

        claims.MapPost("/claims/{claimNumber}/evidence-requests", async (string claimNumber, EvidenceRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RequestEvidence(claimNumber, r.EvidenceType), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("ClaimsRequestEvidence")
            .WithSummary("Request evidence of a type")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/evidence", async (string claimNumber, EvidenceRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                r.DocumentId is { } document
                    ? (await dispatcher.Send(new RecordEvidence(claimNumber, r.EvidenceType, document), ct)).ToHttp(Results.Ok)
                    : Results.Problem(statusCode: 400, title: "documentId is required."))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("ClaimsRecordEvidence")
            .WithSummary("Record evidence received, as a stored document")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/communications", async (string claimNumber, RecordCommunication r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(r with { ClaimNumber = claimNumber }, ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("ClaimsRecordCommunication")
            .WithSummary("Record a communication on a claim")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapPost("/claims/{claimNumber}/fraud-evaluations", async (string claimNumber, JsonElement facts, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new EvaluateFraudIndicators(claimNumber, facts), ct)).ToHttp(Results.Ok))
            .Produces<ClaimView>(StatusCodes.Status200OK)
            .WithName("ClaimsEvaluateFraud")
            .WithSummary("Evaluate the fraud indicators in force against the claim's facts")
            .RequirePermission(ClaimsPermissions.ClaimUpdate);

        claims.MapGet("/claims/{claimNumber}/financials", async (string claimNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetFinancials(claimNumber), ct)).ToHttp(Results.Ok))
            .Produces<FinancialsView>(StatusCodes.Status200OK)
            .WithName("ClaimsGetFinancials")
            .WithSummary("Get a claim's financial movements and outstanding reserves (CLM-004)")
            .RequirePermission(ClaimsPermissions.FinancialsRead);

        claims.MapPost("/claims/{claimNumber}/reserves", async (string claimNumber, ReserveRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new MoveReserve(claimNumber, r.Action, r.ReserveType, r.Amount, r.Currency, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<FinancialsView>(StatusCodes.Status200OK)
            .WithName("ClaimsMoveReserve")
            .WithSummary("Set or change a reserve, or record a recovery, with a reason; a reserve never goes below zero")
            .RequirePermission(ClaimsPermissions.FinancialsUpdate);

        claims.MapPost("/claims/{claimNumber}/payment-instructions", async (string claimNumber, DraftInstructionRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new DraftInstruction(claimNumber, r.InstructionRef, r.PayeePartyId, r.BankAccountId, r.Amount, r.ApprovedAmount, r.Currency, r.ReserveType), ct))
                .ToHttp(view => Results.Created($"/api/v1/claims/payment-instructions/{view.Id}", view)))
            .Produces<InstructionView>(StatusCodes.Status201Created)
            .WithName("ClaimsDraftInstruction")
            .WithSummary("Draft a claim payment within the approved amount; a repeat reference returns the same instruction (PAY-001)")
            .RequirePermission(ClaimsPermissions.InstructionCreate);

        claims.MapGet("/claims/{claimNumber}/payment-instructions", async (string claimNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListInstructions(claimNumber), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<InstructionView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ClaimsListInstructions")
            .WithSummary("A claim's payment instructions, oldest first, each with its payment once created (GAP-036)")
            .RequirePermission(ClaimsPermissions.InstructionRead);

        claims.MapGet("/payment-instructions/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetInstruction(id), ct)).ToHttp(Results.Ok))
            .Produces<InstructionView>(StatusCodes.Status200OK)
            .WithName("ClaimsGetInstruction")
            .WithSummary("Get a claim payment instruction")
            .RequirePermission(ClaimsPermissions.InstructionRead);

        claims.MapPost("/payment-instructions/{id:guid}/submit", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ChangeInstruction(id, InstructionAction.Submit), ct)).ToHttp(Results.Ok))
            .Produces<InstructionView>(StatusCodes.Status200OK)
            .WithName("ClaimsSubmitInstruction")
            .WithSummary("Submit for a second person's approval in Workflow (SRS-002)")
            .RequirePermission(ClaimsPermissions.InstructionCreate);

        claims.MapPost("/payment-instructions/{id:guid}/release", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ChangeInstruction(id, InstructionAction.Release), ct)).ToHttp(Results.Ok))
            .Produces<InstructionView>(StatusCodes.Status200OK)
            .WithName("ClaimsReleaseInstruction")
            .WithSummary("Release an approved instruction to Payments, within the outstanding reserve")
            .RequirePermission(ClaimsPermissions.InstructionRelease);

        claims.MapPost("/payment-instructions/{id:guid}/retry", async (Guid id, PayeeRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryInstruction(id, r.PayeePartyId, r.BankAccountId), ct)).ToHttp(Results.Ok))
            .Produces<InstructionView>(StatusCodes.Status200OK)
            .WithName("ClaimsRetryInstruction")
            .WithSummary("Release a failed instruction again, as a new payment; new payee or bank details are approved by a second person first (SRS-002)")
            .RequirePermission(ClaimsPermissions.InstructionRelease);

        claims.MapPost("/payment-instructions/{id:guid}/redirect", async (Guid id, PayeeRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RedirectInstruction(id, r.PayeePartyId, r.BankAccountId), ct)).ToHttp(Results.Ok))
            .Produces<InstructionView>(StatusCodes.Status200OK)
            .WithName("ClaimsRedirectInstruction")
            .WithSummary("Redirect an approved instruction to another payee or bank account; a second person approves it before release (SRS-002)")
            .RequirePermission(ClaimsPermissions.InstructionRelease);
    }
}
