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
-- V0099: Wave 4 batch 4.3a (addenda/renewals-and-retention.md §4.3, §4.4; OQ-130, OQ-132). How a General renewal is
-- accepted (AUTOMATIC, ON_ACCEPTANCE, ON_PAYMENT; a renewal on worse terms needs acceptance), the customer's acceptance or
-- decline, a lapse at the period end, and an insurer's non-renewal with its reason, notice and (where the reason needs it)
-- a second person's approval; a decision too late for its notice extends cover, pro rata, until the notice runs out.
-- =====================================================================

ALTER TABLE general.cfg_renewal_rules ADD COLUMN acceptance varchar(13);
ALTER TABLE general.cfg_renewal_rules ADD CONSTRAINT cfg_renewal_rules_ck1 CHECK (acceptance IN ('AUTOMATIC', 'ON_ACCEPTANCE', 'ON_PAYMENT'));
COMMENT ON COLUMN general.cfg_renewal_rules.acceptance IS 'acceptance: Enum{Automatic,OnAcceptance,OnPayment}. How the renewal is accepted (OQ-130); none is AUTOMATIC. Sample: ON_ACCEPTANCE';

CREATE TABLE general.cfg_non_renewal_reasons (
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
  reason_code                    varchar(20) NOT NULL,
  notice_days                    integer NOT NULL,
  customer_wording               varchar(500) NOT NULL,
  requires_approval              boolean NOT NULL,
  CONSTRAINT cfg_non_renewal_reasons_pk PRIMARY KEY (id),
  CONSTRAINT cfg_non_renewal_reasons_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_non_renewal_reasons_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_non_renewal_reasons_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_non_renewal_reasons_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_non_renewal_reasons_ck1 CHECK (notice_days BETWEEN 0 AND 3650),
  CONSTRAINT cfg_non_renewal_reasons_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_non_renewal_reasons_lookup ON general.cfg_non_renewal_reasons (tenant_id, code, effective_from);
CREATE INDEX cfg_non_renewal_reasons_import ON general.cfg_non_renewal_reasons (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE general.cfg_non_renewal_reasons ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_non_renewal_reasons FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_non_renewal_reasons
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_non_renewal_reasons
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_non_renewal_reasons IS 'Non-renewal reasons. CFG, scope T, Tier 2. Why an insurer does not renew a General policy, the notice the policyholder gets (OQ-132: 31 days by default), the wording they are told, and whether a second person approves it (F025). Added in Wave 4 batch 4.3.';

ALTER TABLE general.renewal DROP CONSTRAINT ck_renewal_status;
ALTER TABLE general.renewal ALTER COLUMN status TYPE varchar(25);
ALTER TABLE general.renewal ADD CONSTRAINT ck_renewal_status CHECK (status IN ('REVIEWING', 'REFERRED', 'NOT_ELIGIBLE', 'OFFERED', 'ACCEPTED', 'RENEWED', 'NOT_RENEWED',
  'DECLINED_BY_CUSTOMER', 'NOT_RENEWED_BY_INSURER', 'LAPSED'));
ALTER TABLE general.renewal ADD COLUMN acceptance varchar(13);
ALTER TABLE general.renewal ADD COLUMN needs_acceptance boolean NOT NULL DEFAULT false;
ALTER TABLE general.renewal ADD COLUMN accepted_at timestamptz;
ALTER TABLE general.renewal ADD COLUMN accepted_by uuid;
ALTER TABLE general.renewal ADD COLUMN accepted_channel varchar(10);
ALTER TABLE general.renewal ADD COLUMN declined_at timestamptz;
ALTER TABLE general.renewal ADD COLUMN declined_by uuid;
ALTER TABLE general.renewal ADD COLUMN declined_channel varchar(10);
ALTER TABLE general.renewal ADD COLUMN renewal_policy_id uuid;
ALTER TABLE general.renewal ADD COLUMN non_renewal_reason varchar(20);
ALTER TABLE general.renewal ADD COLUMN non_renewal_status varchar(10);
ALTER TABLE general.renewal ADD COLUMN non_renewal_requested_by uuid;
ALTER TABLE general.renewal ADD COLUMN non_renewal_requested_at timestamptz;
ALTER TABLE general.renewal ADD COLUMN non_renewal_decided_by uuid;
ALTER TABLE general.renewal ADD COLUMN non_renewal_decided_at timestamptz;
ALTER TABLE general.renewal ADD COLUMN non_renewal_notice_on date;
ALTER TABLE general.renewal ADD COLUMN cover_extended_to date;
ALTER TABLE general.renewal ADD CONSTRAINT ck_renewal_acceptance CHECK (acceptance IS NULL OR acceptance IN ('AUTOMATIC', 'ON_ACCEPTANCE', 'ON_PAYMENT'));
ALTER TABLE general.renewal ADD CONSTRAINT ck_renewal_channels CHECK ((accepted_channel IS NULL OR accepted_channel IN ('STAFF', 'PORTAL', 'BROKER', 'PAYMENT'))
  AND (declined_channel IS NULL OR declined_channel IN ('STAFF', 'PORTAL', 'BROKER')));
ALTER TABLE general.renewal ADD CONSTRAINT ck_renewal_non_renewal CHECK (non_renewal_status IS NULL OR non_renewal_status IN ('REQUESTED', 'DECIDED', 'REJECTED'));
COMMENT ON COLUMN general.renewal.cover_extended_to IS 'OQ-132 (b): a non-renewal decided too late for its notice extends cover, pro rata, to this day.';

ALTER TABLE general.policy ADD COLUMN expiry_reason varchar(25);
ALTER TABLE general.policy ADD CONSTRAINT ck_policy_expiry_reason CHECK (expiry_reason IS NULL OR expiry_reason IN ('NOT_RENEWED_BY_INSURER', 'DECLINED_BY_CUSTOMER', 'LAPSED'));
COMMENT ON COLUMN general.policy.expiry_reason IS 'Why a policy expired at its period end without renewal (Wave 4 §4.3, §4.4): the insurer did not renew it, the customer declined, or the renewal lapsed unaccepted. None for an expiry before Wave 4 batch 4.3.';
