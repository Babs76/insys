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
namespace Insys.Integration.Contracts;

/// <summary>
/// Permission codes for the Integration module, in the iam.ref_permission_catalogue format "resource.operation".
/// The catalogue's operation enum is {read, create, update, approve, delete}, so replay and discard use "update" (OQ-007).
/// </summary>
public static class IntegrationPermissions
{
    public const string DeadLetterRead = "dead_letter.read";
    public const string DeadLetterUpdate = "dead_letter.update";
    public const string WebhookRead = "webhook_delivery.read";
    public const string WebhookUpdate = "webhook_delivery.update";
}
