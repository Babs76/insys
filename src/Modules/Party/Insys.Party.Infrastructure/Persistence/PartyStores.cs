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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Application;
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

internal sealed class BulkJobs(PartyDbContext db) : IBulkJobs
{
    public Task<BulkJob?> Get(Guid id, CancellationToken cancellationToken) => db.Set<BulkJob>().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PartyExportRow>> ExportRows(string? status, CancellationToken cancellationToken)
    {
        var wanted = status is null ? (PartyStatus?)null : Enum.Parse<PartyStatus>(status, ignoreCase: true);
        return await db.Parties.AsNoTracking().Where(p => p.Status != PartyStatus.Merged && p.Status != PartyStatus.Anonymised && (wanted == null || p.Status == wanted)).OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.PartyNumber, p.DisplayName, p.Status }).ToListAsync(cancellationToken)
            .ContinueWith(t => (IReadOnlyList<PartyExportRow>)t.Result.Select(p => new PartyExportRow(p.Id, p.PartyNumber, p.DisplayName, p.Status.ToString().ToUpperInvariant())).ToList(), TaskScheduler.Default);
    }

    public void Add(BulkJob job) => db.Set<BulkJob>().Add(job);
}

internal sealed class DataSubjectRequests(PartyDbContext db) : IDataSubjectRequests
{
    public Task<DataSubjectRequest?> Get(Guid id, CancellationToken cancellationToken) => db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DataSubjectRequest>> List(Guid? partyId, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.DataSubjectRequests.AsNoTracking().Where(r => (partyId == null || r.PartyId == partyId) && (afterId == null || r.Id.CompareTo(afterId.Value) > 0)).OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<int> ResponseDays(DateOnly day, CancellationToken cancellationToken) =>
        await db.Set<StaticData.DataSubjectSettings>().AsNoTracking().InForce(BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration, day).OrderBy(s => s.Code).Select(s => (int?)s.ResponseDays)
            .FirstOrDefaultAsync(cancellationToken) ?? DataSubjectDefaults.ResponseDays;

    public async Task<IReadOnlyList<(Guid Id, PartyStatus Status)>> DormancyCandidates(DateOnly day, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Parties.AsNoTracking().Where(p => (p.Status == PartyStatus.Active || p.Status == PartyStatus.Dormant)
                && (EF.Property<DateOnly?>(p, "DormancyCheckedOn") == null || EF.Property<DateOnly?>(p, "DormancyCheckedOn") < day))
            .OrderBy(p => EF.Property<DateOnly?>(p, "DormancyCheckedOn") != null).ThenBy(p => EF.Property<DateOnly?>(p, "DormancyCheckedOn")).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Status }).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.Status)).ToList();
    }

    public Task DormancyChecked(IReadOnlyList<Guid> ids, DateOnly day, CancellationToken cancellationToken) =>
        db.Parties.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(set => set.SetProperty(p => EF.Property<DateOnly?>(p, "DormancyCheckedOn"), day), cancellationToken);

    public async Task<IReadOnlyList<Guid>> RetentionCandidates(int limit, CancellationToken cancellationToken) =>
        await db.Parties.AsNoTracking().Where(p => (p.Status == PartyStatus.Dormant || p.Status == PartyStatus.Deceased) && p.RetentionReviewRaisedOn == null).OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public void Add(DataSubjectRequest request) => db.DataSubjectRequests.Add(request);
}

internal sealed class PartyRepository(PartyDbContext db) : IPartyRepository
{
    public async Task<IReadOnlyList<Guid>> MergedInto(Guid partyId, CancellationToken cancellationToken)
    {
        var found = new List<Guid>();
        var next = new List<Guid> { partyId };
        while (next.Count > 0)
        {
            next = await db.Parties.AsNoTracking().Where(p => p.MergedIntoId != null && next.Contains(p.MergedIntoId.Value) && !found.Contains(p.Id) && p.Id != partyId)
                .Select(p => p.Id).ToListAsync(cancellationToken);
            found.AddRange(next);
        }

        return found;
    }

    public async Task<DateOnly?> RegisteredOn(Guid id, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var at = await db.Parties.AsNoTracking().Where(p => p.Id == id).Select(p => (DateTimeOffset?)EF.Property<DateTimeOffset>(p, ModuleDbContext.CreatedAt)).FirstOrDefaultAsync(cancellationToken);
        return at is { } when ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(when, zone).DateTime) : null;
    }

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

    public void Add(DuplicateCandidate candidate) => db.DuplicateCandidates.Add(candidate);

    public void Add(DueDiligenceCheck check) => db.DueDiligenceChecks.Add(check);

    public async Task<IReadOnlyList<DueDiligenceCheck>> Checks(Guid partyId, CancellationToken cancellationToken) =>
        await db.DueDiligenceChecks.AsNoTracking().Where(c => c.PartyId == partyId).OrderBy(c => c.CheckedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DuplicateCandidateView>> Candidates(IndividualData person, CancellationToken cancellationToken)
    {
        var first = person.FirstNames.Trim().ToLower();
        var surname = person.Surname.Trim().ToLower();
        var matches = await db.Database.SqlQuery<Guid>($"""
            SELECT i.party_id AS "Value" FROM party.individual i JOIN party.party p ON p.tenant_id = i.tenant_id AND p.id = i.party_id
            WHERE lower(trim(i.first_names)) = {first} AND lower(trim(i.surname)) = {surname} AND i.birth_date = {person.BirthDate} AND p.merged_into_id IS NULL
            ORDER BY i.party_id LIMIT 10
            """).ToListAsync(cancellationToken);
        var found = await db.Parties.AsNoTracking().Include(p => p.Identifiers).Where(p => matches.Contains(p.Id)).ToListAsync(cancellationToken);
        return found.OrderBy(p => p.Id)
            .Select(p => new DuplicateCandidateView(p.Id, p.PartyNumber, p.DisplayName, p.Identifiers.Select(i => i.NumberLast4).FirstOrDefault(l => l != null)))
            .ToList();
    }
}

