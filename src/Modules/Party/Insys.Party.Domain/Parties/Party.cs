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

public enum PartyKind
{
    Individual,
    Organisation,
}

/// <summary>States from domain-model.json (party › Party), stored in party.status_code.</summary>
public enum PartyStatus
{
    Active,
    Dormant,
    Deceased,
    Merged,
    Anonymised,
}

public enum PartyTrigger
{
    NoActivity,
    NewActivity,
    DeathRecorded,
    MergeParties,
    RetentionEnded,
}

/// <summary>PersonName and the other details of an individual (party.individual).</summary>
public sealed record IndividualData(string? TitleCode, string FirstNames, string Surname, DateOnly BirthDate, string? GenderCode, string? MaritalStatusCode,
    string? Nationality, string? PreferredLanguage);

/// <summary>A portal registration matched an existing party: staff link the two or confirm a new one (external-user access §6).</summary>
public sealed record PartyMatchReviewRequested(Guid PartyId, Guid MatchedPartyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Staff proposed how to resolve a matched portal registration; a second person decides it in Workflow (SRS-002).</summary>
public sealed record PartyMatchResolved(Guid PartyId, string Resolution, Guid? ExistingPartyId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PartyMatchResolutionProposed(Guid PartyId, string Resolution, Guid ExistingPartyId, Guid RequestedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The details of an organisation (party.organisation).</summary>
public sealed record OrganisationData(string LegalName, string? TradingName, string LegalFormCode, string? RegistrationNumber, string? IndustryCode);

/// <summary>A postal address (party.address).</summary>
public sealed record AddressData(string AddressType, string Line1, string? Line2, string City, string? Subdivision, string? PostalCode, string Country, DateOnly ValidFrom);

public sealed record PartyRegistered(Guid PartyId, PartyKind Kind, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Names which part changed, never the values (INT-007).</summary>
public sealed record PartyDetailsChanged(Guid PartyId, string Part, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PartiesMerged(Guid SurvivorId, Guid MergedId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PartyAnonymised(Guid PartyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A party's bank account was added (superseding <paramref name="SupersededId"/>, a pending account whose verification is
/// withdrawn), verified (superseding the previous active account) or rejected. Ids only, never account details.
/// </summary>
public sealed record PartyBankAccountChanged(Guid PartyId, Guid BankAccountId, Guid? SupersededId, string Status, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Asks Workflow for a second person's verification of the account (BANK_ACCOUNT_VERIFICATION): its party, id and keyed hash only.</summary>
public sealed record BankAccountVerificationRequested(Guid PartyId, Guid BankAccountId, Guid RequestedBy, string Last4, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The account changed while its verification was pending: that approval is withdrawn.</summary>
public sealed record BankAccountVerificationWithdrawn(Guid PartyId, Guid BankAccountId, DateTimeOffset OccurredAt) : IDomainEvent;

public enum BankAccountStatus
{
    PendingVerification,
    Active,
    Superseded,
    Rejected,
}

/// <summary>
/// Entity BankAccount (party.bank_account, V0018; a gap in the original specification): the account number is
/// encrypted, with a keyed hash and its last four digits. A change is a new account; the previous one is superseded
/// once the new one is verified, never changed.
/// </summary>
public sealed class BankAccount : Entity<Guid>, ITenantScoped
{
    private BankAccount()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public byte[] AccountNumberEnc { get; private set; } = [];

    public string AccountNumberHash { get; private set; } = "";

    public string AccountNumberLast4 { get; private set; } = "";

    public string BankCode { get; private set; } = "";

    public string? BranchCode { get; private set; }

    public string AccountHolderName { get; private set; } = "";

    /// <summary>CURRENT, SAVINGS, TRANSMISSION or OTHER.</summary>
    public string AccountType { get; private set; } = "";

    public string Currency { get; private set; } = "";

    public DateTimeOffset? VerifiedAt { get; private set; }

    public string? VerificationRef { get; private set; }

    public Guid? VerifiedBy { get; private set; }

    public BankAccountStatus Status { get; private set; }

    public Guid? SupersededById { get; private set; }

    /// <summary>The user who added the account: the maker of its verification.</summary>
    public Guid? RequestedBy { get; private set; }

    /// <summary>The Workflow approval that decided the verification.</summary>
    public Guid? ApprovalId { get; private set; }

    public long RowVersion { get; private set; }

    /// <summary>What the verification approves: the party, the account and its keyed hash; never the number.</summary>
    public string SubjectHash => ApprovalSubject.Hash("BANK_ACCOUNT", PartyId, Id, AccountNumberHash);

    internal static BankAccount Create(Guid partyId, BankAccountData data, Guid requestedBy) => new()
    {
        RequestedBy = requestedBy,
        Id = Guid.CreateVersion7(), PartyId = partyId, AccountNumberEnc = data.Encrypted, AccountNumberHash = data.Hash, AccountNumberLast4 = data.Last4,
        BankCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.BankCode), 20), BranchCode = data.BranchCode,
        AccountHolderName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.AccountHolderName), 140), AccountType = data.AccountType,
        Currency = Guard.MaxLength(data.Currency, 3), Status = BankAccountStatus.PendingVerification,
    };

    internal void Verify(Guid approvalId, Guid verifiedBy, DateTimeOffset now)
    {
        VerifiedAt = now;
        VerificationRef = Guard.MaxLength("WF-" + approvalId.ToString("N"), 60);
        VerifiedBy = verifiedBy;
        ApprovalId = approvalId;
        Status = BankAccountStatus.Active;
    }

    internal void Reject(Guid approvalId)
    {
        ApprovalId = approvalId;
        Status = BankAccountStatus.Rejected;
    }

    internal void Supersede(Guid by)
    {
        Status = BankAccountStatus.Superseded;
        SupersededById = by;
    }
}

/// <summary>A bank account as protected by the application: the encrypted number, its keyed hash and last four digits, and the rest.</summary>
public sealed record BankAccountData(byte[] Encrypted, string Hash, string Last4, string BankCode, string? BranchCode, string AccountHolderName, string AccountType,
    string Currency);

public sealed class Individual : Entity<Guid>, ITenantScoped
{
    private Individual()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string? TitleCode { get; private set; }

    public string FirstNames { get; private set; } = "";

    public string Surname { get; private set; } = "";

    public DateOnly BirthDate { get; private set; }

    public string? GenderCode { get; private set; }

    public string? MaritalStatusCode { get; private set; }

    public string? Nationality { get; private set; }

    public string? PreferredLanguage { get; private set; }

    public DateOnly? DeceasedDate { get; private set; }

    public long RowVersion { get; private set; }

    internal static Individual Create(Guid partyId, IndividualData data)
    {
        var individual = new Individual { Id = partyId, PartyId = partyId };
        individual.Apply(data);
        return individual;
    }

    internal void Apply(IndividualData data)
    {
        TitleCode = data.TitleCode;
        FirstNames = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.FirstNames), 100);
        Surname = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.Surname), 100);
        BirthDate = data.BirthDate;
        GenderCode = data.GenderCode;
        MaritalStatusCode = data.MaritalStatusCode;
        Nationality = data.Nationality;
        PreferredLanguage = data.PreferredLanguage;
    }

    internal void RecordDeath(DateOnly date) => DeceasedDate = date;

    internal void ChangePreferredLanguage(string? language) => PreferredLanguage = language;
}

public sealed class Organisation : Entity<Guid>, ITenantScoped
{
    private Organisation()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string LegalName { get; private set; } = "";

    public string? TradingName { get; private set; }

    public string LegalFormCode { get; private set; } = "";

    public string? RegistrationNumber { get; private set; }

    public string? IndustryCode { get; private set; }

    public long RowVersion { get; private set; }

    internal static Organisation Create(Guid partyId, OrganisationData data)
    {
        var organisation = new Organisation { Id = partyId, PartyId = partyId };
        organisation.Apply(data);
        return organisation;
    }

    internal void Apply(OrganisationData data)
    {
        LegalName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.LegalName), 150);
        TradingName = data.TradingName;
        LegalFormCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(data.LegalFormCode), 10);
        RegistrationNumber = data.RegistrationNumber;
        IndustryCode = data.IndustryCode;
    }
}

