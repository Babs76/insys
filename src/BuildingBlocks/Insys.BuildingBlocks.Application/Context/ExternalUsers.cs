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
namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// The party, intermediary or provider an external user is (docs/specs/addenda/external-user-access.md §1), from their
/// approved link. A broker's codes include its sub-brokers' (§2, decided 24 October 2026); its own code is the one it
/// quotes and sells under.
/// </summary>
public sealed record ExternalSubject(string Kind, Guid? PartyId, string? IntermediaryCode, IReadOnlySet<string> IntermediaryCodes, string? ProviderId)
{
    public const string Party = "PARTY";

    public const string Intermediary = "INTERMEDIARY";

    public const string Provider = "PROVIDER";

    /// <summary>The channel audience an external user acts through (§5): members on the customer portal, brokers and providers on theirs.</summary>
    public string ChannelAudience => Kind switch
    {
        Party => "CUSTOMER",
        Intermediary => "BROKER",
        _ => "PROVIDER",
    };

    /// <summary>Whether a record with this intermediary is the broker's, or one of its sub-brokers' (§2).</summary>
    public bool Brokers(string? intermediaryCode) => Kind == Intermediary && intermediaryCode is not null && IntermediaryCodes.Contains(intermediaryCode);

    /// <summary>Broker and provider portal users must use MFA (§6); for members it is the tenant's authentication policy.</summary>
    public bool AlwaysNeedsMfa => Kind is Intermediary or Provider;
}

/// <summary>Whether a portal client's authentication policy requires MFA (iam.cfg_authentication_and_session_policies by client, §6).</summary>
public interface IPortalAuthentication
{
    Task<bool> MfaRequired(Guid tenantId, string? clientId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// Whether a module's business record is in an external user's relationship (external-user access §2), for a portal
/// read in another module: Documents files documents under business references owned elsewhere. Each module answers
/// for the reference types it owns.
/// </summary>
public interface IPortalRecordRelations
{
    bool Covers(string businessRefType);

    /// <summary>False when the record is not found, as well as when it is not the caller's.</summary>
    Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken);
}

/// <summary>The policies a broker sells and services in one domain (its codes and its sub-brokers', §2), for Billing's portal reads.</summary>
public interface IBrokeredPolicies
{
    Task<IReadOnlyList<Guid>> PolicyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken);

    Task<bool> Brokers(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, Guid policyId, CancellationToken cancellationToken);
}

/// <summary>
/// The parties a broker services in one domain (BR-091; external-user access §2, §3): those who hold, or are quoted or
/// proposed for, a policy under one of its codes. Party lists a broker's clients from every domain's answer and the
/// parties the broker registered.
/// </summary>
public interface IBrokeredClients
{
    Task<IReadOnlyList<Guid>> PartyIds(Guid tenantId, IReadOnlyCollection<string> intermediaryCodes, CancellationToken cancellationToken);
}
