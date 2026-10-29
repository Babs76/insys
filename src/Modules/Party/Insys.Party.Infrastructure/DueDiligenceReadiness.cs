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
/// <item>DUE_DILIGENCE_REVIEWER: with the gates on, a staff role in force grants PARTY_REVIEW_SCREENING_HIT, so hits are reviewed.</item>
/// </list>
/// </summary>
internal sealed class DueDiligenceReadiness([FromKeyedServices(PartyDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantFunctionSettings tenants,
    IPartnerDirectory partners, IStaffRoles roles) : IReadinessCheck
{
    private const string Step = "SHARED_OPERATIONAL_SETUP";

    public IReadOnlyCollection<string> Steps { get; } = [Step];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var life = (await tenants.For(context.TenantId, context.Day, cancellationToken)).EntitledFeatures.Contains("LIFE");
        var setting = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction, $"""
            SELECT gates_enabled::text FROM party.cfg_due_diligence_settings WHERE tenant_id = @t AND {ReadinessSql.InForce} ORDER BY version DESC LIMIT 1
            """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        var gatesOn = setting.Count == 0 ? life : setting[0] == "true";

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

            if (!(await roles.InForce(context.TenantId, context.Day, cancellationToken)).Any(r => r.Functions.Contains("PARTY_REVIEW_SCREENING_HIT")))
            {
                reviewers.Add("PARTY_REVIEW_SCREENING_HIT");
            }
        }

        return
        [
            ReadinessResult.Of(Step, "DUE_DILIGENCE_LIFE_GATE", "A tenant entitled to LIFE has its due diligence gates on (GATES_OFF listed when not)",
                life && !gatesOn ? ["GATES_OFF"] : []),
            ReadinessResult.Of(Step, "DUE_DILIGENCE_PROVIDER", "With the gates on, an active partner of each kind they use (partner type listed when none)", providers),
            ReadinessResult.Of(Step, "DUE_DILIGENCE_REVIEWER", "With the gates on, a staff role grants the screening hit review (function listed when none does)", reviewers),
        ];
    }
}
