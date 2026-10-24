/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.ProviderCallbacks;

namespace Insys.BuildingBlocks.Application.ProviderCallbacks;

// ADR-023 revision 2: provider callbacks authenticated by an endpoint key in the address and an HMAC signature keyed to the
// tenant and provider, over a timestamp and the callback's ID. No user token. Keys and secrets are never logged. Shared by
// every module that receives provider callbacks (Payments; Communications, addenda/communications.md §5.1); each keeps its
// own endpoint table, its own address and its own views.

/// <summary>Each module binds its own subclass to its own section (Payments:Callbacks, Communications:Callbacks).</summary>
public class CallbackOptions
{
    /// <summary>How far a callback's timestamp may be from the server's clock.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a rotated endpoint is still accepted beside its replacement.</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>A module's callback endpoints and the callback IDs they received, in the current tenant.</summary>
public interface ICallbackEndpointStore
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

public static class CallbackKeys
{
    /// <summary>SHA-256 of the endpoint key, hex in lower case: what is stored and looked up.</summary>
    public static string Hash(string endpointKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpointKey)));

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The signature a provider sends: HMAC-SHA256 of "timestamp.callbackId.body" under the endpoint's secret, hex in lower case.</summary>
    public static string Sign(string secret, string timestamp, string callbackId, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{callbackId}.{body}")));

    /// <summary>A key in an address is between 20 and 100 characters; anything else is refused without a lookup.</summary>
    public static bool Plausible(string endpointKey) => endpointKey.Length is >= 20 and <= 100;
}

/// <summary>A newly issued endpoint, with its key and secret in plain text: shown once, then never again.</summary>
public sealed record IssuedEndpoint(CallbackEndpoint Endpoint, string EndpointKey, string SigningSecret);

/// <summary>What a received callback's authentication came to.</summary>
public enum CallbackAuthentication
{
    /// <summary>Signed, fresh and not seen before: apply it.</summary>
    Accepted,

    /// <summary>Unknown or unusable endpoint, bad or missing signature, timestamp outside the tolerance, or client token missing.</summary>
    Unauthorized,

    /// <summary>The callback ID was already received at this endpoint.</summary>
    Replayed,
}

/// <summary>The endpoint lifecycle and the authentication of a callback, the same for every module (ADR-023).</summary>
public static class CallbackEndpoints
{
    /// <summary>Issues an endpoint for the provider; an active one for the provider is rotated (still accepted until the overlap ends).</summary>
    public static async Task<IssuedEndpoint> Issue(ICallbackEndpointStore store, IFieldProtector protector, string providerCode, bool requireClientToken, string issuedBy,
        DateTimeOffset now, CallbackOptions options, CancellationToken cancellationToken)
    {
        foreach (var current in await store.Active(providerCode, cancellationToken))
        {
            current.Retire(now + options.Overlap);
        }

        var key = CallbackKeys.NewSecret();
        var secret = CallbackKeys.NewSecret();
        var endpoint = CallbackEndpoint.Issue(Guid.CreateVersion7(), providerCode, CallbackKeys.Hash(key), protector.Protect(secret).Encrypted, requireClientToken,
            issuedBy, now);
        store.Add(endpoint);
        return new IssuedEndpoint(endpoint, key, secret);
    }

    public static async Task<Result<CallbackEndpoint>> Revoke(ICallbackEndpointStore store, Guid endpointId, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await store.Get(endpointId, cancellationToken) is not { } endpoint)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The callback endpoint was not found.");
        }

        var revoked = endpoint.Revoke(by, now);
        return revoked.IsFailure ? revoked.Error : endpoint;
    }

    /// <summary>
    /// Whether the signature over the timestamp, callback ID and body is the endpoint's, and the timestamp within the
    /// tolerance. Compared in constant time.
    /// </summary>
    public static bool Signed(CallbackEndpoint endpoint, IFieldProtector protector, string body, string? timestamp, string? callbackId, string? signature,
        DateTimeOffset now, CallbackOptions options)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(callbackId) || callbackId.Length > 100
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253_402_300_799));
        if ((now - sentAt).Duration() > options.Tolerance)
        {
            return false;
        }

        var expected = CallbackKeys.Sign(protector.Unprotect(endpoint.SecretEnc), timestamp!, callbackId, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    /// <summary>
    /// Records a signed callback's ID for the replay window, after forgetting IDs older than twice the tolerance; Replayed
    /// when the ID was already received.
    /// </summary>
    public static async Task<CallbackAuthentication> Admit(ICallbackEndpointStore store, CallbackEndpoint endpoint, string callbackId, DateTimeOffset now,
        CallbackOptions options, CancellationToken cancellationToken)
    {
        await store.PurgeReceipts(endpoint.Id, now - (options.Tolerance * 2), cancellationToken);
        return await store.TryRecordReceipt(endpoint.Id, callbackId, now, cancellationToken) ? CallbackAuthentication.Accepted : CallbackAuthentication.Replayed;
    }
}
