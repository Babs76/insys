using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;
using Insys.Testing;

namespace Insys.Party.Tests;

public sealed class PartyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly IndividualData Thandi = new("MS", "Thandi", "Nkosi", new DateOnly(1990, 4, 2), "F", null, "ZA", "en");

    private static Domain.Parties.Party Individual() => Domain.Parties.Party.RegisterIndividual(Guid.CreateVersion7(), Thandi, Now);

    [Fact]
    public void Party_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("party", "Party")
            .Select(t => t.Replace("No activity", "NoActivity", StringComparison.Ordinal).Replace("New activity", "NewActivity", StringComparison.Ordinal)
                .Replace("Death recorded", "DeathRecorded", StringComparison.Ordinal).Replace("Retention ended", "RetentionEnded", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        Domain.Parties.Party.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void Kind_NeverChanges_PTY001()
    {
        var party = Individual();
        party.UpdateDetails(new OrganisationData("Acme", null, "PTY", null, null), Now).Error!.Code.ShouldBe(PartyErrorCodes.KindCannotChange);
        party.Kind.ShouldBe(PartyKind.Individual);
    }

    [Fact]
    public void Party_HoldsManyRoles_PTY002()
    {
        var party = Individual();
        party.AssignRole("POLICYHOLDER", "LIFE", "POLICY", Guid.CreateVersion7(), Today, Now).IsSuccess.ShouldBeTrue();
        party.AssignRole("BENEFICIARY", "LIFE", "POLICY", Guid.CreateVersion7(), Today, Now).IsSuccess.ShouldBeTrue();
        party.Roles.Count.ShouldBe(2);
    }

    [Fact]
    public void MergedParty_IsReadOnly_AndPointsToItsSurvivor_PTY004()
    {
        var survivor = Individual();
        var duplicate = Individual();
        duplicate.MergeInto(survivor, Guid.CreateVersion7(), "Same person", "{}", Now).IsSuccess.ShouldBeTrue();

        duplicate.Status.ShouldBe(PartyStatus.Merged);
        duplicate.MergedIntoId.ShouldBe(survivor.Id);
        duplicate.Merges.Single().SurvivorId.ShouldBe(survivor.Id);
        duplicate.UpdateDetails(Thandi with { Surname = "Dlamini" }, Now).Error!.Code.ShouldBe(PartyErrorCodes.PartyReadOnly);
        duplicate.DomainEvents.OfType<PartiesMerged>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Party_IsNotMergedIntoAPartyOfAnotherKind()
    {
        var organisation = Domain.Parties.Party.RegisterOrganisation(Guid.CreateVersion7(), new OrganisationData("Acme (Pty) Ltd", "Acme", "PTY", "2020/1", null), Now);
        Individual().MergeInto(organisation, Guid.CreateVersion7(), "?", "{}", Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Identifier_OfTheSameTypeAndCountry_IsAddedOnce()
    {
        var party = Individual();
        party.AddIdentifier("NATIONAL_ID", "ZA", [1, 2, 3], new string('a', 64), Now).IsSuccess.ShouldBeTrue();
        party.AddIdentifier("NATIONAL_ID", "ZA", [4, 5, 6], new string('b', 64), Now).Error!.Code.ShouldBe(PartyErrorCodes.IdentifierInUse);
    }

    [Fact]
    public void Events_CarryNoPersonalData_INT007()
    {
        var party = Individual();
        party.UpdateDetails(Thandi with { Surname = "Dlamini" }, Now).IsSuccess.ShouldBeTrue();
        party.DomainEvents.ShouldAllBe(e => !e.ToString()!.Contains("Thandi") && !e.ToString()!.Contains("Dlamini"));
    }

    [Fact]
    public void DeceasedParty_IsAnonymised_AndKeepsNoPersonalData()
    {
        var party = Individual();
        party.AddIdentifier("NATIONAL_ID", "ZA", [1], new string('a', 64), Now).IsSuccess.ShouldBeTrue();
        party.AddContactPoint("EMAIL", "thandi@example.com", true, Now).IsSuccess.ShouldBeTrue();
        party.AnonymiseParty(Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

        party.RecordDeath(Today.AddDays(-3), Today, Now).IsSuccess.ShouldBeTrue();
        party.AnonymiseParty(Now).IsSuccess.ShouldBeTrue();
        party.Status.ShouldBe(PartyStatus.Anonymised);
        party.DisplayName.ShouldBe("Anonymised");
        party.Individual!.Surname.ShouldBe("Anonymised");
        party.Identifiers.ShouldBeEmpty();
        party.ContactPoints.ShouldBeEmpty();
    }

    [Fact]
    public void NewPrimaryContact_DemotesThePreviousOne()
    {
        var party = Individual();
        party.AddContactPoint("EMAIL", "a@example.com", true, Now).IsSuccess.ShouldBeTrue();
        party.AddContactPoint("EMAIL", "b@example.com", true, Now).IsSuccess.ShouldBeTrue();
        party.ContactPoints.Single(c => c.IsPrimary).Value.ShouldBe("b@example.com");
    }

    [Fact]
    public void ConsentLedger_IsAppendOnly_AndTheLatestRecordIsCurrent_PTY005()
    {
        var partyId = Guid.CreateVersion7();
        var ledger = new ConsentLedger(partyId, []);
        ledger.GrantConsent("MKT_EMAIL", "PORTAL", "form-1", Now).ShouldNotBeNull();
        ledger.GrantConsent("MKT_EMAIL", "PORTAL", "form-2", Now.AddMinutes(1)).ShouldBeNull();
        ledger.HasConsent("MKT_EMAIL").ShouldBeTrue();

        var withdrawal = ledger.WithdrawConsent("MKT_EMAIL", "CALL_CENTRE", null, Now.AddDays(1)).ShouldNotBeNull();
        withdrawal.Granted.ShouldBeFalse();
        withdrawal.WithdrawnAt.ShouldBe(Now.AddDays(1));
        ledger.HasConsent("MKT_EMAIL").ShouldBeFalse();
        ledger.Records.Count.ShouldBe(2);
        ledger.Records[0].Granted.ShouldBeTrue();
    }
}
