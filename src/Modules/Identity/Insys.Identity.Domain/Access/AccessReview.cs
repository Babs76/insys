/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Domain.Access;

public enum ReviewStatus
{
    Open,
    Completed,
}

public enum ReviewTrigger
{
    CompleteReview,
}

/// <summary>The scope of an access review (catalogue iam.cfg_access_review_schedules: Privileged, All users, Service accounts).</summary>
public enum ReviewScope
{
    AllUsers,
    Privileged,
    ServiceAccounts,
}

public enum ItemDecision
{
    Pending,
    Confirmed,
    Revoked,
}

/// <summary>Entity ReviewItem (iam.access_review_item): one role held by one user, confirmed or revoked by the reviewer.</summary>
public sealed class ReviewItem : Entity<Guid>, ITenantScoped
{
    private ReviewItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ReviewId { get; private set; }

    public Guid UserId { get; private set; }

    public string RoleCode { get; private set; } = "";

    public ItemDecision Decision { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public long RowVersion { get; private set; }

    internal static ReviewItem Create(Guid reviewId, Guid userId, string roleCode) =>
        new() { Id = Guid.CreateVersion7(), ReviewId = reviewId, UserId = userId, RoleCode = roleCode, Decision = ItemDecision.Pending };

    internal void Decide(ItemDecision decision, Guid reviewerId, DateTimeOffset now)
    {
        Decision = decision;
        DecidedBy = reviewerId;
        DecidedAt = now;
    }
}

public sealed record AccessReviewCompleted(Guid ReviewId, IReadOnlyList<(Guid UserId, string RoleCode)> Revoked, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The AccessReview aggregate: a reviewer confirms or revokes each role held in the scope. Every item is confirmed or
/// revoked before completion; the revocations take effect when the review completes (IAM-005).
/// </summary>
public sealed class AccessReview : AggregateRoot<Guid>
{
    public static readonly StateMachine<ReviewStatus, ReviewTrigger> Lifecycle = new StateMachine<ReviewStatus, ReviewTrigger>()
        .Allow(ReviewStatus.Open, ReviewTrigger.CompleteReview, ReviewStatus.Completed);

    private readonly List<ReviewItem> _items = [];

    private AccessReview()
    {
    }

    public ReviewScope Scope { get; private set; }

    public Guid ReviewerId { get; private set; }

    public DateOnly DueDate { get; private set; }

    public ReviewStatus Status { get; private set; }

    public int ItemsTotal { get; private set; }

    public int ItemsRevoked { get; private set; }

    public IReadOnlyList<ReviewItem> Items => _items;

    /// <summary>Command StartReview, over a snapshot of the roles held in scope.</summary>
    public static Result<AccessReview> StartReview(Guid id, ReviewScope scope, Guid reviewerId, DateOnly dueDate, IEnumerable<(Guid UserId, string RoleCode)> holdings,
        DateOnly today)
    {
        if (dueDate < today)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The due date cannot be in the past.");
        }

        var review = new AccessReview { Id = Guard.NotEmpty(id), Scope = scope, ReviewerId = Guard.NotEmpty(reviewerId), DueDate = dueDate, Status = ReviewStatus.Open };
        foreach (var (userId, roleCode) in holdings.Distinct())
        {
            review._items.Add(ReviewItem.Create(id, userId, roleCode));
        }

        if (review._items.Count == 0)
        {
            return Error.Validation(IdentityErrorCodes.NothingToReview, "No role assignments are in scope for this review.");
        }

        review.ItemsTotal = review._items.Count;
        review.MarkChanged();
        return review;
    }

    /// <summary>Command ConfirmAccess.</summary>
    public Result ConfirmAccess(Guid itemId, Guid reviewerId, DateTimeOffset now) => Decide(itemId, ItemDecision.Confirmed, reviewerId, now);

    /// <summary>Command RevokeAccess. The role is revoked when the review completes.</summary>
    public Result RevokeAccess(Guid itemId, Guid reviewerId, DateTimeOffset now) => Decide(itemId, ItemDecision.Revoked, reviewerId, now);

    /// <summary>Command CompleteReview: every item is confirmed or revoked first (invariant in domain-model.json).</summary>
    public Result CompleteReview(Guid reviewerId, DateTimeOffset now)
    {
        if (reviewerId != ReviewerId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the assigned reviewer completes the review.");
        }

        if (_items.Any(i => i.Decision == ItemDecision.Pending))
        {
            return Error.Conflict(IdentityErrorCodes.ReviewIncomplete, "Every item is confirmed or revoked before the review completes.");
        }

        var next = Lifecycle.Fire(Status, ReviewTrigger.CompleteReview);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        Raise(new AccessReviewCompleted(Id, _items.Where(i => i.Decision == ItemDecision.Revoked).Select(i => (i.UserId, i.RoleCode)).ToList(), now));
        return Result.Success();
    }

    private Result Decide(Guid itemId, ItemDecision decision, Guid reviewerId, DateTimeOffset now)
    {
        if (reviewerId != ReviewerId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the assigned reviewer decides review items.");
        }

        if (Status != ReviewStatus.Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The review is already completed.");
        }

        var item = _items.SingleOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The review item was not found.");
        }

        if (item.Decision != ItemDecision.Pending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "This item has already been decided.");
        }

        item.Decide(decision, reviewerId, now);
        ItemsRevoked = _items.Count(i => i.Decision == ItemDecision.Revoked);
        MarkChanged();
        return Result.Success();
    }
}
