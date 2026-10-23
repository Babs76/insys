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

public sealed record ReleaseSearchLogHold(Guid HoldId) : ICommand<long>;

public sealed record SearchLogHoldView(Guid Id, string? SearchedBy, string Reason, string PlacedBy, DateTimeOffset PlacedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt);

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
    IClock clock, IAuditTrail audit) :
    ICommandHandler<ApplySearchLogRetention, SearchLogRetentionRun>,
    ICommandHandler<PlaceSearchLogHold, Guid>,
    ICommandHandler<ReleaseSearchLogHold, long>,
    IQueryHandler<ListSearchLogHolds, IReadOnlyList<SearchLogHoldView>>
{
    public async Task<Result<SearchLogRetentionRun>> Handle(ApplySearchLogRetention command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var period = await retention.Period(RetentionClasses.Security, clock.UtcToday, cancellationToken)
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

    public async Task<Result<long>> Handle(ReleaseSearchLogHold command, CancellationToken cancellationToken)
    {
        if (await log.Hold(command.HoldId, cancellationToken) is not { } hold)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The hold was not found.");
        }

        audit.Record(new AuditEntry("PARTY", "ReleaseSearchLogHold", nameof(SearchLogHold), hold.Id.ToString()));
        var released = hold.Release(execution.ActorId, clock.UtcNow);
        return released.IsFailure ? released.Error : hold.RowVersion + 1;
    }

    public async Task<Result<IReadOnlyList<SearchLogHoldView>>> Handle(ListSearchLogHolds query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<SearchLogHoldView>>((await log.Holds(cancellationToken))
            .Select(h => new SearchLogHoldView(h.Id, h.SearchedBy, h.Reason, h.PlacedBy, h.PlacedAt, h.ReleasedBy, h.ReleasedAt)).ToList());
}
