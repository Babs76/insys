/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General.Tests
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
using Insys.General.Domain;
using Insys.General.Domain.Renewals;
using Insys.General.Domain.Retention;

namespace Insys.General.Tests;

/// <summary>F026 (Wave 4 §4.3): retention cases, offers within their uses and product, the customer's answers, and a discount on a renewal.</summary>
public sealed class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly RetentionOfferTerms Loyal = new("LOYAL-10", null, RetentionOfferKinds.DiscountPercent, 10m, 1, null);

    private static RetentionCase Case(Guid? renewalId = null) =>
        RetentionCase.OpenCase(Guid.CreateVersion7(), Guid.CreateVersion7(), "GP0001", "GMOT", renewalId, RetentionTriggers.NotRenewing, null, null, Today, Now);

    [Fact]
    public void AnOffer_IsMadeWithinItsUsesAndProduct_AndAnAcceptedOneIsAnnounced_F026()
    {
        var retention = Case();
        retention.DomainEvents.OfType<RetentionCaseOpened>().Single().Trigger.ShouldBe(RetentionTriggers.NotRenewing);
        var clerk = Guid.CreateVersion7();
        retention.MakeOffer(Loyal with { ProductCode = "OTHER" }, 0, clerk, Now).Error!.Code.ShouldBe(GeneralErrorCodes.CodeNotInForce);
        retention.MakeOffer(Loyal, 1, clerk, Now).Error!.Code.ShouldBe(GeneralErrorCodes.RetentionOfferUsedUp, "made on the policy before");
        var offer = retention.MakeOffer(Loyal, 0, clerk, Now).Value;
        retention.OwnerId.ShouldBe(clerk, "the first person to work it owns it");

        retention.DecideOffer(offer, accepted: true, clerk, Now).IsSuccess.ShouldBeTrue();
        retention.DecideOffer(offer, accepted: false, clerk, Now).IsFailure.ShouldBeTrue("answered already");
        retention.DomainEvents.OfType<RetentionOfferAccepted>().Single().ShouldSatisfyAllConditions(e => e.Kind.ShouldBe(RetentionOfferKinds.DiscountPercent),
            e => e.Value.ShouldBe(10m));
    }

    [Fact]
    public void ACase_ClosesOnceWithItsOutcome_AndThenTakesNoOffers_F026()
    {
        var retention = Case();
        retention.Close("MAYBE", Guid.CreateVersion7(), null, Now).IsFailure.ShouldBeTrue();
        retention.Close(RetentionOutcomes.Lost, Guid.CreateVersion7(), "Moved insurer", Now).IsSuccess.ShouldBeTrue();
        (retention.Status, retention.Outcome).ShouldBe((RetentionCase.Closed, RetentionOutcomes.Lost));
        retention.Close(RetentionOutcomes.Retained, Guid.CreateVersion7(), null, Now).IsFailure.ShouldBeTrue();
        retention.MakeOffer(Loyal, 0, Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
    }

    /// <summary>F026: a discount accepted on a renewal, even one declined, lowers its offered premium and accepts it.</summary>
    [Fact]
    public void ARetentionDiscount_OnADeclinedRenewal_LowersItsPremiumAndAcceptsIt_F026()
    {
        var renewal = Renewal.OpenOffered(Guid.CreateVersion7(), Guid.CreateVersion7(), "GP0001", "GMOT", Today.AddDays(20), 3_050m, 100_000m, "ZAR", Today, Now);
        renewal.Decline(Guid.CreateVersion7(), RenewalAcceptance.Portal, Now).IsSuccess.ShouldBeTrue();
        var staff = Guid.CreateVersion7();
        renewal.ApplyRetentionDiscount(100m, staff, Now).IsFailure.ShouldBeTrue();
        renewal.ApplyRetentionDiscount(10m, staff, Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.OfferedPremium, renewal.AcceptedBy, renewal.ChangedTerms).ShouldBe((RenewalStatus.Accepted, (decimal?)2_745m, (Guid?)staff, true));
        renewal.MarkRenewed(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        renewal.DomainEvents.OfType<GeneralRenewalRenewed>().Single().ShouldSatisfyAllConditions(e => e.CurrentPremium.ShouldBe(3_050m), e => e.RenewedPremium.ShouldBe(2_745m));
    }
}
