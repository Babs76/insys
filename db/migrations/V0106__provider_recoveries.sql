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
-- V0106: Wave 5a batch 5a.3 (addenda/providers-and-care.md §4.6 to §4.8; F048 to F050). Provider credit notes, overpayment
-- recoveries and their offsets in payment runs, disputed recoveries held from offsets (OQ-146), and a claim that replaces a
-- reversed one. Business references and money only: no clinical data.
-- =====================================================================

-- OQ-139, addition 1: may this provider's overpayments be deducted from its payments? Null takes the tenant's default.
ALTER TABLE health.cfg_provider_contracts ADD COLUMN offset_allowed boolean;

CREATE TABLE health.cfg_provider_recovery_settings (
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
  medical_scheme                 boolean NOT NULL,
  review_after_days              integer NOT NULL,
  invoice_collection_method      varchar(15) NOT NULL,
  CONSTRAINT cfg_provider_recovery_settings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_provider_recovery_settings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_provider_recovery_settings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_provider_recovery_settings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_provider_recovery_settings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_provider_recovery_settings_ck1 CHECK (review_after_days BETWEEN 1 AND 3650),
  CONSTRAINT cfg_provider_recovery_settings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_provider_recovery_settings_lookup ON health.cfg_provider_recovery_settings (tenant_id, code, effective_from);
CREATE INDEX cfg_provider_recovery_settings_import ON health.cfg_provider_recovery_settings (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE health.cfg_provider_recovery_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_provider_recovery_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_provider_recovery_settings
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_provider_recovery_settings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_provider_recovery_settings IS 'Provider recovery settings. CFG, scope T, Tier 2. Whether the tenant is a medical scheme (the default of a provider contract''s offset flag, Medical Schemes Act s59(3), for compliance to confirm), the days before an uncollected recovery goes to a person (OQ-139), and the collection method of a recovery invoice.';


-- Reverse-and-reprocess: the new claim names the reversed one, shown both ways. One claim replaces another at most once.
ALTER TABLE health.medical_claim ADD COLUMN replaces_claim_id uuid;
ALTER TABLE health.medical_claim ADD COLUMN replaced_by_claim_id uuid;
CREATE UNIQUE INDEX uq_medical_claim_replaces ON health.medical_claim (tenant_id, replaces_claim_id) WHERE replaces_claim_id IS NOT NULL;

-- Gap 2: what a remittance owed, what was deducted, what was repaid; amount stays what is paid (gross - offset + repaid).
ALTER TABLE health.remittance_advice ADD COLUMN gross_amount numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE health.remittance_advice ADD COLUMN offset_amount numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE health.remittance_advice ADD COLUMN repaid_amount numeric(18,2) NOT NULL DEFAULT 0;
UPDATE health.remittance_advice SET gross_amount = amount;

CREATE TABLE health.provider_credit_note (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  credit_note_number         varchar(25) NOT NULL,
  provider_id                varchar(15) NOT NULL,
  provider_reference         varchar(40) NOT NULL,
  claim_id                   uuid,
  remittance_id              uuid,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(10) NOT NULL,
  reason                     varchar(500) NOT NULL,
  submitted_by               uuid NOT NULL,
  approval_id                uuid,
  decided_by                 uuid,
  decided_at                 timestamptz,
  recovery_id                uuid,
  CONSTRAINT pk_provider_credit_note PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_provider_credit_note_ref UNIQUE (tenant_id, provider_id, provider_reference),
  CONSTRAINT ck_provider_credit_note_status CHECK (status IN ('SUBMITTED', 'APPROVED', 'REJECTED')),
  CONSTRAINT ck_provider_credit_note_amount CHECK (amount > 0),
  CONSTRAINT ck_provider_credit_note_ref CHECK (claim_id IS NOT NULL OR remittance_id IS NOT NULL)
);

CREATE TABLE health.provider_recovery (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  recovery_number            varchar(25) NOT NULL,
  debtor_kind                varchar(10) NOT NULL,
  provider_id                varchar(15),
  debtor_party_id            uuid,
  source                     varchar(12) NOT NULL,
  source_id                  uuid,
  claim_id                   uuid,
  claim_number               varchar(25),
  original_amount            numeric(18,2) NOT NULL,
  balance                    numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  basis                      varchar(10) NOT NULL,
  status                     varchar(12) NOT NULL,
  reason                     varchar(500) NOT NULL,
  raised_by                  uuid,
  raised_on                  date NOT NULL,
  review_due_on              date,
  in_review                  boolean NOT NULL,
  held_by_case_id            uuid,
  invoice_id                 uuid,
  write_off_reason           varchar(500),
  write_off_requested_by     uuid,
  approval_id                uuid,
  decided_by                 uuid,
  repay_amount               numeric(18,2) NOT NULL,
  repaid_in_run_id           uuid,
  closed_on                  date,
  CONSTRAINT pk_provider_recovery PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_provider_recovery_source UNIQUE NULLS NOT DISTINCT (tenant_id, source, source_id),
  CONSTRAINT ck_provider_recovery_kind CHECK (debtor_kind IN ('PROVIDER', 'MEMBER')),
  CONSTRAINT ck_provider_recovery_source CHECK (source IN ('REVERSAL', 'CREDIT_NOTE', 'FINDING')),
  CONSTRAINT ck_provider_recovery_basis CHECK (basis IN ('OFFSET', 'INVOICE', 'PERSON')),
  CONSTRAINT ck_provider_recovery_status CHECK (status IN ('PROPOSED', 'OPEN', 'INVOICED', 'RECOVERED', 'WRITTEN_OFF', 'REVERSED', 'REJECTED')),
  CONSTRAINT ck_provider_recovery_balance CHECK (balance >= 0 AND balance <= original_amount),
  CONSTRAINT ck_provider_recovery_provider CHECK (debtor_kind <> 'PROVIDER' OR provider_id IS NOT NULL)
);
CREATE INDEX ix_provider_recovery_1 ON health.provider_recovery (tenant_id, provider_id, status, raised_on);
CREATE INDEX ix_provider_recovery_2 ON health.provider_recovery (tenant_id, claim_id) WHERE claim_id IS NOT NULL;
CREATE INDEX ix_provider_recovery_3 ON health.provider_recovery (tenant_id, review_due_on) WHERE status = 'OPEN' AND NOT in_review;

CREATE TABLE health.provider_recovery_offset (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  recovery_id                uuid NOT NULL,
  run_id                     uuid NOT NULL,
  remittance_id              uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_provider_recovery_offset PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_provider_recovery_offset FOREIGN KEY (tenant_id, recovery_id) REFERENCES health.provider_recovery (tenant_id, id),
  CONSTRAINT uq_provider_recovery_offset UNIQUE (tenant_id, recovery_id, run_id),
  CONSTRAINT ck_provider_recovery_offset_amount CHECK (amount > 0),
  CONSTRAINT ck_provider_recovery_offset_status CHECK (status IN ('RESERVED', 'TAKEN', 'RELEASED'))
);
CREATE INDEX ix_provider_recovery_offset_1 ON health.provider_recovery_offset (tenant_id, remittance_id);
CREATE INDEX ix_provider_recovery_offset_2 ON health.provider_recovery_offset (tenant_id, run_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['provider_credit_note', 'provider_recovery', 'provider_recovery_offset'] LOOP
    EXECUTE format('ALTER TABLE health.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE health.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON health.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
