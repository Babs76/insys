using Insys.BuildingBlocks.Domain;
using Insys.Identity.Domain.Access;
using Insys.Testing;

namespace Insys.Identity.Tests;

public sealed class PrivilegedAccessGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();

    private static PrivilegedAccessGrant Grant(TimeSpan? window = null) =>
        PrivilegedAccessGrant.RequestAccess(Guid.CreateVersion7(), Guid.CreateVersion7(), "INC-42", Now.AddMinutes(5), Now.AddMinutes(5) + (window ?? TimeSpan.FromHours(1)),
            Requester, TimeSpan.FromHours(8), Now).Value;

    [Fact]
    public void PrivilegedAccessGrant_Lifecycle_MatchesDomainModel()
    {
        var implemented = PrivilegedAccessGrant.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList();
        var specified = DomainModelSpec.Transitions("iam", "PrivilegedAccessGrant").Select(t => t.Replace("Window ends", "ExpireAccess", StringComparison.Ordinal)).ToList();

        implemented.ShouldBe(specified);
    }

    [Fact]
    public void Grant_ApprovedByTheRequester_IsForbidden_TEN007() =>
        Grant().ApproveAccess(Requester, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);

    [Fact]
    public void Grant_ApprovedBySomeoneElse_IsActiveOnlyInsideItsWindow_TEN007()
    {
        var grant = Grant();
        grant.ApproveAccess(Approver, Now).IsSuccess.ShouldBeTrue();

        grant.IsActiveAt(Now).ShouldBeFalse();
        grant.IsActiveAt(Now.AddMinutes(30)).ShouldBeTrue();
        grant.IsActiveAt(Now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void Grant_NeedsAPurpose_AndALimitedWindow_TEN007()
    {
        PrivilegedAccessGrant.RequestAccess(Guid.CreateVersion7(), Guid.CreateVersion7(), " ", Now.AddMinutes(1), Now.AddHours(1), Requester, TimeSpan.FromHours(8), Now)
            .Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        PrivilegedAccessGrant.RequestAccess(Guid.CreateVersion7(), Guid.CreateVersion7(), "x", Now.AddMinutes(1), Now.AddHours(9), Requester, TimeSpan.FromHours(8), Now)
            .Error!.Code.ShouldBe("ACCESS_WINDOW_TOO_LONG");
    }

    [Fact]
    public void Grant_ExpiresOnlyAfterItsWindowEnds()
    {
        var grant = Grant();
        grant.ApproveAccess(Approver, Now);

        grant.ExpireAccess(Now.AddMinutes(30)).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        grant.ExpireAccess(Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        grant.Status.ShouldBe(GrantStatus.Expired);
    }

    [Fact]
    public void Requested_CannotExpire_AndRejected_CannotBeApproved_IsInvalidStateTransition()
    {
        Grant().ExpireAccess(Now.AddHours(2)).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        var rejected = Grant();
        rejected.RejectAccess(Approver, "No", Now).IsSuccess.ShouldBeTrue();
        rejected.ApproveAccess(Approver, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }
}

public sealed class AccessReviewTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Reviewer = Guid.CreateVersion7();
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();

    private static AccessReview Review() =>
        AccessReview.StartReview(Guid.CreateVersion7(), ReviewScope.AllUsers, Reviewer, Today.AddDays(7), [(UserA, "CLERK"), (UserB, "ADMIN")], Today).Value;

    [Fact]
    public void AccessReview_Lifecycle_MatchesDomainModel() =>
        AccessReview.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").ToList().ShouldBe(DomainModelSpec.Transitions("iam", "AccessReview"));

    [Fact]
    public void Review_CannotComplete_UntilEveryItemIsDecided()
    {
        var review = Review();
        review.ConfirmAccess(review.Items[0].Id, Reviewer, Now);

        review.CompleteReview(Reviewer, Now).Error!.Code.ShouldBe("REVIEW_INCOMPLETE");
    }

    [Fact]
    public void Review_OnCompletion_ReportsTheRevokedAccess_IAM005()
    {
        var review = Review();
        review.ConfirmAccess(review.Items.Single(i => i.UserId == UserA).Id, Reviewer, Now);
        review.RevokeAccess(review.Items.Single(i => i.UserId == UserB).Id, Reviewer, Now);

        review.CompleteReview(Reviewer, Now).IsSuccess.ShouldBeTrue();

        review.ItemsRevoked.ShouldBe(1);
        review.DomainEvents.OfType<AccessReviewCompleted>().Single().Revoked.ShouldBe([(UserB, "ADMIN")]);
    }

    [Fact]
    public void Review_ItemsAreDecidedOnce_AndOnlyByTheReviewer()
    {
        var review = Review();
        var item = review.Items[0].Id;

        review.ConfirmAccess(item, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        review.ConfirmAccess(item, Reviewer, Now).IsSuccess.ShouldBeTrue();
        review.RevokeAccess(item, Reviewer, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void CompletedReview_CannotCompleteAgain_IsInvalidStateTransition()
    {
        var review = Review();
        foreach (var item in review.Items)
        {
            review.ConfirmAccess(item.Id, Reviewer, Now);
        }

        review.CompleteReview(Reviewer, Now).IsSuccess.ShouldBeTrue();
        review.CompleteReview(Reviewer, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void Review_WithNothingInScope_IsRefused() =>
        AccessReview.StartReview(Guid.CreateVersion7(), ReviewScope.Privileged, Reviewer, Today, [], Today).Error!.Code.ShouldBe("NOTHING_TO_REVIEW");
}
