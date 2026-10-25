/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.Distribution.Contracts;
using Insys.General.Contracts;
using Insys.Life.Contracts;

namespace Insys.Analytics.Application;

/// <summary>
/// One change to a projection, as kept in the event log (minimal data only, INT-007). Written premium: by day,
/// product, channel and branch. Claims incurred: by month, product and cause. A claim's allocation adds to the claim
/// count; its reserve changes are what it has incurred (payments draw the reserve down, so incurred is unchanged).
/// </summary>
public sealed record ProjectionEntry(string Kind, DateOnly Day, string ProductCode, string Dimension, string Branch, decimal Amount, int Count, string Currency,
    string? ClaimNumber = null)
{
    public const string WrittenPremium = "WRITTEN_PREMIUM";
    public const string ClaimsIncurred = "CLAIMS_INCURRED";
    public const string ClaimRegistered = "CLAIM_REGISTERED";

    /// <summary>The month key of the claims incurred projection, yyyy-MM.</summary>
    public string MonthKey => Day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the entry's day is the business date the event occurred on (ADR-025): written premium and registered claims.
    /// Claims incurred carry their movement's own date.
    /// </summary>
    public bool DatedByOccurrence => Kind is WrittenPremium or ClaimRegistered;
}

public sealed record WrittenPremiumRow(DateOnly DateKey, string ProductCode, string ChannelCode, string BranchCode, int PolicyCount, decimal WrittenPremium, string Currency);

public sealed record ClaimsIncurredRow(string MonthKey, string ProductCode, string CauseCode, int ClaimCount, decimal Incurred, string Currency, string BranchCode = "ALL");

public interface IProjectionStore
{
    /// <summary>Keeps the entry in the event log; false when the event was logged before (duplicate delivery, INT-005).</summary>
    Task<bool> Log(Guid eventId, string eventType, DateTimeOffset occurredAt, ProjectionEntry entry, CancellationToken cancellationToken);

