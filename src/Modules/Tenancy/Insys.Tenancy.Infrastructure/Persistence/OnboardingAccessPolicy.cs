/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.Tenancy.Application.Tenants;
using Microsoft.Extensions.Logging;

namespace Insys.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Tenancy's policy for a new tenant's first administrator (OQ-067): the ORGANISATION_AND_ACCESS onboarding step is
/// completed when the tenant has an administrator, it is the current step, and the step's other readiness checks pass
/// (roles, organisation structure, authentication policy, channels). Otherwise the step is left to the operator
/// (CompleteOnboardingStep). The event arrives in the operator tenant; the onboarding case is the new tenant's, so it
/// is changed as that tenant.
/// </summary>
public sealed class OnboardingAccessPolicy(TenancyDbContext db, ITenantRepository tenants, OnboardingReadiness readiness, IClock clock,
    ILogger<OnboardingAccessPolicy> logger)
    : IIntegrationEventHandler<TenantAdministratorProvisioned>
{
    public const string Step = "ORGANISATION_AND_ACCESS";

    public Task Handle(TenantAdministratorProvisioned provisioned, EventEnvelope envelope, CancellationToken cancellationToken) =>
        db.WithinTenant(provisioned.TargetTenantId, async () =>
        {
            if (await tenants.Get(provisioned.TargetTenantId, cancellationToken) is not { } tenant)
            {
                return;
            }

            if (tenant.Onboarding?.CurrentStep == Step && OnboardingReadiness.NotReady(await readiness.For(tenant, cancellationToken), Step) is { } notReady)
            {
                logger.LogInformation("Tenant {TenantId} has its first administrator; {Step} is left to the operator: {Reason}",
                    provisioned.TargetTenantId, Step, notReady.Message);
                return;
            }

            var result = tenant.CompleteOnboardingStep(Step, clock.UtcNow);
            if (result.IsFailure)
            {
                logger.LogInformation("Tenant {TenantId} has its first administrator; {Step} was not completed: {Reason}",
                    provisioned.TargetTenantId, Step, result.Error.Message);
            }
        }, cancellationToken);
}
