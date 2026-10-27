/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Domain.Documents;
using Insys.Workflow.Contracts;

namespace Insys.Documents.Application;

public interface IRecordHoldRepository
{
    Task<RecordHold?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>Holds by scope and status, oldest first, after the cursor.</summary>
    Task<IReadOnlyList<RecordHold>> List(string? scopeType, Guid? scopeId, bool? inForce, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>Whether a hold is in force on any of the records.</summary>
    Task<bool> AnyInForce(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);

    void Add(RecordHold hold);
}

/// <summary>A legal hold on a record itself (D-34): in force until releasedAt is set; a release waits for approval while releaseRequestedAt is set (OQ-111).</summary>
/// <param name="ScopeType">The held record's kind.</param>
/// <param name="ScopeId">The held record.</param>
/// <param name="LatestRejection">The hold's latest release approval that ended without being granted (OQ-113), or null.</param>
public sealed record RecordHoldView(Guid Id,
    [property: OneOf("PARTY", "LIFE_POLICY", "GENERAL_POLICY", "HEALTH_MEMBERSHIP", "LIFE_CLAIM", "GENERAL_CLAIM", "HEALTH_CLAIM", "CASE")] string ScopeType,
    Guid ScopeId, string ReasonCode, Guid PlacedBy, DateTimeOffset PlacedAt, DateTimeOffset? ReleasedAt, Guid? ReleasedBy, Guid? ReleaseRequestedBy,
    DateTimeOffset? ReleaseRequestedAt, string? ReleaseReason, LatestRejection? LatestRejection, long RowVersion)
{
    public static RecordHoldView From(RecordHold h, LatestRejection? latestRejection) => new(h.Id, h.ScopeType, h.ScopeId, h.ReasonCode, h.PlacedBy, h.PlacedAt,
        h.ReleasedAt, h.ReleasedBy, h.ReleaseRequestedBy, h.ReleaseRequestedAt, h.ReleaseReason, latestRejection, h.RowVersion);
}

/// <summary>Command PlaceRecordHold (D-34): a legal hold on a party, policy, membership, claim or case that exists and is in the caller's scope.</summary>
public sealed record PlaceRecordHold(string ScopeType, Guid ScopeId, string ReasonCode) : ICommand<RecordHoldView>;

/// <summary>Command RequestRecordHoldRelease: asks for a second person's approval to release the hold (OQ-111); it stays in force until approved.</summary>
public sealed record RequestRecordHoldRelease(Guid HoldId, string Reason, long ExpectedVersion) : ICommand<RecordHoldView>;

public sealed record GetRecordHold(Guid HoldId) : IQuery<RecordHoldView>;

public sealed record ListRecordHolds(string? ScopeType, Guid? ScopeId, bool? InForce, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<RecordHoldView>>;

public sealed class PlaceRecordHoldValidator : AbstractValidator<PlaceRecordHold>
{
    public PlaceRecordHoldValidator()
    {
        RuleFor(c => c.ScopeType).Must(RecordHoldScopes.All.Contains).WithMessage($"One of: {string.Join(", ", RecordHoldScopes.All)}.");
        RuleFor(c => c.ScopeId).NotEmpty();
        RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(15);
    }
}

public sealed class RequestRecordHoldReleaseValidator : AbstractValidator<RequestRecordHoldRelease>
{
    public RequestRecordHoldReleaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
}

/// <summary>The held record, through the module that owns it (ILegalHoldTargets): 404 when it does not exist, 403 OUT_OF_SCOPE outside the caller's scope (F103).</summary>
public sealed class RecordHoldTargets(IEnumerable<ILegalHoldTargets> targets, ITenantContext tenant, IExecutionContext execution)
{
    public async Task<Result> Check(string scopeType, Guid scopeId, CancellationToken cancellationToken)
    {
        var scopes = await ScopesOf(scopeType, scopeId, cancellationToken);
        if (scopes is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"The {scopeType} record was not found.");
        }

        return scopes.All(s => execution.CurrentScope.Allows(s.ProductCode, s.OrgNodeCode))
            ? Result.Success()
            : Error.Forbidden(ErrorCodes.OutOfScope, $"The {scopeType} record is outside your scope.");
    }

    /// <summary>Whether the caller may see a hold on the record: a record outside the caller's scope, or gone, is left out of reads.</summary>
    public async Task<bool> Visible(string scopeType, Guid scopeId, CancellationToken cancellationToken) =>
        await ScopesOf(scopeType, scopeId, cancellationToken) is { } scopes && scopes.All(s => execution.CurrentScope.Allows(s.ProductCode, s.OrgNodeCode));

    private async Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(string scopeType, Guid scopeId, CancellationToken cancellationToken) =>
        targets.FirstOrDefault(t => t.Covers(scopeType)) is { } source
            ? await source.ScopesOf(tenant.TenantId ?? throw new InvalidOperationException("A legal hold is placed in a tenant."), scopeType, scopeId, cancellationToken)
            : null;
}

internal sealed class RecordHoldHandlers(IRecordHoldRepository holds, IDocumentCatalogue catalogue, RecordHoldTargets targets, IApprovalHistory history,
    IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<PlaceRecordHold, RecordHoldView>,
    ICommandHandler<RequestRecordHoldRelease, RecordHoldView>,
    IQueryHandler<GetRecordHold, RecordHoldView>,
    IQueryHandler<ListRecordHolds, IReadOnlyList<RecordHoldView>>
{
    public async Task<Result<RecordHoldView>> Handle(PlaceRecordHold command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var target = await targets.Check(command.ScopeType, command.ScopeId, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        if (!await catalogue.LegalHoldReasonExists(command.ReasonCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(DocumentErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a legal hold reason in force.");
        }

        var hold = RecordHold.Place(command.ScopeType, command.ScopeId, command.ReasonCode, user.Value, clock.UtcNow);
        if (hold.IsFailure)
        {
            return hold.Error;
        }

        holds.Add(hold.Value);
        audit.Record(new AuditEntry("DOCUMENT", "PlaceRecordHold", nameof(RecordHold), hold.Value.Id.ToString(),
            new Dictionary<string, string?> { ["scopeType"] = command.ScopeType, ["scopeId"] = command.ScopeId.ToString(), ["reason"] = command.ReasonCode }));
        return RecordHoldView.From(hold.Value, null);
    }

    public async Task<Result<RecordHoldView>> Handle(RequestRecordHoldRelease command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var hold = await Load(command.HoldId, cancellationToken);
        if (hold.IsFailure)
        {
            return hold.Error;
        }

        var target = await targets.Check(hold.Value.ScopeType, hold.Value.ScopeId, cancellationToken);
        if (target.IsFailure && target.Error.Code == ErrorCodes.OutOfScope)
        {
            return target.Error;
        }

        if (hold.Value.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The hold has changed since it was read.");
        }

        var requested = hold.Value.RequestRelease(user.Value, command.Reason, clock.UtcNow);
        if (requested.IsFailure)
        {
            return requested.Error;
        }

        audit.Record(new AuditEntry("DOCUMENT", "RequestRecordHoldRelease", nameof(RecordHold), hold.Value.Id.ToString(),
            new Dictionary<string, string?> { ["scopeType"] = hold.Value.ScopeType, ["scopeId"] = hold.Value.ScopeId.ToString() }));
        return RecordHoldView.From(hold.Value, await history.Latest(Contracts.DocumentApprovals.RecordHoldBusinessRefType, hold.Value.Id, cancellationToken));
    }

    public async Task<Result<RecordHoldView>> Handle(GetRecordHold query, CancellationToken cancellationToken)
    {
        var hold = await holds.Get(query.HoldId, cancellationToken);
        if (hold is null || !await targets.Visible(hold.ScopeType, hold.ScopeId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The legal hold was not found.");
        }

        return RecordHoldView.From(hold, await history.Latest(Contracts.DocumentApprovals.RecordHoldBusinessRefType, hold.Id, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<RecordHoldView>>> Handle(ListRecordHolds query, CancellationToken cancellationToken)
    {
        var views = new List<RecordHoldView>();
        foreach (var hold in await holds.List(query.ScopeType, query.ScopeId, query.InForce, query.AfterId, query.Limit, cancellationToken))
        {
            if (await targets.Visible(hold.ScopeType, hold.ScopeId, cancellationToken))
            {
                views.Add(RecordHoldView.From(hold, await history.Latest(Contracts.DocumentApprovals.RecordHoldBusinessRefType, hold.Id, cancellationToken)));
            }
        }

        return views;
    }

    private async Task<Result<RecordHold>> Load(Guid id, CancellationToken cancellationToken) =>
        await holds.Get(id, cancellationToken) is { } hold ? hold : Error.NotFound(ErrorCodes.NotFound, "The legal hold was not found.");
}

/// <summary>
/// Workflow's decision on releasing a record's legal hold (LEGAL_HOLD_RELEASE, OQ-111), as for a document's hold. Approved:
/// the hold is released. Rejected, expired or withdrawn: the request is cleared and the hold stays in force. A redelivered
/// grant already applied changes nothing; one the hold will not take parks the delivery (D-5, ADR-022).
/// </summary>
public sealed class RecordHoldReleasePolicy(IRecordHoldRepository holds, IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid holdId, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != Contracts.DocumentApprovals.RecordHoldBusinessRefType || await holds.Get(holdId, cancellationToken) is not { } hold)
        {
            return;
        }

        if (approved && hold.ReleaseApplied(approvalId))
        {
            return;
        }

        if (hold.DecideRelease(approved, approvalId, checkerId, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied,
                $"{Contracts.DocumentApprovals.LegalHoldReleaseType}:{approvalId}");
        }

        audit.Record(new AuditEntry("DOCUMENT", approved ? "ReleaseRecordHold" : "KeepRecordHold", nameof(RecordHold), hold.Id.ToString(),
            new Dictionary<string, string?> { ["scopeType"] = hold.ScopeType, ["scopeId"] = hold.ScopeId.ToString(), ["approval"] = approvalId.ToString() }));
    }
}
