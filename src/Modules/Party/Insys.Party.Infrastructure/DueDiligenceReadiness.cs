/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Party.Infrastructure;

/// <summary>
/// Onboarding readiness for due diligence (addenda/due-diligence.md §7, OQ-123), in SHARED_OPERATIONAL_SETUP:
/// <list type="bullet">
/// <item>DUE_DILIGENCE_LIFE_GATE: a tenant entitled to LIFE has its gates on (they are, with no settings row).</item>
/// <item>DUE_DILIGENCE_PROVIDER: with the gates on, an active SCREENING partner (every domain screens sanctions) and, for
/// LIFE, an IDENTITY_VERIFICATION partner; without it identity is verified by people only.</item>
/// <item>DUE_DILIGENCE_REVIEWER: with the gates on, staff roles in force grant PARTY_REVIEW_SCREENING_HIT, so hits are reviewed, and
/// PARTY_COMPLETE_DUE_DILIGENCE_REVIEW, so periodic reviews end (OQ-138).</item>
/// </list>
/// </summary>
internal sealed class DueDiligenceReadiness([FromKeyedServices(PartyDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantFunctionSettings tenants,
    IPartnerDirectory partners, IStaffRoles roles) : IReadinessCheck, IApprovalTypeConditions
{
    /// <summary>
    /// The due diligence approvals (OQ-126) are raised only with the gates on, and their checkers need each type's permission
    /// besides authority: enhanced due diligence, a risk class override, an unblock.
    /// </summary>
    public async Task<IReadOnlyList<ApprovalTypeCondition>> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var gatesOn = await GatesOn(tenantId, day, cancellationToken);
        return
        [
            new(Contracts.PartyApprovals.EnhancedDueDiligenceType, gatesOn, Contracts.PartyPermissions.DueDiligenceApproveEnhanced),
            new(Contracts.PartyApprovals.RiskClassOverrideType, gatesOn, Contracts.PartyPermissions.DueDiligenceOverrideRisk),
            new(Contracts.PartyApprovals.UnblockType, gatesOn, Contracts.PartyPermissions.DueDiligenceUnblock),
        ];
    }

    /// <summary>The settings row in force says; with none, the gates are on for a tenant entitled to LIFE (OQ-123).</summary>
    private async Task<bool> GatesOn(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var life = (await tenants.For(tenantId, day, cancellationToken)).EntitledFeatures.Contains("LIFE");
        var setting = await ReadinessSql.InTenant(dataSource, tenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction, $"""
            SELECT gates_enabled::text FROM party.cfg_due_diligence_settings WHERE tenant_id = @t AND {ReadinessSql.InForce} ORDER BY effective_from DESC, version DESC LIMIT 1
            """, cancellationToken, ("t", tenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", day)), cancellationToken);
        return setting.Count == 0 ? life : setting[0] == "true";
    }

    private const string Step = "SHARED_OPERATIONAL_SETUP";

    public IReadOnlyCollection<string> Steps { get; } = [Step];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var life = (await tenants.For(context.TenantId, context.Day, cancellationToken)).EntitledFeatures.Contains("LIFE");
        var gatesOn = await GatesOn(context.TenantId, context.Day, cancellationToken);

        var providers = new List<string>();
        var reviewers = new List<string>();
        if (gatesOn)
        {
            if (await partners.ActivePartner(context.TenantId, PartnerTypes.Screening, context.Day, cancellationToken) is null)
            {
                providers.Add(PartnerTypes.Screening);
            }

            if (life && await partners.ActivePartner(context.TenantId, PartnerTypes.IdentityVerification, context.Day, cancellationToken) is null)
            {
                providers.Add(PartnerTypes.IdentityVerification);
            }

            // §7 and OQ-138: a role reviews hits, and a role completes periodic reviews.
            var inForce = await roles.InForce(context.TenantId, context.Day, cancellationToken);
            foreach (var function in new[] { "PARTY_REVIEW_SCREENING_HIT", "PARTY_COMPLETE_DUE_DILIGENCE_REVIEW" })
            {
                if (!inForce.Any(r => r.Functions.Contains(function)))
                {
                    reviewers.Add(function);
                }
            }
        }

        return
        [
            ReadinessResult.Of(Step, "DUE_DILIGENCE_LIFE_GATE", "A tenant entitled to LIFE has its due diligence gates on (GATES_OFF listed when not)",
                life && !gatesOn ? ["GATES_OFF"] : []),
            ReadinessResult.Of(Step, "DUE_DILIGENCE_PROVIDER", "With the gates on, an active partner of each kind they use (partner type listed when none)", providers),
            ReadinessResult.Of(Step, "DUE_DILIGENCE_REVIEWER", "With the gates on, staff roles grant the screening hit review and the periodic review (each function listed when no role grants it)", reviewers),
        ];
    }
}
