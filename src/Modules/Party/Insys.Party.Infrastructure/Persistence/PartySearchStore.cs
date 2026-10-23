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
using Insys.Party.Application;
using Insys.Party.Domain.Parties;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>
/// Party search (GAP-022) on the existing indexes: the display name's trigram index (ix_party_1), the identifier's keyed
/// hash, and party roles by party (ix_party_role_1). Parties merged into another are left out.
/// </summary>
internal sealed class PartySearchStore(PartyDbContext db) : IPartySearch
{
    public async Task<IReadOnlyList<PartySummary>> Search(string? name, (string DocType, string Country, string Hash)? identifier, string? roleCode,
        PartyStatus? status, DateOnly today, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var parties = db.Parties.AsNoTracking().Where(p => p.MergedIntoId == null);
        if (name is not null)
        {
            var pattern = "%" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            parties = parties.Where(p => EF.Functions.ILike(p.DisplayName, pattern, "\\"));
        }

        if (identifier is { } i)
        {
            parties = parties.Where(p => p.Identifiers.Any(x => x.DocType == i.DocType && x.Country == i.Country && x.NumberHash == i.Hash));
        }

        if (roleCode is not null)
        {
            parties = parties.Where(p => p.Roles.Any(r => r.RoleCode == roleCode && r.ValidFrom <= today && (r.ValidTo == null || r.ValidTo > today)));
        }

        if (status is { } s)
        {
            parties = parties.Where(p => p.Status == s);
        }

        if (afterId is { } after)
        {
            parties = parties.Where(p => p.Id.CompareTo(after) > 0);
        }

        var page = await parties.OrderBy(p => p.Id).Take(limit)
            .Select(p => new
            {
                p.Id, p.Kind, p.DisplayName, p.Status,
                Roles = p.Roles.Where(r => r.ValidFrom <= today && (r.ValidTo == null || r.ValidTo > today)).Select(r => r.RoleCode).Distinct().ToList(),
            })
            .ToListAsync(cancellationToken);
        return page.Select(p => new PartySummary(p.Id, p.Kind, p.DisplayName, p.Status, [.. p.Roles.Order(StringComparer.Ordinal)])).ToList();
    }

    public void Log(PartySearchLog entry) => db.Set<PartySearchLog>().Add(entry);

    public async Task<IReadOnlyList<PartySearchLogEntry>> Searches(ListPartySearches query, CancellationToken cancellationToken)
    {
        var log = db.Set<PartySearchLog>().AsNoTracking();
        if (query.SearchedBy is { } who)
        {
            log = log.Where(l => l.SearchedBy == who);
        }

        if (query.From is { } from)
        {
            log = log.Where(l => l.SearchedAt >= from);
        }

        if (query.To is { } to)
        {
            log = log.Where(l => l.SearchedAt < to);
        }

        if (query.BeforeId is { } before)
        {
            log = log.Where(l => l.Id.CompareTo(before) < 0);
        }

        return await log.OrderByDescending(l => l.Id).Take(query.Limit)
            .Select(l => new PartySearchLogEntry(l.Id, l.SearchedAt, l.SearchedBy, l.NameFilter, l.IdentifierDocType, l.IdentifierCountry, l.IdentifierHash,
                l.RoleFilter, l.StatusFilter, l.ResultCount))
            .ToListAsync(cancellationToken);
    }
}
