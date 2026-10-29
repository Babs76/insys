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
-- V0096: Wave 4 batch 4.2 (addenda/renewals-and-retention.md §4.2, §4.5; OQ-130, OQ-131, Wave 4 addition 2). The renewal
-- review of a General policy before its invitation: eligibility, changes in the risk, claims history, re-rating with the
-- claims experience factor, and referral to an underwriter. The tenant's renewal settings: the worse-terms threshold and
-- the notice a renewal with changed terms needs.
-- =====================================================================

CREATE TABLE general.cfg_renewal_review_rules (
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
  eligibility_ruleset            varchar(15),
  underwriting_rules             boolean,
  claims_lookback_years          integer NOT NULL,
  review_days_before_invitation  integer NOT NULL,
  CONSTRAINT cfg_renewal_review_rules_pk PRIMARY KEY (id),
  CONSTRAINT cfg_renewal_review_rules_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_renewal_review_rules_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_renewal_review_rules_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_renewal_review_rules_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_renewal_review_rules_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_renewal_review_rules_lookup ON general.cfg_renewal_review_rules (tenant_id, code, effective_from);
CREATE INDEX cfg_renewal_review_rules_import ON general.cfg_renewal_review_rules (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX cfg_renewal_review_rules_product ON general.cfg_renewal_review_rules (tenant_id, product_code);
ALTER TABLE general.cfg_renewal_review_rules ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_renewal_review_rules FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_renewal_review_rules
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_renewal_review_rules
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_renewal_review_rules IS 'Renewal review rules. CFG, scope T, Tier 2. What a products renewal review checks before its invitation: the eligibility rule set (Rules), whether the products underwriting rules refer it (null: true), how many years of claims it reads, and how many days before the invitation it starts. Added in Wave 4 batch 4.2.';

ALTER TABLE general.cfg_renewal_review_rules ADD CONSTRAINT cfg_renewal_review_rules_band CHECK (claims_lookback_years BETWEEN 1 AND 10 AND review_days_before_invitation >= 0);
CREATE TABLE general.cfg_claims_experience_factors (
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
  claims_count_from              integer NOT NULL,
  claims_count_to                integer,
  loss_ratio_from                numeric(7,4),
  loss_ratio_to                  numeric(7,4),
  factor                         numeric(7,4) NOT NULL,
  CONSTRAINT cfg_claims_experience_factors_pk PRIMARY KEY (id),
  CONSTRAINT cfg_claims_experience_factors_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_claims_experience_factors_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_claims_experience_factors_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_claims_experience_factors_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_claims_experience_factors_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_claims_experience_factors_lookup ON general.cfg_claims_experience_factors (tenant_id, code, effective_from);
CREATE INDEX cfg_claims_experience_factors_import ON general.cfg_claims_experience_factors (import_batch_id) WHERE import_batch_id IS NOT NULL;
CREATE INDEX cfg_claims_experience_factors_product ON general.cfg_claims_experience_factors (tenant_id, product_code);
ALTER TABLE general.cfg_claims_experience_factors ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_claims_experience_factors FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_claims_experience_factors
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_claims_experience_factors
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_claims_experience_factors IS 'Claims experience factors. CFG, scope T, Tier 2. The factor a re-rated renewal premium is multiplied by, for a claims count and loss ratio in the bands (OQ-131: 1.0 when none applies). Added in Wave 4 batch 4.2.';

ALTER TABLE general.cfg_claims_experience_factors ADD CONSTRAINT cfg_claims_experience_factors_band CHECK (factor > 0 AND claims_count_from >= 0 AND (claims_count_to IS NULL OR claims_count_to >= claims_count_from));
CREATE TABLE general.cfg_renewal_settings (
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
  worse_terms_pct                numeric(5,2) NOT NULL,
  changed_terms_notice_days      integer NOT NULL,
  CONSTRAINT cfg_renewal_settings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_renewal_settings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_renewal_settings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_renewal_settings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_renewal_settings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_renewal_settings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_renewal_settings_lookup ON general.cfg_renewal_settings (tenant_id, code, effective_from);
CREATE INDEX cfg_renewal_settings_import ON general.cfg_renewal_settings (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE general.cfg_renewal_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_renewal_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_renewal_settings
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_renewal_settings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_renewal_settings IS 'Renewal settings. CFG, scope T, Tier 2. The tenants threshold above which a renewal premium increase is worse terms needing acceptance (OQ-130, default 15), and the notice a renewal with changed terms is invited with at least (Wave 4 addition 2, default 31 days). Added in Wave 4 batch 4.2.';

ALTER TABLE general.cfg_renewal_settings ADD CONSTRAINT cfg_renewal_settings_band CHECK (worse_terms_pct >= 0 AND changed_terms_notice_days > 0);
-- A product's renewal is reviewed before its invitation (§4.2); null: not reviewed, as before.
ALTER TABLE general.cfg_renewal_rules ADD COLUMN review boolean;
COMMENT ON COLUMN general.cfg_renewal_rules.review IS 'review: Boolean. Whether the renewal is reviewed (eligibility, risk, claims, re-rating, referral) before the invitation (Wave 4). Sample: true';

-- The renewal of a General policy's period (§3.2): its review, the offered terms and the decision.
CREATE TABLE general.renewal (
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
  period_end                 date NOT NULL,
  status                     varchar(20) NOT NULL,
  opened_on                  date NOT NULL,
  currency                   char(3) NOT NULL,
  current_premium            numeric(18,2) NOT NULL,
  sum_insured                numeric(18,2) NOT NULL,
  claims_count               integer NOT NULL,
  claims_paid                numeric(18,2) NOT NULL,
  earned_premium             numeric(18,2) NOT NULL,
  loss_ratio                 numeric(9,4),
  endorsements               integer NOT NULL,
  fraud_referred_claims      integer NOT NULL,
  factor                     numeric(7,4) NOT NULL,
  re_rated                   boolean NOT NULL,
  quote_id                   uuid,
  rated_premium              numeric(18,2),
  eligibility_decision_id    uuid,
  eligibility_outcome        varchar(15),
  underwriting_decision_id   uuid,
  underwriting_outcome       varchar(15),
  referral_reason            varchar(200),
  loading_pct                numeric(7,4),
  offered_premium            numeric(18,2),
  changed_terms              boolean NOT NULL DEFAULT false,
  decision_code              varchar(15),
  decided_by                 uuid,
  decided_at                 timestamptz,
  CONSTRAINT pk_renewal PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_renewal_period UNIQUE (tenant_id, policy_id, period_end),
  CONSTRAINT fk_renewal_policy FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id),
  CONSTRAINT ck_renewal_status CHECK (status IN ('REVIEWING', 'REFERRED', 'NOT_ELIGIBLE', 'OFFERED', 'RENEWED', 'NOT_RENEWED')),
  CONSTRAINT ck_renewal_offer CHECK ((status = 'OFFERED') <= (offered_premium IS NOT NULL))
);
CREATE INDEX ix_renewal_1 ON general.renewal (tenant_id, status, period_end);
COMMENT ON TABLE general.renewal IS 'Wave 4 (F015-F019): the renewal of a policy''s period. Its review (eligibility, risk changes, claims history, re-rating and referral), the offered terms and the underwriter''s decision.';

-- Each check of a renewal's review, with its result.
CREATE TABLE general.renewal_check (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  renewal_id                 uuid NOT NULL,
  check_code                 varchar(30) NOT NULL,
  outcome                    varchar(15) NOT NULL,
  detail                     varchar(200),
  CONSTRAINT pk_renewal_check PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_renewal_check_renewal FOREIGN KEY (tenant_id, renewal_id) REFERENCES general.renewal (tenant_id, id),
  CONSTRAINT ck_renewal_check_outcome CHECK (outcome IN ('PASS', 'REFER', 'FAIL', 'PENDING'))
);
CREATE INDEX ix_renewal_check_1 ON general.renewal_check (tenant_id, renewal_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['renewal', 'renewal_check'] LOOP
    EXECUTE format('ALTER TABLE general.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE general.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON general.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;

-- A renewal quote (§4.2): re-rated as new business for the renewal period, it records the rating the renewal offers.
ALTER TABLE general.quote ADD COLUMN renewal_id uuid;
CREATE INDEX ix_quote_renewal ON general.quote (tenant_id, renewal_id) WHERE renewal_id IS NOT NULL;

-- §4.5: a Health scheme's renewal notice, once per scheme year.
ALTER TABLE health.scheme ADD COLUMN renewal_notice_sent_for date;
