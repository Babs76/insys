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
-- =====================================================================
-- V0098: GAP-113 / OQ-138 (Ade, 30 October 2026). Ongoing due diligence: what ends a periodic review.
--   - A customer risk class says how an expired party's review ends: PERSON (the default for every class), when a person
--     with the review function completes it through Party; or AUTOMATIC, on a fresh screening (the system is recorded as
--     the reviewer).
--   - The due diligence record keeps who completed the current review, and when.
--   - A party identifier may carry its document's expiry date; a review cannot complete while the identity check's
--     document has expired and no newer one of that kind is on file.
-- =====================================================================

ALTER TABLE party.cfg_customer_risk_classes ADD COLUMN review_mode varchar(9) NOT NULL DEFAULT 'PERSON';
ALTER TABLE party.cfg_customer_risk_classes ADD CONSTRAINT cfg_customer_risk_classes_ck3 CHECK (review_mode IN ('PERSON', 'AUTOMATIC'));
COMMENT ON COLUMN party.cfg_customer_risk_classes.review_mode IS 'review_mode: Enum{Person,Automatic}. How a periodic review of a party in this class ends: PERSON, by a person with the review function; AUTOMATIC, on a fresh screening (OQ-138). Sample: PERSON';

ALTER TABLE party.due_diligence ADD COLUMN reviewed_at timestamptz;
ALTER TABLE party.due_diligence ADD COLUMN reviewed_by uuid;
ALTER TABLE party.due_diligence ADD COLUMN review_due boolean NOT NULL DEFAULT false;
UPDATE party.due_diligence SET review_due = true WHERE status = 'EXPIRED';
ALTER TABLE party.due_diligence ADD CONSTRAINT ck_due_diligence_reviewed CHECK ((reviewed_at IS NULL) = (reviewed_by IS NULL));
COMMENT ON COLUMN party.due_diligence.review_due IS 'The party passed its review date and its periodic review has not ended (OQ-138). Set on expiry; cleared when the review ends.';
COMMENT ON COLUMN party.due_diligence.reviewed_by IS 'Who completed the current periodic review (OQ-138): a person, or the system requester for an AUTOMATIC class. Cleared when the party next expires.';

ALTER TABLE party.party_identifier ADD COLUMN expiry_date date;
COMMENT ON COLUMN party.party_identifier.expiry_date IS 'The identity document''s expiry date, when it has one (OQ-138). Optional: an identifier that does not expire has none.';
