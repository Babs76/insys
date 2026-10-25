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
-- V0056: every way an approval can end leaves its record in a defined state (Wave 2 batch 1, D-1 to D-5).
-- - workflow.cfg_approval_expiry: how long each approval type waits before it expires (D-4).
-- - A rejected payout batch or provider payment run ends as REJECTED, and its payments or claims are released (D-1, D-2).
-- - Authority limits carry a currency, the tenant's base currency when empty (OQ-112).
-- =====================================================================
-- Workflow and Case Management · Approvals · Approval expiry
CREATE TABLE workflow.cfg_approval_expiry (
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
  approval_type                  varchar(30) NOT NULL,
  expires_after                  varchar(20) NOT NULL,
  CONSTRAINT cfg_approval_expiry_pk PRIMARY KEY (id),
  CONSTRAINT cfg_approval_expiry_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_approval_expiry_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_approval_expiry_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_approval_expiry_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_approval_expiry_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_approval_expiry_lookup ON workflow.cfg_approval_expiry (tenant_id, code, effective_from);
ALTER TABLE workflow.cfg_approval_expiry ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.cfg_approval_expiry FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.cfg_approval_expiry
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.cfg_approval_expiry
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.cfg_approval_expiry IS 'Approval expiry. CFG, scope T, Tier 2. How long an approval of a type waits (ISO 8601 duration, calendar time) before it expires; 14 days when none is in force. Added in Wave 2 batch 1 (D-4).';

ALTER TABLE payment.payout_batch DROP CONSTRAINT ck_payout_batch_status;
ALTER TABLE payment.payout_batch ADD CONSTRAINT ck_payout_batch_status CHECK (status IN ('BUILT', 'APPROVED', 'RELEASED', 'SETTLED', 'REJECTED'));

ALTER TABLE health.provider_payment_run DROP CONSTRAINT ck_provider_payment_run_status;
ALTER TABLE health.provider_payment_run ADD CONSTRAINT ck_provider_payment_run_status CHECK (status IN ('OPEN', 'CUT_OFF', 'CALCULATED', 'APPROVED', 'RELEASED', 'RECONCILED', 'REJECTED'));

ALTER TABLE workflow.cfg_delegation_and_approval_authority ADD COLUMN currency char(3);
COMMENT ON COLUMN workflow.cfg_delegation_and_approval_authority.currency IS 'The currency of limit_value; the tenant''s base currency when empty (OQ-112). An approval in another currency needs a row in its currency.';
