/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Contracts;
using Insys.StaticData.Domain.Changes;

namespace Insys.StaticData.Application.Changes;

public sealed record SetupBatchView(int Seq, string Table, string EntityCode, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash, DateTimeOffset? AppliedAt);

/// <param name="Batches">Every batch in its order; those with <c>appliedAt</c> are in force, on an abandoned set too.</param>
public sealed record SetupChangeSetView(Guid Id, string SetNumber, Guid SourceRef, Guid MakerId, string Status, string Hash, Guid? CheckerId, DateTimeOffset? ApprovedAt,
    DateTimeOffset? AppliedAt, string? Ending, Guid? AbandonRequestedBy, string? AbandonReason, DateTimeOffset? AbandonRequestedAt, Guid? AbandonedBy,
    DateTimeOffset? AbandonedAt, IReadOnlyList<SetupBatchView> Batches, long RowVersion)
{
    public static SetupChangeSetView From(SetupChangeSet s) => new(s.Id, s.SetNumber, s.SourceRef, s.MakerId, s.Status, s.Hash, s.CheckerId, s.ApprovedAt, s.AppliedAt, s.Ending,
        s.AbandonRequestedBy, s.AbandonReason, s.AbandonRequestedAt, s.AbandonedBy, s.AbandonedAt,
        [.. s.Batches.OrderBy(b => b.Seq).Select(b => new SetupBatchView(b.Seq, b.TableName, b.EntityCode, b.BatchId, b.ChangeRequestId, b.LineCount, b.Hash, b.AppliedAt))],
        s.RowVersion + (s.HasChanges ? 1 : 0));
}

public sealed record GetSetupChangeSet(Guid Id) : IQuery<SetupChangeSetView>;

/// <summary>OQ-180: asks to abandon an approved set that can't finish applying, with a reason.</summary>
public sealed record RequestSetupAbandon(Guid Id, string Reason, long ExpectedVersion) : ICommand<SetupChangeSetView>, IVersionedCommand;

/// <summary>A second person approves abandoning it.</summary>
public sealed record ApproveSetupAbandon(Guid Id, long ExpectedVersion) : ICommand<SetupChangeSetView>, IVersionedCommand;

/// <summary>A second person declines it: the set carries on applying.</summary>
public sealed record DeclineSetupAbandon(Guid Id, long ExpectedVersion) : ICommand<SetupChangeSetView>, IVersionedCommand;

/// <summary>
/// Abandoning an approved SETUP change set (OQ-180, Ade, 17 November): asked with a reason by one person and approved or declined by a different one,
/// only while the tenant is onboarding, every step audited. A live tenant's configuration changes table by table, never by abandoning a set.
/// </summary>
internal sealed class SetupAbandonHandlers(IChangeRequestRepository changes, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    Tenancy.Contracts.ITenantStatusQuery tenants)
    : IQueryHandler<GetSetupChangeSet, SetupChangeSetView>, ICommandHandler<RequestSetupAbandon, SetupChangeSetView>, ICommandHandler<ApproveSetupAbandon, SetupChangeSetView>,
        ICommandHandler<DeclineSetupAbandon, SetupChangeSetView>
{
    public async Task<Result<SetupChangeSetView>> Handle(GetSetupChangeSet query, CancellationToken cancellationToken) =>
        await changes.SetupSet(query.Id, cancellationToken) is { } set ? SetupChangeSetView.From(set) : Error.NotFound(ErrorCodes.NotFound, "The setup change set was not found.");

    public Task<Result<SetupChangeSetView>> Handle(RequestSetupAbandon command, CancellationToken cancellationToken) =>
        Decide(command.Id, command.ExpectedVersion, "RequestSetupAbandon", (set, by) => set.RequestAbandon(by, command.Reason, clock.UtcNow), command.Reason, cancellationToken);

    public Task<Result<SetupChangeSetView>> Handle(ApproveSetupAbandon command, CancellationToken cancellationToken) =>
        Decide(command.Id, command.ExpectedVersion, "ApproveSetupAbandon", (set, by) => set.ApproveAbandon(by, clock.UtcNow), null, cancellationToken);

    public Task<Result<SetupChangeSetView>> Handle(DeclineSetupAbandon command, CancellationToken cancellationToken) =>
        Decide(command.Id, command.ExpectedVersion, "DeclineSetupAbandon", (set, by) => set.DeclineAbandon(by), null, cancellationToken);

    private async Task<Result<SetupChangeSetView>> Decide(Guid id, long expected, string operation, Func<SetupChangeSet, Guid, Result> act, string? reason,
        CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out var by))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A set is abandoned by identified people.");
        }

        if (await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly)
        {
            return Error.Conflict(StaticDataGovernanceCodes.SetupTenantLive, "A setup change set is abandoned only while the tenant is onboarding.");
        }

        if (await changes.SetupSet(id, cancellationToken) is not { } set)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup change set was not found.");
        }

        if (set.RowVersion != expected)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The setup change set has changed since it was read. Read it again.");
        }

        var done = act(set, by);
        if (done.IsFailure)
        {
            return done.Error;
        }

        audit.Record(new AuditEntry("STATIC_DATA", operation, nameof(SetupChangeSet), set.Id.ToString(), new Dictionary<string, string?>
        {
            ["status"] = set.Status, ["reason"] = reason, ["requestedBy"] = set.AbandonRequestedBy?.ToString(),
            ["applied"] = set.Batches.Count(b => b.AppliedAt is not null).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["batches"] = set.Batches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        return SetupChangeSetView.From(set);
    }
}
