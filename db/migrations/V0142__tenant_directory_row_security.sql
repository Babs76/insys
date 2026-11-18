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
-- V0142: tenant.tenant gets row-level security (Ade, 18 November 2026). Before, it had none: the Tenancy module's role could read every
-- tenant's row in any session, and only the API kept a business tenant from another's code and name.
--  * A session with app.tenant_id set sees only its own tenant's row.
--  * The platform operator's session sees every row, through an explicit mark: platform_operator, set only by the owner (the bootstrap,
--    and below for the tenant the bootstrap registered as OPERATOR). Never through "no tenant set": a session without one sees no row.
--  * The owner (migrations, the bootstrap, tenant.current_tenant_code()) is not subject to it.
-- =====================================================================
ALTER TABLE tenant.tenant ADD COLUMN platform_operator boolean NOT NULL DEFAULT false;
UPDATE tenant.tenant SET platform_operator = true WHERE tenant_code = 'OPERATOR';
CREATE UNIQUE INDEX uq_tenant_platform_operator ON tenant.tenant (platform_operator) WHERE platform_operator;
COMMENT ON COLUMN tenant.tenant.platform_operator IS 'The platform operator''s tenant (one at most), whose sessions see every tenant (V0142). Set only by the owner.';

-- Only the owner may mark or unmark the operator: a module role can't raise its own tenant to see the others.
CREATE FUNCTION tenant.guard_platform_operator() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (TG_OP = 'INSERT' AND NEW.platform_operator) OR (TG_OP = 'UPDATE' AND NEW.platform_operator IS DISTINCT FROM OLD.platform_operator) THEN
    IF current_user <> (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'tenant.tenant'::regclass) THEN
      RAISE EXCEPTION 'tenant %: only the database owner marks the platform operator (V0142)', NEW.tenant_code USING ERRCODE = 'insufficient_privilege';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER guard_platform_operator BEFORE INSERT OR UPDATE ON tenant.tenant FOR EACH ROW EXECUTE FUNCTION tenant.guard_platform_operator();

-- Whether this session is the platform operator's: read as the owner, so the policy below doesn't read the table through itself.
CREATE FUNCTION tenant.session_is_platform_operator() RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT coalesce((SELECT t.platform_operator FROM tenant.tenant t WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid), false)
$$;
-- Like tenant.current_tenant_code(): any role may ask, about its own session only.
REVOKE ALL ON FUNCTION tenant.session_is_platform_operator() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION tenant.session_is_platform_operator() TO PUBLIC;

ALTER TABLE tenant.tenant ENABLE ROW LEVEL SECURITY;
-- For every role but the owner; only the Tenancy module's role (insys_tenant) has a grant on the table (db/roles/module_grants.sql).
CREATE POLICY own_or_platform ON tenant.tenant
  USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR tenant.session_is_platform_operator())
  WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR tenant.session_is_platform_operator());
