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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Communications.Contracts;

public static class CommunicationPermissions
{
    public const string NotificationRead = "notification.read";
    public const string PreferenceRead = "notification_preference.read";
    public const string PreferenceUpdate = "notification_preference.update";
    public const string DeviceUpdate = "notification_device.update";
    public const string CallbackEndpointRead = "communication_callback_endpoint.read";
    public const string CallbackEndpointUpdate = "communication_callback_endpoint.update";
}

/// <summary>The channel codes (communication.ref_channels, OQ-090).</summary>
public static class Channels
{
    public const string Email = "EMAIL";
    public const string Sms = "SMS";
    public const string Push = "PUSH";
    public const string WhatsApp = "WHATSAPP";

    public static readonly IReadOnlyList<string> All = [Email, Sms, Push, WhatsApp];
}

/// <summary>A notification type's category (addenda/communications.md §2): marketing needs consent when it is sent (§4).</summary>
public static class NotificationCategories
{
    public const string Service = "SERVICE";
    public const string Marketing = "MARKETING";
}

/// <summary>The service notification types the platform triggers (§2, §6); a tenant configures each in cfg_notification_types.</summary>
public static class NotificationTypes
{
    public const string PaymentReminder = "PAYMENT_REMINDER";
    public const string PaymentFinalNotice = "PAYMENT_FINAL_NOTICE";
    public const string RenewalInvitation = "RENEWAL_INVITATION";
    public const string RenewalReminder = "RENEWAL_REMINDER";
    public const string ClaimStatus = "CLAIM_STATUS";
    public const string HealthClaimDecision = "HEALTH_CLAIM_DECISION";
    public const string PreauthDecision = "PREAUTH_DECISION";

    /// <summary>A case was acknowledged to the complainant (addenda/cases.md §7; OQ-107: the acknowledgement is the confirmation).</summary>
    public const string CaseAcknowledged = "CASE_ACKNOWLEDGED";

    /// <summary>A case's progress or delay, with its new expected date (§7, PPR 18.9).</summary>
    public const string CaseUpdate = "CASE_UPDATE";

    /// <summary>A case's decision, with its reasons, the review options and the ombud's details (§7, PPR 18.7).</summary>
    public const string CaseDecision = "CASE_DECISION";

    /// <summary>
    /// The claimant's rights on a claim decision that declines all or part of a claim (§7, OQ-102, PPR 17.6.3): the
    /// reasons, the right to make representations, internal escalation, the ombud and any time-bar. A claim module
    /// requests it (NotificationRequested); the period and contact wording are the tenant's template.
    /// </summary>
    public const string ClaimDecisionRights = "CLAIM_DECISION_RIGHTS";

    /// <summary>A General policy entered grace for a missed instalment: cover ends on a date unless paid (F006, Wave 4 addition 1).</summary>
    public const string GraceNotice = "GRACE_NOTICE";

    /// <summary>A General policy was cancelled for non-payment when its grace ended unpaid (F006, Wave 4 addition 1).</summary>
    public const string NonPaymentCancellation = "NON_PAYMENT_CANCELLATION";

    /// <summary>A Health membership was suspended for unpaid contributions, with the date the notice before termination ends (F006).</summary>
    public const string ContributionSuspension = "CONTRIBUTION_SUSPENSION";

    /// <summary>A Health scheme's renewal is near: its sponsor is told, by the scheme's renewal rule's notice (Wave 4 §4.5).</summary>
    public const string SchemeRenewalNotice = "SCHEME_RENEWAL_NOTICE";
}

/// <summary>
/// A module asks for a notification to a party (§1). Merge values are business references only (a claim number, a due
/// date, an amount), never contact details, ID numbers or clinical data (INT-007). One notification per event ID, type
/// and recipient: a redelivered request creates nothing.
/// </summary>
/// <param name="SourceType">What the notification is about (GENERAL_CLAIM, BILLING_ACCOUNT …), with <paramref name="SourceId"/>.</param>
/// <param name="ProductCode">The source record's product, for F103 scope on staff reads.</param>
/// <param name="OrgNodeCode">The source record's organisation node, for F103 scope on staff reads.</param>
[IntegrationEvent("communication.NotificationRequested")]
public sealed record NotificationRequested(string NotificationType, Guid RecipientPartyId, string SourceType, Guid SourceId, IReadOnlyDictionary<string, string> MergeValues,
    string? ProductCode = null, string? OrgNodeCode = null) : IIntegrationEvent;

/// <summary>A channel accepted a notification (§5). Claims Framework logs a claim's notices as outbound communications.</summary>
[IntegrationEvent("communication.NotificationSent")]
public sealed record NotificationSent(Guid NotificationId, string NotificationType, Guid RecipientPartyId, string SourceType, Guid SourceId, string Channel)
    : IIntegrationEvent;

/// <summary>The provider confirmed delivery (§5.1).</summary>
[IntegrationEvent("communication.NotificationDelivered")]
public sealed record NotificationDelivered(Guid NotificationId, string Channel) : IIntegrationEvent;

/// <summary>A notification ended without being sent: FAILED, UNDELIVERABLE or SUPPRESSED_NO_CONSENT (§5).</summary>
[IntegrationEvent("communication.NotificationFailed")]
public sealed record NotificationFailed(Guid NotificationId, string NotificationType, Guid RecipientPartyId, string Status) : IIntegrationEvent;

/// <summary>
/// A party opted out of a marketing type (§4): Party records it as the withdrawal of the type's consent purpose, so the
/// consent ledger and the preference agree. <paramref name="CaptureChannel"/> is PORTAL or STAFF.
/// </summary>
[IntegrationEvent("communication.MarketingOptedOut")]
public sealed record MarketingOptedOut(Guid PartyId, string PurposeCode, string CaptureChannel) : IIntegrationEvent;
