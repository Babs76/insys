/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using System.Globalization;
using FluentValidation;
using Insys.Audit.Contracts;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;
using Microsoft.Extensions.Options;

namespace Insys.Party.Application;

/// <summary>
/// Retention of the party search access log (OQ-073, decided 23 October 2026): the SECURITY class, per tenant, in
/// audit.cfg_audit_retention_policies. Basis: aligned with the FICA s23 and LTIA PPR rule 16.4(b) five-year minimums;
/// POPIA s14 minimisation; compliance to confirm before go-live.
/// </summary>
public sealed class SearchLogRetentionOptions
{
    /// <summary>Full entries are kept this many months; after that the plain-text name filter is removed.</summary>
    public int MinimiseAfterMonths { get; set; } = 12;

    /// <summary>Used when the tenant has no SECURITY retention policy in force: the decided five years.</summary>
    public int DefaultRetentionYears { get; set; } = 5;

    public bool JobEnabled { get; set; } = true;

    public TimeSpan JobInterval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Command ApplySearchLogRetention: minimise and delete the caller's tenant's entries that are due; every run is audited.</summary>
public sealed record ApplySearchLogRetention : ICommand<SearchLogRetentionRun>;

public sealed record SearchLogRetentionRun(Guid RunId, int Minimised, int Deleted, string RetentionPeriod, DateTimeOffset MinimisedBefore, DateTimeOffset DeletedBefore);

public sealed record PlaceSearchLogHold(string? SearchedBy, string Reason) : ICommand<Guid>;

/// <summary>Command RequestSearchLogHoldRelease: asks for a second person's approval to release the hold (OQ-111); it stays in force until approved.</summary>
public sealed record RequestSearchLogHoldRelease(Guid HoldId, string Reason) : ICommand<SearchLogHoldView>;

/// <summary>A legal hold: in force until releasedAt is set; a release waits for approval while releaseRequestedAt is set (OQ-111).</summary>
/// <param name="ReleasedBy">The checker who approved the release.</param>
/// <param name="ReleaseRequestedBy">Who asked for the hold to be released, while that waits for a second person's approval (OQ-111); the hold is in force meanwhile.</param>
/// <param name="LatestRejection">The hold's latest release approval that ended without being granted (OQ-113), or null.</param>
public sealed record SearchLogHoldView(Guid Id, string? SearchedBy, string Reason, string PlacedBy, DateTimeOffset PlacedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt,
    Guid? ReleaseRequestedBy, DateTimeOffset? ReleaseRequestedAt, string? ReleaseReason, Workflow.Contracts.LatestRejection? LatestRejection = null)
{
    public static SearchLogHoldView From(SearchLogHold h, Workflow.Contracts.LatestRejection? latestRejection = null) =>
        new(h.Id, h.SearchedBy, h.Reason, h.PlacedBy, h.PlacedAt, h.ReleasedBy, h.ReleasedAt, h.ReleaseRequestedBy, h.ReleaseRequestedAt, h.ReleaseReason, latestRejection);
}

public sealed class RequestSearchLogHoldReleaseValidator : AbstractValidator<RequestSearchLogHoldRelease>
{
    public RequestSearchLogHoldReleaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
}

public sealed record ListSearchLogHolds : IQuery<IReadOnlyList<SearchLogHoldView>>;

public sealed class PlaceSearchLogHoldValidator : AbstractValidator<PlaceSearchLogHold>
{
    public PlaceSearchLogHoldValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
        RuleFor(c => c.SearchedBy).MaximumLength(80);
    }
}

public interface ISearchLogRetention
{
    Task<int> Minimise(DateTimeOffset before, CancellationToken cancellationToken);

    Task<int> Purge(DateTimeOffset before, CancellationToken cancellationToken);

