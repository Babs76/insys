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
-- V0100: Wave 4 batch 4.3b (addenda/renewals-and-retention.md §4.3, F026). Retention: what staff may offer to keep a policy
-- (general.cfg_retention_offers), retention cases (a cancellation asked for, a renewal declined, a renewal at risk of
-- lapsing) with their offers and outcome, and the tenant's lapse-risk days.
-- =====================================================================

CREATE TABLE general.cfg_retention_offers (
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
  offer_code                     varchar(20) NOT NULL,
  product_code                   varchar(15),
  kind                           varchar(16) NOT NULL,
  value                          numeric(18,2),
  max_uses_per_policy            integer NOT NULL,
  authority_role                 varchar(80),
  CONSTRAINT cfg_retention_offers_pk PRIMARY KEY (id),
  CONSTRAINT cfg_retention_offers_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_retention_offers_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_retention_offers_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_retention_offers_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_retention_offers_ck1 CHECK (kind IN ('DISCOUNT_PERCENT', 'EXCESS_CHANGE', 'COVER_CHANGE', 'PAYMENT_TERMS')),
  CONSTRAINT cfg_retention_offers_ck2 CHECK (max_uses_per_policy BETWEEN 1 AND 99),
  CONSTRAINT cfg_retention_offers_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_retention_offers_lookup ON general.cfg_retention_offers (tenant_id, code, effective_from);
CREATE INDEX cfg_retention_offers_import ON general.cfg_retention_offers (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE general.cfg_retention_offers ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_retention_offers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_retention_offers
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_retention_offers
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_retention_offers IS 'Retention offers. CFG, scope T, Tier 2. What staff may offer to keep a General policy (F026): a discount in per cent, an excess or cover change, or payment terms, for a product (none: any), how often per policy, and the role whose holders may offer it (none: any holder of the function). Added in Wave 4 batch 4.3.';

ALTER TABLE general.cfg_renewal_settings ADD COLUMN lapse_risk_days integer;
COMMENT ON COLUMN general.cfg_renewal_settings.lapse_risk_days IS 'lapse_risk_days: Integer. A renewal still awaiting acceptance this many days before its period end opens a LAPSE_RISK retention case (F026); none: no case is opened. Sample: 14';

CREATE TABLE general.retention_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  policy_number              varchar(30) NOT NULL,
  product_code               varchar(15) NOT NULL,
  renewal_id                 uuid,
  trigger                    varchar(25) NOT NULL,
  status                     varchar(10) NOT NULL,
  outcome                    varchar(10),
  opened_on                  date NOT NULL,
  opened_by                  uuid,
  owner_id                   uuid,
  closed_at                  timestamptz,
  closed_by                  uuid,
  note                       varchar(500),
  CONSTRAINT pk_retention_case PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_retention_case_policy FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id),
  CONSTRAINT ck_retention_case_trigger CHECK (trigger IN ('CANCELLATION_REQUESTED', 'NOT_RENEWING', 'LAPSE_RISK')),
  CONSTRAINT ck_retention_case_status CHECK (status IN ('OPEN', 'CLOSED')),
  CONSTRAINT ck_retention_case_outcome CHECK ((status = 'CLOSED') = (outcome IS NOT NULL) AND (outcome IS NULL OR outcome IN ('RETAINED', 'LOST', 'WITHDRAWN')))
);
CREATE INDEX ix_retention_case_1 ON general.retention_case (tenant_id, policy_id);
CREATE UNIQUE INDEX ux_retention_case_open ON general.retention_case (tenant_id, policy_id) WHERE status = 'OPEN';

CREATE TABLE general.retention_offer (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  retention_case_id          uuid NOT NULL,
  offer_code                 varchar(20) NOT NULL,
  kind                       varchar(16) NOT NULL,
  value                      numeric(18,2),
  made_by                    uuid NOT NULL,
  made_at                    timestamptz NOT NULL,
  outcome                    varchar(10) NOT NULL,
  decided_at                 timestamptz,
  CONSTRAINT pk_retention_offer PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_retention_offer_case FOREIGN KEY (tenant_id, retention_case_id) REFERENCES general.retention_case (tenant_id, id),
  CONSTRAINT ck_retention_offer_outcome CHECK (outcome IN ('PENDING', 'ACCEPTED', 'DECLINED'))
);
CREATE INDEX ix_retention_offer_1 ON general.retention_offer (tenant_id, retention_case_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['retention_case', 'retention_offer'] LOOP
    EXECUTE format('ALTER TABLE general.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE general.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON general.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;

-- F027 (addendum §4.7): the renewal and retention figures, by month (a renewal's period end, or the day a retention case or
-- offer changed), product, branch and measure, projected from General's events and rebuilt from the event log.
CREATE TABLE analytics.fact_renewals (
  tenant_id                  uuid NOT NULL,
  refreshed_at               timestamptz NOT NULL,
  month_key                  char(7) NOT NULL,
  product_code               varchar(15) NOT NULL,
  branch_code                varchar(15) NOT NULL,
  measure                    varchar(40) NOT NULL,
  item_count                 integer NOT NULL,
  amount                     numeric(20,2) NOT NULL,
  currency                   char(3) NOT NULL,
  CONSTRAINT pk_fact_renewals PRIMARY KEY (tenant_id, month_key, product_code, branch_code, measure)
);
ALTER TABLE analytics.fact_renewals ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.fact_renewals FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON analytics.fact_renewals USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
