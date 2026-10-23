/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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
namespace Insys.BuildingBlocks.Contracts.Auditing;

/// <summary>
/// A sensitive operation and its outcome (IAM-006). Every module writes it to its own outbox in the command's
/// transaction (or, when the command fails, in a separate one), and the Audit module records it in
/// audit.audit_event. Details are masked before they get here (INT-007).
/// </summary>
[IntegrationEvent("audit.SensitiveOperationRecorded")]
public sealed record SensitiveOperationRecorded(
    string ActorId,
    string ActorType,
    string EventType,
    string Operation,
    string TargetType,
    string TargetId,
    string Outcome,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string?>? Details) : IIntegrationEvent;
