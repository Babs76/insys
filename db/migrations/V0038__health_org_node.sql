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
-- V0038: the organisation node that owns each Health membership (F103, docs/specs/addenda/access-scopes.md).
-- Preauthorisations, medical claims, utilisation cases and fraud cases follow their membership. Health records have no
-- Product Factory product, so a product scope does not reach them (OQ-080). Existing memberships are placed at the
-- tenant's root legal entity where the tenant has one, as in V0036.
-- =====================================================================
ALTER TABLE health.membership ADD COLUMN org_node_code varchar(15);
CREATE INDEX ix_membership_org_node ON health.membership (tenant_id, org_node_code);

-- Backfill tenant by tenant: the table has forced row-level security, so each tenant is set in turn.
DO $$
DECLARE
  t uuid;
  root varchar(15);
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    SELECT node_code INTO root FROM tenant.mst_organisation_structure
    WHERE tenant_id = t AND parent IS NULL AND record_status IN ('APPROVED', 'ACTIVE') ORDER BY node_code LIMIT 1;
    IF root IS NOT NULL THEN
      UPDATE health.membership SET org_node_code = root WHERE org_node_code IS NULL;
    END IF;
  END LOOP;
END $$;