    Task<SearchLogHold?> Hold(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchLogHold>> Holds(CancellationToken cancellationToken);

    void Add(SearchLogHold hold);
}

internal sealed class SearchLogRetentionHandlers(ISearchLogRetention log, IAuditRetention retention, IOptions<SearchLogRetentionOptions> options, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit, Workflow.Contracts.IApprovalHistory history) :
    ICommandHandler<ApplySearchLogRetention, SearchLogRetentionRun>,
    ICommandHandler<PlaceSearchLogHold, Guid>,
    ICommandHandler<RequestSearchLogHoldRelease, SearchLogHoldView>,
    IQueryHandler<ListSearchLogHolds, IReadOnlyList<SearchLogHoldView>>
{
    public async Task<Result<SearchLogRetentionRun>> Handle(ApplySearchLogRetention command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var period = await retention.Period(RetentionClasses.Security, businessDate.Today, cancellationToken)
            ?? new RetentionPeriod(options.Value.DefaultRetentionYears, 0, 0);
        var minimiseBefore = now.AddMonths(-options.Value.MinimiseAfterMonths);
        var deleteBefore = period.Before(now);

        var minimised = await log.Minimise(minimiseBefore, cancellationToken);
        var deleted = await log.Purge(deleteBefore, cancellationToken);
        var run = new SearchLogRetentionRun(Guid.CreateVersion7(), minimised, deleted, period.ToString(), minimiseBefore, deleteBefore);
        audit.Record(new AuditEntry("PARTY", "ApplySearchLogRetention", nameof(PartySearchLog), run.RunId.ToString(), new Dictionary<string, string?>
        {
            ["minimised"] = minimised.ToString(CultureInfo.InvariantCulture),
            ["deleted"] = deleted.ToString(CultureInfo.InvariantCulture),
            ["retention"] = period.ToString(),
            ["minimiseAfterMonths"] = options.Value.MinimiseAfterMonths.ToString(CultureInfo.InvariantCulture),
        }));
        return run;
    }

    public Task<Result<Guid>> Handle(PlaceSearchLogHold command, CancellationToken cancellationToken)
    {
        var hold = SearchLogHold.Place(command.SearchedBy, command.Reason, execution.ActorId, clock.UtcNow);
        log.Add(hold);
        audit.Record(new AuditEntry("PARTY", "PlaceSearchLogHold", nameof(SearchLogHold), hold.Id.ToString(),
            new Dictionary<string, string?> { ["searchedBy"] = command.SearchedBy ?? "(whole log)" }));
        return Task.FromResult(Result<Guid>.Success(hold.Id));
    }

    public async Task<Result<SearchLogHoldView>> Handle(RequestSearchLogHoldRelease command, CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out var user))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
        }

        if (await log.Hold(command.HoldId, cancellationToken) is not { } hold)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The hold was not found.");
        }

        var requested = hold.RequestRelease(user, command.Reason, clock.UtcNow);
        if (requested.IsFailure)
        {
            return requested.Error;
        }

        audit.Record(new AuditEntry("PARTY", "RequestSearchLogHoldRelease", nameof(SearchLogHold), hold.Id.ToString()));
        return SearchLogHoldView.From(hold);
    }

    public async Task<Result<IReadOnlyList<SearchLogHoldView>>> Handle(ListSearchLogHolds query, CancellationToken cancellationToken)
    {
        var views = new List<SearchLogHoldView>();
        foreach (var hold in await log.Holds(cancellationToken))
        {
            views.Add(SearchLogHoldView.From(hold, await history.Latest(Contracts.PartyApprovals.SearchLogHoldBusinessRefType, hold.Id, cancellationToken)));
        }

        return views;
    }
}

/// <summary>
/// Workflow's decision on releasing a search log hold (LEGAL_HOLD_RELEASE, OQ-111). Approved: the hold is released, and
/// retention may then delete the entries it kept. Rejected, expired or withdrawn: the request is cleared and the hold
/// stays in force. A redelivered grant already applied changes nothing; one the hold will not take (no release waits any
/// more, or the checker is the requester) parks the delivery (D-5, ADR-022): a grant as APPROVAL_NOT_APPLIED, an ending as
/// APPROVAL_ENDING_NOT_APPLIED.
/// </summary>
public sealed class SearchLogHoldReleasePolicy(ISearchLogRetention log, IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<Workflow.Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Workflow.Contracts.ApprovalRejected>
{
    public Task Handle(Workflow.Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    public Task Handle(Workflow.Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid holdId, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != Contracts.PartyApprovals.SearchLogHoldBusinessRefType || await log.Hold(holdId, cancellationToken) is not { } hold)
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
                $"{Contracts.PartyApprovals.LegalHoldReleaseType}:{approvalId}");
        }

        audit.Record(new AuditEntry("PARTY", approved ? "ReleaseSearchLogHold" : "KeepSearchLogHold", nameof(SearchLogHold), holdId.ToString(),
            new Dictionary<string, string?> { ["approval"] = approvalId.ToString() }));
    }
}
