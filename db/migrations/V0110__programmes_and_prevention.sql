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
-- V0110: Wave 5b batch 5b.4 (addenda/benefit-programmes.md §3, §4.4, §4.5; F086 to F088). Disease programmes and enrolment, and preventive
-- schedules. A programme names a condition, so an enrolment is clinical. A schedule's last use is read from claims, so it isn't stored.
-- =====================================================================

CREATE TABLE health.cfg_disease_programmes (
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
  programme_code                 varchar(15) NOT NULL,
  condition_codes                text[] NOT NULL,
  benefit_category               varchar(15) NOT NULL,
  review_interval                varchar(10) NOT NULL,
  CONSTRAINT cfg_disease_programmes_pk PRIMARY KEY (id),
  CONSTRAINT cfg_disease_programmes_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_disease_programmes_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_disease_programmes_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_disease_programmes_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_disease_programmes_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_disease_programmes_lookup ON health.cfg_disease_programmes (tenant_id, code, effective_from);
CREATE INDEX cfg_disease_programmes_import ON health.cfg_disease_programmes (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE health.cfg_disease_programmes ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_disease_programmes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_disease_programmes
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_disease_programmes
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_disease_programmes IS 'Disease programmes. CFG, scope T, Tier 2. A programme, the conditions it covers, its benefit category and review interval (F086).';

CREATE TABLE health.cfg_preventive_schedules (
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
  schedule_code                  varchar(15) NOT NULL,
  plan_code                      varchar(15),
  item_kind                      varchar(10) NOT NULL,
  service_code                   varchar(20) NOT NULL,
  benefit_category               varchar(15) NOT NULL,
  min_age                        integer,
  max_age                        integer,
  sex                            varchar(10),
  service_interval               varchar(10) NOT NULL,
  no_copay                       boolean NOT NULL,
  CONSTRAINT cfg_preventive_schedules_pk PRIMARY KEY (id),
  CONSTRAINT cfg_preventive_schedules_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_preventive_schedules_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_preventive_schedules_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_preventive_schedules_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_preventive_schedules_ck1 CHECK (item_kind IN ('SCREENING', 'VACCINE')),
  CONSTRAINT cfg_preventive_schedules_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_preventive_schedules_lookup ON health.cfg_preventive_schedules (tenant_id, code, effective_from);
CREATE INDEX cfg_preventive_schedules_import ON health.cfg_preventive_schedules (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE health.cfg_preventive_schedules ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_preventive_schedules FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_preventive_schedules
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_preventive_schedules
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_preventive_schedules IS 'Preventive schedules. CFG, scope T, Tier 2. A screening or vaccine: who it is for, how often, and its benefit category (F087, F088).';

CREATE TABLE health.programme_enrolment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  enrolment_number           varchar(25) NOT NULL,
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  programme_code             varchar(15) NOT NULL,
  complex_case_id            uuid,
  status                     varchar(10) NOT NULL,
  enrolled_on                date NOT NULL,
  ended_on                   date,
  next_review_on             date NOT NULL,
  review_raised_for          date,
  enrolled_by                uuid NOT NULL,
  CONSTRAINT pk_programme_enrolment PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_programme_enrolment_number UNIQUE (tenant_id, enrolment_number),
  CONSTRAINT ck_programme_enrolment_status CHECK (status IN ('ACTIVE', 'ENDED'))
);
CREATE INDEX ix_programme_enrolment_1 ON health.programme_enrolment (tenant_id, patient_party_id, status);
CREATE INDEX ix_programme_enrolment_2 ON health.programme_enrolment (tenant_id, next_review_on) WHERE status = 'ACTIVE';
ALTER TABLE health.programme_enrolment ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.programme_enrolment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.programme_enrolment USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
