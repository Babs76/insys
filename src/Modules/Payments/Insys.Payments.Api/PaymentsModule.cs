/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.Payments.Api.StaticData;
using Insys.Payments.Application;
using Insys.Payments.Contracts;
using Insys.Payments.Domain.Payments;
using Insys.Payments.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Payments.Api;

/// <param name="BankAccountId">For a payout: the payee's verified bank account (party.bank_account); account numbers are never sent here.</param>
public sealed record InitiatePaymentRequest(Direction Direction, string Purpose, string MethodCode, decimal Amount, string Currency, Guid? CounterpartyPartyId,
    string? BusinessRefType, Guid? BusinessRefId, Guid? BankAccountId);

public sealed record ResolveUnknownRequest(bool Succeeded, string Evidence);

public sealed record ReversePaymentRequest(string Reason);

public sealed record BuildBatchRequest(
    [property: BuildingBlocks.Domain.OneOf(PaymentPurposes.Refund, PaymentPurposes.ClaimPayout, PaymentPurposes.Commission)] string Purpose, string Currency);

public sealed record ImportStatementRequest(string AccountRef, StatementLine[] Lines);

/// <param name="ExternalRef">The provider's reference for the payment.</param>
/// <param name="Status">The provider's own status code; mapped by payment.ref_payment_status_mappings.</param>
public sealed record ProviderCallbackRequest(string ExternalRef, string Status);

public sealed record IssueCallbackEndpointRequest(string ProviderCode, bool RequireClientToken = false);

/// <summary>
/// The Payments module: payments with idempotency (PAY-001), traceable status changes (PAY-002), provider callbacks,
/// payout batches, bank statement reconciliation (PAY-003), and unknown outcomes that go to a person, never to success
/// (PAY-005).
/// </summary>
public sealed class PaymentsModule : IModule
{
    public string Name => "Payments";

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPaymentsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        PaymentsStaticDataEndpoints.MapStaticData(api);
        var payments = api.MapGroup("/payments").WithTags(Name);

