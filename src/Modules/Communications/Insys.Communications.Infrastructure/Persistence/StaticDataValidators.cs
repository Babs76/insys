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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Communications.Application;
using Insys.Communications.Infrastructure.StaticData;

namespace Insys.Communications.Infrastructure.Persistence;

/// <summary>
/// A notification type (communications §2, §4): the platform's types are service notices with no consent purpose; any
/// other is the tenant's marketing and names the consent purpose it needs. Channels are named once each.
/// </summary>
internal sealed class NotificationTypeValidator : IStaticRecordValidator
{
    public string Schema => "communication";

    public string Table => "cfg_notification_types";

    public string Rules =>
        "The platform's types (PAYMENT_REMINDER, PAYMENT_FINAL_NOTICE, RENEWAL_INVITATION, RENEWAL_REMINDER, CLAIM_STATUS, HEALTH_CLAIM_DECISION, "
        + "PREAUTH_DECISION, CASE_ACKNOWLEDGED, CASE_UPDATE, CASE_DECISION, CLAIM_DECISION_RIGHTS) are SERVICE with no consent purpose. Any other type is MARKETING and names the consent purpose it needs (POPIA s69); "
        + "SERVICE is refused for it. Channels are named once each.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        var errors = new Dictionary<string, string[]>();
        if (record is not NotificationTypes type)
        {
            return errors;
        }

        if (MergeFields.IsPlatformType(type.TypeCode))
        {
            if (type.Category != Contracts.NotificationCategories.Service || type.ConsentPurpose is not null)
            {
                errors["category"] = [$"{type.TypeCode} is a service notice: category SERVICE, with no consent purpose."];
            }
        }
        else if (type.Category != Contracts.NotificationCategories.Marketing || string.IsNullOrWhiteSpace(type.ConsentPurpose))
        {
            errors["category"] = [$"{type.TypeCode} is not a platform notice, so it is marketing: category MARKETING with the consent purpose it needs (POPIA s69)."];
        }

        if (type.Channels.Length == 0 || type.Channels.Distinct(StringComparer.Ordinal).Count() != type.Channels.Length)
        {
            errors["channels"] = ["Name each channel once, at least one."];
        }

        return errors;
    }
}

/// <summary>
/// A template (§2, §7) uses only its type's merge fields, so no contact detail, ID number or clinical value can be
/// merged in; a marketing template carries the sender identity and the opt-out contact (POPIA s69(4)). Email and push
/// have a subject.
/// </summary>
internal sealed class NotificationTemplateValidator : IStaticRecordValidator
{
    public string Schema => "communication";

    public string Table => "cfg_notification_templates";

    public string Rules =>
        "Merge fields ({name}) are only those the type offers: business references, never contact details, ID numbers, bank details or clinical data. "
        + "A marketing type's template contains {sender_identity} and {opt_out_contact} (POPIA s69(4)). Email and push templates have a subject.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        var errors = new Dictionary<string, string[]>();
        if (record is not NotificationTemplates template)
        {
            return errors;
        }

        var allowed = MergeFields.Allowed(template.TypeCode);
        var used = MergeFields.Used(template.Body).Concat(MergeFields.Used(template.Subject ?? "")).Distinct(StringComparer.Ordinal).ToList();
        if (used.Where(f => !allowed.Contains(f)).ToList() is { Count: > 0 } unknown)
        {
            errors["body"] = [$"{template.TypeCode} does not offer {string.Join(", ", unknown.Select(f => "{" + f + "}"))}. It offers: {string.Join(", ", allowed)}."];
        }

        if (!MergeFields.IsPlatformType(template.TypeCode) && (!used.Contains(MergeFields.SenderIdentity) || !used.Contains(MergeFields.OptOutContact)))
        {
            errors["body"] = [.. errors.GetValueOrDefault("body", []), "A marketing message names its sender and how to stop such messages: {sender_identity} and {opt_out_contact} (POPIA s69(4))."];
        }

        if (template.Channel is Contracts.Channels.Email or Contracts.Channels.Push && string.IsNullOrWhiteSpace(template.Subject))
        {
            errors["subject"] = [$"A {template.Channel} template has a subject."];
        }

        return errors;
    }
}
