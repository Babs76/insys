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

public sealed record ConsentGranted(Guid PartyId, string PurposeCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ConsentWithdrawn(Guid PartyId, string PurposeCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Entity ConsentRecord (party.consent_record): one grant or withdrawal, never changed after it is written. A
/// withdrawal is a new record with granted false and withdrawn_at set (OQ-055).
/// </summary>
public sealed class ConsentRecord : AggregateRoot<Guid>
{
    private ConsentRecord()
    {
    }

    public Guid PartyId { get; private set; }

    public string PurposeCode { get; private set; } = "";

    public bool Granted { get; private set; }

    public DateTimeOffset CapturedAt { get; private set; }

    public string CaptureChannel { get; private set; } = "";

    /// <summary>CaptureEvidence: where the consent statement or recording is kept.</summary>
    public string? EvidenceRef { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    internal static ConsentRecord Create(Guid partyId, string purposeCode, bool granted, string channel, string? evidence, DateTimeOffset now)
    {
        var record = new ConsentRecord
        {
            Id = Guid.CreateVersion7(),
            PartyId = partyId,
            PurposeCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(purposeCode), 20),
            Granted = granted,
            CapturedAt = now,
            CaptureChannel = Guard.MaxLength(Guard.NotNullOrWhiteSpace(channel), 15),
            EvidenceRef = evidence,
            WithdrawnAt = granted ? null : now,
        };
        record.Raise(granted ? new ConsentGranted(partyId, record.PurposeCode, now) : new ConsentWithdrawn(partyId, record.PurposeCode, now));
        return record;
    }
}

/// <summary>
/// The ConsentLedger aggregate: a party's consent records, append-only; the latest record per purpose is the current
/// consent (PTY-005).
/// </summary>
public sealed class ConsentLedger
{
    private readonly List<ConsentRecord> _records;

    public ConsentLedger(Guid partyId, IEnumerable<ConsentRecord> records)
    {
        PartyId = partyId;
        _records = records.Where(r => r.PartyId == partyId).ToList();
    }

    public Guid PartyId { get; }

    public IReadOnlyList<ConsentRecord> Records => _records;

    /// <summary>The current consent per purpose: the latest record.</summary>
    public IReadOnlyDictionary<string, ConsentRecord> Current =>
        _records.GroupBy(r => r.PurposeCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CapturedAt).ThenByDescending(r => r.Id).First(), StringComparer.Ordinal);

    public bool HasConsent(string purposeCode) => Current.TryGetValue(purposeCode, out var record) && record.Granted;

    /// <summary>Command GrantConsent. Granting what is already granted records nothing.</summary>
    public ConsentRecord? GrantConsent(string purposeCode, string channel, string? evidence, DateTimeOffset now) =>
        HasConsent(purposeCode) ? null : Append(ConsentRecord.Create(PartyId, purposeCode, true, channel, evidence, now));

    /// <summary>Command WithdrawConsent. Withdrawing what is not granted records nothing.</summary>
    public ConsentRecord? WithdrawConsent(string purposeCode, string channel, string? evidence, DateTimeOffset now) =>
        !HasConsent(purposeCode) ? null : Append(ConsentRecord.Create(PartyId, purposeCode, false, channel, evidence, now));

    private ConsentRecord Append(ConsentRecord record)
    {
        _records.Add(record);
        return record;
    }
}

/// <summary>
/// A possible duplicate (party.duplicate_candidate, PTY-004). Staff registering a party that matches an existing one may
/// assert it is new, with a reason (OQ-127): CONFIRMED_NEW records that assertion and who made it (ReviewedBy), not a
/// review finding. PartyAId is the party registered; PartyBId the existing match.
/// </summary>
public sealed class DuplicateCandidate : AggregateRoot<Guid>
{
    /// <summary>The registrar asserted the party is new despite the match (OQ-127): an assertion, not a review finding.</summary>
    public const string ConfirmedNew = "CONFIRMED_NEW";

    private DuplicateCandidate()
    {
    }

    public Guid PartyAId { get; private set; }

    public Guid PartyBId { get; private set; }

    public decimal Score { get; private set; }

    public string RuleCode { get; private set; } = "";

    public string Status { get; private set; } = "";

    public Guid? ReviewedBy { get; private set; }

    /// <summary>The registration confirmed new by the person who made it, over a close name and birth date match.</summary>
    public static DuplicateCandidate ConfirmedNewOver(Guid registered, Guid existing, string ruleCode, Guid reviewer) => new()
    {
        Id = Guid.CreateVersion7(),
        PartyAId = registered,
        PartyBId = existing,
        Score = 0.9m,
        RuleCode = Guard.MaxLength(ruleCode, 15),
        Status = ConfirmedNew,
        ReviewedBy = reviewer,
    };
}
