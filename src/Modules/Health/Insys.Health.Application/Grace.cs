/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Health.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Health.Application;

/// <summary>
/// Grace for Health contributions (F006, addenda/renewals-and-retention.md §4.1): Billing's arrears on a membership's
/// member and employer contributions, as of its detection date (BR-103), start or update its grace; a payment refreshes
/// them, so a payment clears it (and reinstates a membership suspended for them). A membership with no grace rule in
/// force is left as it was; readiness flags the missing rule.
/// </summary>
public sealed class HealthGrace(IHealthRepository health, IHealthCodes codes, IPolicyArrears arrears, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    ILogger<HealthGrace> logger) : IIntegrationEventHandler<ArrearsDetected>, IIntegrationEventHandler<InvoiceSettled>
{
    public async Task Handle(ArrearsDetected detected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var detectedOn = detected.DetectedOn ?? businessDate.DateOf(envelope.OccurredAt);
        foreach (var membershipId in await health.MembershipsOnAccount(detected.AccountId, cancellationToken))
        {
            await Refresh(membershipId, detectedOn, cancellationToken);
        }
    }

    public Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        settled.SourceDomain is HealthReferences.SourceDomain or HealthReferences.EmployerSourceDomain
            ? Refresh(settled.PolicyRef, businessDate.Today, cancellationToken)
            : Task.CompletedTask;

    private async Task Refresh(Guid membershipId, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (await health.Membership(membershipId, cancellationToken) is not { } membership)
        {
            return;
        }

        var schemeNumber = membership.SchemeId is { } schemeId && await health.Scheme(schemeId, cancellationToken) is { } scheme ? scheme.SchemeNumber : null;
        if (await codes.ContributionGrace(schemeNumber, asOf, cancellationToken) is not { } grace)
        {
            logger.LogWarning("No contribution grace rule in force for membership {MembershipId}: its arrears are left to staff", membership.Id);
            return;
        }

        // The member's and the employer's contributions both pay for the cover: the oldest unpaid of either starts grace.
        var tenantId = tenant.RequireTenantId();
        var member = await arrears.For(tenantId, HealthReferences.SourceDomain, membership.Id, asOf, cancellationToken);
        var employer = await arrears.For(tenantId, HealthReferences.EmployerSourceDomain, membership.Id, asOf, cancellationToken);
        var oldest = new[] { member.OldestDueDate, employer.OldestDueDate }.Where(d => d is not null).Min();
        var recorded = membership.RecordArrears(member.OverdueInstalments + employer.OverdueInstalments, oldest, grace, asOf, clock.UtcNow);
        if (recorded.IsFailure)
        {
            throw new InvalidOperationException($"Membership {membership.MemberNumber}'s arrears were not recorded: {recorded.Error.Message}");
        }
    }
}
