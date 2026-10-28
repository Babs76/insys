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
-- V0068: the case's own single-person legal hold is retired (Ade, 28 October 2026). A case is held only by a record hold
-- with scope CASE (D-34, V0066), which a second person must approve releasing. Every case hold still in force becomes a
-- record hold on its case, placed by whoever placed it and when (the case's latest LEGAL_HOLD_PLACED event), with the
-- reason code CASE_HOLD; the case's own flag is then cleared. Nothing held is released by the change.
-- =====================================================================

INSERT INTO document.record_hold (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, scope_type, scope_id, reason_code,
                                  placed_by, placed_at)
SELECT c.tenant_id, gen_random_uuid(), 1, now(), 'V0068', now(), 'V0068', 'CASE', c.id, 'CASE_HOLD',
       CASE WHEN placed.actor ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN placed.actor::uuid
            ELSE '00000000-0000-0000-0000-000000000000'::uuid END,
       COALESCE(placed.occurred_at, now())
FROM workflow.service_case c
LEFT JOIN LATERAL (
    SELECT e.actor, e.occurred_at FROM workflow.case_event e
    WHERE e.tenant_id = c.tenant_id AND e.case_id = c.id AND e.event_type = 'LEGAL_HOLD_PLACED'
    ORDER BY e.occurred_at DESC LIMIT 1) placed ON true
WHERE c.legal_hold;

UPDATE workflow.service_case SET legal_hold = false WHERE legal_hold;

COMMENT ON COLUMN workflow.service_case.legal_hold IS
  'Retired by V0068: a case is held by a record hold with scope CASE (document.record_hold); always false.';
