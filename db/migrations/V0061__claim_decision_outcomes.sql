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
-- V0061: a claim's decision outcome (OQ-102, Wave 2). A partial approval is its own outcome: settled or approved below
-- the amount claimed, with a decision reason of type PARTIAL; its declined part sends CLAIM_DECISION_RIGHTS.
-- General and Life keep the amount claimed they compare against; Health's partial approval is an adjudicated claim with
-- declined lines, so it keeps the reason code that names why.
-- =====================================================================
ALTER TABLE general.claim
  ADD COLUMN claimed_amount numeric(18,2),
  ADD COLUMN decision_outcome varchar(20),
  ADD CONSTRAINT ck_claim_decision_outcome CHECK (decision_outcome IS NULL OR decision_outcome IN ('APPROVED', 'PARTLY_APPROVED', 'DECLINED'));
ALTER TABLE life.claim
  ADD COLUMN claimed_amount numeric(18,2),
  ADD COLUMN decision_outcome varchar(20),
  ADD CONSTRAINT ck_claim_decision_outcome CHECK (decision_outcome IS NULL OR decision_outcome IN ('APPROVED', 'PARTLY_APPROVED', 'DECLINED'));
ALTER TABLE health.medical_claim
  ADD COLUMN decision_outcome varchar(20),
  ADD COLUMN decision_reason_code varchar(20),
  ADD CONSTRAINT ck_medical_claim_decision_outcome CHECK (decision_outcome IS NULL OR decision_outcome IN ('APPROVED', 'PARTLY_APPROVED')),
  ADD CONSTRAINT ck_medical_claim_partial_reason CHECK (decision_outcome IS DISTINCT FROM 'PARTLY_APPROVED' OR decision_reason_code IS NOT NULL);
COMMENT ON COLUMN general.claim.claimed_amount IS 'OQ-102: the accepted assessment less the excess, which a settlement below is a partial approval of (General records no amount claimed).';
COMMENT ON COLUMN life.claim.claimed_amount IS 'OQ-102: the sum assured of the claimed benefits, set at assessment.';
