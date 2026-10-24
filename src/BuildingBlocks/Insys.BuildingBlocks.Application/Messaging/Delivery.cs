/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using Insys.BuildingBlocks.Contracts;

namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>
/// Thrown by a consumer when the tenant lacks configuration it needs, for example BILLING_FREQUENCY_NOT_IN_FORCE with
/// item MONTHLY. The delivery is parked instead of retried as a failure, and retried when configuration is published
/// (ADR-022). The code and item name configuration only, never a party or payload value.
/// </summary>
public sealed class PrerequisiteMissingException(string code, string? item = null)
    : Exception($"Prerequisite {code}{(item is null ? "" : " " + item)} is missing.")
{
    public string Code { get; } = code.Length <= 60 ? code : throw new ArgumentException("A prerequisite code is at most 60 characters.", nameof(code));

    public string? Item { get; } = item is null || item.Length <= 80 ? item : item[..80];
}

/// <summary>
/// The prerequisite codes consumers park on (ADR-022), shared so that the operations view, alerts and escalation name
/// the same configuration the same way. The item names the code or key that is missing, never a party or payload value.
/// </summary>
public static class PrerequisiteCodes
{
    /// <summary>Item: the billing frequency code (billing.ref_billing_frequencies).</summary>
    public const string BillingFrequencyNotInForce = "BILLING_FREQUENCY_NOT_IN_FORCE";

    /// <summary>Item: the collection method code (billing.ref_collection_methods).</summary>
    public const string CollectionMethodNotInForce = "COLLECTION_METHOD_NOT_IN_FORCE";

    /// <summary>Item: the adjustment reason code (billing.ref_adjustment_and_reversal_reason_codes).</summary>
    public const string AdjustmentReasonNotInForce = "ADJUSTMENT_REASON_NOT_IN_FORCE";

    /// <summary>Item: product and document type ("GEN-MOTOR:POLICY_SCHEDULE"), or the template code.</summary>
    public const string DocumentTemplateNotInForce = "DOCUMENT_TEMPLATE_NOT_IN_FORCE";

    /// <summary>Item: product and treaty ("GEN-MOTOR:QS-2026"); no cession rule in force for a cession being recalculated.</summary>
    public const string CessionRuleNotInForce = "CESSION_RULE_NOT_IN_FORCE";

    /// <summary>Item: the facultative arrangement code that is no longer in force for a cession being recalculated.</summary>
    public const string FacultativeArrangementNotInForce = "FACULTATIVE_ARRANGEMENT_NOT_IN_FORCE";

    /// <summary>Item: the treaty code, or the error code when no single treaty is at fault.</summary>
    public const string TreatyNotApplicable = "TREATY_NOT_APPLICABLE";

    /// <summary>Item: the realm (insys). Keycloak's admin API could not be reached or failed (keycloak-user-provisioning §4).</summary>
    public const string KeycloakUnavailable = "KEYCLOAK_UNAVAILABLE";

    /// <summary>Item: username or email. The realm already has a user with it, in another tenant; the operator resolves it.</summary>
    public const string KeycloakUserConflict = "KEYCLOAK_USER_CONFLICT";

    /// <summary>Item: the realm. The user exists and is linked, but Keycloak could not send its invitation email.</summary>
    public const string KeycloakEmailUnsent = "KEYCLOAK_EMAIL_UNSENT";

    /// <summary>Item: the days overdue that no arrears ageing bucket (billing.cfg_arrears_ageing_buckets) covers, for example "1".</summary>
    public const string ArrearsAgeingBucketNotInForce = "ARREARS_AGEING_BUCKET_NOT_IN_FORCE";

    /// <summary>Item: the notification type code (communication.cfg_notification_types) a trigger asked for (communications §9).</summary>
    public const string NotificationTypeNotConfigured = "NOTIFICATION_TYPE_NOT_CONFIGURED";

    /// <summary>Item: type and channels ("PAYMENT_REMINDER:EMAIL,SMS"): no template in any language of the chain (communications §9).</summary>
    public const string NotificationTemplateNotInForce = "NOTIFICATION_TEMPLATE_NOT_IN_FORCE";

