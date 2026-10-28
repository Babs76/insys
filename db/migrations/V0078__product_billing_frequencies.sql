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
-- V0078: a General quote is priced in the frequency chosen, when its product allows it (BR-095 follow-up, decided by
-- Ade on 28 October 2026).
-- general.cfg_product_billing_frequencies (CFG, scope T) lists the billing frequencies each product allows. A quote made
-- in one of them records it and its periods per year: the quote shows the instalment Billing will charge (the annual
-- premium divided by the periods per year) beside the annual premium. Otherwise the quote shows the annual premium,
-- labelled annual. It has every static table's columns, import_batch_id (V0067) included.
-- =====================================================================
CREATE TABLE general.cfg_product_billing_frequencies (
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
  product_code                   varchar(80) NOT NULL,
  frequency                      varchar(80) NOT NULL,
  CONSTRAINT cfg_product_billing_frequencies_pk PRIMARY KEY (id),
  CONSTRAINT cfg_product_billing_frequencies_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_product_billing_frequencies_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_product_billing_frequencies_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_product_billing_frequencies_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_product_billing_frequencies_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_product_billing_frequencies_lookup ON general.cfg_product_billing_frequencies (tenant_id, code, effective_from);
CREATE INDEX cfg_product_billing_frequencies_product ON general.cfg_product_billing_frequencies (tenant_id, product_code, frequency);
CREATE INDEX cfg_product_billing_frequencies_import ON general.cfg_product_billing_frequencies (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE general.cfg_product_billing_frequencies ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_product_billing_frequencies FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_product_billing_frequencies
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_product_billing_frequencies
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_product_billing_frequencies IS 'Product billing frequencies. CFG, scope T, Tier 2. The billing frequencies (billing.ref_billing_frequencies) a General product allows; a quote in one of them shows its instalment beside the annual premium. Added by Ade''s BR-095 follow-up (28 October 2026).';

-- The quote's frequency, when its product allows the one chosen: the instalment is the annual premium over the periods.
ALTER TABLE general.quote
  ADD COLUMN premium_frequency varchar(20),
  ADD COLUMN premium_periods smallint,
  ADD CONSTRAINT ck_quote_premium_frequency CHECK ((premium_frequency IS NULL) = (premium_periods IS NULL) AND (premium_periods IS NULL OR premium_periods > 0));

COMMENT ON COLUMN general.quote.premium_frequency IS 'The billing frequency the quote is priced in, when its product allows the one chosen (general.cfg_product_billing_frequencies); null means annual.';
COMMENT ON COLUMN general.quote.premium_periods IS 'The frequency''s periods per year when the quote was made (billing.ref_billing_frequencies): the instalment is the annual premium over them, as Billing charges it.';
