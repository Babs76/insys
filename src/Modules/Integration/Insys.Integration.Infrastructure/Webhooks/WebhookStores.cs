using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Application.Webhooks;
using Insys.Integration.Domain.Webhooks;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Integration.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using NodaTime.Text;

namespace Insys.Integration.Infrastructure.Webhooks;

internal sealed class WebhookRepository(IntegrationDbContext db) : IWebhookRepository
{
    public Task<WebhookDelivery?> Get(Guid id, CancellationToken cancellationToken) => db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<bool> Exists(string subscriptionId, Guid eventId, CancellationToken cancellationToken) =>
        db.WebhookDeliveries.AnyAsync(d => d.SubscriptionId == subscriptionId && d.EventId == eventId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> Due(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.WebhookDeliveries.AsNoTracking().Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).ThenBy(d => d.Id).Select(d => d.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WebhookDelivery>> List(WebhookDeliveryStatus? status, string? subscriptionId, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.WebhookDeliveries.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(d => d.Status == s);
        }

        if (subscriptionId is not null)
        {
            query = query.Where(d => d.SubscriptionId == subscriptionId);
        }

        if (afterId is { } after)
        {
            query = query.Where(d => d.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(d => d.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public void Add(WebhookDelivery delivery) => db.WebhookDeliveries.Add(delivery);
}

internal sealed class WebhookCodes(IntegrationDbContext db) : IWebhookCodes
{
    public async Task<IReadOnlyList<WebhookSubscription>> SubscriptionsFor(string eventType, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<WebhookSubscriptions>(day).Where(s => s.EventTypes.Contains(eventType)).OrderBy(s => s.SubscriptionId).ToListAsync(cancellationToken))
        .Select(View).ToList();

    public async Task<WebhookSubscription?> Subscription(string subscriptionId, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<WebhookSubscriptions>(day).Where(s => s.SubscriptionId == subscriptionId).OrderByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken)
            is { } s ? View(s) : null;

    public async Task<RetryPolicy?> RetryPolicy(string policyCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RetryAndDeadLetterPolicies>(day).Where(p => p.PolicyCode == policyCode).OrderByDescending(p => p.Version).FirstOrDefaultAsync(cancellationToken)
            is { } p
            ? new RetryPolicy(p.PolicyCode, p.MaxAttempts, p.Backoff, Time(p.InitialDelay), p.MaxDelay is null ? null : Time(p.MaxDelay))
            : null;

    private static WebhookSubscription View(WebhookSubscriptions s) => new(s.SubscriptionId, s.PartnerCode, s.EventTypes, s.EndpointUrl, s.SigningKeyRef, s.RetryPolicy);

    /// <summary>An ISO 8601 duration of days and times (PT1M, P1D) as a time span.</summary>
    private static TimeSpan Time(string duration) => PeriodPattern.NormalizingIso.Parse(duration).Value.ToDuration().ToTimeSpan();

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(IntegrationStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}
