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
-- V0135: scope batch 1 (F103; OQ-173, OQ-176). An approval about a record scoped by product alone, which has no organisation node of its own (a product
-- version), is judged by the checker's product scope only: a branch-limited checker isn't refused it for want of a node. Existing approvals keep the full rule.
ALTER TABLE workflow.approval ADD COLUMN org_node_applies boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN workflow.approval.org_node_applies IS 'False for a record scoped by product only (a product version, OQ-173): the checker''s organisation scope does not apply.';

-- OQ-176, fail closed for approvals already waiting: the types that had no scope source until now are scoped. A product version's takes its product, by
-- product only; the rest are scoped to nothing, so only a checker with unrestricted scope sees and decides them. Decided approvals are left as they were.
DO $$
DECLARE t uuid;
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    UPDATE workflow.approval a SET is_scoped = true, product_code = v.product_code, org_node_code = NULL, org_node_applies = false
    FROM product.cfg_product_definitions_and_versions v
    WHERE a.business_ref_type = 'PRODUCT_VERSION' AND a.status = 'REQUESTED' AND NOT a.is_scoped AND v.tenant_id = a.tenant_id AND v.id = a.business_ref_id;
    UPDATE workflow.approval SET is_scoped = true, product_code = NULL, org_node_code = NULL
    WHERE status = 'REQUESTED' AND NOT is_scoped
      AND business_ref_type IN ('PRODUCT_VERSION', 'REFUND_REQUEST', 'PAYMENT', 'CASE_REDRESS', 'DOCUMENT_LEGAL_HOLD', 'RECORD_LEGAL_HOLD', 'GENERAL_RENEWAL',
                                'HEALTH_RECOVERY', 'HEALTH_CREDIT_NOTE');
  END LOOP;
END $$;
