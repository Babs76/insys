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
-- V0134: Wave 7, batch 7.6. AI-3's tenant setup (AI Assist SRS §5A.3 and §5A.5): a run of the wizard, its questionnaire answers, each version kept
-- (AIA-081), and the draft assembled from the packs, one item per row (AIA-082, AIA-084). The run records the pack versions it used (AIA-089). Nothing here
-- changes the tenant's configuration: the SETUP change set (7.7) does, after a second person's approval.
CREATE TABLE assist.setup_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  status                     varchar(12) NOT NULL,
  started_by                 varchar(80) NOT NULL,
  answers_version            integer NOT NULL,
  pack_ids                   uuid[] NOT NULL,
  assembled_at               timestamptz NOT NULL,
  CONSTRAINT pk_setup_run PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_setup_run_status CHECK (status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'APPLIED', 'WITHDRAWN'))
);
ALTER TABLE assist.setup_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.setup_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.setup_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE assist.setup_answer (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  version                    integer NOT NULL,
  answers                    jsonb NOT NULL,
  CONSTRAINT pk_setup_answer PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_setup_answer_version UNIQUE (tenant_id, run_id, version),
  CONSTRAINT fk_setup_answer_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.setup_run (tenant_id, id)
);
ALTER TABLE assist.setup_answer ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.setup_answer FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.setup_answer
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE assist.setup_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  table_name                 varchar(80) NOT NULL,
  row_code                   varchar(120) NOT NULL,
  class                      varchar(12) NOT NULL,
  regulated                  boolean NOT NULL,
  flag                       varchar(10) NOT NULL,
  values                     jsonb,
  source_pack_id             uuid,
  source_line_no             integer,
  missing_reason             varchar(30),
  decision                   varchar(10),
  decided_by                 varchar(80),
  decided_at                 timestamptz,
  decision_note              varchar(500),
  answers_version            integer NOT NULL,
  CONSTRAINT pk_setup_item PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_setup_item_row UNIQUE (tenant_id, run_id, table_name, row_code),
  CONSTRAINT fk_setup_item_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.setup_run (tenant_id, id),
  CONSTRAINT ck_setup_item_flag CHECK (flag IN ('PACK', 'TAILORED', 'MAPPED', 'MISSING')),
  CONSTRAINT ck_setup_item_decision CHECK (decision IS NULL OR decision IN ('ACCEPTED', 'REJECTED')),
  CONSTRAINT ck_setup_item_missing CHECK ((flag = 'MISSING') = (missing_reason IS NOT NULL)),
  CONSTRAINT ck_setup_item_not_accepted_missing CHECK (NOT (flag = 'MISSING' AND decision = 'ACCEPTED'))
);
ALTER TABLE assist.setup_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.setup_item FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.setup_item
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN assist.setup_item.missing_reason IS 'NOT_APPROVED: a regulated value from an overlay not approved for this tenant (ASSIST_PACK_NOT_APPROVED). NO_VALUE: the pack leaves it empty.';
