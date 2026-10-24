/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
namespace Insys.Testing;

/// <summary>Notification configuration for tests (addenda/communications.md): an email provider, service types and English email templates.</summary>
public static class CommunicationsSeed
{
    /// <summary>
    /// Service types sent by email, with their English templates, and the stub email provider. <paramref name="prefix"/>
    /// keeps each test class's static codes apart.
    /// </summary>
    public static async Task SeedEmailNotices(this TestDatabase database, Guid tenantId, DateOnly from, string prefix,
        params (string Type, string Subject, string Body)[] notices)
    {
        await database.SeedStatic(tenantId, "communication.cfg_channel_providers", "EFFECTIVE", $"{prefix}-PRV-EMAIL", from, ("channel", "EMAIL"),
            ("provider_code", "MAIL-1"), ("sender_identity", "noreply@insurer.test"), ("enabled", true));
        foreach (var (type, subject, body) in notices)
        {
            await database.SeedStatic(tenantId, "communication.cfg_notification_types", "EFFECTIVE", $"{prefix}-{type}", from, ("type_code", type), ("category", "SERVICE"),
                ("consent_purpose", DBNull.Value), ("channels", new[] { "EMAIL" }), ("portal_visible", true));
            await database.SeedStatic(tenantId, "communication.cfg_notification_templates", "EFFECTIVE", $"{prefix}-{type}-EMAIL-en", from, ("type_code", type),
                ("channel", "EMAIL"), ("language", "en"), ("subject", subject), ("body", body));
        }
    }

    /// <summary>A primary contact point for the party, written directly.</summary>
    public static Task AddContact(this TestDatabase database, Guid tenantId, Guid partyId, string channelType, string value) =>
        database.ExecuteAsOwner("""
            UPDATE party.contact_point SET is_primary = false WHERE tenant_id = @t AND party_id = @p AND channel_type = @c;
            INSERT INTO party.contact_point (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, party_id, channel_type, value, is_primary)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @p, @c, @v, true)
            """, ("t", tenantId), ("p", partyId), ("c", channelType), ("v", value));
}
