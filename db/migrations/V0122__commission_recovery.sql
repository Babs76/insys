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
-- V0113: Wave 6 batch 6.3 (addenda/finance-and-compliance.md §4.8, §4.9; F058, F076; OQ-158, OQ-160). Fraud cases for Life and General, as Health's, seen only by holders of the
-- fraud function; bulk jobs for parties, all or nothing.
-- =====================================================================
-- B-009: commission already paid when a broker of record changes: recovered by default, or kept if a tenant says so.
CREATE TABLE commission.cfg_broker_change_settings (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid,
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
  paid_commission                varchar(10) NOT NULL,
  CONSTRAINT cfg_broker_change_settings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_broker_change_settings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_broker_change_settings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_broker_change_settings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_broker_change_settings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_broker_change_settings_ck1 CHECK (paid_commission IN ('RECOVER', 'KEEP')),
  CONSTRAINT cfg_broker_change_settings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_broker_change_settings_lookup ON commission.cfg_broker_change_settings (tenant_id, code, effective_from);

ALTER TABLE commission.cfg_broker_change_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.cfg_broker_change_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON commission.cfg_broker_change_settings FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON commission.cfg_broker_change_settings FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON commission.cfg_broker_change_settings FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON commission.cfg_broker_change_settings FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON commission.cfg_broker_change_settings TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON commission.cfg_broker_change_settings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE commission.cfg_broker_change_settings IS 'Broker change settings. CFG, scope T, Tier 1. What happens to commission already paid when a broker of record changes (B-009): RECOVER (the default, with no row) or KEEP. Module: Distribution; sub-module: Commission. Proposed owner role: FIN. Provenance: B-009.';

COMMENT ON COLUMN commission.cfg_broker_change_settings.paid_commission IS 'paid_commission: RECOVER or KEEP. Sample: RECOVER';

CREATE TABLE commission.commission_recovery (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  entitlement_id             uuid NOT NULL,
  intermediary_code          varchar(15) NOT NULL,
  policy_ref                 uuid NOT NULL,
  currency                   char(3) NOT NULL,
  reason                     varchar(20) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  recovered_amount           numeric(18,2) NOT NULL,
  statement_id               uuid,
  on_statement_amount        numeric(18,2) NOT NULL,
  CONSTRAINT pk_commission_recovery PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_commission_recovery_entitlement UNIQUE (tenant_id, entitlement_id),
  CONSTRAINT ck_commission_recovery_amounts CHECK (amount > 0 AND recovered_amount >= 0 AND recovered_amount + on_statement_amount <= amount),
  CONSTRAINT fk_commission_recovery_entitlement FOREIGN KEY (tenant_id, entitlement_id) REFERENCES commission.commission_entitlement (tenant_id, id)
);
CREATE INDEX ix_commission_recovery_owed ON commission.commission_recovery (tenant_id, intermediary_code, currency) WHERE recovered_amount < amount;
ALTER TABLE commission.commission_recovery ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.commission_recovery FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON commission.commission_recovery USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
