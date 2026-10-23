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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<UserAccount?> Get(Guid userId, CancellationToken cancellationToken) =>
        db.Users.Include(u => u.RoleAssignments).Include(u => u.Sessions).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<bool> UsernameExists(string username, CancellationToken cancellationToken) => db.Users.AnyAsync(u => u.Username == username, cancellationToken);

    public Task<UserAccount?> ByAssignment(Guid assignmentId, CancellationToken cancellationToken) =>
        db.Users.Include(u => u.RoleAssignments).Include(u => u.Sessions).SingleOrDefaultAsync(u => u.RoleAssignments.Any(a => a.Id == assignmentId), cancellationToken);

    public void Add(UserAccount user) => db.Users.Add(user);
}

internal sealed class UserQueries(IdentityDbContext db, IRolePolicyReader roles, IClock clock) : IUserQueries
{
    public async Task<IReadOnlyList<UserView>> List(Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking().Include(u => u.RoleAssignments).AsQueryable();
        if (afterId is not null)
        {
            query = query.Where(u => u.Id.CompareTo(afterId.Value) > 0);
        }

        return await Views(await query.OrderBy(u => u.Id).Take(limit).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<UserView?> Get(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Include(u => u.RoleAssignments).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is { } user
            ? (await Views([user], cancellationToken))[0]
            : null;

    /// <summary>Each assignment says whether its role is privileged, needs MFA and needs approval to assign (GAP-021, OQ-038).</summary>
    private async Task<IReadOnlyList<UserView>> Views(IReadOnlyList<UserAccount> users, CancellationToken cancellationToken)
    {
        var facts = (await roles.Roles(users.SelectMany(u => u.RoleAssignments).Select(a => a.RoleCode), clock.UtcToday, cancellationToken))
            .ToDictionary(r => r.RoleCode, StringComparer.Ordinal);
        return users.Select(u => new UserView(u.Id, u.Username, u.DisplayName, u.IdpCode, u.ExternalSubject, u.Status, u.MfaEnrolled, u.LastLoginAt,
            u.RoleAssignments.OrderBy(a => a.ValidFrom)
                .Select(a => new RoleAssignmentView(a.Id, a.RoleCode, a.Status, a.ValidFrom, a.ValidTo, a.ScopeProducts, a.ScopeBranches, a.AssignedBy,
                    a.ApprovedBy, facts[a.RoleCode].Privileged, facts[a.RoleCode].RequiresMfa, facts[a.RoleCode].Privileged, a.RevokedAt, a.RevokedBy))
                .ToList(),
            u.RowVersion)).ToList();
    }
}

/// <summary>Role facts from iam static data in force (roles, authentication policies, segregation-of-duties rules).</summary>
internal sealed class RolePolicyReader(IdentityDbContext db) : IRolePolicyReader
{
    public async Task<IReadOnlyList<RoleFacts>> Roles(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken)
    {
        var codes = roleCodes.Distinct().ToArray();
        var roles = await db.Set<RolesAndPermissionSets>().AsNoTracking().InForce(Descriptor<RolesAndPermissionSets>().Lifecycle, day)
            .Where(r => codes.Contains(r.RoleCode)).ToListAsync(cancellationToken);
        var policies = await db.Set<AuthenticationAndSessionPolicies>().AsNoTracking().InForce(Descriptor<AuthenticationAndSessionPolicies>().Lifecycle, day)
            .ToListAsync(cancellationToken);
        var mfaRoles = policies.SelectMany(p => p.MfaRequiredRoles).ToHashSet(StringComparer.Ordinal);
        var mfaRoleCodes = mfaRoles.ToArray();
        var mfaPermissions = (await db.Set<RolesAndPermissionSets>().AsNoTracking().InForce(Descriptor<RolesAndPermissionSets>().Lifecycle, day)
                .Where(r => mfaRoleCodes.Contains(r.RoleCode)).Select(r => r.Permissions).ToListAsync(cancellationToken))
            .SelectMany(p => p).ToHashSet(StringComparer.Ordinal);

        // A tenant's own role definition overrides a global one with the same code.
        return codes.Select(code => roles.Where(r => r.RoleCode == code).OrderBy(r => r.TenantId is null).FirstOrDefault() is { } role
                ? new RoleFacts(code, true, role.Permissions.ToHashSet(StringComparer.Ordinal), mfaRoles.Contains(code),
                    mfaRoles.Contains(code) || role.Permissions.Any(mfaPermissions.Contains)) { ScopeProducts = role.ScopeProducts, ScopeBranches = role.ScopeBranches }
                : new RoleFacts(code, false, new HashSet<string>(), mfaRoles.Contains(code), mfaRoles.Contains(code)))
            .ToList();
    }

    public async Task<IReadOnlyList<DutyConflictRule>> ConflictRules(DateOnly day, CancellationToken cancellationToken) =>
        await db.Set<SegregationOfDutiesConflictRules>().AsNoTracking().InForce(Descriptor<SegregationOfDutiesConflictRules>().Lifecycle, day)
            .Select(r => new DutyConflictRule(r.PermissionA, r.PermissionB, r.Conflict)).ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> PrivilegedRoles(DateOnly day, CancellationToken cancellationToken)
    {
        var policies = await db.Set<AuthenticationAndSessionPolicies>().AsNoTracking().InForce(Descriptor<AuthenticationAndSessionPolicies>().Lifecycle, day)
            .ToListAsync(cancellationToken);
        return policies.SelectMany(p => p.MfaRequiredRoles).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RoleFacts>> AllRoles(DateOnly day, CancellationToken cancellationToken)
    {
        var codes = await db.Set<RolesAndPermissionSets>().AsNoTracking().InForce(Descriptor<RolesAndPermissionSets>().Lifecycle, day)
            .Select(r => r.RoleCode).Distinct().ToListAsync(cancellationToken);
        return (await Roles(codes, day, cancellationToken)).OrderBy(r => r.RoleCode, StringComparer.Ordinal).ToList();
    }

    private static StaticDataDescriptor Descriptor<T>() => IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(T));
}

internal sealed class IdentityEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Users.RoleAssigned e => new Contracts.RoleAssigned(e.UserId, e.RoleCode, e.ValidFrom, e.ValidTo),
        Domain.Users.RoleRevoked e => new Contracts.RoleRevoked(e.UserId, e.RoleCode),
        Domain.Users.PrivilegedRoleRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.IdentityApprovals.BusinessRefType, e.AssignmentId,
            Contracts.IdentityApprovals.ApprovalType, e.AssignedBy, null, null, $"Assign {e.RoleCode} to user {e.UserId}"),
        Domain.Users.UserDisabled e => new Contracts.UserDisabled(e.UserId),
        Domain.Access.AccessReviewCompleted e => new Contracts.AccessReviewCompleted(e.ReviewId,
            e.Revoked.Select(r => new Contracts.RevokedAccess(r.UserId, r.RoleCode)).ToList()),
        _ => null,
    };
}

