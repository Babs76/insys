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
-- V0058: a granted approval its record would not take (D-5, extended 25 October 2026). The record changed after it was
-- submitted, so its module refuses the approval: the delivery parks (APPROVAL_NOT_APPLIED, ADR-022) with an alert, the
-- approval is NOT_APPLIED with the module that refused it and when, and the requester gets a follow-up task. A retry that
-- the record then takes puts the approval back to APPROVED.
-- =====================================================================

ALTER TABLE workflow.approval
  DROP CONSTRAINT ck_approval_status,
  DROP CONSTRAINT ck_approval_decided,
  ADD COLUMN not_applied_by varchar(100),
  ADD COLUMN not_applied_at timestamptz,
  ADD CONSTRAINT ck_approval_status CHECK (status IN ('REQUESTED', 'APPROVED', 'NOT_APPLIED', 'REJECTED', 'EXPIRED', 'WITHDRAWN')),
  ADD CONSTRAINT ck_approval_decided CHECK ((status IN ('APPROVED', 'NOT_APPLIED', 'REJECTED')) = (checker_id IS NOT NULL AND decided_at IS NOT NULL)),
  ADD CONSTRAINT ck_approval_not_applied CHECK ((status = 'NOT_APPLIED') = (not_applied_by IS NOT NULL AND not_applied_at IS NOT NULL));
COMMENT ON COLUMN workflow.approval.not_applied_by IS 'The consumer (module) that refused the granted approval, while it is NOT_APPLIED.';
COMMENT ON COLUMN workflow.approval.not_applied_at IS 'When the granted approval''s delivery was first parked as not applied.';
