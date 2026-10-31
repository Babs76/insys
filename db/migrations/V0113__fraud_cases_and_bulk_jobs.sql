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
-- V0113: Wave 6 batch 6.3 (addenda/finance-and-compliance.md §4.8, §4.9; F058, F076; OQ-158, OQ-160). Fraud cases for Life and General, as Health's, seen only by holders of the
-- fraud function; bulk jobs for parties, all or nothing.
-- =====================================================================

CREATE TABLE claims.fraud_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  case_number                varchar(25) NOT NULL,
  domain                     varchar(10) NOT NULL,
  claim_number               varchar(25),
  policy_ref                 uuid,
  indicator_codes            text[] NOT NULL,
  status                     varchar(15) NOT NULL,
  investigator_id            uuid,
  outcome                    varchar(15),
  outcome_reason             varchar(1000),
  opened_by                  uuid NOT NULL,
  concluded_at               timestamptz,
  CONSTRAINT pk_fraud_case PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_fraud_case_number UNIQUE (tenant_id, case_number),
  CONSTRAINT ck_fraud_case_domain CHECK (domain IN ('LIFE', 'GENERAL')),
  CONSTRAINT ck_fraud_case_status CHECK (status IN ('OPENED', 'INVESTIGATING', 'CONCLUDED')),
  CONSTRAINT ck_fraud_case_subject CHECK (claim_number IS NOT NULL OR policy_ref IS NOT NULL)
);
ALTER TABLE claims.fraud_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.fraud_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.fraud_case USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE party.bulk_job (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  job_type                   varchar(15) NOT NULL,
  status                     varchar(15) NOT NULL,
  filter                     varchar(200),
  total_count                integer NOT NULL,
  accepted_count             integer NOT NULL,
  rejected_lines             jsonb,
  run_by                     uuid NOT NULL,
  approved_by                uuid,
  CONSTRAINT pk_bulk_job PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_bulk_job_type CHECK (job_type IN ('PARTY_IMPORT', 'PARTY_EXPORT')),
  CONSTRAINT ck_bulk_job_status CHECK (status IN ('DONE', 'REJECTED', 'TO_APPROVE', 'APPROVED'))
);
ALTER TABLE party.bulk_job ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.bulk_job FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.bulk_job USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
