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
-- V0036: the organisation node that owns each General root record (F103, docs/specs/addenda/access-scopes.md, signed off
-- 23 October 2026): quotes, policies and claims. Proposals and endorsements follow their quote and policy. A claim also
-- keeps its policy's product, for scoping claim lists. Existing records are placed at the tenant's root legal entity
-- (a node of tenant.mst_organisation_structure with no parent) where the tenant has one; without a structure there is no
-- organisation scoping, and the column stays NULL.
-- =====================================================================
ALTER TABLE general.quote ADD COLUMN org_node_code varchar(15);
ALTER TABLE general.policy ADD COLUMN org_node_code varchar(15);
ALTER TABLE general.claim ADD COLUMN org_node_code varchar(15), ADD COLUMN product_code varchar(15);
CREATE INDEX ix_quote_org_node ON general.quote (tenant_id, org_node_code);
CREATE INDEX ix_policy_org_node ON general.policy (tenant_id, org_node_code);
CREATE INDEX ix_claim_org_node ON general.claim (tenant_id, org_node_code);

-- Backfill tenant by tenant: these tables have forced row-level security, so each tenant is set in turn.
DO $$
DECLARE
  t uuid;
  root varchar(15);
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    UPDATE general.claim c SET product_code = p.product_code FROM general.policy p
    WHERE p.tenant_id = c.tenant_id AND p.id = c.policy_id AND c.product_code IS NULL;
    SELECT node_code INTO root FROM tenant.mst_organisation_structure
    WHERE tenant_id = t AND parent IS NULL AND record_status IN ('APPROVED', 'ACTIVE') ORDER BY node_code LIMIT 1;
    IF root IS NOT NULL THEN
      UPDATE general.quote SET org_node_code = root WHERE org_node_code IS NULL;
      UPDATE general.policy SET org_node_code = root WHERE org_node_code IS NULL;
      UPDATE general.claim SET org_node_code = root WHERE org_node_code IS NULL;
    END IF;
  END LOOP;
END $$;
