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
-- V0082: the instalment loading carries through (Ade, 28 October 2026). A General policy keeps the frequency and loading
-- its quote was priced in, and Billing charges the instalments as quoted: each with the loading, the year totalling the
-- loaded annual premium exactly (the last instalment taking the rounding). The schedule keeps the loading for amendments.
-- =====================================================================
ALTER TABLE general.policy ADD COLUMN instalment_loading_pct numeric(7,4);
ALTER TABLE general.policy ADD CONSTRAINT ck_policy_instalment_loading CHECK (instalment_loading_pct IS NULL OR instalment_loading_pct > 0);
COMMENT ON COLUMN general.policy.instalment_loading_pct IS 'The instalment loading in per cent the policy was quoted with in its billing frequency; null when none.';

ALTER TABLE billing.premium_schedule ADD COLUMN loading_pct numeric(7,4);
ALTER TABLE billing.premium_schedule ADD CONSTRAINT ck_premium_schedule_loading CHECK (loading_pct IS NULL OR loading_pct > 0);
COMMENT ON COLUMN billing.premium_schedule.loading_pct IS 'The instalment loading in per cent of the policy billed, kept for its amendments; annual_premium already includes it.';
