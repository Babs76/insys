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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Party.Contracts;

/// <summary>What Party asks Workflow to approve (ADR-009, ADR-013).</summary>
public static class PartyApprovals
{
    public const string BankAccountBusinessRefType = "BANK_ACCOUNT";
    public const string BankAccountVerificationType = "BANK_ACCOUNT_VERIFICATION";

    /// <summary>A matched portal registration's resolution (external-user access §6); the business reference is the registration's party.</summary>
    public const string MatchBusinessRefType = "PARTY_MATCH";

    public const string MatchResolutionType = "PARTY_MATCH_RESOLUTION";

    /// <summary>A legal hold on the party search log (OQ-073); the business reference is the hold.</summary>
    public const string SearchLogHoldBusinessRefType = "SEARCH_LOG_HOLD";

    /// <summary>Releasing a legal hold needs a second person (OQ-111, 25 October 2026); the compliance officer is the checker.</summary>
    public const string LegalHoldReleaseType = "LEGAL_HOLD_RELEASE";
}

public static class PartyPermissions
{
    public const string PartyRead = "party.read";

    /// <summary>Place and release legal holds on the party search access log (OQ-073).</summary>
    public const string SearchLogHold = "party_search_log.hold";
    public const string PartyCreate = "party.create";
    public const string PartyUpdate = "party.update";

    /// <summary>Merging and anonymising change or remove identity; they need their own permission (PTY-004, PTY-005).</summary>
    public const string PartyMerge = "party.merge";

    public const string PartyAnonymise = "party.anonymise";
    public const string BankAccountRead = "bank_account.read";
    public const string BankAccountCreate = "bank_account.create";

    /// <summary>The checker of a bank account's verification approval needs it (BANK_ACCOUNT_VERIFICATION in Workflow).</summary>
    public const string BankAccountVerify = "bank_account.verify";

    public const string ConsentRead = "consent.read";
    public const string ConsentUpdate = "consent.update";
}

/// <summary>Party events carry identifiers only, never personal data (INT-007).</summary>
/// <summary>
/// A party registered through a portal matched an existing one (identifier or close name with the same birth date): Workflow
/// opens a staff review task (PARTY_MATCH_REVIEW). The portal user was told only that the registration was received.
/// </summary>
[IntegrationEvent("party.PartyMatchReviewRequested")]
public sealed record PartyMatchReviewRequested(Guid PartyId, Guid MatchedPartyId) : IIntegrationEvent;

/// <summary>
/// A matched portal registration was resolved after a second person's approval (external-user access §6): LINK merged it
/// into the existing party (party.PartiesMerged follows), NEW confirmed it. Workflow closes the review task.
/// </summary>
[IntegrationEvent("party.PartyMatchResolved")]
public sealed record PartyMatchResolved(Guid PartyId, string Resolution, Guid? ExistingPartyId) : IIntegrationEvent;

[IntegrationEvent("party.PartyRegistered")]
public sealed record PartyRegistered(Guid PartyId, string Kind) : IIntegrationEvent;

/// <summary>An identity document was added to the party (F001): Party verifies it with the tenant's identity verification provider, if one is active.</summary>
[IntegrationEvent("party.PartyIdentifierAdded")]
public sealed record PartyIdentifierAdded(Guid PartyId, Guid IdentifierId) : IIntegrationEvent;

[IntegrationEvent("party.PartyDetailsChanged")]
public sealed record PartyDetailsChanged(Guid PartyId, string Part) : IIntegrationEvent;

[IntegrationEvent("party.PartiesMerged")]
public sealed record PartiesMerged(Guid SurvivorId, Guid MergedId) : IIntegrationEvent;

[IntegrationEvent("party.PartyAnonymised")]
public sealed record PartyAnonymised(Guid PartyId) : IIntegrationEvent;

[IntegrationEvent("party.ConsentGranted")]
public sealed record ConsentGranted(Guid PartyId, string PurposeCode) : IIntegrationEvent;

[IntegrationEvent("party.ConsentWithdrawn")]
public sealed record ConsentWithdrawn(Guid PartyId, string PurposeCode) : IIntegrationEvent;

