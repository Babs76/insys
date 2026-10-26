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

/// <summary>OQ-111: releasing a search log hold needs a second person; the hold stays in force until the release is approved.</summary>
public sealed class SearchLogHoldReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static SearchLogHold Requested(out Guid maker)
    {
        var hold = SearchLogHold.Place("clerk-1", "Investigation 42", Guid.CreateVersion7().ToString(), Now);
        maker = Guid.CreateVersion7();
        hold.RequestRelease(maker, "Investigation closed", Now).IsSuccess.ShouldBeTrue();
        return hold;
    }

    [Fact]
    public void ReleasingAHold_IsARequest_AndTheHoldStaysInForce_OQ111()
    {
        var hold = Requested(out var maker);

        hold.IsActive.ShouldBeTrue();
        hold.ReleasePending.ShouldBeTrue();
        (hold.ReleaseRequestedBy, hold.ReleaseRequestedAt, hold.ReleaseReason).ShouldBe((maker, Now, "Investigation closed"));
        var requested = hold.DomainEvents.OfType<SearchLogHoldReleaseRequested>().Single();
        (requested.HoldId, requested.RequestedBy, requested.SearchedBy).ShouldBe((hold.Id, maker, "clerk-1"));
        hold.RequestRelease(maker, "Again", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void ApprovedRelease_ReleasesTheHold_ByTheChecker_AndARedeliveredGrantIsRecognised_OQ111()
    {
        var hold = Requested(out _);
        var approval = Guid.CreateVersion7();
        var checker = Guid.CreateVersion7();

        hold.ReleaseApplied(approval).ShouldBeFalse();
        hold.DecideRelease(true, approval, checker, Now).IsSuccess.ShouldBeTrue();

        hold.IsActive.ShouldBeFalse();
        (hold.ReleasedBy, hold.ReleasedAt, hold.ReleaseApprovalId).ShouldBe((checker.ToString(), Now, approval));
        hold.ReleaseRequestedBy.ShouldBeNull();
        hold.ReleaseApplied(approval).ShouldBeTrue();
        hold.RequestRelease(Guid.CreateVersion7(), "Again", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndingWithoutApproval_ClearsTheRequest_AndTheHoldStays_OQ111(bool byAChecker)
    {
        var hold = Requested(out _);

        hold.DecideRelease(false, Guid.CreateVersion7(), byAChecker ? Guid.CreateVersion7() : Guid.Empty, Now).IsSuccess.ShouldBeTrue();

        hold.IsActive.ShouldBeTrue();
        hold.ReleasePending.ShouldBeFalse();
        (hold.ReleaseRequestedBy, hold.ReleaseRequestedAt, hold.ReleaseReason).ShouldBe((null, null, null));
        hold.DecideRelease(false, Guid.CreateVersion7(), Guid.Empty, Now).IsSuccess.ShouldBeTrue("a redelivered ending changes nothing");
        hold.RequestRelease(Guid.CreateVersion7(), "Closed after all", Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AGrant_WithNoReleaseWaiting_IsRefused_D5()
    {
        var hold = SearchLogHold.Place(null, "Investigation 42", "officer", Now);

        hold.DecideRelease(true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        hold.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void TheRequester_CannotApproveTheirOwnRelease_SRS002()
    {
        var hold = Requested(out var maker);

        hold.DecideRelease(true, Guid.CreateVersion7(), maker, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        hold.ReleasePending.ShouldBeTrue();
    }
}
