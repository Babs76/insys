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
-- V0105: Wave 5a batch 5a.2 (addenda/providers-and-care.md §4.3 to §4.5; F034 to F038). Complex medical cases with their case
-- manager, care plans with progress, coordination notes, and clinical referrals. Clinical data stays in this schema (OQ-140)
-- and never appears in events, logs, notices or audit detail (HLT-008).
-- =====================================================================

CREATE TABLE health.complex_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  case_number                varchar(25) NOT NULL,
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  opened_from                varchar(15) NOT NULL,
  source_id                  uuid,
  condition_category         varchar(120) NOT NULL,
  status                     varchar(10) NOT NULL,
  case_manager_id            uuid,
  close_reason               varchar(500),
  CONSTRAINT pk_complex_case PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_complex_case_number UNIQUE (tenant_id, case_number),
  CONSTRAINT fk_complex_case_membership FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id),
  CONSTRAINT ck_complex_case_from CHECK (opened_from IN ('MEMBERSHIP', 'UTILISATION', 'HIGH_COST')),
  CONSTRAINT ck_complex_case_status CHECK (status IN ('OPEN', 'ACTIVE', 'ON_HOLD', 'CLOSED')),
  CONSTRAINT ck_complex_case_closed CHECK ((status = 'CLOSED') = (close_reason IS NOT NULL)),
  CONSTRAINT ck_complex_case_active CHECK (status = 'OPEN' OR status = 'CLOSED' OR case_manager_id IS NOT NULL)
);
CREATE INDEX ix_complex_case_1 ON health.complex_case (tenant_id, membership_id);
CREATE INDEX ix_complex_case_2 ON health.complex_case (tenant_id, case_manager_id);

CREATE TABLE health.complex_case_assignment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  complex_case_id            uuid NOT NULL,
  case_manager_id            uuid NOT NULL,
  assigned_at                timestamptz NOT NULL,
  assigned_by                uuid NOT NULL,
  reason                     varchar(500),
  ended_at                   timestamptz,
  CONSTRAINT pk_complex_case_assignment PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_complex_case_assignment FOREIGN KEY (tenant_id, complex_case_id) REFERENCES health.complex_case (tenant_id, id)
);
CREATE INDEX ix_complex_case_assignment_1 ON health.complex_case_assignment (tenant_id, complex_case_id);

CREATE TABLE health.care_plan (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  complex_case_id            uuid NOT NULL,
  plan_version               integer NOT NULL,
  is_current                 boolean NOT NULL,
  change_reason              varchar(500),
  CONSTRAINT pk_care_plan PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_care_plan_version UNIQUE (tenant_id, complex_case_id, plan_version),
  CONSTRAINT fk_care_plan_case FOREIGN KEY (tenant_id, complex_case_id) REFERENCES health.complex_case (tenant_id, id)
);
CREATE UNIQUE INDEX ux_care_plan_current ON health.care_plan (tenant_id, complex_case_id) WHERE is_current;

CREATE TABLE health.care_plan_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  care_plan_id               uuid NOT NULL,
  kind                       varchar(10) NOT NULL,
  description                varchar(1000) NOT NULL,
  responsible                varchar(120),
  due_on                     date,
  status                     varchar(10) NOT NULL,
  progress_note              varchar(1000),
  overdue_raised             boolean NOT NULL,
  CONSTRAINT pk_care_plan_item PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_care_plan_item FOREIGN KEY (tenant_id, care_plan_id) REFERENCES health.care_plan (tenant_id, id),
  CONSTRAINT ck_care_plan_item_kind CHECK (kind IN ('GOAL', 'ACTIVITY')),
  CONSTRAINT ck_care_plan_item_status CHECK (status IN ('OPEN', 'DONE', 'MISSED', 'CHANGED')),
  CONSTRAINT ck_care_plan_item_due CHECK (kind = 'GOAL' OR due_on IS NOT NULL)
);
CREATE INDEX ix_care_plan_item_1 ON health.care_plan_item (tenant_id, care_plan_id);
CREATE INDEX ix_care_plan_item_2 ON health.care_plan_item (tenant_id, due_on) WHERE status = 'OPEN' AND NOT overdue_raised;

CREATE TABLE health.care_coordination_note (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  complex_case_id            uuid NOT NULL,
  contact_type               varchar(10) NOT NULL,
  contact_ref                varchar(120),
  note                       varchar(2000) NOT NULL,
  shared_with_provider_id    varchar(15),
  consent_reference          varchar(120),
  written_by                 uuid NOT NULL,
  written_at                 timestamptz NOT NULL,
  CONSTRAINT pk_care_coordination_note PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_care_coordination_note FOREIGN KEY (tenant_id, complex_case_id) REFERENCES health.complex_case (tenant_id, id),
  CONSTRAINT ck_care_note_type CHECK (contact_type IN ('PROVIDER', 'MEMBER', 'INTERNAL')),
  CONSTRAINT ck_care_note_consent CHECK (shared_with_provider_id IS NULL OR consent_reference IS NOT NULL)
);
CREATE INDEX ix_care_coordination_note_1 ON health.care_coordination_note (tenant_id, complex_case_id);

CREATE TABLE health.clinical_referral (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  referral_number            varchar(25) NOT NULL,
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  referring_provider_id      varchar(15) NOT NULL,
  referred_provider_id       varchar(15),
  referred_specialty         varchar(40),
  clinical_reason            varchar(1000) NOT NULL,
  valid_from                 date NOT NULL,
  valid_to                   date NOT NULL,
  visits_allowed             integer NOT NULL,
  visits_used                integer NOT NULL,
  complex_case_id            uuid,
  preauthorisation_id        uuid,
  status                     varchar(10) NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  decline_reason             varchar(500),
  CONSTRAINT pk_clinical_referral PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_clinical_referral_number UNIQUE (tenant_id, referral_number),
  CONSTRAINT fk_clinical_referral_membership FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id),
  CONSTRAINT ck_clinical_referral_to CHECK (referred_provider_id IS NOT NULL OR referred_specialty IS NOT NULL),
  CONSTRAINT ck_clinical_referral_dates CHECK (valid_to >= valid_from),
  CONSTRAINT ck_clinical_referral_visits CHECK (visits_allowed > 0 AND visits_used BETWEEN 0 AND visits_allowed),
  CONSTRAINT ck_clinical_referral_status CHECK (status IN ('REQUESTED', 'APPROVED', 'DECLINED', 'USED', 'EXPIRED'))
);
CREATE INDEX ix_clinical_referral_1 ON health.clinical_referral (tenant_id, membership_id);
CREATE INDEX ix_clinical_referral_2 ON health.clinical_referral (tenant_id, referring_provider_id);
CREATE INDEX ix_clinical_referral_3 ON health.clinical_referral (tenant_id, valid_to) WHERE status IN ('REQUESTED', 'APPROVED');

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['complex_case', 'complex_case_assignment', 'care_plan', 'care_plan_item', 'care_coordination_note', 'clinical_referral'] LOOP
    EXECUTE format('ALTER TABLE health.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE health.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON health.%I USING (tenant_id = current_setting(''app.tenant_id'')::uuid) WITH CHECK (tenant_id = current_setting(''app.tenant_id'')::uuid)', t);
  END LOOP;
END $$;
