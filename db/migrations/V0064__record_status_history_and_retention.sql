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
-- V0064: when a party's retention has ended (D-34, OQ-120 working default, 27 October 2026).
-- * Status history for the policies, memberships and claims that keep a party's data, so retention runs from the business
--   date a record reached its final status. Written by a trigger on every insert and status change, whatever the code
--   path; insert-only (DBA-021). Existing records get one row: their current status, dated by their last change.
-- * party.cfg_record_retention_policies: how long a record kind (POLICY, CLAIM, PAYMENT) keeps a party's data after it
--   reaches its final status. Scope GT: the platform's rows are the working default, 7 years (FICA s23: at least 5 years
--   from the end of the relationship or transaction; Companies Act s24(1): 7 years for company records), and a tenant's
--   own rows replace them. A working default for compliance to confirm before go-live, not a requirement.
-- =====================================================================

CREATE FUNCTION life.record_status_history() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
    EXECUTE format('INSERT INTO %I.%I (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at) '
                   'VALUES ($1, $2, now(), $3, $4, $5, $6, $7)', TG_TABLE_SCHEMA, TG_TABLE_NAME || '_status_history')
      USING NEW.tenant_id, gen_random_uuid(), NEW.updated_by, NEW.id,
            CASE WHEN TG_OP = 'UPDATE' THEN OLD.status END, NEW.status,
            -- The application's clock when it stamped the change; the database's when a change left updated_at as it was.
            CASE WHEN TG_OP = 'INSERT' OR NEW.updated_at IS DISTINCT FROM OLD.updated_at THEN NEW.updated_at ELSE now() END;
  END IF;
  RETURN NULL;
END $$;

CREATE FUNCTION life.refuse_history_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is a history: records are never changed (DBA-021)', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END $$;

CREATE FUNCTION general.record_status_history() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
    EXECUTE format('INSERT INTO %I.%I (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at) '
                   'VALUES ($1, $2, now(), $3, $4, $5, $6, $7)', TG_TABLE_SCHEMA, TG_TABLE_NAME || '_status_history')
      USING NEW.tenant_id, gen_random_uuid(), NEW.updated_by, NEW.id,
            CASE WHEN TG_OP = 'UPDATE' THEN OLD.status END, NEW.status,
            -- The application's clock when it stamped the change; the database's when a change left updated_at as it was.
            CASE WHEN TG_OP = 'INSERT' OR NEW.updated_at IS DISTINCT FROM OLD.updated_at THEN NEW.updated_at ELSE now() END;
  END IF;
  RETURN NULL;
END $$;

CREATE FUNCTION general.refuse_history_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is a history: records are never changed (DBA-021)', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END $$;

CREATE FUNCTION health.record_status_history() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
    EXECUTE format('INSERT INTO %I.%I (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at) '
                   'VALUES ($1, $2, now(), $3, $4, $5, $6, $7)', TG_TABLE_SCHEMA, TG_TABLE_NAME || '_status_history')
      USING NEW.tenant_id, gen_random_uuid(), NEW.updated_by, NEW.id,
            CASE WHEN TG_OP = 'UPDATE' THEN OLD.status END, NEW.status,
            -- The application's clock when it stamped the change; the database's when a change left updated_at as it was.
            CASE WHEN TG_OP = 'INSERT' OR NEW.updated_at IS DISTINCT FROM OLD.updated_at THEN NEW.updated_at ELSE now() END;
  END IF;
  RETURN NULL;
END $$;

CREATE FUNCTION health.refuse_history_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is a history: records are never changed (DBA-021)', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END $$;

CREATE TABLE life.policy_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_policy_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_policy_status_history_record ON life.policy_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE life.policy_status_history IS 'D-34: every status life.policy has had, from the trigger on it; from_status is null for the first. Insert-only.';

CREATE TABLE life.claim_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_claim_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_claim_status_history_record ON life.claim_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE life.claim_status_history IS 'D-34: every status life.claim has had, from the trigger on it; from_status is null for the first. Insert-only.';

CREATE TABLE general.policy_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_policy_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_policy_status_history_record ON general.policy_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE general.policy_status_history IS 'D-34: every status general.policy has had, from the trigger on it; from_status is null for the first. Insert-only.';

CREATE TABLE general.claim_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_claim_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_claim_status_history_record ON general.claim_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE general.claim_status_history IS 'D-34: every status general.claim has had, from the trigger on it; from_status is null for the first. Insert-only.';

CREATE TABLE health.membership_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_membership_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_membership_status_history_record ON health.membership_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE health.membership_status_history IS 'D-34: every status health.membership has had, from the trigger on it; from_status is null for the first. Insert-only.';

CREATE TABLE health.medical_claim_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  record_id                  uuid NOT NULL,
  from_status                varchar(40),
  to_status                  varchar(40) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_medical_claim_status_history PRIMARY KEY (tenant_id, id)
);
CREATE INDEX ix_medical_claim_status_history_record ON health.medical_claim_status_history (tenant_id, record_id, changed_at);
COMMENT ON TABLE health.medical_claim_status_history IS 'D-34: every status health.medical_claim has had, from the trigger on it; from_status is null for the first. Insert-only.';

