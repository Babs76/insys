using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Payments.Api.StaticData;
using Insys.Payments.Application;
using Insys.Payments.Contracts;
using Insys.Payments.Domain.Payments;
using Insys.Payments.Infrastructure;
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

public sealed record BuildBatchRequest(string Purpose, string Currency);

public sealed record ImportStatementRequest(string AccountRef, StatementLine[] Lines);

/// <param name="ExternalRef">The provider's reference for the payment.</param>
/// <param name="Status">The provider's own status code; mapped by payment.ref_payment_status_mappings.</param>
public sealed record ProviderCallbackRequest(string ExternalRef, string Status);

/// <summary>
/// The Payments module: payments with idempotency (PAY-001), traceable status changes (PAY-002), provider callbacks,
/// payout batches, bank statement reconciliation (PAY-003), and unknown outcomes that go to a person, never to success
/// (PAY-005).
/// </summary>
public sealed class PaymentsModule : IModule
{
    public string Name => "Payments";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPaymentsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        PaymentsStaticDataEndpoints.MapStaticData(api);
        var payments = api.MapGroup("/payments").WithTags(Name);

        payments.MapPost("/payments", async (InitiatePaymentRequest r, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new InitiatePayment($"API:{http.Request.Headers["Idempotency-Key"]}", r.Direction, r.Purpose, r.MethodCode, r.Amount, r.Currency,
                    r.CounterpartyPartyId, r.BusinessRefType, r.BusinessRefId, r.BankAccountId), ct)).ToHttp(view => Results.Created($"/api/v1/payments/payments/{view.Id}", view)))
            .WithName("PaymentsInitiate")
            .WithSummary("Start a payment; its Idempotency-Key is also the payment's own key, so it never moves money twice (PAY-001)")
            .WithDescription("An inbound payment is sent to its provider at once; an outbound one waits for a payout batch.")
            .RequirePermission(PaymentPermissions.PaymentCreate)
            .RequireIdempotencyKey();

        payments.MapGet("/payments/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetPayment(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsGet")
            .WithSummary("Get a payment with its status history (PAY-002) and allocations")
            .RequirePermission(PaymentPermissions.PaymentRead);

        payments.MapPost("/payments/{id:guid}/resolve", async (Guid id, ResolveUnknownRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ResolveUnknown(id, r.Succeeded, r.Evidence), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsResolveUnknown")
            .WithSummary("Decide an unknown outcome from the evidence (PAY-005)")
            .RequirePermission(PaymentPermissions.PaymentResolve);

        payments.MapPost("/payments/{id:guid}/reverse", async (Guid id, ReversePaymentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReversePayment(id, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsReverse")
            .WithSummary("Reverse a succeeded payment and its allocations")
            .RequirePermission(PaymentPermissions.PaymentResolve);

        payments.MapPost("/payments/{id:guid}/allocate", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AllocatePayment(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsAllocate")
            .WithSummary("Allocate what is left of a received payment to its invoices (BIL-003); done automatically when it is received")
            .RequirePermission(PaymentPermissions.PaymentUpdate);

        payments.MapPost("/providers/{providerCode}/callbacks", async (string providerCode, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
            {
                using var reader = new StreamReader(http.Request.Body);
                var body = await reader.ReadToEndAsync(ct);
                ProviderCallbackRequest? callback;
                try
                {
                    callback = JsonSerializer.Deserialize<ProviderCallbackRequest>(body, JsonSerializerOptions.Web);
                }
                catch (JsonException)
                {
                    callback = null;
                }

                if (callback is null || string.IsNullOrEmpty(callback.ExternalRef) || string.IsNullOrEmpty(callback.Status))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The callback needs externalRef and status.", StatusCodes.Status422UnprocessableEntity);
                }

                return (await dispatcher.Send(new RecordProviderCallback(providerCode, callback.ExternalRef, callback.Status, body, http.Request.Headers["X-Signature"]), ct))
                    .ToHttp(Results.Ok);
            })
            .WithName("PaymentsProviderCallback")
            .WithSummary("A provider reports a payment's status, signed in the X-Signature header (HMAC-SHA256 of the body)")
            .WithDescription("Every callback is recorded. An invalid signature changes nothing; a status with no mapping makes the payment Unknown (PAY-005).")
            .Accepts<ProviderCallbackRequest>("application/json")
            .RequirePermission(PaymentPermissions.CallbackCreate);

        payments.MapPost("/payout-batches", async (BuildBatchRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new BuildBatch(r.Purpose, r.Currency), ct)).ToHttp(view => Results.Created($"/api/v1/payments/payout-batches/{view.Id}", view)))
            .WithName("PaymentsBuildBatch")
            .WithSummary("Batch the payouts waiting for a purpose and currency; a second person approves the batch in Workflow (SRS-002)")
            .RequirePermission(PaymentPermissions.PayoutCreate)
            .RequireIdempotencyKey();

        payments.MapGet("/payout-batches/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetBatch(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsGetBatch")
            .WithSummary("Get a payout batch")
            .RequirePermission(PaymentPermissions.PayoutRead);

        payments.MapPost("/payout-batches/{id:guid}/release", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReleaseBatch(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsReleaseBatch")
            .WithSummary("Release an approved batch: each payment is sent to its provider")
            .RequirePermission(PaymentPermissions.PayoutRelease);

        payments.MapPost("/bank-statements", async (ImportStatementRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ImportStatement(r.AccountRef, r.Lines), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsImportStatement")
            .WithSummary("Import bank statement lines and match them to payments (PAY-003); unmatched lines become reconciliation exceptions")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate)
            .RequireIdempotencyKey();

        payments.MapGet("/reconciliation-exceptions", async (string? status, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListExceptions(status, Cursor.ClampLimit(limit)), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsListExceptions")
            .WithSummary("Unmatched and ambiguous items waiting for a person (PAY-005)")
            .RequirePermission(PaymentPermissions.ReconciliationRead);

        payments.MapPost("/reconciliation-exceptions/{id:guid}/assign", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AssignException(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsAssignException")
            .WithSummary("Take an exception to investigate")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate);

        payments.MapPost("/reconciliation-exceptions/{id:guid}/resolve", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ResolveException(id), ct)).ToHttp(Results.Ok))
            .WithName("PaymentsResolveException")
            .WithSummary("Resolve an exception you are investigating; an unknown payment is resolved first")
            .RequirePermission(PaymentPermissions.ReconciliationUpdate);
    }
}
