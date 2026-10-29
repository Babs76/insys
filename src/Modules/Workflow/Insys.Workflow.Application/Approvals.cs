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
using Insys.Workflow.Domain.Processes;
using Microsoft.Extensions.Options;

namespace Insys.Workflow.Application;

public sealed class ApprovalOptions
{
    /// <summary>How long an approval waits when its type has no expiry in force (workflow.cfg_approval_expiry); not specified, so 14 days.</summary>
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

/// <param name="Screen">The SCREEN function that opens the record the approval is about (BR-005); the console maps it to its route.</param>
public sealed record ApprovalView(Guid Id, [property: BusinessRefTypeField] string BusinessRefType, Guid BusinessRefId, [property: ApprovalTypeField] string ApprovalType,
    Guid MakerId, Guid? CheckerId, ApprovalStatus Status,
    decimal? Amount, string? Currency, string? Reason, string? DecisionReason, DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, DateTimeOffset? ExpiresAt,
    string? SubjectHash, string? RequiredPermission, long RowVersion, [property: BuildingBlocks.Contracts.AlwaysPresent] string? NotAppliedBy = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? NotAppliedAt = null)
{
    [BuildingBlocks.Contracts.AlwaysPresent]
    public string? Screen => ApprovalKinds.ScreenFor(BusinessRefType);

    /// <summary>BR-109: raised by the platform with no person acting (MakerId is the system requester); any person with authority decides it.</summary>
    public bool RaisedBySystem => MakerChecker.IsSystem(MakerId);

    public static ApprovalView Of(Approval a) =>
        new(a.Id, a.BusinessRefType, a.BusinessRefId, a.ApprovalType, a.MakerId, a.CheckerId, a.Status, a.Amount, a.Currency, a.Reason, a.DecisionReason,
            a.RequestedAt, a.DecidedAt, a.ExpiresAt, a.SubjectHash, a.RequiredPermission, a.RowVersion, a.NotAppliedBy, a.NotAppliedAt);
}

public interface IApprovalQueries
{
    Task<IReadOnlyList<ApprovalView>> List(string? status, string? businessRefType, Guid? businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ApprovalView?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>Waiting approvals in the caller's scope that another user asked for, oldest first by ID (BR-004 candidates).</summary>
    Task<IReadOnlyList<Approval>> WaitingFromOthers(Guid userId, string? businessRefType, Guid? businessRefId, Guid? afterId, int limit,
        CancellationToken cancellationToken);
}

/// <param name="Reason">Optional (BR-001): why the checker approved; kept on the approval like a rejection's reason.</param>
public sealed record Approve(Guid ApprovalId, long ExpectedVersion, string? Reason = null) : ICommand<long>, IVersionedCommand;

public sealed class ApproveValidator : AbstractValidator<Approve>
{
    public ApproveValidator() => RuleFor(c => c.Reason).MaximumLength(2000);
}

/// <summary>The signed-in user's approval authority (BR-002): one row per role and approval type in force, with the limit's currency.</summary>
public sealed record MyAuthority : IQuery<IReadOnlyList<AuthorityView>>;

/// <param name="Currency">The limit's currency: the row's own, or the tenant's base currency when the row has none (OQ-112).</param>
/// <param name="Basis">
/// Where the authority comes from (BR-081): DELEGATION, the delegation table; MODULE_LIMIT, a module's own limits (Life
/// claims by claim type); PERMISSION, a static data approval type the delegation table does not configure, which a holder
/// of static_data.approve decides whatever the amount (Limit 0, RoleCode the permission).
/// </param>
/// <param name="AppliesTo">The records the row covers, as name=value (claimType=DEATH); null when it covers every record of the type.</param>
public sealed record AuthorityView([property: ApprovalTypeField] string ApprovalType, string RoleCode, decimal Limit, string? Currency,
    [property: OneOf(AuthorityBases.Delegation, AuthorityBases.ModuleLimit, AuthorityBases.Permission)] string Basis = AuthorityBases.Delegation,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? AppliesTo = null);

public static class AuthorityBases
{
    public const string Delegation = "DELEGATION";
    public const string ModuleLimit = "MODULE_LIMIT";
    public const string Permission = "PERMISSION";
}

public sealed record Reject(Guid ApprovalId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Approve or reject the approval of a business reference, without its version (for the static data routes).</summary>
public sealed record DecideByReference(string BusinessRefType, Guid BusinessRefId, bool Approve, string? Reason) : ICommand<Guid>;

public sealed record ExpireApproval(Guid ApprovalId) : ICommand<long>;

/// <summary>Command WithdrawApproval: the requester no longer wants it; the owning module is told, as for a rejection.</summary>
public sealed record WithdrawApproval(Guid ApprovalId, string Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>How long an approval of each type waits before it expires (workflow.cfg_approval_expiry); the default when none is in force.</summary>
public interface IApprovalExpiry
{
    Task<TimeSpan?> For(string approvalType, DateOnly day, CancellationToken cancellationToken);
}

/// <param name="Decidable">Only approvals the caller can decide now (BR-004): waiting, not their own, in scope, and within their authority.</param>
public sealed record ListApprovals(string? Status, string? BusinessRefType, Guid? BusinessRefId, Guid? AfterId, int Limit, bool Decidable = false)
    : IQuery<IReadOnlyList<ApprovalView>>;

public sealed record GetApproval(Guid ApprovalId) : IQuery<ApprovalView>;

public sealed record DueApprovals(DateTimeOffset Now) : IQuery<IReadOnlyList<Guid>>;

public sealed class RejectValidator : AbstractValidator<Reject>
{
    public RejectValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

public sealed class WithdrawApprovalValidator : AbstractValidator<WithdrawApproval>
{
    public WithdrawApprovalValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

/// <summary>
/// Tells the requester that their approval expired or was withdrawn for them: a follow-up task on the built-in process
/// APPROVAL_FOLLOW_UP, assigned to them, due in one business day, so it is in their work list.
/// </summary>
public sealed class RequesterFollowUp(IProcessRepository processes, Contracts.IBusinessTime businessTime, ITenantContext tenant, IClock clock, IAuditTrail audit)
{
    public const string QueueCode = "REQUESTER";

    public async Task Open(Approval approval, string ending, CancellationToken cancellationToken)
    {
        // BR-109: an approval the platform raised has no person to tell; the module that raised it receives the ending and acts on it.
        if (MakerChecker.IsSystem(approval.MakerId))
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.ApprovalFollowUp, approval.BusinessRefType, approval.BusinessRefId,
            _ => new TaskRouting(QueueCode, null, due), now).Value;
        process.AssignTask(process.OpenTask!.Id, approval.MakerId);
        processes.Add(process);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Id.ToString(),
            new Dictionary<string, string?> { ["process"] = BuiltInProcesses.ApprovalFollowUp.ProcessCode, ["approval"] = approval.Id.ToString(), ["ending"] = ending }));
    }
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
    IExecutionContext execution, IBusinessDate businessDate, Tenancy.Contracts.ITenantBaseCurrency baseCurrency, IClock clock, IAuditTrail audit,
    IEnumerable<IApprovalCheckerExclusions> exclusions)
{
    private const string PermissionAuthority = "(static_data.approve)";

    public async Task<Result> Decide(Approval approval, Guid checkerId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry("APPROVAL", approve ? "Approve" : "Reject", nameof(Approval), approval.Id.ToString(),
            new Dictionary<string, string?> { ["type"] = approval.ApprovalType, ["businessRef"] = $"{approval.BusinessRefType}:{approval.BusinessRefId}" }));
        // F103 (addenda/access-scopes.md §4): the checker's scope for deciding approvals covers the record the approval is about.
        if (approval.IsScoped && !execution.CurrentScope.Allows(approval.ProductCode, approval.OrgNodeCode))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "The record this approval is about is outside your scope.");
        }

