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

namespace Insys.General.Tests;

/// <summary>
/// Wave 4 §4.3, §4.4 (OQ-130, OQ-132): how a renewal is accepted, the customer's answer, a lapse at the end, and an insurer's
/// non-renewal with its notice and, when late, the cover extended until the notice runs out.
/// </summary>
public sealed class RenewalAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateOnly PeriodEnd = new(2026, 12, 31);
    private static readonly Guid Holder = Guid.CreateVersion7();

    private static Renewal Offered() =>
        Renewal.OpenOffered(Guid.CreateVersion7(), Guid.CreateVersion7(), "GP0001", "GMOT", PeriodEnd, 3_050m, 100_000m, "ZAR", Today, Now);

    private static Renewal Offering(decimal factor)
    {
        var renewal = Renewal.OpenReview(Guid.CreateVersion7(), Guid.CreateVersion7(), "GP0002", "GMOT", PeriodEnd,
            new RenewalFacts(3_050m, "ZAR", 100_000m, 0, 0m, 3_000m, 0, 0, true, true, factor, true, null, false), Today, Now);
        renewal.RecordRating(Guid.CreateVersion7(), 3_050m, Now).IsSuccess.ShouldBeTrue();
        return renewal;
    }

    [Fact]
    public void AnAutomaticRenewal_RenewsWithoutAnAnswer_ButOnAcceptanceWaitsForOne_OQ130()
    {
        var automatic = Offered();
        automatic.SetAcceptance(RenewalAcceptance.Automatic, 15m);
        automatic.NeedsAcceptance.ShouldBeFalse();
        automatic.MarkRenewed(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();

        var onAcceptance = Offered();
        onAcceptance.SetAcceptance(RenewalAcceptance.OnAcceptance, 15m);
        (onAcceptance.NeedsAcceptance, onAcceptance.AwaitsAcceptance).ShouldBe((true, true));
        onAcceptance.MarkRenewed().Error!.Code.ShouldBe(GeneralErrorCodes.RenewalNotOffered, "not yet accepted");
        var portalUser = Guid.CreateVersion7();
        onAcceptance.Accept(portalUser, RenewalAcceptance.Portal, Now).IsSuccess.ShouldBeTrue();
        (onAcceptance.Status, onAcceptance.AcceptedBy, onAcceptance.AcceptedChannel).ShouldBe((RenewalStatus.Accepted, (Guid?)portalUser, RenewalAcceptance.Portal));
        onAcceptance.Accept(portalUser, RenewalAcceptance.Portal, Now).IsFailure.ShouldBeTrue("only an offered renewal is accepted");
        onAcceptance.MarkRenewed(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
    }

    /// <summary>OQ-130: worse terms than the tenant's threshold (premium up more than 15 %) need acceptance even on an AUTOMATIC product; within it, they do not.</summary>
    [Theory]
    [InlineData(1.15, false)]
    [InlineData(1.16, true)]
    public void WorseTermsThanTheThreshold_NeedAcceptance_WhateverTheProductSays_OQ130(double factor, bool needs)
    {
        var renewal = Offering((decimal)factor);
        renewal.Status.ShouldBe(RenewalStatus.Offered);
        renewal.SetAcceptance(RenewalAcceptance.Automatic, 15m);
        renewal.NeedsAcceptance.ShouldBe(needs);
    }

    [Fact]
    public void ARenewalAwaitingAcceptance_LapsesAtTheEnd_AndADeclinedOneDoesNotRenew_F022()
    {
        var waiting = Offered();
        waiting.SetAcceptance(RenewalAcceptance.OnAcceptance, 15m);
        waiting.Lapse(Now).ShouldBeTrue();
        waiting.Status.ShouldBe(RenewalStatus.Lapsed);
        waiting.DomainEvents.OfType<GeneralRenewalLapsed>().Count().ShouldBe(1);

        var automatic = Offered();
        automatic.SetAcceptance(RenewalAcceptance.Automatic, 15m);
        automatic.Lapse(Now).ShouldBeFalse("an automatic renewal does not lapse");

        var declined = Offered();
        declined.SetAcceptance(RenewalAcceptance.Automatic, 15m);
        declined.Decline(Guid.CreateVersion7(), RenewalAcceptance.Broker, Now).IsSuccess.ShouldBeTrue();
        (declined.Status, declined.DeclinedChannel).ShouldBe((RenewalStatus.DeclinedByCustomer, RenewalAcceptance.Broker));
        declined.MarkRenewed().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void OnPayment_TheFirstPremiumIsTheAcceptance_AndGraceEndingUnpaidLapsesIt_F022()
    {
        var paid = Offered();
        paid.SetAcceptance(RenewalAcceptance.OnPayment, 15m);
        paid.NeedsAcceptance.ShouldBeFalse("it renews, to be paid");
        paid.MarkRenewed(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        paid.AcceptedByPayment(Now);
        (paid.Status, paid.AcceptedChannel).ShouldBe((RenewalStatus.Renewed, RenewalAcceptance.Payment));

        var unpaid = Offered();
        unpaid.SetAcceptance(RenewalAcceptance.OnPayment, 15m);
        unpaid.MarkRenewed(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        unpaid.LapsedUnpaid(Now);
        unpaid.Status.ShouldBe(RenewalStatus.Lapsed);
    }

    /// <summary>F025: a reason needing approval waits for the second person; approved in time, the notice goes and the policy ends at its period end.</summary>
    [Fact]
    public void ANonRenewal_NeedingApproval_WaitsForIt_ThenTheNoticeGoes_F025()
    {
        var renewal = Offered();
        var proposer = Guid.CreateVersion7();
        renewal.RequestNonRenewal("CLAIMS", requiresApproval: true, proposer, Now).IsSuccess.ShouldBeTrue();
        renewal.DomainEvents.OfType<GeneralNonRenewalRequested>().Single().RequestedBy.ShouldBe(proposer);
        renewal.RequestNonRenewal("CLAIMS", true, proposer, Now).IsFailure.ShouldBeTrue("one at a time");

        renewal.DecideNonRenewal(true, Guid.CreateVersion7(), 31, "Your claims experience", Holder, null, PeriodEnd.AddDays(-40), Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.NonRenewalStatus, renewal.CoverExtendedTo).ShouldBe((RenewalStatus.NotRenewedByInsurer, NonRenewalStates.Decided, (DateOnly?)null));
        renewal.DomainEvents.OfType<GeneralPolicyNotRenewed>().Single().ShouldSatisfyAllConditions(e => e.CoverEndsOn.ShouldBe(PeriodEnd),
            e => e.CustomerWording.ShouldBe("Your claims experience"), e => e.PolicyholderPartyId.ShouldBe(Holder));
    }

    /// <summary>OQ-132 (b): decided less than the notice before the end, cover runs on until the notice runs out; a rejection changes nothing.</summary>
    [Fact]
    public void ALateNonRenewal_ExtendsCoverUntilTheNoticeRunsOut_AndARejectedOneChangesNothing_OQ132()
    {
        var late = Offered();
        late.RequestNonRenewal("CLAIMS", false, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        late.DecideNonRenewal(true, Guid.CreateVersion7(), 31, "w", Holder, null, PeriodEnd.AddDays(-10), Now).IsSuccess.ShouldBeTrue();
        late.CoverExtendedTo.ShouldBe(PeriodEnd.AddDays(21));
        late.DomainEvents.OfType<GeneralPolicyNotRenewed>().Single().ShouldSatisfyAllConditions(e => e.CoverEndsOn.ShouldBe(PeriodEnd.AddDays(21)), e => e.CoverExtended.ShouldBeTrue());

        var rejected = Offered();
        rejected.RequestNonRenewal("CLAIMS", true, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        rejected.DecideNonRenewal(false, Guid.CreateVersion7(), 31, "w", Holder, null, Today, Now).IsSuccess.ShouldBeTrue();
        (rejected.Status, rejected.NonRenewalStatus).ShouldBe((RenewalStatus.Offered, NonRenewalStates.Rejected));
        rejected.DomainEvents.OfType<GeneralPolicyNotRenewed>().ShouldBeEmpty();
    }
}
