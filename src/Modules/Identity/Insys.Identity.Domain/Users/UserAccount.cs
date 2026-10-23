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

namespace Insys.Identity.Domain.Users;

/// <summary>States from domain-model.json (iam › UserAccount), stored upper-case in iam.user_account.status.</summary>
public enum UserStatus
{
    Invited,
    Active,
    Locked,
    Disabled,
}

public enum UserTrigger
{
    FirstSignIn,
    LockUser,
    UnlockUser,
    DisableUser,
}

/// <summary>iam.user_role_assignment.status (V0013, V0025). Only an Active assignment gives permissions.</summary>
public enum AssignmentStatus
{
    PendingApproval,
    Active,
    Rejected,
    Revoked,
}

/// <summary>
/// Entity RoleAssignment (iam.user_role_assignment). A revoked assignment is Revoked, with when and by whom, so it
/// stops applying at once (IAM-005, GAP-020). A privileged assignment is pending until a second person approves it in Workflow (SRS-002, ADR-009).
/// </summary>
public sealed class RoleAssignment : Entity<Guid>, ITenantScoped
{
    private RoleAssignment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string RoleCode { get; private set; } = "";

    public string[]? ScopeProducts { get; private set; }

    public string[]? ScopeBranches { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public Guid AssignedBy { get; private set; }

    public AssignmentStatus Status { get; private set; }

    /// <summary>The checker of a privileged assignment.</summary>
    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public long RowVersion { get; private set; }

    /// <summary>In effect on <paramref name="day"/>: active, valid_from inclusive, valid_to exclusive.</summary>
    public bool IsEffectiveOn(DateOnly day) => Status == AssignmentStatus.Active && ValidFrom <= day && (ValidTo is null || ValidTo > day);

    internal static RoleAssignment Create(Guid userId, string roleCode, DateOnly validFrom, DateOnly? validTo, string[]? scopeProducts, string[]? scopeBranches,
        Guid assignedBy, bool pending) =>
        new()
        {
            Id = Guid.CreateVersion7(), UserId = userId, RoleCode = roleCode, ValidFrom = validFrom, ValidTo = validTo,
            ScopeProducts = scopeProducts, ScopeBranches = scopeBranches, AssignedBy = assignedBy,
            Status = pending ? AssignmentStatus.PendingApproval : AssignmentStatus.Active,
        };

    internal void Decide(bool approved, Guid checkerId, DateTimeOffset now)
    {
        Status = approved ? AssignmentStatus.Active : AssignmentStatus.Rejected;
        ApprovedBy = approved ? checkerId : null;
        DecidedAt = now;
    }

    /// <summary>
    /// Revoked from now. The validity period also ends today where it began before today; an assignment that began today
    /// (or begins later) keeps its dates, since an empty period is not valid; its status ends it.
    /// </summary>
    internal void Revoke(Guid revokedBy, DateOnly today, DateTimeOffset now)
    {
        Status = AssignmentStatus.Revoked;
        RevokedAt = now;
        RevokedBy = revokedBy;
        if (ValidFrom < today && (ValidTo is null || ValidTo > today))
        {
            ValidTo = today;
        }
    }
}

/// <summary>Entity Session (iam.session). Sessions are issued by the identity provider; disabling a user revokes them.</summary>
public sealed class Session : Entity<Guid>, ITenantScoped
{
    private Session()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public long RowVersion { get; private set; }

    internal void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is null && ExpiresAt > now)
        {
            RevokedAt = now;
        }
    }
}

/// <summary>What the role policy knows about a role, read from static data in force today.</summary>
/// <param name="Exists">The role has a version in force (iam.cfg_roles_and_permission_sets).</param>
/// <param name="RequiresMfa">The role is in an authentication policy's mfa_required_roles (IAM-003).</param>
/// <param name="Privileged">
/// The role requires MFA, or holds a permission that an MFA-required role holds: assigning it needs a second person
/// (SRS-002, OQ-038). Copying a privileged role's permissions into a new role does not avoid the approval.
/// </param>
public sealed record RoleFacts(string RoleCode, bool Exists, IReadOnlySet<string> Permissions, bool RequiresMfa, bool Privileged = false)
{
    /// <summary>The role definition's default scopes (null = all), where the definition gives them.</summary>
    public string[]? ScopeProducts { get; init; }