/// <summary>A party's bank account was added (superseding another) or verified. Ids only; the details stay in the Party module.</summary>
[IntegrationEvent("party.PartyBankAccountChanged")]
public sealed record PartyBankAccountChanged(Guid PartyId, Guid BankAccountId, Guid? SupersededId, string Status) : IIntegrationEvent;

/// <summary>A bank account as other modules see it: whose, its status, the keyed hash of its number and the last four digits. Never the number.</summary>
public sealed record BankAccountRef(Guid Id, Guid PartyId, string Currency, string Status, string AccountNumberHash, string Last4)
{
    /// <summary>Verified and not superseded: payments may go to it.</summary>
    public bool IsPayable => Status == "ACTIVE";
}

/// <summary>Public query interface: a bank account by id, for the current tenant, on the Party module's own connection.</summary>
public interface IBankAccounts
{
    Task<BankAccountRef?> Get(Guid tenantId, Guid bankAccountId, CancellationToken cancellationToken);

    /// <summary>The party's current, verified (ACTIVE) account in the currency, if any.</summary>
    Task<BankAccountRef?> Current(Guid tenantId, Guid partyId, string currency, CancellationToken cancellationToken);
}

/// <summary>An account's details, decrypted, for a payment provider adapter only. Its text never shows them.</summary>
public sealed record PayeeAccountDetails(string AccountNumber, string BankCode, string? BranchCode, string AccountHolderName, string AccountType, string Currency)
{
    public override string ToString() => "PayeeAccountDetails { *** }";
}

/// <summary>
/// The one way to an account number in clear: for sending a payment to a provider, inside the Payments module's
/// provider adapter. Each read is audited by the caller (IAM-006).
/// </summary>
public interface IBankAccountDetails
{
    Task<PayeeAccountDetails?> ForPayment(Guid tenantId, Guid bankAccountId, CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: an individual's date of birth, for age rules (Life entry ages, Health dependant age limits).
/// Only the date is returned (minimum necessary); null for an organisation or an unknown party.
/// </summary>
/// <summary>Public query interface: whether a party exists in the tenant and is not merged away (for linking an external user to it).</summary>
public interface IPartyDirectory
{
    Task<bool> Exists(Guid tenantId, Guid partyId, CancellationToken cancellationToken);

    /// <summary>Who registered the party through a portal ("INTERMEDIARY:BRK-1"), if anyone (external-user access §3).</summary>
    Task<string?> RegisteredBy(Guid tenantId, Guid partyId, CancellationToken cancellationToken);

    /// <summary>The party a merged party was merged into (PTY-004), for example a portal registration linked after review (§6); null if it was not merged.</summary>
    Task<Guid?> MergedInto(Guid tenantId, Guid partyId, CancellationToken cancellationToken);

    /// <summary>The party's display name, for naming a payee to staff (BR-035); null for an unknown party.</summary>
    Task<string?> DisplayName(Guid tenantId, Guid partyId, CancellationToken cancellationToken);
}

public interface IPersonFacts
{
    Task<DateOnly?> BirthDate(Guid tenantId, Guid partyId, CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: an individual's preferred language, for choosing a document or notification language
/// (docs/specs/addenda/localisation.md §5); null for an organisation, an unknown party or none recorded.
/// </summary>
public interface IPartyLanguage
{
    Task<string?> PreferredLanguage(Guid tenantId, Guid partyId, CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: a party's primary contact for a channel type (EMAIL, MOBILE), for sending a notification
/// (addenda/communications.md §3). Read when sending and never copied; null when the party has none.
/// </summary>
public interface IPartyContacts
{
    Task<string?> Primary(Guid tenantId, Guid partyId, string channelType, CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: whether a party's current consent for a purpose is granted (PTY-005: the latest record per
/// purpose is current). Marketing is sent only when it is (communications §4, POPIA s69).
/// </summary>
public interface IPartyConsent
{
    Task<bool> IsGranted(Guid tenantId, Guid partyId, string purposeCode, CancellationToken cancellationToken);

    /// <summary>The ID of the party's current consent record for the purpose when it grants it, else null: the proof of consent a marketing message keeps.</summary>
    Task<Guid?> GrantingRecord(Guid tenantId, Guid partyId, string purposeCode, CancellationToken cancellationToken);
}
