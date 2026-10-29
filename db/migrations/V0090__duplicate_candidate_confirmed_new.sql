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
-- V0090: a staff registration confirmed new over a possible duplicate is recorded as CONFIRMED_NEW (Ade, 29 October
-- 2026, OQ-127): the registrar's assertion, not a review finding. The status takes up to 20 characters.
-- =====================================================================
ALTER TABLE party.duplicate_candidate ALTER COLUMN status TYPE varchar(20);
UPDATE party.duplicate_candidate SET status = 'CONFIRMED_NEW' WHERE status = 'DISTINCT';
COMMENT ON COLUMN party.duplicate_candidate.status IS 'CONFIRMED_NEW: the registrar asserted the registered party (party_a_id) is not the matched one (party_b_id), with a reason in the audit trail (OQ-127).';
