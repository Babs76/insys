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
