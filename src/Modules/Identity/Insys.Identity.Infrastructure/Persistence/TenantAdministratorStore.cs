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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Application.Tenants;
using Insys.Identity.Domain.Tenants;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Identity.Infrastructure.Persistence;

/// <summary>
/// First-administrator requests in the operator tenant, and their effect in the new tenant (OQ-067). The new tenant's
/// rows are written through <see cref="BuildingBlocks.Infrastructure.Persistence.ModuleDbContext.WithinTenant"/>: in
/// the caller's transaction, with row-level security set to the new tenant for that block only.
/// </summary>
internal sealed class TenantAdministratorStore(IdentityDbContext db, IClock clock) : ITenantAdministrators
{
    public Task<TenantAdministratorRequest?> Get(Guid requestId, CancellationToken cancellationToken) =>
        db.TenantAdministratorRequests.Include(r => r.People).SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<TenantAdministratorRequest>> List(Guid? targetTenantId, CancellationToken cancellationToken) =>
        await db.TenantAdministratorRequests.AsNoTracking().Include(r => r.People).Where(r => targetTenantId == null || r.TargetTenantId == targetTenantId)
            .OrderBy(r => r.Id).ToListAsync(cancellationToken);

    public Task<bool> HasPendingRequest(Guid targetTenantId, CancellationToken cancellationToken) =>
        db.TenantAdministratorRequests.AnyAsync(r => r.TargetTenantId == targetTenantId && r.Status == TenantAdministratorRequestStatus.PendingApproval,
            cancellationToken);

    public void Add(TenantAdministratorRequest request) => db.TenantAdministratorRequests.Add(request);

    public async Task<bool> TenantHasUsers(Guid targetTenantId, CancellationToken cancellationToken)
    {
        var any = false;
        await db.WithinTenant(targetTenantId, async () => any = await db.Users.AnyAsync(cancellationToken), cancellationToken);
        return any;
    }

    public async Task<bool> TenantHasAdministratorRole(Guid targetTenantId, CancellationToken cancellationToken)
    {
        var inForce = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RolesAndPermissionSets)).Lifecycle.InForce;
        var today = clock.UtcToday;
        var any = false;
        await db.WithinTenant(targetTenantId, async () => any = await db.Set<RolesAndPermissionSets>().AnyAsync(r => r.RoleCode == TenantAdministratorRequest.RoleCode
            && inForce.Contains(r.RecordStatus) && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo > today), cancellationToken), cancellationToken);
        return any;
    }

    public async Task<IReadOnlyList<string>> UsernamesTaken(Guid targetTenantId, IEnumerable<string> usernames, CancellationToken cancellationToken)
    {
        var names = usernames.ToList();
        List<string> taken = [];
        await db.WithinTenant(targetTenantId, async () => taken = await db.Users.Where(u => names.Contains(u.Username)).Select(u => u.Username).ToListAsync(cancellationToken),
            cancellationToken);
        return taken;
    }

    public async Task<IReadOnlyList<AdministratorUser>> Users(Guid targetTenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        List<AdministratorUser> users = [];
        await db.WithinTenant(targetTenantId, async () => users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new AdministratorUser(u.Id, u.ExternalSubject != null)).ToListAsync(cancellationToken), cancellationToken);
        return users;
    }

    public async Task<IReadOnlyList<Guid>> Provision(TenantAdministratorRequest request, Guid checkerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = clock.UtcToday;
        // INSYS's own realm: each user waits for its Keycloak user, which Identity's consumer creates (keycloak-user-provisioning §1).
        var users = request.People.OrderBy(p => p.Position).Select(p => p.IdpCode == UserAccount.OwnRealm
            ? UserAccount.ProvisionUserInOwnRealm(Guid.CreateVersion7(), p.Username, p.DisplayName, p.Email!, p.MfaEnrolled, now)
            : UserAccount.ProvisionUser(Guid.CreateVersion7(), p.Username, p.DisplayName, p.IdpCode, p.ExternalSubject!, p.MfaEnrolled)).ToList();
        await db.WithinTenant(request.TargetTenantId, async () =>
        {
            if (request.Kind == TenantAdministratorRequestKind.FirstAdministrators)
            {
                if (await db.Users.AnyAsync(cancellationToken))
                {
                    throw new InvalidOperationException($"Tenant {request.TargetTenantId} already has users; its first administrators were not provisioned.");
                }

                AddRole(request, checkerId, today, now);
            }

            // Each administrator holds the TENANT_ADMIN role: the two platform administrators' approval is its approval.
            foreach (var user in users)
            {
                user.GrantFirstAdministratorRole(request.Role, today, checkerId, now);
                db.Users.Add(user);
            }
        }, cancellationToken);
        return [.. users.Select(u => u.Id)];
    }

    /// <summary>
    /// A new tenant's TENANT_ADMIN role definition and its function grants, with the functions the two platform
    /// administrators agreed (OQ-099), in force at once: the second administrator's approval is its approval (SDM-003).
    /// </summary>
    private void AddRole(TenantAdministratorRequest request, Guid checkerId, DateOnly today, DateTimeOffset now)
    {
        var lifecycle = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RolesAndPermissionSets)).Lifecycle;
        db.Set<RolesAndPermissionSets>().Add(new RolesAndPermissionSets
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TargetTenantId,
            Code = request.Role,
            Version = 1,
            EffectiveFrom = today,
            RecordStatus = lifecycle.Active,
            ApprovedAt = now,
            ApprovedBy = checkerId.ToString(),
            ChangeReason = $"First administrators of the tenant (OQ-067, BR-013), request {request.Id}",
            RoleCode = request.Role,
        });

        var grants = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RoleFunctionAssignments)).Lifecycle;
        foreach (var function in request.Functions)
        {
            db.Set<RoleFunctionAssignments>().Add(new RoleFunctionAssignments
            {
                Id = Guid.CreateVersion7(),
                TenantId = request.TargetTenantId,
                Code = $"{request.Role}:{function}",
                Version = 1,
                EffectiveFrom = today,
                RecordStatus = grants.Active,
                ApprovedAt = now,
                ApprovedBy = checkerId.ToString(),
                ChangeReason = $"First administrators of the tenant (OQ-067, BR-013), request {request.Id}",
                RoleCode = request.Role,
                FunctionCode = function,
            });
        }
    }
}
