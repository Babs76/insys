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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Communications.Api;

/// <summary>
/// The Communications module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class CommunicationsFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("COMMUNICATION_CALLBACK_ENDPOINTS", "NOTIFICATIONS", "Delivery tracking", "communications/callback-endpoints", "CommunicationsListCallbackEndpoints")
        .Action("COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT", "COMMUNICATION_CALLBACK_ENDPOINTS", "CommunicationsIssueCallbackEndpoint")
        .Action("COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT", "COMMUNICATION_CALLBACK_ENDPOINTS", "CommunicationsRevokeCallbackEndpoint")
        .Screen("COMMUNICATION_NOTIFICATIONS", "NOTIFICATIONS", "Delivery tracking", "communications/notifications", "CommunicationsListNotifications", "CommunicationsGetNotification")
        .Screen("COMMUNICATION_PREFERENCES", "CONSENT_PREFERENCES", "Communication preferences", "communications/preferences", "CommunicationsGetPreferences")
        .Action("COMMUNICATIONS_SET_PREFERENCE", "COMMUNICATION_PREFERENCES", "CommunicationsSetPreference")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_COMMUNICATIONS_NOTIFICATIONS", "CUSTOMER_PORTAL", "Notifications", "portal/communications/notifications", "PortalCommunicationsListNotifications", "PortalCommunicationsGetNotification")
        .Screen("CUSTOMER_PORTAL_COMMUNICATIONS_PREFERENCES", "CUSTOMER_PORTAL", "Notifications", "portal/communications/preferences", "PortalCommunicationsGetPreferences")
        .Action("CUSTOMER_PORTAL_COMMUNICATIONS_SET_PREFERENCE", "CUSTOMER_PORTAL_COMMUNICATIONS_PREFERENCES", "PortalCommunicationsSetPreference")
        .Screen("CUSTOMER_PORTAL_COMMUNICATIONS_DEVICES", "CUSTOMER_PORTAL", "Notifications", "portal/communications/devices", "PortalCommunicationsListDevices")
        .Action("CUSTOMER_PORTAL_COMMUNICATIONS_REGISTER_DEVICE", "CUSTOMER_PORTAL_COMMUNICATIONS_DEVICES", "PortalCommunicationsRegisterDevice")
        .Action("CUSTOMER_PORTAL_COMMUNICATIONS_REVOKE_DEVICE", "CUSTOMER_PORTAL_COMMUNICATIONS_DEVICES", "PortalCommunicationsRevokeDevice")
            .Functions,
    ];
}
