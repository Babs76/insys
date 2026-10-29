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
/// BR-109 / GAP-112: the enhanced due diligence approval names who requested it. A person who completed the last check is
/// the maker and cannot approve it; with no person acting (a provider's result, a job) the platform is the requester, so any
/// person with authority can. A request recorded with no requester (before BR-109) is asked again.
/// </summary>
public sealed class EnhancedApprovalRequesterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 30);

    private static DueDiligence Enhanced()
    {
        var dueDiligence = DueDiligence.Start(Guid.CreateVersion7());
        dueDiligence.Classify(new RiskAssessment("HIGH", 80, DueDiligenceCodes.Enhanced, "P1Y"), _ => null);
        return dueDiligence;
    }

    [Fact]
    public void WithNoPersonActing_ThePlatformRequestsTheApproval_AndAnyPersonWithAuthorityMayGiveIt_BR109()
    {
        var dueDiligence = Enhanced();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today, Today.AddYears(1), actor: null, Now);

        dueDiligence.DomainEvents.OfType<EnhancedDueDiligenceApprovalRequested>().Single().RequestedBy.ShouldBe(MakerChecker.SystemMaker);
        dueDiligence.Outstanding.ShouldBe([DueDiligenceCodes.EnhancedApproval]);
        dueDiligence.DecideEnhanced(approved: true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void APersonWhoCompletedIt_IsTheMaker_AndCannotApproveIt_SRS002()
    {
        var dueDiligence = Enhanced();
        var clerk = Guid.CreateVersion7();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today, Today.AddYears(1), clerk, Now);

        dueDiligence.DomainEvents.OfType<EnhancedDueDiligenceApprovalRequested>().Single().RequestedBy.ShouldBe(clerk);
        dueDiligence.DecideEnhanced(approved: true, Guid.CreateVersion7(), clerk, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
    }

    [Fact]
    public void ARequestDeclinedOrExpired_IsAskedAgain_ByThePlatform_WhenNoPersonActs_BR109()
    {
        var dueDiligence = Enhanced();
        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today, Today.AddYears(1), Guid.CreateVersion7(), Now);
        dueDiligence.DecideEnhanced(approved: false, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        dueDiligence.Assess([], anyCheck: true, freshSinceExpiry: false, Today, Today.AddYears(1), actor: null, Now);
        dueDiligence.DomainEvents.OfType<EnhancedDueDiligenceApprovalRequested>().Select(e => e.RequestedBy).Last().ShouldBe(MakerChecker.SystemMaker);
    }
}
