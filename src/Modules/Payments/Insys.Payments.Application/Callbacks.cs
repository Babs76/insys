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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.Payments.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Payments.Application;

// ADR-023 revision 2: provider callbacks authenticated by an endpoint key in the address and an HMAC signature keyed to the
// tenant and provider, over a timestamp and the callback's ID. No user token. Keys and secrets are never logged.

public sealed class CallbackOptions
{
    /// <summary>How far a callback's timestamp may be from the server's clock.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a rotated endpoint is still accepted beside its replacement.</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromDays(7);
}

public interface ICallbackEndpoints
{
    Task<CallbackEndpoint?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CallbackEndpoint>> All(CancellationToken cancellationToken);

    Task<IReadOnlyList<CallbackEndpoint>> Active(string providerCode, CancellationToken cancellationToken);

    void Add(CallbackEndpoint endpoint);

    /// <summary>Records the callback ID for the endpoint; false when it was already received (a replay).</summary>
    Task<bool> TryRecordReceipt(Guid endpointId, string callbackId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Forgets the endpoint's callback IDs received before <paramref name="before"/>, outside the replay window.</summary>
    Task PurgeReceipts(Guid endpointId, DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>Finds the tenant and endpoint for an endpoint key's hash, across tenants, before any tenant is known (V0033).</summary>
public interface ICallbackEndpointResolver
{
    Task<(Guid TenantId, Guid EndpointId)?> Resolve(string keyHash, CancellationToken cancellationToken);
}

public static class CallbackKeys
{
    /// <summary>SHA-256 of the endpoint key, hex in lower case: what is stored and looked up.</summary>
    public static string Hash(string endpointKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpointKey)));

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The signature a provider sends: HMAC-SHA256 of "timestamp.callbackId.body" under the endpoint's secret, hex in lower case.</summary>
    public static string Sign(string secret, string timestamp, string callbackId, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{callbackId}.{body}")));
}

public sealed record CallbackEndpointView(Guid Id, string ProviderCode, CallbackEndpointStatus Status, bool RequireClientToken, DateTimeOffset IssuedAt, string IssuedBy,
    DateTimeOffset? RetiresAt, DateTimeOffset? RevokedAt, long RowVersion)
{
    public static CallbackEndpointView From(CallbackEndpoint e) =>
        new(e.Id, e.ProviderCode, e.Status, e.RequireClientToken, e.IssuedAt, e.IssuedBy, e.RetiresAt, e.RevokedAt, e.RowVersion);
}

/// <summary>A new endpoint: the key and the signing secret are shown here once, and never again.</summary>
public sealed record IssuedCallbackEndpoint(CallbackEndpointView Endpoint, string CallbackPath, string EndpointKey, string SigningSecret);

/// <summary>Issue a provider's callback endpoint in the tenant; an active one for the provider is rotated (still accepted until the overlap ends).</summary>
public sealed record IssueCallbackEndpoint(string ProviderCode, bool RequireClientToken) : ICommand<IssuedCallbackEndpoint>;

public sealed record RevokeCallbackEndpoint(Guid EndpointId) : ICommand<CallbackEndpointView>;

public sealed record ListCallbackEndpoints : IQuery<IReadOnlyList<CallbackEndpointView>>;

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
    IOptions<CallbackOptions> options, IAuditTrail audit)
    : ICommandHandler<IssueCallbackEndpoint, IssuedCallbackEndpoint>,
      ICommandHandler<RevokeCallbackEndpoint, CallbackEndpointView>,
      IQueryHandler<ListCallbackEndpoints, IReadOnlyList<CallbackEndpointView>>
{
    public async Task<Result<IssuedCallbackEndpoint>> Handle(IssueCallbackEndpoint command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var current in await endpoints.Active(command.ProviderCode, cancellationToken))
        {
            current.Retire(now + options.Value.Overlap);
        }

        var key = CallbackKeys.NewSecret();
        var secret = CallbackKeys.NewSecret();
        var endpoint = CallbackEndpoint.Issue(Guid.CreateVersion7(), command.ProviderCode, CallbackKeys.Hash(key), protector.Protect(secret).Encrypted,
            command.RequireClientToken, execution.ActorId, now);
        endpoints.Add(endpoint);

        // Never the key or the secret: the audit names the endpoint and provider only.
        audit.Record(new AuditEntry("PAYMENT", "IssueCallbackEndpoint", nameof(CallbackEndpoint), endpoint.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = command.ProviderCode, ["requireClientToken"] = command.RequireClientToken ? "true" : "false" }));
        return new IssuedCallbackEndpoint(CallbackEndpointView.From(endpoint), $"/api/v1/payments/callbacks/{key}", key, secret);
    }

    public async Task<Result<CallbackEndpointView>> Handle(RevokeCallbackEndpoint command, CancellationToken cancellationToken)
    {
        if (await endpoints.Get(command.EndpointId, cancellationToken) is not { } endpoint)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The callback endpoint was not found.");
        }

        var revoked = endpoint.Revoke(execution.ActorId, clock.UtcNow);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        audit.Record(new AuditEntry("PAYMENT", "RevokeCallbackEndpoint", nameof(CallbackEndpoint), endpoint.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = endpoint.ProviderCode }));
        return CallbackEndpointView.From(endpoint);
    }

    public async Task<Result<IReadOnlyList<CallbackEndpointView>>> Handle(ListCallbackEndpoints query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<CallbackEndpointView>>((await endpoints.All(cancellationToken)).Select(CallbackEndpointView.From).ToList());
}

