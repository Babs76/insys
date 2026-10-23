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
-- V0025: an explicit ended state for a revoked role assignment (Phase 8 Wave 0, GAP-020, IAM-005). A revoked
-- assignment is REVOKED, with when and by whom; only ACTIVE assignments give permissions, so it stops applying from
-- the next request. Before this, revocation only set valid_to = today and left the status ACTIVE.
-- =====================================================================
ALTER TABLE iam.user_role_assignment
  ADD COLUMN revoked_at timestamptz,
  ADD COLUMN revoked_by uuid;
ALTER TABLE iam.user_role_assignment DROP CONSTRAINT ck_user_role_assignment_status;
ALTER TABLE iam.user_role_assignment
  ADD CONSTRAINT ck_user_role_assignment_status CHECK (status IN ('PENDING_APPROVAL', 'ACTIVE', 'REJECTED', 'REVOKED')),
  ADD CONSTRAINT ck_user_role_assignment_revoked CHECK ((status = 'REVOKED') = (revoked_at IS NOT NULL)),
  ADD CONSTRAINT ck_user_role_assignment_revoked_by CHECK ((revoked_at IS NULL) = (revoked_by IS NULL));
