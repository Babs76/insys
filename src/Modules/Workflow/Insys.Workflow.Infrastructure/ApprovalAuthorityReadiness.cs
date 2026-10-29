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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Contracts;
using Insys.StaticData.Contracts;
using Insys.Tenancy.Contracts;
using Insys.Workflow.Contracts;
using Insys.Workflow.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Workflow.Infrastructure;

/// <summary>
/// APPROVAL_AUTHORITY (onboarding readiness §2, Ade 29 October 2026): every approval type the tenant can raise has a role
/// in force that can decide it, so no approval sits undecidable. A type can be raised when the module that raises it says
/// so (<see cref="IApprovalTypeConditions"/>: the due diligence types with the gates on), else when the tenant is entitled
/// to its feature (<see cref="ApprovalKinds.FeatureOf"/>; operator types only in the operator tenant), else always.
/// A role can decide it when it holds authority for it, as approving reads it (a module's own limits, else the delegation
/// table, else for an unconfigured static data type the static_data.approve permission), and holds the permission the
/// route needs (approval.approve, or static_data.approve for static data) and any permission the type requires.
/// </summary>
internal sealed class ApprovalAuthorityReadiness([FromKeyedServices(WorkflowDbContext.SchemaName)] NpgsqlDataSource dataSource,
    IEnumerable<IApprovalTypeConditions> conditions, IEnumerable<IApprovalAuthoritySource> sources, ITenantFunctionSettings tenants, IStaffRoles staffRoles,
    IOptions<PlatformOptions> platform) : IReadinessCheck
{
    public const string Step = "SHARED_OPERATIONAL_SETUP";

    public IReadOnlyCollection<string> Steps { get; } = [Step];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var (tenantId, day) = (context.TenantId, context.Day);
        var stated = new Dictionary<string, ApprovalTypeCondition>(StringComparer.Ordinal);
        foreach (var source in conditions)
        {
            foreach (var condition in await source.For(tenantId, day, cancellationToken))
            {
                stated[condition.ApprovalType] = condition;
            }
        }

        var features = (await tenants.For(tenantId, day, cancellationToken)).EntitledFeatures;
        var permissions = await staffRoles.Permissions(tenantId, day, cancellationToken);
        var delegated = await ReadinessSql.InTenant(dataSource, tenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            $"SELECT DISTINCT transaction_type || ' ' || role_code FROM workflow.cfg_delegation_and_approval_authority WHERE tenant_id = @t AND {ReadinessSql.InForce}",
            cancellationToken, ("t", tenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", day)), cancellationToken);
        var delegation = delegated.Select(r => r.Split(' ', 2)).ToLookup(r => r[0], r => r[1], StringComparer.Ordinal);
        var listed = new List<ListedAuthority>();
        foreach (var source in sources)
        {
            listed.AddRange(await source.Listed(tenantId, [.. permissions.Keys], day, cancellationToken));
        }

        var undecidable = new List<string>();
        foreach (var type in ApprovalKinds.ApprovalTypes)
        {
            var condition = stated.GetValueOrDefault(type);
            if (!(condition?.Raisable ?? Raisable(type, tenantId, features)))
            {
                continue;
            }

            var staticData = type.StartsWith(StaticDataApprovals.ApprovalTypePrefix, StringComparison.Ordinal);
            IEnumerable<string> authorised = sources.Any(s => s.Decides(type))
                ? listed.Where(l => l.ApprovalType == type).Select(l => l.RoleCode)
                : delegation[type];
            if (staticData && !delegation.Contains(type))
            {
                authorised = permissions.Where(p => p.Value.Contains(StaticDataApprovalPermissions.Approve)).Select(p => p.Key);
            }

            var route = staticData ? StaticDataApprovalPermissions.Approve : WorkflowPermissions.ApprovalApprove;
            if (!authorised.Any(role => permissions.TryGetValue(role, out var held) && held.Contains(route)
                    && (condition?.RequiredPermission is not { } required || held.Contains(required))))
            {
                undecidable.Add(type);
            }
        }

        return [ReadinessResult.Of(Step, "APPROVAL_AUTHORITY", "Every approval type the tenant can raise has a role with authority to decide it", undecidable)];
    }

    private bool Raisable(string type, Guid tenantId, IReadOnlySet<string> features) => ApprovalKinds.FeatureOf(type) switch
    {
        null => true,
        ApprovalKinds.PlatformOperator => tenantId == platform.Value.OperatorTenantId,
        var feature => features.Contains(feature),
    };
}