        // The checker also holds the permission the requester named (bank_account.verify, say), for approving and rejecting alike.
        if (approval.RequiredPermission is { } permission && !execution.HasPermission(permission))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, $"Deciding this approval needs the {permission} permission.");
        }

        // SRS-002, BR-109: nor anyone the owning module excludes (for enhanced due diligence, everyone who contributed to the assessment).
        if (await Excluded(approval, checkerId, cancellationToken))
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "You contributed to what this approval is about, so another person must decide it.");
        }

        if (!approve)
        {
            return approval.Reject(checkerId, reason ?? "", clock.UtcNow);
        }

        return approval.Approve(checkerId, await GrantsFor(approval, checkerId, cancellationToken), clock.UtcNow, await BaseCurrency(cancellationToken), reason);
    }

    /// <summary>
    /// Whether the checker could approve the approval now (BR-004), by the same rules as approving: scope, the permission
    /// the requester named, not their own, not expired, and authority for the type and amount.
    /// </summary>
    public async Task<bool> CanApprove(Approval approval, Guid checkerId, CancellationToken cancellationToken) =>
        (!approval.IsScoped || execution.CurrentScope.Allows(approval.ProductCode, approval.OrgNodeCode))
        && (approval.RequiredPermission is not { } permission || execution.HasPermission(permission))
        && !await Excluded(approval, checkerId, cancellationToken)
        && approval.CanApprove(checkerId, await GrantsFor(approval, checkerId, cancellationToken), clock.UtcNow, await BaseCurrency(cancellationToken)).IsSuccess;

    private async Task<bool> Excluded(Approval approval, Guid checkerId, CancellationToken cancellationToken)
    {
        foreach (var source in exclusions.Where(e => e.Covers(approval.ApprovalType)))
        {
            if (await source.Excludes(tenant.RequireTenantId(), approval.BusinessRefType, approval.BusinessRefId, checkerId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The user's authority from the delegation table, for their roles today (BR-002).</summary>
    public async Task<IReadOnlyList<AuthorityGrant>> Authority(Guid userId, CancellationToken cancellationToken) =>
        await authority.For(await Roles(userId, cancellationToken), businessDate.Today, cancellationToken);

    /// <summary>
    /// BR-081: all the authority approving uses for the user today. The delegation table's rows, except for approval types
    /// a module decides from its own limits, whose rows come from the module; and the static data approval types the
    /// delegation table does not configure, decided by the static_data.approve permission (as GrantsFor does).
    /// </summary>
    public async Task<IReadOnlyList<AuthorityView>> AllAuthority(Guid userId, CancellationToken cancellationToken)
    {
        var userRoles = await Roles(userId, cancellationToken);
        var rows = (await authority.For(userRoles, businessDate.Today, cancellationToken))
            .Where(g => !sources.Any(s => s.Decides(g.ApprovalType)))
            .Select(g => new AuthorityView(g.ApprovalType, g.RoleCode, g.Limit, g.Currency)).ToList();
        foreach (var source in sources)
        {
            rows.AddRange((await source.Listed(tenant.RequireTenantId(), userRoles, businessDate.Today, cancellationToken))
                .Select(l => new AuthorityView(l.ApprovalType, l.RoleCode, l.Limit, null, AuthorityBases.ModuleLimit, l.AppliesTo)));
        }

        if (execution.HasPermission(StaticDataApprovalPermissions.Approve))
        {
            foreach (var type in ApprovalKinds.ApprovalTypes.Where(t => t.StartsWith(StaticDataApprovals.ApprovalTypePrefix, StringComparison.Ordinal)))
            {
                if (!await authority.IsConfigured(type, businessDate.Today, cancellationToken))
                {
                    rows.Add(new AuthorityView(type, PermissionAuthority, 0m, null, AuthorityBases.Permission));
                }
            }
        }

        return rows;
    }

    /// <summary>The tenant's base currency today, or null when it has none.</summary>
    public async Task<string?> BaseCurrency(CancellationToken cancellationToken)
    {
        if (!_baseCurrencyRead)
        {
            _baseCurrency = await baseCurrency.BaseCurrency(tenant.RequireTenantId(), businessDate.Today, cancellationToken);
            _baseCurrencyRead = true;
        }

        return _baseCurrency;
    }

    // Read once per request: a list of decidable approvals checks many approvals for the same checker.
    private string? _baseCurrency;
    private bool _baseCurrencyRead;
    private (Guid User, IReadOnlyList<string> Roles)? _roles;
    private readonly Dictionary<string, bool> _configured = new(StringComparer.Ordinal);

    private async Task<IReadOnlyList<string>> Roles(Guid userId, CancellationToken cancellationToken)
    {
        if (_roles is not { } cached || cached.User != userId)
        {
            _roles = cached = (userId, await roles.EffectiveRoles(tenant.RequireTenantId(), userId, cancellationToken));
        }

        return cached.Roles;
    }

    private async Task<IReadOnlyList<AuthorityGrant>> GrantsFor(Approval approval, Guid checkerId, CancellationToken cancellationToken)
    {
        var checkerRoles = await Roles(checkerId, cancellationToken);
        // A module that keeps the authority for this approval type in its own static data decides it (Life claims).
        var grants = sources.FirstOrDefault(s => s.Decides(approval.ApprovalType)) is { } source
            ? (await source.For(tenant.RequireTenantId(), approval.BusinessRefType, approval.BusinessRefId, checkerRoles, businessDate.Today, cancellationToken))
                .Select(a => new AuthorityGrant(a.RoleCode, approval.ApprovalType, a.Limit)).ToList()
            : await authority.For(checkerRoles, businessDate.Today, cancellationToken);
        if (approval.ApprovalType.StartsWith(StaticDataApprovals.ApprovalTypePrefix, StringComparison.Ordinal))
        {
            if (!_configured.TryGetValue(approval.ApprovalType, out var configured))
            {
                _configured[approval.ApprovalType] = configured = await authority.IsConfigured(approval.ApprovalType, businessDate.Today, cancellationToken);
            }

            if (!configured)
            {
                grants = [.. grants, new AuthorityGrant(PermissionAuthority, approval.ApprovalType, 0m)];
            }
        }

        return grants;
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

        var result = await decision.Decide(approval, checker.Value, approve: true, command.Reason, cancellationToken);
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

internal sealed class WithdrawApprovalHandler(IApprovalRepository approvals, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<WithdrawApproval, long>
{
    public async Task<Result<long>> Handle(WithdrawApproval command, CancellationToken cancellationToken)
    {
        var requester = Checker.Require(execution);
        var approval = await approvals.Get(command.ApprovalId, cancellationToken);
        if (requester.IsFailure || approval is null)
        {
            return requester.IsFailure ? requester.Error : Error.NotFound(ErrorCodes.NotFound, "The approval was not found.");
        }

        if (approval.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The approval has changed since it was read.");
        }

        audit.Record(new AuditEntry("APPROVAL", "Withdraw", nameof(Approval), approval.Id.ToString(), new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var result = approval.WithdrawByRequester(requester.Value, command.Reason, clock.UtcNow);
        return result.IsFailure ? result.Error : approval.RowVersion + 1;
    }
}

internal sealed class ExpireApprovalHandler(IApprovalRepository approvals, RequesterFollowUp followUp, IClock clock, IAuditTrail audit) : ICommandHandler<ExpireApproval, long>
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
        if (result.IsFailure)
        {
            return result.Error;
        }

        await followUp.Open(approval, Contracts.ApprovalEndings.Expired, cancellationToken);
        return approval.RowVersion + 1;
    }
}

internal sealed class ListApprovalsHandler(IApprovalQueries queries, ApprovalDecision decision, IExecutionContext execution)
    : IQueryHandler<ListApprovals, IReadOnlyList<ApprovalView>>
{
    public async Task<Result<IReadOnlyList<ApprovalView>>> Handle(ListApprovals query, CancellationToken cancellationToken)
    {
        if (!query.Decidable)
        {
            return Result.Success(await queries.List(query.Status, query.BusinessRefType, query.BusinessRefId, query.AfterId, query.Limit, cancellationToken));
        }

        var checker = Checker.Require(execution);
        if (checker.IsFailure)
        {
            return checker.Error;
        }

        // BR-004: the waiting approvals of others, checked one by one by the rules of approving, until the page is full.
        // The cursor is the last approval returned, so one checked and left out is checked again on the next page.
        var page = new List<ApprovalView>(query.Limit);
        var after = query.AfterId;
        var batchSize = Math.Max(query.Limit * 2, 50);
        while (page.Count < query.Limit)
        {
            var batch = await queries.WaitingFromOthers(checker.Value, query.BusinessRefType, query.BusinessRefId, after, batchSize, cancellationToken);
            foreach (var approval in batch)
            {
                if (await decision.CanApprove(approval, checker.Value, cancellationToken))
                {
                    page.Add(ApprovalView.Of(approval));
                    if (page.Count == query.Limit)
                    {
                        break;
                    }
                }
            }

            if (batch.Count < batchSize)
            {
                break;
            }

            after = batch[^1].Id;
        }

        return page;
    }
}

internal sealed class MyAuthorityHandler(ApprovalDecision decision, IExecutionContext execution) : IQueryHandler<MyAuthority, IReadOnlyList<AuthorityView>>
{
    public async Task<Result<IReadOnlyList<AuthorityView>>> Handle(MyAuthority query, CancellationToken cancellationToken)
    {
        var user = Checker.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var baseCurrency = await decision.BaseCurrency(cancellationToken);
        return (await decision.AllAuthority(user.Value, cancellationToken))
            .Select(a => a with { Currency = a.Currency ?? baseCurrency })
            .OrderBy(a => a.ApprovalType, StringComparer.Ordinal).ThenBy(a => a.RoleCode, StringComparer.Ordinal).ThenBy(a => a.AppliesTo, StringComparer.Ordinal)
            .ToList();
    }
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
public sealed class RequestApprovalPolicy(IApprovalRepository approvals, IClock clock, IOptions<ApprovalOptions> options, IEnumerable<IApprovalScopeSource> scopes,
    ITenantContext tenant, IApprovalExpiry expiry, IBusinessDate businessDate) : IIntegrationEventHandler<ApprovalRequested>
{
    public async Task Handle(ApprovalRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // BR-005: every approval and reference type is one a module declared, so the contract's enums and screens hold. An
        // undeclared one is a defect in the requesting module: the delivery fails and is retried, then dead-lettered.
        if (!ApprovalKinds.ApprovalTypes.Contains(requested.ApprovalType) || ApprovalKinds.ScreenFor(requested.BusinessRefType) is null)
        {
            throw new InvalidOperationException($"Approval type {requested.ApprovalType} for {requested.BusinessRefType} is not declared (ApprovalKinds, BR-005).");
        }

        if (await approvals.FindRequested(requested.BusinessRefType, requested.BusinessRefId, cancellationToken) is not null)
        {
            return;
        }

        var now = clock.UtcNow;
        var approval = Approval.RequestApproval(Guid.CreateVersion7(), requested.BusinessRefType, requested.BusinessRefId, requested.ApprovalType, requested.MakerId,
            requested.Amount, requested.Currency, requested.Reason, now,
            now + (await expiry.For(requested.ApprovalType, businessDate.Today, cancellationToken) ?? options.Value.DefaultExpiry), requested.SubjectHash,
            requested.RequiredPermission);

        // F103 (addenda/access-scopes.md §4): an approval about a scoped record carries that record's product and node.
        if (scopes.FirstOrDefault(s => s.Covers(requested.BusinessRefType)) is { } source)
        {
            var scope = await source.ScopeOf(tenant.RequireTenantId(), requested.BusinessRefType, requested.BusinessRefId, cancellationToken);
            approval.ScopeTo(scope?.ProductCode, scope?.OrgNodeCode);
        }

        approvals.Add(approval);
    }
}

/// <summary>
/// Workflow's policy for workflow.ApprovalWithdrawn: the business reference's waiting approval is withdrawn for the
/// requester, who is told (a follow-up task); the owning module receives the ending, as for a rejection.
/// </summary>
public sealed class WithdrawApprovalPolicy(IApprovalRepository approvals, RequesterFollowUp followUp, IClock clock) : IIntegrationEventHandler<ApprovalWithdrawn>
{
    public async Task Handle(ApprovalWithdrawn withdrawn, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await approvals.FindRequested(withdrawn.BusinessRefType, withdrawn.BusinessRefId, cancellationToken) is { } approval
            && approval.Withdraw(withdrawn.Reason, clock.UtcNow).IsSuccess)
        {
            await followUp.Open(approval, Contracts.ApprovalEndings.Withdrawn, cancellationToken);
        }
    }
}

/// <summary>
/// Workflow's policy for integration.DeliveryParked (D-5, extended): a granted approval whose record would not take it
/// (its delivery parked as APPROVAL_NOT_APPLIED) is marked not applied, with the module that refused it, and the requester
/// gets a follow-up task. The parked delivery itself is alerted, escalated and retried under ADR-022.
/// </summary>
public sealed class ApprovalNotAppliedPolicy(IApprovalRepository approvals, RequesterFollowUp followUp, IClock clock, IAuditTrail audit)
    : IIntegrationEventHandler<Integration.Contracts.DeliveryParked>
{
    public const string FollowUpReason = "NOT_APPLIED";

    public async Task Handle(Integration.Contracts.DeliveryParked parked, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ApprovalOf(parked.Code, parked.EventType, parked.Item) is not { } approvalId || await approvals.Get(approvalId, cancellationToken) is not { } approval)
        {
            return;
        }

        if (approval.MarkNotApplied(parked.Consumer, clock.UtcNow).IsSuccess)
        {
            audit.Record(new AuditEntry("APPROVAL", "MarkNotApplied", nameof(Approval), approval.Id.ToString(),
                new Dictionary<string, string?> { ["consumer"] = parked.Consumer, ["parkedDelivery"] = parked.ParkedDeliveryId.ToString() }));
            await followUp.Open(approval, FollowUpReason, cancellationToken);
        }
    }

    /// <summary>The approval a parked or resolved delivery is about: a granted approval parked as APPROVAL_NOT_APPLIED, item "TYPE:id".</summary>
    internal static Guid? ApprovalOf(string code, string eventType, string? item) =>
        code == PrerequisiteCodes.ApprovalNotApplied && eventType == IntegrationEventTypes.Describe(typeof(Contracts.ApprovalGranted)).Type
        && item?.LastIndexOf(':') is int colon and >= 0 && Guid.TryParse(item[(colon + 1)..], out var id)
            ? id
            : null;
}

/// <summary>
/// Workflow's policy for integration.ParkedDeliveryResolved: a retry of a not-applied approval's delivery succeeded, so the
/// record took it after all and the approval is approved again (D-5).
/// </summary>
public sealed class ApprovalAppliedPolicy(IApprovalRepository approvals, IAuditTrail audit) : IIntegrationEventHandler<Integration.Contracts.ParkedDeliveryResolved>
{
    public async Task Handle(Integration.Contracts.ParkedDeliveryResolved resolved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ApprovalNotAppliedPolicy.ApprovalOf(resolved.Code, resolved.EventType, resolved.Item) is { } approvalId
            && await approvals.Get(approvalId, cancellationToken) is { } approval && approval.MarkApplied().IsSuccess)
        {
            audit.Record(new AuditEntry("APPROVAL", "MarkApplied", nameof(Approval), approval.Id.ToString(),
                new Dictionary<string, string?> { ["consumer"] = resolved.Consumer, ["parkedDelivery"] = resolved.ParkedDeliveryId.ToString() }));
        }
    }
}
