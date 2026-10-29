/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks
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
namespace Insys.BuildingBlocks.Contracts.Payouts;

/// <summary>
/// A payout to a payee was held because the payee is not cleared (addenda/due-diligence.md §5): Party screens the payee
/// again before the payout when their screening is older than the gate allows (OQ-125), and the holder is told through
/// party.DueDiligenceChanged. Published by every module that holds payouts, so it lives in the building blocks, as
/// ApprovalRequested does. The payee only; no amount, account or reason beyond the hold.
/// </summary>
[IntegrationEvent("party.PayeeScreeningRequested")]
public sealed record PayeeScreeningRequested(Guid PartyId) : IIntegrationEvent;