public sealed class Address : Entity<Guid>, ITenantScoped
{
    private Address()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string AddressType { get; private set; } = "";

    public string Line1 { get; private set; } = "";

    public string? Line2 { get; private set; }

    public string City { get; private set; } = "";

    public string? Subdivision { get; private set; }

    public string? PostalCode { get; private set; }

    public string Country { get; private set; } = "";

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public long RowVersion { get; private set; }

    internal static Address Create(Guid partyId, AddressData data) => new()
    {
        Id = Guid.CreateVersion7(),
        PartyId = partyId,
        AddressType = data.AddressType,
        Line1 = data.Line1,
        Line2 = data.Line2,
        City = data.City,
        Subdivision = data.Subdivision,
        PostalCode = data.PostalCode,
        Country = data.Country,
        ValidFrom = data.ValidFrom,
    };

    internal void End(DateOnly validTo) => ValidTo = validTo;
}

public sealed class ContactPoint : Entity<Guid>, ITenantScoped
{
    private ContactPoint()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string ChannelType { get; private set; } = "";

    public string Value { get; private set; } = "";

    public bool IsPrimary { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public long RowVersion { get; private set; }

    internal static ContactPoint Create(Guid partyId, string channelType, string value, bool isPrimary) =>
        new() { Id = Guid.CreateVersion7(), PartyId = partyId, ChannelType = channelType, Value = value, IsPrimary = isPrimary };

    internal void Demote() => IsPrimary = false;
}

/// <summary>Entity PartyIdentifier: the number is stored encrypted, with a keyed hash for lookup (DBA-014); it is never returned.</summary>
public sealed class PartyIdentifier : Entity<Guid>, ITenantScoped
{
    private PartyIdentifier()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string DocType { get; private set; } = "";

