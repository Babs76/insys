/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health.Tests
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
using Insys.Health.Domain.Care;

namespace Insys.Health.Tests;

/// <summary>F083 to F085, OQ-150: a person decides a registration, not the one who asked for it; the decline's notice names no condition.</summary>
public sealed class ChronicRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 31);
    private static readonly Guid Maker = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();

    private static ChronicRegistration Requested() => ChronicRegistration.Request(Guid.CreateVersion7(), "CR-1", Guid.CreateVersion7(), Guid.CreateVersion7(), "DIABETES", "E11", "HbA1c results attached", Maker, null);

    [Fact]
    public void TheRequester_CannotDecide_SRS002()
    {
        var registration = Requested();
        registration.Approve(Day, null, null, [], Maker, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        registration.Decline("No evidence", Maker, Guid.CreateVersion7(), null, null, "ZAR", Day, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
    }

    [Fact]
    public void AnApproval_IsInForceBetweenItsDates_AndCanEnd()
    {
        var registration = Requested();
        registration.Approve(Day, Day.AddYears(1), Day.AddMonths(6), [new EntitlementRequest(null, "METFORMIN", 60)], Checker, Now).IsSuccess.ShouldBeTrue();
        registration.InForce(Day.AddDays(-1)).ShouldBeFalse();
        registration.InForce(Day).ShouldBeTrue();
        registration.InForce(Day.AddYears(1)).ShouldBeTrue();
        registration.InForce(Day.AddYears(1).AddDays(1)).ShouldBeFalse();
        registration.Entitlements.Single().MonthlyQuantity.ShouldBe(60);

        registration.End(Day.AddMonths(1)).IsSuccess.ShouldBeTrue();
        registration.InForce(Day).ShouldBeFalse();
        registration.Approve(Day, null, null, [], Checker, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Theory]
    [InlineData(null, null, 60)]
    [InlineData("N-1", null, 0)]
    public void AnEntitlement_NeedsAMedicineAndAQuantity(string? product, string? ingredient, int monthly) =>
        Requested().Approve(Day, null, null, [new EntitlementRequest(product, ingredient, monthly)], Checker, Now).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);

    [Fact]
    public void ADeclinesNotice_NamesNoCondition_HLT008()
    {
        var registration = Requested();
        var recipient = Guid.CreateVersion7();
        registration.Decline("Criteria not met", Checker, recipient, "PROD", null, "ZAR", Day, Now).IsSuccess.ShouldBeTrue();
        var raised = registration.DomainEvents.OfType<ChronicRegistrationDeclinedRightsDue>().Single();
        raised.RecipientPartyId.ShouldBe(recipient);
        raised.ToString().ShouldNotContain("DIABETES");
        raised.ToString().ShouldNotContain("E11");
        raised.ToString().ShouldNotContain("Criteria");
    }

    [Fact]
    public void AnEnrolmentsReview_IsRaisedOnceForItsDate_AndAReviewMovesIt_F086()
    {
        var enrolment = ProgrammeEnrolment.Enrol(Guid.CreateVersion7(), "PE-1", Guid.CreateVersion7(), Guid.CreateVersion7(), "DM-CARE", null, Day, Day.AddMonths(6), Maker);
        enrolment.RaiseReviewDue(Day.AddMonths(6).AddDays(-1), Now).ShouldBeFalse();
        enrolment.RaiseReviewDue(Day.AddMonths(6), Now).ShouldBeTrue();
        enrolment.RaiseReviewDue(Day.AddMonths(6).AddDays(1), Now).ShouldBeFalse();
        var raised = enrolment.DomainEvents.OfType<ProgrammeReviewDue>().Single();
        raised.ToString().ShouldNotContain("DM-CARE");

        enrolment.RecordReview(Day.AddMonths(12), Day.AddMonths(6)).IsSuccess.ShouldBeTrue();
        enrolment.RaiseReviewDue(Day.AddMonths(12), Now).ShouldBeTrue();

        enrolment.End(Day.AddMonths(13)).IsSuccess.ShouldBeTrue();
        enrolment.RaiseReviewDue(Day.AddMonths(30), Now).ShouldBeFalse();
    }
}
