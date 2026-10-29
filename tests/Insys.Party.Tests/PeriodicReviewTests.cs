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

/// <summary>
/// OQ-138 (GAP-113, Ade 30 October 2026): what ends a periodic review. A PERSON class stays expired after a fresh screening until
/// a person completes the review; an AUTOMATIC class ends it on the fresh screening with the system as reviewer. A hit found
/// meanwhile is dealt with first. A renewed identity document is held beside the one it replaces, and the expired one blocks a
/// review only while no newer one of its kind is on file.
/// </summary>
public sealed class PeriodicReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 30);

    private static DueDiligence Expired()
    {
        var dueDiligence = DueDiligence.Start(Guid.CreateVersion7());
        dueDiligence.Classify(new RiskAssessment("LOW", 0, DueDiligenceCodes.Standard, "P5Y"), _ => null);
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today.AddYears(-5), Today, actor: null, Now.AddYears(-5));
        dueDiligence.Status.ShouldBe(DueDiligenceCodes.Cleared);
        dueDiligence.Expire(Today, Now).ShouldBeTrue();
        return dueDiligence;
    }

    [Fact]
    public void APersonClass_StaysExpiredAfterAFreshScreening_UntilAPersonCompletesTheReview_OQ138()
    {
        var dueDiligence = Expired();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: true, Today, Today.AddYears(5), actor: null, Now);
        (dueDiligence.Status, dueDiligence.Outstanding.Single(), dueDiligence.ReviewDue).ShouldBe((DueDiligenceCodes.Expired, DueDiligenceCodes.PeriodicReview, true));

        var reviewer = Guid.CreateVersion7();
        dueDiligence.CompleteReview(MakerChecker.SystemMaker, Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        dueDiligence.CompleteReview(reviewer, Now).IsSuccess.ShouldBeTrue();
        dueDiligence.CompleteReview(reviewer, Now).Error!.Code.ShouldBe(PartyErrorCodes.DueDiligenceNotUnderReview, "completed already");
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: true, Today, Today.AddYears(5), reviewer, Now);
        (dueDiligence.Status, dueDiligence.ReviewedBy, dueDiligence.ReviewDue, dueDiligence.NextReviewOn)
            .ShouldBe((DueDiligenceCodes.Cleared, (Guid?)reviewer, false, (DateOnly?)Today.AddYears(5)));
    }

    [Fact]
    public void AReviewWithoutAFreshScreening_LeavesThePartyExpired_OQ138()
    {
        var dueDiligence = Expired();
        dueDiligence.CompleteReview(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today, Today.AddYears(5), null, Now);
        (dueDiligence.Status, dueDiligence.ReviewDue).ShouldBe((DueDiligenceCodes.Expired, true));
    }

    [Fact]
    public void AnAutomaticClass_EndsItsReviewOnAFreshScreening_WithTheSystemAsReviewer_OQ138()
    {
        var dueDiligence = Expired();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: true, Today, Today.AddYears(5), actor: null, Now, DueDiligenceCodes.ReviewAutomatic);
        (dueDiligence.Status, dueDiligence.ReviewedBy, dueDiligence.ReviewDue).ShouldBe((DueDiligenceCodes.Cleared, (Guid?)MakerChecker.SystemMaker, false));
    }

    [Fact]
    public void NoReviewIsCompleted_BeforeThePartyIsDueOne_OQ138()
    {
        var dueDiligence = DueDiligence.Start(Guid.CreateVersion7());
        dueDiligence.CompleteReview(Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(PartyErrorCodes.DueDiligenceNotUnderReview);
    }

    [Fact]
    public void ARenewedPassport_IsHeldBesideTheOldOne_AndOnlyAnExpiredDocumentWithNoNewerOne_BlocksAReview_OQ138()
    {
        var party = Domain.Parties.Party.RegisterIndividual(Guid.CreateVersion7(), new IndividualData("MS", "Thandi", "Nkosi", new DateOnly(1990, 4, 2), "F", null, "ZA", "en"),
            Now);
        var old = party.AddIdentifier("PASSPORT", "ZA", [1], new string('a', 64), "5671", Now, Today.AddDays(-1)).Value;
        party.ExpiredIdentityDocument(old, Today).ShouldBe("PASSPORT");
        party.ExpiredIdentityDocument(old, Today.AddDays(-1)).ShouldBeNull("not expired on its expiry date");

        party.AddIdentifier("PASSPORT", "ZA", [2], new string('b', 64), "3219", Now).Error!.Code.ShouldBe(PartyErrorCodes.IdentifierInUse, "a renewal states its expiry");
        party.AddIdentifier("PASSPORT", "ZA", [2], new string('b', 64), "3219", Now, Today.AddDays(-5)).Error!.Code.ShouldBe(PartyErrorCodes.IdentifierInUse,
            "a renewal expires later");
        party.AddIdentifier("PASSPORT", "ZA", [2], new string('b', 64), "3219", Now, Today.AddYears(10)).IsSuccess.ShouldBeTrue();
        party.ExpiredIdentityDocument(old, Today).ShouldBeNull("a newer passport is on file");

        var id = party.AddIdentifier("NATIONAL_ID", "ZA", [3], new string('c', 64), "9086", Now).Value;
        party.ExpiredIdentityDocument(id, Today.AddYears(50)).ShouldBeNull("an identifier with no expiry date never blocks");
        party.AddIdentifier("NATIONAL_ID", "ZA", [4], new string('d', 64), "9087", Now, Today.AddYears(1)).Error!.Code
            .ShouldBe(PartyErrorCodes.IdentifierInUse, "a kind held without an expiry date is held once");
    }
}
