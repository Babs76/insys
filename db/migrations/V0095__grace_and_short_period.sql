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
-- V0095: Wave 4 batch 4.1 (addenda/renewals-and-retention.md §4.1, signed off 29 October 2026). Grace for General
-- instalments and Health contributions, and the short-period refund scale (OQ-133: the tenant's own bands, no platform default).
-- =====================================================================

CREATE TABLE general.cfg_grace_period_rules (
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
  frequency                      varchar(20) NOT NULL,
  grace_days                     integer NOT NULL,
  cover_during_grace             boolean NOT NULL,
  cancellation_reason            varchar(15) NOT NULL,
  claim_decline_reason           varchar(15) NOT NULL,
  deduct_arrears_from_claims     boolean,
  CONSTRAINT cfg_grace_period_rules_pk PRIMARY KEY (id),
  CONSTRAINT cfg_grace_period_rules_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_grace_period_rules_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_grace_period_rules_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_grace_period_rules_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_grace_period_rules_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_grace_period_rules_lookup ON general.cfg_grace_period_rules (tenant_id, code, effective_from);
CREATE INDEX cfg_grace_period_rules_import ON general.cfg_grace_period_rules (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX cfg_grace_period_rules_product ON general.cfg_grace_period_rules (tenant_id, product_code, frequency);
ALTER TABLE general.cfg_grace_period_rules ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_grace_period_rules FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_grace_period_rules
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_grace_period_rules
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_grace_period_rules IS 'General grace periods. CFG, scope T, Tier 2. Per product and billing frequency: the days of grace after a missed instalment, whether cover continues in them, and the cancellation reason used when grace ends unpaid. A claim for a loss in grace without cover is accepted; its payment is held until the arrears are paid (or, with deduct_arrears_from_claims, paid less them), and declined with claim_decline_reason if grace ends unpaid. Added in Wave 4 batch 4.1.';

ALTER TABLE general.cfg_grace_period_rules ADD CONSTRAINT cfg_grace_period_rules_band CHECK (grace_days >= 0);
CREATE TABLE general.cfg_short_period_scales (
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
  scale_code                     varchar(20) NOT NULL,
  days_in_force_to               integer NOT NULL,
  percent_retained               numeric(5,2) NOT NULL,
  CONSTRAINT cfg_short_period_scales_pk PRIMARY KEY (id),
  CONSTRAINT cfg_short_period_scales_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_short_period_scales_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_short_period_scales_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_short_period_scales_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_short_period_scales_ck1 CHECK (percent_retained BETWEEN 0 AND 100),
  CONSTRAINT cfg_short_period_scales_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_short_period_scales_lookup ON general.cfg_short_period_scales (tenant_id, code, effective_from);
CREATE INDEX cfg_short_period_scales_import ON general.cfg_short_period_scales (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX cfg_short_period_scales_scale ON general.cfg_short_period_scales (tenant_id, scale_code, days_in_force_to);
ALTER TABLE general.cfg_short_period_scales ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_short_period_scales FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_short_period_scales
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_short_period_scales
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_short_period_scales IS 'Short-period refund scales. CFG, scope T, Tier 2. A band of a scale: for a policy in force up to days_in_force_to days, the insurer keeps percent_retained of the annual premium (OQ-133: the tenant loads its own; no platform default). Added in Wave 4 batch 4.1.';

ALTER TABLE general.cfg_short_period_scales ADD CONSTRAINT cfg_short_period_scales_band CHECK (days_in_force_to > 0);
-- A SHORT_PERIOD reason names its scale; without one it stays refused, as before.
ALTER TABLE general.cfg_cancellation_reasons_and_refund_rules ADD COLUMN short_period_scale varchar(20);
COMMENT ON COLUMN general.cfg_cancellation_reasons_and_refund_rules.short_period_scale IS 'short_period_scale: Code(20). The scale (general.cfg_short_period_scales.scale_code) a SHORT_PERIOD refund uses. Sample: SP-STD';
CREATE TABLE health.cfg_contribution_grace_rules (
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
  scheme                         varchar(80),
  grace_days                     integer NOT NULL,
  suspend_after_days             integer NOT NULL,
  terminate_notice_days          integer NOT NULL,
  reinstate_on_payment           boolean,
  CONSTRAINT cfg_contribution_grace_rules_pk PRIMARY KEY (id),
  CONSTRAINT cfg_contribution_grace_rules_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_contribution_grace_rules_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_contribution_grace_rules_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_contribution_grace_rules_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_contribution_grace_rules_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_contribution_grace_rules_lookup ON health.cfg_contribution_grace_rules (tenant_id, code, effective_from);
CREATE INDEX cfg_contribution_grace_rules_import ON health.cfg_contribution_grace_rules (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX cfg_contribution_grace_rules_scheme ON health.cfg_contribution_grace_rules (tenant_id, scheme);
ALTER TABLE health.cfg_contribution_grace_rules ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_contribution_grace_rules FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_contribution_grace_rules
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_contribution_grace_rules
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_contribution_grace_rules IS 'Contribution grace rules. CFG, scope T, Tier 2. For a scheme (or, with none named, every scheme): the grace days after a missed contribution, in which claims are held; the days after which the membership is suspended for arrears; and the notice before a person may terminate it. reinstate_on_payment (null: true): whether paying the arrears reinstates a membership suspended for them. Added in Wave 4 batch 4.1.';

ALTER TABLE health.cfg_contribution_grace_rules ADD CONSTRAINT cfg_contribution_grace_rules_band CHECK (grace_days >= 0 AND suspend_after_days >= grace_days AND terminate_notice_days >= 0);
-- The policy's arrears, from Billing (BR-103: as of its detection date), and the grace they started.
ALTER TABLE general.policy ADD COLUMN arrears_since date;
ALTER TABLE general.policy ADD COLUMN arrears_count integer NOT NULL DEFAULT 0;
ALTER TABLE general.policy ADD COLUMN grace_until date;
ALTER TABLE general.policy ADD COLUMN cover_during_grace boolean;
ALTER TABLE general.policy ADD CONSTRAINT ck_policy_grace CHECK ((grace_until IS NULL) = (arrears_since IS NULL) AND arrears_count >= 0);
CREATE INDEX policy_grace_until ON general.policy (tenant_id, grace_until) WHERE grace_until IS NOT NULL;

-- The membership's contribution arrears and grace; suspended_for_arrears_on when suspension followed them.
ALTER TABLE health.membership ADD COLUMN arrears_since date;
ALTER TABLE health.membership ADD COLUMN grace_until date;
ALTER TABLE health.membership ADD COLUMN suspend_on date;
ALTER TABLE health.membership ADD COLUMN suspended_for_arrears_on date;
ALTER TABLE health.membership ADD COLUMN termination_notice_ends date;
ALTER TABLE health.membership ADD COLUMN termination_due_raised boolean NOT NULL DEFAULT false;
ALTER TABLE health.membership ADD CONSTRAINT ck_membership_grace CHECK ((grace_until IS NULL) = (arrears_since IS NULL));
CREATE INDEX membership_arrears ON health.membership (tenant_id, suspend_on) WHERE arrears_since IS NOT NULL;

-- OQ-137 (Ade, 29 October 2026): a claim paid less the policy's arrears records the gross. The claim keeps what was
-- deducted; the instruction pays the net (its amount) and keeps the deduction; the payment movement records the gross
-- (what the claim paid), the deduction and the net paid to the bank.
ALTER TABLE general.claim ADD COLUMN premium_offset numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE general.claim ADD CONSTRAINT ck_claim_premium_offset CHECK (premium_offset >= 0);
ALTER TABLE claims.claim_payment_instruction ADD COLUMN premium_offset numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE claims.claim_payment_instruction ADD CONSTRAINT ck_instruction_premium_offset CHECK (premium_offset >= 0);
ALTER TABLE claims.claim_financial_movement ADD COLUMN premium_offset numeric(18,2);
ALTER TABLE claims.claim_financial_movement ADD COLUMN net_amount numeric(18,2);
ALTER TABLE claims.claim_financial_movement ADD CONSTRAINT ck_movement_premium_offset
  CHECK ((premium_offset IS NULL) = (net_amount IS NULL) AND (premium_offset IS NULL OR (premium_offset > 0 AND net_amount = amount - premium_offset)));
COMMENT ON COLUMN claims.claim_financial_movement.premium_offset IS 'A payment made less the policy''s arrears (OQ-137): what was deducted; amount is the gross the claim paid.';
COMMENT ON COLUMN claims.claim_financial_movement.net_amount IS 'A payment made less the policy''s arrears (OQ-137): what was paid to the payee.';
