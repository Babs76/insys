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
-- V0161: OQ-201 (Ade, 26 November 2026). The tenant's disclosure setting stays the default until a change is granted, not from its first request: a
-- first request that is rejected, expires or is withdrawn leaves it the default. The row a first request creates is kept (it carries the pending change
-- and the row version), so whether the tenant has a setting of its own is a column, set when a grant applies.
-- =====================================================================
ALTER TABLE channel.disclosure_setting ADD COLUMN is_default boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN channel.disclosure_setting.is_default IS 'OQ-201: true until a change is granted (DISCLOSURE_SETTING_CHANGE); a request alone, or one rejected, expired or withdrawn, leaves it true';

-- Rows from before: a value of false was granted (only a grant changes it). A true value may have been granted too (false then true, or true asked for
-- while true), which the row can't tell; it is taken as the default, the setting's value being the same either way.
UPDATE channel.disclosure_setting SET is_default = false WHERE NOT broker_acknowledgement_allowed;
