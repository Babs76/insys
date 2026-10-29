/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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

namespace Insys.Party.Domain.Parties;

/// <summary>
/// A due diligence check run on a party (party.due_diligence_check, addenda/due-diligence.md §3): by a provider, with its
/// reference, or by a person against a document. It holds no number, only the identifier it is about. Never changed.
/// </summary>
public sealed class DueDiligenceCheck : AggregateRoot<Guid>
{
    public const string Identity = "IDENTITY";
    public const string Passed = "PASSED";
    public const string Failed = "FAILED";

    private DueDiligenceCheck()
    {
    }

    public Guid PartyId { get; private set; }

    public string CheckType { get; private set; } = "";

    public string Outcome { get; private set; } = "";

    public Guid? IdentifierId { get; private set; }

    public string? PartnerCode { get; private set; }

    public string? ProviderRef { get; private set; }

    public Guid? DocumentId { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }

    public Guid? CheckedBy { get; private set; }

    /// <summary>F001: a provider's answer on an identifier.</summary>
    public static DueDiligenceCheck ByProvider(Guid partyId, Guid identifierId, string partnerCode, bool verified, string? providerRef, string? reason, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = Identity, Outcome = verified ? Passed : Failed, IdentifierId = identifierId,
            PartnerCode = Guard.MaxLength(partnerCode, 15), ProviderRef = providerRef is null ? null : Guard.MaxLength(providerRef, 80),
            Reason = reason is null ? null : Guard.MaxLength(reason, 200), CheckedAt = now,
        };

    /// <summary>F001: a person verified an identifier against a document filed against the party.</summary>
    public static DueDiligenceCheck ByPerson(Guid partyId, Guid identifierId, Guid documentId, Guid checkedBy, string reference, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = Identity, Outcome = Passed, IdentifierId = identifierId, DocumentId = documentId,
            ProviderRef = Guard.MaxLength(reference, 80), CheckedAt = now, CheckedBy = checkedBy,
        };
}
