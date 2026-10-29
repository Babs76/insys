/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
namespace Insys.BuildingBlocks.Application.Auditing;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

/// <summary>
/// A sensitive operation to audit (IAM-006). Actor, tenant, time and correlation are added by the writer.
/// Details must already be masked; use <see cref="AuditMask"/>.
/// </summary>
public sealed record AuditEntry(
    string EventType,
    string Operation,
    string TargetType,
    string TargetId,
    IReadOnlyDictionary<string, string?>? Details = null)
{
    /// <summary>
    /// The outcome this entry records whatever the command's result, for a refused read of sensitive data (Denied, HLT-008,
    /// DOC-003); null takes the command's outcome (Success, or Failure when it fails).
    /// </summary>
    public AuditOutcome? Outcome { get; init; }
}

/// <summary>
/// Collects audit entries during a command. The transaction pipeline publishes them through the module's outbox in
/// the command's transaction when it succeeds, and in a separate transaction with outcome Failure when it fails.
/// The Audit module records them in audit.audit_event.
/// </summary>
public interface IAuditTrail
{
    void Record(AuditEntry entry);
}

public static class AuditMask
{
    /// <summary>Keeps the last four characters, for example "****1234".</summary>
    public static string? Last4(string? value) =>
        value is null ? null : value.Length <= 4 ? new string('*', value.Length) : $"****{value[^4..]}";
}

/// <summary>A complete audit record, for events outside a command such as an authorisation denial.</summary>
public sealed record AuditRecord(
    Guid TenantId,
    string ActorId,
    string ActorType,
    AuditEntry Entry,
    AuditOutcome Outcome,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);

/// <summary>
/// Writes an audit record directly, for events that have no business transaction: denied requests and
/// cross-tenant attempts (IAM-006, TEN-005). Implemented by the Audit module.
/// </summary>
public interface IAuditSink
{
    Task Record(AuditRecord record, CancellationToken cancellationToken);
}
