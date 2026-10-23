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
using Insys.BuildingBlocks.Domain;

namespace Insys.Payments.Domain.Payments;

public enum CallbackEndpointStatus
{
    Active,
    Retiring,
    Revoked,
}

/// <summary>
/// A provider's callback address in a tenant (payment.callback_endpoint, ADR-023): the endpoint key's hash, the encrypted
/// signing secret, and whether a client-credentials token is also required. The key and the secret are never held here
/// in plain text.
/// </summary>
public sealed class CallbackEndpoint : AggregateRoot<Guid>
{
    private CallbackEndpoint()
    {
    }

    public string ProviderCode { get; private set; } = "";

    public string KeyHash { get; private set; } = "";

    public byte[] SecretEnc { get; private set; } = [];

    public CallbackEndpointStatus Status { get; private set; }

    public bool RequireClientToken { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public string IssuedBy { get; private set; } = "";

    public DateTimeOffset? RetiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedBy { get; private set; }

    public bool UsableAt(DateTimeOffset now) => Status == CallbackEndpointStatus.Active || (Status == CallbackEndpointStatus.Retiring && RetiresAt > now);

    public static CallbackEndpoint Issue(Guid id, string providerCode, string keyHash, byte[] secretEnc, bool requireClientToken, string issuedBy, DateTimeOffset now)
    {
        var endpoint = new CallbackEndpoint
        {
            Id = Guard.NotEmpty(id),
            ProviderCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(providerCode), 15),
            KeyHash = keyHash.Length == 64 ? keyHash : throw new ArgumentException("A key hash is 64 hex characters.", nameof(keyHash)),
            SecretEnc = secretEnc,
            Status = CallbackEndpointStatus.Active,
            RequireClientToken = requireClientToken,
            IssuedAt = now,
            IssuedBy = Guard.MaxLength(issuedBy, 80),
        };
        endpoint.MarkChanged();
        return endpoint;
    }

    /// <summary>Rotation: still accepted until <paramref name="cutOver"/>, alongside its replacement.</summary>
    public void Retire(DateTimeOffset cutOver)
    {
        if (Status == CallbackEndpointStatus.Active)
        {
            Status = CallbackEndpointStatus.Retiring;
            RetiresAt = cutOver;
            MarkChanged();
        }
    }

    /// <summary>Refused at once, for a leaked key or secret.</summary>
    public Result Revoke(string by, DateTimeOffset now)
    {
        if (Status == CallbackEndpointStatus.Revoked)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The callback endpoint is already revoked.");
        }

        Status = CallbackEndpointStatus.Revoked;
        RevokedAt = now;
        RevokedBy = Guard.MaxLength(by, 80);
        MarkChanged();
        return Result.Success();
    }
}