    public string[]? ScopeBranches { get; init; }
}

/// <summary>A segregation-of-duties rule (iam.cfg_segregation_of_duties_conflict_rules): Block or Warn.</summary>
public sealed record DutyConflictRule(string PermissionA, string PermissionB, string Conflict);

public sealed record RoleAssigned(Guid UserId, string RoleCode, DateOnly ValidFrom, DateOnly? ValidTo, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RoleRevoked(Guid UserId, string RoleCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A privileged assignment waits for approval: Workflow is asked for a PRIVILEGED_ROLE approval (ADR-009).</summary>
public sealed record PrivilegedRoleRequested(Guid UserId, Guid AssignmentId, string RoleCode, Guid AssignedBy, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record UserDisabled(Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The UserAccount aggregate. Identities and permissions are tenant-scoped (IAM-001); conflicting permissions are
/// never held together (SRS-002); privileged roles require MFA (IAM-003); revocation takes effect at once (IAM-005).
/// </summary>
public sealed class UserAccount : AggregateRoot<Guid>
{
    public static readonly StateMachine<UserStatus, UserTrigger> Lifecycle = new StateMachine<UserStatus, UserTrigger>()
        .Allow(UserStatus.Invited, UserTrigger.FirstSignIn, UserStatus.Active)
        .Allow(UserStatus.Active, UserTrigger.LockUser, UserStatus.Locked)
        .Allow(UserStatus.Locked, UserTrigger.UnlockUser, UserStatus.Active)
        .Allow(UserStatus.Active, UserTrigger.DisableUser, UserStatus.Disabled)
        .Allow(UserStatus.Locked, UserTrigger.DisableUser, UserStatus.Disabled);

    private readonly List<RoleAssignment> _roleAssignments = [];
    private readonly List<Session> _sessions = [];

    private UserAccount()
    {
    }

    public string Username { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    /// <summary>The identity provider that authenticates the user. TODO(spec): mapping from token issuer to idp_code (SRS §30).</summary>
    public string IdpCode { get; private set; } = "";

    /// <summary>The token's sub claim at that identity provider.</summary>
    public string? ExternalSubject { get; private set; }

    public UserStatus Status { get; private set; }

    public bool MfaEnrolled { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>The user's preferred language (ISO 639-1), or null for the tenant's default (V0024; GAP-003).</summary>
    public string? PreferredLanguage { get; private set; }

    public IReadOnlyList<RoleAssignment> RoleAssignments => _roleAssignments;

    public IReadOnlyList<Session> Sessions => _sessions;

    /// <summary>Command ProvisionUser: the user is Invited until first sign-in.</summary>
    public static UserAccount ProvisionUser(Guid id, string username, string displayName, string idpCode, string externalSubject, bool mfaEnrolled) =>
        new()
        {
            Id = Guard.NotEmpty(id),
            Username = Guard.MaxLength(Guard.NotNullOrWhiteSpace(username), 120),
            DisplayName = Guard.MaxLength(Guard.NotNullOrWhiteSpace(displayName), 120),
            IdpCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(idpCode), 15),
            ExternalSubject = Guard.MaxLength(Guard.NotNullOrWhiteSpace(externalSubject), 120),
            MfaEnrolled = mfaEnrolled,
            Status = UserStatus.Invited,
        };

    public IEnumerable<RoleAssignment> EffectiveAssignments(DateOnly day) => _roleAssignments.Where(a => a.IsEffectiveOn(day));

    /// <summary>
    /// A new tenant's first administrator (OQ-067): an active assignment approved by a second platform administrator.
    /// assigned_by must be a user of the same tenant, so it is the user itself, as in the operator bootstrap (OQ-031);
    /// the requesting platform administrator is on the tenant administrator request.
    /// </summary>
    public void GrantFirstAdministratorRole(string roleCode, DateOnly validFrom, Guid approvedBy, DateTimeOffset now)
    {
        var assignment = RoleAssignment.Create(Id, roleCode, validFrom, null, null, null, Id, pending: true);
        assignment.Decide(approved: true, approvedBy, now);
        _roleAssignments.Add(assignment);
        Raise(new RoleAssigned(Id, roleCode, validFrom, null, now));
    }

    /// <summary>
    /// Command AssignRole. <paramref name="currentRoles"/> are the facts for the roles the user already holds today;
    /// the assignment is refused if it would combine permissions that a Block rule forbids together (SRS-002).
    /// </summary>
    public Result AssignRole(RoleFacts role, IReadOnlyCollection<RoleFacts> currentRoles, IReadOnlyCollection<DutyConflictRule> rules, DateOnly validFrom,
        DateOnly? validTo, string[]? scopeProducts, string[]? scopeBranches, Guid assignedBy, DateOnly today, DateTimeOffset now)
    {
        if (Status == UserStatus.Disabled)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Roles cannot be assigned to a disabled user.");
        }

        if (!role.Exists)
        {
            return Error.Validation(IdentityErrorCodes.RoleNotFound, $"Role {role.RoleCode} has no version in force.");
        }

        if (validTo is { } end && end <= validFrom)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "validTo must be after validFrom.");
        }

        if (EffectiveAssignments(today).Any(a => a.RoleCode == role.RoleCode))
        {
            return Error.Conflict(IdentityErrorCodes.RoleAlreadyAssigned, $"The user already holds {role.RoleCode}.");
        }

        if (_roleAssignments.Any(a => a.RoleCode == role.RoleCode && a.Status == AssignmentStatus.PendingApproval))
        {
            return Error.Conflict(IdentityErrorCodes.RoleAlreadyAssigned, $"An assignment of {role.RoleCode} to the user is waiting for approval.");
        }

        if (role.RequiresMfa && !MfaEnrolled)
        {
            return Error.Conflict(IdentityErrorCodes.MfaRequired, $"{role.RoleCode} is a privileged role and requires MFA enrolment (IAM-003).");
        }

        var held = currentRoles.SelectMany(r => r.Permissions).ToHashSet(StringComparer.Ordinal);
        var conflict = rules.FirstOrDefault(rule => rule.Conflict == "BLOCK"
            && ((role.Permissions.Contains(rule.PermissionA) && (held.Contains(rule.PermissionB) || role.Permissions.Contains(rule.PermissionB)))
                || (role.Permissions.Contains(rule.PermissionB) && held.Contains(rule.PermissionA))));
        if (conflict is not null)
        {
            return Error.Conflict(IdentityErrorCodes.SegregationOfDuties,
                $"The user cannot hold both {conflict.PermissionA} and {conflict.PermissionB} (segregation of duties, SRS-002).");
        }

        var assignment = RoleAssignment.Create(Id, role.RoleCode, validFrom, validTo, scopeProducts, scopeBranches, assignedBy, pending: role.Privileged);
        _roleAssignments.Add(assignment);
        Raise(role.Privileged
            ? new PrivilegedRoleRequested(Id, assignment.Id, role.RoleCode, assignedBy, now)
            : new RoleAssigned(Id, role.RoleCode, validFrom, validTo, now));
        return Result.Success();
    }

    /// <summary>
    /// Workflow's decision on a pending privileged assignment: approved, it becomes active (and gives its permissions);
    /// rejected, it never applies. The checker is never the administrator who requested it (SRS-002).
    /// </summary>
    public Result DecideAssignment(Guid assignmentId, bool approved, Guid checkerId, DateTimeOffset now)
    {
        var assignment = _roleAssignments.SingleOrDefault(a => a.Id == assignmentId && a.Status == AssignmentStatus.PendingApproval);
        if (assignment is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No assignment with that ID is waiting for approval.");
        }

        if (checkerId == assignment.AssignedBy)
        {
            return MakerChecker.EnsureDifferentPeople(assignment.AssignedBy.ToString(), checkerId.ToString());
        }

        assignment.Decide(approved, checkerId, now);
        if (approved)
        {
            Raise(new RoleAssigned(Id, assignment.RoleCode, assignment.ValidFrom, assignment.ValidTo, now));
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Command RevokeRole: the role's current and future assignments become Revoked, so the next request is already
    /// without it (IAM-005, GAP-020). <paramref name="revokedBy"/> is the administrator, or the reviewer of an access review.
    /// </summary>
    public Result RevokeRole(string roleCode, Guid revokedBy, DateOnly today, DateTimeOffset now)
    {
        var assignments = _roleAssignments.Where(a => a.RoleCode == roleCode && a.Status == AssignmentStatus.Active && (a.ValidTo is null || a.ValidTo > today)).ToList();
        if (assignments.Count == 0)
        {
            return Error.NotFound(IdentityErrorCodes.RoleNotAssigned, $"The user does not hold {roleCode}.");
        }

        foreach (var assignment in assignments)
        {
            assignment.Revoke(revokedBy, today, now);
        }

        Raise(new RoleRevoked(Id, roleCode, now));
        return Result.Success();
    }

    /// <summary>The first request from the identity provider activates an invited user (transition "First sign-in").</summary>
    public Result RecordSignIn(DateTimeOffset now)
    {
        if (Status == UserStatus.Invited)
        {
            var next = Lifecycle.Fire(Status, UserTrigger.FirstSignIn);
            if (next.IsFailure)
            {
                return next.Error;
            }

            Status = next.Value;
        }

        LastLoginAt = now;
        MarkChanged();
        return Result.Success();
    }

    public Result LockUser() => Transition(UserTrigger.LockUser);

    public Result UnlockUser() => Transition(UserTrigger.UnlockUser);

    /// <summary>The user sets or clears their own preferred language; a two-letter code checked against the register by the caller.</summary>
    public Result ChangePreferredLanguage(string? language)
    {
        if (language is not null && (language.Length != 2 || !language.All(char.IsAsciiLetterLower)))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "language must be a two-letter lower-case ISO 639-1 code.");
        }

        PreferredLanguage = language;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command DisableUser: open sessions are revoked and the user can no longer act (IAM-005).</summary>
    public Result DisableUser(DateTimeOffset now)
    {
        var result = Transition(UserTrigger.DisableUser);
        if (result.IsSuccess)
        {
            foreach (var session in _sessions)
            {
                session.Revoke(now);
            }

            Raise(new UserDisabled(Id, now));
        }

        return result;
    }

    private Result Transition(UserTrigger trigger)
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

public static class IdentityErrorCodes
{
    [ErrorCode(422, "error.identity.role_not_found")]
    public const string RoleNotFound = "ROLE_NOT_FOUND";

    [ErrorCode(409, "error.identity.role_already_assigned")]
    public const string RoleAlreadyAssigned = "ROLE_ALREADY_ASSIGNED";

    [ErrorCode(404, "error.identity.role_not_assigned")]
    public const string RoleNotAssigned = "ROLE_NOT_ASSIGNED";

    [ErrorCode(409, "error.identity.mfa_required")]
    public const string MfaRequired = "MFA_REQUIRED";

    [ErrorCode(409, "error.identity.segregation_of_duties")]
    public const string SegregationOfDuties = "SEGREGATION_OF_DUTIES";

    [ErrorCode(409, "error.identity.username_taken")]
    public const string UsernameTaken = "USERNAME_TAKEN";

    [ErrorCode(422, "error.identity.access_window_too_long")]
    public const string AccessWindowTooLong = "ACCESS_WINDOW_TOO_LONG";

    [ErrorCode(422, "error.identity.nothing_to_review")]
    public const string NothingToReview = "NOTHING_TO_REVIEW";

    [ErrorCode(409, "error.identity.review_incomplete")]
    public const string ReviewIncomplete = "REVIEW_INCOMPLETE";

    [ErrorCode(409, "error.identity.tenant_not_onboarding")]
    public const string TenantNotOnboarding = "TENANT_NOT_ONBOARDING";

    [ErrorCode(409, "error.identity.tenant_has_users")]
    public const string TenantHasUsers = "TENANT_HAS_USERS";

    /// <summary>F103 interim control: product and organisation scopes are not enforced yet, so a scoped assignment is refused.</summary>
    [ErrorCode(422, "error.identity.scopes_not_enforced")]
    public const string ScopesNotEnforced = "SCOPES_NOT_ENFORCED";
}
