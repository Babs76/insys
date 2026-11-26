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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Application;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure.Channels;

/// <summary>
/// OQ-201 (Ade, 26 November 2026): the disclosure setting's pending change, written by SQL in the caller's transaction on Integration's DbContext, so the
/// pending change and its ApprovalRequested on the outbox are written together (the command's), and the grant or ending with its audit (the consumer's).
/// </summary>
internal sealed class DisclosureSettingRequests(IntegrationDbContext db) : IDisclosureSettingRequests
{
    private const string Returning = """
        RETURNING id AS "Id", row_version AS "RowVersion", broker_acknowledgement_allowed AS "Allowed", pending_broker_acknowledgement_allowed AS "PendingAllowed",
          pending_requested_by AS "RequestedBy", pending_requested_at AS "RequestedAt"
        """;

    public async Task<DisclosureSettingView?> Request(Guid tenantId, long? expectedVersion, bool brokerAcknowledgementAllowed, Guid requestedBy, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var hash = DisclosureSettingApproval.Subject(Guid.CreateVersion7(), brokerAcknowledgementAllowed);
        var by = requestedBy.ToString();
        var id = Guid.CreateVersion7();
        var rows = expectedVersion is { } version
            ? await db.Database.SqlQueryRaw<SettingRow>($"""
                UPDATE channel.disclosure_setting
                SET pending_broker_acknowledgement_allowed = @p2, pending_requested_by = @p3, pending_requested_at = @p4, pending_subject_hash = @p5,
                    row_version = row_version + 1, updated_at = @p4, updated_by = @p6
                WHERE tenant_id = @p0 AND row_version = @p1 AND pending_subject_hash IS NULL
                {Returning}
                """, Parameters(tenantId, version, brokerAcknowledgementAllowed, requestedBy, at, hash, by)).ToListAsync(cancellationToken)
            : await db.Database.SqlQueryRaw<SettingRow>($"""
                INSERT INTO channel.disclosure_setting (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, broker_acknowledgement_allowed,
                  pending_broker_acknowledgement_allowed, pending_requested_by, pending_requested_at, pending_subject_hash)
                VALUES (@p0, @p1, 1, @p4, @p6, @p4, @p6, true, @p2, @p3, @p4, @p5)
                ON CONFLICT (tenant_id) DO NOTHING
                {Returning}
                """, Parameters(tenantId, id, brokerAcknowledgementAllowed, requestedBy, at, hash, by)).ToListAsync(cancellationToken);
        if (rows.SingleOrDefault() is not { } row)
        {
            return null;
        }

        // The approval, through Integration's outbox in the same transaction (as a money dead letter's discard, DE-007).
        db.Publish(new ApprovalRequested(DisclosureSettingApproval.BusinessRefType, row.Id, DisclosureSettingApproval.ApprovalType, requestedBy, null, null,
            brokerAcknowledgementAllowed ? "Allow brokers to record their clients' disclosure acknowledgements" : "Make disclosure acknowledgement customer-only", hash),
            "DisclosureSetting", row.Id, at);
        return row.View();
    }

    public async Task<(bool Allowed, Guid RequestedBy)?> Grant(Guid tenantId, Guid settingId, string? subjectHash, Guid checkerId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // Captures the pending value before it is cleared: a CTE reads the row as it was.
        var rows = await db.Database.SqlQueryRaw<Ended>("""
            WITH before AS (
              SELECT id, pending_broker_acknowledgement_allowed AS allowed, pending_requested_by AS requested_by FROM channel.disclosure_setting
              WHERE tenant_id = @p0 AND id = @p1 AND pending_subject_hash = @p2 AND pending_requested_by <> @p3 FOR UPDATE)
            UPDATE channel.disclosure_setting s
            SET broker_acknowledgement_allowed = b.allowed, pending_broker_acknowledgement_allowed = NULL, pending_requested_by = NULL, pending_requested_at = NULL,
                pending_subject_hash = NULL, row_version = s.row_version + 1, updated_at = @p4, updated_by = @p5
            FROM before b WHERE s.tenant_id = @p0 AND s.id = b.id
            RETURNING b.allowed AS "Allowed", b.requested_by AS "RequestedBy"
            """, Parameters(tenantId, settingId, subjectHash ?? "", checkerId, at, checkerId.ToString())).ToListAsync(cancellationToken);
        return rows.SingleOrDefault() is { } ended ? (ended.Allowed, ended.RequestedBy) : null;
    }

