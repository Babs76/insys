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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Contracts;
using Insys.StaticData.Contracts;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Approvals;
using Microsoft.Extensions.Options;

namespace Insys.Workflow.Application;

public sealed class ApprovalOptions
{
    /// <summary>TODO(spec): how long an approval may wait is not specified.</summary>
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromDays(14);
}

public interface IApprovalRepository
{
    Task<Approval?> Get(Guid id, CancellationToken cancellationToken);

    Task<Approval?> FindRequested(string businessRefType, Guid businessRefId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> DueForExpiry(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    void Add(Approval approval);
}

/// <summary>Approval authority in force (workflow.cfg_delegation_and_approval_authority) for a set of roles.</summary>
public interface IAuthorityReader
{
    Task<IReadOnlyList<AuthorityGrant>> For(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether any role in the tenant has authority in force for the approval type.</summary>
    Task<bool> IsConfigured(string approvalType, DateOnly day, CancellationToken cancellationToken);
}

public sealed record ApprovalView(Guid Id, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid? CheckerId, string Status,
    decimal? Amount, string? Currency, string? Reason, string? DecisionReason, DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, DateTimeOffset? ExpiresAt,
    string? SubjectHash, long RowVersion);

public interface IApprovalQueries
{
    Task<IReadOnlyList<ApprovalView>> List(string? status, string? businessRefType, Guid? businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ApprovalView?> Get(Guid id, CancellationToken cancellationToken);
}

public sealed record Approve(Guid ApprovalId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record Reject(Guid ApprovalId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Approve or reject the approval of a business reference, without its version (for the static data routes).</summary>
public sealed record DecideByReference(string BusinessRefType, Guid BusinessRefId, bool Approve, string? Reason) : ICommand<Guid>;

public sealed record ExpireApproval(Guid ApprovalId) : ICommand<long>;

public sealed record ListApprovals(string? Status, string? BusinessRefType, Guid? BusinessRefId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ApprovalView>>;

public sealed record GetApproval(Guid ApprovalId) : IQuery<ApprovalView>;

public sealed record DueApprovals(DateTimeOffset Now) : IQuery<IReadOnlyList<Guid>>;

public sealed class RejectValidator : AbstractValidator<Reject>
{
    public RejectValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

internal static class Checker
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "Approvals are decided by identified users.");
}

/// <summary>
/// Decides an approval: the checker's authority comes from their roles today and the authority in force (WFL-004).
/// Static data approvals are the exception while the tenant has no authority configured for their type: the authority
/// table is itself Tier 1 static data, so its first rows could never be approved. Until then the static_data.approve
/// permission on the route is the authority, with maker and checker still different (SDM-003; OQ-050).
/// </summary>
public sealed class ApprovalDecision(IUserRoleQuery roles, IAuthorityReader authority, IEnumerable<IApprovalAuthoritySource> sources, ITenantContext tenant,
    IExecutionContext execution, IClock clock, IAuditTrail audit)
{
    private const string PermissionAuthority = "(static_data.approve)";

    public async Task<Result> Decide(Approval approval, Guid checkerId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("APPROVAL", approve ? "Approve" : "Reject", nameof(Approval), approval.Id.ToString(),
            new Dictionary<string, string?> { ["type"] = approval.ApprovalType, ["businessRef"] = $"{approval.BusinessRefType}:{approval.BusinessRefId}" }));
        // The checker also holds the permission the requester named (bank_account.verify, say), for approving and rejecting alike.
        if (approval.RequiredPermission is { } permission && !execution.HasPermission(permission))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, $"Deciding this approval needs the {permission} permission.");
        }

        if (!approve)
        {
            return approval.Reject(checkerId, reason ?? "", clock.UtcNow);
        }

        var checkerRoles = await roles.EffectiveRoles(tenant.RequireTenantId(), checkerId, cancellationToken);
        // A module that keeps the authority for this approval type in its own static data decides it (Life claims).
        var grants = sources.FirstOrDefault(s => s.Decides(approval.ApprovalType)) is { } source
            ? (await source.For(tenant.RequireTenantId(), approval.BusinessRefType, approval.BusinessRefId, checkerRoles, clock.UtcToday, cancellationToken))
                .Select(a => new AuthorityGrant(a.RoleCode, approval.ApprovalType, a.Limit)).ToList()
            : await authority.For(checkerRoles, clock.UtcToday, cancellationToken);
        if (approval.ApprovalType.StartsWith(StaticDataApprovals.ApprovalTypePrefix, StringComparison.Ordinal)
            && !await authority.IsConfigured(approval.ApprovalType, clock.UtcToday, cancellationToken))
        {
            grants = [.. grants, new AuthorityGrant(PermissionAuthority, approval.ApprovalType, 0m)];
        }

        return approval.Approve(checkerId, grants, clock.UtcNow);
    }
}

internal sealed class ApproveHandler(IApprovalRepository approvals, ApprovalDecision decision, IExecutionContext execution) : ICommandHandler<Approve, long>
{
    public async Task<Result<long>> Handle(Approve command, CancellationToken cancellationToken)
    {
        var checker = Checker.Require(execution);
        var approval = await approvals.Get(command.ApprovalId, cancellationToken);
        if (checker.IsFailure || approval is null)
        {
            return checker.IsFailure ? checker.Error : Error.NotFound(ErrorCodes.NotFound, "The approval was not found.");
        }

        if (approval.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The approval has changed since it was read.");
        }

        var result = await decision.Decide(approval, checker.Value, approve: true, null, cancellationToken);
        return result.IsFailure ? result.Error : approval.RowVersion + 1;
    }
}

internal sealed class RejectHandler(IApprovalRepository approvals, ApprovalDecision decision, IExecutionContext execution) : ICommandHandler<Reject, long>
{
    public async Task<Result<long>> Handle(Reject command, CancellationToken cancellationToken)
    {
        var checker = Checker.Require(execution);
        var approval = await approvals.Get(command.ApprovalId, cancellationToken);
        if (checker.IsFailure || approval is null)
        {
            return checker.IsFailure ? checker.Error : Error.NotFound(ErrorCodes.NotFound, "The approval was not found.");
        }

        if (approval.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The approval has changed since it was read.");
        }

        var result = await decision.Decide(approval, checker.Value, approve: false, command.Reason, cancellationToken);
        return result.IsFailure ? result.Error : approval.RowVersion + 1;
    }
}

internal sealed class DecideByReferenceHandler(IApprovalRepository approvals, ApprovalDecision decision, IExecutionContext execution)
    : ICommandHandler<DecideByReference, Guid>
{
    public async Task<Result<Guid>> Handle(DecideByReference command, CancellationToken cancellationToken)
    {
        var checker = Checker.Require(execution);
        if (checker.IsFailure)
        {
            return checker.Error;
        }

        var approval = await approvals.FindRequested(command.BusinessRefType, command.BusinessRefId, cancellationToken);
        if (approval is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "No approval is waiting for this change. A just-submitted change appears within seconds.");
        }

        var result = await decision.Decide(approval, checker.Value, command.Approve, command.Reason, cancellationToken);
        return result.IsFailure ? result.Error : approval.Id;
    }
}

internal sealed class ExpireApprovalHandler(IApprovalRepository approvals, IClock clock, IAuditTrail audit) : ICommandHandler<ExpireApproval, long>
{
    public async Task<Result<long>> Handle(ExpireApproval command, CancellationToken cancellationToken)
    {
        var approval = await approvals.Get(command.ApprovalId, cancellationToken);
        if (approval is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The approval was not found.");
        }

        audit.Record(new AuditEntry("APPROVAL", "Expire", nameof(Approval), approval.Id.ToString()));
        var result = approval.Expire(clock.UtcNow);
        return result.IsFailure ? result.Error : approval.RowVersion + 1;
    }
}

internal sealed class ListApprovalsHandler(IApprovalQueries queries) : IQueryHandler<ListApprovals, IReadOnlyList<ApprovalView>>
{
    public async Task<Result<IReadOnlyList<ApprovalView>>> Handle(ListApprovals query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.Status, query.BusinessRefType, query.BusinessRefId, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetApprovalHandler(IApprovalQueries queries) : IQueryHandler<GetApproval, ApprovalView>
{
    public async Task<Result<ApprovalView>> Handle(GetApproval query, CancellationToken cancellationToken) =>
        await queries.Get(query.ApprovalId, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The approval was not found.");
}

internal sealed class DueApprovalsHandler(IApprovalRepository approvals) : IQueryHandler<DueApprovals, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(DueApprovals query, CancellationToken cancellationToken) =>
        Result.Success(await approvals.DueForExpiry(query.Now, 500, cancellationToken));
}

/// <summary>
/// Workflow's policy for workflow.ApprovalRequested: create the Approval (RequestApproval). A request for a business
/// reference that already has an approval waiting is a duplicate and changes nothing.
/// </summary>
public sealed class RequestApprovalPolicy(IApprovalRepository approvals, IClock clock, IOptions<ApprovalOptions> options) : IIntegrationEventHandler<ApprovalRequested>
{
    public async Task Handle(ApprovalRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await approvals.FindRequested(requested.BusinessRefType, requested.BusinessRefId, cancellationToken) is not null)
        {
            return;
        }

        var now = clock.UtcNow;
        approvals.Add(Approval.RequestApproval(Guid.CreateVersion7(), requested.BusinessRefType, requested.BusinessRefId, requested.ApprovalType, requested.MakerId,
            requested.Amount, requested.Currency, requested.Reason, now, now + options.Value.DefaultExpiry, requested.SubjectHash, requested.RequiredPermission));
    }
}

/// <summary>Workflow's policy for workflow.ApprovalWithdrawn: the business reference's waiting approval is withdrawn.</summary>
public sealed class WithdrawApprovalPolicy(IApprovalRepository approvals) : IIntegrationEventHandler<ApprovalWithdrawn>
{
    public async Task Handle(ApprovalWithdrawn withdrawn, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await approvals.FindRequested(withdrawn.BusinessRefType, withdrawn.BusinessRefId, cancellationToken) is { } approval)
        {
            approval.Withdraw();
        }
    }
}
