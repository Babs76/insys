/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.Identity.Contracts;
using Insys.StaticData.Contracts;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>
/// BR-013: a new tenant's administrators approve its first changes themselves. When the first administrators are
/// provisioned, the TENANT_ADMIN role's approval authority (role-templates.md) is put in force in the new tenant: a
/// privileged role assignment, an external-subject link, and Tier 1 and Tier 2 static data changes. Without it no one in
/// the tenant could decide an approval (WFL-004), and changing the authority table is itself a Tier 1 change. The two
/// platform administrators' approval is its approval (SDM-003), as for the role. Only a tenant with no authority row for
/// the role gets them, so an additional administrator, or a tenant that has changed its own, is left as it is.
/// The event is published in the administrator's own tenant, so the rows are written there as any consumer's are.
/// </summary>
public sealed class TenantAdministratorAuthority(WorkflowDbContext db, ITenantContext tenant, IClock clock) : IIntegrationEventHandler<AdministratorGranted>
{
    public static readonly IReadOnlyList<string> ApprovalTypes =
        [IdentityApprovals.ApprovalType, IdentityApprovals.SubjectLinkApprovalType, StaticDataApprovals.ApprovalType(1), StaticDataApprovals.ApprovalType(2)];

    public async Task Handle(AdministratorGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var authority = db.Set<DelegationAndApprovalAuthority>();
        if (await authority.AnyAsync(a => a.RoleCode == granted.RoleCode, cancellationToken))
        {
            return;
        }

        var lifecycle = WorkflowStaticData.Descriptors.Single(d => d.EntityType == typeof(DelegationAndApprovalAuthority)).Lifecycle;
        foreach (var type in ApprovalTypes)
        {
            authority.Add(new DelegationAndApprovalAuthority
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Code = $"{granted.RoleCode}:{type}",
                Version = 1,
                EffectiveFrom = clock.UtcToday,
                RecordStatus = lifecycle.Active,
                ApprovedAt = clock.UtcNow,
                ApprovedBy = "first-administrators",
                ChangeReason = "The first administrators' approval authority (BR-013)",
                RoleCode = granted.RoleCode,
                TransactionType = type,
                LimitValue = 0,
                DelegationAllowed = false,
            });
        }
    }
}
