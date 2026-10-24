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
-- V0039: the product and organisation node of each registered claim (F103, docs/specs/addenda/access-scopes.md §1).
-- The domain gives them when it registers its claim; the claim's payment instructions, reserves and evidence are scoped
-- by them. Existing registrations take them from their General or Life claim (V0036, V0037).
-- =====================================================================
ALTER TABLE claims.claim_register ADD COLUMN product_code varchar(15), ADD COLUMN org_node_code varchar(15);

-- Backfill tenant by tenant: these tables have forced row-level security, so each tenant is set in turn.
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    UPDATE claims.claim_register r SET product_code = c.product_code, org_node_code = c.org_node_code FROM general.claim c
    WHERE r.domain = 'GENERAL' AND c.tenant_id = r.tenant_id AND c.id = r.domain_claim_id AND r.product_code IS NULL;
    UPDATE claims.claim_register r SET product_code = c.product_code, org_node_code = c.org_node_code FROM life.claim c
    WHERE r.domain = 'LIFE' AND c.tenant_id = r.tenant_id AND c.id = r.domain_claim_id AND r.product_code IS NULL;
  END LOOP;
END $$;