    Task Apply(ProjectionEntry entry, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The product and branch a claim number was registered against (its CLAIM_REGISTERED entry).</summary>
    Task<(string ProductCode, string Branch)?> ClaimDimensions(string claimNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the tenant's projections and replays its event log in order; returns the entries replayed. An entry dated by its event's occurrence is dated again from the logged
    /// occurred_at in the tenant's time zone (<paramref name="businessDateOf"/>), so facts logged by the UTC day are corrected (ADR-025).
    /// </summary>
    Task<int> Rebuild(DateTimeOffset now, Func<DateTimeOffset, DateOnly> businessDateOf, CancellationToken cancellationToken);

    Task<IReadOnlyList<WrittenPremiumRow>> WrittenPremium(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimsIncurredRow>> ClaimsIncurred(string fromMonth, string toMonth, CancellationToken cancellationToken);

    /// <summary>The headline figures over the dates (GAP-046), within the caller's F103 scope.</summary>
    Task<DashboardSummary> Summary(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public sealed record WrittenPremiumQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<WrittenPremiumRow>>;

public sealed record ClaimsIncurredQuery(string FromMonth, string ToMonth) : IQuery<IReadOnlyList<ClaimsIncurredRow>>;

/// <summary>
/// The Analytics dashboard figures (GAP-046, addenda/dashboard-figures.md): WRITTEN_PREMIUM and POLICIES_WRITTEN over the
/// days from and to (both included), CLAIMS_INCURRED and CLAIMS_REPORTED over their months. Health is not included.
/// </summary>
public sealed record AnalyticsSummaryQuery(DateOnly From, DateOnly To) : IQuery<DashboardSummary>;

public static class AnalyticsFigures
{
    public const string WrittenPremium = "WRITTEN_PREMIUM";
    public const string PoliciesWritten = "POLICIES_WRITTEN";
    public const string ClaimsIncurred = "CLAIMS_INCURRED";
    public const string ClaimsReported = "CLAIMS_REPORTED";

    /// <summary>Health is not projected yet (decision 3: its figures come with Waves 5a and 5b).</summary>
    public const string Health = "HEALTH";
}

/// <summary>Rebuilds the tenant's projections from the events Analytics consumed (domain-model.json: projections are rebuilt from events).</summary>
public sealed record RebuildProjections : ICommand<int>;

internal sealed class ProjectionQueries(IProjectionStore store) :
    IQueryHandler<WrittenPremiumQuery, IReadOnlyList<WrittenPremiumRow>>,
    IQueryHandler<ClaimsIncurredQuery, IReadOnlyList<ClaimsIncurredRow>>,
    IQueryHandler<AnalyticsSummaryQuery, DashboardSummary>
{
    public async Task<Result<DashboardSummary>> Handle(AnalyticsSummaryQuery query, CancellationToken cancellationToken) =>
        query.To < query.From
            ? Error.Validation(ErrorCodes.ValidationFailed, "to is on or after from.")
            : await store.Summary(query.From, query.To, cancellationToken);

    public async Task<Result<IReadOnlyList<WrittenPremiumRow>>> Handle(WrittenPremiumQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.WrittenPremium(query.From, query.To, cancellationToken));

    public async Task<Result<IReadOnlyList<ClaimsIncurredRow>>> Handle(ClaimsIncurredQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ClaimsIncurred(query.FromMonth, query.ToMonth, cancellationToken));
}

internal sealed class RebuildProjectionsHandler(IProjectionStore store, IBusinessDate businessDate, IClock clock) : ICommandHandler<RebuildProjections, int>
{
    public async Task<Result<int>> Handle(RebuildProjections command, CancellationToken cancellationToken) =>
        await store.Rebuild(clock.UtcNow, businessDate.DateOf, cancellationToken);
}

/// <summary>
/// Analytics' projections (domain-model.json: analytics consumes all published domain and financial events). Each
/// event becomes a minimal entry in the event log and is applied to its projection, once (the log's key is the event).
/// Dimensions (OQ-065): the channel is INTERMEDIARY when the policy has an intermediary, otherwise DIRECT; the branch
/// is the policy's organisation node (ALL when it has none, GAP-046 decision 2); the cause is UNKNOWN until the claim
/// events carry one. Written premium is the annual premium at issue, plus
/// endorsement deltas, less cancellation refunds. TODO(spec): Health is not projected (no product on its events); the
/// projections assume one currency per tenant (their keys have none).
/// </summary>
public sealed class ProjectionPolicies(IProjectionStore store, IEnumerable<IPolicyDistribution> policies, IPolicyArrears billing, ITenantContext tenant, IBusinessDate businessDate,
    IClock clock) :
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<GeneralPolicyCancelled>,
    IIntegrationEventHandler<LifePolicyIssued>,
    IIntegrationEventHandler<ClaimNumberAllocated>,
    IIntegrationEventHandler<ClaimReserveChanged>
{
    public const string AllBranches = "ALL";
    public const string UnknownCause = "UNKNOWN";

    public async Task Handle(GeneralPolicyIssued e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var (channel, branch) = await Dimensions(GeneralRatingSubjects.SourceDomain, e.PolicyId, cancellationToken);
        await Project(envelope, new ProjectionEntry(ProjectionEntry.WrittenPremium, Day(envelope), e.ProductCode, channel, branch, e.AnnualPremium, 1, e.Currency),
            cancellationToken);
    }

    public async Task Handle(GeneralPolicyEndorsed e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var (channel, branch) = await Dimensions(GeneralRatingSubjects.SourceDomain, e.PolicyId, cancellationToken);
        await Project(envelope, new ProjectionEntry(ProjectionEntry.WrittenPremium, Day(envelope), e.ProductCode, channel, branch, e.PremiumDelta, 0, e.Currency),
            cancellationToken);
    }

    public async Task Handle(GeneralPolicyCancelled e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var (channel, branch) = await Dimensions(GeneralRatingSubjects.SourceDomain, e.PolicyId, cancellationToken);
        await Project(envelope, new ProjectionEntry(ProjectionEntry.WrittenPremium, Day(envelope), e.ProductCode, channel, branch, -e.RefundAmount, 0, e.Currency),
            cancellationToken);
    }

    /// <summary>A Life policy's written premium is its instalment × the frequency's periods per year (Billing).</summary>
    public async Task Handle(LifePolicyIssued e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var periods = await billing.PeriodsPerYear(tenant.RequireTenantId(), e.BillingFrequency, e.InceptionDate, cancellationToken)
                      ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, e.BillingFrequency);
        var (channel, branch) = await Dimensions(LifeSubjects.SourceDomain, e.PolicyId, cancellationToken);
        await Project(envelope, new ProjectionEntry(ProjectionEntry.WrittenPremium, Day(envelope), e.ProductCode, channel, branch, e.Premium * periods, 1, e.Currency),
            cancellationToken);
    }

    /// <summary>A registered claim counts in its month, against its policy's product.</summary>
    public async Task Handle(ClaimNumberAllocated e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (policies.FirstOrDefault(p => p.SourceDomain == e.Domain) is not { } domain
            || await domain.For(tenant.RequireTenantId(), e.PolicyRef, cancellationToken) is not { } policy)
        {
            return;
        }

        await Project(envelope, new ProjectionEntry(ProjectionEntry.ClaimRegistered, Day(envelope), policy.ProductCode, UnknownCause,
            policy.OrgNodeCode ?? AllBranches, 0m, 1, "", e.ClaimNumber), cancellationToken);
    }

    public async Task Handle(ClaimReserveChanged e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await store.ClaimDimensions(e.ClaimNumber, cancellationToken) is not { } claim)
        {
            return;
        }

        await Project(envelope, new ProjectionEntry(ProjectionEntry.ClaimsIncurred, e.MovementDate, claim.ProductCode, UnknownCause, claim.Branch, e.Change, 0, e.Currency,
            e.ClaimNumber), cancellationToken);
    }

    private async Task Project(EventEnvelope envelope, ProjectionEntry entry, CancellationToken cancellationToken)
    {
        if (await store.Log(envelope.EventId, envelope.EventType, envelope.OccurredAt, entry, cancellationToken))
        {
            await store.Apply(entry, clock.UtcNow, cancellationToken);
        }
    }

    private async Task<(string Channel, string Branch)> Dimensions(string sourceDomain, Guid policyRef, CancellationToken cancellationToken)
    {
        var policy = policies.FirstOrDefault(p => p.SourceDomain == sourceDomain) is { } domain
            ? await domain.For(tenant.RequireTenantId(), policyRef, cancellationToken)
            : null;
        return (policy is { IntermediaryCode: { Length: > 0 } } ? "INTERMEDIARY" : "DIRECT", policy?.OrgNodeCode ?? AllBranches);
    }

    // ADR-025: a figure counts on the tenant's business date when the event occurred, not the UTC date (an event at 23:30 UTC
    // is the next day in Johannesburg).
    private DateOnly Day(EventEnvelope envelope) => businessDate.DateOf(envelope.OccurredAt);
}