    public string Country { get; private set; } = "";

    public byte[] NumberEnc { get; private set; } = [];

    public string NumberHash { get; private set; } = "";

    public DateTimeOffset? VerifiedAt { get; private set; }

    public string? VerificationRef { get; private set; }

    public long RowVersion { get; private set; }

    internal static PartyIdentifier Create(Guid partyId, string docType, string country, byte[] encrypted, string hash) =>
        new() { Id = Guid.CreateVersion7(), PartyId = partyId, DocType = docType, Country = country, NumberEnc = encrypted, NumberHash = hash };

    internal void Verify(string reference, DateTimeOffset now)
    {
        VerifiedAt = now;
        VerificationRef = reference;
    }
}

public sealed class PartyRelationship : Entity<Guid>, ITenantScoped
{
    private PartyRelationship()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid FromPartyId { get; private set; }

    public Guid ToPartyId { get; private set; }

    public string RelationshipCode { get; private set; } = "";

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public long RowVersion { get; private set; }

    internal static PartyRelationship Create(Guid from, Guid to, string code, DateOnly validFrom) =>
        new() { Id = Guid.CreateVersion7(), FromPartyId = from, ToPartyId = to, RelationshipCode = code, ValidFrom = validFrom };
}

/// <summary>Entity PartyRole: a party holds many roles (PTY-002), each in a domain and a context (a policy, a claim).</summary>
public sealed class PartyRole : Entity<Guid>, ITenantScoped
{
    private PartyRole()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PartyId { get; private set; }

    public string RoleCode { get; private set; } = "";

    public string Domain { get; private set; } = "";

    public string ContextType { get; private set; } = "";

