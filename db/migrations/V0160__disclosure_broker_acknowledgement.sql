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
-- V0160: DE-039 part 2 (Ade, 25 November 2026; OQ-201). A broker records its client's acknowledgement of a disclosure, saying how the notice was
-- presented; the customer sees it in the member portal. A tenant can switch broker recording off (customer-only).
-- =====================================================================
-- How the notice was presented when someone other than the customer recorded the acknowledgement: IN_PERSON, PHONE, EMAIL or OTHER (OTHER needs a
-- detail). Null for an acknowledgement the customer or staff recorded, or one from before. intermediary_code is the broker's own code when a broker
-- recorded it; channel_code and recorded_by stay the channel and the recording user.
ALTER TABLE channel.disclosure_acknowledgement
  ADD COLUMN presentation_method varchar(12),
  ADD COLUMN presentation_detail varchar(200),
  ADD COLUMN intermediary_code   varchar(30),
  ADD CONSTRAINT ck_disclosure_acknowledgement_presentation CHECK (presentation_method IS NULL OR presentation_method IN ('IN_PERSON', 'PHONE', 'EMAIL', 'OTHER')),
  ADD CONSTRAINT ck_disclosure_acknowledgement_presentation_detail CHECK (presentation_method IS DISTINCT FROM 'OTHER' OR presentation_detail IS NOT NULL),
  ADD CONSTRAINT ck_disclosure_acknowledgement_broker CHECK (intermediary_code IS NULL OR presentation_method IS NOT NULL);
COMMENT ON COLUMN channel.disclosure_acknowledgement.presentation_method IS 'DE-039: how the broker presented the notice (IN_PERSON, PHONE, EMAIL, OTHER); null when the customer or staff recorded it';
COMMENT ON COLUMN channel.disclosure_acknowledgement.presentation_detail IS 'DE-039: the broker''s reason or detail, required with OTHER (at most 200 characters)';
COMMENT ON COLUMN channel.disclosure_acknowledgement.intermediary_code IS 'DE-039: the code of the broker who recorded it; null when the customer or staff did';

-- The tenant's disclosure setting: whether a broker may record its client's acknowledgement. With no row, it may (the default, Ade's beta decision).
CREATE TABLE channel.disclosure_setting (
  tenant_id                       uuid NOT NULL,
  id                              uuid NOT NULL,
  row_version                     bigint NOT NULL,
  created_at                      timestamptz NOT NULL,
  created_by                      varchar(80) NOT NULL,
  updated_at                      timestamptz NOT NULL,
  updated_by                      varchar(80) NOT NULL,
  broker_acknowledgement_allowed  boolean NOT NULL DEFAULT true,
  CONSTRAINT pk_disclosure_setting PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_disclosure_setting_tenant UNIQUE (tenant_id)
);
COMMENT ON TABLE channel.disclosure_setting IS 'DE-039, OQ-201: a tenant''s disclosure setting. broker_acknowledgement_allowed false makes acknowledgement customer-only (staff can still record one).';
ALTER TABLE channel.disclosure_setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE channel.disclosure_setting FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON channel.disclosure_setting
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
