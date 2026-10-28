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
-- V0085: a commission entitlement keeps its plan's basis (BR-059), so its rate reads as a percentage (PREMIUM) or an
-- amount (FIXED, POLICY_COUNT). Existing entitlements take the basis of the plan version they were accrued with.
-- =====================================================================
ALTER TABLE commission.commission_entitlement ADD COLUMN rate_basis varchar(15);
UPDATE commission.commission_entitlement e
SET rate_basis = left(p.basis, 15)
FROM commission.cfg_commission_plans p
WHERE p.tenant_id = e.tenant_id AND p.plan_code = e.plan_code AND p.version = e.plan_version;
COMMENT ON COLUMN commission.commission_entitlement.rate_basis IS 'BR-059: the plan''s basis: PREMIUM (rate is a percentage of the net premium), FIXED or POLICY_COUNT (rate is an amount).';
