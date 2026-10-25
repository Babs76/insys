/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Communications.Domain;

public enum NotificationStatus
{
    Queued,
    Sent,
    Delivered,
    Failed,
    Undeliverable,
    SuppressedNoConsent,
}

public enum AttemptStatus
{
    Sent,
    Delivered,
    Failed,
    Bounced,
    Undeliverable,
}

/// <summary>What a provider's delivery callback reports (§5.1).</summary>
public enum DeliveryOutcome
{
    Delivered,
    Bounced,
    Failed,
}

public static class CommunicationErrorCodes
{
    [ErrorCode(422, "error.communication.preference_not_allowed")]
    public const string PreferenceNotAllowed = "PREFERENCE_NOT_ALLOWED";
}

public sealed record NotificationWasSent(Guid NotificationId, string TypeCode, Guid RecipientPartyId, string SourceType, Guid SourceId, string Channel, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record NotificationWasDelivered(Guid NotificationId, string Channel, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record NotificationEnded(Guid NotificationId, string TypeCode, Guid RecipientPartyId, NotificationStatus Status, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// One message to one party from one trigger (addenda/communications.md §5): queued, then sent when a channel accepts it,
/// delivered when the provider confirms. Channels are tried in order; a retryable failure is retried on the same channel
/// up to <see cref="AttemptsPerChannel"/> times with back-off, anything else moves to the next channel. With every channel
/// tried, it is undeliverable (no address on any) or failed.
/// </summary>
public sealed class Notification : AggregateRoot<Guid>
{
    public const int AttemptsPerChannel = 3;

    private readonly List<DeliveryAttempt> _attempts = [];

    private Notification()
    {
    }

    public string RequestKey { get; private set; } = "";

    public string TypeCode { get; private set; } = "";

    public string Category { get; private set; } = "";

    public string? ConsentPurpose { get; private set; }

    public Guid RecipientPartyId { get; private set; }

    public string SourceType { get; private set; } = "";

    public Guid SourceId { get; private set; }

    /// <summary>The source record's own reference (a claim, policy, account or authorisation number), for display.</summary>
    public string? SourceReference { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    /// <summary>The merge values: business references only (INT-007).</summary>
    public IReadOnlyDictionary<string, string> MergeValues { get; private set; } = new Dictionary<string, string>();

    /// <summary>The channels to try, in order: the party's preference within the type's channels and the tenant's providers.</summary>
    public IReadOnlyList<string> Channels { get; private set; } = [];

    public string Language { get; private set; } = "";

    /// <summary>The text as sent, on the channel that took it: evidence of what was sent.</summary>
    public string? Subject { get; private set; }

    public string? Body { get; private set; }

    public NotificationStatus Status { get; private set; }

    /// <summary>The channel that took it, once sent.</summary>
    public string? Channel { get; private set; }

    public short Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public IReadOnlyList<DeliveryAttempt> DeliveryAttempts => _attempts;

    public bool IsMarketing => ConsentPurpose is not null;

    public static Notification Queue(Guid id, string requestKey, string typeCode, string category, string? consentPurpose, Guid recipientPartyId, string sourceType,
        Guid sourceId, string? productCode, string? orgNodeCode, IReadOnlyDictionary<string, string> mergeValues, IReadOnlyList<string> channels, string language,
        DateTimeOffset now)
    {
        if (channels.Count == 0)
        {
            throw new ArgumentException("A notification has at least one channel to try.", nameof(channels));
        }

        var notification = new Notification
        {
            Id = Guard.NotEmpty(id),
            RequestKey = Guard.MaxLength(requestKey, 200),
            TypeCode = Guard.MaxLength(typeCode, 30),
            Category = category,
            ConsentPurpose = consentPurpose,
            RecipientPartyId = Guard.NotEmpty(recipientPartyId),
            SourceType = Guard.MaxLength(sourceType, 30),
            SourceId = sourceId,
            SourceReference = SourceReferenceOf(mergeValues),
            RequestedAt = now,
            ProductCode = productCode,
            OrgNodeCode = orgNodeCode,
            MergeValues = new Dictionary<string, string>(mergeValues, StringComparer.Ordinal),
            Channels = [.. channels],
            Language = language,
            Status = NotificationStatus.Queued,
            NextAttemptAt = now,
        };
        notification.MarkChanged();
        return notification;
    }

    private static readonly string[] ReferenceFields = ["claim_number", "auth_number", "policy_number", "account_number"];

    private static string? SourceReferenceOf(IReadOnlyDictionary<string, string> values) =>
        ReferenceFields.Select(f => values.TryGetValue(f, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrEmpty(v)) is { } reference
            ? Guard.MaxLength(reference, 40)
            : null;

    /// <summary>The channel to try next: the first whose attempts are not used up, or null when every channel is.</summary>
    public string? NextChannel() => Channels.FirstOrDefault(c => !Exhausted(c));

    /// <summary>Marketing whose consent is not granted when it is due (§4): nothing is sent.</summary>
    public void SuppressForConsent(DateTimeOffset now)
    {
        if (Status == NotificationStatus.Queued)
        {
            End(NotificationStatus.SuppressedNoConsent, now);
        }
    }

    /// <summary>The party has no address for the channel: the channel is used up, without a provider.</summary>
    public void NoAddress(string channel, DateTimeOffset now)
    {
        Record(channel, null, null, null, AttemptStatus.Undeliverable, "NO_ADDRESS", now);
        Advance(now);
    }

    /// <summary>The channel took the message; <paramref name="subject"/> and <paramref name="body"/> are what was sent.</summary>
    public void Accepted(string channel, string providerCode, string providerRef, string maskedAddress, string? subject, string body, DateTimeOffset now)
    {
        Record(channel, providerCode, providerRef, maskedAddress, AttemptStatus.Sent, null, now);
        Status = NotificationStatus.Sent;
        Channel = channel;
        Subject = subject;
        Body = body;
        SentAt = now;
        NextAttemptAt = null;
        Raise(new NotificationWasSent(Id, TypeCode, RecipientPartyId, SourceType, SourceId, channel, now));
        MarkChanged();
    }

    /// <summary>The channel refused it. A retryable failure is retried after a back-off; otherwise the next channel is tried.</summary>
    public void Refused(string channel, string providerCode, string maskedAddress, string failureCode, bool retryable, DateTimeOffset now)
    {
        Record(channel, providerCode, null, maskedAddress, AttemptStatus.Failed, failureCode, now, retryable);
        Advance(now);
    }

    /// <summary>
    /// The provider's delivery callback for one of the attempts (§5.1). Delivered ends the notification; a bounce or a
    /// failure after sending uses the channel up and tries the next. False when no attempt has the reference.
    /// </summary>
    public bool Confirm(string providerRef, DeliveryOutcome outcome, DateTimeOffset now)
    {
        if (_attempts.FirstOrDefault(a => a.ProviderRef == providerRef) is not { } attempt)
        {
            return false;
        }

        if (attempt.Status is AttemptStatus.Delivered or AttemptStatus.Bounced || (attempt.Status == AttemptStatus.Failed && outcome != DeliveryOutcome.Delivered))
        {
            return true;
        }

        attempt.Settle(outcome switch
        {
            DeliveryOutcome.Delivered => AttemptStatus.Delivered,
            DeliveryOutcome.Bounced => AttemptStatus.Bounced,
            _ => AttemptStatus.Failed,
        }, now);

        if (outcome == DeliveryOutcome.Delivered)
        {
            if (Status != NotificationStatus.Delivered)
            {
                Status = NotificationStatus.Delivered;
                DeliveredAt = now;
                EndedAt = now;
                Raise(new NotificationWasDelivered(Id, attempt.Channel, now));
            }
        }
        else if (Status == NotificationStatus.Sent && Channel == attempt.Channel)
        {
            Status = NotificationStatus.Queued;
            Channel = null;
            Subject = null;
            Body = null;
            Advance(now);
        }

        MarkChanged();
        return true;
    }

    /// <summary>POPIA: the party is anonymised, so what was sent to them is blanked (§10).</summary>
    public void Anonymise()
    {
        Subject = null;
        Body = null;
        MergeValues = new Dictionary<string, string>();
        foreach (var attempt in _attempts)
        {
            attempt.ForgetAddress();
        }

        MarkChanged();
    }

    private bool Exhausted(string channel)
    {
        var onChannel = _attempts.Where(a => a.Channel == channel).ToList();
        // Used up: no address, a bounce, a failure reported after sending or one that is not retryable, or the retries spent.
        return onChannel.Any(a => a.Status is AttemptStatus.Undeliverable or AttemptStatus.Bounced
                                  || (a.Status == AttemptStatus.Failed && !a.Retryable))
               || onChannel.Count(a => a.Status == AttemptStatus.Failed) >= AttemptsPerChannel;
    }

    private void Advance(DateTimeOffset now)
    {
        if (NextChannel() is { } next)
        {
            var failuresOnNext = _attempts.Count(a => a.Channel == next && a.Status == AttemptStatus.Failed);
            NextAttemptAt = failuresOnNext == 0 ? now : now + TimeSpan.FromMinutes(Math.Pow(2, failuresOnNext - 1));
            MarkChanged();
            return;
        }

        End(_attempts.All(a => a.Status == AttemptStatus.Undeliverable) ? NotificationStatus.Undeliverable : NotificationStatus.Failed, now);
    }

    private void End(NotificationStatus status, DateTimeOffset now)
    {
        Status = status;
        NextAttemptAt = null;
        EndedAt = now;
        Raise(new NotificationEnded(Id, TypeCode, RecipientPartyId, status, now));
        MarkChanged();
    }

    private void Record(string channel, string? providerCode, string? providerRef, string? maskedAddress, AttemptStatus status, string? failureCode, DateTimeOffset now,
        bool retryable = false)
    {
        Attempts++;
        _attempts.Add(DeliveryAttempt.Create(Id, (short)(_attempts.Count + 1), channel, providerCode, providerRef, maskedAddress, status, failureCode, retryable, now));
        MarkChanged();
    }
}

/// <summary>One try on one channel (communication.delivery_attempt). The address is kept only masked.</summary>
public sealed class DeliveryAttempt : Entity<Guid>, ITenantScoped
{
    private DeliveryAttempt()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid NotificationId { get; private set; }

    public short AttemptNo { get; private set; }

    public string Channel { get; private set; } = "";

    public string? ProviderCode { get; private set; }

    public string? ProviderRef { get; private set; }

    public string? MaskedAddress { get; private set; }

    public AttemptStatus Status { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>A temporary failure, retried on the same channel; false once a provider reports a failure after sending.</summary>
    public bool Retryable { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    public DateTimeOffset StatusAt { get; private set; }

    internal static DeliveryAttempt Create(Guid notificationId, short attemptNo, string channel, string? providerCode, string? providerRef, string? maskedAddress,
        AttemptStatus status, string? failureCode, bool retryable, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notificationId,
            AttemptNo = attemptNo,
            Channel = channel,
            ProviderCode = providerCode,
            ProviderRef = providerRef is null ? null : Guard.MaxLength(providerRef, 120),
            MaskedAddress = maskedAddress is null ? null : Guard.MaxLength(maskedAddress, 120),
            Status = status,
            FailureCode = failureCode is null ? null : Guard.MaxLength(failureCode, 40),
            Retryable = retryable,
            AttemptedAt = now,
            StatusAt = now,
        };

    internal void Settle(AttemptStatus status, DateTimeOffset now)
    {
        Status = status;
        Retryable = false;
        StatusAt = now;
    }

    internal void ForgetAddress() => MaskedAddress = null;
}
