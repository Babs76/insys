/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party.Tests
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
using Insys.Party.Domain.Parties;

namespace Insys.Party.Tests;

/// <summary>D-3 and D-5: a bank account change keeps the active account until the new one is verified; every ending is applied once.</summary>
public sealed class BankAccountEndingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly IndividualData Thandi = new("MS", "Thandi", "Nkosi", new DateOnly(1990, 4, 2), "F", null, "ZA", "en");
    private static readonly Guid Maker = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();

    private static BankAccountData Account(string hash) => new([1, 2, 3], hash.PadRight(64, '0'), "1234", "250655", null, "T Nkosi", "CURRENT", "ZAR");

    private static Domain.Parties.Party WithVerifiedAccount(out Guid first)
    {
        var party = Domain.Parties.Party.RegisterIndividual(Guid.CreateVersion7(), Thandi, Now);
        var id = party.AddBankAccount(Account("a"), Maker, Now).Value;
        var account = party.BankAccounts.Single(a => a.Id == id);
        party.DecideBankAccountVerification(id, true, Guid.CreateVersion7(), Checker, account.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        first = id;
        return party;
    }

    private static void ExactlyOneActive(Domain.Parties.Party party, Guid expected) =>
        party.BankAccounts.Where(a => a.Currency == "ZAR" && a.Status == BankAccountStatus.Active).Select(a => a.Id).ShouldBe([expected]);

    [Fact]
    public void ANewAccount_IsPending_AndThePreviousStaysActiveAndPayable_UntilItIsVerified_D3()
    {
        var party = WithVerifiedAccount(out var first);

        var second = party.AddBankAccount(Account("b"), Maker, Now).Value;

        party.BankAccounts.Single(a => a.Id == second).Status.ShouldBe(BankAccountStatus.PendingVerification);
        ExactlyOneActive(party, first);
        party.DomainEvents.OfType<BankAccountVerificationWithdrawn>().ShouldBeEmpty("nothing was pending");
    }

    [Fact]
    public void OnApproval_TheNewAccountIsActive_AndThePreviousIsSupersededByIt_D3()
    {
        var party = WithVerifiedAccount(out var first);
        var second = party.AddBankAccount(Account("b"), Maker, Now).Value;
        var hash = party.BankAccounts.Single(a => a.Id == second).SubjectHash;

        party.DecideBankAccountVerification(second, true, Guid.CreateVersion7(), Checker, hash, Now).IsSuccess.ShouldBeTrue();

        ExactlyOneActive(party, second);
        var previous = party.BankAccounts.Single(a => a.Id == first);
        (previous.Status, previous.SupersededById).ShouldBe((BankAccountStatus.Superseded, (Guid?)second));
        party.DomainEvents.OfType<PartyBankAccountChanged>().Last().ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe("ACTIVE"), e => e.SupersededId.ShouldBe(first));
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("EXPIRED")]
    [InlineData("WITHDRAWN")]
    public void EveryOtherEnding_RejectsTheNewAccount_AndThePreviousStaysActive_AndARedeliveryIsApplied_D3_D5(string ending)
    {
        var party = WithVerifiedAccount(out var first);
        var second = party.AddBankAccount(Account("b"), Maker, Now).Value;
        var hash = party.BankAccounts.Single(a => a.Id == second).SubjectHash;
        var approval = Guid.CreateVersion7();
        var checker = ending == "REJECTED" ? Checker : Guid.Empty;

        party.BankAccountEndingApplied(second, approval).ShouldBeFalse();
        party.DecideBankAccountVerification(second, false, approval, checker, hash, Now).IsSuccess.ShouldBeTrue();

        party.BankAccounts.Single(a => a.Id == second).Status.ShouldBe(BankAccountStatus.Rejected);
        ExactlyOneActive(party, first);
        party.BankAccountEndingApplied(second, approval).ShouldBeTrue("the same ending delivered again changes nothing");
        party.BankAccountEndingApplied(second, Guid.CreateVersion7()).ShouldBeFalse("another approval's ending is not this one");
        party.DecideBankAccountVerification(second, false, approval, checker, hash, Now).IsFailure.ShouldBeTrue("it is no longer pending");
    }

    [Fact]
    public void AChangeWhilePending_WithdrawsThatVerification_SoItsLaterWithdrawnEnding_IsAlreadyApplied_D3()
    {
        var party = WithVerifiedAccount(out var first);
        var second = party.AddBankAccount(Account("b"), Maker, Now).Value;

        var third = party.AddBankAccount(Account("c"), Maker, Now).Value;

        party.DomainEvents.OfType<BankAccountVerificationWithdrawn>().Single().BankAccountId.ShouldBe(second);
        var withdrawn = party.BankAccounts.Single(a => a.Id == second);
        (withdrawn.Status, withdrawn.SupersededById, withdrawn.VerifiedAt).ShouldBe((BankAccountStatus.Superseded, (Guid?)third, (DateTimeOffset?)null));
        party.BankAccountEndingApplied(second, Guid.CreateVersion7()).ShouldBeTrue("Workflow's WITHDRAWN ending finds it already in its defined state");
        ExactlyOneActive(party, first);
        party.BankAccounts.Count(a => a.Status == BankAccountStatus.PendingVerification).ShouldBe(1);
        party.BankAccountEndingApplied(first, Guid.CreateVersion7()).ShouldBeFalse("a verified account is not an applied ending");
    }

    [Fact]
    public void TheMakerCannotDecide_NorCanAnEndingWithoutACheckerApprove_NorAnApprovalForAnotherSubject_SRS002()
    {
        var party = WithVerifiedAccount(out var first);
        var second = party.AddBankAccount(Account("b"), Maker, Now).Value;
        var hash = party.BankAccounts.Single(a => a.Id == second).SubjectHash;

        party.DecideBankAccountVerification(second, false, Guid.CreateVersion7(), Maker, hash, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        party.DecideBankAccountVerification(second, true, Guid.CreateVersion7(), Guid.Empty, hash, Now).IsFailure.ShouldBeTrue();
        party.DecideBankAccountVerification(second, false, Guid.CreateVersion7(), Guid.Empty, "other", Now).Error!.Code
            .ShouldBe(PartyErrorCodes.BankAccountSubjectChanged);

        party.BankAccounts.Single(a => a.Id == second).Status.ShouldBe(BankAccountStatus.PendingVerification);
        ExactlyOneActive(party, first);
    }
}