        payments.MapPost("/payments", async (InitiatePaymentRequest r, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new InitiatePayment($"API:{http.Request.Headers["Idempotency-Key"]}", r.Direction, r.Purpose, r.MethodCode, r.Amount, r.Currency,
                    r.CounterpartyPartyId, r.BusinessRefType, r.BusinessRefId, r.BankAccountId), ct)).ToHttp(view => Results.Created($"/api/v1/payments/payments/{view.Id}", view)))
            .Produces<PaymentView>(StatusCodes.Status201Created)
            .WithName("PaymentsInitiate")
            .WithSummary("Start a payment; its Idempotency-Key is also the payment's own key, so it never moves money twice (PAY-001)")
            .WithDescription("An inbound payment is sent to its provider at once; an outbound one waits for a payout batch.")
            .RequirePermission(PaymentPermissions.PaymentCreate)
            .RequireIdempotencyKey();

        payments.MapGet("/payments/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetPayment(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentView>(StatusCodes.Status200OK)
            .WithName("PaymentsGet")
            .WithSummary("Get a payment with its status history (PAY-002) and allocations")
            .RequirePermission(PaymentPermissions.PaymentRead)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "a commission payout is found only by a caller who also holds it");

        payments.MapGet("/payments", async (string businessRefType, Guid businessRefId, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListPayments(businessRefType, businessRefId, afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<PaymentView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<PaymentView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PaymentsList")
            .WithSummary("The payments for a business record, oldest first (GAP-036)")
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "commission payouts are listed only to a caller who also holds it")
            .WithDescription("A refund (REFUND_REQUEST), claim payment instruction (CLAIM_PAYMENT), invoice (INVOICE), remittance or commission statement. "
                + "A payment is listed from when it is created, before it completes.")
            .WithKnownValues("businessRefType", PaymentReferenceTypes.Known, "The business record types INSYS itself pays or collects for (GAP-037).")
            .RequirePermission(PaymentPermissions.PaymentRead);

        payments.MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new PaymentsSummaryQuery(), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("PaymentsSummary")
            .WithSummary("Dashboard figure PAYMENTS_UNKNOWN (GAP-046): payments waiting for a decision on an unknown outcome, per currency, and the oldest")
            .RequirePermission(PaymentPermissions.PaymentRead)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "commission payouts are counted only for a caller who also holds it");

        payments.MapPost("/payments/{id:guid}/resolve", async (Guid id, ResolveUnknownRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ResolveUnknown(id, r.Succeeded, r.Evidence), ct)).ToHttp(Results.Ok))
            .Produces<PaymentView>(StatusCodes.Status200OK)
            .WithName("PaymentsResolveUnknown")
            .WithSummary("Decide an unknown outcome from the evidence (PAY-005)")
            .RequirePermission(PaymentPermissions.PaymentResolve);

        payments.MapPost("/payments/{id:guid}/reverse", async (Guid id, ReversePaymentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReversePayment(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<PaymentView>(StatusCodes.Status200OK)
            .WithName("PaymentsReverse")
            .WithSummary("Reverse a succeeded payment and its allocations")
            .RequirePermission(PaymentPermissions.PaymentResolve);

        payments.MapPost("/payments/{id:guid}/allocate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AllocatePayment(id), ct)).ToHttp(Results.Ok))
            .Produces<PaymentView>(StatusCodes.Status200OK)
            .WithName("PaymentsAllocate")
            .WithSummary("Allocate what is left of a received payment to its invoices (BIL-003); done automatically when it is received")
            .RequirePermission(PaymentPermissions.PaymentUpdate);

        MapCallbackEndpoints(payments);

        payments.MapPost("/payout-batches", async (BuildBatchRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new BuildBatch(r.Purpose, r.Currency), ct)).ToHttp(view => Results.Created($"/api/v1/payments/payout-batches/{view.Id}", view)))
            .Produces<BatchView>(StatusCodes.Status201Created)
            .WithName("PaymentsBuildBatch")
            .WithSummary("Batch the payouts waiting for a purpose and currency; a second person approves the batch in Workflow (SRS-002)")
            .RequirePermission(PaymentPermissions.PayoutCreate)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "batching commission payouts: without it they are outside the caller's scope and the batch is refused (OUT_OF_SCOPE)")
            .RequireIdempotencyKey()
            .ChannelScoped() // F005: payments out
            .HasApprovalFlow();

        payments.MapGet("/payout-batches/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetBatch(id), ct)).ToHttp(Results.Ok))
            .Produces<BatchView>(StatusCodes.Status200OK)
            .WithName("PaymentsGetBatch")
            .WithSummary("Get a payout batch")
            .RequirePermission(PaymentPermissions.PayoutRead)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "a commission batch is found, and its payouts counted, only for a caller who also holds it");

        payments.MapGet("/payout-batches/{id:guid}/payments", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListBatchPayments(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PaymentView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("PaymentsListBatchPayments")
            .WithSummary("The payments in a payout batch (GAP-036)")
            .RequirePermission(PaymentPermissions.PayoutRead)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "a commission batch's payouts are listed only to a caller who also holds it");

        payments.MapPost("/payout-batches/{id:guid}/release", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReleaseBatch(id), ct)).ToHttp(Results.Ok))
            .Produces<BatchView>(StatusCodes.Status200OK)
            .WithName("PaymentsReleaseBatch")
            .WithSummary("Release an approved batch: each payment is sent to its provider")
            .RequirePermission(PaymentPermissions.PayoutRelease)
            .AlsoChecks(PaymentPermissions.CommissionPayoutRead, "releasing a commission batch: without it the batch is not found")
            .ChannelScoped(); // F005: payments out

        payments.MapPost("/bank-statements", async (ImportStatementRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ImportStatement(r.AccountRef, r.Lines), ct)).ToHttp(Results.Ok))
            .Produces<StatementResult>(StatusCodes.Status200OK)
            .WithName("PaymentsImportStatement")
            .WithSummary("Import bank statement lines and match them to payments (PAY-003); unmatched lines become reconciliation exceptions")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate)
            .RequireIdempotencyKey();

        payments.MapGet("/reconciliation-exceptions", async (string? status, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListExceptions(status, Cursor.ClampLimit(limit)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ExceptionView>>(StatusCodes.Status200OK)
            .WithName("PaymentsListExceptions")
            .WithSummary("Unmatched and ambiguous items waiting for a person (PAY-005)")
            .RequirePermission(PaymentPermissions.ReconciliationRead);

        payments.MapPost("/reconciliation-exceptions/{id:guid}/assign", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AssignException(id), ct)).ToHttp(Results.Ok))
            .Produces<ExceptionView>(StatusCodes.Status200OK)
            .WithName("PaymentsAssignException")
            .WithSummary("Take an exception to investigate")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate);

        payments.MapPost("/reconciliation-exceptions/{id:guid}/resolve", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ResolveException(id), ct)).ToHttp(Results.Ok))
            .Produces<ExceptionView>(StatusCodes.Status200OK)
            .WithName("PaymentsResolveException")
            .WithSummary("Resolve an exception you are investigating; an unknown payment is resolved first")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate);
    }

    /// <summary>ADR-023: provider callback addresses per tenant and provider, and the address providers call.</summary>
    private static void MapCallbackEndpoints(RouteGroupBuilder payments)
    {
        payments.MapPost("/callbacks/{endpointKey}", async (string endpointKey, HttpContext http, ICallbackEndpointResolver resolver, IServiceScopeFactory scopes,
                IAuthorizationService authorization, CancellationToken ct) =>
            {
                using var reader = new StreamReader(http.Request.Body);
                var body = await reader.ReadToEndAsync(ct);

                // One answer for an unknown key, a revoked endpoint, a bad signature or an old timestamp, so none can be told apart.
                var refused = Problems.For(PaymentErrorCodes.CallbackSignatureInvalid, "The callback is not authenticated.", StatusCodes.Status401Unauthorized);
                if (!CallbackKeys.Plausible(endpointKey) || await resolver.Resolve(CallbackKeys.Hash(endpointKey), ct) is not { } found)
                {
                    return refused;
                }

                // Optional extra (ADR-023 decision 4): a client-credentials token for this tenant holding payment_callback.create.
                var clientToken = http.User.Identity?.IsAuthenticated == true
                                  && http.User.FindFirst("tenant_id")?.Value == found.TenantId.ToString()
                                  && (await authorization.AuthorizeAsync(http.User, PermissionPolicyProvider.Prefix + PaymentPermissions.CallbackCreate)).Succeeded;

                // The tenant comes only from the key, and the handler accepts nothing without the signature keyed to that tenant and provider.
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.Context.ExecutionScope>()
                    .InitialiseForService(found.TenantId, "payment-callback");
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReceiveCallback(found.EndpointId, body,
                    http.Request.Headers["X-Callback-Timestamp"], http.Request.Headers["X-Callback-Id"], http.Request.Headers["X-Signature"], clientToken), ct);
                if (result.IsFailure)
                {
                    return Problems.For(result.Error);
                }

                return result.Value.Result switch
                {
                    CallbackResult.Applied => Results.Ok(result.Value.Payment),
                    CallbackResult.Replayed => Problems.For(PaymentErrorCodes.CallbackReplayed, "This callback ID was already received.", StatusCodes.Status409Conflict),
                    CallbackResult.Invalid => Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The callback needs externalRef and status.",
                        StatusCodes.Status422UnprocessableEntity),
                    CallbackResult.NoPayment => Problems.For(BuildingBlocks.Domain.ErrorCodes.NotFound, "No payment has this provider reference; the callback was recorded.",
                        StatusCodes.Status404NotFound),
                    _ => refused,
                };
            })
            .Accepts<ProviderCallbackRequest>("application/json")
            .Produces<PaymentView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PaymentsSignedCallback")
            .WithSummary("A provider reports a payment's status at its callback address (ADR-023)")
            .WithDescription("No bearer token. The endpoint key in the path names the tenant and provider. Headers: X-Callback-Timestamp (Unix seconds, within 5 minutes), "
                + "X-Callback-Id (unique per callback; a repeat is refused) and X-Signature (HMAC-SHA256 of \"timestamp.callbackId.body\" under the endpoint's "
                + "signing secret, hex in lower case).")
            .RequireSignedProviderCallback();

        var endpoints = payments.MapGroup("/callback-endpoints");

        endpoints.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new ListCallbackEndpoints(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<CallbackEndpointView>>(StatusCodes.Status200OK)
            .WithName("PaymentsListCallbackEndpoints")
            .WithSummary("The tenant's provider callback endpoints, without their keys or secrets (ADR-023)")
            .RequirePermission(PaymentPermissions.CallbackEndpointRead);

        endpoints.MapPost("/", async (IssueCallbackEndpointRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new IssueCallbackEndpoint(r.ProviderCode, r.RequireClientToken), ct))
                    .ToHttp(issued => Results.Created($"/api/v1/payments/callback-endpoints/{issued.Endpoint.Id}", issued)))
            .Produces<IssuedCallbackEndpoint>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .OnboardingConfiguration()
            .WithName("PaymentsIssueCallbackEndpoint")
            .WithSummary("Issue a provider's callback endpoint; an active one is rotated (ADR-023)")
            .WithDescription("Returns the callback path with its endpoint key, and the signing secret, once: they are never shown again. The provider's current endpoint "
                + "is still accepted until Payments:Callbacks:Overlap (7 days) has passed. Audited.")
            .RequirePermission(PaymentPermissions.CallbackEndpointUpdate);

        endpoints.MapPost("/{id:guid}/revoke", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RevokeCallbackEndpoint(id), ct)).ToHttp(Results.Ok))
            .Produces<CallbackEndpointView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .OnboardingConfiguration()
            .WithName("PaymentsRevokeCallbackEndpoint")
            .WithSummary("Revoke a callback endpoint at once, for a leaked key or secret. Audited (ADR-023)")
            .RequirePermission(PaymentPermissions.CallbackEndpointUpdate);
    }
}
