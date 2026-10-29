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

    /// <summary>F027: a renewal or retention measure (Dimension) by month, product and branch.</summary>
    public const string Renewal = "RENEWAL";

    /// <summary>The month key of the claims incurred projection, yyyy-MM.</summary>
    public string MonthKey => Day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the entry's day is the business date the event occurred on (ADR-025): written premium and registered claims.
    /// Claims incurred carry their movement's own date.
    /// </summary>
    public bool DatedByOccurrence => Kind is WrittenPremium or ClaimRegistered || (Kind == Renewal && RenewalMeasures.ByOccurrence(Dimension));
}

public sealed record WrittenPremiumRow(DateOnly DateKey, string ProductCode, string ChannelCode, string BranchCode, int PolicyCount, decimal WrittenPremium, string Currency);

public sealed record ClaimsIncurredRow([property: YearMonth] string MonthKey, string ProductCode, string CauseCode, int ClaimCount, decimal Incurred, string Currency, string BranchCode = "ALL");

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

    /// <summary>
    /// Records a release task for the tenant (analytics.release_task) in the current transaction; false when it is already
    /// recorded, so the caller does not run it again.
    /// </summary>
    Task<bool> BeginReleaseTask(string taskCode, DateTimeOffset now, CancellationToken cancellationToken);

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

/// <summary>
/// The renewal and retention measures (F027) kept in analytics.fact_renewals. Renewal measures count in their renewal's
/// period end month; retention ones (cases closed, offers) in the month they happened.
/// </summary>
public static class RenewalMeasures
{
    public const string Due = "DUE";
    public const string Invited = "INVITED";
    public const string Accepted = "ACCEPTED";
    public const string Renewed = "RENEWED";
    public const string Declined = "DECLINED";
    public const string NotRenewed = "NOT_RENEWED";
    public const string Lapsed = "LAPSED";
    public const string RetentionClosed = "RETENTION_CLOSED";
    public const string RetentionRetained = "RETENTION_RETAINED";
    public const string OfferMade = "OFFER_MADE:";
    public const string OfferAccepted = "OFFER_ACCEPTED:";

    public static bool ByOccurrence(string measure) =>
        measure is RetentionClosed or RetentionRetained || measure.StartsWith(OfferMade, StringComparison.Ordinal) || measure.StartsWith(OfferAccepted, StringComparison.Ordinal);
}

public static class AnalyticsFigures
{
    public const string WrittenPremium = "WRITTEN_PREMIUM";
    public const string PoliciesWritten = "POLICIES_WRITTEN";
    public const string ClaimsIncurred = "CLAIMS_INCURRED";
    public const string ClaimsReported = "CLAIMS_REPORTED";

    /// <summary>F027 (Wave 4 §4.7): renewals by the month of their renewal date, the rates, the premium change and retention offers.</summary>
    public const string RenewalsDue = "RENEWALS_DUE";
    public const string RenewalsInvited = "RENEWALS_INVITED";
    public const string RenewalsAccepted = "RENEWALS_ACCEPTED";
    public const string RenewalsRenewed = "RENEWALS_RENEWED";
    public const string RenewalsDeclined = "RENEWALS_DECLINED_BY_CUSTOMER";
    public const string RenewalsNotRenewed = "RENEWALS_NOT_RENEWED_BY_INSURER";
    public const string RenewalsLapsed = "RENEWALS_LAPSED";
    public const string RenewalRate = "RENEWAL_RATE";
    public const string RetentionRate = "RETENTION_RATE";
    public const string AveragePremiumChange = "AVERAGE_RENEWAL_PREMIUM_CHANGE";
    public const string RetentionOffersMade = "RETENTION_OFFERS_MADE";
    public const string RetentionOffersAccepted = "RETENTION_OFFERS_ACCEPTED";

    public static readonly string[] Renewals =
    [
        RenewalsDue, RenewalsInvited, RenewalsAccepted, RenewalsRenewed, RenewalsDeclined, RenewalsNotRenewed, RenewalsLapsed, RenewalRate, RetentionRate,
        AveragePremiumChange, RetentionOffersMade, RetentionOffersAccepted,
    ];

    /// <summary>Health is not projected yet (decision 3: its figures come with Waves 5a and 5b).</summary>
    public const string Health = "HEALTH";
}

/// <summary>Rebuilds the tenant's projections from the events Analytics consumed (domain-model.json: projections are rebuilt from events).</summary>
public sealed record RebuildProjections : ICommand<int>;

