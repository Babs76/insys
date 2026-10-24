/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.BuildingBlocks.Infrastructure;
using Insys.Communications.Application.Triggers;
using Insys.Communications.Infrastructure.Persistence;
using Insys.General.Contracts;
using Insys.Health.Contracts;
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Communications.Infrastructure.Triggers;

/// <summary>
/// The event-driven triggers of addenda/communications.md §6, one inbox consumer per event (INT-005). A handler that
/// takes several events is registered once per event, with the event as the consumer's suffix.
/// </summary>
public static class TriggerRegistration
{
    public static IServiceCollection AddNotificationTriggers(this IServiceCollection services)
    {
        const string schema = CommunicationsDbContext.SchemaName;

        services.AddIntegrationEventHandler<ArrearsDetected, PaymentReminderTrigger, CommunicationsDbContext>(schema);

        services.AddIntegrationEventHandler<GeneralRenewalInvited, RenewalInvitationTrigger, CommunicationsDbContext>(schema);
        services.AddIntegrationEventHandler<GeneralRenewalReminderDue, RenewalReminderTrigger, CommunicationsDbContext>(schema);

        services.AddIntegrationEventHandler<GeneralClaimRegistered, GeneralClaimStatusTrigger, CommunicationsDbContext>(schema, "Registered");
        services.AddIntegrationEventHandler<GeneralClaimApproved, GeneralClaimStatusTrigger, CommunicationsDbContext>(schema, "Approved");
        services.AddIntegrationEventHandler<GeneralClaimDeclined, GeneralClaimStatusTrigger, CommunicationsDbContext>(schema, "Declined");
        services.AddIntegrationEventHandler<GeneralClaimSettled, GeneralClaimStatusTrigger, CommunicationsDbContext>(schema, "Settled");
        services.AddIntegrationEventHandler<GeneralClaimClosed, GeneralClaimStatusTrigger, CommunicationsDbContext>(schema, "Closed");

        services.AddIntegrationEventHandler<LifeClaimRegistered, LifeClaimStatusTrigger, CommunicationsDbContext>(schema, "Registered");
        services.AddIntegrationEventHandler<LifeClaimApproved, LifeClaimStatusTrigger, CommunicationsDbContext>(schema, "Approved");
        services.AddIntegrationEventHandler<LifeClaimDeclined, LifeClaimStatusTrigger, CommunicationsDbContext>(schema, "Declined");
        services.AddIntegrationEventHandler<LifeClaimSettled, LifeClaimStatusTrigger, CommunicationsDbContext>(schema, "Settled");

        services.AddIntegrationEventHandler<MedicalClaimAdjudicated, HealthClaimDecisionTrigger, CommunicationsDbContext>(schema, "Adjudicated");
        services.AddIntegrationEventHandler<MedicalClaimSettled, HealthClaimDecisionTrigger, CommunicationsDbContext>(schema, "Settled");
        services.AddIntegrationEventHandler<MedicalClaimRejected, HealthClaimDecisionTrigger, CommunicationsDbContext>(schema, "Rejected");
        services.AddIntegrationEventHandler<MedicalClaimReversed, HealthClaimDecisionTrigger, CommunicationsDbContext>(schema, "Reversed");

        services.AddIntegrationEventHandler<PreauthorisationApproved, PreauthDecisionTrigger, CommunicationsDbContext>(schema, "Approved");
        services.AddIntegrationEventHandler<PreauthorisationRejected, PreauthDecisionTrigger, CommunicationsDbContext>(schema, "Rejected");
        return services;
    }
}
