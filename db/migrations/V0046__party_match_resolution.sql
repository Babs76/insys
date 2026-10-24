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
-- V0046: staff resolve a portal registration that matched an existing party (external-user access §6, Wave 0b batch 6).
-- Staff propose to link the registration to the existing party or to confirm it as new; a second person approves the
-- proposal in Workflow (PARTY_MATCH_RESOLUTION, SRS-002). A link merges the registration into the existing party
-- (PTY-004). Rejected, the registration goes back to review.
-- - matched_party_id: the existing party the registration matched; null for registrations made before this migration,
--   for which staff name the party when they propose a link.
-- - match_resolution: LINK or NEW while a proposal waits for approval, and after it is approved.
-- =====================================================================
ALTER TABLE party.party ADD COLUMN matched_party_id uuid;
ALTER TABLE party.party ADD COLUMN match_resolution varchar(4);
ALTER TABLE party.party ADD COLUMN resolution_requested_by uuid;
ALTER TABLE party.party
  ADD CONSTRAINT ck_party_review_status CHECK (review_status IS NULL OR review_status IN ('PENDING_REVIEW', 'PROPOSED', 'LINKED', 'CONFIRMED_NEW')),
  ADD CONSTRAINT ck_party_match_resolution CHECK (match_resolution IS NULL OR match_resolution IN ('LINK', 'NEW'));
COMMENT ON COLUMN party.party.matched_party_id IS 'The existing party a portal registration matched (external-user access §6).';
COMMENT ON COLUMN party.party.match_resolution IS 'LINK or NEW: the staff resolution of a matched portal registration, proposed and then approved.';
