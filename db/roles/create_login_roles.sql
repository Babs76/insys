/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Database.Roles
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
-- Login roles for local development, tests and CI only (OQ-004 resolved).
-- Production creates its roles and credentials outside this repository.
-- {{PASSWORD}} is replaced by the migration runner from INSYS_ROLE_PASSWORD.
-- No role is a superuser, owns tables or has BYPASSRLS (DBA-002).
-- =====================================================================
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY[
    'insys_integration', 'insys_sdm', 'insys_party', 'insys_product', 'insys_rating', 'insys_rules', 'insys_workflow',
    'insys_document', 'insys_billing', 'insys_payment', 'insys_claims', 'insys_life', 'insys_general', 'insys_health',
    'insys_finance', 'insys_commission', 'insys_reinsurance', 'insys_reporting', 'insys_analytics', 'insys_tenant',
    'insys_iam', 'insys_audit', 'insys_api', 'insys_platform_admin_login'
  ] LOOP
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = r) THEN
      EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L', r, '{{PASSWORD}}');
    END IF;
  END LOOP;
END $$;

-- The platform administrator login acts through insys_platform_admin (created by V0002), whose policies are the
-- only ones that allow writing global static data rows (tenant_id IS NULL).
GRANT insys_platform_admin TO insys_platform_admin_login;
