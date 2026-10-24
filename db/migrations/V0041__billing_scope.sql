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
-- V0041: the product and organisation node of the policy or membership a premium schedule bills, carried on its
-- invoices (F103, docs/specs/addenda/access-scopes.md §1). An invoice is scoped by them; an account, when any of its
-- schedules is in scope. Health schedules have a node and no product (OQ-080). Existing schedules and invoices take them
-- from their General or Life policy or Health membership (V0036 to V0038).
-- =====================================================================
ALTER TABLE billing.premium_schedule ADD COLUMN product_code varchar(15), ADD COLUMN org_node_code varchar(15);
ALTER TABLE billing.invoice ADD COLUMN product_code varchar(15), ADD COLUMN org_node_code varchar(15);
CREATE INDEX ix_invoice_org_node ON billing.invoice (tenant_id, org_node_code);

-- Backfill tenant by tenant: these tables have forced row-level security, so each tenant is set in turn.
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    UPDATE billing.premium_schedule s SET product_code = p.product_code, org_node_code = p.org_node_code FROM general.policy p
    WHERE s.source_domain = 'GENERAL' AND p.tenant_id = s.tenant_id AND p.id = s.policy_ref;
    UPDATE billing.premium_schedule s SET product_code = p.product_code, org_node_code = p.org_node_code FROM life.policy p
    WHERE s.source_domain = 'LIFE' AND p.tenant_id = s.tenant_id AND p.id = s.policy_ref;
    UPDATE billing.premium_schedule s SET org_node_code = m.org_node_code FROM health.membership m
    WHERE s.source_domain LIKE 'HEALTH%' AND m.tenant_id = s.tenant_id AND m.id = s.policy_ref;
    UPDATE billing.invoice i SET product_code = s.product_code, org_node_code = s.org_node_code FROM billing.premium_schedule s
    WHERE s.tenant_id = i.tenant_id AND s.billing_account_id = i.billing_account_id AND s.source_domain = i.source_domain AND s.policy_ref = i.policy_ref;
  END LOOP;
END $$;
