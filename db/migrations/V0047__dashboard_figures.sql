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
-- V0047: dashboard figures (docs/specs/addenda/dashboard-figures.md, signed off 24 October 2026; GAP-046).
-- - Claims incurred are projected by branch (the claim's policy's organisation node), as written premium is, so a
--   branch-scoped user's figures are their branch's (decision 2). Rows projected before this keep the branch ALL, which
--   no branch-scoped user covers; a rebuild (POST /analytics/projections/rebuild) re-projects from the event log.
-- - A regulatory return definition may say within how many days after its period's end it must be submitted
--   (decision 4). Compliance sets it; no deadline is assumed where it is empty (OQ-083).
-- =====================================================================
ALTER TABLE analytics.fact_claims_incurred ADD COLUMN branch_code varchar(15) NOT NULL DEFAULT 'ALL';
ALTER TABLE analytics.fact_claims_incurred DROP CONSTRAINT pk_fact_claims_incurred;
ALTER TABLE analytics.fact_claims_incurred ADD CONSTRAINT pk_fact_claims_incurred PRIMARY KEY (tenant_id, month_key, product_code, cause_code, branch_code);

ALTER TABLE reporting.cfg_regulatory_return_definitions ADD COLUMN submission_days integer;
ALTER TABLE reporting.cfg_regulatory_return_definitions
  ADD CONSTRAINT cfg_regulatory_return_definitions_submission_days CHECK (submission_days IS NULL OR submission_days BETWEEN 0 AND 366);
COMMENT ON COLUMN reporting.cfg_regulatory_return_definitions.submission_days IS 'Days after the period end by which the return must be submitted; empty when not set (OQ-083).';
