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
-- V0137: abandoning an approved SETUP change set that can't finish applying (OQ-180, Ade, 17 November 2026). One person asks, with a reason; a
-- different person approves (the database refuses the same person too). The batches already applied stay applied and listed on the set; the rest are
-- discarded. Only while the tenant is onboarding; activation stays blocked until a new set completes.
-- =====================================================================
ALTER TABLE sdm.setup_change_set
  ADD COLUMN abandon_requested_by uuid,
  ADD COLUMN abandon_reason varchar(500),
  ADD COLUMN abandon_requested_at timestamptz,
  ADD COLUMN abandoned_by uuid,
  ADD COLUMN abandoned_at timestamptz;
ALTER TABLE sdm.setup_change_set DROP CONSTRAINT ck_setup_change_set_status;
ALTER TABLE sdm.setup_change_set ADD CONSTRAINT ck_setup_change_set_status CHECK (status IN ('SUBMITTED', 'APPLYING', 'APPLIED', 'NOT_APPROVED', 'ABANDONED'));
ALTER TABLE sdm.setup_change_set DROP CONSTRAINT ck_setup_change_set_approved;
ALTER TABLE sdm.setup_change_set ADD CONSTRAINT ck_setup_change_set_approved
  CHECK ((status IN ('APPLYING', 'APPLIED', 'ABANDONED')) = (approved_at IS NOT NULL AND checker_id IS NOT NULL));
ALTER TABLE sdm.setup_change_set ADD CONSTRAINT ck_setup_change_set_abandon_request
  CHECK ((abandon_requested_by IS NULL) = (abandon_reason IS NULL) AND (abandon_requested_by IS NULL) = (abandon_requested_at IS NULL));
ALTER TABLE sdm.setup_change_set ADD CONSTRAINT ck_setup_change_set_abandoned
  CHECK ((status = 'ABANDONED') = (abandoned_by IS NOT NULL AND abandoned_at IS NOT NULL AND abandon_requested_by IS NOT NULL)
         AND (abandoned_by IS NULL OR abandoned_by <> abandon_requested_by));
COMMENT ON COLUMN sdm.setup_change_set.abandon_requested_by IS 'OQ-180: who asked to abandon the set, with abandon_reason; cleared if a second person declines.';
COMMENT ON COLUMN sdm.setup_change_set.abandoned_by IS 'OQ-180: who approved abandoning it, never whoever asked.';