/// <summary>A broker's clients for its portal (BR-091): merged parties are followed to their survivor before paging, so each client appears once.</summary>
internal sealed class PartyPortalQueries(PartyDbContext db) : IPartyPortalQueries
{
    public async Task<IReadOnlyList<Domain.Parties.Party>> Clients(IReadOnlyCollection<Guid> partyIds, IReadOnlyCollection<string> registeredBy, Guid? only, Guid? afterId,
        int limit, CancellationToken cancellationToken)
    {
        var ids = partyIds.ToList();
        var registrars = registeredBy.ToList();
        var rows = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.Id) || (p.RegisteredBy != null && registrars.Contains(p.RegisteredBy)))
            .Select(p => new { p.Id, p.Status, p.MergedIntoId }).ToListAsync(cancellationToken);
        var clients = new HashSet<Guid>();
        var seen = rows.Select(r => r.Id).ToHashSet();
        while (rows.Count > 0)
        {
            clients.UnionWith(rows.Where(r => r.Status is not (PartyStatus.Merged or PartyStatus.Anonymised)).Select(r => r.Id));
            var survivors = rows.Where(r => r.Status == PartyStatus.Merged && r.MergedIntoId is { } s && seen.Add(s)).Select(r => r.MergedIntoId!.Value).ToList();
            rows = survivors.Count == 0
                ? []
                : await db.Parties.AsNoTracking().Where(p => survivors.Contains(p.Id)).Select(p => new { p.Id, p.Status, p.MergedIntoId }).ToListAsync(cancellationToken);
        }

        var page = clients.Where(id => (only is null || id == only) && (afterId is null || id.CompareTo(afterId.Value) > 0)).Order().Take(limit).ToList();
        return page.Count == 0 ? [] : await db.Parties.AsNoTracking().Where(p => page.Contains(p.Id)).OrderBy(p => p.Id).ToListAsync(cancellationToken);
    }
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
        DataSubjectRequestReceived e => new Contracts.DataSubjectRequestReceived(e.RequestId, e.RequestNumber, e.DueOn),
        PartyRetentionEnded e => new Contracts.PartyRetentionEnded(e.PartyId),
        PartyRegistered e => new Contracts.PartyRegistered(e.PartyId, e.Kind.ToString().ToUpperInvariant()),
        PartyDetailsChanged e => new Contracts.PartyDetailsChanged(e.PartyId, e.Part),
        PartyIdentifierAdded e => new Contracts.PartyIdentifierAdded(e.PartyId, e.IdentifierId),
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
        SearchLogHoldReleaseRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.SearchLogHoldBusinessRefType, e.HoldId,
            Contracts.PartyApprovals.LegalHoldReleaseType, e.RequestedBy, null, null,
            $"Release legal hold on the party search log ({(e.SearchedBy is null ? "whole log" : "one searcher's entries")}): {e.Reason}"),
        DueDiligenceStatusChanged e => new Contracts.DueDiligenceChanged(e.PartyId, e.Status),
        DueDiligenceReached e => e.Status switch
        {
            DueDiligenceCodes.Cleared => new Contracts.DueDiligenceCleared(e.PartyId, e.Status),
            DueDiligenceCodes.Referred => new Contracts.DueDiligenceReferred(e.PartyId, e.Status),
            DueDiligenceCodes.Blocked => new Contracts.DueDiligenceBlocked(e.PartyId, e.Status),
            DueDiligenceCodes.Expired => new Contracts.DueDiligenceExpired(e.PartyId, e.Status),
            _ => null,
        },

        // OQ-126, §4.3, §4.4: the reasons stay on the due diligence record; the approval names the party only.
        EnhancedDueDiligenceApprovalRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.DueDiligenceBusinessRefType, e.PartyId,
            Contracts.PartyApprovals.EnhancedDueDiligenceType, e.RequestedBy, null, null, $"Clear enhanced due diligence of party {e.PartyId}", null,
            Contracts.PartyPermissions.DueDiligenceApproveEnhanced),
        RiskClassOverrideRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.RiskOverrideBusinessRefType, e.PartyId,
            Contracts.PartyApprovals.RiskClassOverrideType, e.RequestedBy, null, null, $"Set the risk class of party {e.PartyId} to {e.RiskClass}", null,
            Contracts.PartyPermissions.DueDiligenceOverrideRisk),
        DueDiligenceUnblockRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.PartyApprovals.UnblockBusinessRefType, e.PartyId,
            Contracts.PartyApprovals.UnblockType, e.RequestedBy, null, null, $"Unblock the due diligence of party {e.PartyId}", null,
            Contracts.PartyPermissions.DueDiligenceUnblock),
        BankAccountVerificationWithdrawn e => new BuildingBlocks.Contracts.Approvals.ApprovalWithdrawn(Contracts.PartyApprovals.BankAccountBusinessRefType, e.BankAccountId,
            "The account was changed before it was verified."),
        _ => null,
    };
}
