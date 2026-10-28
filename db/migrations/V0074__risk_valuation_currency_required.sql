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
-- V0074: a risk object's valuation always names its currency (BR-033, decided 28 October 2026). Valuations recorded
-- without one take the tenant's base currency (tenant.cfg_locale_defaults.base_currency, the version in force today);
-- from here on a valuation and its currency are given together or not at all.
-- =====================================================================
UPDATE general.risk_object r SET valuation_currency = (
  SELECT upper(d.base_currency) FROM tenant.cfg_locale_defaults d
  WHERE d.tenant_id = r.tenant_id AND d.record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE')
    AND d.effective_from <= current_date AND (d.effective_to IS NULL OR d.effective_to > current_date)
  ORDER BY d.effective_from DESC LIMIT 1)
WHERE r.valuation IS NOT NULL AND r.valuation_currency IS NULL;

DO $$
DECLARE missing bigint;
BEGIN
  SELECT count(*) INTO missing FROM general.risk_object WHERE valuation IS NOT NULL AND valuation_currency IS NULL;
  IF missing > 0 THEN
    RAISE EXCEPTION 'V0074: % valued risk object(s) belong to tenants with no locale defaults in force, so no base currency to give them', missing;
  END IF;
END $$;

ALTER TABLE general.risk_object DROP CONSTRAINT ck_risk_object_valuation_currency;
ALTER TABLE general.risk_object ADD CONSTRAINT ck_risk_object_valuation_currency
  CHECK ((valuation IS NULL) = (valuation_currency IS NULL));
COMMENT ON COLUMN general.risk_object.valuation_currency IS 'BR-033: the valuation''s currency, present exactly when there is a valuation. Valuations before V0074 took the tenant''s base currency.';
