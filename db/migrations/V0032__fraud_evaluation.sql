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
-- V0032: fraud evaluations (Phase 8 Wave 0, F009). Each screening of a claim against the fraud indicators in force,
-- with its score and the indicators it skipped and why: MISSING_FACTS with the facts' names, or EVALUATION_FAILED with
-- the error code. Names and codes only, never a fact's value. Append-only, like claims.fraud_indicator_hit.
-- =====================================================================
CREATE TABLE claims.fraud_evaluation (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  evaluated_at               timestamptz NOT NULL,
  score                      integer NOT NULL,
  referred                   boolean NOT NULL,
  indicators_evaluated       smallint NOT NULL,
  skipped                    jsonb NOT NULL,
  CONSTRAINT pk_fraud_evaluation PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_fraud_evaluation_counts CHECK (score >= 0 AND indicators_evaluated >= 0),
  CONSTRAINT ck_fraud_evaluation_skipped CHECK (jsonb_typeof(skipped) = 'array')
);
CREATE INDEX ix_fraud_evaluation_claim ON claims.fraud_evaluation (tenant_id, claim_number, evaluated_at);
ALTER TABLE claims.fraud_evaluation ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.fraud_evaluation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.fraud_evaluation
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
