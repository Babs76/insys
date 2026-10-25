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
-- V0057: global static data rows that name a tenant are that tenant's to read (front-end request BR-007, isolation).
-- Platform-maintained (scope G or GT) tables with a `tenant` column let every tenant read every row, so a tenant saw
-- which features, isolation profile and operational limits every other tenant has. Now a row naming a tenant is
-- readable by that tenant only (its tenant code); the platform operator still reads and maintains all of them through
-- `…/global` (insys_platform_admin). Tables: tenant.cfg_feature_entitlements, tenant.cfg_isolation_profile_assignment,
-- tenant.cfg_operational_limits (G); product.cfg_product_approval_workflow_settings (GT: a tenant's own rows, and the
-- global rows naming it). IntegrationTests' TenantNamedRowsTests fails if any static table with a `tenant` column leaks.
-- =====================================================================

-- The code of the tenant this transaction is for (app.tenant_id), for row-level security across schemas. It returns
-- only the caller's own tenant code.
CREATE FUNCTION tenant.current_tenant_code() RETURNS varchar LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT t.tenant_code FROM tenant.tenant t WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;
REVOKE ALL ON FUNCTION tenant.current_tenant_code() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION tenant.current_tenant_code() TO PUBLIC;
COMMENT ON FUNCTION tenant.current_tenant_code() IS 'The tenant code of app.tenant_id; for row-level security on rows that name a tenant (BR-007).';

DROP POLICY read_all ON tenant.cfg_feature_entitlements;
CREATE POLICY read_own ON tenant.cfg_feature_entitlements FOR SELECT USING (tenant = tenant.current_tenant_code());

DROP POLICY read_all ON tenant.cfg_isolation_profile_assignment;
CREATE POLICY read_own ON tenant.cfg_isolation_profile_assignment FOR SELECT USING (tenant = tenant.current_tenant_code());

DROP POLICY read_all ON tenant.cfg_operational_limits;
CREATE POLICY read_own ON tenant.cfg_operational_limits FOR SELECT USING (tenant = tenant.current_tenant_code());

DROP POLICY read_global_and_own ON product.cfg_product_approval_workflow_settings;
CREATE POLICY read_own_and_named ON product.cfg_product_approval_workflow_settings FOR SELECT
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
         OR (tenant_id IS NULL AND tenant = tenant.current_tenant_code()));
