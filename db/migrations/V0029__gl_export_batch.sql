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
-- V0029: the general ledger export (Phase 8 Wave 0, F055; SRS §15.1). finance.gl_export_batch (V0001) gets its status
-- values and a reference from the target system. The database specification's example status, ACKNOWLEDGED, is longer
-- than V0001's varchar(10), so the column is widened to keep the specification's value.
-- =====================================================================
ALTER TABLE finance.gl_export_batch ALTER COLUMN status TYPE varchar(12);
ALTER TABLE finance.gl_export_batch
  ADD COLUMN target_ref varchar(40),
  ADD COLUMN rejection_reason varchar(200),
  ADD CONSTRAINT ck_gl_export_batch_status CHECK (status IN ('SENT', 'ACKNOWLEDGED', 'REJECTED')),
  ADD CONSTRAINT ck_gl_export_batch_dates CHECK (to_date >= from_date),
  ADD CONSTRAINT ck_gl_export_batch_lines CHECK (line_count >= 0);
CREATE INDEX ix_gl_export_batch_layout ON finance.gl_export_batch (tenant_id, layout_code, from_date);