    public Guid ContextId { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public long RowVersion { get; private set; }

    internal static PartyRole Create(Guid partyId, string roleCode, string domain, string contextType, Guid contextId, DateOnly validFrom) =>
        new() { Id = Guid.CreateVersion7(), PartyId = partyId, RoleCode = roleCode, Domain = domain, ContextType = contextType, ContextId = contextId, ValidFrom = validFrom };
}

/// <summary>party.merge_history: who merged which party into which, why, and the merged party as it was (PTY-004).</summary>
public sealed class MergeHistory : Entity<Guid>, ITenantScoped
{
    private MergeHistory()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid SurvivorId { get; private set; }

    public Guid MergedId { get; private set; }

    public DateTimeOffset MergedAt { get; private set; }

    public Guid MergedBy { get; private set; }

    public string Reason { get; private set; } = "";

    /// <summary>JSON: the merged party's kind, name and status, and its role and relationship counts.</summary>
    public string Snapshot { get; private set; } = "{}";

    internal static MergeHistory Create(Guid survivor, Guid merged, Guid by, string reason, string snapshot, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), SurvivorId = survivor, MergedId = merged, MergedBy = by, Reason = reason, Snapshot = snapshot, MergedAt = now };
}

/// <summary>
/// The Party aggregate: a person or organisation (PTY-001) whose kind never changes, holding many roles (PTY-002).
/// A merged party is read-only and points to its survivor (PTY-004); an anonymised one keeps no personal data.
/// </summary>
public sealed class Party : AggregateRoot<Guid>
{
    public static readonly StateMachine<PartyStatus, PartyTrigger> Lifecycle = new StateMachine<PartyStatus, PartyTrigger>()
        .Allow(PartyStatus.Active, PartyTrigger.NoActivity, PartyStatus.Dormant)
        .Allow(PartyStatus.Dormant, PartyTrigger.NewActivity, PartyStatus.Active)
        .Allow(PartyStatus.Active, PartyTrigger.DeathRecorded, PartyStatus.Deceased)
        .Allow(PartyStatus.Active, PartyTrigger.MergeParties, PartyStatus.Merged)
        .Allow(PartyStatus.Dormant, PartyTrigger.MergeParties, PartyStatus.Merged)
        .Allow(PartyStatus.Deceased, PartyTrigger.RetentionEnded, PartyStatus.Anonymised)
        .Allow(PartyStatus.Dormant, PartyTrigger.RetentionEnded, PartyStatus.Anonymised);

    private readonly List<Address> _addresses = [];
    private readonly List<ContactPoint> _contactPoints = [];
    private readonly List<PartyIdentifier> _identifiers = [];
    private readonly List<BankAccount> _bankAccounts = [];
    private readonly List<PartyRelationship> _relationships = [];
    private readonly List<PartyRole> _roles = [];
    private readonly List<MergeHistory> _merges = [];

    private Party()
    {
    }

    public PartyKind Kind { get; private set; }

    public string DisplayName { get; private set; } = "";

    public PartyStatus Status { get; private set; }

    public Guid? MergedIntoId { get; private set; }

    /// <summary>PENDING_REVIEW for a portal registration that matched an existing party, until staff resolve it (external-user access §6).</summary>
    public string? ReviewStatus { get; private set; }

    /// <summary>Who registered the party through a portal ("INTERMEDIARY:BRK-1"); null for staff registrations.</summary>
    public string? RegisteredBy { get; private set; }

    /// <summary>
    /// A registration through a portal: who made it, and, when it matched an existing party, a staff review instead of a
    /// duplicate error, so the portal user cannot learn that someone is already a client (POPIA).
    /// </summary>
    public void RegisteredThroughPortal(string registeredBy, Guid? matchedPartyId, DateTimeOffset now)
    {
        RegisteredBy = Guard.MaxLength(registeredBy, 40);
        if (matchedPartyId is { } matched)
        {
            ReviewStatus = PendingReview;
            MatchedPartyId = matched;
            Raise(new PartyMatchReviewRequested(Id, matched, now));
        }

        MarkChanged();
    }

    public const string PendingReview = "PENDING_REVIEW";

    /// <summary>The existing party a portal registration matched; null for registrations before V0046.</summary>
    public Guid? MatchedPartyId { get; private set; }

