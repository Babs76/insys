/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow.Tests
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
using Insys.Workflow.Domain.Cases;

namespace Insys.Workflow.Tests;

/// <summary>The ServiceCase lifecycle and rules (addenda/cases.md §4, OQ-101 to OQ-110).</summary>
public sealed class ServiceCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Decider = Guid.CreateVersion7();
    private static readonly CaseTypeRules Complaint = new("COMPLAINT", "COMPLAINT", ["POLICY", "CLAIM"], RequiresCategory: true);
    private static readonly CaseTypeRules Enquiry = new("ENQUIRY", "ENQUIRY", ["POLICY"], RequiresCategory: false);
    private static readonly CaseTypeRules Appeal = new("REVIEW", "APPEAL", ["POLICY"], RequiresCategory: false);

    private static ServiceCase Open(CaseTypeRules type, string? category = "SERVICE", Guid? parent = null) =>
        ServiceCase.Open(Guid.CreateVersion7(), "CASE-000001", type, Now, "PHONE", new Complainant(Guid.CreateVersion7(), null, null, null),
            new CaseRaiser("STAFF", Decider, null, null), category, "Summary", false, parent, Now.AddDays(2), "staff", Now).Value;

    private static ServiceCase InProgress(CaseTypeRules type)
    {
        var c = Open(type);
        c.Acknowledge(Now.AddDays(10), "staff", Now).IsSuccess.ShouldBeTrue();
        c.Start("staff", Now).IsSuccess.ShouldBeTrue();
        return c;
    }

    [Fact]
    public void Lifecycle_IsTheAddendumsStatuses_CasesSection4()
    {
        var transitions = ServiceCase.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").Order(StringComparer.Ordinal).ToList();
        transitions.ShouldBe(new[]
        {
            "Received-Acknowledge->Acknowledged", "Acknowledged-Start->InProgress", "Reopened-Start->InProgress", "InProgress-AwaitComplainant->AwaitingComplainant",
            "AwaitingComplainant-Resume->InProgress", "InProgress-Decide->Decided", "Decided-Close->Closed", "Received-CloseAnswered->Closed",
            "Received-Withdraw->Withdrawn", "Acknowledged-Withdraw->Withdrawn", "InProgress-Withdraw->Withdrawn", "AwaitingComplainant-Withdraw->Withdrawn",
            "Reopened-Withdraw->Withdrawn", "Closed-Reopen->Reopened",
        }.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void AComplaintTypeRequiringACategory_NeedsOne_PPR18_5_3() =>
        ServiceCase.Open(Guid.CreateVersion7(), "CASE-000001", Complaint, Now, "PHONE", new Complainant(Guid.CreateVersion7(), null, null, null),
            new CaseRaiser("STAFF", null, null, null), null, "Summary", false, null, null, "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.CategoryRequired);

    [Fact]
    public void ACase_NeedsAComplainant() =>
        ServiceCase.Open(Guid.CreateVersion7(), "CASE-000001", Enquiry, Now, "PHONE", new Complainant(null, null, null, null),
            new CaseRaiser("STAFF", null, null, null), null, "Summary", false, null, null, "staff", Now).IsFailure.ShouldBeTrue();

    [Fact]
    public void ALinkType_TheCaseTypeDoesNotAllow_IsRefused()
    {
        var c = Open(Complaint);
        c.LinkRecord(Complaint, "INVOICE", Guid.CreateVersion7(), "INV-1", "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.LinkNotAllowed);
        c.LinkRecord(Complaint, "POLICY", Guid.CreateVersion7(), "POL-1", "staff", Now).IsSuccess.ShouldBeTrue();
        c.Links.Count.ShouldBe(1);
    }

    [Fact]
    public void ARejection_NeedsReasons_PPR18_7_2()
    {
        var c = InProgress(Complaint);
        c.Decide("REJECTED", " ", Decider, null, Now).Error!.Code.ShouldBe(CaseErrorCodes.ReasonsRequired);
        c.Decide("REJECTED", "Not covered", Decider, null, Now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(CaseStatus.Decided);
        c.DecidedBy.ShouldBe(Decider);
    }

    [Fact]
    public void AnEnquiry_IsAnswered_AndAComplaintIsNot()
    {
        InProgress(Complaint).Decide("ANSWERED", null, Decider, null, Now).IsFailure.ShouldBeTrue();
        InProgress(Enquiry).Decide("ANSWERED", null, Decider, null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AnEnquiry_ClosesStraightFromReceived_AsAnswered()
    {
        var c = Open(Enquiry);
        c.Close(null, new DateOnly(2031, 10, 26), Decider, "staff", Now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(CaseStatus.Closed);
        c.Outcome.ShouldBe("ANSWERED");
        c.Reportable.ShouldBeNull();
    }

    [Fact]
    public void AComplaint_DoesNotCloseFromReceived() =>
        Open(Complaint).Close(true, new DateOnly(2031, 10, 26), Decider, "staff", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

    [Fact]
    public void AnAppeal_IsDecidedBySomeoneElse_PPR18_6()
    {
        var c = InProgress(Appeal);
        c.Decide("UPHELD", null, Decider, appealedDecidedBy: Decider, Now).Error!.Code.ShouldBe(CaseErrorCodes.DeciderSamePerson);
        c.Decide("UPHELD", null, Guid.CreateVersion7(), appealedDecidedBy: Decider, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AClosedCase_TakesNoChange()
    {
        var c = InProgress(Complaint);
        c.Decide("UPHELD", null, Decider, null, Now).IsSuccess.ShouldBeTrue();
        c.Close(true, new DateOnly(2031, 10, 26), Decider, "staff", Now).IsSuccess.ShouldBeTrue();
        c.AddMessage(CaseMessage.Out, "STAFF", Decider, "Hello", "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.Closed);
        c.LinkRecord(Complaint, "POLICY", Guid.CreateVersion7(), "P", "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.Closed);
        c.Escalate(1, "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.Closed);
        c.Withdraw("No", new DateOnly(2031, 1, 1), "staff", Now).Error!.Code.ShouldBe(CaseErrorCodes.Closed);
    }

    [Fact]
    public void Reopening_ClearsTheDecisionAndRetention_AndWorkResumes()
    {
        var c = InProgress(Complaint);
        c.Decide("REJECTED", "Reasons", Decider, null, Now).IsSuccess.ShouldBeTrue();
        c.Close(true, new DateOnly(2031, 10, 26), Decider, "staff", Now).IsSuccess.ShouldBeTrue();
        c.Reopen("New evidence", Now.AddDays(5), "staff", Now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(CaseStatus.Reopened);
        (c.Outcome, c.RetainUntil, c.ClosedAt, c.Reportable).ShouldBe((null, null, null, null));
        c.Start("staff", Now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(CaseStatus.InProgress);
    }

    [Fact]
    public void Withdrawal_FromAnyOpenStatus_EndsTheCase()
    {
        var c = InProgress(Complaint);
        c.AwaitComplainant(true, "staff", Now).IsSuccess.ShouldBeTrue();
        c.Withdraw("Complainant withdrew", new DateOnly(2031, 10, 26), "staff", Now).IsSuccess.ShouldBeTrue();
        (c.Status, c.Outcome, c.IsOpen).ShouldBe((CaseStatus.Withdrawn, "WITHDRAWN", false));
    }

    [Fact]
    public void AnOmbudReferral_IsOfADecidedCase_AndKeepsItOpenForTheOutcome()
    {
        var c = InProgress(Complaint);
        c.ReferToOmbud("NFO", "NFO-1", Now, "staff", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        c.Decide("REJECTED", "Reasons", Decider, null, Now).IsSuccess.ShouldBeTrue();
        c.ReferToOmbud("NFO", "NFO-1", Now, "staff", Now).IsSuccess.ShouldBeTrue();
        c.Close(true, new DateOnly(2031, 10, 26), Decider, "staff", Now).IsFailure.ShouldBeTrue();
        c.RecordOmbudOutcome("DISMISSED", Now, "staff", Now).IsSuccess.ShouldBeTrue();
        c.Close(true, new DateOnly(2031, 10, 26), Decider, "staff", Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Extension_NeedsAReason_AndTellsTheComplainant_PPR18_9()
    {
        var c = InProgress(Complaint);
        c.Extend("RESOLVE", "P5D", Now.AddDays(15), " ", "staff", Now).IsFailure.ShouldBeTrue();
        c.Extend("RESOLVE", "P5D", Now.AddDays(15), "Awaiting a report", "staff", Now).IsSuccess.ShouldBeTrue();
        c.ExpectedBy.ShouldBe(Now.AddDays(15));
        c.DomainEvents.OfType<CaseUpdated>().Single().Reason.ShouldBe("EXTENDED");
    }

    [Fact]
    public void Overdue_IsTold_OnlyWhenPastItsDate()
    {
        var c = Open(Complaint);
        c.MarkOverdue("job", Now).IsFailure.ShouldBeTrue();
        c.MarkOverdue("job", Now.AddDays(3)).IsSuccess.ShouldBeTrue();
        c.DomainEvents.OfType<CaseUpdated>().Single().Reason.ShouldBe("OVERDUE");
    }

    [Fact]
    public void Events_CarryIdentifiersOnly_NeverTheCasesText_INT007()
    {
        var c = InProgress(Complaint);
        c.LinkRecord(Complaint, "POLICY", Guid.CreateVersion7(), "POL-9", "staff", Now);
        c.AnnounceOpened(Now);
        c.Decide("REJECTED", "Sensitive reasons", Decider, null, Now);
        foreach (var e in c.DomainEvents)
        {
            e.ToString()!.ShouldNotContain("Summary");
            e.ToString()!.ShouldNotContain("Sensitive reasons");
        }

        c.DomainEvents.OfType<CaseOpened>().Single().Links.Single().Reference.ShouldBe("POL-9");
    }

    [Fact]
    public void Redress_IsRequestedForApproval_ThenApprovedOrRejected_ThenPaid_OQ104()
    {
        var c = InProgress(Complaint);
        var redress = CaseRedress.Request(Guid.CreateVersion7(), c, "GOODWILL", 500m, "ZAR", Guid.CreateVersion7(), null, Decider, Now).Value;
        redress.DomainEvents.OfType<RedressApprovalRequested>().Single().Amount.ShouldBe(500m);
        redress.MarkPaid(Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
        redress.Approve(Guid.CreateVersion7(), "another subject", c.CaseNumber, Now).IsFailure.ShouldBeTrue();
        var checker = Guid.CreateVersion7();
        redress.Approve(checker, redress.SubjectHash, c.CaseNumber, Now).IsSuccess.ShouldBeTrue();
        redress.DomainEvents.OfType<CaseRedressApproved>().Single().Amount.ShouldBe(500m);
        redress.Reject(checker, "REJECTED", Now).IsFailure.ShouldBeTrue();
        redress.MarkPaid(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        redress.Status.ShouldBe(RedressStatus.Paid);
    }

    [Theory]
    [InlineData("SOMETHING", 10, "ZAR")]
    [InlineData("GOODWILL", 0, "ZAR")]
    [InlineData("GOODWILL", 10.005, "ZAR")]
    [InlineData("GOODWILL", 10, "RAND")]
    public void Redress_IsATypeAndAnAmountToTheCentInACurrency(string type, double amount, string currency) =>
        CaseRedress.Request(Guid.CreateVersion7(), InProgress(Complaint), type, (decimal)amount, currency, Guid.CreateVersion7(), null, Decider, Now).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData("P5Y", 2031, 10, 26)]
    [InlineData("P1M", 2026, 11, 26)]
    [InlineData("P10D", 2026, 11, 5)]
    public void IsoDurations_AddCalendarTime(string iso, int year, int month, int day) =>
        IsoDurations.Add(new DateOnly(2026, 10, 26), iso).ShouldBe(new DateOnly(year, month, day));

    [Fact]
    public void IsoDurations_RejectWhatIsNotADuration()
    {
        IsoDurations.IsValid("PT4H").ShouldBeTrue();
        IsoDurations.IsValid("5 days").ShouldBeFalse();
        IsoDurations.IsValid("P").ShouldBeFalse();
    }
}
