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
-- V0155: DE-019 (Ade, 25 November 2026). Reading the legal holds on records (GET /documents/legal-holds and /{id}) moves from the privileged
-- DOCUMENTS_PLACE_LEGAL_HOLD to a read-only screen, DOCUMENTS_RECORD_HOLDS (legal_hold.read), which the compliance officer can hold without
-- placing or releasing holds. So whoever reads them today keeps reading, every role that grants DOCUMENTS_PLACE_LEGAL_HOLD now or
-- later also grants DOCUMENTS_RECORD_HOLDS, from its earliest such grant's dates, approved as this migration. Idempotent; the function itself
-- is added to the catalogue by R__function_catalogue.sql.
-- =====================================================================
INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, effective_to, record_status, created_by, approved_at, approved_by,
                                               change_reason, role_code, function_code)
SELECT DISTINCT ON (a.tenant_id, a.role_code)
       a.tenant_id, a.role_code || ':DOCUMENTS_RECORD_HOLDS', 1, a.effective_from, a.effective_to, a.record_status, 'migration (V0155)', now(), 'migration (V0155)',
       'Reading record legal holds moved to DOCUMENTS_RECORD_HOLDS (DE-019): kept for the role that read them through DOCUMENTS_PLACE_LEGAL_HOLD', a.role_code,
       'DOCUMENTS_RECORD_HOLDS'
FROM iam.cfg_role_function_assignments a
WHERE a.function_code = 'DOCUMENTS_PLACE_LEGAL_HOLD' AND a.tenant_id IS NOT NULL
  AND a.record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE') AND (a.effective_to IS NULL OR a.effective_to > current_date)
  AND NOT EXISTS (SELECT 1 FROM iam.cfg_role_function_assignments b
                  WHERE b.tenant_id = a.tenant_id AND b.code = a.role_code || ':DOCUMENTS_RECORD_HOLDS')
ORDER BY a.tenant_id, a.role_code, a.effective_from
ON CONFLICT DO NOTHING;
