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
        db.Users.Include(u => u.RoleAssignments).Include(u => u.Sessions).Include(u => u.SubjectLinks).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<bool> UsernameExists(string username, CancellationToken cancellationToken) => db.Users.AnyAsync(u => u.Username == username, cancellationToken);

    public Task<UserAccount?> ByAssignment(Guid assignmentId, CancellationToken cancellationToken) =>
        db.Users.Include(u => u.RoleAssignments).Include(u => u.Sessions).Include(u => u.SubjectLinks).SingleOrDefaultAsync(u => u.RoleAssignments.Any(a => a.Id == assignmentId), cancellationToken);

    public Task<UserAccount?> ByLink(Guid linkId, CancellationToken cancellationToken) =>
        db.Users.Include(u => u.RoleAssignments).Include(u => u.Sessions).Include(u => u.SubjectLinks)
            .SingleOrDefaultAsync(u => u.SubjectLinks.Any(l => l.Id == linkId), cancellationToken);

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

    public async Task<IReadOnlyList<ScopedAssignmentView>> ScopedAssignments(CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().Include(u => u.RoleAssignments).Where(u => u.RoleAssignments.Any(a => a.ScopeProducts != null || a.ScopeBranches != null))
            .ToListAsync(cancellationToken))
        .SelectMany(u => u.RoleAssignments.Where(a => a.ScopeProducts is { Length: > 0 } || a.ScopeBranches is { Length: > 0 })
            .Select(a => new ScopedAssignmentView(a.Id, u.Id, u.Username, a.RoleCode, a.Status, a.ScopeProducts, a.ScopeBranches, a.ValidFrom, a.ValidTo)))
        .OrderBy(a => a.Username, StringComparer.Ordinal).ThenBy(a => a.RoleCode, StringComparer.Ordinal).ToList();

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
            u.RowVersion, u.Email)).ToList();
    }
}

/// <summary>
/// Role facts from iam static data in force (roles, their function grants, authentication policies, segregation-of-duties
/// rules). A role's permissions are those its functions require (ADR-026); it is privileged when a policy names it or it
/// holds a privileged function (§4).
/// </summary>
internal sealed class RolePolicyReader(IdentityDbContext db, FunctionCatalogueReader catalogue) : IRolePolicyReader
{
    public async Task<IReadOnlyList<RoleFacts>> Roles(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken)
    {
        var codes = roleCodes.Distinct().ToArray();
        var policies = await db.Set<AuthenticationAndSessionPolicies>().AsNoTracking().InForce(Descriptor<AuthenticationAndSessionPolicies>().Lifecycle, day)
            .ToListAsync(cancellationToken);
        var mfaRoles = policies.SelectMany(p => p.MfaRequiredRoles).ToHashSet(StringComparer.Ordinal);
        var wanted = codes.Concat(mfaRoles).Distinct().ToArray();
        var roles = await db.Set<RolesAndPermissionSets>().AsNoTracking().InForce(Descriptor<RolesAndPermissionSets>().Lifecycle, day)
            .Where(r => codes.Contains(r.RoleCode)).ToListAsync(cancellationToken);
        var grants = (await db.Set<RoleFunctionAssignments>().AsNoTracking().InForce(Descriptor<RoleFunctionAssignments>().Lifecycle, day)
                .Where(g => wanted.Contains(g.RoleCode)).Select(g => new { g.TenantId, g.RoleCode, g.FunctionCode }).ToListAsync(cancellationToken));
        var functions = await catalogue.Functions(day, cancellationToken);
        // OQ-038: a role holding a function of a role that requires MFA is privileged too, without itself requiring MFA.
        var mfaFunctions = grants.Where(g => mfaRoles.Contains(g.RoleCode)).Select(g => g.FunctionCode).ToHashSet(StringComparer.Ordinal);

        // A tenant's own role definition overrides a global one with the same code; its grants are those of the same scope.
        return codes.Select(code =>
            {
                if (roles.Where(r => r.RoleCode == code).OrderBy(r => r.TenantId is null).FirstOrDefault() is not { } role)
                {
                    return new RoleFacts(code, false, new HashSet<string>(), mfaRoles.Contains(code), mfaRoles.Contains(code));
                }

                var held = grants.Where(g => g.RoleCode == code && g.TenantId == role.TenantId && functions.ContainsKey(g.FunctionCode))
                    .Select(g => functions[g.FunctionCode]).ToList();
                var requiresMfa = mfaRoles.Contains(code) || held.Any(f => f.Privileged);
                var privileged = requiresMfa || held.Any(f => mfaFunctions.Contains(f.Code));
                return new RoleFacts(code, true, held.SelectMany(f => f.Permissions).ToHashSet(StringComparer.Ordinal), requiresMfa, privileged)
                {
                    ScopeProducts = role.ScopeProducts,
                    ScopeBranches = role.ScopeBranches,
                    Functions = held.Select(f => f.Code).ToHashSet(StringComparer.Ordinal),
                };
            })
            .ToList();
    }

