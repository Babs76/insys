/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Workflow.Contracts;

public static class WorkflowPermissions
{
    public const string ApprovalRead = "approval.read";
    public const string ApprovalApprove = "approval.approve";
    public const string ProcessRead = "process.read";
    public const string ProcessCreate = "process.create";
    public const string ProcessUpdate = "process.update";
}

/// <summary>An approval was granted. Consumers act on their own business references only.</summary>
[IntegrationEvent("workflow.ApprovalGranted")]
public sealed record ApprovalGranted(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string? SubjectHash = null) : IIntegrationEvent;

[IntegrationEvent("workflow.ApprovalRejected")]
public sealed record ApprovalRejected(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId, string Reason, string? SubjectHash = null)
    : IIntegrationEvent;

/// <summary>Consumed by Workflow itself: EscalateTask (domain-model.json).</summary>
[IntegrationEvent("workflow.SlaBreached")]
public sealed record SlaBreached(Guid ProcessInstanceId, Guid TaskId, string TaskType, string? SlaCode) : IIntegrationEvent;

/// <summary>A role's authority for one approval: it may approve amounts up to the limit.</summary>
public sealed record ApprovalAuthority(string RoleCode, decimal Limit);

/// <summary>
/// Authority a module keeps in its own static data, for its own approval types (Life claims: life.cfg_claims_authority_limits
/// by claim type). For an approval type a source decides, Workflow uses the source's authority instead of the
/// delegation table; maker and checker still differ (SRS-002, WFL-004).
/// </summary>
public interface IApprovalAuthoritySource
{
    bool Decides(string approvalType);

    Task<IReadOnlyList<ApprovalAuthority>> For(Guid tenantId, string businessRefType, Guid businessRefId, IReadOnlyCollection<string> roleCodes, DateOnly day,
        CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: business time in a tenant. On the named calendar (workflow.ref_business_calendars) when it is
/// in force, otherwise Monday to Friday, 08:00 to 17:00, in the tenant's time zone (tenant.cfg_locale_defaults), or
/// Africa/Johannesburg when it has none (ADR-022 decision 6). Whole days in the duration are working days.
/// </summary>
public interface IBusinessTime
{
    /// <param name="isoDuration">An ISO 8601 duration such as PT4H or P1D.</param>
    Task<DateTimeOffset> Add(Guid tenantId, string? calendarCode, DateTimeOffset start, string isoDuration, CancellationToken cancellationToken);
}
