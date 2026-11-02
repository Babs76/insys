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
-- V0128: Wave 7, AI Assist (addendum §5; AIA-050, AIA-062). The module's own schema, forced row-level security, its outbox and inbox,
-- and the tenant's settings. Nothing assumes South African hosting.
-- =====================================================================
CREATE SCHEMA IF NOT EXISTS assist;

CREATE TABLE assist.outbox_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  aggregate_type             varchar(60) NOT NULL,
  aggregate_id               uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  event_version              smallint NOT NULL,
  payload                    jsonb NOT NULL,
  occurred_at                timestamptz NOT NULL,
  correlation_id             uuid NOT NULL,
  causation_id               uuid,
  published_at               timestamptz,
  publish_attempts           smallint NOT NULL,
  CONSTRAINT pk_outbox_event PRIMARY KEY (tenant_id, id)
);
ALTER TABLE assist.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE assist.inbox_message (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  consumer                   varchar(60) NOT NULL,
  event_id                   uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  received_at                timestamptz NOT NULL,
  processed_at               timestamptz,
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_inbox_message PRIMARY KEY (tenant_id, id)
);
CREATE UNIQUE INDEX uq_inbox_message_1 ON assist.inbox_message (tenant_id, consumer, event_id);
ALTER TABLE assist.inbox_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.inbox_message FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.inbox_message
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- Communications · Channels (OQ-090: the shared channel list)

CREATE TABLE assist.tenant_setting (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  jurisdiction               char(2) NOT NULL,
  inference_region           varchar(20) NOT NULL,
  legal_basis                varchar(500),
  explanations_enabled       boolean NOT NULL,
  monthly_token_cap          bigint,
  CONSTRAINT pk_tenant_setting PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_tenant_setting UNIQUE (tenant_id),
  CONSTRAINT ck_tenant_setting_cap CHECK (monthly_token_cap IS NULL OR monthly_token_cap >= 0)
);
ALTER TABLE assist.tenant_setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.tenant_setting FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assist.tenant_setting
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- The feature that switches it on, off for every tenant until entitled (D-6).
ALTER TABLE iam.ref_function_catalogue DROP CONSTRAINT ref_function_catalogue_ck4;
ALTER TABLE iam.ref_function_catalogue ADD CONSTRAINT ref_function_catalogue_ck4 CHECK (required_feature IN ('LIFE', 'GENERAL', 'HEALTH', 'REINSURANCE', 'BROKER_PORTAL', 'PROVIDER_PORTAL', 'CUSTOMER_PORTAL', 'AI_ASSIST'));
ALTER TABLE tenant.cfg_feature_entitlements DROP CONSTRAINT cfg_feature_entitlements_ck1;
ALTER TABLE tenant.cfg_feature_entitlements ADD CONSTRAINT cfg_feature_entitlements_ck1 CHECK (feature IN ('LIFE', 'GENERAL', 'HEALTH', 'REINSURANCE', 'BROKER_PORTAL', 'PROVIDER_PORTAL', 'CUSTOMER_PORTAL', 'AI_ASSIST'));
