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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Application;
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

internal sealed class PartyRepository(PartyDbContext db) : IPartyRepository
{
    public Task<Domain.Parties.Party?> Get(Guid id, CancellationToken cancellationToken) =>
        db.Parties.Include(p => p.Individual).Include(p => p.Organisation).Include(p => p.Addresses).Include(p => p.ContactPoints).Include(p => p.Identifiers).Include(p => p.BankAccounts)
            .Include(p => p.Relationships).Include(p => p.Roles).AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<Domain.Parties.Party?> WithBankAccount(Guid bankAccountId, CancellationToken cancellationToken) =>
        await db.Parties.AsNoTracking().Where(p => p.BankAccounts.Any(a => a.Id == bankAccountId)).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken) is { } id
            ? await Get(id, cancellationToken)
            : null;

    public async Task<Guid?> Match(IndividualData person, (string DocType, string Country, string Hash)? identifier, CancellationToken cancellationToken)
    {
        if (identifier is { } i && await db.Set<PartyIdentifier>().Where(x => x.DocType == i.DocType && x.Country == i.Country && x.NumberHash == i.Hash)
                .Select(x => (Guid?)x.PartyId).FirstOrDefaultAsync(cancellationToken) is { } byIdentifier)
        {
            return byIdentifier;
        }

        // A close name match: the same first names and surname, ignoring case and outer spaces, and the same birth date.
        var first = person.FirstNames.Trim().ToLower();
        var surname = person.Surname.Trim().ToLower();
        return await db.Database.SqlQuery<Guid?>($"""
            SELECT party_id AS "Value" FROM party.individual
            WHERE lower(trim(first_names)) = {first} AND lower(trim(surname)) = {surname} AND birth_date = {person.BirthDate} LIMIT 1
            """).FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> IdentifierExists(string docType, string country, string hash, CancellationToken cancellationToken) =>
        db.Set<PartyIdentifier>().AnyAsync(i => i.DocType == docType && i.Country == country && i.NumberHash == hash, cancellationToken);

    public async Task<IReadOnlyList<ConsentRecord>> Consents(Guid partyId, CancellationToken cancellationToken) =>
        await db.Consents.Where(c => c.PartyId == partyId).ToListAsync(cancellationToken);

    public void Add(Domain.Parties.Party party) => db.Parties.Add(party);

    public void Add(ConsentRecord record) => db.Consents.Add(record);
}

internal sealed class PartyCodes(PartyDbContext db) : IPartyCodes
{
    public async Task<(bool Exists, string? FormatPattern)> IdentityDocumentType(string country, string docType, DateOnly day, CancellationToken cancellationToken)
    {
        var type = await InForce<IdentityDocumentTypes>(day).Where(t => t.Country == country && t.DocType == docType)
            .OrderByDescending(t => t.TenantId != null).FirstOrDefaultAsync(cancellationToken);
        return (type is not null, type?.FormatPattern);
    }

    public async Task<bool> RoleAllowed(string roleCode, PartyKind kind, string domain, DateOnly day, CancellationToken cancellationToken)
    {
        var role = await InForce<PartyRoleTypes>(day).Where(r => r.RoleCode == roleCode).OrderByDescending(r => r.TenantId != null).FirstOrDefaultAsync(cancellationToken);
        return role is not null && role.AllowedPartyKinds.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase)
            && role.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool> RelationshipTypeExists(string relationshipCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<RelationshipTypes>(day).AnyAsync(r => r.Code == relationshipCode, cancellationToken);

    public Task<bool> ConsentPurposeExists(string purposeCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ConsentPurposes>(day).AnyAsync(p => p.PurposeCode == purposeCode, cancellationToken);

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(PartyStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class PartyEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        PartyRegistered e => new Contracts.PartyRegistered(e.PartyId, e.Kind.ToString().ToUpperInvariant()),
        PartyDetailsChanged e => new Contracts.PartyDetailsChanged(e.PartyId, e.Part),
        PartiesMerged e => new Contracts.PartiesMerged(e.SurvivorId, e.MergedId),
        PartyMatchReviewRequested e => new Contracts.PartyMatchReviewRequested(e.PartyId, e.MatchedPartyId),
        PartyAnonymised e => new Contracts.PartyAnonymised(e.PartyId),
        ConsentGranted e => new Contracts.ConsentGranted(e.PartyId, e.PurposeCode),
        ConsentWithdrawn e => new Contracts.ConsentWithdrawn(e.PartyId, e.PurposeCode),
        PartyBankAccountChanged e => new Contracts.PartyBankAccountChanged(e.PartyId, e.BankAccountId, e.SupersededId, e.Status),
        BankAccountVerificationRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.BankAccountBusinessRefType, e.BankAccountId,
            Contracts.PartyApprovals.BankAccountVerificationType, e.RequestedBy, null, null, $"Verify bank account ending {e.Last4}", e.SubjectHash,
            Contracts.PartyPermissions.BankAccountVerify),
        PartyMatchResolved e => new Contracts.PartyMatchResolved(e.PartyId, e.Resolution, e.ExistingPartyId),
        PartyMatchResolutionProposed e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.MatchBusinessRefType, e.PartyId,
            Contracts.PartyApprovals.MatchResolutionType, e.RequestedBy, null, null,
            e.Resolution == "LINK" ? $"Link portal registration {e.PartyId} to party {e.ExistingPartyId}" : $"Confirm portal registration {e.PartyId} as a new party",
            null, Contracts.PartyPermissions.PartyUpdate),
        BankAccountVerificationWithdrawn e => new BuildingBlocks.Contracts.Approvals.ApprovalWithdrawn(Contracts.PartyApprovals.BankAccountBusinessRefType, e.BankAccountId,
            "The account was changed before it was verified."),
        _ => null,
    };
}
