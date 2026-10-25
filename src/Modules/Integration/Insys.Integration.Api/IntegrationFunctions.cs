/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Integration.Api;

/// <summary>
/// The Integration module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class IntegrationFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("INTEGRATION_DEAD_LETTERS", "INTEGRATION_OPERATIONS", "Dead letters", "integration/dead-letters", "ListDeadLetters", "GetDeadLetter")
        .Action("REPLAY_MESSAGE", "INTEGRATION_DEAD_LETTERS", "ReplayMessage")
        .Action("DISCARD_MESSAGE", "INTEGRATION_DEAD_LETTERS", "DiscardMessage")
        .Screen("INTEGRATION_DELIVERIES", "INTEGRATION_OPERATIONS", "Parked deliveries", "integration/deliveries", "IntegrationDeliveriesSummary")
        .Screen("INTEGRATION_PARKED_DELIVERIES", "INTEGRATION_OPERATIONS", "Parked deliveries", "integration/parked-deliveries", "ListParkedDeliveries", "GetParkedDelivery", "ListParkedDeliveriesByTenant")
        .Action("RETRY_PARKED_DELIVERY", "INTEGRATION_PARKED_DELIVERIES", "RetryParkedDelivery")
        .Action("RETRY_PARKED_DELIVERIES_FOR_CODE", "INTEGRATION_PARKED_DELIVERIES", "RetryParkedDeliveriesForCode")
        .Action("DISCARD_PARKED_DELIVERY", "INTEGRATION_PARKED_DELIVERIES", "DiscardParkedDelivery")
        .Screen("INTEGRATION_PARKING_SETTING", "INTEGRATION_OPERATIONS", "Parked deliveries", "integration/parking-setting", "GetParkingSetting")
        .Action("CHANGE_PARKING_SETTING", "INTEGRATION_PARKING_SETTING", "ChangeParkingSetting")
        .Screen("INTEGRATION_WEBHOOK_DELIVERIES", "INTEGRATION_OPERATIONS", "Webhook deliveries", "integration/webhook-deliveries", "ListWebhookDeliveries")
        .Action("RETRY_WEBHOOK_DELIVERY", "INTEGRATION_WEBHOOK_DELIVERIES", "RetryWebhookDelivery")
            .Functions,
    ];
}