internal sealed class ReceiveCallbackHandler(ICallbackEndpoints endpoints, IPaymentRepository payments, IPaymentCodes codes, IFieldProtector protector, IClock clock,
    IOptions<CallbackOptions> options, ILogger<ReceiveCallbackHandler> logger) : ICommandHandler<ReceiveCallback, CallbackHandled>
{
    public async Task<Result<CallbackHandled>> Handle(ReceiveCallback command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (await endpoints.Get(command.EndpointId, cancellationToken) is not { } endpoint || !endpoint.UsableAt(now))
        {
            return new CallbackHandled(CallbackResult.Unauthorized);
        }

        var (externalRef, status) = Parse(command.Body);
        var signed = Verify(endpoint, command, now);
        if (!signed || (endpoint.RequireClientToken && !command.ClientTokenValid))
        {
            // Recorded in the tenant and not applied. The log names the endpoint by ID only, never its key, secret or signature.
            payments.RecordCallback(endpoint.ProviderCode, Truncate(externalRef ?? "", 80), BodyJson(command.Body), signatureValid: false, now, processed: false);
            logger.LogWarning("Callback refused at endpoint {EndpointId} ({Provider}): {Reason}", endpoint.Id, endpoint.ProviderCode,
                signed ? "client token required" : "signature or timestamp not valid");
            return new CallbackHandled(CallbackResult.Unauthorized);
        }

        await endpoints.PurgeReceipts(endpoint.Id, now - (options.Value.Tolerance * 2), cancellationToken);
        if (!await endpoints.TryRecordReceipt(endpoint.Id, command.CallbackId!, now, cancellationToken))
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

        var outcome = await codes.MapStatus(endpoint.ProviderCode, status, clock.UtcToday, cancellationToken);
        var result = payment.RecordCallback(outcome, $"Provider status {status}", clock.UtcToday, now);
        return result.IsFailure ? result.Error : new CallbackHandled(CallbackResult.Applied, PaymentView.From(payment));
    }

    private bool Verify(CallbackEndpoint endpoint, ReceiveCallback command, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(command.Signature) || string.IsNullOrEmpty(command.CallbackId) || command.CallbackId.Length > 100
            || !long.TryParse(command.Timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253_402_300_799));
        if ((now - sentAt).Duration() > options.Value.Tolerance)
        {
            return false;
        }

        var expected = CallbackKeys.Sign(protector.Unprotect(endpoint.SecretEnc), command.Timestamp!, command.CallbackId, command.Body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(command.Signature.ToLowerInvariant()));
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
