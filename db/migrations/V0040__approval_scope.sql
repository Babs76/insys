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
-- V0040: the scope of the record an approval is about (F103, docs/specs/addenda/access-scopes.md §4). An approval about
-- a scoped record (General endorsements and claim settlements, Life underwriting cases and claims, claim payment
-- instructions) carries its product and organisation node; a checker sees and decides it only within their scope.
-- Existing approvals of those types take them from their record (V0036, V0037, V0039).
-- =====================================================================
ALTER TABLE workflow.approval ADD COLUMN is_scoped boolean NOT NULL DEFAULT false,
  ADD COLUMN product_code varchar(15), ADD COLUMN org_node_code varchar(15);

-- Backfill tenant by tenant: these tables have forced row-level security, so each tenant is set in turn.
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    UPDATE workflow.approval a SET is_scoped = true, product_code = p.product_code, org_node_code = p.org_node_code
    FROM general.endorsement e JOIN general.policy p ON p.tenant_id = e.tenant_id AND p.id = e.policy_id
    WHERE a.business_ref_type = 'GENERAL_ENDORSEMENT' AND e.tenant_id = a.tenant_id AND e.id = a.business_ref_id;
    UPDATE workflow.approval a SET is_scoped = true, product_code = c.product_code, org_node_code = c.org_node_code FROM general.claim c
    WHERE a.business_ref_type = 'GENERAL_CLAIM' AND c.tenant_id = a.tenant_id AND c.id = a.business_ref_id;
    UPDATE workflow.approval a SET is_scoped = true, product_code = p.product_code, org_node_code = p.org_node_code
    FROM life.underwriting_case u JOIN life.proposal p ON p.tenant_id = u.tenant_id AND p.id = u.proposal_id
    WHERE a.business_ref_type = 'LIFE_UNDERWRITING_CASE' AND u.tenant_id = a.tenant_id AND u.id = a.business_ref_id;
    UPDATE workflow.approval a SET is_scoped = true, product_code = c.product_code, org_node_code = c.org_node_code FROM life.claim c
    WHERE a.business_ref_type = 'LIFE_CLAIM' AND c.tenant_id = a.tenant_id AND c.id = a.business_ref_id;
    UPDATE workflow.approval a SET is_scoped = true, product_code = r.product_code, org_node_code = r.org_node_code
    FROM claims.claim_payment_instruction i JOIN claims.claim_register r ON r.tenant_id = i.tenant_id AND r.claim_number = i.claim_number
    WHERE a.business_ref_type = 'CLAIM_PAYMENT' AND i.tenant_id = a.tenant_id AND i.id = a.business_ref_id;
  END LOOP;
END $$;
