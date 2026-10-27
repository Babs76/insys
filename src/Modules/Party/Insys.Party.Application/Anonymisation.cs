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
using Insys.Audit.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.Documents.Contracts;

namespace Insys.Party.Application;

/// <summary>
/// One thing that keeps a party's data (D-34). <c>reason</c> is OPEN (the record has not ended or closed), IN_RETENTION
/// (inside the tenant's period until <c>until</c>, exclusive), LEGAL_HOLD (a hold in force on the record or one of its
/// documents) or NO_RETENTION_POLICY (the tenant has no period in force for the record's kind, so its end cannot be known).
/// </summary>
/// <param name="RecordType">LIFE_POLICY, LIFE_CLAIM, GENERAL_POLICY, GENERAL_CLAIM, HEALTH_MEMBERSHIP, HEALTH_CLAIM, PAYMENT or AUDIT_EVIDENCE.</param>
/// <param name="RecordId">The record; for AUDIT_EVIDENCE, the party.</param>
/// <param name="Retention">The period that applies: POLICY, CLAIM or PAYMENT (the record retention policies), or AUDIT (the longest audit class); null for a hold.</param>
/// <param name="Until">For IN_RETENTION, the first business date the record no longer keeps the party's data.</param>
/// <param name="HoldId">For LEGAL_HOLD, the hold (null for a document flagged as held without a hold row).</param>
/// <param name="DocumentId">For LEGAL_HOLD on a document, the document.</param>
public sealed record AnonymisationBlock(string Reason, string RecordType, Guid RecordId, string? Retention = null, DateOnly? Until = null, Guid? HoldId = null,
    Guid? DocumentId = null);

/// <summary>A retention period: years, months and days.</summary>
public sealed record RetentionLength(int Years, int Months, int Days)
{
    /// <summary>The first day after the period that starts on <paramref name="start"/>.</summary>
    public DateOnly After(DateOnly start) => start.AddYears(Years).AddMonths(Months).AddDays(Days);
}

/// <summary>
/// party.cfg_record_retention_policies in force for the current tenant (OQ-120): the tenant's own rows replace the
/// platform's (the 7-year working default); across jurisdictions the longest wins. Null when none is in force.
/// </summary>
public interface IRecordRetentionPolicies
{
    Task<RetentionLength?> Period(string recordKind, DateOnly day, CancellationToken cancellationToken);
}

public static class AnonymisationBlockReasons
{
    public const string Open = "OPEN";
    public const string InRetention = "IN_RETENTION";
    public const string LegalHold = "LEGAL_HOLD";
    public const string NoRetentionPolicy = "NO_RETENTION_POLICY";
}

/// <summary>
/// Whether retention has ended for a party (D-34, OQ-120, DBA-022). Each record linked to the party in a module that keeps
/// them (policies and memberships, claims, payments), its own or a merged party's, is open until it reaches a final
/// status, and then kept for the tenant's record retention policy for its kind (party.cfg_record_retention_policies;
/// working default 7 years) from the business date it reached it. The audit evidence about the party is kept for the
/// longest audit class from its latest event. A legal hold in force on any of them blocks too. With no period in force
/// for a kind, its records block: nothing is anonymised early.
/// </summary>
public sealed class AnonymisationCheck(IEnumerable<IPartyLinkedRecords> sources, IPartyRepository parties, IRecordRetentionPolicies policies, IRecordHolds holds,
    IAuditRetention auditRetention, IAuditedTargets audited, ITenantContext tenant, IBusinessDate businessDate)
{
    public const string AuditEvidence = "AUDIT_EVIDENCE";
    public const string AuditRetention = "AUDIT";

    public async Task<IReadOnlyList<AnonymisationBlock>> Blocks(Guid partyId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("A party is anonymised in a tenant.");
        var today = businessDate.Today;
        var zone = businessDate.Zone.Id;
        var partyIds = new[] { partyId }.Concat(await parties.MergedInto(partyId, cancellationToken)).ToList();

        var records = new List<PartyLinkedRecord>();
        foreach (var source in sources)
        {
            foreach (var id in partyIds)
            {
                records.AddRange(await source.For(tenantId, id, zone, cancellationToken));
            }
        }

        records = records.DistinctBy(r => (r.RecordType, r.RecordId)).OrderBy(r => r.RecordType, StringComparer.Ordinal).ThenBy(r => r.RecordId).ToList();
        var periods = new Dictionary<string, RetentionLength?>(StringComparer.Ordinal);
        var blocks = new List<AnonymisationBlock>();
        foreach (var record in records)
        {
            if (record.ClosedOn is not { } closed)
            {
                blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.Open, record.RecordType, record.RecordId, record.Retention));
                continue;
            }

            if (!periods.TryGetValue(record.Retention, out var period))
            {
                period = periods[record.Retention] = await policies.Period(record.Retention, today, cancellationToken);
            }

            if (period is null)
            {
                blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.NoRetentionPolicy, record.RecordType, record.RecordId, record.Retention));
            }
            else if (period.After(closed) is var until && until > today)
            {
                blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.InRetention, record.RecordType, record.RecordId, record.Retention, until));
            }
        }

        // Audit evidence about the party: which class an event belongs to is not recorded, so the longest class applies.
        foreach (var id in partyIds)
        {
            if (await audited.LatestAbout(nameof(Domain.Parties.Party), id.ToString(), cancellationToken) is not { } latest)
            {
                continue;
            }

            var period = await LongestAuditClass(today, cancellationToken);
            if (period is null)
            {
                blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.NoRetentionPolicy, AuditEvidence, id, AuditRetention));
            }
            else if (period.After(businessDate.DateOf(latest)) is var until && until > today)
            {
                blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.InRetention, AuditEvidence, id, AuditRetention, until));
            }
        }

        foreach (var hold in await holds.Holds([.. partyIds, .. records.Select(r => r.RecordId)], cancellationToken))
        {
            blocks.Add(new AnonymisationBlock(AnonymisationBlockReasons.LegalHold, hold.RecordType, hold.RecordId, HoldId: hold.HoldId, DocumentId: hold.DocumentId));
        }

        return blocks;
    }

    private async Task<RetentionLength?> LongestAuditClass(DateOnly today, CancellationToken cancellationToken)
    {
        var periods = new List<RetentionLength>();
        foreach (var eventClass in RetentionClasses.All)
        {
            if (await auditRetention.Period(eventClass, today, cancellationToken) is { } period)
            {
                periods.Add(new RetentionLength(period.Years, period.Months, period.Days));
            }
        }

        return periods.OrderByDescending(p => p.After(today)).FirstOrDefault();
    }
}
