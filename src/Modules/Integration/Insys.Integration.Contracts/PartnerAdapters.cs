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
using System.Text.Json;

namespace Insys.Integration.Contracts;

/// <summary>A partner's answer to a call: accepted with its reference, or why not.</summary>
public sealed record PartnerCallResult(bool Accepted, string? PartnerRef, string? Error);

/// <summary>
/// Partner adapter to reinsurers (§8, §17): bordereaux and recovery bills. The platform makes no real calls (PROMPT
/// §11); the stub implementation accepts each and records it in integration.external_call_log (INT-006).
/// </summary>
public interface IReinsurerAdapter
{
    Task<PartnerCallResult> SendBordereau(Guid tenantId, string partnerCode, string treatyCode, string periodCode, string content, CancellationToken cancellationToken);

    Task<PartnerCallResult> BillRecovery(Guid tenantId, string partnerCode, string treatyCode, string claimNumber, decimal amount, string currency,
        CancellationToken cancellationToken);
}

/// <summary>Partner adapter to regulators: returns are submitted through it; the stub accepts and records each call.</summary>
public interface IRegulatorAdapter
{
    Task<PartnerCallResult> SubmitReturn(Guid tenantId, string regulator, string returnCode, string periodCode, JsonElement values, CancellationToken cancellationToken);
}
