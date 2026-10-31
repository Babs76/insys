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
-- V0109: Wave 5b batch 5b.3 (addenda/benefit-programmes.md §3, §4.3; F083 to F085). Chronic registration, decided by a person with the
-- clinical function (OQ-150). Clinical and restricted. What an entitlement has used is counted from paid claims, so it isn't stored.
-- =====================================================================

ALTER TABLE health.cfg_chronic_condition_list ADD COLUMN benefit_category varchar(15);

CREATE TABLE health.chronic_registration (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  registration_number        varchar(25) NOT NULL,
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  condition_code             varchar(15) NOT NULL,
  diagnosis_code             varchar(10),
  supporting_note            varchar(2000) NOT NULL,
  requested_by               uuid NOT NULL,
  requesting_provider_id     varchar(15),
  status                     varchar(10) NOT NULL,
  valid_from                 date,
  valid_to                   date,
  review_on                  date,
  decided_by                 uuid,
  decided_at                 timestamptz,
  decline_reason             varchar(500),
  CONSTRAINT pk_chronic_registration PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_chronic_registration_number UNIQUE (tenant_id, registration_number),
  CONSTRAINT ck_chronic_registration_status CHECK (status IN ('REQUESTED', 'APPROVED', 'DECLINED', 'ENDED')),
  CONSTRAINT ck_chronic_registration_dates CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_chronic_registration_1 ON health.chronic_registration (tenant_id, patient_party_id, status);
CREATE INDEX ix_chronic_registration_2 ON health.chronic_registration (tenant_id, membership_id);

CREATE TABLE health.chronic_entitlement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  registration_id            uuid NOT NULL,
  product_code               varchar(20),
  active_ingredient          varchar(100),
  monthly_quantity           integer NOT NULL,
  CONSTRAINT pk_chronic_entitlement PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_chronic_entitlement_medicine CHECK (product_code IS NOT NULL OR active_ingredient IS NOT NULL),
  CONSTRAINT ck_chronic_entitlement_quantity CHECK (monthly_quantity > 0)
);
CREATE INDEX ix_chronic_entitlement_1 ON health.chronic_entitlement (tenant_id, registration_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['chronic_registration', 'chronic_entitlement'] LOOP
    EXECUTE format('ALTER TABLE health.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE health.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON health.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
