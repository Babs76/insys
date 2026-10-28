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
-- V0086: when a GL export was sent, and by whom (BR-046). Existing batches take the time they were created; their sender
-- stays unknown.
-- =====================================================================
ALTER TABLE finance.gl_export_batch ADD COLUMN sent_at timestamptz;
ALTER TABLE finance.gl_export_batch ADD COLUMN sent_by uuid;
UPDATE finance.gl_export_batch SET sent_at = created_at;
COMMENT ON COLUMN finance.gl_export_batch.sent_at IS 'BR-046: when the batch was sent to the general ledger.';
COMMENT ON COLUMN finance.gl_export_batch.sent_by IS 'BR-046: the user who sent it; null for a job.';
