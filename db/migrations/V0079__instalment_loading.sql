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
-- V0079: an instalment loading per product and frequency (Ade, 28 October 2026): a percentage, 0 when not set (null), added
-- to the instalment a quote shows for that frequency. The quote keeps the loading it was made with.
-- =====================================================================
ALTER TABLE general.cfg_product_billing_frequencies ADD COLUMN instalment_loading_pct numeric(7,4);
ALTER TABLE general.cfg_product_billing_frequencies ADD CONSTRAINT cfg_product_billing_frequencies_loading CHECK (instalment_loading_pct IS NULL OR instalment_loading_pct >= 0);

COMMENT ON COLUMN general.cfg_product_billing_frequencies.instalment_loading_pct IS 'The loading on each instalment in this frequency, in per cent of the annual premium''s share; none (null) means 0.';

ALTER TABLE general.quote
  ADD COLUMN premium_loading_pct numeric(7,4),
  ADD CONSTRAINT ck_quote_premium_loading CHECK ((premium_loading_pct IS NULL) = (premium_frequency IS NULL) AND (premium_loading_pct IS NULL OR premium_loading_pct >= 0));

COMMENT ON COLUMN general.quote.premium_loading_pct IS 'The instalment loading in per cent when the quote was made in its frequency (general.cfg_product_billing_frequencies).';