    /// <summary>LINK or NEW: the proposed, then approved, resolution of a matched portal registration.</summary>
    public string? MatchResolution { get; private set; }

    public Guid? ResolutionRequestedBy { get; private set; }

    /// <summary>
    /// Command ResolvePartyMatch (external-user access §6): staff propose to link this registration to the party it
    /// matched, or to confirm it as a new party. Nothing changes until a second person approves the proposal.
    /// </summary>
    public Result ProposeMatchResolution(bool link, Guid? existingPartyId, Guid requestedBy, DateTimeOffset now)
    {
        if (ReviewStatus != PendingReview)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a portal registration waiting for review is resolved.");
        }

        var existing = MatchedPartyId ?? existingPartyId;
        if (existing is not { } target || (MatchedPartyId is { } matched && existingPartyId is { } named && named != matched))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A link is to the party the registration matched.");
        }

        (ReviewStatus, MatchResolution, ResolutionRequestedBy, MatchedPartyId) = ("PROPOSED", link ? "LINK" : "NEW", requestedBy, target);
        Raise(new PartyMatchResolutionProposed(Id, MatchResolution, target, requestedBy, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Workflow's decision on the proposal. Approved: a link is applied by merging this registration into the existing
    /// party (the caller does that, as it needs both parties), and a new party is simply confirmed. Rejected: back to review.
    /// </summary>
    public Result DecideMatchResolution(bool approved, DateTimeOffset now)
    {
        if (ReviewStatus != "PROPOSED")
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No resolution is waiting for a decision.");
        }

        if (approved)
        {
            ReviewStatus = MatchResolution == "LINK" ? "LINKED" : "CONFIRMED_NEW";
            Raise(new PartyMatchResolved(Id, MatchResolution!, MatchResolution == "LINK" ? MatchedPartyId : null, now));
        }
        else
        {
            (ReviewStatus, MatchResolution, ResolutionRequestedBy) = (PendingReview, null, null);
        }

        MarkChanged();
        return Result.Success();
    }

    public Individual? Individual { get; private set; }

    public Organisation? Organisation { get; private set; }

    public IReadOnlyList<Address> Addresses => _addresses;

    public IReadOnlyList<ContactPoint> ContactPoints => _contactPoints;

    public IReadOnlyList<PartyIdentifier> Identifiers => _identifiers;

    public IReadOnlyList<BankAccount> BankAccounts => _bankAccounts;

    /// <summary>
    /// Command AddBankAccount: a new account, pending verification by a second person in Workflow (ADR-013). The party's
    /// active account in the same currency stays active (and payable) until the new one is verified (D-3); if another
    /// account in the currency was still pending, its verification is withdrawn and it is superseded by the new one.
    /// </summary>
    public Result<Guid> AddBankAccount(BankAccountData data, Guid requestedBy, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var account = BankAccount.Create(Id, data, Guard.NotEmpty(requestedBy));
        var pending = _bankAccounts.SingleOrDefault(a => a.Currency == data.Currency && a.Status == BankAccountStatus.PendingVerification);
        if (pending is not null)
        {
            Raise(new BankAccountVerificationWithdrawn(Id, pending.Id, now));
            pending.Supersede(account.Id);
        }

        _bankAccounts.Add(account);
        Raise(new PartyBankAccountChanged(Id, account.Id, pending?.Id, "PENDING_VERIFICATION", now));
        Raise(new BankAccountVerificationRequested(Id, account.Id, requestedBy, account.AccountNumberLast4, account.SubjectHash, now));
        return account.Id;
    }

    /// <summary>
    /// Workflow's decision on the verification: approved by a second person, the account is active and payable and the
    /// party's previous active account in the currency is superseded by it; not approved (rejected, expired or withdrawn),
    /// it is rejected and never activated, and the previous account stays active. A decision for another subject (an
    /// account since changed) does not apply. With no checker (expired or withdrawn), maker ≠ checker does not arise.
    /// </summary>
    public Result DecideBankAccountVerification(Guid bankAccountId, bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (_bankAccounts.SingleOrDefault(a => a.Id == bankAccountId) is not { Status: BankAccountStatus.PendingVerification } account)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an account pending verification is verified.");
        }

        if (subjectHash != account.SubjectHash)
        {
            return Error.Conflict(PartyErrorCodes.BankAccountSubjectChanged, "The approval was for another account.");
        }

        if (checkerId != Guid.Empty && checkerId == account.RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(account.RequestedBy.ToString()!, checkerId.ToString());
        }

        if (approved && checkerId == Guid.Empty)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A verification is approved by a checker.");
        }

