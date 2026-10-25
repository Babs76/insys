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
-- V0052: the function catalogue (ADR-026, addenda/function-catalogue.md, signed off 25 October 2026).
-- A role is granted functions; functions replace permission sets as what a role holds, and a user's permissions are
-- those the functions of their roles in effect require.
-- - iam.ref_business_domains, iam.ref_business_sub_domains and iam.ref_function_catalogue: global, shipped with each
--   release (R__function_catalogue.sql, generated from the code's declarations).
-- - tenant.cfg_tenant_function_availability: a tenant disables a function (a function with no row is enabled).
-- - iam.cfg_role_function_assignments: a role's functions.
-- - iam.cfg_roles_and_permission_sets.permissions is retired (nullable; new versions carry none); segregation-of-duties
--   rules pair functions; channel transaction rows name a function. iam.adopt_function_catalogue() converts what is
--   there, once the catalogue is loaded, with no change in what any user can do.
-- - tenant.cfg_feature_entitlements.feature is one of the seven features (OQ-091).
-- =====================================================================
-- Identity and Access Management · Function catalogue · Business domains
CREATE TABLE iam.ref_business_domains (
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
  domain_code                    varchar(30) NOT NULL,
  icon                           varchar(40),
  CONSTRAINT ref_business_domains_pk PRIMARY KEY (id),
  CONSTRAINT ref_business_domains_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_business_domains_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_business_domains_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_business_domains_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_business_domains_glob CHECK (tenant_id IS NULL),
  CONSTRAINT ref_business_domains_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED'))
);
CREATE INDEX ref_business_domains_lookup ON iam.ref_business_domains (tenant_id, code, effective_from);
ALTER TABLE iam.ref_business_domains ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.ref_business_domains FORCE ROW LEVEL SECURITY;
CREATE POLICY read_all ON iam.ref_business_domains FOR SELECT USING (true);
CREATE POLICY platform_maintain ON iam.ref_business_domains TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON iam.ref_business_domains
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE iam.ref_business_domains IS 'Business domains. REF, scope G, Tier 2. The menu''s top level (docs/specs/INSYS_NAVIGATION.md); labels are translation keys. Added in Wave 1 batch 3 (ADR-026).';

-- Identity and Access Management · Function catalogue · Business sub-domains
CREATE TABLE iam.ref_business_sub_domains (
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
  sub_domain_code                varchar(40) NOT NULL,
  domain_code                    varchar(30) NOT NULL,
  icon                           varchar(40),
  CONSTRAINT ref_business_sub_domains_pk PRIMARY KEY (id),
  CONSTRAINT ref_business_sub_domains_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_business_sub_domains_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_business_sub_domains_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_business_sub_domains_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_business_sub_domains_glob CHECK (tenant_id IS NULL),
  CONSTRAINT ref_business_sub_domains_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED'))
);
CREATE INDEX ref_business_sub_domains_lookup ON iam.ref_business_sub_domains (tenant_id, code, effective_from);
ALTER TABLE iam.ref_business_sub_domains ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.ref_business_sub_domains FORCE ROW LEVEL SECURITY;
CREATE POLICY read_all ON iam.ref_business_sub_domains FOR SELECT USING (true);
CREATE POLICY platform_maintain ON iam.ref_business_sub_domains TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON iam.ref_business_sub_domains
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE iam.ref_business_sub_domains IS 'Business sub-domains. REF, scope G, Tier 2. The menu''s second level, one sidebar item each. Added in Wave 1 batch 3 (ADR-026).';

-- Identity and Access Management · Function catalogue · Functions
CREATE TABLE iam.ref_function_catalogue (
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
  function_code                  varchar(80) NOT NULL,
  sub_domain_code                varchar(40) NOT NULL,
  process_area                   varchar(80) NOT NULL,
  interaction_type               varchar(10) NOT NULL,
  function_kind                  varchar(10) NOT NULL,
  parent_function_code           varchar(80),
  audiences                      text[] NOT NULL,
  route_key                      varchar(120),
  required_permissions           text[] NOT NULL,
  required_feature               varchar(30),
  privileged                     boolean NOT NULL DEFAULT false,
  allowed_while_onboarding       boolean NOT NULL DEFAULT false,
  CONSTRAINT ref_function_catalogue_pk PRIMARY KEY (id),
  CONSTRAINT ref_function_catalogue_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_function_catalogue_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_function_catalogue_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_function_catalogue_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_function_catalogue_glob CHECK (tenant_id IS NULL),
  CONSTRAINT ref_function_catalogue_ck1 CHECK (interaction_type IN ('HUMAN', 'SYSTEM')),
  CONSTRAINT ref_function_catalogue_ck2 CHECK (function_kind IN ('SCREEN', 'ACTION', 'JOB')),
  CONSTRAINT ref_function_catalogue_ck3 CHECK (audiences <@ ARRAY['PLATFORM', 'STAFF', 'BROKER', 'PROVIDER', 'CUSTOMER']::text[]),
  CONSTRAINT ref_function_catalogue_ck4 CHECK (required_feature IN ('LIFE', 'GENERAL', 'HEALTH', 'REINSURANCE', 'BROKER_PORTAL', 'PROVIDER_PORTAL', 'CUSTOMER_PORTAL')),
  CONSTRAINT ref_function_catalogue_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED'))
);
CREATE INDEX ref_function_catalogue_lookup ON iam.ref_function_catalogue (tenant_id, code, effective_from);
ALTER TABLE iam.ref_function_catalogue ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.ref_function_catalogue FORCE ROW LEVEL SECURITY;
CREATE POLICY read_all ON iam.ref_function_catalogue FOR SELECT USING (true);
CREATE POLICY platform_maintain ON iam.ref_function_catalogue TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON iam.ref_function_catalogue
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE iam.ref_function_catalogue IS 'Function catalogue. REF, scope G, Tier 1. Every screen, action and job; what each requires; platform-maintained. Added in Wave 1 batch 3 (ADR-026).';

-- Tenant Management · Function availability
CREATE TABLE tenant.cfg_tenant_function_availability (
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
  function_code                  varchar(80) NOT NULL,
  enabled                        boolean NOT NULL,
  CONSTRAINT cfg_tenant_function_availability_pk PRIMARY KEY (id),
  CONSTRAINT cfg_tenant_function_availability_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_tenant_function_availability_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_tenant_function_availability_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_tenant_function_availability_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_tenant_function_availability_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_tenant_function_availability_lookup ON tenant.cfg_tenant_function_availability (tenant_id, code, effective_from);
ALTER TABLE tenant.cfg_tenant_function_availability ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant.cfg_tenant_function_availability FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant.cfg_tenant_function_availability
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON tenant.cfg_tenant_function_availability
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE tenant.cfg_tenant_function_availability IS 'Tenant function availability. CFG, scope T, Tier 1. A tenant enables or disables a catalogue function; no row means enabled. Added in Wave 1 batch 3 (ADR-026).';

-- Identity and Access Management · Role-based access control · Role function assignments
CREATE TABLE iam.cfg_role_function_assignments (
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
  role_code                      varchar(30) NOT NULL,
  function_code                  varchar(80) NOT NULL,
  CONSTRAINT cfg_role_function_assignments_pk PRIMARY KEY (id),
  CONSTRAINT cfg_role_function_assignments_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_role_function_assignments_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_role_function_assignments_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_role_function_assignments_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_role_function_assignments_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_role_function_assignments_lookup ON iam.cfg_role_function_assignments (tenant_id, code, effective_from);
ALTER TABLE iam.cfg_role_function_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.cfg_role_function_assignments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.cfg_role_function_assignments
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON iam.cfg_role_function_assignments
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE iam.cfg_role_function_assignments IS 'Role function assignments. CFG, scope T, Tier 1. What a role holds: one row per function granted. Added in Wave 1 batch 3 (ADR-026).';

CREATE INDEX cfg_role_function_assignments_role ON iam.cfg_role_function_assignments (tenant_id, role_code);
CREATE INDEX ref_function_catalogue_function ON iam.ref_function_catalogue (function_code);

-- Retired: a role holds functions now. Kept on old versions as their record.
ALTER TABLE iam.cfg_roles_and_permission_sets ALTER COLUMN permissions DROP NOT NULL;
COMMENT ON COLUMN iam.cfg_roles_and_permission_sets.permissions IS 'Retired by ADR-026: a role holds functions (iam.cfg_role_function_assignments). Null on new versions.';

-- Segregation of duties on function pairs.
ALTER TABLE iam.cfg_segregation_of_duties_conflict_rules ADD COLUMN function_a varchar(80), ADD COLUMN function_b varchar(80);
ALTER TABLE iam.cfg_segregation_of_duties_conflict_rules ALTER COLUMN permission_a DROP NOT NULL, ALTER COLUMN permission_b DROP NOT NULL;
COMMENT ON COLUMN iam.cfg_segregation_of_duties_conflict_rules.permission_a IS 'Retired by ADR-026: rules pair functions (function_a, function_b).';

-- Channel transaction permissions key on the function.
ALTER TABLE channel.cfg_channel_transaction_permissions ADD COLUMN function_code varchar(80);
ALTER TABLE channel.cfg_channel_transaction_permissions ALTER COLUMN transaction_type DROP NOT NULL;
COMMENT ON COLUMN channel.cfg_channel_transaction_permissions.transaction_type IS 'Retired by ADR-026: rows name the function (function_code).';

ALTER TABLE tenant.cfg_feature_entitlements ADD CONSTRAINT cfg_feature_entitlements_ck1 CHECK (feature IN ('LIFE', 'GENERAL', 'HEALTH', 'REINSURANCE', 'BROKER_PORTAL', 'PROVIDER_PORTAL', 'CUSTOMER_PORTAL'));

-- The functions whose required permissions a permission set holds: what that set becomes.
CREATE OR REPLACE FUNCTION iam.functions_within(p_permissions text[], p_day date DEFAULT current_date)
RETURNS SETOF varchar LANGUAGE sql STABLE AS $$
  SELECT DISTINCT f.function_code FROM iam.ref_function_catalogue f
  WHERE f.tenant_id IS NULL AND f.record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') AND f.effective_from <= p_day
    AND (f.effective_to IS NULL OR f.effective_to > p_day)
    AND cardinality(f.required_permissions) > 0 AND f.required_permissions <@ p_permissions
$$;

-- Converts what holds permissions to function grants (ADR-026 §7): roles in force, segregation-of-duties rules and channel
-- rows. Each old version is retired and kept as the record; the new versions are effective from the same day, approved as
-- 'migration (ADR-026)'. Idempotent: what is converted is not converted again. Run by R__function_catalogue.sql.
CREATE OR REPLACE FUNCTION iam.adopt_function_catalogue() RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  r record;
  f varchar;
  n int;
  pair record;
BEGIN
  FOR r IN SELECT * FROM iam.cfg_roles_and_permission_sets
           WHERE tenant_id IS NOT NULL AND permissions IS NOT NULL AND record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE')
             AND (effective_to IS NULL OR effective_to > current_date) LOOP
    FOR f IN SELECT * FROM iam.functions_within(r.permissions, greatest(r.effective_from, current_date)) LOOP
      INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, effective_to, record_status, created_by, approved_at,
                                                      approved_by, change_reason, role_code, function_code)
      VALUES (r.tenant_id, r.role_code || ':' || f, 1, r.effective_from, r.effective_to, 'EFFECTIVE', 'migration (ADR-026)', now(), 'migration (ADR-026)',
              'Converted from the role''s permission set (ADR-026)', r.role_code, f)
      ON CONFLICT DO NOTHING;
    END LOOP;
    UPDATE iam.cfg_roles_and_permission_sets SET record_status = 'RETIRED' WHERE id = r.id;
    INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, effective_to, record_status, supersedes_id, labels, description,
                                                   created_by, approved_at, approved_by, change_reason, role_code, permissions, scope_products, scope_branches)
    VALUES (r.tenant_id, r.code, (SELECT max(version) + 1 FROM iam.cfg_roles_and_permission_sets WHERE tenant_id = r.tenant_id AND code = r.code),
            r.effective_from, r.effective_to, r.record_status, r.id, r.labels, r.description, 'migration (ADR-026)', now(), 'migration (ADR-026)',
            'Permissions retired: the role holds functions (ADR-026)', r.role_code, NULL, r.scope_products, r.scope_branches);
  END LOOP;

  FOR r IN SELECT * FROM iam.cfg_segregation_of_duties_conflict_rules
           WHERE function_a IS NULL AND permission_a IS NOT NULL AND record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE')
             AND (effective_to IS NULL OR effective_to > current_date) LOOP
    n := 0;
    FOR pair IN SELECT a.function_code AS fa, b.function_code AS fb
                FROM iam.ref_function_catalogue a, iam.ref_function_catalogue b
                WHERE a.tenant_id IS NULL AND b.tenant_id IS NULL AND r.permission_a = ANY(a.required_permissions) AND r.permission_b = ANY(b.required_permissions)
                  AND a.record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') AND b.record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') LOOP
      n := n + 1;
      INSERT INTO iam.cfg_segregation_of_duties_conflict_rules (tenant_id, code, version, effective_from, effective_to, record_status, supersedes_id, created_by,
                                                                approved_at, approved_by, change_reason, conflict, override_role, function_a, function_b)
      VALUES (r.tenant_id, left(r.code || '#' || n, 120), 1, r.effective_from, r.effective_to, r.record_status, r.id, 'migration (ADR-026)', now(),
              'migration (ADR-026)', 'Converted from a permission pair (ADR-026)', r.conflict, r.override_role, pair.fa, pair.fb)
      ON CONFLICT DO NOTHING;
    END LOOP;
    UPDATE iam.cfg_segregation_of_duties_conflict_rules SET record_status = 'RETIRED' WHERE id = r.id;
  END LOOP;

  FOR r IN SELECT * FROM channel.cfg_channel_transaction_permissions
           WHERE function_code IS NULL AND transaction_type IS NOT NULL AND record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE')
             AND (effective_to IS NULL OR effective_to > current_date) LOOP
    n := 0;
    FOR f IN SELECT c.function_code FROM iam.ref_function_catalogue c
             WHERE c.tenant_id IS NULL AND c.function_kind = 'ACTION' AND r.transaction_type = ANY(c.required_permissions)
               AND c.record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') LOOP
      n := n + 1;
      INSERT INTO channel.cfg_channel_transaction_permissions (tenant_id, code, version, effective_from, effective_to, record_status, supersedes_id,
                                                               created_by, approved_at, approved_by, change_reason, channel_code, role_code, product_code,
                                                               allowed, requires_approval, function_code)
      VALUES (r.tenant_id, left(r.code || '#' || n, 120), 1, r.effective_from, r.effective_to, r.record_status, r.id, 'migration (ADR-026)', now(),
              'migration (ADR-026)', 'Converted from a transaction type (ADR-026)', r.channel_code, r.role_code, r.product_code, r.allowed,
              r.requires_approval, f)
      ON CONFLICT DO NOTHING;
    END LOOP;
    UPDATE channel.cfg_channel_transaction_permissions SET record_status = 'RETIRED' WHERE id = r.id;
  END LOOP;
END $$;
