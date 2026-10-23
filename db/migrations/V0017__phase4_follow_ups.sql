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
-- INSYS Core · Phase 4 review follow-ups
-- 1. An approval is bound to its subject: the requester's hash of exactly what is approved
--    (for a payment, the payee and a hash of their bank details) comes back with the decision.
-- 2. A claim payment instruction keeps the approved payee and bank-details hash; new details
--    wait for a new approval before the payment is released again.
-- 3. Policy periods include their end date (cover to the end of that day): a period may be one day.
-- 4. A referred quote may be declined (domain-model.json: Referred -> Declined).
-- 5. A product's cover starts at issue or at the first payment.
-- =====================================================================

ALTER TABLE workflow.approval ADD COLUMN subject_hash varchar(64);

ALTER TABLE general.claim ADD COLUMN settlement_payee_bank_hash varchar(64);

ALTER TABLE claims.claim_payment_instruction
  ADD COLUMN payee_bank_hash varchar(64),
  ADD COLUMN pending_payee_party_id uuid,
  ADD COLUMN pending_payee_bank_hash varchar(64),
  ADD COLUMN retry_requested_by uuid,
  ADD CONSTRAINT ck_claim_payment_instruction_pending CHECK (
    (pending_payee_party_id IS NULL) = (retry_requested_by IS NULL) AND (pending_payee_party_id IS NULL OR status = 'FAILED'));

ALTER TABLE general.quote DROP CONSTRAINT ck_quote_period, DROP CONSTRAINT ck_quote_status,
  ADD CONSTRAINT ck_quote_period CHECK (period_end IS NULL OR period_end >= period_start),
  ADD CONSTRAINT ck_quote_status CHECK (status IN ('DRAFT', 'RATED', 'REFERRED', 'ISSUED', 'ACCEPTED', 'REJECTED', 'EXPIRED', 'DECLINED'));
ALTER TABLE general.underwriting_referral ADD COLUMN decline_reason text;

ALTER TABLE general.policy DROP CONSTRAINT ck_policy_period,
  ADD CONSTRAINT ck_policy_period CHECK (period_end >= period_start),
  ADD COLUMN cover_starts_on varchar(15) NOT NULL DEFAULT 'ISSUE',
  ADD COLUMN first_paid_on date,
  ADD CONSTRAINT ck_policy_cover_starts_on CHECK (cover_starts_on IN ('ISSUE', 'FIRST_PAYMENT'));
ALTER TABLE general.policy ALTER COLUMN cover_starts_on DROP DEFAULT;
