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
        db.Parties.Include(p => p.Individual).Include(p => p.Organisation).Include(p => p.Addresses).Include(p => p.ContactPoints).Include(p => p.Identifiers)
            .Include(p => p.Relationships).Include(p => p.Roles).AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

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
        PartyAnonymised e => new Contracts.PartyAnonymised(e.PartyId),
        ConsentGranted e => new Contracts.ConsentGranted(e.PartyId, e.PurposeCode),
        ConsentWithdrawn e => new Contracts.ConsentWithdrawn(e.PartyId, e.PurposeCode),
        _ => null,
    };
}
