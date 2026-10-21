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

    public void Add(UserAccount user) => db.Users.Add(user);
}

internal sealed class UserQueries(IdentityDbContext db) : IUserQueries
{
    public async Task<IReadOnlyList<UserView>> List(Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking().Include(u => u.RoleAssignments).AsQueryable();
        if (afterId is not null)
        {
            query = query.Where(u => u.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(u => u.Id).Take(limit).ToListAsync(cancellationToken)).Select(View).ToList();
    }

    public async Task<UserView?> Get(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Include(u => u.RoleAssignments).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is { } user ? View(user) : null;

    private static UserView View(UserAccount u) =>
        new(u.Id, u.Username, u.DisplayName, u.IdpCode, u.ExternalSubject, u.Status.ToString(), u.MfaEnrolled, u.LastLoginAt,
            u.RoleAssignments.OrderBy(a => a.ValidFrom)
                .Select(a => new RoleAssignmentView(a.RoleCode, a.ValidFrom, a.ValidTo, a.ScopeProducts, a.ScopeBranches, a.AssignedBy)).ToList(),
            u.RowVersion);
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

        // A tenant's own role definition overrides a global one with the same code.
        return codes.Select(code => roles.Where(r => r.RoleCode == code).OrderBy(r => r.TenantId is null).FirstOrDefault() is { } role
                ? new RoleFacts(code, true, role.Permissions.ToHashSet(StringComparer.Ordinal), mfaRoles.Contains(code))
                : new RoleFacts(code, false, new HashSet<string>(), mfaRoles.Contains(code)))
            .ToList();
    }

    public async Task<IReadOnlyList<DutyConflictRule>> ConflictRules(DateOnly day, CancellationToken cancellationToken) =>
        await db.Set<SegregationOfDutiesConflictRules>().AsNoTracking().InForce(Descriptor<SegregationOfDutiesConflictRules>().Lifecycle, day)
            .Select(r => new DutyConflictRule(r.PermissionA, r.PermissionB, r.Conflict)).ToListAsync(cancellationToken);

    private static StaticDataDescriptor Descriptor<T>() => IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(T));
}

internal sealed class IdentityEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Users.RoleAssigned e => new Contracts.RoleAssigned(e.UserId, e.RoleCode, e.ValidFrom, e.ValidTo),
        Domain.Users.RoleRevoked e => new Contracts.RoleRevoked(e.UserId, e.RoleCode),
        Domain.Users.UserDisabled e => new Contracts.UserDisabled(e.UserId),
        _ => null,
    };
}
