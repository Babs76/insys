using Insys.BuildingBlocks.Domain;
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Domain.Access;

/// <summary>States from domain-model.json (iam › PrivilegedAccessGrant), stored upper-case in iam.privileged_access_grant.status.</summary>
public enum GrantStatus
{
    Requested,
    Approved,
    Rejected,
    Expired,

    /// <summary>Allowed by the table (V0007) but reached by no transition in domain-model.json (OQ-041).</summary>
    Revoked,
}

public enum GrantTrigger
{
    ApproveAccess,
    RejectAccess,
    ExpireAccess,
}

/// <summary>
/// The PrivilegedAccessGrant aggregate: administrative access to tenant data, approved by someone other than the
/// requester, time-limited and with its purpose recorded (TEN-007).
/// </summary>
public sealed class PrivilegedAccessGrant : AggregateRoot<Guid>
{
    public static readonly StateMachine<GrantStatus, GrantTrigger> Lifecycle = new StateMachine<GrantStatus, GrantTrigger>()
        .Allow(GrantStatus.Requested, GrantTrigger.ApproveAccess, GrantStatus.Approved)
        .Allow(GrantStatus.Requested, GrantTrigger.RejectAccess, GrantStatus.Rejected)
        .Allow(GrantStatus.Approved, GrantTrigger.ExpireAccess, GrantStatus.Expired);

    private PrivilegedAccessGrant()
    {
    }

    /// <summary>The user who receives the access.</summary>
    public Guid UserId { get; private set; }

    public string Purpose { get; private set; } = "";

    public Guid RequestedBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset ValidTo { get; private set; }

    public GrantStatus Status { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? RejectionReason { get; private set; }

    /// <summary>Approved and inside its time window.</summary>
    public bool IsActiveAt(DateTimeOffset now) => Status == GrantStatus.Approved && ValidFrom <= now && now < ValidTo;

    /// <summary>Command RequestAccess. The window must be in the future and no longer than <paramref name="maxWindow"/>.</summary>
    public static Result<PrivilegedAccessGrant> RequestAccess(Guid id, Guid userId, string purpose, DateTimeOffset validFrom, DateTimeOffset validTo,
        Guid requestedBy, TimeSpan maxWindow, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Privileged access needs a recorded purpose (TEN-007).");
        }

        if (validTo <= validFrom || validTo <= now)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The access window must end after it starts, and in the future.");
        }

        if (validTo - validFrom > maxWindow)
        {
            return Error.Validation(IdentityErrorCodes.AccessWindowTooLong, $"Privileged access is time-limited to at most {maxWindow} (TEN-007).");
        }

        return new PrivilegedAccessGrant
        {
            Id = Guard.NotEmpty(id),
            UserId = Guard.NotEmpty(userId),
            Purpose = purpose,
            ValidFrom = validFrom,
            ValidTo = validTo,
            RequestedBy = Guard.NotEmpty(requestedBy),
            Status = GrantStatus.Requested,
        };
    }

    /// <summary>Command ApproveAccess: by someone other than the requester (TEN-007).</summary>
    public Result ApproveAccess(Guid approverId, DateTimeOffset now)
    {
        if (approverId == RequestedBy)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "Privileged access is approved by someone other than the requester (TEN-007).");
        }

        if (now >= ValidTo)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The requested window has already ended.");
        }

        var result = Fire(GrantTrigger.ApproveAccess);
        if (result.IsSuccess)
        {
            ApprovedBy = approverId;
            DecidedAt = now;

            // PrivilegedAccessGranted (provenance P) is not raised: nothing consumes it yet (PROMPT §5).
        }

        return result;
    }

    /// <summary>Command RejectAccess, with a reason.</summary>
    public Result RejectAccess(Guid deciderId, string reason, DateTimeOffset now)
    {
        if (deciderId == RequestedBy)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "A request is decided by someone other than the requester (TEN-007).");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A rejection needs a reason.");
        }

        var result = Fire(GrantTrigger.RejectAccess);
        if (result.IsSuccess)
        {
            DecidedAt = now;
            RejectionReason = reason;
        }

        return result;
    }

    /// <summary>Command ExpireAccess: transition "Window ends". Refused before the window has ended.</summary>
    public Result ExpireAccess(DateTimeOffset now) =>
        now < ValidTo ? Error.Conflict(ErrorCodes.InvalidStateTransition, "The access window has not ended yet.") : Fire(GrantTrigger.ExpireAccess);

    private Result Fire(GrantTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
