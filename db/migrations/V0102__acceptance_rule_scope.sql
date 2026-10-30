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
-- V0102: BR-113 (GAP-117). Acceptance and referral rules had no scope, so every rule ran against every product's
-- underwriting decision: a Life rule on sum_assured failed a General renewal review for want of that input. A rule now
-- applies to its product, or to every product of its domain, or (with neither, as before) to every product. A rule
-- whose input is missing refers the decision to a person rather than failing it.
-- =====================================================================

ALTER TABLE rules.cfg_acceptance_and_referral_rules ADD COLUMN product_code varchar(15);
ALTER TABLE rules.cfg_acceptance_and_referral_rules ADD COLUMN domain varchar(10);
ALTER TABLE rules.cfg_acceptance_and_referral_rules ADD CONSTRAINT cfg_acceptance_and_referral_rules_ck2 CHECK (domain IN ('LIFE', 'GENERAL', 'HEALTH'));
COMMENT ON COLUMN rules.cfg_acceptance_and_referral_rules.product_code IS 'product_code: Ref Product. The product the rule applies to; empty for every product of its domain (BR-113). Sample: LIF-TERM';
COMMENT ON COLUMN rules.cfg_acceptance_and_referral_rules.domain IS 'domain: Enum{Life,General,Health}. With no product, the domain whose products the rule applies to; with neither, every product (BR-113). Sample: LIFE';
