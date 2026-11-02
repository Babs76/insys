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
-- V0129: Wave 7, batch 7.1. Run records and what was sent (SRS §7.2; AIA-051, AIA-073). A run is written once, when it ends; tokens are metered from it.
-- =====================================================================
CREATE TABLE assist.run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  feature                    varchar(20) NOT NULL,
  started_at                 timestamptz NOT NULL,
  finished_at                timestamptz NOT NULL,
  model_version              varchar(60) NOT NULL,
  prompt_version             varchar(40) NOT NULL,
  status                     varchar(12) NOT NULL,
  input_tokens               bigint NOT NULL,
  output_tokens              bigint NOT NULL,
  cost                       numeric(18,6),
  cost_currency              char(3),
  correlation_id             uuid,
  CONSTRAINT pk_run PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_run_feature CHECK (feature IN ('EXPLAIN_REFUSAL', 'EXPLAIN_PREMIUM', 'PRODUCT_DRAFT', 'TENANT_SETUP')),
  CONSTRAINT ck_run_status CHECK (status IN ('VERIFIED', 'REJECTED', 'UNAVAILABLE')),
  CONSTRAINT ck_run_tokens CHECK (input_tokens >= 0 AND output_tokens >= 0)
);
CREATE INDEX ix_run_month ON assist.run (tenant_id, started_at);
ALTER TABLE assist.run ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.run
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE assist.run_payload (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  payload_sent               text NOT NULL,
  raw_output                 text,
  masked                     jsonb NOT NULL,
  CONSTRAINT pk_run_payload PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_run_payload UNIQUE (tenant_id, run_id),
  CONSTRAINT fk_run_payload_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.run (tenant_id, id)
);
ALTER TABLE assist.run_payload ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.run_payload FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.run_payload
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
