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
-- V0092: customer due diligence (Wave 3 batch 2, addenda/due-diligence.md §3 to §7, signed off 29 October 2026).
-- * Five configuration tables, with platform defaults: the requirement sets (F002), risk factors and classes (F077,
--   OQ-124), the gates (OQ-123) and the tenant's settings (adverse media off by default).
-- * party.due_diligence, party.screening_hit and party.beneficial_owner. All HIGHLY_SENSITIVE.
-- * payment.payment.held_reason: a payout whose payee is not cleared is held out of the batches (§5).
-- * workflow.process_instance.process_code widened to 30, as case types' process codes are, for the due diligence processes.
-- =====================================================================
CREATE TABLE party.cfg_due_diligence_requirements (
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
  import_batch_id                uuid,
  party_type                     varchar(15) NOT NULL,
  dd_level                       varchar(12) NOT NULL,
  domain                         varchar(10) NOT NULL,
  check_type                     varchar(25) NOT NULL,
  basis                          varchar(200),
  CONSTRAINT cfg_due_diligence_requirements_pk PRIMARY KEY (id),
  CONSTRAINT cfg_due_diligence_requirements_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_due_diligence_requirements_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_due_diligence_requirements_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_due_diligence_requirements_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_due_diligence_requirements_ck1 CHECK (party_type IN ('INDIVIDUAL', 'ORGANISATION')),
  CONSTRAINT cfg_due_diligence_requirements_ck2 CHECK (dd_level IN ('SIMPLIFIED', 'STANDARD', 'ENHANCED')),
  CONSTRAINT cfg_due_diligence_requirements_ck3 CHECK (domain IN ('LIFE', 'GENERAL', 'HEALTH')),
  CONSTRAINT cfg_due_diligence_requirements_ck4 CHECK (check_type IN ('IDENTITY', 'ADDRESS', 'SANCTIONS', 'PEP', 'ADVERSE_MEDIA', 'BENEFICIAL_OWNERSHIP', 'SOURCE_OF_FUNDS')),
  CONSTRAINT cfg_due_diligence_requirements_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_due_diligence_requirements_lookup ON party.cfg_due_diligence_requirements (tenant_id, code, effective_from);
CREATE INDEX cfg_due_diligence_requirements_import ON party.cfg_due_diligence_requirements (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_due_diligence_requirements ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_due_diligence_requirements FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON party.cfg_due_diligence_requirements FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON party.cfg_due_diligence_requirements FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON party.cfg_due_diligence_requirements FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON party.cfg_due_diligence_requirements FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON party.cfg_due_diligence_requirements TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_due_diligence_requirements
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_due_diligence_requirements IS 'Due diligence requirements. CFG, scope GT, Tier 1. The checks a party of a type needs at a due diligence level in a domain (F002, addenda/due-diligence.md §4.2); the platform rows are working defaults for compliance to confirm. Added in Wave 3 batch 2.';

CREATE TABLE party.cfg_customer_risk_factors (
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
  import_batch_id                uuid,
  factor                         varchar(20) NOT NULL,
  match_values                   text[] NOT NULL,
  weight                         integer NOT NULL,
  CONSTRAINT cfg_customer_risk_factors_pk PRIMARY KEY (id),
  CONSTRAINT cfg_customer_risk_factors_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_customer_risk_factors_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_customer_risk_factors_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_customer_risk_factors_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_customer_risk_factors_ck1 CHECK (factor IN ('COUNTRY', 'PARTY_TYPE', 'LEGAL_FORM', 'OCCUPATION', 'INDUSTRY', 'PEP', 'DOMAIN', 'CHANNEL', 'SOURCE_OF_FUNDS')),
  CONSTRAINT cfg_customer_risk_factors_ck2 CHECK (weight BETWEEN -100 AND 100),
  CONSTRAINT cfg_customer_risk_factors_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_customer_risk_factors_lookup ON party.cfg_customer_risk_factors (tenant_id, code, effective_from);
CREATE INDEX cfg_customer_risk_factors_import ON party.cfg_customer_risk_factors (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_customer_risk_factors ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_customer_risk_factors FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON party.cfg_customer_risk_factors FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON party.cfg_customer_risk_factors FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON party.cfg_customer_risk_factors FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON party.cfg_customer_risk_factors FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON party.cfg_customer_risk_factors TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_customer_risk_factors
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_customer_risk_factors IS 'Customer risk factors. CFG, scope GT, Tier 1. A weighted rule on a party''s facts: when the fact is one of match_values, the weight is added to the party''s risk score (F077, addenda/due-diligence.md §4.4). The tenant''s compliance sets the weights. Added in Wave 3 batch 2.';

CREATE TABLE party.cfg_customer_risk_classes (
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
  import_batch_id                uuid,
  risk_class                     varchar(6) NOT NULL,
  min_score                      integer NOT NULL,
  max_score                      integer,
  dd_level                       varchar(12) NOT NULL,
  review_interval                interval NOT NULL,
  CONSTRAINT cfg_customer_risk_classes_pk PRIMARY KEY (id),
  CONSTRAINT cfg_customer_risk_classes_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_customer_risk_classes_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_customer_risk_classes_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_customer_risk_classes_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_customer_risk_classes_ck1 CHECK (risk_class IN ('LOW', 'MEDIUM', 'HIGH')),
  CONSTRAINT cfg_customer_risk_classes_ck2 CHECK (dd_level IN ('SIMPLIFIED', 'STANDARD', 'ENHANCED')),
  CONSTRAINT cfg_customer_risk_classes_band CHECK (max_score IS NULL OR max_score >= min_score),
  CONSTRAINT cfg_customer_risk_classes_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_customer_risk_classes_lookup ON party.cfg_customer_risk_classes (tenant_id, code, effective_from);
CREATE INDEX cfg_customer_risk_classes_import ON party.cfg_customer_risk_classes (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_customer_risk_classes ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_customer_risk_classes FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON party.cfg_customer_risk_classes FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON party.cfg_customer_risk_classes FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON party.cfg_customer_risk_classes FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON party.cfg_customer_risk_classes FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON party.cfg_customer_risk_classes TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_customer_risk_classes
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_customer_risk_classes IS 'Customer risk classes. CFG, scope GT, Tier 1. The score band of each risk class, its due diligence level and its review interval (F077, OQ-124: LOW 5 years, MEDIUM 3, HIGH 1 by default). Added in Wave 3 batch 2.';

CREATE TABLE party.cfg_due_diligence_gates (
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
  import_batch_id                uuid,
  domain                         varchar(10) NOT NULL,
  step                           varchar(15) NOT NULL,
  requirement                    varchar(10) NOT NULL,
  screening_max_age              interval,
  CONSTRAINT cfg_due_diligence_gates_pk PRIMARY KEY (id),
  CONSTRAINT cfg_due_diligence_gates_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_due_diligence_gates_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_due_diligence_gates_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_due_diligence_gates_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_due_diligence_gates_ck1 CHECK (domain IN ('LIFE', 'GENERAL', 'HEALTH', 'ANY')),
  CONSTRAINT cfg_due_diligence_gates_ck2 CHECK (step IN ('QUOTE', 'ISSUE', 'PAYOUT', 'CLAIM_PAYOUT')),
  CONSTRAINT cfg_due_diligence_gates_ck3 CHECK (requirement IN ('NONE', 'SCREENED', 'CLEARED')),
  CONSTRAINT cfg_due_diligence_gates_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_due_diligence_gates_lookup ON party.cfg_due_diligence_gates (tenant_id, code, effective_from);
CREATE INDEX cfg_due_diligence_gates_import ON party.cfg_due_diligence_gates (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_due_diligence_gates ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_due_diligence_gates FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON party.cfg_due_diligence_gates FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON party.cfg_due_diligence_gates FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON party.cfg_due_diligence_gates FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON party.cfg_due_diligence_gates FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON party.cfg_due_diligence_gates TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_due_diligence_gates
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_due_diligence_gates IS 'Due diligence gates. CFG, scope GT, Tier 1. What a party needs before a step in a domain (OQ-123, addenda/due-diligence.md §5): nothing, a recent sanctions screening with no unreviewed hit, or cleared due diligence. ANY is for payouts whose domain is not known (refunds, case redress). Added in Wave 3 batch 2.';

CREATE TABLE party.cfg_due_diligence_settings (
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
  gates_enabled                  boolean NOT NULL,
  adverse_media_enabled          boolean NOT NULL,
  adverse_media_basis            varchar(200),
  CONSTRAINT cfg_due_diligence_settings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_due_diligence_settings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_due_diligence_settings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_due_diligence_settings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_due_diligence_settings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_due_diligence_settings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_due_diligence_settings_lookup ON party.cfg_due_diligence_settings (tenant_id, code, effective_from);
CREATE INDEX cfg_due_diligence_settings_import ON party.cfg_due_diligence_settings (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE party.cfg_due_diligence_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_due_diligence_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.cfg_due_diligence_settings
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_due_diligence_settings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_due_diligence_settings IS 'Due diligence settings. CFG, scope T, Tier 1. Whether the tenant''s due diligence gates apply, and whether adverse media is screened (off by default; POPIA, addenda/due-diligence.md §1). With no row, the gates apply to a tenant entitled to LIFE (OQ-123). Added in Wave 3 batch 2.';

-- The platform defaults (working defaults for compliance to confirm before go-live).
-- Requirements (§4.2): Life follows FICA; General and Health screen sanctions only (s26B).
INSERT INTO party.cfg_due_diligence_requirements
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, party_type, dd_level, domain, check_type, basis)
SELECT NULL, r.domain || '-' || r.party_type || '-' || r.dd_level || '-' || r.check_type, 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.2',
       'migration (V0092)', 'Working default, for compliance to confirm before go-live', r.party_type, r.dd_level, r.domain, r.check_type, r.basis
FROM (
  SELECT 'LIFE' AS domain, p.party_type, l.dd_level, c.check_type, 'FICA s21 to s21H' AS basis
  FROM unnest(ARRAY['INDIVIDUAL', 'ORGANISATION']) AS p(party_type)
  CROSS JOIN unnest(ARRAY['SIMPLIFIED', 'STANDARD', 'ENHANCED']) AS l(dd_level)
  CROSS JOIN unnest(ARRAY['IDENTITY', 'ADDRESS', 'SANCTIONS', 'PEP', 'BENEFICIAL_OWNERSHIP', 'SOURCE_OF_FUNDS']) AS c(check_type)
  WHERE (l.dd_level <> 'SIMPLIFIED' OR c.check_type IN ('IDENTITY', 'SANCTIONS'))
    AND (c.check_type <> 'BENEFICIAL_OWNERSHIP' OR (p.party_type = 'ORGANISATION' AND l.dd_level <> 'SIMPLIFIED'))
    AND (c.check_type <> 'SOURCE_OF_FUNDS' OR l.dd_level = 'ENHANCED')
  UNION ALL
  SELECT d.domain, p.party_type, l.dd_level, 'SANCTIONS', 'FICA s26B'
  FROM unnest(ARRAY['GENERAL', 'HEALTH']) AS d(domain)
  CROSS JOIN unnest(ARRAY['INDIVIDUAL', 'ORGANISATION']) AS p(party_type)
  CROSS JOIN unnest(ARRAY['SIMPLIFIED', 'STANDARD', 'ENHANCED']) AS l(dd_level)
) r;

-- Risk factors (§4.4): starter weights; the tenant's compliance sets its own.
INSERT INTO party.cfg_customer_risk_factors
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, factor, match_values, weight)
VALUES
  (NULL, 'PEP', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.4', 'migration (V0092)', 'Working default, for compliance to confirm before go-live', 'PEP', ARRAY['TRUE'], 60),
  (NULL, 'COUNTRY-HIGH-RISK', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.4', 'migration (V0092)', 'Working default (FATF call for action, October 2026), for compliance to confirm before go-live', 'COUNTRY', ARRAY['KP', 'IR', 'MM'], 60),
  (NULL, 'ORGANISATION', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.4', 'migration (V0092)', 'Working default, for compliance to confirm before go-live', 'PARTY_TYPE', ARRAY['ORGANISATION'], 10),
  (NULL, 'LIFE-PRODUCTS', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.4', 'migration (V0092)', 'Working default, for compliance to confirm before go-live', 'DOMAIN', ARRAY['LIFE'], 10),
  (NULL, 'NOT-MET', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §4.4', 'migration (V0092)', 'Working default, for compliance to confirm before go-live', 'CHANNEL', ARRAY['PORTAL'], 10);

-- Risk classes (OQ-124): LOW every 5 years, MEDIUM every 3, HIGH every year; HIGH needs enhanced due diligence.
INSERT INTO party.cfg_customer_risk_classes
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, risk_class, min_score, max_score, dd_level, review_interval)
VALUES
  (NULL, 'LOW', 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-124', 'migration (V0092)', 'OQ-124, decided 29 October 2026', 'LOW', -1000, 29, 'STANDARD', interval '5 years'),
  (NULL, 'MEDIUM', 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-124', 'migration (V0092)', 'OQ-124, decided 29 October 2026', 'MEDIUM', 30, 59, 'STANDARD', interval '3 years'),
  (NULL, 'HIGH', 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-124', 'migration (V0092)', 'OQ-124, decided 29 October 2026', 'HIGH', 60, NULL, 'ENHANCED', interval '1 year');

-- Gates (OQ-123, §5).
INSERT INTO party.cfg_due_diligence_gates
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, domain, step, requirement, screening_max_age)
SELECT NULL, g.domain || '-' || g.step, 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-123', 'migration (V0092)', 'OQ-123, decided 29 October 2026', g.domain, g.step, g.requirement, g.max_age
FROM (VALUES
  ('LIFE', 'QUOTE', 'NONE', NULL::interval), ('LIFE', 'ISSUE', 'CLEARED', NULL), ('LIFE', 'PAYOUT', 'CLEARED', interval '30 days'), ('LIFE', 'CLAIM_PAYOUT', 'CLEARED', interval '30 days'),
  ('GENERAL', 'QUOTE', 'NONE', NULL), ('GENERAL', 'ISSUE', 'NONE', NULL), ('GENERAL', 'PAYOUT', 'SCREENED', interval '30 days'), ('GENERAL', 'CLAIM_PAYOUT', 'SCREENED', interval '30 days'),
  ('HEALTH', 'QUOTE', 'NONE', NULL), ('HEALTH', 'ISSUE', 'NONE', NULL), ('HEALTH', 'PAYOUT', 'SCREENED', interval '30 days'), ('HEALTH', 'CLAIM_PAYOUT', 'SCREENED', interval '30 days'),
  ('ANY', 'PAYOUT', 'SCREENED', interval '30 days')
) AS g(domain, step, requirement, max_age);

-- §5: tenants that exist today start with the gates off until their readiness item is met; a new tenant entitled to
-- LIFE has them on (OQ-123), since a tenant with no settings row is gated.
INSERT INTO party.cfg_due_diligence_settings
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, gates_enabled, adverse_media_enabled)
SELECT t.id, 'DEFAULT', 1, DATE '2026-10-01', 'EFFECTIVE', 'Due diligence addendum §5', 'migration (V0092)',
       'An existing tenant starts with the gates off until its readiness item is met (§5)', false, false
FROM tenant.tenant t;

-- party.due_diligence: one per party (§3).
CREATE TABLE party.due_diligence (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  status                     varchar(12) NOT NULL,
  risk_class                 varchar(6),
  risk_score                 integer,
  risk_class_overridden      boolean NOT NULL DEFAULT false,
  dd_level                   varchar(12) NOT NULL,
  assessed_on                date,
  outstanding                text[] NOT NULL DEFAULT '{}',
  cleared_at                 timestamptz,
  cleared_by                 uuid,
  next_review_on             date,
  last_screened_at           timestamptz,
  screened_list_version      varchar(40),
  blocked                    boolean NOT NULL DEFAULT false,
  enhanced_requested_by      uuid,
  enhanced_approved_at       timestamptz,
  enhanced_approval_id       uuid,
  override_class             varchar(6),
  override_reason            varchar(200),
  override_requested_by      uuid,
  unblock_reason             varchar(200),
  unblock_requested_by       uuid,
  CONSTRAINT pk_due_diligence PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_due_diligence_party UNIQUE (tenant_id, party_id),
  CONSTRAINT fk_due_diligence_party FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id),
  CONSTRAINT ck_due_diligence_status CHECK (status IN ('NOT_STARTED', 'IN_PROGRESS', 'CLEARED', 'REFERRED', 'BLOCKED', 'EXPIRED')),
  CONSTRAINT ck_due_diligence_class CHECK (risk_class IS NULL OR risk_class IN ('LOW', 'MEDIUM', 'HIGH')),
  CONSTRAINT ck_due_diligence_override CHECK (override_class IS NULL OR override_class IN ('LOW', 'MEDIUM', 'HIGH')),
  CONSTRAINT ck_due_diligence_level CHECK (dd_level IN ('SIMPLIFIED', 'STANDARD', 'ENHANCED'))
);
CREATE INDEX ix_due_diligence_1 ON party.due_diligence (tenant_id, status, next_review_on);
COMMENT ON TABLE party.due_diligence IS 'F002, F003, F077: a party''s due diligence. Its status against the requirement sets, its risk class and level, when it was cleared and is next reviewed. HIGHLY_SENSITIVE.';

-- party.screening_hit: a match a screening returned, and its review (§3, §4.3).
CREATE TABLE party.screening_hit (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  due_diligence_id           uuid NOT NULL,
  check_id                   uuid NOT NULL,
  list_type                  varchar(15) NOT NULL,
  list_name                  varchar(80) NOT NULL,
  matched_name               varchar(150) NOT NULL,
  score                      numeric(5,2) NOT NULL,
  provider_ref               varchar(80),
  raised_at                  timestamptz NOT NULL,
  review                     varchar(15),
  review_reason              varchar(200),
  reviewed_by                uuid,
  reviewed_at                timestamptz,
  report_required            boolean NOT NULL DEFAULT false,
  report_type                varchar(5),
  fic_reference              varchar(40),
  reported_by                uuid,
  reported_at                timestamptz,
  CONSTRAINT pk_screening_hit PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_screening_hit_due_diligence FOREIGN KEY (tenant_id, due_diligence_id) REFERENCES party.due_diligence (tenant_id, id),
  CONSTRAINT fk_screening_hit_check FOREIGN KEY (tenant_id, check_id) REFERENCES party.due_diligence_check (tenant_id, id),
  CONSTRAINT ck_screening_hit_list CHECK (list_type IN ('SANCTIONS', 'PEP', 'ADVERSE_MEDIA')),
  CONSTRAINT ck_screening_hit_review CHECK (review IS NULL OR review IN ('TRUE_MATCH', 'FALSE_POSITIVE')),
  CONSTRAINT ck_screening_hit_reviewed CHECK ((review IS NULL) = (reviewed_by IS NULL) AND (review IS NULL OR review_reason IS NOT NULL)),
  CONSTRAINT ck_screening_hit_report CHECK (report_type IS NULL OR report_type IN ('S28A', 'S29')),
  CONSTRAINT ck_screening_hit_reported CHECK ((fic_reference IS NULL) = (reported_by IS NULL) AND (fic_reference IS NULL) = (report_type IS NULL))
);
CREATE INDEX ix_screening_hit_1 ON party.screening_hit (tenant_id, due_diligence_id);
CREATE INDEX ix_screening_hit_2 ON party.screening_hit (tenant_id, review) WHERE review IS NULL;
COMMENT ON TABLE party.screening_hit IS 'F003: a sanctions, PEP or adverse media match, its review (true match or false positive) and, for a true sanctions match, the report made to the FIC. HIGHLY_SENSITIVE: never in events, logs or exceptions.';

-- party.beneficial_owner: who owns or controls an organisation (§3, FICA s21B).
CREATE TABLE party.beneficial_owner (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  owner_party_id             uuid NOT NULL,
  control_type               varchar(15) NOT NULL,
  share_percent              numeric(5,2),
  established_by             varchar(12) NOT NULL,
  document_id                uuid,
  recorded_by                uuid NOT NULL,
  recorded_at                timestamptz NOT NULL,
  CONSTRAINT pk_beneficial_owner PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_beneficial_owner UNIQUE (tenant_id, party_id, owner_party_id),
  CONSTRAINT fk_beneficial_owner_party FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id),
  CONSTRAINT fk_beneficial_owner_owner FOREIGN KEY (tenant_id, owner_party_id) REFERENCES party.party (tenant_id, id),
  CONSTRAINT ck_beneficial_owner_control CHECK (control_type IN ('OWNERSHIP', 'CONTROL', 'SENIOR_MANAGER')),
  CONSTRAINT ck_beneficial_owner_share CHECK (share_percent IS NULL OR share_percent > 0 AND share_percent <= 100),
  CONSTRAINT ck_beneficial_owner_established CHECK (established_by IN ('DOCUMENT', 'REGISTER', 'DECLARATION')),
  CONSTRAINT ck_beneficial_owner_document CHECK (established_by <> 'DOCUMENT' OR document_id IS NOT NULL),
  CONSTRAINT ck_beneficial_owner_self CHECK (owner_party_id <> party_id)
);
COMMENT ON TABLE party.beneficial_owner IS 'F002: an organisation''s beneficial owner, itself a party with its own due diligence, its share or control and how it was established. HIGHLY_SENSITIVE.';

-- A screening check records the list version it was screened against, so a list update finds who to re-screen (OQ-125).
ALTER TABLE party.due_diligence_check ADD COLUMN list_version varchar(40);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['due_diligence', 'screening_hit', 'beneficial_owner'] LOOP
    EXECUTE format('ALTER TABLE party.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE party.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON party.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;

-- §4.3: the due diligence processes are named as signed off (DUE_DILIGENCE_HIT_REVIEW is 24 characters); case types already name processes of 30.
ALTER TABLE workflow.process_instance ALTER COLUMN process_code TYPE varchar(30);

-- §5: a payout to a payee not cleared is held, not refused: it is left out of payout batches until clearance.
ALTER TABLE payment.payment ADD COLUMN held_reason varchar(30), ADD COLUMN held_at timestamptz;
ALTER TABLE payment.payment ADD CONSTRAINT ck_payment_held CHECK ((held_reason IS NULL) = (held_at IS NULL));
COMMENT ON COLUMN payment.payment.held_reason IS 'Why a payout waits outside the payout batches (DUE_DILIGENCE_NOT_CLEARED: its payee is not cleared, addenda/due-diligence.md §5); null when it may be batched.';
