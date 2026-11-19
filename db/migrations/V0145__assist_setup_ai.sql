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
-- V0145: Wave 7, batch 7.8. AI-3's tailoring and document mapping (AI Assist SRS §5A, AIA-082 to AIA-084, AIA-087, AIA-090), as Ade decided on
-- 19 November 2026 (OQ-186 to OQ-188), each value taking effect only once a person accepts it and a second person approves the SETUP change set.
--  * starter_pack_value: what tailoring may change and from which answers (OQ-187 (a)).
--  * setup_document: the documents a setup run reads, each with its uploader's confirmation (D-16, ai-assist.md §5.5), as AI-1's.
--  * setup_ai_run: one AI pass over a run's answers and documents, done by a job acting for the user who asked, as AI-1's.
--  * setup_item: a Tailored value cites its answer, a Mapped value its document, page and quote (AIA-084).
--  * setup_unmapped: what the AI couldn't map, and what INSYS's checks dropped, with why (AIA-083).
-- No document's text is kept but the quotes cited; the minimised payload sent is kept with the model call in assist.run_payload (SRS §7.2).
-- =====================================================================
ALTER TABLE assist.starter_pack_value
  ADD COLUMN tailor_columns text[],
  ADD COLUMN tailor_from text[],
  ADD CONSTRAINT ck_starter_pack_value_tailor CHECK ((tailor_columns IS NULL) = (tailor_from IS NULL) AND (tailor_columns IS NULL OR tailorable));
COMMENT ON COLUMN assist.starter_pack_value.tailor_columns IS 'The row''s columns AI may tailor (OQ-187 (a)); null when none.';
COMMENT ON COLUMN assist.starter_pack_value.tailor_from IS 'The questionnaire answers that may set them (OQ-187 (a)); null when none.';

CREATE TABLE assist.setup_ai_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  setup_run_id               uuid NOT NULL,
  answers_version            integer NOT NULL,
  status                     varchar(10) NOT NULL,
  requested_by               varchar(80) NOT NULL,
  requested_subject          varchar(255) NOT NULL,
  requested_at               timestamptz NOT NULL,
  correlation_id             uuid,
  started_at                 timestamptz,
  finished_at                timestamptz,
  outcome_code               varchar(60),
  assist_run_id              uuid,
  tailored                   integer,
  mapped                     integer,
  unmapped                   integer,
  CONSTRAINT pk_setup_ai_run PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_setup_ai_run_setup FOREIGN KEY (tenant_id, setup_run_id) REFERENCES assist.setup_run (tenant_id, id),
  CONSTRAINT ck_setup_ai_run_status CHECK (status IN ('QUEUED', 'RUNNING', 'DRAFTED', 'REFUSED', 'FAILED')),
  CONSTRAINT ck_setup_ai_run_outcome CHECK ((status IN ('REFUSED', 'FAILED')) = (outcome_code IS NOT NULL)),
  CONSTRAINT ck_setup_ai_run_started CHECK (status = 'QUEUED' OR started_at IS NOT NULL),
  CONSTRAINT ck_setup_ai_run_counts CHECK (status <> 'DRAFTED' OR (tailored IS NOT NULL AND mapped IS NOT NULL AND unmapped IS NOT NULL))
);
CREATE INDEX ix_setup_ai_run_open ON assist.setup_ai_run (tenant_id, requested_at) WHERE status IN ('QUEUED', 'RUNNING');
-- One pass at a time on a setup run.
CREATE UNIQUE INDEX uq_setup_ai_run_one_open ON assist.setup_ai_run (tenant_id, setup_run_id) WHERE status IN ('QUEUED', 'RUNNING');

CREATE TABLE assist.setup_document (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  setup_run_id               uuid NOT NULL,
  document_id                uuid NOT NULL,
  file_name                  varchar(255),
  sha256                     char(64) NOT NULL,
  size_bytes                 bigint NOT NULL,
  -- D-16: the uploader's confirmation that it holds no customer personal data, naming the person, the document (above) and the date.
  confirmed_by               varchar(80) NOT NULL,
  confirmed_at               timestamptz NOT NULL,
  -- The last pass's reading: the type from the content, unreadable pages, the identifier check (kind and page, never the value), a refusal.
  kind                       varchar(4),
  unreadable_pages           integer[] NOT NULL DEFAULT '{}',
  identifiers                jsonb,
  refusal_code               varchar(40),
  removed                    boolean NOT NULL DEFAULT false,
  CONSTRAINT pk_setup_document PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_setup_document UNIQUE (tenant_id, setup_run_id, document_id),
  CONSTRAINT fk_setup_document_run FOREIGN KEY (tenant_id, setup_run_id) REFERENCES assist.setup_run (tenant_id, id),
  CONSTRAINT ck_setup_document_kind CHECK (kind IS NULL OR kind IN ('PDF', 'DOCX', 'XLSX')),
  CONSTRAINT ck_setup_document_size CHECK (size_bytes BETWEEN 0 AND 20971520),
  CONSTRAINT ck_setup_document_sha CHECK (sha256 ~ '^[0-9a-f]{64}$')
);

ALTER TABLE assist.setup_item
  ADD COLUMN ai_run_id uuid,
  ADD COLUMN citation_document_id uuid,
  ADD COLUMN citation_page integer,
  ADD COLUMN citation_quote varchar(2000),
  ADD COLUMN cited_answer varchar(30),
  ADD COLUMN cited_answer_value varchar(200),
  -- AIA-084: Tailored cites its answer, Mapped its document, page and quote, and each comes from an AI pass; Pack and Missing cite neither.
  ADD CONSTRAINT ck_setup_item_tailored CHECK ((flag = 'TAILORED') = (cited_answer IS NOT NULL AND cited_answer_value IS NOT NULL)),
  ADD CONSTRAINT ck_setup_item_mapped CHECK ((flag = 'MAPPED') = (citation_document_id IS NOT NULL AND citation_page IS NOT NULL AND citation_quote IS NOT NULL)),
  ADD CONSTRAINT ck_setup_item_ai CHECK ((flag IN ('TAILORED', 'MAPPED')) = (ai_run_id IS NOT NULL)),
  -- AIA-082: a regulated value is never tailored; one is mapped only onto OQ-186's tables, as the tenant's own cited fact.
  ADD CONSTRAINT ck_setup_item_not_tailored_regulated CHECK (NOT (flag = 'TAILORED' AND regulated));
COMMENT ON COLUMN assist.setup_item.cited_answer IS 'A Tailored value''s questionnaire answer (OQ-187 (a)), as SetupAnswerFields names it.';
COMMENT ON COLUMN assist.setup_item.cited_answer_value IS 'The answer''s value the AI chose: the verifier checks it equals one the answer holds (AIA-087).';

CREATE TABLE assist.setup_unmapped (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  setup_run_id               uuid NOT NULL,
  ai_run_id                  uuid NOT NULL,
  document_id                uuid,
  page                       integer,
  quote                      varchar(2000),
  target_table               varchar(80),
  -- NOT_MAPPED: the AI listed it; otherwise the reason INSYS's checks dropped a proposed value.
  reason                     varchar(40) NOT NULL,
  CONSTRAINT pk_setup_unmapped PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_setup_unmapped_run FOREIGN KEY (tenant_id, ai_run_id) REFERENCES assist.setup_ai_run (tenant_id, id)
);
CREATE INDEX ix_setup_unmapped_run ON assist.setup_unmapped (tenant_id, setup_run_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['setup_ai_run', 'setup_document', 'setup_unmapped'] LOOP
    EXECUTE format('ALTER TABLE assist.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE assist.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON assist.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) '
      || 'WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