/// <summary>
/// Release task (Wave 2 batch 1): the tenant's projections rebuilt by business date once, recorded so it never runs again.
/// Returns the entries replayed, or null when it had already run.
/// </summary>
public sealed record RunReleaseRebuild : ICommand<int?>
{
    public const string TaskCode = "ANALYTICS_REBUILD_BY_BUSINESS_DATE";
}

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

internal sealed class RunReleaseRebuildHandler(IProjectionStore store, IBusinessDate businessDate, IClock clock) : ICommandHandler<RunReleaseRebuild, int?>
{
    public async Task<Result<int?>> Handle(RunReleaseRebuild command, CancellationToken cancellationToken) =>
        await store.BeginReleaseTask(RunReleaseRebuild.TaskCode, clock.UtcNow, cancellationToken)
            ? await store.Rebuild(clock.UtcNow, businessDate.DateOf, cancellationToken)
            : (int?)null;
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
    IIntegrationEventHandler<ClaimReserveChanged>,
    IIntegrationEventHandler<GeneralRenewalOpened>,
    IIntegrationEventHandler<GeneralRenewalInvited>,
    IIntegrationEventHandler<GeneralRenewalAccepted>,
    IIntegrationEventHandler<GeneralRenewalDeclined>,
    IIntegrationEventHandler<GeneralRenewalLapsed>,
    IIntegrationEventHandler<GeneralRenewalRenewed>,
    IIntegrationEventHandler<GeneralRenewalNotRenewed>,
    IIntegrationEventHandler<GeneralRetentionCaseClosed>,
    IIntegrationEventHandler<GeneralRetentionOfferMade>,
    IIntegrationEventHandler<GeneralRetentionOfferAccepted>
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

    // F027: renewal measures by their renewal's period end month; retention measures by the day they happened.
    public Task Handle(GeneralRenewalOpened e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Due, cancellationToken);

    public Task Handle(GeneralRenewalInvited e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Invited, cancellationToken);

    public Task Handle(GeneralRenewalAccepted e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Accepted, cancellationToken);

    public Task Handle(GeneralRenewalDeclined e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Declined, cancellationToken);

    public Task Handle(GeneralRenewalLapsed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Lapsed, cancellationToken);

    public Task Handle(GeneralRenewalNotRenewed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.NotRenewed, cancellationToken);

    /// <summary>A renewal renewed: counted, with its premium change (renewed less current) for the average.</summary>
    public Task Handle(GeneralRenewalRenewed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, e.PeriodEnd, RenewalMeasures.Renewed, cancellationToken, e.RenewedPremium - e.CurrentPremium, e.Currency);

    public async Task Handle(GeneralRetentionCaseClosed e, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await Renewal(envelope, e.PolicyId, e.ProductCode, Day(envelope), RenewalMeasures.RetentionClosed, cancellationToken);
        if (e.Outcome == "RETAINED")
        {
            await Renewal(envelope, e.PolicyId, e.ProductCode, Day(envelope), RenewalMeasures.RetentionRetained, cancellationToken, eventSuffix: "retained");
        }
    }

    public Task Handle(GeneralRetentionOfferMade e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, Day(envelope), RenewalMeasures.OfferMade + e.Kind, cancellationToken);

    public Task Handle(GeneralRetentionOfferAccepted e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Renewal(envelope, e.PolicyId, e.ProductCode, Day(envelope), RenewalMeasures.OfferAccepted + e.Kind, cancellationToken);

    private async Task Renewal(EventEnvelope envelope, Guid policyId, string? productCode, DateOnly? day, string measure, CancellationToken cancellationToken,
        decimal amount = 0m, string currency = "", string? eventSuffix = null)
    {
        if (productCode is null || day is not { } month)
        {
            return; // an event from before 4.3b carries no product or period: it is not projected
        }

        var (_, branch) = await Dimensions(GeneralRatingSubjects.SourceDomain, policyId, cancellationToken);
        var entry = new ProjectionEntry(ProjectionEntry.Renewal, month, productCode, measure, branch, amount, 1, currency);
        // One event can give two entries (a retained case is also closed): the second is logged under its own key.
        var logged = eventSuffix is null ? envelope : envelope with { EventId = DeterministicId(envelope.EventId, eventSuffix) };
        await Project(logged, entry, cancellationToken);
    }

    private static Guid DeterministicId(Guid eventId, string suffix)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{eventId:N}:{suffix}"));
        return new Guid(bytes.AsSpan(0, 16));
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