-- Existing records: their current status, dated by their last change. Tenant by tenant (forced row-level security).
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    INSERT INTO life.policy_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM life.policy;
    INSERT INTO life.claim_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM life.claim;
    INSERT INTO general.policy_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM general.policy;
    INSERT INTO general.claim_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM general.claim;
    INSERT INTO health.membership_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM health.membership;
    INSERT INTO health.medical_claim_status_history (tenant_id, id, created_at, created_by, record_id, from_status, to_status, changed_at)
    SELECT tenant_id, gen_random_uuid(), now(), 'migration (V0064)', id, NULL, status, updated_at FROM health.medical_claim;
  END LOOP;
END $$;

ALTER TABLE life.policy_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER policy_status_history_immutable BEFORE UPDATE OR DELETE ON life.policy_status_history
  FOR EACH ROW EXECUTE FUNCTION life.refuse_history_change();
CREATE TRIGGER policy_status_history AFTER INSERT OR UPDATE OF status ON life.policy
  FOR EACH ROW EXECUTE FUNCTION life.record_status_history();

ALTER TABLE life.claim_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.claim_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.claim_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER claim_status_history_immutable BEFORE UPDATE OR DELETE ON life.claim_status_history
  FOR EACH ROW EXECUTE FUNCTION life.refuse_history_change();
CREATE TRIGGER claim_status_history AFTER INSERT OR UPDATE OF status ON life.claim
  FOR EACH ROW EXECUTE FUNCTION life.record_status_history();

ALTER TABLE general.policy_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.policy_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.policy_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER policy_status_history_immutable BEFORE UPDATE OR DELETE ON general.policy_status_history
  FOR EACH ROW EXECUTE FUNCTION general.refuse_history_change();
CREATE TRIGGER policy_status_history AFTER INSERT OR UPDATE OF status ON general.policy
  FOR EACH ROW EXECUTE FUNCTION general.record_status_history();

ALTER TABLE general.claim_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.claim_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.claim_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER claim_status_history_immutable BEFORE UPDATE OR DELETE ON general.claim_status_history
  FOR EACH ROW EXECUTE FUNCTION general.refuse_history_change();
CREATE TRIGGER claim_status_history AFTER INSERT OR UPDATE OF status ON general.claim
  FOR EACH ROW EXECUTE FUNCTION general.record_status_history();

ALTER TABLE health.membership_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.membership_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.membership_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER membership_status_history_immutable BEFORE UPDATE OR DELETE ON health.membership_status_history
  FOR EACH ROW EXECUTE FUNCTION health.refuse_history_change();
CREATE TRIGGER membership_status_history AFTER INSERT OR UPDATE OF status ON health.membership
  FOR EACH ROW EXECUTE FUNCTION health.record_status_history();

ALTER TABLE health.medical_claim_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.medical_claim_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.medical_claim_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER medical_claim_status_history_immutable BEFORE UPDATE OR DELETE ON health.medical_claim_status_history
  FOR EACH ROW EXECUTE FUNCTION health.refuse_history_change();
CREATE TRIGGER medical_claim_status_history AFTER INSERT OR UPDATE OF status ON health.medical_claim
  FOR EACH ROW EXECUTE FUNCTION health.record_status_history();

-- Party · Retention · Record retention policies
CREATE TABLE party.cfg_record_retention_policies (
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
  record_kind                    varchar(20) NOT NULL,
  jurisdiction                   varchar(80) NOT NULL,
  period                         interval NOT NULL,
  basis                          varchar(200),
  CONSTRAINT cfg_record_retention_policies_pk PRIMARY KEY (id),
  CONSTRAINT cfg_record_retention_policies_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_record_retention_policies_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_record_retention_policies_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_record_retention_policies_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_record_retention_policies_ck1 CHECK (record_kind IN ('POLICY', 'CLAIM', 'PAYMENT')),
  CONSTRAINT cfg_record_retention_policies_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_record_retention_policies_lookup ON party.cfg_record_retention_policies (tenant_id, code, effective_from);

-- The working default (OQ-120): 7 years for each kind, platform-wide, until a tenant configures its own.
INSERT INTO party.cfg_record_retention_policies
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, record_kind, jurisdiction, period, basis)
SELECT NULL, 'DEFAULT-' || kind, 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-120 working default', 'migration (V0064)',
       'OQ-120 working default, for compliance to confirm before go-live', kind, 'ZA', interval '7 years',
       'Working default: FICA s23 (at least 5 years from the end of the relationship or transaction); Companies Act s24(1) (7 years)'
FROM unnest(ARRAY['POLICY', 'CLAIM', 'PAYMENT']) AS kind;

ALTER TABLE party.cfg_record_retention_policies ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.cfg_record_retention_policies FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON party.cfg_record_retention_policies FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON party.cfg_record_retention_policies FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON party.cfg_record_retention_policies FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON party.cfg_record_retention_policies FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON party.cfg_record_retention_policies TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON party.cfg_record_retention_policies
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE party.cfg_record_retention_policies IS 'Record retention policies. CFG, scope GT, Tier 1. How long a policy, claim or payment keeps a party''s data after its final status (D-34); the platform rows are the OQ-120 working default of 7 years. Added in Wave 2 batch 3.';
