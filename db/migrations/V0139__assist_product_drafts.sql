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
-- V0139: Wave 7, batch 7.4. AI-1's runs (AI Assist SRS §4, §7.2): a run for a product, the documents it reads with each uploader's confirmation (D-16,
-- ai-assist.md §5.5), and its proposed values (draft_item), each with its flag (AIA-014) and citation (AIA-013). The run's job acts for the user who
-- asked (AIA-017). The Draft version itself is Product Factory's; a run records only its number. These tables hold no document's text but the quotes
-- items cite; the minimised payload sent is kept with the model call in assist.run_payload, as for every run (SRS §7.2), under the tenant's retention.
-- =====================================================================

CREATE TABLE assist.product_draft_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  product_code               varchar(30) NOT NULL,
  status                     varchar(10) NOT NULL,
  requested_by               varchar(80) NOT NULL,
  requested_subject          varchar(255) NOT NULL,
  requested_at               timestamptz NOT NULL,
  correlation_id             uuid,
  started_at                 timestamptz,
  finished_at                timestamptz,
  outcome_code               varchar(60),
  draft_version              integer,
  assist_run_id              uuid,
  CONSTRAINT pk_product_draft_run PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_product_draft_run_status CHECK (status IN ('QUEUED', 'RUNNING', 'DRAFTED', 'REFUSED', 'FAILED')),
  CONSTRAINT ck_product_draft_run_drafted CHECK (status <> 'DRAFTED' OR draft_version IS NOT NULL),
  CONSTRAINT ck_product_draft_run_outcome CHECK ((status IN ('REFUSED', 'FAILED')) = (outcome_code IS NOT NULL)),
  CONSTRAINT ck_product_draft_run_started CHECK (status = 'QUEUED' OR started_at IS NOT NULL)
);
CREATE INDEX ix_product_draft_run_queued ON assist.product_draft_run (tenant_id, requested_at) WHERE status IN ('QUEUED', 'RUNNING');

CREATE TABLE assist.product_draft_document (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  document_id                uuid NOT NULL,
  file_name                  varchar(255),
  sha256                     char(64) NOT NULL,
  size_bytes                 bigint NOT NULL,
  -- D-16: the uploader's confirmation that it holds no customer personal data, naming the person, the document (above) and the date.
  confirmed_by               varchar(80) NOT NULL,
  confirmed_at               timestamptz NOT NULL,
  kind                       varchar(4),
  unreadable_pages           integer[] NOT NULL DEFAULT '{}',
  -- The identifier check (§5.5): each kind and page found, never the value.
  identifiers                jsonb,
  refusal_code               varchar(40),
  CONSTRAINT pk_product_draft_document PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_product_draft_document UNIQUE (tenant_id, run_id, document_id),
  CONSTRAINT fk_product_draft_document_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.product_draft_run (tenant_id, id),
  CONSTRAINT ck_product_draft_document_kind CHECK (kind IS NULL OR kind IN ('PDF', 'DOCX', 'XLSX')),
  CONSTRAINT ck_product_draft_document_size CHECK (size_bytes BETWEEN 0 AND 20971520),
  CONSTRAINT ck_product_draft_document_sha CHECK (sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TABLE assist.draft_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  kind                       varchar(10) NOT NULL,
  field_path                 varchar(160) NOT NULL,
  value                      jsonb,
  flag                       varchar(11) NOT NULL,
  citation_document_id       uuid,
  citation_page              integer,
  citation_quote             varchar(2000),
  check_result               varchar(7) NOT NULL,
  check_reason               varchar(40),
  in_draft                   boolean NOT NULL,
  CONSTRAINT pk_draft_item PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_draft_item_run FOREIGN KEY (tenant_id, run_id) REFERENCES assist.product_draft_run (tenant_id, id),
  CONSTRAINT ck_draft_item_kind CHECK (kind IN ('VALUE', 'RATE_TABLE', 'RULE', 'SIM_CASE')),
  CONSTRAINT ck_draft_item_flag CHECK (flag IN ('FOUND', 'INTERPRETED', 'MISSING')),
  CONSTRAINT ck_draft_item_check CHECK (check_result IN ('PASSED', 'DROPPED') AND ((check_result = 'DROPPED') = (check_reason IS NOT NULL))),
  -- AIA-013: a value kept carries its citation; a Missing one has neither value nor citation. A simulation case is cited only where the documents state its outcome (AIA-018).
  CONSTRAINT ck_draft_item_cited CHECK (flag = 'MISSING' OR check_result = 'DROPPED' OR kind = 'SIM_CASE'
    OR (citation_document_id IS NOT NULL AND citation_page IS NOT NULL AND citation_quote IS NOT NULL)),
  CONSTRAINT ck_draft_item_missing CHECK (flag <> 'MISSING' OR (value IS NULL AND citation_document_id IS NULL)),
  -- Only a kept value reaches the Draft, and never a rule, a rate table or a simulation case (OQ-184 (a), AIA-016).
  CONSTRAINT ck_draft_item_in_draft CHECK (NOT in_draft OR (kind = 'VALUE' AND check_result = 'PASSED' AND flag <> 'MISSING'))
);
CREATE INDEX ix_draft_item_run ON assist.draft_item (tenant_id, run_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['product_draft_run', 'product_draft_document', 'draft_item'] LOOP
    EXECUTE format('ALTER TABLE assist.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE assist.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON assist.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) '
      || 'WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
