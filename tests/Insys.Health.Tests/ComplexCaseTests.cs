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
using Shouldly;

namespace Insys.Health.Tests;

/// <summary>Wave 5a §4.3 to §4.5 (F034 to F038): a complex case's life, and a referral's.</summary>
public sealed class ComplexCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 1);
    private static readonly Guid Staff = Guid.CreateVersion7();

    private static ComplexCase Case() =>
        ComplexCase.Open(Guid.CreateVersion7(), "CC-1", Guid.CreateVersion7(), Guid.CreateVersion7(), ComplexCaseOrigin.Membership, null, "Condition");

    [Fact]
    public void Reassigning_NeedsAReason_AndEndsTheLastAssignment_F035()
    {
        var c = Case();
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        c.AssignManager(first, Staff, null, Now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(ComplexCaseStatus.Active);
        c.AssignManager(first, Staff, "Again", Now).IsFailure.ShouldBeTrue("already the manager");
        c.AssignManager(second, Staff, " ", Now).IsFailure.ShouldBeTrue("needs a reason");
        c.CaseManagerId.ShouldBe(first);

        c.AssignManager(second, Staff, "Leave", Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        c.CaseManagerId.ShouldBe(second);
        c.Assignments.Single(a => a.CaseManagerId == first).EndedAt.ShouldBe(Now.AddDays(1));
        c.Assignments.Single(a => a.CaseManagerId == second).EndedAt.ShouldBeNull();
    }

    [Fact]
    public void Changing_APlan_NeedsAReason_AndKeepsTheLastVersion_F036()
    {
        var c = Case();
        c.SetPlan([new(CarePlanItemKind.Activity, "No date", null, null)], null).IsFailure.ShouldBeTrue("an activity has a date");
        c.SetPlan([new(CarePlanItemKind.Goal, "Goal", null, null)], null).IsSuccess.ShouldBeTrue();
        c.SetPlan([new(CarePlanItemKind.Goal, "Better goal", null, null)], null).IsFailure.ShouldBeTrue("a change needs a reason");
        c.SetPlan([new(CarePlanItemKind.Goal, "Better goal", null, null)], "Reassessed").IsSuccess.ShouldBeTrue();
        c.Plans.Count.ShouldBe(2);
        c.Plans.Count(p => p.IsCurrent).ShouldBe(1);
        c.CurrentPlan!.PlanVersion.ShouldBe(2);
    }

    [Fact]
    public void AnOverdueActivity_RaisesOnce_OnlyWhileTheCaseIsActive_WithoutItsText_F037()
    {
        var c = Case();
        c.AssignManager(Staff, Staff, null, Now);
        c.SetPlan([new(CarePlanItemKind.Activity, "Clinical text", null, Today.AddDays(-1)), new(CarePlanItemKind.Activity, "Later", null, Today.AddDays(1))], null);

        c.Hold();
        c.RaiseOverdue(Today, Now).ShouldBe(0, "on hold");
        c.Resume();
        c.RaiseOverdue(Today, Now).ShouldBe(1);
        c.RaiseOverdue(Today, Now).ShouldBe(0, "once");
        c.DomainEvents.OfType<CareActivityOverdue>().Single().ToString().ShouldNotContain("Clinical text");
    }

    [Fact]
    public void ANoteSharedWithAProvider_NamesTheConsent_AndAClosedCaseTakesNoMore()
    {
        var c = Case();
        c.AddNote("PROVIDER", null, "Note", "PR-1", null, Staff, Now).IsFailure.ShouldBeTrue();
        c.AddNote("PROVIDER", null, "Note", "PR-1", "CONSENT-1", Staff, Now).IsSuccess.ShouldBeTrue();
        c.Close("Done").IsSuccess.ShouldBeTrue();
        c.AddNote("INTERNAL", null, "Note", null, null, Staff, Now).IsFailure.ShouldBeTrue();
        c.AssignManager(Staff, Staff, null, Now).IsFailure.ShouldBeTrue();
    }

    private static ClinicalReferral Referral(int visits = 2) => ClinicalReferral.Request(Guid.CreateVersion7(), "RF-1", Guid.CreateVersion7(), Guid.CreateVersion7(), "PR-1", null, "NEPHROLOGY",
        "Reason", Today, Today.AddDays(10), visits, null, null).Value;

    [Fact]
    public void AReferral_IsDecidedThenUsedUp_WithinItsDates_F038()
    {
        var r = Referral();
        r.RecordVisit(Today).IsFailure.ShouldBeTrue("not approved");
        r.Decide(false, null, Staff, Now).IsFailure.ShouldBeTrue("a decline needs a reason");
        r.Decide(true, null, Staff, Now).IsSuccess.ShouldBeTrue();
        r.RecordVisit(Today.AddDays(11)).IsFailure.ShouldBeTrue("past its date");
        r.RecordVisit(Today).IsSuccess.ShouldBeTrue();
        r.Status.ShouldBe(ReferralStatus.Approved);
        r.RecordVisit(Today).IsSuccess.ShouldBeTrue();
        r.Status.ShouldBe(ReferralStatus.Used);
    }

    [Theory]
    [InlineData("PR-1", null, true)] // its writer
    [InlineData("PR-2", null, false)] // its receiver, without consent
    [InlineData("PR-2", "CONSENT-1", true)]
    [InlineData("PR-3", "CONSENT-1", false)] // anyone else
    public void AReferralsReason_IsOpenToItsWriter_AndToItsReceiverWithConsent_F038(string provider, string? consent, bool open)
    {
        var r = ClinicalReferral.Request(Guid.CreateVersion7(), "RF-1", Guid.CreateVersion7(), Guid.CreateVersion7(), "PR-1", "PR-2", null, "Reason", Today, Today, 1, null, null, consent).Value;
        r.ReasonOpenTo(provider).ShouldBe(open);
    }

    [Fact]
    public void AReferral_ExpiresOnlyPastItsDate_AndNeedsADestination_F038()
    {
        var r = Referral();
        r.Expire(Today.AddDays(10)).IsFailure.ShouldBeTrue();
        r.Expire(Today.AddDays(11)).IsSuccess.ShouldBeTrue();
        r.Status.ShouldBe(ReferralStatus.Expired);
        ClinicalReferral.Request(Guid.CreateVersion7(), "RF-2", Guid.CreateVersion7(), Guid.CreateVersion7(), "PR-1", null, " ", "Reason", Today, Today, 1, null, null).IsFailure.ShouldBeTrue();
    }
}
