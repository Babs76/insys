/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.General.Contracts;
using Insys.General.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace Insys.General.Application;

/// <summary>
/// Grace for General instalments (F006, addenda/renewals-and-retention.md §4.1): Billing's arrears, as of its detection
/// date (BR-103), start or update a policy's grace; a payment refreshes them, so a payment in grace clears it. A policy
/// whose product has no grace rule for its frequency is left as it was (its arrears stay with staff); readiness flags the
/// missing rule.
/// </summary>
public sealed class GeneralGrace(IGeneralRepository general, IGeneralCodes codes, IPolicyArrears arrears, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    ILogger<GeneralGrace> logger) : IIntegrationEventHandler<ArrearsDetected>, IIntegrationEventHandler<InvoiceSettled>
{
    public async Task Handle(ArrearsDetected detected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var detectedOn = detected.DetectedOn ?? businessDate.DateOf(envelope.OccurredAt);
        foreach (var policyId in await general.PoliciesOnAccount(detected.AccountId, cancellationToken))
        {
            await Refresh(policyId, detectedOn, cancellationToken);
        }
    }

    public Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        settled.SourceDomain == GeneralRatingSubjects.SourceDomain ? Refresh(settled.PolicyRef, businessDate.Today, cancellationToken) : Task.CompletedTask;

    private async Task Refresh(Guid policyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (await general.Policy(policyId, cancellationToken) is not { Status: PolicyStatus.InForce } policy)
        {
            return;
        }

        if (await codes.GraceTerms(policy.ProductCode, policy.BillingFrequency ?? "", asOf, cancellationToken) is not { } grace)
        {
            logger.LogWarning("No grace rule in force for {ProductCode} {Frequency}: policy {PolicyId}'s arrears are left to staff", policy.ProductCode, policy.BillingFrequency,
                policy.Id);
            return;
        }

        var overdue = await arrears.For(tenant.RequireTenantId(), GeneralRatingSubjects.SourceDomain, policy.Id, asOf, cancellationToken);
        policy.RecordArrears(overdue.OverdueInstalments, overdue.OldestDueDate, grace, clock.UtcNow);
    }
}

/// <summary>
/// F006: grace ended unpaid and the policy was cancelled for non-payment: its claims for losses from the cancellation date
/// are declined with the grace rule's claim decision, at whatever stage short of payment. Earlier losses are untouched.
/// </summary>
public sealed class DeclineClaimsOnLapse(IGeneralRepository general, IClock clock, ILogger<DeclineClaimsOnLapse> logger)
    : IIntegrationEventHandler<General.Contracts.GeneralPolicyLapsedUnpaid>
{
    public async Task Handle(General.Contracts.GeneralPolicyLapsedUnpaid lapsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var id in await general.ClaimsOfPolicy(lapsed.PolicyId, cancellationToken))
        {
            if (await general.Claim(id, cancellationToken) is { } claim && claim.LossDate >= lapsed.CancelledFrom
                && claim.Status is Domain.Claims.ClaimStatus.FirstNotification or Domain.Claims.ClaimStatus.Registered or Domain.Claims.ClaimStatus.UnderAssessment
                    or Domain.Claims.ClaimStatus.Approved)
            {
                var declined = claim.DeclineForNonPayment(lapsed.ClaimDeclineReason, lapsed.CancelledFrom, clock.UtcNow);
                if (declined.IsFailure)
                {
                    logger.LogWarning("Claim {ClaimId} not declined for non-payment: {Code}", id, declined.Error.Code);
                }
            }
        }
    }
}

