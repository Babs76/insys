using Insys.BuildingBlocks.Domain;

namespace Insys.Integration.Domain.Webhooks;

public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
}

public enum WebhookDeliveryTrigger
{
    Response2xx,
    RetriesExhausted,
}

/// <summary>A retry and dead-letter policy (integration.cfg_retry_and_dead_letter_policies): attempts in all, back-off and delays.</summary>
public sealed record RetryPolicy(string PolicyCode, int MaxAttempts, string Backoff, TimeSpan InitialDelay, TimeSpan? MaxDelay)
{
    /// <summary>The default when a subscription names none: five attempts, exponential from one minute, at most an hour apart (OQ-066).</summary>
    public static readonly RetryPolicy Default = new("DEFAULT", 5, "EXPONENTIAL", TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

    /// <summary>The wait after a failed attempt: the initial delay, doubled per attempt for EXPONENTIAL, never above the maximum.</summary>
    public TimeSpan DelayAfter(int attempt)
    {
        var delay = Backoff == "EXPONENTIAL" ? InitialDelay * Math.Pow(2, Math.Max(0, attempt - 1)) : InitialDelay;
        return MaxDelay is { } max && delay > max ? max : delay;
    }
}

/// <summary>Entity DeliveryAttempt: the outcome of one attempt; the delivery keeps its latest (webhook_delivery has one row per delivery).</summary>
public sealed record DeliveryAttempt(int Attempt, int? HttpStatus, string? Error);

/// <summary>
/// The WebhookDelivery aggregate (integration.webhook_delivery): one subscribed event for one partner subscription, with
/// what it sends, so every attempt sends the same. Delivered on a 2xx response; otherwise retried with back-off up to the
/// policy's limit (INT-006), then Failed (and dead-lettered by the caller).
/// </summary>
public sealed class WebhookDelivery : AggregateRoot<Guid>
{
    public static readonly StateMachine<WebhookDeliveryStatus, WebhookDeliveryTrigger> Lifecycle = new StateMachine<WebhookDeliveryStatus, WebhookDeliveryTrigger>()
        .Allow(WebhookDeliveryStatus.Pending, WebhookDeliveryTrigger.Response2xx, WebhookDeliveryStatus.Delivered)
        .Allow(WebhookDeliveryStatus.Pending, WebhookDeliveryTrigger.RetriesExhausted, WebhookDeliveryStatus.Failed);

    private WebhookDelivery()
    {
    }

    public string SubscriptionId { get; private set; } = "";

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = "";

    public string Payload { get; private set; } = "";

    public int Attempt { get; private set; }

    public WebhookDeliveryStatus Status { get; private set; }

    public int? HttpStatus { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? Error { get; private set; }

    public DeliveryAttempt? LastAttempt => Attempt == 0 ? null : new DeliveryAttempt(Attempt, HttpStatus, Error);

    /// <summary>Command DeliverEvent: a subscribed event waits for its first attempt, now.</summary>
    public static WebhookDelivery DeliverEvent(Guid id, string subscriptionId, Guid eventId, string eventType, string payload, DateTimeOffset now)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guard.NotEmpty(id), SubscriptionId = Guard.MaxLength(subscriptionId, 20), EventId = eventId, EventType = Guard.MaxLength(eventType, 80), Payload = payload,
            Status = WebhookDeliveryStatus.Pending, NextAttemptAt = now,
        };
        delivery.MarkChanged();
        return delivery;
    }

    public bool IsDue(DateTimeOffset now) => Status == WebhookDeliveryStatus.Pending && NextAttemptAt <= now;

    /// <summary>
    /// The outcome of an attempt (DeliverEvent, or RetryDelivery when due again): a 2xx delivers it; anything else waits
    /// for the policy's back-off, until its attempts are used up and it fails.
    /// </summary>
    public Result RecordAttempt(int? httpStatus, string? error, RetryPolicy policy, DateTimeOffset now)
    {
        if (Status != WebhookDeliveryStatus.Pending)
        {
            return BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition, $"The delivery is {Status}.");
        }

        Attempt++;
        HttpStatus = httpStatus;
        Error = error is null ? null : Guard.MaxLength(error, 500);
        if (httpStatus is >= 200 and < 300)
        {
            NextAttemptAt = null;
            return Fire(WebhookDeliveryTrigger.Response2xx);
        }

        if (Attempt >= policy.MaxAttempts)
        {
            NextAttemptAt = null;
            return Fire(WebhookDeliveryTrigger.RetriesExhausted);
        }

        NextAttemptAt = now + policy.DelayAfter(Attempt);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command RetryDelivery by an operator: a pending delivery is attempted now instead of at its back-off time.</summary>
    public Result RetryNow(DateTimeOffset now)
    {
        if (Status != WebhookDeliveryStatus.Pending)
        {
            return BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a pending delivery is retried; this one is {Status}.");
        }

        NextAttemptAt = now;
        MarkChanged();
        return Result.Success();
    }

    private Result Fire(WebhookDeliveryTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