    /// <summary>Item: type and channels: no enabled provider for any channel the party's order allows (communications §9).</summary>
    public const string ChannelProviderNotConfigured = "CHANNEL_PROVIDER_NOT_CONFIGURED";
}

public enum DeliveryStatus
{
    /// <summary>The consumer handled the event, or had already handled it (its inbox).</summary>
    Processed,

    /// <summary>The consumer's configuration is missing; the delivery is in the ledger.</summary>
    Parked,

    /// <summary>An earlier delivery for the same record to this consumer is parked or failing; this one waits behind it.</summary>
    Waiting,

    /// <summary>The ledger is already retrying, has resolved or has discarded this delivery.</summary>
    Skipped,

    /// <summary>The consumer failed; the delivery is retried with back-off.</summary>
    Failed,
}

/// <summary>One consumer's outcome for one event.</summary>
public sealed record ConsumerDelivery(string Consumer, DeliveryStatus Status, Exception? Error = null, string? Code = null, string? Item = null);

public enum LedgerStatus
{
    Parked,
    Waiting,
    Failing,
    Retrying,
    DeadLettered,
    Resolved,
    Discarded,
}

/// <summary>A delivery the ledger holds: one consumer and one event.</summary>
public sealed record LedgerEntry(string Consumer, Guid EventId, LedgerStatus Status)
{
    /// <summary>Whether a later delivery for the same record to the same consumer must wait behind this one.</summary>
    public bool Blocks => Status is LedgerStatus.Parked or LedgerStatus.Waiting or LedgerStatus.Failing or LedgerStatus.Retrying or LedgerStatus.DeadLettered;
}

/// <summary>
/// The record of deliveries that did not simply succeed (ADR-022): parked for missing configuration, waiting behind an
/// earlier delivery for the same record, or failing. Implemented by the Integration module (integration.parked_delivery);
/// without it, a missing prerequisite is retried as a failure and order per record is not kept.
/// </summary>
public interface IDeliveryLedger
{
    bool Enabled { get; }

    /// <summary>The open deliveries for the event's record (tenant, aggregate type and ID), and any for this event.</summary>
    Task<IReadOnlyList<LedgerEntry>> ForRecord(EventEnvelope envelope, CancellationToken cancellationToken);

    Task Park(EventEnvelope envelope, string consumer, string code, string? item, CancellationToken cancellationToken);

    Task Wait(EventEnvelope envelope, string consumer, CancellationToken cancellationToken);

    /// <param name="ledgerOwned">True when the ledger retries it (a released or retried delivery); false when the outbox does.</param>
    Task Failing(EventEnvelope envelope, string consumer, bool ledgerOwned, CancellationToken cancellationToken);

    Task Resolve(EventEnvelope envelope, string consumer, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the record's earliest waiting delivery to the consumer, when nothing ahead of it still blocks, and marks it
    /// retrying. Null when there is none, or another instance has it.
    /// </summary>
    Task<EventEnvelope?> ClaimNextWaiting(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken);

    /// <summary>The outbox gave up on the event: these consumers' failing deliveries are dead-lettered and still block.</summary>
    Task DeadLettered(EventEnvelope envelope, IEnumerable<string> consumers, CancellationToken cancellationToken);
}

/// <summary>No ledger: every delivery goes ahead, and a missing prerequisite is a failure.</summary>
public sealed class NoDeliveryLedger : IDeliveryLedger
{
    public bool Enabled => false;

    public Task<IReadOnlyList<LedgerEntry>> ForRecord(EventEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LedgerEntry>>([]);

    public Task Park(EventEnvelope envelope, string consumer, string code, string? item, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task Wait(EventEnvelope envelope, string consumer, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task Failing(EventEnvelope envelope, string consumer, bool ledgerOwned, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task Resolve(EventEnvelope envelope, string consumer, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<EventEnvelope?> ClaimNextWaiting(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken) =>
        Task.FromResult<EventEnvelope?>(null);

    public Task DeadLettered(EventEnvelope envelope, IEnumerable<string> consumers, CancellationToken cancellationToken) => Task.CompletedTask;
}