        Guid? superseded = null;
        if (approved)
        {
            if (_bankAccounts.SingleOrDefault(a => a.Id != account.Id && a.Currency == account.Currency && a.Status == BankAccountStatus.Active) is { } previous)
            {
                previous.Supersede(account.Id);
                superseded = previous.Id;
            }

            account.Verify(approvalId, checkerId, now);
        }
        else
        {
            account.Reject(approvalId);
        }

        Raise(new PartyBankAccountChanged(Id, account.Id, superseded, approved ? "ACTIVE" : "REJECTED", now));
        return Result.Success();
    }

    /// <summary>
    /// Whether that approval already verified the account (it became active, and may have been superseded since): a
    /// redelivery changes nothing.
    /// </summary>
    public bool BankAccountVerificationApplied(Guid bankAccountId, Guid approvalId) =>
        _bankAccounts.Any(a => a.Id == bankAccountId && a.ApprovalId == approvalId && a.VerifiedAt is not null);

    /// <summary>
    /// Whether an approval's ending (not granted) is already applied to the account, so a redelivery changes nothing: it
    /// was rejected by that approval, or this module withdrew its verification when another account replaced it before
    /// it was verified (superseded without ever being active), which is already its defined state.
    /// </summary>
    public bool BankAccountEndingApplied(Guid bankAccountId, Guid approvalId) =>
        _bankAccounts.SingleOrDefault(a => a.Id == bankAccountId) is { } account
        && ((account.Status == BankAccountStatus.Rejected && account.ApprovalId == approvalId)
            || (account.Status == BankAccountStatus.Superseded && account.VerifiedAt is null));

    public IReadOnlyList<PartyRelationship> Relationships => _relationships;

    public IReadOnlyList<PartyRole> Roles => _roles;

    public IReadOnlyList<MergeHistory> Merges => _merges;

    public static Party RegisterIndividual(Guid id, IndividualData data, DateTimeOffset now)
    {
        var party = new Party { Id = Guard.NotEmpty(id), Kind = PartyKind.Individual, Status = PartyStatus.Active, Individual = Individual.Create(id, data) };
        party.DisplayName = Name(data);
        party.Raise(new PartyRegistered(id, PartyKind.Individual, now));
        party.MarkChanged();
        return party;
    }

    public static Party RegisterOrganisation(Guid id, OrganisationData data, DateTimeOffset now)
    {
        var party = new Party { Id = Guard.NotEmpty(id), Kind = PartyKind.Organisation, Status = PartyStatus.Active, Organisation = Organisation.Create(id, data) };
        party.DisplayName = data.TradingName ?? data.LegalName;
        party.Raise(new PartyRegistered(id, PartyKind.Organisation, now));
        party.MarkChanged();
        return party;
    }

    /// <summary>Command UpdateDetails for an individual. The kind never changes (PTY-001).</summary>
    public Result UpdateDetails(IndividualData data, DateTimeOffset now)
    {
        var allowed = Changeable(PartyKind.Individual);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Individual!.Apply(data);
        DisplayName = Name(data);
        return Changed("details", now);
    }

    /// <summary>
    /// Command ChangePreferredLanguage for an individual (docs/specs/addenda/localisation.md §7, OQ-068); null clears it.
    /// Organisations have no preferred language in the schema.
    /// </summary>
    public Result ChangePreferredLanguage(string? language, DateTimeOffset now)
    {
        var allowed = Changeable(PartyKind.Individual);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Individual!.ChangePreferredLanguage(language);
        return Changed("preferred_language", now);
    }

    /// <summary>Command UpdateDetails for an organisation.</summary>
    public Result UpdateDetails(OrganisationData data, DateTimeOffset now)
    {
        var allowed = Changeable(PartyKind.Organisation);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Organisation!.Apply(data);
        DisplayName = data.TradingName ?? data.LegalName;
        return Changed("details", now);
    }

    /// <summary>UpdateDetails: a new address of a type ends the current one of that type the day before.</summary>
    public Result AddAddress(AddressData data, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        foreach (var current in _addresses.Where(a => a.AddressType == data.AddressType && a.ValidTo is null && a.ValidFrom < data.ValidFrom))
        {
            current.End(data.ValidFrom);
        }

        _addresses.Add(Address.Create(Id, data));
        return Changed("address", now);
    }

    /// <summary>UpdateDetails: a contact point; a new primary one demotes the previous primary of its channel.</summary>
    public Result AddContactPoint(string channelType, string value, bool isPrimary, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (isPrimary)
        {
            foreach (var primary in _contactPoints.Where(c => c.ChannelType == channelType && c.IsPrimary))
            {
                primary.Demote();
            }
        }

        _contactPoints.Add(ContactPoint.Create(Id, channelType, Guard.MaxLength(Guard.NotNullOrWhiteSpace(value), 200), isPrimary));
        return Changed("contact", now);
    }

    /// <summary>Command AddIdentifier. The number arrives encrypted with its keyed hash; uniqueness per type and country is checked by the caller and the database.</summary>
    public Result<Guid> AddIdentifier(string docType, string country, byte[] encrypted, string hash, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        if (_identifiers.Any(i => i.DocType == docType && i.Country == country))
        {
            return Error.Conflict(PartyErrorCodes.IdentifierInUse, $"The party already has a {docType} identifier for {country}.");
        }

        var identifier = PartyIdentifier.Create(Id, docType, country, encrypted, hash);
        _identifiers.Add(identifier);
        var changed = Changed("identifier", now);
        return changed.IsFailure ? changed.Error : identifier.Id;
    }

    public Result VerifyIdentifier(Guid identifierId, string reference, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var identifier = _identifiers.SingleOrDefault(i => i.Id == identifierId);
        if (identifier is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party has no such identifier.");
        }

        identifier.Verify(Guard.MaxLength(Guard.NotNullOrWhiteSpace(reference), 80), now);
        return Changed("identifier", now);
    }

    /// <summary>Command RecordRelationship, from this party to another.</summary>
    public Result RecordRelationship(Guid toPartyId, string relationshipCode, DateOnly validFrom, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (toPartyId == Id)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A party cannot be related to itself.");
        }

        _relationships.Add(PartyRelationship.Create(Id, toPartyId, relationshipCode, validFrom));
        return Changed("relationship", now);
    }

    /// <summary>Command AssignRole (PTY-002). The role type must allow the party's kind and the domain (checked by the caller).</summary>
    public Result AssignRole(string roleCode, string domain, string contextType, Guid contextId, DateOnly validFrom, DateTimeOffset now)
    {
        var allowed = Changeable(null);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (_roles.Any(r => r.RoleCode == roleCode && r.ContextType == contextType && r.ContextId == contextId && r.ValidTo is null))
        {
            return Error.Conflict(PartyErrorCodes.RoleAlreadyHeld, $"The party already holds {roleCode} in this context.");
        }

        _roles.Add(PartyRole.Create(Id, roleCode, domain, contextType, contextId, validFrom));
        return Changed("role", now);
    }

    /// <summary>Transition "Death recorded" (individuals only).</summary>
    public Result RecordDeath(DateOnly deceasedDate, DateOnly today, DateTimeOffset now)
    {
        if (Kind != PartyKind.Individual || deceasedDate > today)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A death is recorded for an individual, on a date not in the future.");
        }

        var result = Fire(PartyTrigger.DeathRecorded);
        if (result.IsSuccess)
        {
            Individual!.RecordDeath(deceasedDate);
            Raise(new PartyDetailsChanged(Id, "death", now));
        }

        return result;
    }

    /// <summary>
    /// Command MergeParties: this party is merged into <paramref name="survivor"/>, keeps an auditable history and becomes
    /// read-only (PTY-004). TODO(spec): moving roles and relationships to the survivor is not specified (OQ-054).
    /// </summary>
    public Result MergeInto(Party survivor, Guid mergedBy, string reason, string snapshot, DateTimeOffset now)
    {
        if (survivor.Id == Id || survivor.Kind != Kind || survivor.Status is PartyStatus.Merged or PartyStatus.Anonymised)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A party is merged into another active party of the same kind.");
        }

        var result = Fire(PartyTrigger.MergeParties);
        if (result.IsSuccess)
        {
            MergedIntoId = survivor.Id;
            _merges.Add(MergeHistory.Create(survivor.Id, Id, mergedBy, Guard.NotNullOrWhiteSpace(reason), snapshot, now));
            Raise(new PartiesMerged(survivor.Id, Id, now));
        }

        return result;
    }

    /// <summary>
    /// Command AnonymiseParty (transition "Retention ended"): personal data is replaced or removed; the party, its
    /// roles and relationships remain for the records that refer to them.
    /// </summary>
    public Result AnonymiseParty(DateTimeOffset now)
    {
        var result = Fire(PartyTrigger.RetentionEnded);
        if (result.IsFailure)
        {
            return result;
        }

        DisplayName = "Anonymised";
        Individual?.Apply(new IndividualData(null, "Anonymised", "Anonymised", new DateOnly(1900, 1, 1), null, null, null, null));
        Organisation?.Apply(new OrganisationData("Anonymised", null, Organisation.LegalFormCode, null, null));
        _addresses.Clear();
        _contactPoints.Clear();
        _identifiers.Clear();
        Raise(new PartyAnonymised(Id, now));
        return result;
    }

    private Result Changeable(PartyKind? kind)
    {
        if (Status is PartyStatus.Merged or PartyStatus.Anonymised)
        {
            return Error.Conflict(PartyErrorCodes.PartyReadOnly,
                Status == PartyStatus.Merged ? $"The party was merged into {MergedIntoId}; change the survivor (PTY-004)." : "The party is anonymised.");
        }

        return kind is { } k && k != Kind
            ? Error.Validation(PartyErrorCodes.KindCannotChange, $"The party is an {Kind.ToString().ToLowerInvariant()}; its kind never changes (PTY-001).")
            : Result.Success();
    }

    private Result Changed(string part, DateTimeOffset now)
    {
        Raise(new PartyDetailsChanged(Id, part, now));
        MarkChanged();
        return Result.Success();
    }

    private Result Fire(PartyTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }

    private static string Name(IndividualData data) => $"{data.FirstNames} {data.Surname}";
}

public static class PartyErrorCodes
{
    [ErrorCode(409, "error.party.bank_account_subject_changed")]
    public const string BankAccountSubjectChanged = "BANK_ACCOUNT_SUBJECT_CHANGED";

    [ErrorCode(409, "error.party.read_only")]
    public const string PartyReadOnly = "PARTY_READ_ONLY";

    [ErrorCode(422, "error.party.kind_cannot_change")]
    public const string KindCannotChange = "PARTY_KIND_CANNOT_CHANGE";

    [ErrorCode(409, "error.party.identifier_in_use")]
    public const string IdentifierInUse = "PARTY_IDENTIFIER_IN_USE";

    [ErrorCode(422, "error.party.identifier_invalid")]
    public const string IdentifierInvalid = "PARTY_IDENTIFIER_INVALID";

    [ErrorCode(409, "error.party.role_already_held")]
    public const string RoleAlreadyHeld = "PARTY_ROLE_ALREADY_HELD";

    [ErrorCode(422, "error.party.code_not_in_force")]
    public const string CodeNotInForce = "PARTY_CODE_NOT_IN_FORCE";
}
