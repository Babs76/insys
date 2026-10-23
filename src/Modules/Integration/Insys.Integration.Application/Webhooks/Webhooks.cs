/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Application.DeadLetters;
using Insys.Integration.Domain.DeadLetters;
using Insys.Integration.Domain.Webhooks;

namespace Insys.Integration.Application.Webhooks;

public interface IWebhookRepository
{
    Task<WebhookDelivery?> Get(Guid id, CancellationToken cancellationToken);

    Task<bool> Exists(string subscriptionId, Guid eventId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> Due(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookDelivery>> List(WebhookDeliveryStatus? status, string? subscriptionId, Guid? afterId, int limit, CancellationToken cancellationToken);

    void Add(WebhookDelivery delivery);
}

/// <summary>A partner's webhook subscription in force (integration.cfg_webhook_subscriptions).</summary>
public sealed record WebhookSubscription(string SubscriptionId, string PartnerCode, IReadOnlyList<string> EventTypes, string EndpointUrl, string SigningKeyRef,
    string? RetryPolicy);

public interface IWebhookCodes
{
    Task<IReadOnlyList<WebhookSubscription>> SubscriptionsFor(string eventType, DateOnly day, CancellationToken cancellationToken);

    Task<WebhookSubscription?> Subscription(string subscriptionId, DateOnly day, CancellationToken cancellationToken);

    Task<RetryPolicy?> RetryPolicy(string policyCode, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Sends a signed webhook. Returns the HTTP status, or null with an error when there was no response.</summary>
public interface IWebhookTransport
{
    Task<(int? Status, string? Error)> Send(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

/// <summary>Resolves a secret reference (a signing key) to its value; null when it is not known.</summary>
public interface ISecretStore
{
    string? Resolve(string secretRef);
}

public sealed record WebhookDeliveryView(Guid Id, string SubscriptionId, Guid EventId, string EventType, int Attempt, string Status, int? HttpStatus,
    DateTimeOffset? NextAttemptAt, string? Error, long RowVersion)
{
    public static WebhookDeliveryView From(WebhookDelivery d) => new(d.Id, d.SubscriptionId, d.EventId, d.EventType, d.Attempt, d.Status.ToString(), d.HttpStatus,
        d.NextAttemptAt, d.Error, d.RowVersion);
}

/// <summary>Command DeliverEvent's attempt for a due delivery (the webhook job sends it).</summary>
public sealed record AttemptDelivery(Guid DeliveryId) : ICommand<WebhookDeliveryView>;

/// <summary>Command RetryDelivery: attempt a pending delivery now.</summary>
public sealed record RetryDelivery(Guid DeliveryId) : ICommand<WebhookDeliveryView>;

public sealed record ListDeliveries(WebhookDeliveryStatus? Status, string? SubscriptionId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<WebhookDeliveryView>>;

public sealed record DueDeliveries(DateTimeOffset Now, int Limit) : IQuery<IReadOnlyList<Guid>>;

public static class WebhookSignatures
{
    public const string SignatureHeader = "X-Insys-Signature";
    public const string EventIdHeader = "X-Insys-Event-Id";
    public const string EventTypeHeader = "X-Insys-Event-Type";

    /// <summary>"sha256=" and the hex HMAC-SHA256 of the body with the subscription's signing key.</summary>
    public static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
}

internal sealed class WebhookHandlers(IWebhookRepository deliveries, IWebhookCodes codes, IWebhookTransport transport, ISecretStore secrets,
    IDeadLetterRepository deadLetters, IClock clock, IAuditTrail audit) :
    ICommandHandler<AttemptDelivery, WebhookDeliveryView>,
    ICommandHandler<RetryDelivery, WebhookDeliveryView>,
    IQueryHandler<ListDeliveries, IReadOnlyList<WebhookDeliveryView>>,
    IQueryHandler<DueDeliveries, IReadOnlyList<Guid>>
{
    /// <summary>
    /// Sends the delivery, signed with the subscription's key (OQ-066 item 1), and records the outcome. A delivery whose
    /// retries are used up fails and is dead-lettered for an operator (INT-006).
    /// </summary>
    public async Task<Result<WebhookDeliveryView>> Handle(AttemptDelivery command, CancellationToken cancellationToken)
    {
        if (await deliveries.Get(command.DeliveryId, cancellationToken) is not { } delivery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The webhook delivery was not found.");
        }

        var now = clock.UtcNow;
        if (!delivery.IsDue(now))
        {
            return WebhookDeliveryView.From(delivery);
        }

        var subscription = await codes.Subscription(delivery.SubscriptionId, clock.UtcToday, cancellationToken);
        var policy = subscription?.RetryPolicy is { } code ? await codes.RetryPolicy(code, clock.UtcToday, cancellationToken) ?? RetryPolicy.Default : RetryPolicy.Default;
        (int? Status, string? Error) outcome;
        if (subscription is null)
        {
            outcome = (null, "The subscription is no longer in force.");
        }
        else if (secrets.Resolve(subscription.SigningKeyRef) is not { } secret)
        {
            outcome = (null, "The signing key is not available.");
        }
        else
        {
            var headers = new Dictionary<string, string>
            {
                [WebhookSignatures.SignatureHeader] = WebhookSignatures.Sign(delivery.Payload, secret),
                [WebhookSignatures.EventIdHeader] = delivery.EventId.ToString(),
                [WebhookSignatures.EventTypeHeader] = delivery.EventType,
            };
            outcome = await transport.Send(subscription.EndpointUrl, delivery.Payload, headers, cancellationToken);
        }

        var recorded = delivery.RecordAttempt(outcome.Status, outcome.Error ?? (outcome.Status is >= 200 and < 300 ? null : $"HTTP {outcome.Status}"), policy, now);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (delivery.Status == WebhookDeliveryStatus.Failed)
        {
            deadLetters.Add(DeadLetter.DeadLetterMessage(Guid.CreateVersion7(), new Source($"webhook:{delivery.SubscriptionId}"), new MessageRef(delivery.EventId.ToString()),
                delivery.Payload, new DeliveryError(delivery.Error ?? "Retries exhausted."), now, (short)delivery.Attempt));
        }

        return WebhookDeliveryView.From(delivery);
    }

    public async Task<Result<WebhookDeliveryView>> Handle(RetryDelivery command, CancellationToken cancellationToken)
    {
        if (await deliveries.Get(command.DeliveryId, cancellationToken) is not { } delivery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The webhook delivery was not found.");
        }

        audit.Record(new AuditEntry("INTEGRATION", "RetryDelivery", nameof(WebhookDelivery), delivery.Id.ToString()));
        var result = delivery.RetryNow(clock.UtcNow);
        return result.IsFailure ? result.Error : WebhookDeliveryView.From(delivery);
    }

    public async Task<Result<IReadOnlyList<WebhookDeliveryView>>> Handle(ListDeliveries query, CancellationToken cancellationToken) =>
        (await deliveries.List(query.Status, query.SubscriptionId, query.AfterId, Math.Clamp(query.Limit, 1, 200), cancellationToken)).Select(WebhookDeliveryView.From).ToList();

    public async Task<Result<IReadOnlyList<Guid>>> Handle(DueDeliveries query, CancellationToken cancellationToken) =>
        Result.Success(await deliveries.Due(query.Now, query.Limit, cancellationToken));
}

/// <summary>
/// Integration's policy for every published event (domain-model.json: integration consumes subscribed domain events →
/// DeliverEvent): one delivery per partner subscription in force that lists the event's type. The delivery sends the
/// event's identity and its payload, which carries no medical or identity data (INT-007).
/// </summary>
public sealed class WebhookPolicy(IWebhookRepository deliveries, IWebhookCodes codes, IClock clock) : IIntegrationEventHandler<AnyIntegrationEvent>
{
    public async Task Handle(AnyIntegrationEvent integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var subscriptions = await codes.SubscriptionsFor(envelope.EventType, clock.UtcToday, cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            eventId = envelope.EventId, eventType = envelope.EventType, eventVersion = envelope.EventVersion, occurredAt = envelope.OccurredAt,
            data = integrationEvent.Payload,
        }, JsonSerializerOptions.Web);
        foreach (var subscription in subscriptions)
        {
            if (!await deliveries.Exists(subscription.SubscriptionId, envelope.EventId, cancellationToken))
            {
                deliveries.Add(WebhookDelivery.DeliverEvent(Guid.CreateVersion7(), subscription.SubscriptionId, envelope.EventId, envelope.EventType, body, clock.UtcNow));
            }
        }
    }
}
