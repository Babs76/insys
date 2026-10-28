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
-- V0072: General's claim and endorsement amounts carry their currency (BR-033). Assessments, recoveries (with their
-- salvage) and endorsements are in their policy's currency: a policy's currency never changes, so each record keeps it
-- from the day it is made, and existing records take it from their policy here. A risk object belongs to no single
-- policy, so its valuation names its own currency, when one is given.
-- =====================================================================
ALTER TABLE general.loss_assessment ADD COLUMN currency char(3);
UPDATE general.loss_assessment a SET currency = p.currency
FROM general.claim c JOIN general.policy p ON p.tenant_id = c.tenant_id AND p.id = c.policy_id
WHERE c.tenant_id = a.tenant_id AND c.id = a.claim_id;
ALTER TABLE general.loss_assessment ALTER COLUMN currency SET NOT NULL;
COMMENT ON COLUMN general.loss_assessment.currency IS 'BR-033: the currency of the assessed amount, excess and estimates: the claim''s policy''s.';

ALTER TABLE general.recovery_case ADD COLUMN currency char(3);
UPDATE general.recovery_case r SET currency = p.currency
FROM general.claim c JOIN general.policy p ON p.tenant_id = c.tenant_id AND p.id = c.policy_id
WHERE c.tenant_id = r.tenant_id AND c.id = r.claim_id;
ALTER TABLE general.recovery_case ALTER COLUMN currency SET NOT NULL;
COMMENT ON COLUMN general.recovery_case.currency IS 'BR-033: the currency of the expected and recovered amounts and of the salvage proceeds: the claim''s policy''s.';

ALTER TABLE general.endorsement ADD COLUMN currency char(3);
UPDATE general.endorsement e SET currency = p.currency FROM general.policy p WHERE p.tenant_id = e.tenant_id AND p.id = e.policy_id;
ALTER TABLE general.endorsement ALTER COLUMN currency SET NOT NULL;
COMMENT ON COLUMN general.endorsement.currency IS 'BR-033: the currency of the premium change and new annual premium: the policy''s.';

ALTER TABLE general.risk_object ADD COLUMN valuation_currency char(3);
ALTER TABLE general.risk_object ADD CONSTRAINT ck_risk_object_valuation_currency CHECK (valuation_currency IS NULL OR valuation IS NOT NULL);
COMMENT ON COLUMN general.risk_object.valuation_currency IS 'BR-033: the valuation''s currency, when the valuation names one. NULL for valuations recorded before V0072.';
