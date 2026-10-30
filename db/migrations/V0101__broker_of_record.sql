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
-- V0101: Wave 4 batch 4.3c (addenda/renewals-and-retention.md §4.8; B-001; OQ-134 to OQ-136; Wave 4 addition 3). A broker of
-- record change: an INTERMEDIARY endorsement names the new intermediary and carries the policyholder's appointment letter;
-- approved, the policy moves at its effective date (a later date waits on the policy until the daily job moves it).
-- =====================================================================

ALTER TABLE general.endorsement ADD COLUMN new_intermediary_code varchar(15);
ALTER TABLE general.endorsement ADD COLUMN appointment_letter_id uuid;
COMMENT ON COLUMN general.endorsement.new_intermediary_code IS 'B-001: the intermediary an INTERMEDIARY endorsement moves the policy to, from its effective date.';
COMMENT ON COLUMN general.endorsement.appointment_letter_id IS 'B-001, Wave 4 addition 3: the policyholder''s broker appointment letter, a document filed against the endorsement, required before it is submitted.';

-- An INTERMEDIARY endorsement changes nothing in the cover, so it is rated at once at the current premium with no rating
-- request; every other endorsement past DRAFT still needs one (V0016).
ALTER TABLE general.endorsement DROP CONSTRAINT ck_endorsement_rated;
ALTER TABLE general.endorsement ADD CONSTRAINT ck_endorsement_rated CHECK (status = 'DRAFT' OR (new_annual_premium IS NOT NULL
  AND (rating_request_id IS NOT NULL OR (endorsement_type = 'INTERMEDIARY' AND new_intermediary_code IS NOT NULL))));

ALTER TABLE general.policy ADD COLUMN pending_intermediary_code varchar(15);
ALTER TABLE general.policy ADD COLUMN pending_intermediary_from date;
ALTER TABLE general.policy ADD CONSTRAINT ck_policy_pending_intermediary CHECK ((pending_intermediary_code IS NULL) = (pending_intermediary_from IS NULL));
COMMENT ON COLUMN general.policy.pending_intermediary_code IS 'B-001: an approved broker of record change waiting for its effective date (OQ-136: until then only the current broker sees the policy).';
