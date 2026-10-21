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
        var specified = DomainModelSpec.Transitions("workflow", "Approval").Select(t => t.Replace("Time limit reached", "Expire", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        Approval.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void Approval_ByItsMaker_IsForbidden_SRS002() =>
        Requested().Approve(Maker, Authority(1000), Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);

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
}
