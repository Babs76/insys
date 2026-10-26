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
-- V0063: releasing a legal hold needs a second person (OQ-111, decided 25 October 2026), so no one person can release a
-- hold and dispose of what it protected. A release is a request (who, when, why) that waits for a LEGAL_HOLD_RELEASE
-- approval; the hold stays in force until it is approved, and a rejected, expired or withdrawn approval clears the request.
-- =====================================================================

ALTER TABLE document.legal_hold
  ADD COLUMN released_by uuid,
  ADD COLUMN release_approval_id uuid,
  ADD COLUMN release_requested_by uuid,
  ADD COLUMN release_requested_at timestamptz,
  ADD COLUMN release_reason varchar(200),
  ADD CONSTRAINT ck_legal_hold_release_request CHECK (
    (release_requested_by IS NULL) = (release_requested_at IS NULL) AND (release_requested_by IS NULL) = (release_reason IS NULL)),
  ADD CONSTRAINT ck_legal_hold_release_pending CHECK (released_at IS NULL OR release_requested_by IS NULL);
COMMENT ON COLUMN document.legal_hold.released_by IS 'OQ-111: the checker who approved the release (null for holds released before it).';
COMMENT ON COLUMN document.legal_hold.release_approval_id IS 'OQ-111: the LEGAL_HOLD_RELEASE approval that released the hold.';
COMMENT ON COLUMN document.legal_hold.release_requested_by IS 'OQ-111: who asked for the release, while it waits for approval; the hold is in force meanwhile.';

ALTER TABLE party.search_log_hold
  ADD COLUMN release_approval_id uuid,
  ADD COLUMN release_requested_by uuid,
  ADD COLUMN release_requested_at timestamptz,
  ADD COLUMN release_reason varchar(200),
  ADD CONSTRAINT ck_search_log_hold_release_request CHECK (
    (release_requested_by IS NULL) = (release_requested_at IS NULL) AND (release_requested_by IS NULL) = (release_reason IS NULL)),
  ADD CONSTRAINT ck_search_log_hold_release_pending CHECK (released_at IS NULL OR release_requested_by IS NULL);
COMMENT ON COLUMN party.search_log_hold.release_approval_id IS 'OQ-111: the LEGAL_HOLD_RELEASE approval that released the hold.';
COMMENT ON COLUMN party.search_log_hold.release_requested_by IS 'OQ-111: who asked for the release, while it waits for approval; the hold is in force meanwhile.';
