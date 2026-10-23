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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Access;
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Application.Access;

public interface IAccessRepository
{
    Task<PrivilegedAccessGrant?> Grant(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GrantsToExpire(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    void Add(PrivilegedAccessGrant grant);

    Task<AccessReview?> Review(Guid id, CancellationToken cancellationToken);

    /// <summary>Every role assignment in effect today, as (user, role).</summary>
    Task<IReadOnlyList<(Guid UserId, string RoleCode)>> EffectiveAssignments(DateOnly today, CancellationToken cancellationToken);

    void Add(AccessReview review);
}

public sealed class PrivilegedAccessOptions
{
    /// <summary>TODO(spec): TEN-007 asks for time-limited access but gives no maximum.</summary>
    public TimeSpan MaxWindow { get; set; } = TimeSpan.FromHours(8);

    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromMinutes(5);

    public bool ExpiryEnabled { get; set; } = true;
}

public sealed record GrantView(Guid Id, Guid UserId, string Purpose, Guid RequestedBy, Guid? ApprovedBy, DateTimeOffset ValidFrom, DateTimeOffset ValidTo,
    GrantStatus Status, DateTimeOffset? DecidedAt, string? RejectionReason, long RowVersion);

public sealed record ReviewItemView(Guid Id, Guid UserId, string RoleCode, string Decision, Guid? DecidedBy, DateTimeOffset? DecidedAt);

public sealed record ReviewView(Guid Id, string Scope, Guid ReviewerId, DateOnly DueDate, ReviewStatus Status, int ItemsTotal, int ItemsRevoked,
    IReadOnlyList<ReviewItemView> Items, long RowVersion);

public interface IAccessQueries
{
    Task<IReadOnlyList<GrantView>> Grants(string? status, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<GrantView?> Grant(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewView>> Reviews(Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ReviewView?> Review(Guid id, CancellationToken cancellationToken);
}

public sealed record RequestAccess(Guid UserId, string Purpose, DateTimeOffset ValidFrom, DateTimeOffset ValidTo) : ICommand<Guid>;

public sealed record ApproveAccess(Guid GrantId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RejectAccess(Guid GrantId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Command ExpireAccess, run by the expiry job when a grant's window has ended.</summary>
public sealed record ExpireAccess(Guid GrantId) : ICommand<long>;

public sealed record StartReview(ReviewScope Scope, Guid ReviewerId, DateOnly DueDate) : ICommand<Guid>;

public sealed record DecideReviewItem(Guid ReviewId, Guid ItemId, bool Confirm, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record CompleteReview(Guid ReviewId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Approved grants whose window has ended, for the expiry job.</summary>
public sealed record DueGrants(DateTimeOffset Now) : IQuery<IReadOnlyList<Guid>>;

internal sealed class DueGrantsHandler(IAccessRepository access) : IQueryHandler<DueGrants, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(DueGrants query, CancellationToken cancellationToken) =>
        Result.Success(await access.GrantsToExpire(query.Now, 500, cancellationToken));
}

public sealed record ListGrants(string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<GrantView>>;

public sealed record GetGrant(Guid GrantId) : IQuery<GrantView>;

public sealed record ListReviews(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ReviewView>>;

public sealed record GetReview(Guid ReviewId) : IQuery<ReviewView>;

public sealed class RequestAccessValidator : AbstractValidator<RequestAccess>
{
    public RequestAccessValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Purpose).NotEmpty().MaximumLength(2000);
    }
}

public sealed class RejectAccessValidator : AbstractValidator<RejectAccess>
{
    public RejectAccessValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

internal static class Actor
{
    public static Result<Guid> User(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");

    public static Result<T> Check<T>(T? aggregate, long expectedVersion, string what)
        where T : class, IAggregateRoot =>
        aggregate is null
            ? Error.NotFound(ErrorCodes.NotFound, $"The {what} was not found.")
            : aggregate.RowVersion == expectedVersion
                ? aggregate
                : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, $"The {what} has changed since it was read.");
}

internal sealed class RequestAccessHandler(IAccessRepository access, IUserRepository users, IExecutionContext execution, IClock clock, IAuditTrail audit,
    Microsoft.Extensions.Options.IOptions<PrivilegedAccessOptions> options) : ICommandHandler<RequestAccess, Guid>
{
    public async Task<Result<Guid>> Handle(RequestAccess command, CancellationToken cancellationToken)
    {
        var requester = Actor.User(execution);
        if (requester.IsFailure)
        {
            return requester.Error;
        }

        if (await users.Get(command.UserId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The user was not found.");
        }

        var grant = PrivilegedAccessGrant.RequestAccess(Guid.CreateVersion7(), command.UserId, command.Purpose, command.ValidFrom, command.ValidTo,
            requester.Value, options.Value.MaxWindow, clock.UtcNow);
        if (grant.IsFailure)
        {
            return grant.Error;
        }

        access.Add(grant.Value);
        audit.Record(new AuditEntry("PRIVILEGED_ACCESS", "RequestAccess", nameof(PrivilegedAccessGrant), grant.Value.Id.ToString(),
            new Dictionary<string, string?> { ["user"] = command.UserId.ToString(), ["validTo"] = command.ValidTo.ToString("O") }));
        return grant.Value.Id;
    }
}

internal sealed class ApproveAccessHandler(IAccessRepository access, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ApproveAccess, long>
{
    public async Task<Result<long>> Handle(ApproveAccess command, CancellationToken cancellationToken)
    {
        var approver = Actor.User(execution);
        var grant = Actor.Check(await access.Grant(command.GrantId, cancellationToken), command.ExpectedVersion, "access request");
        if (approver.IsFailure || grant.IsFailure)
        {
            return approver.IsFailure ? approver.Error : grant.Error!;
        }

        audit.Record(new AuditEntry("PRIVILEGED_ACCESS", "ApproveAccess", nameof(PrivilegedAccessGrant), command.GrantId.ToString(),
            new Dictionary<string, string?> { ["requestedBy"] = grant.Value.RequestedBy.ToString() }));
        var result = grant.Value.ApproveAccess(approver.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : grant.Value.RowVersion + 1;
    }
}

internal sealed class RejectAccessHandler(IAccessRepository access, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<RejectAccess, long>
{
    public async Task<Result<long>> Handle(RejectAccess command, CancellationToken cancellationToken)
    {
        var decider = Actor.User(execution);
        var grant = Actor.Check(await access.Grant(command.GrantId, cancellationToken), command.ExpectedVersion, "access request");
        if (decider.IsFailure || grant.IsFailure)
        {
            return decider.IsFailure ? decider.Error : grant.Error!;
        }

        audit.Record(new AuditEntry("PRIVILEGED_ACCESS", "RejectAccess", nameof(PrivilegedAccessGrant), command.GrantId.ToString()));
        var result = grant.Value.RejectAccess(decider.Value, command.Reason, clock.UtcNow);
        return result.IsFailure ? result.Error : grant.Value.RowVersion + 1;
    }
}

internal sealed class ExpireAccessHandler(IAccessRepository access, IClock clock, IAuditTrail audit) : ICommandHandler<ExpireAccess, long>
{
    public async Task<Result<long>> Handle(ExpireAccess command, CancellationToken cancellationToken)
    {
        var grant = await access.Grant(command.GrantId, cancellationToken);
        if (grant is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The grant was not found.");
        }

        audit.Record(new AuditEntry("PRIVILEGED_ACCESS", "ExpireAccess", nameof(PrivilegedAccessGrant), command.GrantId.ToString()));
        var result = grant.ExpireAccess(clock.UtcNow);
        return result.IsFailure ? result.Error : grant.RowVersion + 1;
    }
}

internal sealed class StartReviewHandler(IAccessRepository access, IUserRepository users, IRolePolicyReader policy, IExecutionContext execution, IClock clock,
    IAuditTrail audit) : ICommandHandler<StartReview, Guid>
{
    public async Task<Result<Guid>> Handle(StartReview command, CancellationToken cancellationToken)
    {
        var starter = Actor.User(execution);
        if (starter.IsFailure)
        {
            return starter.Error;
        }

        if (command.Scope == ReviewScope.ServiceAccounts)
        {
            // TODO(spec): IAM-004 service accounts are not modelled as users yet, so there is nothing to snapshot (OQ-042).
            return Error.Validation(ErrorCodes.ValidationFailed, "Service account reviews are not available until service accounts are managed.");
        }

        if (await users.Get(command.ReviewerId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The reviewer was not found.");
        }

        var today = clock.UtcToday;
        var holdings = await access.EffectiveAssignments(today, cancellationToken);
        if (command.Scope == ReviewScope.Privileged)
        {
            var privileged = await policy.PrivilegedRoles(today, cancellationToken);
            holdings = holdings.Where(h => privileged.Contains(h.RoleCode)).ToList();
        }

        var review = AccessReview.StartReview(Guid.CreateVersion7(), command.Scope, command.ReviewerId, command.DueDate, holdings, today);
        if (review.IsFailure)
        {
            return review.Error;
        }

        access.Add(review.Value);
        audit.Record(new AuditEntry("ACCESS_REVIEW", "StartReview", nameof(AccessReview), review.Value.Id.ToString(),
            new Dictionary<string, string?> { ["scope"] = command.Scope.ToString(), ["items"] = review.Value.ItemsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return review.Value.Id;
    }
}

internal sealed class DecideReviewItemHandler(IAccessRepository access, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<DecideReviewItem, long>
{
    public async Task<Result<long>> Handle(DecideReviewItem command, CancellationToken cancellationToken)
    {
        var reviewer = Actor.User(execution);
        var review = Actor.Check(await access.Review(command.ReviewId, cancellationToken), command.ExpectedVersion, "access review");
        if (reviewer.IsFailure || review.IsFailure)
        {
            return reviewer.IsFailure ? reviewer.Error : review.Error!;
        }

        audit.Record(new AuditEntry("ACCESS_REVIEW", command.Confirm ? "ConfirmAccess" : "RevokeAccess", nameof(AccessReview), command.ReviewId.ToString(),
            new Dictionary<string, string?> { ["item"] = command.ItemId.ToString() }));
        var result = command.Confirm
            ? review.Value.ConfirmAccess(command.ItemId, reviewer.Value, clock.UtcNow)
            : review.Value.RevokeAccess(command.ItemId, reviewer.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : review.Value.RowVersion + 1;
    }
}

internal sealed class CompleteReviewHandler(IAccessRepository access, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<CompleteReview, long>
{
    public async Task<Result<long>> Handle(CompleteReview command, CancellationToken cancellationToken)
    {
        var reviewer = Actor.User(execution);
        var review = Actor.Check(await access.Review(command.ReviewId, cancellationToken), command.ExpectedVersion, "access review");
        if (reviewer.IsFailure || review.IsFailure)
        {
            return reviewer.IsFailure ? reviewer.Error : review.Error!;
        }

        audit.Record(new AuditEntry("ACCESS_REVIEW", "CompleteReview", nameof(AccessReview), command.ReviewId.ToString(),
            new Dictionary<string, string?> { ["revoked"] = review.Value.ItemsRevoked.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var result = review.Value.CompleteReview(reviewer.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : review.Value.RowVersion + 1;
    }
}

internal sealed class ListGrantsHandler(IAccessQueries queries) : IQueryHandler<ListGrants, IReadOnlyList<GrantView>>
{
    public async Task<Result<IReadOnlyList<GrantView>>> Handle(ListGrants query, CancellationToken cancellationToken) =>
        Result.Success(await queries.Grants(query.Status, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetGrantHandler(IAccessQueries queries) : IQueryHandler<GetGrant, GrantView>
{
    public async Task<Result<GrantView>> Handle(GetGrant query, CancellationToken cancellationToken) =>
        await queries.Grant(query.GrantId, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The grant was not found.");
}

internal sealed class ListReviewsHandler(IAccessQueries queries) : IQueryHandler<ListReviews, IReadOnlyList<ReviewView>>
{
    public async Task<Result<IReadOnlyList<ReviewView>>> Handle(ListReviews query, CancellationToken cancellationToken) =>
        Result.Success(await queries.Reviews(query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetReviewHandler(IAccessQueries queries) : IQueryHandler<GetReview, ReviewView>
{
    public async Task<Result<ReviewView>> Handle(GetReview query, CancellationToken cancellationToken) =>
        await queries.Review(query.ReviewId, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The review was not found.");
}

/// <summary>
/// Identity's policy for its own AccessReviewCompleted: every role the reviewer revoked ends today (IAM-005). The
/// revocations of one review are applied together, in the inbox transaction, so a review is never half applied.
/// </summary>
public sealed class RevokeReviewedAccess(IUserRepository users, IAccessRepository access, IClock clock, IPermissionCache cache)
    : IIntegrationEventHandler<Contracts.AccessReviewCompleted>
{
    public async Task Handle(Contracts.AccessReviewCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // The reviewer revoked these roles (GAP-020: revoked_by).
        var reviewer = (await access.Review(completed.ReviewId, cancellationToken))?.ReviewerId
            ?? throw new InvalidOperationException($"Access review {completed.ReviewId} was not found.");
        foreach (var revoked in completed.Revoked)
        {
            if (await users.Get(revoked.UserId, cancellationToken) is { } user)
            {
                // The role may already have been revoked elsewhere; that is fine.
                user.RevokeRole(revoked.RoleCode, reviewer, clock.UtcToday, clock.UtcNow);
            }
        }

        cache.EvictTenant(envelope.TenantId);
    }
}
