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
using System.Text.RegularExpressions;
using Insys.Communications.Contracts;
using Insys.Communications.Domain;

namespace Insys.Communications.Application;

/// <summary>A notification type in force (communication.cfg_notification_types).</summary>
/// <param name="Labels">The type's labels by language (the static data's labels), for showing it to a member.</param>
public sealed record NotificationTypeDefinition(string TypeCode, string Category, string? ConsentPurpose, IReadOnlyList<string> Channels, bool PortalVisible,
    System.Text.Json.JsonElement? Labels = null)
{
    public bool IsMarketing => Category == NotificationCategories.Marketing;
}

/// <summary>A template in force for a type, channel and language (communication.cfg_notification_templates).</summary>
public sealed record TemplateDefinition(string TypeCode, string Channel, string Language, string? Subject, string Body);

/// <summary>An enabled provider for a channel (communication.cfg_channel_providers).</summary>
public sealed record ChannelProvider(string Channel, string ProviderCode, string SenderIdentity);

/// <summary>Communications' static data in force on a day.</summary>
public interface ICommunicationCodes
{
    Task<NotificationTypeDefinition?> Type(string typeCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Every type in force, by code.</summary>
    Task<IReadOnlyList<NotificationTypeDefinition>> Types(DateOnly day, CancellationToken cancellationToken);

    Task<TemplateDefinition?> Template(string typeCode, string channel, string language, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelProvider>> Providers(DateOnly day, CancellationToken cancellationToken);
}

public interface INotificationRepository
{
    Task<bool> Exists(string requestKey, CancellationToken cancellationToken);

    Task<Notification?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>The notification, locked for this transaction (FOR UPDATE SKIP LOCKED); null when another instance holds it.</summary>
    Task<Notification?> Lock(Guid id, CancellationToken cancellationToken);

    /// <summary>Queued notifications due by <paramref name="now"/>, oldest first, locked for this pass (SKIP LOCKED).</summary>
    Task<IReadOnlyList<Notification>> Due(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<Notification?> ByProviderRef(string providerCode, string providerRef, CancellationToken cancellationToken);

    /// <summary>The party's queued marketing for a consent purpose (to suppress when consent is withdrawn).</summary>
    Task<IReadOnlyList<Notification>> QueuedMarketing(Guid partyId, string consentPurpose, CancellationToken cancellationToken);

    Task<IReadOnlyList<Notification>> ForParty(Guid partyId, CancellationToken cancellationToken);

    void Add(Notification notification);

    Task<NotificationPreference?> Preference(Guid partyId, string typeCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationPreference>> Preferences(Guid partyId, CancellationToken cancellationToken);

    void Add(NotificationPreference preference);

    Task<DeviceRegistration?> Device(string tokenHash, CancellationToken cancellationToken);

    Task<DeviceRegistration?> Device(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceRegistration>> Devices(Guid userId, CancellationToken cancellationToken);

    /// <summary>The party's newest registered device, for push.</summary>
    Task<DeviceRegistration?> LatestDevice(Guid partyId, CancellationToken cancellationToken);

    void Add(DeviceRegistration device);
}

/// <summary>
/// The merge fields each type may use (addenda/communications.md §2): business references only, never contact details,
/// ID numbers, bank details or clinical data. A tenant's own types are marketing (§4) and must carry the sender's identity
/// and the opt-out contact (POPIA s69(4)).
/// </summary>
public static partial class MergeFields
{
    public const string SenderIdentity = "sender_identity";
    public const string OptOutContact = "opt_out_contact";
    public const string PortalLink = "portal_link";

    private static readonly string[] Common = [SenderIdentity, PortalLink];

    private static readonly IReadOnlyDictionary<string, string[]> ByType = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [NotificationTypes.PaymentReminder] = ["account_number", "policy_number", "amount_overdue", "currency", "days_overdue", "due_date"],
        [NotificationTypes.PaymentFinalNotice] = ["account_number", "policy_number", "amount_overdue", "currency", "days_overdue", "due_date"],
        [NotificationTypes.RenewalInvitation] = ["policy_number", "product_code", "period_end"],
        [NotificationTypes.RenewalReminder] = ["policy_number", "product_code", "period_end", "days_to_renewal"],
        [NotificationTypes.ClaimStatus] = ["claim_number", "status", "status_date", "amount", "currency"],
        [NotificationTypes.HealthClaimDecision] = ["claim_number", "status", "status_date", "total_paid", "currency"],
        [NotificationTypes.PreauthDecision] = ["auth_number", "status", "valid_from", "valid_to"],
        [NotificationTypes.CaseAcknowledged] = Triggers.CaseMergeFields.All,
        [NotificationTypes.CaseUpdate] = Triggers.CaseMergeFields.All,
        [NotificationTypes.CaseDecision] = Triggers.CaseMergeFields.All,
        [NotificationTypes.ClaimDecisionRights] = ClaimDecisionRightsFields.All,
    };

    /// <summary>Whether the type is one the platform triggers (a service notice); any other is a tenant's marketing type.</summary>
    public static bool IsPlatformType(string typeCode) => ByType.ContainsKey(typeCode);

    public static IReadOnlyCollection<string> Allowed(string typeCode) =>
        ByType.TryGetValue(typeCode, out var fields) ? [.. Common, .. fields] : [.. Common, OptOutContact];

    public static IReadOnlyCollection<string> Used(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The text with each {field} replaced by its value; a field without a value is left empty.</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : "");

    [GeneratedRegex(@"\{([a-z][a-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// The merge fields of CLAIM_DECISION_RIGHTS (OQ-102, PPR 17.6.3), which the claim modules fill: the claim, the decision
/// date, the reasons (the decision reason's customer wording), and the amounts approved and claimed. A Health claim's
/// notice gives no reason and no amount but the total paid (HLT-008).
/// </summary>
public static class ClaimDecisionRightsFields
{
    public const string ClaimNumber = "claim_number";
    public const string DecisionDate = "decision_date";
    public const string DecisionReasons = "decision_reasons";
    public const string Amount = "amount";
    public const string ClaimedAmount = "claimed_amount";
    public const string Currency = "currency";

    public static readonly string[] All = [ClaimNumber, DecisionDate, DecisionReasons, Amount, ClaimedAmount, Currency];
}

/// <summary>Masks an address for the record: the first character and the domain of an email, the last three digits of a number.</summary>
public static class Addresses
{
    public static string Mask(string channel, string address) => channel switch
    {
        Contracts.Channels.Email when address.IndexOf('@') is > 0 and var at => $"{address[0]}***{address[at..]}",
        Contracts.Channels.Sms or Contracts.Channels.WhatsApp when address.Length > 6 => $"{address[..3]}*****{address[^3..]}",
        Contracts.Channels.Push => "device",
        _ => "***",
    };

    /// <summary>The Party contact type that holds a channel's address; push uses a device registration instead.</summary>
    public static string? ContactType(string channel) => channel switch
    {
        Contracts.Channels.Email => "EMAIL",
        Contracts.Channels.Sms or Contracts.Channels.WhatsApp => "MOBILE",
        _ => null,
    };
}

public sealed class CommunicationOptions
{
    /// <summary>How many due notifications a sending pass takes per tenant.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How often the sending job runs.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public bool JobEnabled { get; set; } = true;

    /// <summary>The portal address templates link to ({portal_link}).</summary>
    public string? PortalLink { get; set; }

    /// <summary>
    /// The contact in every marketing message for a request that such messages stop ({opt_out_contact}, POPIA s69(4)).
    /// TODO(spec): per tenant once its communications settings exist; one per deployment until then.
    /// </summary>
    public string? OptOutContact { get; set; }
}
