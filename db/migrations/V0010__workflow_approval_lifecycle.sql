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
-- INSYS Core · workflow.approval can hold a pending approval (OQ-047)
-- V0001 makes checker_id, decision and decided_at NOT NULL, so only a decided
-- approval could be stored, while domain-model.json gives Approval the states
-- Requested, Approved, Rejected and Expired. Same approach as V0007 for
-- privileged access grants. Existing rows are decided approvals.
-- =====================================================================
ALTER TABLE workflow.approval
  ADD COLUMN status          varchar(12),
  ADD COLUMN requested_at    timestamptz,
  ADD COLUMN expires_at      timestamptz,
  ADD COLUMN currency        char(3),
  ADD COLUMN decision_reason text,
  ADD COLUMN row_version     bigint NOT NULL DEFAULT 1,
  ADD COLUMN updated_at      timestamptz,
  ADD COLUMN updated_by      varchar(80);
UPDATE workflow.approval
SET status = CASE WHEN decision = 'APPROVED' THEN 'APPROVED' ELSE 'REJECTED' END,
    requested_at = created_at, updated_at = decided_at, updated_by = created_by;
ALTER TABLE workflow.approval
  ALTER COLUMN status SET NOT NULL,
  ALTER COLUMN requested_at SET NOT NULL,
  ALTER COLUMN updated_at SET NOT NULL,
  ALTER COLUMN updated_by SET NOT NULL,
  ALTER COLUMN checker_id DROP NOT NULL,
  ALTER COLUMN decision DROP NOT NULL,
  ALTER COLUMN decided_at DROP NOT NULL,
  ADD CONSTRAINT ck_approval_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'EXPIRED')),
  ADD CONSTRAINT ck_approval_decided CHECK ((status IN ('APPROVED', 'REJECTED')) = (checker_id IS NOT NULL AND decided_at IS NOT NULL));
CREATE INDEX ix_approval_2 ON workflow.approval (tenant_id, status, expires_at);
