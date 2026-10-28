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
-- V0080: a premium schedule that is a share of an annual premium (a General policy) keeps it, so its instalments total it
-- exactly: the last instalment takes the rounding (Ade, 28 October 2026). Schedules of a fixed instalment (Life, Health,
-- by hand) have none. Existing schedules are left as they are.
-- =====================================================================
ALTER TABLE billing.premium_schedule ADD COLUMN annual_premium numeric(18,2);
ALTER TABLE billing.premium_schedule ADD CONSTRAINT ck_premium_schedule_annual_premium CHECK (annual_premium IS NULL OR annual_premium > 0);

COMMENT ON COLUMN billing.premium_schedule.annual_premium IS 'The annual premium the instalments share (a General policy); they then total their share of it exactly, the last taking the rounding. Null for a fixed instalment.';
