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
-- V0107: Wave 5b batch 5b.1 (addenda/benefit-programmes.md §3, §4.1; F039 to F045). A pharmacy line is a medical claim line with
-- more on it: its tariff_code holds the product code. Clinical: shown only through the audited clinical read (HLT-008).
-- =====================================================================

ALTER TABLE health.medical_claim_line ALTER COLUMN tariff_code TYPE varchar(20);
ALTER TABLE health.medical_claim_line ADD COLUMN is_pharmacy boolean NOT NULL DEFAULT false;
ALTER TABLE health.medical_claim_line ADD COLUMN days_supply smallint;
ALTER TABLE health.medical_claim_line ADD COLUMN prescription_number varchar(30);
ALTER TABLE health.medical_claim_line ADD COLUMN prescriber_ref varchar(30);
ALTER TABLE health.medical_claim_line ADD COLUMN prescribed_on date;
ALTER TABLE health.medical_claim_line ADD COLUMN repeat_number smallint;
ALTER TABLE health.medical_claim_line ADD COLUMN repeats_authorised smallint;
ALTER TABLE health.medical_claim_line ADD COLUMN paid_quantity smallint;
ALTER TABLE health.medical_claim_line ADD COLUMN active_ingredient varchar(100);
ALTER TABLE health.medical_claim_line ADD COLUMN formulary_tier varchar(40);

ALTER TABLE health.ref_medicine_formulary ADD COLUMN benefit_category varchar(15);
ALTER TABLE health.ref_medicine_formulary ADD COLUMN prior_auth_required boolean;
ALTER TABLE health.ref_medicine_formulary ADD COLUMN max_quantity integer;
ALTER TABLE health.ref_medicine_formulary ADD COLUMN max_days_supply integer;
ALTER TABLE health.ref_medicine_formulary ADD COLUMN reference_group varchar(20);

-- OQ-147: a format with line_kind PHARMACY takes pharmacy lines, and names the line fields it requires.
ALTER TABLE health.cfg_claim_format_definitions ADD COLUMN line_kind varchar(10);
ALTER TABLE health.cfg_claim_format_definitions ADD COLUMN required_line_fields text[];

CREATE TABLE health.cfg_prescription_benefits (
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
  benefit_code                   varchar(15) NOT NULL,
  plan_code                      varchar(15),
  tier                           varchar(40) NOT NULL,
  covered                        boolean NOT NULL,
  benefit_category               varchar(15),
  copay_type                     varchar(10),
  copay_value                    numeric(18,4),
  limit_amount                   numeric(18,2),
  prescription_validity_days     integer,
  refill_tolerance_percent       numeric(5,2),
  CONSTRAINT cfg_prescription_benefits_pk PRIMARY KEY (id),
  CONSTRAINT cfg_prescription_benefits_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_prescription_benefits_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_prescription_benefits_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_prescription_benefits_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_prescription_benefits_ck1 CHECK (tier IN ('1', '2', '3', 'NON_FORMULARY')),
  CONSTRAINT cfg_prescription_benefits_ck2 CHECK (copay_type IN ('PERCENT', 'FIXED')),
  CONSTRAINT cfg_prescription_benefits_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_prescription_benefits_lookup ON health.cfg_prescription_benefits (tenant_id, code, effective_from);
CREATE INDEX cfg_prescription_benefits_import ON health.cfg_prescription_benefits (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE health.cfg_prescription_benefits ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_prescription_benefits FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_prescription_benefits
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_prescription_benefits
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_prescription_benefits IS 'Prescription benefits. CFG, scope T, Tier 2. Per plan and formulary tier: covered or not, co-payment, limit (F041, F043, OQ-149, OQ-151).';
