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
-- V0104: Wave 5a batch 5a.1 (addenda/providers-and-care.md §4.1, §4.2; F028 to F030). Provider measures and their monthly
-- results, a person's quality reviews, and a provider's statements matched against its claims. No clinical data.
-- =====================================================================

CREATE TABLE health.cfg_provider_measures (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  import_batch_id                uuid,
  measure_code                   varchar(25) NOT NULL,
  provider_type                  varchar(40),
  threshold                      numeric(18,4) NOT NULL,
  direction                      varchar(6) NOT NULL,
  min_claims                     integer NOT NULL,
  CONSTRAINT cfg_provider_measures_pk PRIMARY KEY (id),
  CONSTRAINT cfg_provider_measures_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_provider_measures_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_provider_measures_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_provider_measures_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_provider_measures_ck1 CHECK (measure_code IN ('CLAIMS_SUBMITTED', 'REJECTION_RATE', 'AVERAGE_PAID', 'DECISION_DAYS', 'REPEAT_SERVICE_RATE', 'DISPUTES_PER_100')),
  CONSTRAINT cfg_provider_measures_ck2 CHECK (direction IN ('ABOVE', 'BELOW')),
  CONSTRAINT cfg_provider_measures_ck3 CHECK (min_claims BETWEEN 0 AND 1000000),
  CONSTRAINT cfg_provider_measures_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_provider_measures_lookup ON health.cfg_provider_measures (tenant_id, code, effective_from);
CREATE INDEX cfg_provider_measures_import ON health.cfg_provider_measures (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE health.cfg_provider_measures ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_provider_measures FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_provider_measures
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_provider_measures
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_provider_measures IS 'Provider measures. CFG, scope T, Tier 2. A measure the tenant watches, with its threshold (F028, F029, OQ-141).';

CREATE TABLE health.provider_measure_result (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_id                varchar(15) NOT NULL,
  month_key                  char(7) NOT NULL,
  measure_code               varchar(25) NOT NULL,
  value                      numeric(18,4) NOT NULL,
  claim_count                integer NOT NULL,
  threshold                  numeric(18,4),
  judged                     boolean NOT NULL,
  breached                   boolean NOT NULL,
  CONSTRAINT pk_provider_measure_result PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_provider_measure_result UNIQUE (tenant_id, provider_id, month_key, measure_code),
  CONSTRAINT ck_provider_measure_result_breach CHECK (judged OR NOT breached)
);

CREATE TABLE health.provider_quality_review (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_id                varchar(15) NOT NULL,
  reviewed_on                date NOT NULL,
  score                      integer NOT NULL,
  reason                     varchar(500) NOT NULL,
  reviewed_by                uuid NOT NULL,
  CONSTRAINT pk_provider_quality_review PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_provider_quality_review_score CHECK (score BETWEEN 1 AND 5)
);
CREATE INDEX ix_provider_quality_review_1 ON health.provider_quality_review (tenant_id, provider_id, reviewed_on);

CREATE TABLE health.provider_statement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_id                varchar(15) NOT NULL,
  statement_ref              varchar(40) NOT NULL,
  period_from                date NOT NULL,
  period_to                  date NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_provider_statement PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_provider_statement UNIQUE (tenant_id, provider_id, statement_ref),
  CONSTRAINT ck_provider_statement_status CHECK (status IN ('EXCEPTIONS', 'RECONCILED')),
  CONSTRAINT ck_provider_statement_period CHECK (period_to >= period_from)
);

CREATE TABLE health.provider_statement_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  statement_id               uuid NOT NULL,
  reference                  varchar(25) NOT NULL,
  statement_amount           numeric(18,2),
  our_amount                 numeric(18,2),
  match_result               varchar(20) NOT NULL,
  resolution                 varchar(500),
  resolved_by                uuid,
  resolved_at                timestamptz,
  CONSTRAINT pk_provider_statement_line PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_provider_statement_line FOREIGN KEY (tenant_id, statement_id) REFERENCES health.provider_statement (tenant_id, id),
  CONSTRAINT ck_provider_statement_line_match CHECK (match_result IN ('MATCHED', 'AMOUNT_DIFFERS', 'NOT_ON_OUR_RECORDS', 'NOT_ON_STATEMENT'))
);
CREATE INDEX ix_provider_statement_line_1 ON health.provider_statement_line (tenant_id, statement_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['provider_measure_result', 'provider_quality_review', 'provider_statement', 'provider_statement_line'] LOOP
    EXECUTE format('ALTER TABLE health.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE health.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON health.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
