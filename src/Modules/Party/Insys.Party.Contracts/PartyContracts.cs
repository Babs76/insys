using Insys.BuildingBlocks.Contracts;

namespace Insys.Party.Contracts;

public static class PartyPermissions
{
    public const string PartyRead = "party.read";
    public const string PartyCreate = "party.create";
    public const string PartyUpdate = "party.update";

    /// <summary>Merging and anonymising change or remove identity; they need their own permission (PTY-004, PTY-005).</summary>
    public const string PartyMerge = "party.merge";

    public const string PartyAnonymise = "party.anonymise";
    public const string BankAccountRead = "bank_account.read";
    public const string BankAccountCreate = "bank_account.create";

    /// <summary>Verifying makes an account payable; it is a separate permission.</summary>
    public const string BankAccountVerify = "bank_account.verify";

    public const string ConsentRead = "consent.read";
    public const string ConsentUpdate = "consent.update";
}

/// <summary>Party events carry identifiers only, never personal data (INT-007).</summary>
[IntegrationEvent("party.PartyRegistered")]
public sealed record PartyRegistered(Guid PartyId, string Kind) : IIntegrationEvent;

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

