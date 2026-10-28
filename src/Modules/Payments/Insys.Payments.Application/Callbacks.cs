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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.ProviderCallbacks;
using Insys.Payments.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Payments.Application;

// ADR-023 revision 2: provider callbacks authenticated by an endpoint key in the address and an HMAC signature keyed to the
// tenant and provider, over a timestamp and the callback's ID. No user token. Keys and secrets are never logged. The
// endpoint lifecycle and the signature check are the building blocks' (ProviderCallbacks), shared with Communications.

public sealed class PaymentCallbackOptions : CallbackOptions;

/// <summary>Payments' callback endpoints (payment.callback_endpoint and payment.callback_receipt).</summary>
public interface ICallbackEndpoints : ICallbackEndpointStore;

/// <summary>Finds the tenant and endpoint for an endpoint key's hash, across tenants, before any tenant is known (V0033).</summary>
public interface ICallbackEndpointResolver
{
    Task<(Guid TenantId, Guid EndpointId)?> Resolve(string keyHash, CancellationToken cancellationToken);
}

public sealed record CallbackEndpointView(Guid Id, string ProviderCode, CallbackEndpointStatus Status, bool RequireClientToken, DateTimeOffset IssuedAt, string IssuedBy,
    DateTimeOffset? RetiresAt, DateTimeOffset? RevokedAt, long RowVersion)
{
    public static CallbackEndpointView From(CallbackEndpoint e) =>
        new(e.Id, e.ProviderCode, Enum.Parse<CallbackEndpointStatus>(e.Status.ToString()), e.RequireClientToken, e.IssuedAt, e.IssuedBy, e.RetiresAt, e.RevokedAt,
            e.RowVersion);
}

/// <summary>A new endpoint: the key and the signing secret are shown here once, and never again.</summary>
public sealed record IssuedCallbackEndpoint(CallbackEndpointView Endpoint, string CallbackPath, string EndpointKey, string SigningSecret);

/// <summary>Issue a provider's callback endpoint in the tenant; an active one for the provider is rotated (still accepted until the overlap ends).</summary>
public sealed record IssueCallbackEndpoint(string ProviderCode, bool RequireClientToken) : ICommand<IssuedCallbackEndpoint>;

public sealed record RevokeCallbackEndpoint(Guid EndpointId) : ICommand<CallbackEndpointView>;

public sealed record ListCallbackEndpoints : IQuery<IReadOnlyList<CallbackEndpointView>>;

/// <summary>BR-053: one callback endpoint, without its key or secret.</summary>
public sealed record GetCallbackEndpoint(Guid EndpointId) : IQuery<CallbackEndpointView>;

public enum CallbackResult
{
    Applied,
    Unauthorized,
    Replayed,
    Invalid,
    NoPayment,
}

public sealed record CallbackHandled(CallbackResult Result, PaymentView? Payment = null);

/// <summary>
/// A callback at an endpoint's address, in the endpoint's tenant (set from the key). <paramref name="ClientTokenValid"/> says
/// whether a client-credentials token for this tenant holding payment_callback.create came with it.
/// </summary>
public sealed record ReceiveCallback(Guid EndpointId, string Body, string? Timestamp, string? CallbackId, string? Signature, bool ClientTokenValid)
    : ICommand<CallbackHandled>;

public sealed class IssueCallbackEndpointValidator : AbstractValidator<IssueCallbackEndpoint>
{
    public IssueCallbackEndpointValidator() => RuleFor(c => c.ProviderCode).NotEmpty().MaximumLength(15);
}