    /// <summary>The rules on function pairs (ADR-026 §4); a rule still on permissions has been converted and retired.</summary>
    public async Task<IReadOnlyList<DutyConflictRule>> ConflictRules(DateOnly day, CancellationToken cancellationToken) =>
        await db.Set<SegregationOfDutiesConflictRules>().AsNoTracking().InForce(Descriptor<SegregationOfDutiesConflictRules>().Lifecycle, day)
            .Where(r => r.FunctionA != null && r.FunctionB != null)
            .Select(r => new DutyConflictRule(r.FunctionA!, r.FunctionB!, r.Conflict)).ToListAsync(cancellationToken);

    /// <summary>The roles a policy names, and those holding a privileged function (ADR-026 §4).</summary>
    public async Task<IReadOnlySet<string>> PrivilegedRoles(DateOnly day, CancellationToken cancellationToken) =>
        (await AllRoles(day, cancellationToken)).Where(r => r.Privileged).Select(r => r.RoleCode)
            .Concat((await db.Set<AuthenticationAndSessionPolicies>().AsNoTracking().InForce(Descriptor<AuthenticationAndSessionPolicies>().Lifecycle, day)
                .ToListAsync(cancellationToken)).SelectMany(p => p.MfaRequiredRoles))
            .ToHashSet(StringComparer.Ordinal);

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
        Domain.Tenants.TenantAdministratorRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(
            Contracts.IdentityApprovals.TenantAdministratorBusinessRefType, e.RequestId, Contracts.IdentityApprovals.TenantAdministratorApprovalType,
            e.RequestedBy, null, null, $"First administrator {e.Username} for tenant {e.TargetTenantId}"),
        Domain.Tenants.TenantAdministratorProvisioned e => new Contracts.TenantAdministratorProvisioned(e.TargetTenantId, e.UserId, e.RoleCode),
        Domain.Users.RoleAssigned e => new Contracts.RoleAssigned(e.UserId, e.RoleCode, e.ValidFrom, e.ValidTo),
        Domain.Users.RoleRevoked e => new Contracts.RoleRevoked(e.UserId, e.RoleCode),
        Domain.Users.PrivilegedRoleRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.IdentityApprovals.BusinessRefType, e.AssignmentId,
            Contracts.IdentityApprovals.ApprovalType, e.AssignedBy, null, null, $"Assign {e.RoleCode} to user {e.UserId}"),
        Domain.Users.UserDisabled e => new Contracts.UserDisabled(e.UserId),
        Domain.Users.UserReenabled e => new Contracts.UserReenabled(e.UserId),
        Domain.Users.UserIdentityRequested e => new Contracts.UserIdentityRequested(e.UserId),
        Domain.Users.UserInvitationRequested e => new Contracts.UserInvitationRequested(e.UserId, e.Actions),
        Domain.Users.SubjectLinkRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.IdentityApprovals.SubjectLinkBusinessRefType, e.LinkId,
            Contracts.IdentityApprovals.SubjectLinkApprovalType, e.RequestedBy, null, null, $"Link user {e.UserId} to {e.Kind} {e.Key} (external user)"),
        Domain.Users.SubjectLinkChanged e => new Contracts.SubjectLinkChanged(e.UserId),
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
