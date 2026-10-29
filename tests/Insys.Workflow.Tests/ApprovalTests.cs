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
using Insys.Testing;
using Insys.Workflow.Domain.Approvals;

namespace Insys.Workflow.Tests;

public sealed class ApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();

    private static Approval Requested(decimal? amount = null, DateTimeOffset? expiresAt = null) =>
        Approval.RequestApproval(Guid.CreateVersion7(), "CLAIM", Guid.CreateVersion7(), "CLAIM_PAYMENT", Maker, amount, amount is null ? null : "ZAR", "Pay",
            Now, expiresAt ?? Now.AddDays(14));

    private static AuthorityGrant[] Authority(decimal limit) => [new("CLAIMS_MANAGER", "CLAIM_PAYMENT", limit)];

    [Fact]
    public void Approval_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("workflow", "Approval").Select(t => t.Replace("Time limit reached", "Expire", StringComparison.Ordinal).Replace("Withdrawn by requester", "WithdrawnByRequester", StringComparison.Ordinal))
            // D-5, extended (25 October 2026): a granted approval its record will not take; domain-model.json has no such state.
            .Concat(["Approved-RecordRefused->NotApplied", "NotApplied-RecordApplied->Approved"])
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        Approval.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void Approval_ByItsMaker_IsForbidden_SRS002() =>
        Requested().Approve(Maker, Authority(1000), Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);

    /// <summary>BR-109: an approval the platform raised (no person acting) is decided by any person with authority, and still needs that authority.</summary>
    [Fact]
    public void AnApprovalRaisedByThePlatform_IsApprovedByAnyPersonWithAuthority_AndNeverWithout_BR109()
    {
        Approval Raised() => Approval.RequestApproval(Guid.CreateVersion7(), "CLAIM", Guid.CreateVersion7(), "CLAIM_PAYMENT", MakerChecker.SystemMaker, null, null, "Pay", Now,
            Now.AddDays(14));
        Raised().Approve(Checker, [new AuthorityGrant("CLAIMS_MANAGER", "OTHER", 1_000_000)], Now).Error!.Code.ShouldBe(WorkflowErrorCodes.AuthorityMissing);
        var raised = Raised();
        raised.Approve(Checker, Authority(1000), Now).IsSuccess.ShouldBeTrue();
        raised.Status.ShouldBe(ApprovalStatus.Approved);
        // An approval always names who requested it: a request with none fails its delivery, to be retried and seen (never created silently).
        Should.Throw<ArgumentException>(() => Approval.RequestApproval(Guid.CreateVersion7(), "CLAIM", Guid.CreateVersion7(), "CLAIM_PAYMENT", Guid.Empty, null, null, "Pay",
            Now, Now.AddDays(14)));
    }

    /// <summary>BR-109: the system requester is reserved; an attempt to approve or reject as it is refused, whoever raised the approval.</summary>
    [Fact]
    public void TheSystemRequester_NeverDecidesAnApproval_BR109()
    {
        Requested().Approve(MakerChecker.SystemMaker, Authority(1000), Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        Requested().Reject(MakerChecker.SystemMaker, "No", Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        var raised = Approval.RequestApproval(Guid.CreateVersion7(), "CLAIM", Guid.CreateVersion7(), "CLAIM_PAYMENT", MakerChecker.SystemMaker, null, null, "Pay", Now,
            Now.AddDays(14));
        raised.Approve(MakerChecker.SystemMaker, Authority(1000), Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        raised.Status.ShouldBe(ApprovalStatus.Requested);
    }

    [Fact]
    public void Rejection_ByItsMaker_IsForbidden_SRS002() =>
        Requested().Reject(Maker, "No", Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);

    [Fact]
    public void Approval_WithoutAuthorityForTheType_IsForbidden_WFL004() =>
        Requested().Approve(Checker, [new AuthorityGrant("CLAIMS_MANAGER", "OTHER", 1_000_000)], Now).Error!.Code.ShouldBe(WorkflowErrorCodes.AuthorityMissing);

    [Fact]
    public void Approval_AboveTheCheckersLimit_IsForbidden_WFL004() =>
        Requested(amount: 5000).Approve(Checker, Authority(4999.99m), Now).Error!.Code.ShouldBe(WorkflowErrorCodes.AuthorityExceeded);

    [Fact]
    public void Approval_WithinAuthority_RaisesApprovalGranted_WFL004()
    {
        var approval = Requested(amount: 5000);
        approval.Approve(Checker, [.. Authority(1000), new AuthorityGrant("HEAD_OF_CLAIMS", "CLAIM_PAYMENT", 10_000)], Now).IsSuccess.ShouldBeTrue();

        approval.Status.ShouldBe(ApprovalStatus.Approved);
        approval.CheckerId.ShouldBe(Checker);
        approval.DomainEvents.OfType<ApprovalGranted>().Single().CheckerId.ShouldBe(Checker);
    }

    [Fact]
    public void Rejection_NeedsAReason_AndRaisesApprovalRejected()
    {
        var approval = Requested();
        approval.Reject(Checker, " ", Now).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        approval.Reject(Checker, "Not supported", Now).IsSuccess.ShouldBeTrue();
        approval.DomainEvents.OfType<ApprovalRejected>().Single().Reason.ShouldBe("Not supported");
    }

    [Fact]
    public void Approval_IsDecidedOnce()
    {
        var approval = Requested();
        approval.Approve(Checker, Authority(0), Now).IsSuccess.ShouldBeTrue();
        approval.Reject(Guid.CreateVersion7(), "Late", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void Approval_PastItsTimeLimit_ExpiresAndCannotBeApproved()
    {
        var approval = Requested(expiresAt: Now.AddHours(1));
        approval.Expire(Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        approval.Approve(Checker, Authority(0), Now.AddHours(2)).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        approval.Expire(Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        approval.Status.ShouldBe(ApprovalStatus.Expired);
    }

    /// <summary>D-4: expiry reaches the owning module as an ending, with no checker and the reason, like a rejection.</summary>
    [Fact]
    public void Expiry_TellsTheOwningModule_AsAnEnding()
    {
        var approval = Requested(expiresAt: Now.AddHours(1));
        approval.Expire(Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        var ended = approval.DomainEvents.OfType<ApprovalRejected>().Single();
        ended.Ending.ShouldBe("EXPIRED");
        ended.CheckerId.ShouldBe(Guid.Empty);
        ended.Reason.ShouldStartWith("No decision by");
    }

    /// <summary>D-4: only the requester withdraws their request; the owning module is told, as for a rejection.</summary>
    [Fact]
    public void Withdrawal_ByTheRequesterOnly_TellsTheOwningModule()
    {
        var approval = Requested();
        approval.WithdrawByRequester(Checker, "Not mine", Now).Error!.Code.ShouldBe(WorkflowErrorCodes.NotRequester);
        approval.WithdrawByRequester(Maker, "Entered twice", Now).IsSuccess.ShouldBeTrue();
        approval.Status.ShouldBe(ApprovalStatus.Withdrawn);
        var ended = approval.DomainEvents.OfType<ApprovalRejected>().Single();
        (ended.Ending, ended.Reason).ShouldBe(("WITHDRAWN", "Entered twice"));
        approval.Approve(Checker, Authority(0), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    /// <summary>OQ-112: a limit counts only in its currency; a limit without one is in the tenant's base currency.</summary>
    [Fact]
    public void AuthorityLimits_CountOnlyInTheirCurrency_OQ112()
    {
        Requested(amount: 5000).Approve(Checker, [new AuthorityGrant("CLAIMS_MANAGER", "CLAIM_PAYMENT", 10_000, "USD")], Now, "ZAR").Error!.Code
            .ShouldBe(WorkflowErrorCodes.AuthorityExceeded);
        Requested(amount: 5000).Approve(Checker, [new AuthorityGrant("CLAIMS_MANAGER", "CLAIM_PAYMENT", 10_000)], Now, "ZAR").IsSuccess.ShouldBeTrue();
        Requested(amount: 5000).Approve(Checker, [new AuthorityGrant("CLAIMS_MANAGER", "CLAIM_PAYMENT", 10_000)], Now, "USD").Error!.Code
            .ShouldBe(WorkflowErrorCodes.AuthorityExceeded);
    }
}