    public async Task<(bool Allowed, Guid RequestedBy)?> Clear(Guid tenantId, Guid settingId, string? subjectHash, string by, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<Ended>("""
            WITH before AS (
              SELECT id, pending_broker_acknowledgement_allowed AS allowed, pending_requested_by AS requested_by FROM channel.disclosure_setting
              WHERE tenant_id = @p0 AND id = @p1 AND pending_subject_hash = @p2 FOR UPDATE)
            UPDATE channel.disclosure_setting s
            SET pending_broker_acknowledgement_allowed = NULL, pending_requested_by = NULL, pending_requested_at = NULL, pending_subject_hash = NULL,
                row_version = s.row_version + 1, updated_at = @p3, updated_by = @p4
            FROM before b WHERE s.tenant_id = @p0 AND s.id = b.id
            RETURNING b.allowed AS "Allowed", b.requested_by AS "RequestedBy"
            """, Parameters(tenantId, settingId, subjectHash ?? "", at, by.Length > 80 ? by[..80] : by)).ToListAsync(cancellationToken);
        return rows.SingleOrDefault() is { } ended ? (ended.Allowed, ended.RequestedBy) : null;
    }

    private static object[] Parameters(params object[] values) =>
        [.. values.Select((v, i) => new Npgsql.NpgsqlParameter($"p{i}", v))];

    private sealed class SettingRow
    {
        public Guid Id { get; set; }

        public long RowVersion { get; set; }

        public bool Allowed { get; set; }

        public bool? PendingAllowed { get; set; }

        public Guid? RequestedBy { get; set; }

        public DateTimeOffset? RequestedAt { get; set; }

        public DisclosureSettingView View() => new(Allowed, false, RowVersion,
            PendingAllowed is { } pending ? new DisclosureSettingChangeView(pending, RequestedBy!.Value, RequestedAt!.Value) : null);
    }

    private sealed class Ended
    {
        public bool Allowed { get; set; }

        public Guid RequestedBy { get; set; }
    }
}

/// <summary>
/// Workflow's decision on a change of the disclosure setting (OQ-201, Ade, 26 November 2026). Granted: the change applies, with the approver recorded and audited.
/// Every other ending (rejected, expired, withdrawn): the pending change is cleared, the setting stays as it was, and the outcome is audited. A decision the setting
/// will not take (another request, or the requester as checker) parks the delivery (ADR-022).
/// </summary>
public sealed class DisclosureSettingPolicies(IDisclosureSettingRequests requests, ITenantContext tenant, IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != DisclosureSettingApproval.BusinessRefType)
        {
            return;
        }

        if (await requests.Grant(tenant.RequireTenantId(), granted.BusinessRefId, granted.SubjectHash, granted.CheckerId, clock.UtcNow, cancellationToken) is not { } applied)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
        }

        audit.Record(new AuditEntry("CHANNEL", "ChangeDisclosureSetting", "DisclosureSetting", granted.BusinessRefId.ToString(), new Dictionary<string, string?>
        {
            ["brokerAcknowledgementAllowed"] = applied.Allowed ? "true" : "false",
            ["outcome"] = "GRANTED",
            ["approvalId"] = granted.ApprovalId.ToString(),
            ["requestedBy"] = applied.RequestedBy.ToString(),
            ["approvedBy"] = granted.CheckerId.ToString(),
        }));
    }

    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != DisclosureSettingApproval.BusinessRefType)
        {
            return;
        }

        var by = rejected.CheckerId == Guid.Empty ? "workflow" : rejected.CheckerId.ToString();
        if (await requests.Clear(tenant.RequireTenantId(), rejected.BusinessRefId, rejected.SubjectHash, by, clock.UtcNow, cancellationToken) is not { } cleared)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }

        audit.Record(new AuditEntry("CHANNEL", "ChangeDisclosureSettingNotApproved", "DisclosureSetting", rejected.BusinessRefId.ToString(), new Dictionary<string, string?>
        {
            ["brokerAcknowledgementAllowed"] = cleared.Allowed ? "true" : "false",
            ["outcome"] = rejected.Ending,
            ["approvalId"] = rejected.ApprovalId.ToString(),
            ["requestedBy"] = cleared.RequestedBy.ToString(),
            ["decidedBy"] = rejected.CheckerId == Guid.Empty ? null : rejected.CheckerId.ToString(),
        }));
    }
}
