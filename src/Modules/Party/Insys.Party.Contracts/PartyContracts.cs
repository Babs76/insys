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
