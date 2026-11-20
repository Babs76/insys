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
-- V0148: OQ-196, option (B) (Ade, 20 November 2026): a case's CLAIM link records the claim's line, GENERAL, LIFE or HEALTH, so the claim
-- screens and the Health read rule (addenda/cases.md §9) can tell a Health claim from a General or Life one. Workflow learns the line
-- through the claim modules' public query (ClaimsFramework.Contracts.IClaimLineSource) when the link is added. Existing links are filled in
-- by the CASE_TIMELINES job through the same query (ADR-001: no join to another schema), so there is no backfill here. A CLAIM link whose
-- line is null is treated as Health (fail closed).
-- =====================================================================

ALTER TABLE workflow.case_link ADD COLUMN claim_line varchar(10) NULL;
ALTER TABLE workflow.case_link ADD CONSTRAINT ck_case_link_claim_line
  CHECK (claim_line IS NULL OR (link_type = 'CLAIM' AND claim_line IN ('GENERAL', 'LIFE', 'HEALTH')));
CREATE INDEX ix_case_link_2 ON workflow.case_link (tenant_id, case_id) WHERE link_type = 'CLAIM' AND claim_line IS NULL;

COMMENT ON COLUMN workflow.case_link.claim_line IS 'OQ-196 (B): a CLAIM link''s line (GENERAL, LIFE or HEALTH), from the claim modules'' public query; null until known, and a CLAIM link with a null line is treated as Health.';
