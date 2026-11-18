/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Migrations
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
-- =====================================================================
-- V0141: a notification whose provider or template is withdrawn between queueing and sending waits for configuration (ADR-022's rule, Ade,
-- 18 November 2026), rather than being refused for good. It stays QUEUED and names what it waits for; it is tried again when configuration is
-- published in the tenant, or every 15 minutes.
-- =====================================================================
ALTER TABLE communication.notification
  ADD COLUMN waiting_for varchar(60),
  ADD COLUMN waiting_item varchar(80);
COMMENT ON COLUMN communication.notification.waiting_for IS 'The configuration a queued notification waits for (CHANNEL_PROVIDER_NOT_CONFIGURED, NOTIFICATION_TEMPLATE_NOT_IN_FORCE), or NULL.';
COMMENT ON COLUMN communication.notification.waiting_item IS 'What is missing: type and channel ("CLAIM_STATUS:EMAIL"). Never a party or address.';
CREATE INDEX ix_notification_waiting ON communication.notification (tenant_id) WHERE waiting_for IS NOT NULL;
