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
using Insys.Identity.Contracts;
using Insys.Identity.Domain.Users;
using Insys.Workflow.Contracts;

namespace Insys.Identity.Application.Users;

public interface IUserRepository
{
    Task<UserAccount?> Get(Guid userId, CancellationToken cancellationToken);

    Task<bool> UsernameExists(string username, CancellationToken cancellationToken);

    /// <summary>The user holding the role assignment with this ID.</summary>
    Task<UserAccount?> ByAssignment(Guid assignmentId, CancellationToken cancellationToken);

    /// <summary>The user with the subject link with this ID (external-user-access.md).</summary>
    Task<UserAccount?> ByLink(Guid linkId, CancellationToken cancellationToken);

    void Add(UserAccount user);
}

/// <summary>Reads role definitions and policies from the IAM static data in force on a day.</summary>
public interface IRolePolicyReader
{
    Task<IReadOnlyList<RoleFacts>> Roles(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<DutyConflictRule>> ConflictRules(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Roles that require MFA under the authentication policies in force (IAM-003): the privileged roles.</summary>
    Task<IReadOnlySet<string>> PrivilegedRoles(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Every role in force for the tenant (its own and the global ones it does not override), by role code.</summary>
    Task<IReadOnlyList<RoleFacts>> AllRoles(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>The per-request permission cache (OQ-014). Evicted after a change commits, and again when its event arrives.</summary>
/// <summary>The function catalogue in force (ADR-026): which of these codes are not staff functions in force on the day.</summary>
public interface IFunctionCatalogueQuery
{
    Task<IReadOnlyList<string>> NotForStaff(IReadOnlyCollection<string> functionCodes, DateOnly day, CancellationToken cancellationToken);
}

public interface IPermissionCache
{
    void EvictTenant(Guid tenantId);
}

/// <param name="Status">PendingApproval for a privileged assignment waiting for a second person; only Active gives permissions.</param>
/// <param name="Privileged">The role is privileged (OQ-038): in an authentication policy's MFA roles, or holding a permission such a role holds.</param>
/// <param name="RequiresMfa">An authentication policy in force names the role in its MFA roles (IAM-003).</param>
/// <param name="RequiresApproval">Assigning the role needs a second administrator's approval: true for every privileged role (OQ-038).</param>
/// <param name="RevokedAt">When a Revoked assignment was revoked (GAP-020); it gave no permission from the next request.</param>
public sealed record RoleAssignmentView(Guid Id, string RoleCode, AssignmentStatus Status, DateOnly ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches,
    Guid AssignedBy, Guid? ApprovedBy, bool Privileged, bool RequiresMfa, bool RequiresApproval, DateTimeOffset? RevokedAt, Guid? RevokedBy);

/// <summary>
/// A role the tenant can assign (GAP-021), with what assigning it involves: a privileged role needs MFA under the
/// policies that name it and a second administrator's approval (OQ-038), so the assignment starts as PendingApproval.
/// </summary>
public sealed record RoleView(string RoleCode, bool Privileged, bool RequiresMfa, bool RequiresApproval, string[] Permissions, string[]? ScopeProducts,
    string[]? ScopeBranches);

public sealed record ListRoles : IQuery<IReadOnlyList<RoleView>>;

/// <summary>A role assignment made with scopes before they were refused (F103 interim control), for review.</summary>
public sealed record ScopedAssignmentView(Guid AssignmentId, Guid UserId, string Username, string RoleCode, AssignmentStatus Status, string[]? ScopeProducts,
    string[]? ScopeBranches, DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>
/// Query ListScopedAssignments (F103 interim control): every assignment with product or organisation scopes, which give
/// their role's permissions without the scope until enforcement is built. Review them: revoke, or keep knowing the scope has no effect.
/// </summary>
public sealed record ListScopedAssignments : IQuery<IReadOnlyList<ScopedAssignmentView>>;

public sealed record UserView(
    Guid Id, string Username, string DisplayName, string IdpCode, string? ExternalSubject, UserStatus Status, bool MfaEnrolled, DateTimeOffset? LastLoginAt,
    IReadOnlyList<RoleAssignmentView> RoleAssignments, long RowVersion, string? Email = null);

public interface IUserQueries
{
    Task<IReadOnlyList<ScopedAssignmentView>> ScopedAssignments(CancellationToken cancellationToken);

    Task<IReadOnlyList<UserView>> List(Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<UserView?> Get(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Command ProvisionUser. For INSYS's own realm (idpCode KEYCLOAK) INSYS creates the Keycloak user: email is required, no
/// external subject is given, and the user is PendingIdentity until it is linked (keycloak-user-provisioning). For a
/// federated identity provider the external subject is required, and nothing is created in Keycloak (OQ-037).
/// </summary>
public sealed record ProvisionUser(string Username, string DisplayName, string IdpCode, string? ExternalSubject, bool MfaEnrolled, string? Email = null) : ICommand<Guid>;

/// <summary>Command ResendInvitation: Keycloak's required-actions email again, for a user of INSYS's own realm who has not signed in.</summary>
public sealed record ResendInvitation(Guid UserId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AssignRole(Guid UserId, string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches,
    long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record RevokeRole(Guid UserId, string RoleCode, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public enum UserStatusAction
{
    Lock,
    Unlock,
    Disable,
}

/// <summary>Commands LockUser, UnlockUser and DisableUser.</summary>
public sealed record ChangeUserStatus(Guid UserId, UserStatusAction Action, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record ListUsers(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<UserView>>;

public sealed record GetUser(Guid UserId) : IQuery<UserView>;

public sealed class ProvisionUserValidator : AbstractValidator<ProvisionUser>
{
    public ProvisionUserValidator()
    {
        RuleFor(c => c.Username).NotEmpty().MaximumLength(120);
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(c => c.IdpCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ExternalSubject).NotEmpty().MaximumLength(120).When(c => c.IdpCode != UserAccount.OwnRealm);
        RuleFor(c => c.ExternalSubject).Empty().When(c => c.IdpCode == UserAccount.OwnRealm)
            .WithMessage("INSYS creates the Keycloak user of its own realm; give its email, not an external subject.");
        RuleFor(c => c.Email).NotEmpty().MaximumLength(254).EmailAddress().When(c => c.IdpCode == UserAccount.OwnRealm);
    }
}

public sealed class AssignRoleValidator : AbstractValidator<AssignRole>
{
    public AssignRoleValidator()
    {
        RuleFor(c => c.RoleCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.ValidTo).GreaterThan(c => c.ValidFrom).When(c => c.ValidFrom is not null && c.ValidTo is not null);
    }
}

public sealed class RevokeRoleValidator : AbstractValidator<RevokeRole>
{
    public RevokeRoleValidator() => RuleFor(c => c.RoleCode).NotEmpty().MaximumLength(30);
}

internal static class Administrator
{
    /// <summary>User administration is done by identified users; their ID is assigned_by (IAM-006).</summary>
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "User administration is done by identified users.");

    public static async Task<Result<UserAccount>> Load(IUserRepository users, Guid userId, long expectedVersion, CancellationToken cancellationToken)
    {
        var user = await users.Get(userId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The user was not found.");
        }

        return user.RowVersion == expectedVersion
            ? user
            : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The user has changed since it was read.");
    }
}

internal sealed class ProvisionUserHandler(IUserRepository users, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<ProvisionUser, Guid>
{
    public async Task<Result<Guid>> Handle(ProvisionUser command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        if (await users.UsernameExists(command.Username, cancellationToken))
        {
            return Error.Conflict(IdentityErrorCodes.UsernameTaken, "Another user in this tenant has this username.");
        }

        var user = command.IdpCode == UserAccount.OwnRealm
            ? UserAccount.ProvisionUserInOwnRealm(Guid.CreateVersion7(), command.Username, command.DisplayName, command.Email!, command.MfaEnrolled, clock.UtcNow)
            : UserAccount.ProvisionUser(Guid.CreateVersion7(), command.Username, command.DisplayName, command.IdpCode, command.ExternalSubject!, command.MfaEnrolled);
        users.Add(user);
        audit.Record(new AuditEntry("IDENTITY", "ProvisionUser", nameof(UserAccount), user.Id.ToString(),
            new Dictionary<string, string?> { ["idp"] = command.IdpCode }));
        return user.Id;
    }
}

internal sealed class AssignRoleHandler(IUserRepository users, IRolePolicyReader policy, IExecutionContext execution, ITenantContext tenant, IClock clock,
    IAuditTrail audit, IAfterCommit afterCommit, IPermissionCache cache) : ICommandHandler<AssignRole, long>
{
    public async Task<Result<long>> Handle(AssignRole command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var today = clock.UtcToday;
        var held = user.Value.EffectiveAssignments(today).Select(a => a.RoleCode).ToList();
        var roles = await policy.Roles([command.RoleCode, .. held], today, cancellationToken);
        var role = roles.FirstOrDefault(r => r.RoleCode == command.RoleCode) ?? new RoleFacts(command.RoleCode, false, new HashSet<string>(), false);

        var rules = await policy.ConflictRules(today, cancellationToken);
        audit.Record(new AuditEntry("IDENTITY", "AssignRole", nameof(UserAccount), command.UserId.ToString(),
            new Dictionary<string, string?> { ["role"] = command.RoleCode }));

        var result = user.Value.AssignRole(role, roles.Where(r => held.Contains(r.RoleCode)).ToList(), rules, command.ValidFrom ?? today, command.ValidTo,
            command.ScopeProducts, command.ScopeBranches, administrator.Value, today, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var tenantId = tenant.RequireTenantId();
        afterCommit.Enqueue(() => cache.EvictTenant(tenantId));
        return user.Value.RowVersion + 1;
    }
}

internal sealed class RevokeRoleHandler(IUserRepository users, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    IAfterCommit afterCommit, IPermissionCache cache) : ICommandHandler<RevokeRole, long>
{
    public async Task<Result<long>> Handle(RevokeRole command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        audit.Record(new AuditEntry("IDENTITY", "RevokeRole", nameof(UserAccount), command.UserId.ToString(),
            new Dictionary<string, string?> { ["role"] = command.RoleCode }));
        var result = user.Value.RevokeRole(command.RoleCode, administrator.Value, clock.UtcToday, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // IAM-005: evict as soon as the revocation commits, so the very next request is resolved without the role.
        var tenantId = tenant.RequireTenantId();
        afterCommit.Enqueue(() => cache.EvictTenant(tenantId));
        return user.Value.RowVersion + 1;
    }
}

internal sealed class ChangeUserStatusHandler(IUserRepository users, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    IAfterCommit afterCommit, IPermissionCache cache) : ICommandHandler<ChangeUserStatus, long>
{
    public async Task<Result<long>> Handle(ChangeUserStatus command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        audit.Record(new AuditEntry("IDENTITY", $"{command.Action}User", nameof(UserAccount), command.UserId.ToString()));
        var result = command.Action switch
        {
            UserStatusAction.Lock => user.Value.LockUser(clock.UtcNow),
            UserStatusAction.Unlock => user.Value.UnlockUser(clock.UtcNow),
            _ => user.Value.DisableUser(clock.UtcNow),
        };
        if (result.IsFailure)
        {
            return result.Error;
        }

        var tenantId = tenant.RequireTenantId();
        afterCommit.Enqueue(() => cache.EvictTenant(tenantId));
        return user.Value.RowVersion + 1;
    }
}

internal sealed class ListUsersHandler(IUserQueries queries) : IQueryHandler<ListUsers, IReadOnlyList<UserView>>
{
    public async Task<Result<IReadOnlyList<UserView>>> Handle(ListUsers query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.AfterId, query.Limit, cancellationToken));
}

internal sealed class ListScopedAssignmentsHandler(IUserQueries queries) : IQueryHandler<ListScopedAssignments, IReadOnlyList<ScopedAssignmentView>>
{
    public async Task<Result<IReadOnlyList<ScopedAssignmentView>>> Handle(ListScopedAssignments query, CancellationToken cancellationToken) =>
        Result.Success(await queries.ScopedAssignments(cancellationToken));
}

internal sealed class ListRolesHandler(IRolePolicyReader roles, IClock clock) : IQueryHandler<ListRoles, IReadOnlyList<RoleView>>
{
    public async Task<Result<IReadOnlyList<RoleView>>> Handle(ListRoles query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<RoleView>>((await roles.AllRoles(clock.UtcToday, cancellationToken))
            .Select(r => new RoleView(r.RoleCode, r.Privileged, r.RequiresMfa, r.Privileged, [.. r.Permissions.Order(StringComparer.Ordinal)], r.ScopeProducts,
                r.ScopeBranches))
            .ToList());
}

internal sealed class GetUserHandler(IUserQueries queries) : IQueryHandler<GetUser, UserView>
{
    public async Task<Result<UserView>> Handle(GetUser query, CancellationToken cancellationToken) =>
        await queries.Get(query.UserId, cancellationToken) is { } user ? user : Error.NotFound(ErrorCodes.NotFound, "The user was not found.");
}

/// <summary>
/// Identity's policy for Workflow's decision on a privileged role assignment (OQ-038): granted, the assignment becomes
/// active and RoleAssigned evicts the permission cache; rejected, it never applies.
/// </summary>
public sealed class PrivilegedRoleApprovalPolicy(IUserRepository users, IClock clock) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.CheckerId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid assignmentId, bool approved, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType == IdentityApprovals.BusinessRefType && await users.ByAssignment(assignmentId, cancellationToken) is { } user)
        {
            user.DecideAssignment(assignmentId, approved, checkerId, clock.UtcNow);
        }

        // An external user's link (external-user-access.md §1): approved, it takes effect; rejected, it never does.
        if (businessRefType == IdentityApprovals.SubjectLinkBusinessRefType && await users.ByLink(assignmentId, cancellationToken) is { } linked)
        {
            linked.DecideSubjectLink(assignmentId, approved, checkerId, clock.UtcNow);
        }
    }
}

internal sealed class ResendInvitationHandler(IUserRepository users, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ResendInvitation, long>
{
    public async Task<Result<long>> Handle(ResendInvitation command, CancellationToken cancellationToken)
    {
        var administrator = Administrator.Require(execution);
        if (administrator.IsFailure)
        {
            return administrator.Error;
        }

        var user = await Administrator.Load(users, command.UserId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var result = user.Value.ResendInvitation(clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("IDENTITY", "ResendInvitation", nameof(UserAccount), command.UserId.ToString()));
        return user.Value.RowVersion + 1;
    }
}
