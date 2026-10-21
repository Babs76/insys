using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Domain.Users;

namespace Insys.Identity.Application.Users;

public interface IUserRepository
{
    Task<UserAccount?> Get(Guid userId, CancellationToken cancellationToken);

    Task<bool> UsernameExists(string username, CancellationToken cancellationToken);

    void Add(UserAccount user);
}

/// <summary>Reads role definitions and policies from the IAM static data in force on a day.</summary>
public interface IRolePolicyReader
{
    Task<IReadOnlyList<RoleFacts>> Roles(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<DutyConflictRule>> ConflictRules(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>The per-request permission cache (OQ-014). Evicted after a change commits, and again when its event arrives.</summary>
public interface IPermissionCache
{
    void EvictTenant(Guid tenantId);
}

public sealed record RoleAssignmentView(string RoleCode, DateOnly ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches, Guid AssignedBy);

public sealed record UserView(
    Guid Id, string Username, string DisplayName, string IdpCode, string? ExternalSubject, string Status, bool MfaEnrolled, DateTimeOffset? LastLoginAt,
    IReadOnlyList<RoleAssignmentView> RoleAssignments, long RowVersion);

public interface IUserQueries
{
    Task<IReadOnlyList<UserView>> List(Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<UserView?> Get(Guid userId, CancellationToken cancellationToken);
}

public sealed record ProvisionUser(string Username, string DisplayName, string IdpCode, string ExternalSubject, bool MfaEnrolled) : ICommand<Guid>;

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
        RuleFor(c => c.ExternalSubject).NotEmpty().MaximumLength(120);
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

internal sealed class ProvisionUserHandler(IUserRepository users, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<ProvisionUser, Guid>
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

        var user = UserAccount.ProvisionUser(Guid.CreateVersion7(), command.Username, command.DisplayName, command.IdpCode, command.ExternalSubject,
            command.MfaEnrolled);
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
        var result = user.Value.RevokeRole(command.RoleCode, clock.UtcToday, clock.UtcNow);
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
            UserStatusAction.Lock => user.Value.LockUser(),
            UserStatusAction.Unlock => user.Value.UnlockUser(),
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

internal sealed class GetUserHandler(IUserQueries queries) : IQueryHandler<GetUser, UserView>
{
    public async Task<Result<UserView>> Handle(GetUser query, CancellationToken cancellationToken) =>
        await queries.Get(query.UserId, cancellationToken) is { } user ? user : Error.NotFound(ErrorCodes.NotFound, "The user was not found.");
}
