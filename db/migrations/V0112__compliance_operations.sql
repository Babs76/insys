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
-- V0112: Wave 6 batch 6.2 (addenda/finance-and-compliance.md §4.4 to §4.7; F057, F059, F060, F064). What enforces a register entry; data-subject requests; disclosure
-- acknowledgements; a party's retention review. Retention periods stay in party.cfg_record_retention_policies (OQ-054).
-- =====================================================================

ALTER TABLE reporting.cfg_compliance_register_entries ADD COLUMN enforced_by text[];
ALTER TABLE party.party ADD COLUMN retention_review_raised_on date;

-- OQ-157 makes renewal a display point; the others were there: quote, issue (inception) and claim (its decision).
ALTER TABLE channel.cfg_disclosure_and_notice_texts DROP CONSTRAINT cfg_disclosure_and_notice_texts_ck1;
ALTER TABLE channel.cfg_disclosure_and_notice_texts ADD CONSTRAINT cfg_disclosure_and_notice_texts_ck1 CHECK (display_point IN ('QUOTE', 'PROPOSAL', 'ISSUE', 'CLAIM', 'RENEWAL'));

CREATE TABLE party.cfg_data_subject_settings (
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
  setting_code                   varchar(15) NOT NULL,
  response_days                  integer NOT NULL,
  CONSTRAINT cfg_data_subject_settings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_data_subject_settings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_data_subject_settings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_data_subject_settings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_data_subject_settings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_data_subject_settings_ck1 CHECK (response_days BETWEEN 1 AND 365),
  CONSTRAINT cfg_data_subject_settings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_data_subject_settings_lookup ON party.cfg_data_subject_settings (tenant_id, code, effective_from);
CREATE INDEX cfg_data_subject_settings_import ON party.cfg_data_subject_settings (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_data_subject_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_data_subject_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.cfg_data_subject_settings
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_data_subject_settings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_data_subject_settings IS 'Data-subject settings. CFG, scope T, Tier 2. Days to answer a request (F059, OQ-159); with none in force, 30.';

CREATE TABLE party.data_subject_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  request_number             varchar(25) NOT NULL,
  party_id                   uuid NOT NULL,
  request_type               varchar(12) NOT NULL,
  received_on                date NOT NULL,
  due_on                     date NOT NULL,
  received_by                uuid,
  status                     varchar(20) NOT NULL,
  identity_confirmed_by      uuid,
  approved_by                uuid,
  outcome                    varchar(12),
  outcome_reason             varchar(1000),
  CONSTRAINT pk_data_subject_request PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_data_subject_request_number UNIQUE (tenant_id, request_number),
  CONSTRAINT ck_data_subject_request_type CHECK (request_type IN ('ACCESS', 'CORRECTION', 'DELETION')),
  CONSTRAINT ck_data_subject_request_status CHECK (status IN ('RECEIVED', 'IDENTITY_CONFIRMED', 'APPROVED', 'COMPLETED', 'REFUSED'))
);
CREATE INDEX ix_data_subject_request_1 ON party.data_subject_request (tenant_id, party_id);
ALTER TABLE party.data_subject_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.data_subject_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.data_subject_request USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE channel.disclosure_acknowledgement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  party_id                   uuid NOT NULL,
  notice_type                varchar(20) NOT NULL,
  display_point              varchar(40) NOT NULL,
  text_version               integer NOT NULL,
  language                   varchar(80) NOT NULL,
  business_ref_type          varchar(30),
  business_ref_id            uuid,
  acknowledged_at            timestamptz NOT NULL,
  channel_code               varchar(40),
  recorded_by                varchar(80) NOT NULL,
  CONSTRAINT pk_disclosure_acknowledgement PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_disclosure_acknowledgement_1 ON channel.disclosure_acknowledgement (tenant_id, party_id, display_point);
ALTER TABLE channel.disclosure_acknowledgement ENABLE ROW LEVEL SECURITY;
ALTER TABLE channel.disclosure_acknowledgement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON channel.disclosure_acknowledgement USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
