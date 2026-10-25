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
internal sealed class TenantAdministratorStore(IdentityDbContext db, FunctionCatalogueReader catalogue, IClock clock) : ITenantAdministrators
{
    public Task<TenantAdministratorRequest?> Get(Guid requestId, CancellationToken cancellationToken) =>
        db.TenantAdministratorRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<TenantAdministratorRequest>> List(Guid? targetTenantId, CancellationToken cancellationToken) =>
        await db.TenantAdministratorRequests.AsNoTracking().Where(r => targetTenantId == null || r.TargetTenantId == targetTenantId)
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

    public async Task<Guid> Provision(TenantAdministratorRequest request, Guid checkerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = clock.UtcToday;
        // INSYS's own realm: the user waits for its Keycloak user, which Identity's consumer creates (keycloak-user-provisioning §1).
        var user = request.IdpCode == UserAccount.OwnRealm
            ? UserAccount.ProvisionUserInOwnRealm(Guid.CreateVersion7(), request.Username, request.DisplayName, request.Email!, request.MfaEnrolled, now)
            : UserAccount.ProvisionUser(Guid.CreateVersion7(), request.Username, request.DisplayName, request.IdpCode, request.ExternalSubject!, request.MfaEnrolled);
        await db.WithinTenant(request.TargetTenantId, async () =>
        {
            if (await db.Users.AnyAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Tenant {request.TargetTenantId} already has users; its first administrator was not provisioned.");
            }

            // The TENANT_ADMIN role definition, with the permissions the two platform administrators agreed. It is in
            // force at once: the second administrator's approval is its approval (SDM-003).
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
                ChangeReason = $"First administrator of the tenant (OQ-067), request {request.Id}",
                RoleCode = request.Role,
            });

            // ADR-026: the role holds the functions that need only the agreed permissions, in force with it.
            var grants = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RoleFunctionAssignments)).Lifecycle;
            foreach (var function in FunctionsWithin(await catalogue.Functions(today, cancellationToken), request.Permissions))
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
                    ChangeReason = $"First administrator of the tenant (OQ-067), request {request.Id}",
                    RoleCode = request.Role,
                    FunctionCode = function,
                });
            }
            user.GrantFirstAdministratorRole(request.Role, today, checkerId, now);
            db.Users.Add(user);
        }, cancellationToken);
        return user.Id;
    }

    /// <summary>The functions whose required permissions are all among these (iam.functions_within).</summary>
    internal static IEnumerable<string> FunctionsWithin(IReadOnlyDictionary<string, CatalogueEntry> functions, IReadOnlyCollection<string> permissions) =>
        functions.Values.Where(f => f.Permissions.Count > 0 && f.Permissions.All(permissions.Contains)).Select(f => f.Code).Order(StringComparer.Ordinal);
}
