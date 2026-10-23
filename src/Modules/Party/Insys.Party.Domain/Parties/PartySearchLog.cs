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
/// A party search, as the access log records it (party.party_search_log, GAP-022; PTY-003, PTY-005): who searched,
/// when, the filters and the number of results. An identifier is kept as its keyed hash only. Never changed.
/// </summary>
public sealed class PartySearchLog : Entity<Guid>, ITenantScoped
{
    private PartySearchLog()
    {
    }

    public Guid TenantId { get; private set; }

    public DateTimeOffset SearchedAt { get; private set; }

    public string SearchedBy { get; private set; } = "";

    public string? NameFilter { get; private set; }

    public string? IdentifierDocType { get; private set; }

    public string? IdentifierCountry { get; private set; }

    public string? IdentifierHash { get; private set; }

    public string? RoleFilter { get; private set; }

    public string? StatusFilter { get; private set; }

    public int ResultCount { get; private set; }

    public static PartySearchLog Record(string searchedBy, string? name, (string DocType, string Country, string Hash)? identifier, string? role,
        string? status, int resultCount, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        SearchedAt = now,
        SearchedBy = Guard.MaxLength(Guard.NotNullOrWhiteSpace(searchedBy), 80),
        NameFilter = name,
        IdentifierDocType = identifier?.DocType,
        IdentifierCountry = identifier?.Country,
        IdentifierHash = identifier?.Hash,
        RoleFilter = role,
        StatusFilter = status,
        ResultCount = resultCount,
    };
}