internal sealed class CallbackEndpointHandlers(ICallbackEndpoints endpoints, IFieldProtector protector, IExecutionContext execution, IClock clock,
    IOptions<PaymentCallbackOptions> options, IAuditTrail audit)
    : ICommandHandler<IssueCallbackEndpoint, IssuedCallbackEndpoint>,
      ICommandHandler<RevokeCallbackEndpoint, CallbackEndpointView>,
      IQueryHandler<ListCallbackEndpoints, IReadOnlyList<CallbackEndpointView>>,
      IQueryHandler<GetCallbackEndpoint, CallbackEndpointView>
{
    public async Task<Result<CallbackEndpointView>> Handle(GetCallbackEndpoint query, CancellationToken cancellationToken) =>
        await endpoints.Get(query.EndpointId, cancellationToken) is { } endpoint
            ? CallbackEndpointView.From(endpoint)
            : Error.NotFound(ErrorCodes.NotFound, "The callback endpoint was not found.");

    public async Task<Result<IssuedCallbackEndpoint>> Handle(IssueCallbackEndpoint command, CancellationToken cancellationToken)
    {
        var issued = await CallbackEndpoints.Issue(endpoints, protector, command.ProviderCode, command.RequireClientToken, execution.ActorId, clock.UtcNow,
            options.Value, cancellationToken);

        // Never the key or the secret: the audit names the endpoint and provider only.
        audit.Record(new AuditEntry("PAYMENT", "IssueCallbackEndpoint", nameof(CallbackEndpoint), issued.Endpoint.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = command.ProviderCode, ["requireClientToken"] = command.RequireClientToken ? "true" : "false" }));
        return new IssuedCallbackEndpoint(CallbackEndpointView.From(issued.Endpoint), $"/api/v1/payments/callbacks/{issued.EndpointKey}", issued.EndpointKey,
            issued.SigningSecret);
    }

    public async Task<Result<CallbackEndpointView>> Handle(RevokeCallbackEndpoint command, CancellationToken cancellationToken)
    {
        var revoked = await CallbackEndpoints.Revoke(endpoints, command.EndpointId, execution.ActorId, clock.UtcNow, cancellationToken);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        audit.Record(new AuditEntry("PAYMENT", "RevokeCallbackEndpoint", nameof(CallbackEndpoint), revoked.Value.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = revoked.Value.ProviderCode }));
        return CallbackEndpointView.From(revoked.Value);
    }

    public async Task<Result<IReadOnlyList<CallbackEndpointView>>> Handle(ListCallbackEndpoints query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<CallbackEndpointView>>((await endpoints.All(cancellationToken)).Select(CallbackEndpointView.From).ToList());
}

internal sealed class ReceiveCallbackHandler(ICallbackEndpoints endpoints, IPaymentRepository payments, IPaymentCodes codes, IFieldProtector protector, IBusinessDate businessDate, IClock clock,
    IOptions<PaymentCallbackOptions> options, ILogger<ReceiveCallbackHandler> logger) : ICommandHandler<ReceiveCallback, CallbackHandled>
{
    public async Task<Result<CallbackHandled>> Handle(ReceiveCallback command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (await endpoints.Get(command.EndpointId, cancellationToken) is not { } endpoint || !endpoint.UsableAt(now))
        {
            return new CallbackHandled(CallbackResult.Unauthorized);
        }

        var (externalRef, status) = Parse(command.Body);
        var signed = CallbackEndpoints.Signed(endpoint, protector, command.Body, command.Timestamp, command.CallbackId, command.Signature, now, options.Value);
        if (!signed || (endpoint.RequireClientToken && !command.ClientTokenValid))
        {
            // Recorded in the tenant and not applied. The log names the endpoint by ID only, never its key, secret or signature.
            payments.RecordCallback(endpoint.ProviderCode, Truncate(externalRef ?? "", 80), BodyJson(command.Body), signatureValid: false, now, processed: false);
            logger.LogWarning("Callback refused at endpoint {EndpointId} ({Provider}): {Reason}", endpoint.Id, endpoint.ProviderCode,
                signed ? "client token required" : "signature or timestamp not valid");
            return new CallbackHandled(CallbackResult.Unauthorized);
        }

        if (await CallbackEndpoints.Admit(endpoints, endpoint, command.CallbackId!, now, options.Value, cancellationToken) == CallbackAuthentication.Replayed)
        {
            logger.LogWarning("Callback replay refused at endpoint {EndpointId} ({Provider})", endpoint.Id, endpoint.ProviderCode);
            return new CallbackHandled(CallbackResult.Replayed);
        }

        if (externalRef is null || status is null)
        {
            return new CallbackHandled(CallbackResult.Invalid);
        }

        var payment = await payments.ByExternalRef(endpoint.ProviderCode, externalRef, cancellationToken);
        payments.RecordCallback(endpoint.ProviderCode, externalRef, command.Body, signatureValid: true, now, processed: payment is not null);
        if (payment is null)
        {
            return new CallbackHandled(CallbackResult.NoPayment);
        }

        var outcome = await codes.MapStatus(endpoint.ProviderCode, status, businessDate.Today, cancellationToken);
        var result = payment.RecordCallback(outcome, $"Provider status {status}", businessDate.Today, now);
        return result.IsFailure ? result.Error : new CallbackHandled(CallbackResult.Applied, PaymentView.From(payment));
    }

    private static (string? ExternalRef, string? Status) Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? (root.TryGetProperty("externalRef", out var r) ? r.GetString() : null, root.TryGetProperty("status", out var s) ? s.GetString() : null)
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string BodyJson(string body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return body;
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
