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
-- AI Assist batch 7.2 (docs/specs/addenda/ai-assist.md §3, SRS §7.2): AI-2's explanations and the feedback on them (AIA-036, AIA-037).
-- Both are written once and only added to: insert-only for the module role (db/roles/module_grants.sql), like assist.run.
CREATE TABLE assist.explanation (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  kind                       varchar(10) NOT NULL,
  source_ref                 varchar(60) NOT NULL,
  cache_key                  char(64) NOT NULL,
  prompt_version             varchar(40) NOT NULL,
  model_version              varchar(60) NOT NULL,
  outcome                    varchar(12) NOT NULL,
  facts                      jsonb NOT NULL,
  text                       text,
  run_id                     uuid,
  reused_from                uuid,
  correlation_id             uuid,
  CONSTRAINT pk_explanation PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_explanation_kind CHECK (kind IN ('REFUSAL', 'PREMIUM')),
  CONSTRAINT ck_explanation_outcome CHECK (outcome IN ('EXPLAINED', 'REJECTED', 'UNAVAILABLE', 'NO_WORDING')),
  CONSTRAINT ck_explanation_text CHECK ((outcome = 'EXPLAINED') = (text IS NOT NULL)),
  CONSTRAINT fk_explanation_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.run (tenant_id, id),
  CONSTRAINT fk_explanation_reused FOREIGN KEY (tenant_id, reused_from) REFERENCES assist.explanation (tenant_id, id)
);
CREATE INDEX ix_explanation_cache ON assist.explanation (tenant_id, cache_key) WHERE outcome = 'EXPLAINED' AND reused_from IS NULL;
ALTER TABLE assist.explanation ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.explanation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.explanation
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN assist.explanation.facts IS 'The structured facts the explanation was built from (D-15): codes, amounts with currency, numbers, dates and the error catalogue''s wording; never free text.';

CREATE TABLE assist.explanation_feedback (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  explanation_id             uuid NOT NULL,
  helpful                    boolean NOT NULL,
  reason                     varchar(12),
  comment                    varchar(500),
  CONSTRAINT pk_explanation_feedback PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_explanation_feedback_reason CHECK (reason IS NULL OR reason IN ('WRONG', 'UNCLEAR', 'INCOMPLETE', 'OTHER')),
  CONSTRAINT ck_explanation_feedback_unhelpful CHECK (helpful OR reason IS NOT NULL),
  CONSTRAINT fk_explanation_feedback FOREIGN KEY (tenant_id, explanation_id) REFERENCES assist.explanation (tenant_id, id)
);
ALTER TABLE assist.explanation_feedback ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.explanation_feedback FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.explanation_feedback
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN assist.explanation_feedback.comment IS 'The user''s own words, for the evaluation team only: never sent to a model (D-15).';