internal sealed class AccessRepository(IdentityDbContext db) : Application.Access.IAccessRepository
{
    public Task<Domain.Access.PrivilegedAccessGrant?> Grant(Guid id, CancellationToken cancellationToken) =>
        db.Grants.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GrantsToExpire(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Grants.Where(g => g.Status == Domain.Access.GrantStatus.Approved && g.ValidTo <= now).OrderBy(g => g.ValidTo).Select(g => g.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(Domain.Access.PrivilegedAccessGrant grant) => db.Grants.Add(grant);

    public Task<Domain.Access.AccessReview?> Review(Guid id, CancellationToken cancellationToken) =>
        db.Reviews.Include(r => r.Items).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<(Guid UserId, string RoleCode)>> EffectiveAssignments(DateOnly today, CancellationToken cancellationToken) =>
        (await db.Set<RoleAssignment>().AsNoTracking()
            .Where(a => a.Status == AssignmentStatus.Active && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today))
            .Select(a => new { a.UserId, a.RoleCode }).Distinct().ToListAsync(cancellationToken))
        .Select(a => (a.UserId, a.RoleCode)).ToList();

    public void Add(Domain.Access.AccessReview review) => db.Reviews.Add(review);
}

internal sealed class AccessQueries(IdentityDbContext db) : Application.Access.IAccessQueries
{
    public async Task<IReadOnlyList<Application.Access.GrantView>> Grants(string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Grants.AsNoTracking();
        if (status is not null && Enum.TryParse<Domain.Access.GrantStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(g => g.Status == parsed);
        }

        if (afterId is not null)
        {
            query = query.Where(g => g.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(g => g.Id).Take(limit).ToListAsync(cancellationToken)).Select(View).ToList();
    }

    public async Task<Application.Access.GrantView?> Grant(Guid id, CancellationToken cancellationToken) =>
        await db.Grants.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken) is { } grant ? View(grant) : null;

    public async Task<IReadOnlyList<Application.Access.ReviewView>> Reviews(Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Reviews.AsNoTracking().Include(r => r.Items).AsQueryable();
        if (afterId is not null)
        {
            query = query.Where(r => r.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken)).Select(View).ToList();
    }

    public async Task<Application.Access.ReviewView?> Review(Guid id, CancellationToken cancellationToken) =>
        await db.Reviews.AsNoTracking().Include(r => r.Items).SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } review ? View(review) : null;

    private static Application.Access.GrantView View(Domain.Access.PrivilegedAccessGrant g) =>
        new(g.Id, g.UserId, g.Purpose, g.RequestedBy, g.ApprovedBy, g.ValidFrom, g.ValidTo, g.Status, g.DecidedAt, g.RejectionReason, g.RowVersion);

    private static Application.Access.ReviewView View(Domain.Access.AccessReview r) =>
        new(r.Id, r.Scope.ToString(), r.ReviewerId, r.DueDate, r.Status, r.ItemsTotal, r.ItemsRevoked,
            r.Items.OrderBy(i => i.Id).Select(i => new Application.Access.ReviewItemView(i.Id, i.UserId, i.RoleCode, i.Decision.ToString(), i.DecidedBy, i.DecidedAt))
                .ToList(),
            r.RowVersion);
}
