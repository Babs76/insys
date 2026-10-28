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
-- V0076: a broker's portal quote is priced as it is made (BR-095, decided by Ade on 28 October 2026). General asks
-- Rating for the premium when the quote, or a later version of it, is made; the quote reads Rated once it comes back.
-- A quote Rating cannot price stays Draft with the reason. Quotes made before stay priced by hand.
-- =====================================================================
ALTER TABLE general.quote
  ADD COLUMN priced_on_creation boolean NOT NULL DEFAULT false,
  ADD COLUMN rating_issue_code varchar(50),
  ADD COLUMN rating_issue varchar(500),
  ADD CONSTRAINT ck_quote_rating_issue CHECK ((rating_issue_code IS NULL) = (rating_issue IS NULL));

COMMENT ON COLUMN general.quote.priced_on_creation IS 'BR-095: priced as it is made (a broker''s portal quote and its later versions); General asks Rating, nobody requests the calculation by hand.';
COMMENT ON COLUMN general.quote.rating_issue_code IS 'BR-095: Rating''s error code when a quote priced as it is made could not be rated; the quote stays Draft. Cleared once rated.';
COMMENT ON COLUMN general.quote.rating_issue IS 'BR-095: the reason in words, for the broker.';
