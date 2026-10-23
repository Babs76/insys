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
-- INSYS Core · Core transactional schema (PostgreSQL)
-- Generated 21 October 2026 from the Database Specification (revision 2)
-- 135 catalogue tables; the outbox pattern table is created in every module
-- schema except tenant and analytics, giving 154 tables in 22 schemas.
-- Tested on PostgreSQL 16.15. Run this file first, then
-- insys_static_data_schema.sql, as the migration role in an empty database.
--
-- Conventions (see the specification for detail):
--  * Primary keys lead with tenant_id. Row-level security is enabled and forced
--    on every tenant table; the application sets app.tenant_id with SET LOCAL
--    at the start of each transaction (DBA-001 to DBA-003).
--  * Foreign keys only within a schema. Cross-schema references are comments
--    at the end of the file and are checked by the owning service (DBA-005).
--  * Eight high-volume append-only tables are range-partitioned by month, with
--    one example partition (January 2027) each. Create further partitions ahead
--    of time and give each the same policy (DBA-018, DBA-023).
--  * Grants per module service role are environment-specific and not included.
-- =====================================================================
-- INSYS Core database DDL, generated from the Database Specification
-- Target: PostgreSQL. Run as the migration role.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE SCHEMA IF NOT EXISTS integration;
CREATE SCHEMA IF NOT EXISTS sdm;
CREATE SCHEMA IF NOT EXISTS party;
CREATE SCHEMA IF NOT EXISTS product;
CREATE SCHEMA IF NOT EXISTS rating;
CREATE SCHEMA IF NOT EXISTS rules;
CREATE SCHEMA IF NOT EXISTS workflow;
CREATE SCHEMA IF NOT EXISTS document;
CREATE SCHEMA IF NOT EXISTS billing;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS claims;
CREATE SCHEMA IF NOT EXISTS life;
CREATE SCHEMA IF NOT EXISTS general;
CREATE SCHEMA IF NOT EXISTS health;
CREATE SCHEMA IF NOT EXISTS finance;
CREATE SCHEMA IF NOT EXISTS commission;
CREATE SCHEMA IF NOT EXISTS reinsurance;
CREATE SCHEMA IF NOT EXISTS reporting;
CREATE SCHEMA IF NOT EXISTS analytics;
CREATE SCHEMA IF NOT EXISTS tenant;
CREATE SCHEMA IF NOT EXISTS iam;
CREATE SCHEMA IF NOT EXISTS audit;

-- Tables
CREATE TABLE integration.idempotency_key (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  idem_key                   varchar(80) NOT NULL,
  operation                  varchar(60) NOT NULL,
  request_hash               char(64) NOT NULL,
  status                     varchar(12) NOT NULL,
  response_ref               jsonb,
  expires_at                 timestamptz NOT NULL,
  CONSTRAINT pk_idempotency_key PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_idempotency_key_idem_key UNIQUE (tenant_id, idem_key)
);
CREATE TABLE integration.inbox_message (
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
CREATE TABLE integration.webhook_delivery (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  subscription_id            varchar(20) NOT NULL,
  event_id                   uuid NOT NULL,
  attempt                    smallint NOT NULL,
  status                     varchar(12) NOT NULL,
  http_status                smallint,
  next_attempt_at            timestamptz,
  error                      text,
  CONSTRAINT pk_webhook_delivery PRIMARY KEY (tenant_id, id)
);
CREATE TABLE integration.dead_letter_message (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  source                     varchar(60) NOT NULL,
  message_id                 varchar(80) NOT NULL,
  payload                    jsonb NOT NULL,
  error                      text NOT NULL,
  first_failed_at            timestamptz NOT NULL,
  attempts                   smallint NOT NULL,
  resolution                 varchar(20),
  resolved_by                uuid,
  CONSTRAINT pk_dead_letter_message PRIMARY KEY (tenant_id, id)
);
CREATE TABLE integration.external_call_log (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  partner_code               varchar(15) NOT NULL,
  operation                  varchar(60) NOT NULL,
  request_id                 varchar(80) NOT NULL,
  status                     varchar(12) NOT NULL,
  http_status                smallint,
  latency_ms                 integer NOT NULL,
  called_at                  timestamptz NOT NULL,
  CONSTRAINT pk_external_call_log PRIMARY KEY (tenant_id, id, called_at)
) PARTITION BY RANGE (called_at);
CREATE TABLE integration.outbox_event (
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
CREATE TABLE sdm.outbox_event (
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
CREATE TABLE party.outbox_event (
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
CREATE TABLE product.outbox_event (
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
CREATE TABLE rating.outbox_event (
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
CREATE TABLE rules.outbox_event (
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
CREATE TABLE workflow.outbox_event (
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
CREATE TABLE document.outbox_event (
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
CREATE TABLE billing.outbox_event (
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
CREATE TABLE payment.outbox_event (
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
CREATE TABLE claims.outbox_event (
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
CREATE TABLE life.outbox_event (
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
CREATE TABLE general.outbox_event (
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
CREATE TABLE health.outbox_event (
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
CREATE TABLE finance.outbox_event (
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
CREATE TABLE commission.outbox_event (
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
CREATE TABLE reinsurance.outbox_event (
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
CREATE TABLE reporting.outbox_event (
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
CREATE TABLE iam.outbox_event (
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
CREATE TABLE audit.outbox_event (
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
CREATE TABLE sdm.change_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  request_number             varchar(20) NOT NULL,
  entity_code                varchar(40) NOT NULL,
  record_key                 varchar(120) NOT NULL,
  action                     varchar(10) NOT NULL,
  before                     jsonb,
  after                      jsonb NOT NULL,
  maker_id                   uuid NOT NULL,
  checker_id                 uuid,
  status                     varchar(15) NOT NULL,
  effective_from             date NOT NULL,
  reason                     text NOT NULL,
  CONSTRAINT pk_change_request PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_change_request_request_number UNIQUE (tenant_id, request_number),
  CONSTRAINT ck_change_request_2 CHECK (checker_id IS NULL OR checker_id <> maker_id)
);
CREATE TABLE sdm.import_batch (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  batch_number               varchar(20) NOT NULL,
  entity_code                varchar(40) NOT NULL,
  file_ref                   varchar(200) NOT NULL,
  row_count                  integer NOT NULL,
  rejected_count             integer NOT NULL,
  status                     varchar(12) NOT NULL,
  submitted_by               uuid NOT NULL,
  CONSTRAINT pk_import_batch PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_import_batch_batch_number UNIQUE (tenant_id, batch_number)
);
CREATE TABLE sdm.import_error (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  batch_id                   uuid NOT NULL,
  line_no                    integer NOT NULL,
  field                      varchar(60) NOT NULL,
  message                    text NOT NULL,
  CONSTRAINT pk_import_error PRIMARY KEY (tenant_id, id)
);
CREATE TABLE sdm.config_package (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  package_code               varchar(30) NOT NULL,
  source_env                 varchar(12) NOT NULL,
  target_env                 varchar(12) NOT NULL,
  contents                   jsonb NOT NULL,
  checksum                   char(64) NOT NULL,
  status                     varchar(12) NOT NULL,
  approved_by                uuid,
  applied_at                 timestamptz,
  CONSTRAINT pk_config_package PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_config_package_package_code UNIQUE (tenant_id, package_code)
);
CREATE TABLE party.party (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_kind                 varchar(12) NOT NULL,
  display_name               varchar(200) NOT NULL,
  status_code                varchar(12) NOT NULL,
  merged_into_id             uuid,
  CONSTRAINT pk_party PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.individual (
  tenant_id                  uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  title_code                 varchar(10),
  first_names                varchar(100) NOT NULL,
  surname                    varchar(100) NOT NULL,
  birth_date                 date NOT NULL,
  gender_code                varchar(10),
  marital_status_code        varchar(15),
  nationality                char(2),
  preferred_language         char(2),
  deceased_date              date,
  CONSTRAINT pk_individual PRIMARY KEY (tenant_id, party_id)
);
CREATE TABLE party.organisation (
  tenant_id                  uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  legal_name                 varchar(150) NOT NULL,
  trading_name               varchar(150),
  legal_form_code            varchar(10) NOT NULL,
  registration_number        varchar(40),
  industry_code              varchar(6),
  CONSTRAINT pk_organisation PRIMARY KEY (tenant_id, party_id)
);
CREATE TABLE party.party_identifier (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  doc_type                   varchar(15) NOT NULL,
  country                    char(2) NOT NULL,
  number_enc                 bytea NOT NULL,
  number_hash                char(64) NOT NULL,
  verified_at                timestamptz,
  verification_ref           varchar(80),
  CONSTRAINT pk_party_identifier PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.address (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  address_type               varchar(12) NOT NULL,
  line1                      varchar(120) NOT NULL,
  line2                      varchar(120),
  city                       varchar(80) NOT NULL,
  subdivision                varchar(6),
  postal_code                varchar(10),
  country                    char(2) NOT NULL,
  valid_from                 date NOT NULL,
  valid_to                   date,
  CONSTRAINT pk_address PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.contact_point (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  channel_type               varchar(10) NOT NULL,
  value                      varchar(200) NOT NULL,
  is_primary                 boolean NOT NULL,
  verified_at                timestamptz,
  CONSTRAINT pk_contact_point PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.party_relationship (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  from_party_id              uuid NOT NULL,
  to_party_id                uuid NOT NULL,
  relationship_code          varchar(15) NOT NULL,
  valid_from                 date NOT NULL,
  valid_to                   date,
  CONSTRAINT pk_party_relationship PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.party_role (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  role_code                  varchar(20) NOT NULL,
  domain                     varchar(10) NOT NULL,
  context_type               varchar(30) NOT NULL,
  context_id                 uuid NOT NULL,
  valid_from                 date NOT NULL,
  valid_to                   date,
  CONSTRAINT pk_party_role PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.consent_record (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  purpose_code               varchar(20) NOT NULL,
  granted                    boolean NOT NULL,
  captured_at                timestamptz NOT NULL,
  capture_channel            varchar(15) NOT NULL,
  evidence_ref               varchar(200),
  withdrawn_at               timestamptz,
  CONSTRAINT pk_consent_record PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.duplicate_candidate (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_a_id                 uuid NOT NULL,
  party_b_id                 uuid NOT NULL,
  score                      numeric(3,2) NOT NULL,
  rule_code                  varchar(15) NOT NULL,
  status                     varchar(12) NOT NULL,
  reviewed_by                uuid,
  CONSTRAINT pk_duplicate_candidate PRIMARY KEY (tenant_id, id)
);
CREATE TABLE party.merge_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  survivor_id                uuid NOT NULL,
  merged_id                  uuid NOT NULL,
  merged_at                  timestamptz NOT NULL,
  merged_by                  uuid NOT NULL,
  reason                     text NOT NULL,
  snapshot                   jsonb NOT NULL,
  CONSTRAINT pk_merge_history PRIMARY KEY (tenant_id, id)
);
CREATE TABLE product.simulation_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_number                 varchar(20) NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  started_by                 uuid NOT NULL,
  started_at                 timestamptz NOT NULL,
  status                     varchar(10) NOT NULL,
  cases_total                integer NOT NULL,
  cases_failed               integer NOT NULL,
  CONSTRAINT pk_simulation_run PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_simulation_run_run_number UNIQUE (tenant_id, run_number)
);
CREATE TABLE product.simulation_result (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  case_id                    varchar(20) NOT NULL,
  expected                   jsonb NOT NULL,
  actual                     jsonb NOT NULL,
  passed                     boolean NOT NULL,
  CONSTRAINT pk_simulation_result PRIMARY KEY (tenant_id, id)
);
CREATE TABLE rating.rating_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  request_ref                varchar(30) NOT NULL,
  source_domain              varchar(10) NOT NULL,
  subject_type               varchar(30) NOT NULL,
  subject_id                 uuid NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  rating_version             integer NOT NULL,
  inputs                     jsonb NOT NULL,
  premium                    numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  calculated_at              timestamptz NOT NULL,
  CONSTRAINT pk_rating_request PRIMARY KEY (tenant_id, id, calculated_at),
  CONSTRAINT uq_rating_request_request_ref UNIQUE (tenant_id, request_ref, calculated_at)
) PARTITION BY RANGE (calculated_at);
CREATE TABLE rating.rating_result_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  request_id                 uuid NOT NULL,
  line_no                    smallint NOT NULL,
  component                  varchar(40) NOT NULL,
  factor_code                varchar(20),
  factor_value               varchar(40),
  amount                     numeric(18,6) NOT NULL,
  CONSTRAINT pk_rating_result_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE rules.decision_log (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  ruleset_code               varchar(20) NOT NULL,
  ruleset_version            integer NOT NULL,
  subject_type               varchar(30) NOT NULL,
  subject_id                 uuid NOT NULL,
  inputs                     jsonb NOT NULL,
  outcome                    varchar(15) NOT NULL,
  fired_rules                text[],
  evaluated_at               timestamptz NOT NULL,
  CONSTRAINT pk_decision_log PRIMARY KEY (tenant_id, id, evaluated_at)
) PARTITION BY RANGE (evaluated_at);
CREATE TABLE workflow.process_instance (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  process_code               varchar(20) NOT NULL,
  process_version            integer NOT NULL,
  business_ref_type          varchar(30) NOT NULL,
  business_ref_id            uuid NOT NULL,
  state                      varchar(30) NOT NULL,
  started_at                 timestamptz NOT NULL,
  ended_at                   timestamptz,
  CONSTRAINT pk_process_instance PRIMARY KEY (tenant_id, id)
);
CREATE TABLE workflow.task (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  process_instance_id        uuid NOT NULL,
  task_type                  varchar(30) NOT NULL,
  queue_code                 varchar(20) NOT NULL,
  assignee_id                uuid,
  status                     varchar(12) NOT NULL,
  sla_code                   varchar(20),
  due_at                     timestamptz NOT NULL,
  completed_at               timestamptz,
  outcome                    varchar(20),
  CONSTRAINT pk_task PRIMARY KEY (tenant_id, id)
);
CREATE TABLE workflow.approval (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  business_ref_type          varchar(30) NOT NULL,
  business_ref_id            uuid NOT NULL,
  approval_type              varchar(30) NOT NULL,
  maker_id                   uuid NOT NULL,
  checker_id                 uuid NOT NULL,
  decision                   varchar(10) NOT NULL,
  decided_at                 timestamptz NOT NULL,
  authority_limit            numeric(18,2),
  reason                     text,
  CONSTRAINT pk_approval PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_approval_2 CHECK (maker_id <> checker_id)
);
CREATE TABLE workflow.timer (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  process_instance_id        uuid NOT NULL,
  timer_type                 varchar(20) NOT NULL,
  fire_at                    timestamptz NOT NULL,
  fired_at                   timestamptz,
  CONSTRAINT pk_timer PRIMARY KEY (tenant_id, id)
);
CREATE TABLE workflow.case_note (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  business_ref_type          varchar(30) NOT NULL,
  business_ref_id            uuid NOT NULL,
  author_id                  uuid NOT NULL,
  note                       text NOT NULL,
  CONSTRAINT pk_case_note PRIMARY KEY (tenant_id, id)
);
CREATE TABLE document.document (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  category_code              varchar(20) NOT NULL,
  classification             varchar(20) NOT NULL,
  storage_uri                varchar(300) NOT NULL,
  mime_type                  varchar(80) NOT NULL,
  size_bytes                 bigint NOT NULL,
  sha256                     char(64) NOT NULL,
  template_code              varchar(20),
  template_version           integer,
  product_version_ref        varchar(30),
  business_ref_type          varchar(30) NOT NULL,
  business_ref_id            uuid NOT NULL,
  retention_until            date,
  legal_hold                 boolean NOT NULL,
  CONSTRAINT pk_document PRIMARY KEY (tenant_id, id)
);
CREATE TABLE document.document_access_log (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  document_id                uuid NOT NULL,
  accessor_id                uuid NOT NULL,
  action                     varchar(10) NOT NULL,
  purpose                    varchar(60) NOT NULL,
  accessed_at                timestamptz NOT NULL,
  CONSTRAINT pk_document_access_log PRIMARY KEY (tenant_id, id, accessed_at)
) PARTITION BY RANGE (accessed_at);
CREATE TABLE document.generation_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  template_code              varchar(20) NOT NULL,
  template_version           integer NOT NULL,
  language                   char(2) NOT NULL,
  data_ref                   jsonb NOT NULL,
  status                     varchar(12) NOT NULL,
  document_id                uuid,
  error                      text,
  CONSTRAINT pk_generation_request PRIMARY KEY (tenant_id, id)
);
CREATE TABLE document.delivery (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  document_id                uuid NOT NULL,
  channel                    varchar(10) NOT NULL,
  recipient                  varchar(200) NOT NULL,
  status                     varchar(12) NOT NULL,
  provider_ref               varchar(80),
  sent_at                    timestamptz,
  CONSTRAINT pk_delivery PRIMARY KEY (tenant_id, id)
);
CREATE TABLE document.legal_hold (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scope_type                 varchar(30) NOT NULL,
  scope_id                   uuid NOT NULL,
  reason_code                varchar(15) NOT NULL,
  placed_by                  uuid NOT NULL,
  placed_at                  timestamptz NOT NULL,
  released_at                timestamptz,
  CONSTRAINT pk_legal_hold PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.billing_account (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  account_number             varchar(15) NOT NULL,
  payer_party_id             uuid NOT NULL,
  currency                   char(3) NOT NULL,
  collection_method          varchar(15) NOT NULL,
  mandate_ref                varchar(60),
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_billing_account PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_billing_account_account_number UNIQUE (tenant_id, account_number)
);
CREATE TABLE billing.premium_schedule (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  billing_account_id         uuid NOT NULL,
  source_domain              varchar(10) NOT NULL,
  policy_ref                 uuid NOT NULL,
  frequency                  varchar(10) NOT NULL,
  start_date                 date NOT NULL,
  end_date                   date,
  instalment_amount          numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  CONSTRAINT pk_premium_schedule PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.premium_instalment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  schedule_id                uuid NOT NULL,
  due_date                   date NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  status                     varchar(12) NOT NULL,
  invoice_id                 uuid,
  CONSTRAINT pk_premium_instalment PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.invoice (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  invoice_number             varchar(20) NOT NULL,
  document_type              varchar(12) NOT NULL,
  billing_account_id         uuid NOT NULL,
  source_domain              varchar(10) NOT NULL,
  policy_ref                 uuid NOT NULL,
  product_version_ref        varchar(30) NOT NULL,
  issue_date                 date NOT NULL,
  due_date                   date NOT NULL,
  currency                   char(3) NOT NULL,
  net_amount                 numeric(18,2) NOT NULL,
  tax_amount                 numeric(18,2) NOT NULL,
  total_amount               numeric(18,2) NOT NULL,
  status                     varchar(12) NOT NULL,
  reverses_invoice_id        uuid,
  CONSTRAINT pk_invoice PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_invoice_invoice_number UNIQUE (tenant_id, invoice_number)
);
CREATE TABLE billing.invoice_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  invoice_id                 uuid NOT NULL,
  line_no                    smallint NOT NULL,
  charge_type                varchar(20) NOT NULL,
  description                varchar(200) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  tax_code                   varchar(15),
  CONSTRAINT pk_invoice_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.adjustment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  invoice_id                 uuid NOT NULL,
  reason_code                varchar(15) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  credit_note_id             uuid,
  approved_by                uuid NOT NULL,
  adjusted_at                timestamptz NOT NULL,
  CONSTRAINT pk_adjustment PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.arrears_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  billing_account_id         uuid NOT NULL,
  bucket_code                varchar(10) NOT NULL,
  days_overdue               integer NOT NULL,
  amount_overdue             numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  next_action                varchar(15) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_arrears_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE billing.refund_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  billing_account_id         uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  reason_code                varchar(15) NOT NULL,
  status                     varchar(12) NOT NULL,
  payment_id                 uuid,
  CONSTRAINT pk_refund_request PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.payment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  payment_ref                varchar(25) NOT NULL,
  direction                  varchar(8) NOT NULL,
  purpose                    varchar(15) NOT NULL,
  method_code                varchar(15) NOT NULL,
  provider_code              varchar(15) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(12) NOT NULL,
  external_ref               varchar(80),
  idempotency_key            varchar(80) NOT NULL,
  counterparty_party_id      uuid,
  initiated_at               timestamptz NOT NULL,
  value_date                 date,
  CONSTRAINT pk_payment PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_payment_payment_ref UNIQUE (tenant_id, payment_ref)
);
CREATE TABLE payment.payment_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  payment_id                 uuid NOT NULL,
  from_status                varchar(12) NOT NULL,
  to_status                  varchar(12) NOT NULL,
  source                     varchar(15) NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_payment_status_history PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.payment_allocation (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  payment_id                 uuid NOT NULL,
  invoice_id                 uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  allocation_rule            varchar(15) NOT NULL,
  allocated_at               timestamptz NOT NULL,
  reversed_by_id             uuid,
  CONSTRAINT pk_payment_allocation PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.provider_callback (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_code              varchar(15) NOT NULL,
  external_ref               varchar(80) NOT NULL,
  payload                    jsonb NOT NULL,
  signature_valid            boolean NOT NULL,
  received_at                timestamptz NOT NULL,
  processed_at               timestamptz,
  CONSTRAINT pk_provider_callback PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.bank_statement_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  account_ref                varchar(15) NOT NULL,
  statement_date             date NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  reference                  varchar(80) NOT NULL,
  matched_payment_id         uuid,
  match_status               varchar(12) NOT NULL,
  CONSTRAINT pk_bank_statement_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.reconciliation_exception (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  exception_type             varchar(15) NOT NULL,
  statement_line_id          uuid,
  payment_id                 uuid,
  amount                     numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  assigned_to                uuid,
  CONSTRAINT pk_reconciliation_exception PRIMARY KEY (tenant_id, id)
);
CREATE TABLE payment.payout_batch (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  batch_ref                  varchar(20) NOT NULL,
  purpose                    varchar(15) NOT NULL,
  payment_count              integer NOT NULL,
  total_amount               numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(12) NOT NULL,
  released_by                uuid,
  CONSTRAINT pk_payout_batch PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_payout_batch_batch_ref UNIQUE (tenant_id, batch_ref)
);
CREATE TABLE claims.claim_register (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  domain                     varchar(10) NOT NULL,
  domain_claim_id            uuid NOT NULL,
  policy_ref                 uuid NOT NULL,
  registered_at              timestamptz NOT NULL,
  CONSTRAINT pk_claim_register PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_claim_register_claim_number UNIQUE (tenant_id, claim_number)
);
CREATE TABLE claims.claim_evidence (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  evidence_type              varchar(20) NOT NULL,
  status                     varchar(12) NOT NULL,
  document_id                uuid,
  requested_at               timestamptz,
  received_at                timestamptz,
  CONSTRAINT pk_claim_evidence PRIMARY KEY (tenant_id, id)
);
CREATE TABLE claims.claim_communication (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  direction                  varchar(8) NOT NULL,
  channel                    varchar(10) NOT NULL,
  document_id                uuid,
  sent_at                    timestamptz NOT NULL,
  CONSTRAINT pk_claim_communication PRIMARY KEY (tenant_id, id)
);
CREATE TABLE claims.fraud_indicator_hit (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  indicator_code             varchar(20) NOT NULL,
  score                      smallint NOT NULL,
  evaluated_at               timestamptz NOT NULL,
  referred                   boolean NOT NULL,
  CONSTRAINT pk_fraud_indicator_hit PRIMARY KEY (tenant_id, id)
);
CREATE TABLE claims.claim_payment_instruction (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  payee_party_id             uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(12) NOT NULL,
  approval_id                uuid,
  payment_id                 uuid,
  CONSTRAINT pk_claim_payment_instruction PRIMARY KEY (tenant_id, id)
);
CREATE TABLE claims.claim_financial_movement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  movement_type              varchar(15) NOT NULL,
  reserve_type               varchar(15) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  movement_at                timestamptz NOT NULL,
  subledger_journal_id       uuid,
  CONSTRAINT pk_claim_financial_movement PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.proposal (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  proposal_number            varchar(15) NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  applicant_party_id         uuid NOT NULL,
  intermediary_code          varchar(15),
  channel_code               varchar(12) NOT NULL,
  status                     varchar(15) NOT NULL,
  currency                   char(3) NOT NULL,
  frequency                  varchar(10) NOT NULL,
  quoted_premium             numeric(18,2),
  rating_request_id          uuid,
  submitted_at               timestamptz,
  CONSTRAINT pk_proposal PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_proposal_proposal_number UNIQUE (tenant_id, proposal_number)
);
CREATE TABLE life.proposal_insured_life (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  proposal_id                uuid NOT NULL,
  party_id                   uuid NOT NULL,
  life_role                  varchar(10) NOT NULL,
  age_next                   smallint NOT NULL,
  smoker_status              varchar(10) NOT NULL,
  occupation_code            varchar(10),
  CONSTRAINT pk_proposal_insured_life PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.proposal_cover (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  proposal_id                uuid NOT NULL,
  insured_life_id            uuid NOT NULL,
  benefit_code               varchar(20) NOT NULL,
  sum_assured                numeric(18,2) NOT NULL,
  term_years                 smallint NOT NULL,
  premium                    numeric(18,2) NOT NULL,
  CONSTRAINT pk_proposal_cover PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.disclosure_answer (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  proposal_id                uuid NOT NULL,
  insured_life_id            uuid NOT NULL,
  question_code              varchar(20) NOT NULL,
  question_version           integer NOT NULL,
  answer                     jsonb NOT NULL,
  CONSTRAINT pk_disclosure_answer PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.underwriting_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  proposal_id                uuid NOT NULL,
  status                     varchar(15) NOT NULL,
  assigned_to                uuid,
  ruleset_version            integer NOT NULL,
  decision_code              varchar(15),
  decision_reason            varchar(20),
  decided_at                 timestamptz,
  authority_role             varchar(30),
  CONSTRAINT pk_underwriting_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.underwriting_requirement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  case_id                    uuid NOT NULL,
  evidence_type              varchar(20) NOT NULL,
  status                     varchar(12) NOT NULL,
  document_id                uuid,
  requested_at               timestamptz NOT NULL,
  received_at                timestamptz,
  CONSTRAINT pk_underwriting_requirement PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.underwriting_decision_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  case_id                    uuid NOT NULL,
  benefit_code               varchar(20) NOT NULL,
  decision_code              varchar(15) NOT NULL,
  loading_pct                numeric(7,2),
  loading_per_mille          numeric(9,4),
  exclusion_code             varchar(20),
  postponed_until            date,
  CONSTRAINT pk_underwriting_decision_item PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.policy (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_number              varchar(15) NOT NULL,
  proposal_id                uuid NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  rating_version             integer NOT NULL,
  policyholder_party_id      uuid NOT NULL,
  status                     varchar(12) NOT NULL,
  inception_date             date NOT NULL,
  term_end_date              date,
  currency                   char(3) NOT NULL,
  premium_frequency          varchar(10) NOT NULL,
  billing_account_ref        uuid NOT NULL,
  intermediary_code          varchar(15),
  CONSTRAINT pk_policy PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_policy_policy_number UNIQUE (tenant_id, policy_number)
);
CREATE TABLE life.policy_insured_life (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  party_id                   uuid NOT NULL,
  life_role                  varchar(10) NOT NULL,
  entry_age                  smallint NOT NULL,
  CONSTRAINT pk_policy_insured_life PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.policy_benefit (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  benefit_code               varchar(20) NOT NULL,
  sum_assured                numeric(18,2) NOT NULL,
  premium                    numeric(18,2) NOT NULL,
  loading_pct                numeric(7,2),
  exclusion_codes            text[],
  start_date                 date NOT NULL,
  end_date                   date,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_policy_benefit PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.policy_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  event_type                 varchar(20) NOT NULL,
  effective_date             date NOT NULL,
  before                     jsonb NOT NULL,
  after                      jsonb NOT NULL,
  document_id                uuid,
  recorded_by                uuid NOT NULL,
  CONSTRAINT pk_policy_event PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.beneficiary (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  party_id                   uuid NOT NULL,
  relationship_code          varchar(15) NOT NULL,
  share_pct                  numeric(5,2) NOT NULL,
  valid_from                 date NOT NULL,
  valid_to                   date,
  CONSTRAINT pk_beneficiary PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.premium_schedule (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  frequency                  varchar(10) NOT NULL,
  premium                    numeric(18,2) NOT NULL,
  next_due_date              date NOT NULL,
  grace_until                date,
  missed_count               smallint NOT NULL,
  CONSTRAINT pk_premium_schedule PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.lapse_reinstatement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  event_type                 varchar(12) NOT NULL,
  effective_date             date NOT NULL,
  reason                     varchar(20) NOT NULL,
  evidence_ref               uuid,
  CONSTRAINT pk_lapse_reinstatement PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.cash_value (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  valuation_date             date NOT NULL,
  cash_value                 numeric(18,2) NOT NULL,
  surrender_value            numeric(18,2) NOT NULL,
  scale_code                 varchar(15) NOT NULL,
  scale_version              integer NOT NULL,
  CONSTRAINT pk_cash_value PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.policy_loan (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  loan_number                varchar(15) NOT NULL,
  policy_id                  uuid NOT NULL,
  principal                  numeric(18,2) NOT NULL,
  rate_code                  varchar(15) NOT NULL,
  balance                    numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  issued_at                  date NOT NULL,
  CONSTRAINT pk_policy_loan PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_policy_loan_loan_number UNIQUE (tenant_id, loan_number)
);
CREATE TABLE life.policy_loan_transaction (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  loan_id                    uuid NOT NULL,
  txn_type                   varchar(12) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  txn_date                   date NOT NULL,
  payment_ref                uuid,
  CONSTRAINT pk_policy_loan_transaction PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.claim (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  policy_id                  uuid NOT NULL,
  claim_type                 varchar(15) NOT NULL,
  event_date                 date NOT NULL,
  cause_code                 varchar(10),
  claimant_party_id          uuid NOT NULL,
  status                     varchar(15) NOT NULL,
  assessed_amount            numeric(18,2),
  approved_amount            numeric(18,2),
  decision_reason            varchar(20),
  authority_role             varchar(30),
  CONSTRAINT pk_claim PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_claim_claim_number UNIQUE (tenant_id, claim_number)
);
CREATE TABLE life.claim_benefit_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  policy_benefit_id          uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  CONSTRAINT pk_claim_benefit_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.maturity_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  maturity_date              date NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  option_code                varchar(15),
  payee_party_id             uuid,
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_maturity_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.group_scheme (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scheme_number              varchar(12) NOT NULL,
  employer_party_id          uuid NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  status                     varchar(10) NOT NULL,
  renewal_date               date NOT NULL,
  CONSTRAINT pk_group_scheme PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_group_scheme_scheme_number UNIQUE (tenant_id, scheme_number)
);
CREATE TABLE life.group_member (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scheme_id                  uuid NOT NULL,
  party_id                   uuid NOT NULL,
  category_code              varchar(10) NOT NULL,
  annual_salary              numeric(18,2),
  cover_amount               numeric(18,2) NOT NULL,
  joined_on                  date NOT NULL,
  left_on                    date,
  CONSTRAINT pk_group_member PRIMARY KEY (tenant_id, id)
);
CREATE TABLE life.valuation_extract_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  layout_code                varchar(20) NOT NULL,
  as_at                      date NOT NULL,
  row_count                  integer NOT NULL,
  document_id                uuid NOT NULL,
  assumption_set             varchar(20) NOT NULL,
  CONSTRAINT pk_valuation_extract_run PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.quote (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  quote_number               varchar(15) NOT NULL,
  quote_version              smallint NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  proposer_party_id          uuid NOT NULL,
  intermediary_code          varchar(15),
  status                     varchar(12) NOT NULL,
  valid_until                date NOT NULL,
  premium                    numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  rating_request_id          uuid NOT NULL,
  CONSTRAINT pk_quote PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_quote_quote_number UNIQUE (tenant_id, quote_number)
);
CREATE TABLE general.proposal (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  quote_id                   uuid NOT NULL,
  submitted_at               timestamptz NOT NULL,
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_proposal PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.underwriting_referral (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  quote_id                   uuid NOT NULL,
  reason_code                varchar(15) NOT NULL,
  status                     varchar(10) NOT NULL,
  decided_by                 uuid,
  decision_code              varchar(15),
  special_conditions         text[],
  CONSTRAINT pk_underwriting_referral PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.risk_object (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  object_type                varchar(15) NOT NULL,
  attributes                 jsonb NOT NULL,
  attribute_schema_version   integer NOT NULL,
  valuation                  numeric(18,2),
  valuation_basis            varchar(15),
  subdivision                varchar(6),
  postal_code                varchar(10),
  risk_zone                  varchar(10),
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_risk_object PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.policy (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_number              varchar(15) NOT NULL,
  product_code               varchar(15) NOT NULL,
  product_version            integer NOT NULL,
  rating_version             integer NOT NULL,
  policyholder_party_id      uuid NOT NULL,
  status                     varchar(12) NOT NULL,
  period_start               date NOT NULL,
  period_end                 date NOT NULL,
  currency                   char(3) NOT NULL,
  annual_premium             numeric(18,2) NOT NULL,
  intermediary_code          varchar(15),
  billing_account_ref        uuid NOT NULL,
  renewal_of_id              uuid,
  CONSTRAINT pk_policy PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_policy_policy_number UNIQUE (tenant_id, policy_number)
);
CREATE TABLE general.policy_risk (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  risk_object_id             uuid NOT NULL,
  sum_insured                numeric(18,2) NOT NULL,
  attached_from              date NOT NULL,
  detached_on                date,
  CONSTRAINT pk_policy_risk PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.coverage (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  policy_risk_id             uuid NOT NULL,
  coverage_code              varchar(20) NOT NULL,
  sum_insured                numeric(18,2) NOT NULL,
  excess_schedule_code       varchar(20),
  premium                    numeric(18,2) NOT NULL,
  extensions                 text[],
  CONSTRAINT pk_coverage PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.endorsement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  endorsement_number         varchar(20) NOT NULL,
  policy_id                  uuid NOT NULL,
  endorsement_type           varchar(15) NOT NULL,
  effective_date             date NOT NULL,
  premium_delta              numeric(18,2) NOT NULL,
  status                     varchar(12) NOT NULL,
  change_set                 jsonb NOT NULL,
  document_id                uuid,
  CONSTRAINT pk_endorsement PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_endorsement_endorsement_number UNIQUE (tenant_id, endorsement_number)
);
CREATE TABLE general.cancellation (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  reason_code                varchar(15) NOT NULL,
  effective_date             date NOT NULL,
  refund_amount              numeric(18,2) NOT NULL,
  refund_basis               varchar(12) NOT NULL,
  CONSTRAINT pk_cancellation PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.claim (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  policy_id                  uuid NOT NULL,
  risk_object_id             uuid,
  loss_date                  date NOT NULL,
  notified_at                timestamptz NOT NULL,
  cause_code                 varchar(15) NOT NULL,
  peril_code                 varchar(10) NOT NULL,
  cat_event_code             varchar(15),
  status                     varchar(15) NOT NULL,
  description                text,
  CONSTRAINT pk_claim PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_claim_claim_number UNIQUE (tenant_id, claim_number)
);
CREATE TABLE general.claim_party (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  party_id                   uuid NOT NULL,
  role                       varchar(15) NOT NULL,
  insurer_ref                varchar(60),
  CONSTRAINT pk_claim_party PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.loss_assessment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  assessor_party_id          uuid NOT NULL,
  assessed_amount            numeric(18,2) NOT NULL,
  excess_applied             numeric(18,2) NOT NULL,
  report_document_id         uuid,
  assessed_at                timestamptz NOT NULL,
  CONSTRAINT pk_loss_assessment PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.repair_estimate (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  repairer_party_id          uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_repair_estimate PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.recovery_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  recovery_type              varchar(15) NOT NULL,
  counterparty_party_id      uuid,
  expected_amount            numeric(18,2) NOT NULL,
  recovered_amount           numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_recovery_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.salvage_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  recovery_case_id           uuid NOT NULL,
  description                varchar(200) NOT NULL,
  disposal_method            varchar(10),
  proceeds                   numeric(18,2),
  CONSTRAINT pk_salvage_item PRIMARY KEY (tenant_id, id)
);
CREATE TABLE general.exposure_snapshot (
  tenant_id                  uuid NOT NULL,
  refreshed_at               timestamptz NOT NULL,
  as_at                      date NOT NULL,
  zone                       varchar(15) NOT NULL,
  peril_code                 varchar(10) NOT NULL,
  sum_insured                numeric(20,2) NOT NULL,
  currency                   char(3) NOT NULL,
  CONSTRAINT pk_exposure_snapshot PRIMARY KEY (tenant_id, as_at, zone, peril_code)
);
CREATE TABLE health.scheme (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scheme_number              varchar(12) NOT NULL,
  sponsor_code               varchar(15) NOT NULL,
  status                     varchar(10) NOT NULL,
  start_date                 date NOT NULL,
  renewal_date               date NOT NULL,
  CONSTRAINT pk_scheme PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_scheme_scheme_number UNIQUE (tenant_id, scheme_number)
);
CREATE TABLE health.membership (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  member_number              varchar(12) NOT NULL,
  principal_party_id         uuid NOT NULL,
  scheme_id                  uuid,
  plan_code                  varchar(15) NOT NULL,
  option_code                varchar(10) NOT NULL,
  plan_version               integer NOT NULL,
  status                     varchar(12) NOT NULL,
  start_date                 date NOT NULL,
  end_date                   date,
  CONSTRAINT pk_membership PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_membership_member_number UNIQUE (tenant_id, member_number)
);
CREATE TABLE health.member_dependant (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  membership_id              uuid NOT NULL,
  party_id                   uuid NOT NULL,
  dependant_type             varchar(10) NOT NULL,
  suffix                     char(2) NOT NULL,
  start_date                 date NOT NULL,
  end_date                   date,
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_member_dependant PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.membership_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  membership_id              uuid NOT NULL,
  event_type                 varchar(15) NOT NULL,
  effective_date             date NOT NULL,
  reason                     varchar(30) NOT NULL,
  CONSTRAINT pk_membership_event PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.benefit_accumulator (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  membership_id              uuid NOT NULL,
  beneficiary_party_id       uuid NOT NULL,
  benefit_category           varchar(15) NOT NULL,
  period_start               date NOT NULL,
  period_end                 date NOT NULL,
  limit_amount               numeric(18,2) NOT NULL,
  used_amount                numeric(18,2) NOT NULL,
  CONSTRAINT pk_benefit_accumulator PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.eligibility_decision (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  member_number              varchar(12) NOT NULL,
  dependant_suffix           char(2) NOT NULL,
  provider_id                varchar(15),
  service_type               varchar(20) NOT NULL,
  response_code              varchar(10) NOT NULL,
  decided_at                 timestamptz NOT NULL,
  CONSTRAINT pk_eligibility_decision PRIMARY KEY (tenant_id, id, decided_at)
) PARTITION BY RANGE (decided_at);
CREATE TABLE health.preauthorisation (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  auth_number                varchar(15) NOT NULL,
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  provider_id                varchar(15) NOT NULL,
  diagnosis_codes            text[] NOT NULL,
  requested_amount           numeric(18,2) NOT NULL,
  authorised_amount          numeric(18,2),
  status                     varchar(12) NOT NULL,
  valid_from                 date,
  valid_to                   date,
  clinical_reviewer          uuid,
  CONSTRAINT pk_preauthorisation PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_preauthorisation_auth_number UNIQUE (tenant_id, auth_number)
);
CREATE TABLE health.preauth_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  preauth_id                 uuid NOT NULL,
  procedure_code             varchar(10) NOT NULL,
  quantity                   smallint NOT NULL,
  authorised_amount          numeric(18,2) NOT NULL,
  CONSTRAINT pk_preauth_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.medical_claim (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  source                     varchar(10) NOT NULL,
  provider_id                varchar(15),
  membership_id              uuid NOT NULL,
  patient_party_id           uuid NOT NULL,
  service_date               date NOT NULL,
  submitted_at               timestamptz NOT NULL,
  format_code                varchar(15) NOT NULL,
  preauth_id                 uuid,
  total_claimed              numeric(18,2) NOT NULL,
  total_paid                 numeric(18,2),
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_medical_claim PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_medical_claim_claim_number UNIQUE (tenant_id, claim_number)
);
CREATE TABLE health.medical_claim_line (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  line_no                    smallint NOT NULL,
  tariff_code                varchar(15) NOT NULL,
  diagnosis_code             varchar(10),
  quantity                   smallint NOT NULL,
  claimed_amount             numeric(18,2) NOT NULL,
  allowed_amount             numeric(18,2) NOT NULL,
  copay_amount               numeric(18,2) NOT NULL,
  paid_amount                numeric(18,2) NOT NULL,
  adjustment_reason          varchar(15),
  CONSTRAINT pk_medical_claim_line PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.utilisation_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  membership_id              uuid NOT NULL,
  rule_code                  varchar(15) NOT NULL,
  status                     varchar(12) NOT NULL,
  reviewer_id                uuid,
  outcome                    varchar(20),
  opened_at                  timestamptz NOT NULL,
  CONSTRAINT pk_utilisation_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.provider_payment_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_code                   varchar(15) NOT NULL,
  cut_off                    timestamptz NOT NULL,
  claim_count                integer NOT NULL,
  total_amount               numeric(18,2) NOT NULL,
  status                     varchar(12) NOT NULL,
  payout_batch_ref           uuid,
  CONSTRAINT pk_provider_payment_run PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.remittance_advice (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  provider_id                varchar(15) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  document_id                uuid NOT NULL,
  CONSTRAINT pk_remittance_advice PRIMARY KEY (tenant_id, id)
);
CREATE TABLE health.fraud_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  subject_type               varchar(12) NOT NULL,
  subject_ref                varchar(30) NOT NULL,
  indicator_codes            text[] NOT NULL,
  status                     varchar(12) NOT NULL,
  outcome_code               varchar(15),
  opened_at                  timestamptz NOT NULL,
  CONSTRAINT pk_fraud_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE finance.journal (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  journal_number             varchar(20) NOT NULL,
  accounting_date            date NOT NULL,
  period_code                char(7) NOT NULL,
  source_event_id            uuid NOT NULL,
  source_domain              varchar(10) NOT NULL,
  status                     varchar(10) NOT NULL,
  posted_at                  timestamptz NOT NULL,
  reverses_journal_id        uuid,
  CONSTRAINT pk_journal PRIMARY KEY (tenant_id, id, accounting_date),
  CONSTRAINT uq_journal_journal_number UNIQUE (tenant_id, journal_number, accounting_date)
) PARTITION BY RANGE (accounting_date);
CREATE TABLE finance.subledger_entry (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  journal_id                 uuid NOT NULL,
  line_no                    smallint NOT NULL,
  account_code               varchar(20) NOT NULL,
  debit                      numeric(18,2) NOT NULL,
  credit                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  base_amount                numeric(18,2) NOT NULL,
  fx_rate                    numeric(18,8),
  accounting_date            date NOT NULL,
  transaction_date           date NOT NULL,
  business_ref_type          varchar(30) NOT NULL,
  business_ref_id            uuid NOT NULL,
  posting_rule_code          varchar(20) NOT NULL,
  posting_rule_version       integer NOT NULL,
  CONSTRAINT pk_subledger_entry PRIMARY KEY (tenant_id, id, accounting_date),
  CONSTRAINT ck_subledger_entry_3 CHECK (debit = 0 OR credit = 0)
) PARTITION BY RANGE (accounting_date);
CREATE TABLE finance.posting_exception (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  source_event_id            uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  error                      text NOT NULL,
  status                     varchar(10) NOT NULL,
  resolved_by                uuid,
  CONSTRAINT pk_posting_exception PRIMARY KEY (tenant_id, id)
);
CREATE TABLE finance.gl_export_batch (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  layout_code                varchar(15) NOT NULL,
  period_code                char(7) NOT NULL,
  from_date                  date NOT NULL,
  to_date                    date NOT NULL,
  line_count                 integer NOT NULL,
  status                     varchar(10) NOT NULL,
  document_id                uuid,
  CONSTRAINT pk_gl_export_batch PRIMARY KEY (tenant_id, id)
);
CREATE TABLE finance.reconciliation_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  rule_code                  varchar(15) NOT NULL,
  as_at                      date NOT NULL,
  matched_count              integer NOT NULL,
  break_count                integer NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_reconciliation_run PRIMARY KEY (tenant_id, id)
);
CREATE TABLE finance.reconciliation_break (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  run_id                     uuid NOT NULL,
  break_type                 varchar(12) NOT NULL,
  reference                  varchar(80) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  CONSTRAINT pk_reconciliation_break PRIMARY KEY (tenant_id, id)
);
CREATE TABLE commission.commission_entitlement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  intermediary_code          varchar(15) NOT NULL,
  source_type                varchar(15) NOT NULL,
  source_id                  uuid NOT NULL,
  policy_ref                 uuid NOT NULL,
  plan_code                  varchar(15) NOT NULL,
  plan_version               integer NOT NULL,
  basis_amount               numeric(18,2) NOT NULL,
  rate                       numeric(7,4) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(12) NOT NULL,
  statement_id               uuid,
  CONSTRAINT pk_commission_entitlement PRIMARY KEY (tenant_id, id)
);
CREATE TABLE commission.clawback (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  entitlement_id             uuid NOT NULL,
  reason                     varchar(20) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  CONSTRAINT pk_clawback PRIMARY KEY (tenant_id, id)
);
CREATE TABLE commission.commission_statement (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  intermediary_code          varchar(15) NOT NULL,
  period_code                char(7) NOT NULL,
  gross_amount               numeric(18,2) NOT NULL,
  tax_amount                 numeric(18,2) NOT NULL,
  net_amount                 numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  document_id                uuid,
  payment_ref                uuid,
  CONSTRAINT pk_commission_statement PRIMARY KEY (tenant_id, id)
);
CREATE TABLE reinsurance.cession (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  treaty_code                varchar(15) NOT NULL,
  source_type                varchar(20) NOT NULL,
  source_id                  uuid NOT NULL,
  period_start               date NOT NULL,
  period_end                 date NOT NULL,
  ceded_sum_insured          numeric(20,2) NOT NULL,
  ceded_premium              numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  rule_code                  varchar(15) NOT NULL,
  rule_version               integer NOT NULL,
  calculated_at              timestamptz NOT NULL,
  CONSTRAINT pk_cession PRIMARY KEY (tenant_id, id)
);
CREATE TABLE reinsurance.recovery (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  claim_number               varchar(25) NOT NULL,
  treaty_code                varchar(15) NOT NULL,
  recoverable                numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  status                     varchar(10) NOT NULL,
  settled_amount             numeric(18,2),
  CONSTRAINT pk_recovery PRIMARY KEY (tenant_id, id)
);
CREATE TABLE reinsurance.bordereau (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  treaty_code                varchar(15) NOT NULL,
  period_code                char(7) NOT NULL,
  layout_code                varchar(15) NOT NULL,
  row_count                  integer NOT NULL,
  status                     varchar(10) NOT NULL,
  document_id                uuid,
  CONSTRAINT pk_bordereau PRIMARY KEY (tenant_id, id)
);
CREATE TABLE reporting.regulatory_return_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  return_code                varchar(20) NOT NULL,
  period_code                char(7) NOT NULL,
  status                     varchar(12) NOT NULL,
  document_id                uuid,
  approved_by                uuid,
  submitted_at               timestamptz,
  CONSTRAINT pk_regulatory_return_run PRIMARY KEY (tenant_id, id)
);
CREATE TABLE reporting.report_run (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  report_code                varchar(20) NOT NULL,
  requested_by               uuid NOT NULL,
  parameters                 jsonb NOT NULL,
  status                     varchar(10) NOT NULL,
  document_id                uuid,
  exported                   boolean NOT NULL,
  CONSTRAINT pk_report_run PRIMARY KEY (tenant_id, id)
);
CREATE TABLE analytics.fact_written_premium (
  tenant_id                  uuid NOT NULL,
  refreshed_at               timestamptz NOT NULL,
  date_key                   date NOT NULL,
  product_code               varchar(15) NOT NULL,
  channel_code               varchar(12) NOT NULL,
  branch_code                varchar(15) NOT NULL,
  policy_count               integer NOT NULL,
  written_premium            numeric(20,2) NOT NULL,
  currency                   char(3) NOT NULL,
  CONSTRAINT pk_fact_written_premium PRIMARY KEY (tenant_id, date_key, product_code, channel_code, branch_code)
);
CREATE TABLE analytics.fact_claims_incurred (
  tenant_id                  uuid NOT NULL,
  refreshed_at               timestamptz NOT NULL,
  month_key                  char(7) NOT NULL,
  product_code               varchar(15) NOT NULL,
  cause_code                 varchar(15) NOT NULL,
  claim_count                integer NOT NULL,
  incurred                   numeric(20,2) NOT NULL,
  currency                   char(3) NOT NULL,
  CONSTRAINT pk_fact_claims_incurred PRIMARY KEY (tenant_id, month_key, product_code, cause_code)
);
CREATE TABLE tenant.tenant (
  id                         uuid NOT NULL,
  tenant_code                varchar(10) NOT NULL,
  legal_name                 varchar(150) NOT NULL,
  isolation_profile          char(1) NOT NULL,
  status                     varchar(12) NOT NULL,
  created_at                 timestamptz NOT NULL,
  CONSTRAINT pk_tenant PRIMARY KEY (id),
  CONSTRAINT uq_tenant_tenant_code UNIQUE (tenant_code)
);
CREATE TABLE tenant.onboarding_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  current_step               varchar(30) NOT NULL,
  status                     varchar(12) NOT NULL,
  started_at                 timestamptz NOT NULL,
  completed_at               timestamptz,
  CONSTRAINT pk_onboarding_case PRIMARY KEY (tenant_id, id)
);
CREATE TABLE tenant.tenant_status_history (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  from_status                varchar(12) NOT NULL,
  to_status                  varchar(12) NOT NULL,
  reason                     text NOT NULL,
  changed_by                 uuid NOT NULL,
  changed_at                 timestamptz NOT NULL,
  CONSTRAINT pk_tenant_status_history PRIMARY KEY (tenant_id, id)
);
CREATE TABLE iam.user_account (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  username                   varchar(120) NOT NULL,
  display_name               varchar(120) NOT NULL,
  idp_code                   varchar(15) NOT NULL,
  external_subject           varchar(120),
  status                     varchar(12) NOT NULL,
  mfa_enrolled               boolean NOT NULL,
  last_login_at              timestamptz,
  CONSTRAINT pk_user_account PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_user_account_username UNIQUE (tenant_id, username)
);
CREATE TABLE iam.user_role_assignment (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  user_id                    uuid NOT NULL,
  role_code                  varchar(30) NOT NULL,
  scope_products             text[],
  scope_branches             text[],
  valid_from                 date NOT NULL,
  valid_to                   date,
  assigned_by                uuid NOT NULL,
  CONSTRAINT pk_user_role_assignment PRIMARY KEY (tenant_id, id)
);
CREATE TABLE iam.session (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  user_id                    uuid NOT NULL,
  issued_at                  timestamptz NOT NULL,
  expires_at                 timestamptz NOT NULL,
  revoked_at                 timestamptz,
  client_ip                  inet,
  CONSTRAINT pk_session PRIMARY KEY (tenant_id, id)
);
CREATE TABLE iam.privileged_access_grant (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  user_id                    uuid NOT NULL,
  purpose                    text NOT NULL,
  approved_by                uuid NOT NULL,
  valid_from                 timestamptz NOT NULL,
  valid_to                   timestamptz NOT NULL,
  CONSTRAINT pk_privileged_access_grant PRIMARY KEY (tenant_id, id)
);
CREATE TABLE iam.access_review (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scope                      varchar(20) NOT NULL,
  reviewer_id                uuid NOT NULL,
  due_date                   date NOT NULL,
  status                     varchar(12) NOT NULL,
  items_total                integer NOT NULL,
  items_revoked              integer NOT NULL,
  CONSTRAINT pk_access_review PRIMARY KEY (tenant_id, id)
);
CREATE TABLE audit.audit_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  event_type                 varchar(40) NOT NULL,
  actor_type                 varchar(10) NOT NULL,
  actor_id                   varchar(80) NOT NULL,
  operation                  varchar(40) NOT NULL,
  target_type                varchar(30) NOT NULL,
  target_id                  varchar(80) NOT NULL,
  outcome                    varchar(10) NOT NULL,
  occurred_at                timestamptz NOT NULL,
  correlation_id             uuid NOT NULL,
  details                    jsonb,
  CONSTRAINT pk_audit_event PRIMARY KEY (tenant_id, id, occurred_at)
) PARTITION BY RANGE (occurred_at);

-- Foreign keys
ALTER TABLE sdm.import_error ADD CONSTRAINT fk_import_error_batch_id FOREIGN KEY (tenant_id, batch_id) REFERENCES sdm.import_batch (tenant_id, id);
ALTER TABLE party.party ADD CONSTRAINT fk_party_merged_into_id FOREIGN KEY (tenant_id, merged_into_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.individual ADD CONSTRAINT fk_individual_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.organisation ADD CONSTRAINT fk_organisation_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.party_identifier ADD CONSTRAINT fk_party_identifier_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.address ADD CONSTRAINT fk_address_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.contact_point ADD CONSTRAINT fk_contact_point_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.party_relationship ADD CONSTRAINT fk_party_relationship_from_party_id FOREIGN KEY (tenant_id, from_party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.party_relationship ADD CONSTRAINT fk_party_relationship_to_party_id FOREIGN KEY (tenant_id, to_party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.party_role ADD CONSTRAINT fk_party_role_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.consent_record ADD CONSTRAINT fk_consent_record_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.duplicate_candidate ADD CONSTRAINT fk_duplicate_candidate_party_a_id FOREIGN KEY (tenant_id, party_a_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.duplicate_candidate ADD CONSTRAINT fk_duplicate_candidate_party_b_id FOREIGN KEY (tenant_id, party_b_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.merge_history ADD CONSTRAINT fk_merge_history_survivor_id FOREIGN KEY (tenant_id, survivor_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE party.merge_history ADD CONSTRAINT fk_merge_history_merged_id FOREIGN KEY (tenant_id, merged_id) REFERENCES party.party (tenant_id, id);
ALTER TABLE product.simulation_result ADD CONSTRAINT fk_simulation_result_run_id FOREIGN KEY (tenant_id, run_id) REFERENCES product.simulation_run (tenant_id, id);
ALTER TABLE workflow.task ADD CONSTRAINT fk_task_process_instance_id FOREIGN KEY (tenant_id, process_instance_id) REFERENCES workflow.process_instance (tenant_id, id);
ALTER TABLE workflow.timer ADD CONSTRAINT fk_timer_process_instance_id FOREIGN KEY (tenant_id, process_instance_id) REFERENCES workflow.process_instance (tenant_id, id);
ALTER TABLE document.document_access_log ADD CONSTRAINT fk_document_access_log_document_id FOREIGN KEY (tenant_id, document_id) REFERENCES document.document (tenant_id, id);
ALTER TABLE document.generation_request ADD CONSTRAINT fk_generation_request_document_id FOREIGN KEY (tenant_id, document_id) REFERENCES document.document (tenant_id, id);
ALTER TABLE document.delivery ADD CONSTRAINT fk_delivery_document_id FOREIGN KEY (tenant_id, document_id) REFERENCES document.document (tenant_id, id);
ALTER TABLE billing.premium_schedule ADD CONSTRAINT fk_premium_schedule_billing_account_id FOREIGN KEY (tenant_id, billing_account_id) REFERENCES billing.billing_account (tenant_id, id);
ALTER TABLE billing.premium_instalment ADD CONSTRAINT fk_premium_instalment_schedule_id FOREIGN KEY (tenant_id, schedule_id) REFERENCES billing.premium_schedule (tenant_id, id);
ALTER TABLE billing.premium_instalment ADD CONSTRAINT fk_premium_instalment_invoice_id FOREIGN KEY (tenant_id, invoice_id) REFERENCES billing.invoice (tenant_id, id);
ALTER TABLE billing.invoice ADD CONSTRAINT fk_invoice_billing_account_id FOREIGN KEY (tenant_id, billing_account_id) REFERENCES billing.billing_account (tenant_id, id);
ALTER TABLE billing.invoice ADD CONSTRAINT fk_invoice_reverses_invoice_id FOREIGN KEY (tenant_id, reverses_invoice_id) REFERENCES billing.invoice (tenant_id, id);
ALTER TABLE billing.invoice_line ADD CONSTRAINT fk_invoice_line_invoice_id FOREIGN KEY (tenant_id, invoice_id) REFERENCES billing.invoice (tenant_id, id);
ALTER TABLE billing.adjustment ADD CONSTRAINT fk_adjustment_invoice_id FOREIGN KEY (tenant_id, invoice_id) REFERENCES billing.invoice (tenant_id, id);
ALTER TABLE billing.adjustment ADD CONSTRAINT fk_adjustment_credit_note_id FOREIGN KEY (tenant_id, credit_note_id) REFERENCES billing.invoice (tenant_id, id);
ALTER TABLE billing.arrears_case ADD CONSTRAINT fk_arrears_case_billing_account_id FOREIGN KEY (tenant_id, billing_account_id) REFERENCES billing.billing_account (tenant_id, id);
ALTER TABLE billing.refund_request ADD CONSTRAINT fk_refund_request_billing_account_id FOREIGN KEY (tenant_id, billing_account_id) REFERENCES billing.billing_account (tenant_id, id);
ALTER TABLE payment.payment_status_history ADD CONSTRAINT fk_payment_status_history_payment_id FOREIGN KEY (tenant_id, payment_id) REFERENCES payment.payment (tenant_id, id);
ALTER TABLE payment.payment_allocation ADD CONSTRAINT fk_payment_allocation_payment_id FOREIGN KEY (tenant_id, payment_id) REFERENCES payment.payment (tenant_id, id);
ALTER TABLE payment.payment_allocation ADD CONSTRAINT fk_payment_allocation_reversed_by_id FOREIGN KEY (tenant_id, reversed_by_id) REFERENCES payment.payment_allocation (tenant_id, id);
ALTER TABLE payment.bank_statement_line ADD CONSTRAINT fk_bank_statement_line_matched_payment_id FOREIGN KEY (tenant_id, matched_payment_id) REFERENCES payment.payment (tenant_id, id);
ALTER TABLE payment.reconciliation_exception ADD CONSTRAINT fk_reconciliation_exception_statement_line_id FOREIGN KEY (tenant_id, statement_line_id) REFERENCES payment.bank_statement_line (tenant_id, id);
ALTER TABLE payment.reconciliation_exception ADD CONSTRAINT fk_reconciliation_exception_payment_id FOREIGN KEY (tenant_id, payment_id) REFERENCES payment.payment (tenant_id, id);
ALTER TABLE claims.claim_evidence ADD CONSTRAINT fk_claim_evidence_claim_number FOREIGN KEY (tenant_id, claim_number) REFERENCES claims.claim_register (tenant_id, claim_number);
ALTER TABLE claims.claim_communication ADD CONSTRAINT fk_claim_communication_claim_number FOREIGN KEY (tenant_id, claim_number) REFERENCES claims.claim_register (tenant_id, claim_number);
ALTER TABLE claims.fraud_indicator_hit ADD CONSTRAINT fk_fraud_indicator_hit_claim_number FOREIGN KEY (tenant_id, claim_number) REFERENCES claims.claim_register (tenant_id, claim_number);
ALTER TABLE claims.claim_payment_instruction ADD CONSTRAINT fk_claim_payment_instruction_claim_number FOREIGN KEY (tenant_id, claim_number) REFERENCES claims.claim_register (tenant_id, claim_number);
ALTER TABLE claims.claim_financial_movement ADD CONSTRAINT fk_claim_financial_movement_claim_number FOREIGN KEY (tenant_id, claim_number) REFERENCES claims.claim_register (tenant_id, claim_number);
ALTER TABLE life.proposal_insured_life ADD CONSTRAINT fk_proposal_insured_life_proposal_id FOREIGN KEY (tenant_id, proposal_id) REFERENCES life.proposal (tenant_id, id);
ALTER TABLE life.proposal_cover ADD CONSTRAINT fk_proposal_cover_proposal_id FOREIGN KEY (tenant_id, proposal_id) REFERENCES life.proposal (tenant_id, id);
ALTER TABLE life.proposal_cover ADD CONSTRAINT fk_proposal_cover_insured_life_id FOREIGN KEY (tenant_id, insured_life_id) REFERENCES life.proposal_insured_life (tenant_id, id);
ALTER TABLE life.disclosure_answer ADD CONSTRAINT fk_disclosure_answer_proposal_id FOREIGN KEY (tenant_id, proposal_id) REFERENCES life.proposal (tenant_id, id);
ALTER TABLE life.disclosure_answer ADD CONSTRAINT fk_disclosure_answer_insured_life_id FOREIGN KEY (tenant_id, insured_life_id) REFERENCES life.proposal_insured_life (tenant_id, id);
ALTER TABLE life.underwriting_case ADD CONSTRAINT fk_underwriting_case_proposal_id FOREIGN KEY (tenant_id, proposal_id) REFERENCES life.proposal (tenant_id, id);
ALTER TABLE life.underwriting_requirement ADD CONSTRAINT fk_underwriting_requirement_case_id FOREIGN KEY (tenant_id, case_id) REFERENCES life.underwriting_case (tenant_id, id);
ALTER TABLE life.underwriting_decision_item ADD CONSTRAINT fk_underwriting_decision_item_case_id FOREIGN KEY (tenant_id, case_id) REFERENCES life.underwriting_case (tenant_id, id);
ALTER TABLE life.policy ADD CONSTRAINT fk_policy_proposal_id FOREIGN KEY (tenant_id, proposal_id) REFERENCES life.proposal (tenant_id, id);
ALTER TABLE life.policy_insured_life ADD CONSTRAINT fk_policy_insured_life_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.policy_benefit ADD CONSTRAINT fk_policy_benefit_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.policy_event ADD CONSTRAINT fk_policy_event_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.beneficiary ADD CONSTRAINT fk_beneficiary_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.premium_schedule ADD CONSTRAINT fk_premium_schedule_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.lapse_reinstatement ADD CONSTRAINT fk_lapse_reinstatement_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.cash_value ADD CONSTRAINT fk_cash_value_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.policy_loan ADD CONSTRAINT fk_policy_loan_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.policy_loan_transaction ADD CONSTRAINT fk_policy_loan_transaction_loan_id FOREIGN KEY (tenant_id, loan_id) REFERENCES life.policy_loan (tenant_id, id);
ALTER TABLE life.claim ADD CONSTRAINT fk_claim_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.claim_benefit_line ADD CONSTRAINT fk_claim_benefit_line_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES life.claim (tenant_id, id);
ALTER TABLE life.claim_benefit_line ADD CONSTRAINT fk_claim_benefit_line_policy_benefit_id FOREIGN KEY (tenant_id, policy_benefit_id) REFERENCES life.policy_benefit (tenant_id, id);
ALTER TABLE life.maturity_case ADD CONSTRAINT fk_maturity_case_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES life.policy (tenant_id, id);
ALTER TABLE life.group_member ADD CONSTRAINT fk_group_member_scheme_id FOREIGN KEY (tenant_id, scheme_id) REFERENCES life.group_scheme (tenant_id, id);
ALTER TABLE general.proposal ADD CONSTRAINT fk_proposal_quote_id FOREIGN KEY (tenant_id, quote_id) REFERENCES general.quote (tenant_id, id);
ALTER TABLE general.underwriting_referral ADD CONSTRAINT fk_underwriting_referral_quote_id FOREIGN KEY (tenant_id, quote_id) REFERENCES general.quote (tenant_id, id);
ALTER TABLE general.policy ADD CONSTRAINT fk_policy_renewal_of_id FOREIGN KEY (tenant_id, renewal_of_id) REFERENCES general.policy (tenant_id, id);
ALTER TABLE general.policy_risk ADD CONSTRAINT fk_policy_risk_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id);
ALTER TABLE general.policy_risk ADD CONSTRAINT fk_policy_risk_risk_object_id FOREIGN KEY (tenant_id, risk_object_id) REFERENCES general.risk_object (tenant_id, id);
ALTER TABLE general.coverage ADD CONSTRAINT fk_coverage_policy_risk_id FOREIGN KEY (tenant_id, policy_risk_id) REFERENCES general.policy_risk (tenant_id, id);
ALTER TABLE general.endorsement ADD CONSTRAINT fk_endorsement_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id);
ALTER TABLE general.cancellation ADD CONSTRAINT fk_cancellation_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id);
ALTER TABLE general.claim ADD CONSTRAINT fk_claim_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id);
ALTER TABLE general.claim ADD CONSTRAINT fk_claim_risk_object_id FOREIGN KEY (tenant_id, risk_object_id) REFERENCES general.risk_object (tenant_id, id);
ALTER TABLE general.claim_party ADD CONSTRAINT fk_claim_party_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES general.claim (tenant_id, id);
ALTER TABLE general.loss_assessment ADD CONSTRAINT fk_loss_assessment_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES general.claim (tenant_id, id);
ALTER TABLE general.repair_estimate ADD CONSTRAINT fk_repair_estimate_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES general.claim (tenant_id, id);
ALTER TABLE general.recovery_case ADD CONSTRAINT fk_recovery_case_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES general.claim (tenant_id, id);
ALTER TABLE general.salvage_item ADD CONSTRAINT fk_salvage_item_recovery_case_id FOREIGN KEY (tenant_id, recovery_case_id) REFERENCES general.recovery_case (tenant_id, id);
ALTER TABLE health.membership ADD CONSTRAINT fk_membership_scheme_id FOREIGN KEY (tenant_id, scheme_id) REFERENCES health.scheme (tenant_id, id);
ALTER TABLE health.member_dependant ADD CONSTRAINT fk_member_dependant_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.membership_event ADD CONSTRAINT fk_membership_event_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.benefit_accumulator ADD CONSTRAINT fk_benefit_accumulator_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.preauthorisation ADD CONSTRAINT fk_preauthorisation_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.preauth_line ADD CONSTRAINT fk_preauth_line_preauth_id FOREIGN KEY (tenant_id, preauth_id) REFERENCES health.preauthorisation (tenant_id, id);
ALTER TABLE health.medical_claim ADD CONSTRAINT fk_medical_claim_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.medical_claim ADD CONSTRAINT fk_medical_claim_preauth_id FOREIGN KEY (tenant_id, preauth_id) REFERENCES health.preauthorisation (tenant_id, id);
ALTER TABLE health.medical_claim_line ADD CONSTRAINT fk_medical_claim_line_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES health.medical_claim (tenant_id, id);
ALTER TABLE health.utilisation_case ADD CONSTRAINT fk_utilisation_case_membership_id FOREIGN KEY (tenant_id, membership_id) REFERENCES health.membership (tenant_id, id);
ALTER TABLE health.remittance_advice ADD CONSTRAINT fk_remittance_advice_run_id FOREIGN KEY (tenant_id, run_id) REFERENCES health.provider_payment_run (tenant_id, id);
ALTER TABLE finance.journal ADD CONSTRAINT fk_journal_reverses_journal_id FOREIGN KEY (tenant_id, reverses_journal_id, accounting_date) REFERENCES finance.journal (tenant_id, id, accounting_date);
ALTER TABLE finance.subledger_entry ADD CONSTRAINT fk_subledger_entry_journal_id FOREIGN KEY (tenant_id, journal_id, accounting_date) REFERENCES finance.journal (tenant_id, id, accounting_date);
ALTER TABLE finance.reconciliation_break ADD CONSTRAINT fk_reconciliation_break_run_id FOREIGN KEY (tenant_id, run_id) REFERENCES finance.reconciliation_run (tenant_id, id);
ALTER TABLE commission.commission_entitlement ADD CONSTRAINT fk_commission_entitlement_statement_id FOREIGN KEY (tenant_id, statement_id) REFERENCES commission.commission_statement (tenant_id, id);
ALTER TABLE commission.clawback ADD CONSTRAINT fk_clawback_entitlement_id FOREIGN KEY (tenant_id, entitlement_id) REFERENCES commission.commission_entitlement (tenant_id, id);
ALTER TABLE iam.user_role_assignment ADD CONSTRAINT fk_user_role_assignment_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.user_role_assignment ADD CONSTRAINT fk_user_role_assignment_assigned_by FOREIGN KEY (tenant_id, assigned_by) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.session ADD CONSTRAINT fk_session_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.privileged_access_grant ADD CONSTRAINT fk_privileged_access_grant_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.privileged_access_grant ADD CONSTRAINT fk_privileged_access_grant_approved_by FOREIGN KEY (tenant_id, approved_by) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.access_review ADD CONSTRAINT fk_access_review_reviewer_id FOREIGN KEY (tenant_id, reviewer_id) REFERENCES iam.user_account (tenant_id, id);

-- Indexes
CREATE UNIQUE INDEX uq_idempotency_key_1 ON integration.idempotency_key (tenant_id, idem_key);
CREATE INDEX ix_idempotency_key_2 ON integration.idempotency_key (tenant_id, expires_at);
CREATE UNIQUE INDEX uq_inbox_message_1 ON integration.inbox_message (tenant_id, consumer, event_id);
CREATE INDEX ix_webhook_delivery_1 ON integration.webhook_delivery (tenant_id, status, next_attempt_at);
CREATE INDEX ix_dead_letter_message_1 ON integration.dead_letter_message (tenant_id, resolution, first_failed_at);
CREATE INDEX ix_external_call_log_1 ON integration.external_call_log (tenant_id, partner_code, called_at);
CREATE INDEX ix_outbox_event_1 ON integration.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON sdm.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON party.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON product.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON rating.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON rules.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON workflow.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON document.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON billing.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON payment.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON claims.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON life.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON general.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON health.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON finance.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON commission.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON reinsurance.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON reporting.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON iam.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_event_1 ON audit.outbox_event (tenant_id, published_at) WHERE published_at IS NULL;
CREATE INDEX ix_change_request_1 ON sdm.change_request (tenant_id, status, entity_code);
CREATE INDEX ix_import_batch_1 ON sdm.import_batch (tenant_id, status);
CREATE INDEX ix_import_error_1 ON sdm.import_error (tenant_id, batch_id);
CREATE INDEX ix_party_1 ON party.party USING gin (display_name gin_trgm_ops);
CREATE INDEX ix_individual_1 ON party.individual (tenant_id, surname, birth_date);
CREATE INDEX ix_organisation_1 ON party.organisation (tenant_id, registration_number);
CREATE UNIQUE INDEX uq_party_identifier_1 ON party.party_identifier (tenant_id, doc_type, country, number_hash);
CREATE INDEX ix_address_1 ON party.address (tenant_id, party_id, address_type, valid_from);
CREATE INDEX ix_contact_point_1 ON party.contact_point (tenant_id, party_id);
CREATE INDEX ix_contact_point_2 ON party.contact_point (tenant_id, value);
CREATE INDEX ix_party_relationship_1 ON party.party_relationship (tenant_id, from_party_id);
CREATE INDEX ix_party_relationship_2 ON party.party_relationship (tenant_id, to_party_id);
CREATE INDEX ix_party_role_1 ON party.party_role (tenant_id, party_id, role_code);
CREATE INDEX ix_party_role_2 ON party.party_role (tenant_id, context_type, context_id);
CREATE INDEX ix_consent_record_1 ON party.consent_record (tenant_id, party_id, purpose_code, captured_at DESC);
CREATE UNIQUE INDEX uq_duplicate_candidate_1 ON party.duplicate_candidate (tenant_id, party_a_id, party_b_id);
CREATE INDEX ix_duplicate_candidate_2 ON party.duplicate_candidate (tenant_id, status);
CREATE INDEX ix_simulation_run_1 ON product.simulation_run (tenant_id, product_code, product_version);
CREATE INDEX ix_simulation_result_1 ON product.simulation_result (tenant_id, run_id);
CREATE INDEX ix_rating_request_1 ON rating.rating_request (tenant_id, subject_type, subject_id);
CREATE INDEX ix_rating_request_2 ON rating.rating_request (tenant_id, calculated_at);
CREATE INDEX ix_rating_result_line_1 ON rating.rating_result_line (tenant_id, request_id);
CREATE INDEX ix_decision_log_1 ON rules.decision_log (tenant_id, subject_type, subject_id);
CREATE INDEX ix_process_instance_1 ON workflow.process_instance (tenant_id, business_ref_type, business_ref_id);
CREATE INDEX ix_process_instance_2 ON workflow.process_instance (tenant_id, state);
CREATE INDEX ix_task_1 ON workflow.task (tenant_id, queue_code, status, due_at);
CREATE INDEX ix_task_2 ON workflow.task (tenant_id, assignee_id, status);
CREATE INDEX ix_approval_1 ON workflow.approval (tenant_id, business_ref_type, business_ref_id);
CREATE INDEX ix_timer_1 ON workflow.timer (tenant_id, fire_at) WHERE fired_at IS NULL;
CREATE INDEX ix_case_note_1 ON workflow.case_note (tenant_id, business_ref_type, business_ref_id, created_at);
CREATE INDEX ix_document_1 ON document.document (tenant_id, business_ref_type, business_ref_id);
CREATE INDEX ix_document_2 ON document.document (tenant_id, retention_until) WHERE legal_hold = false;
CREATE INDEX ix_document_access_log_1 ON document.document_access_log (tenant_id, document_id, accessed_at);
CREATE INDEX ix_generation_request_1 ON document.generation_request (tenant_id, status);
CREATE INDEX ix_delivery_1 ON document.delivery (tenant_id, document_id);
CREATE INDEX ix_delivery_2 ON document.delivery (tenant_id, status);
CREATE INDEX ix_legal_hold_1 ON document.legal_hold (tenant_id, scope_type, scope_id);
CREATE INDEX ix_billing_account_1 ON billing.billing_account (tenant_id, payer_party_id);
CREATE INDEX ix_premium_schedule_1 ON billing.premium_schedule (tenant_id, source_domain, policy_ref);
CREATE INDEX ix_premium_instalment_1 ON billing.premium_instalment (tenant_id, schedule_id, due_date);
CREATE INDEX ix_premium_instalment_2 ON billing.premium_instalment (tenant_id, status, due_date);
CREATE INDEX ix_invoice_1 ON billing.invoice (tenant_id, billing_account_id, due_date);
CREATE INDEX ix_invoice_2 ON billing.invoice (tenant_id, source_domain, policy_ref);
CREATE INDEX ix_invoice_line_1 ON billing.invoice_line (tenant_id, invoice_id);
CREATE INDEX ix_adjustment_1 ON billing.adjustment (tenant_id, invoice_id);
CREATE INDEX ix_arrears_case_1 ON billing.arrears_case (tenant_id, status, bucket_code);
CREATE INDEX ix_refund_request_1 ON billing.refund_request (tenant_id, status);
CREATE UNIQUE INDEX uq_payment_1 ON payment.payment (tenant_id, idempotency_key);
CREATE INDEX ix_payment_2 ON payment.payment (tenant_id, provider_code, external_ref);
CREATE INDEX ix_payment_3 ON payment.payment (tenant_id, status);
CREATE INDEX ix_payment_status_history_1 ON payment.payment_status_history (tenant_id, payment_id, changed_at);
CREATE INDEX ix_payment_allocation_1 ON payment.payment_allocation (tenant_id, payment_id);
CREATE INDEX ix_payment_allocation_2 ON payment.payment_allocation (tenant_id, invoice_id);
CREATE UNIQUE INDEX uq_provider_callback_1 ON payment.provider_callback (tenant_id, provider_code, external_ref, received_at);
CREATE INDEX ix_bank_statement_line_1 ON payment.bank_statement_line (tenant_id, match_status, statement_date);
CREATE INDEX ix_reconciliation_exception_1 ON payment.reconciliation_exception (tenant_id, status);
CREATE INDEX ix_payout_batch_1 ON payment.payout_batch (tenant_id, status);
CREATE INDEX ix_claim_register_1 ON claims.claim_register (tenant_id, domain, domain_claim_id);
CREATE INDEX ix_claim_register_2 ON claims.claim_register (tenant_id, policy_ref);
CREATE INDEX ix_claim_evidence_1 ON claims.claim_evidence (tenant_id, claim_number);
CREATE INDEX ix_claim_communication_1 ON claims.claim_communication (tenant_id, claim_number, sent_at);
CREATE INDEX ix_fraud_indicator_hit_1 ON claims.fraud_indicator_hit (tenant_id, claim_number);
CREATE INDEX ix_claim_payment_instruction_1 ON claims.claim_payment_instruction (tenant_id, claim_number);
CREATE INDEX ix_claim_payment_instruction_2 ON claims.claim_payment_instruction (tenant_id, status);
CREATE INDEX ix_claim_financial_movement_1 ON claims.claim_financial_movement (tenant_id, claim_number, movement_at);
CREATE INDEX ix_proposal_1 ON life.proposal (tenant_id, status);
CREATE INDEX ix_proposal_2 ON life.proposal (tenant_id, applicant_party_id);
CREATE INDEX ix_proposal_insured_life_1 ON life.proposal_insured_life (tenant_id, proposal_id);
CREATE INDEX ix_proposal_cover_1 ON life.proposal_cover (tenant_id, proposal_id);
CREATE INDEX ix_disclosure_answer_1 ON life.disclosure_answer (tenant_id, proposal_id);
CREATE UNIQUE INDEX uq_underwriting_case_1 ON life.underwriting_case (tenant_id, proposal_id);
CREATE INDEX ix_underwriting_case_2 ON life.underwriting_case (tenant_id, status, assigned_to);
CREATE INDEX ix_underwriting_requirement_1 ON life.underwriting_requirement (tenant_id, case_id);
CREATE INDEX ix_underwriting_decision_item_1 ON life.underwriting_decision_item (tenant_id, case_id);
CREATE INDEX ix_policy_1 ON life.policy (tenant_id, policyholder_party_id);
CREATE INDEX ix_policy_2 ON life.policy (tenant_id, status);
CREATE INDEX ix_policy_insured_life_1 ON life.policy_insured_life (tenant_id, policy_id);
CREATE INDEX ix_policy_benefit_1 ON life.policy_benefit (tenant_id, policy_id);
CREATE INDEX ix_policy_event_1 ON life.policy_event (tenant_id, policy_id, effective_date);
CREATE INDEX ix_beneficiary_1 ON life.beneficiary (tenant_id, policy_id, valid_from);
CREATE UNIQUE INDEX uq_premium_schedule_1 ON life.premium_schedule (tenant_id, policy_id);
CREATE INDEX ix_lapse_reinstatement_1 ON life.lapse_reinstatement (tenant_id, policy_id);
CREATE UNIQUE INDEX uq_cash_value_1 ON life.cash_value (tenant_id, policy_id, valuation_date);
CREATE INDEX ix_policy_loan_1 ON life.policy_loan (tenant_id, policy_id);
CREATE INDEX ix_policy_loan_transaction_1 ON life.policy_loan_transaction (tenant_id, loan_id, txn_date);
CREATE INDEX ix_claim_1 ON life.claim (tenant_id, policy_id);
CREATE INDEX ix_claim_2 ON life.claim (tenant_id, status);
CREATE INDEX ix_claim_benefit_line_1 ON life.claim_benefit_line (tenant_id, claim_id);
CREATE INDEX ix_maturity_case_1 ON life.maturity_case (tenant_id, maturity_date, status);
CREATE INDEX ix_group_member_1 ON life.group_member (tenant_id, scheme_id);
CREATE UNIQUE INDEX uq_quote_1 ON general.quote (tenant_id, quote_number, quote_version);
CREATE INDEX ix_quote_2 ON general.quote (tenant_id, status, valid_until);
CREATE UNIQUE INDEX uq_proposal_1 ON general.proposal (tenant_id, quote_id);
CREATE INDEX ix_underwriting_referral_1 ON general.underwriting_referral (tenant_id, status);
CREATE INDEX ix_risk_object_1 ON general.risk_object (tenant_id, object_type);
CREATE INDEX ix_risk_object_2 ON general.risk_object USING gin (attributes jsonb_path_ops);
CREATE INDEX ix_risk_object_3 ON general.risk_object (tenant_id, risk_zone);
CREATE INDEX ix_policy_1 ON general.policy (tenant_id, policyholder_party_id);
CREATE INDEX ix_policy_2 ON general.policy (tenant_id, status, period_end);
CREATE INDEX ix_policy_risk_1 ON general.policy_risk (tenant_id, policy_id);
CREATE INDEX ix_policy_risk_2 ON general.policy_risk (tenant_id, risk_object_id);
CREATE INDEX ix_coverage_1 ON general.coverage (tenant_id, policy_risk_id);
CREATE INDEX ix_endorsement_1 ON general.endorsement (tenant_id, policy_id, effective_date);
CREATE UNIQUE INDEX uq_cancellation_1 ON general.cancellation (tenant_id, policy_id);
CREATE INDEX ix_claim_1 ON general.claim (tenant_id, policy_id);
CREATE INDEX ix_claim_2 ON general.claim (tenant_id, status);
CREATE INDEX ix_claim_3 ON general.claim (tenant_id, cat_event_code);
CREATE INDEX ix_claim_party_1 ON general.claim_party (tenant_id, claim_id);
CREATE INDEX ix_loss_assessment_1 ON general.loss_assessment (tenant_id, claim_id);
CREATE INDEX ix_repair_estimate_1 ON general.repair_estimate (tenant_id, claim_id);
CREATE INDEX ix_recovery_case_1 ON general.recovery_case (tenant_id, claim_id);
CREATE INDEX ix_recovery_case_2 ON general.recovery_case (tenant_id, status);
CREATE INDEX ix_salvage_item_1 ON general.salvage_item (tenant_id, recovery_case_id);
CREATE INDEX ix_membership_1 ON health.membership (tenant_id, principal_party_id);
CREATE INDEX ix_membership_2 ON health.membership (tenant_id, scheme_id);
CREATE UNIQUE INDEX uq_member_dependant_1 ON health.member_dependant (tenant_id, membership_id, suffix);
CREATE INDEX ix_membership_event_1 ON health.membership_event (tenant_id, membership_id, effective_date);
CREATE UNIQUE INDEX uq_benefit_accumulator_1 ON health.benefit_accumulator (tenant_id, membership_id, beneficiary_party_id, benefit_category, period_start);
CREATE INDEX ix_eligibility_decision_1 ON health.eligibility_decision (tenant_id, member_number, decided_at);
CREATE INDEX ix_preauthorisation_1 ON health.preauthorisation (tenant_id, membership_id);
CREATE INDEX ix_preauthorisation_2 ON health.preauthorisation (tenant_id, provider_id, status);
CREATE INDEX ix_preauth_line_1 ON health.preauth_line (tenant_id, preauth_id);
CREATE INDEX ix_medical_claim_1 ON health.medical_claim (tenant_id, membership_id, service_date);
CREATE INDEX ix_medical_claim_2 ON health.medical_claim (tenant_id, provider_id, submitted_at);
CREATE INDEX ix_medical_claim_3 ON health.medical_claim (tenant_id, status);
CREATE INDEX ix_medical_claim_line_1 ON health.medical_claim_line (tenant_id, claim_id);
CREATE INDEX ix_medical_claim_line_2 ON health.medical_claim_line (tenant_id, tariff_code);
CREATE INDEX ix_utilisation_case_1 ON health.utilisation_case (tenant_id, status);
CREATE INDEX ix_provider_payment_run_1 ON health.provider_payment_run (tenant_id, status);
CREATE INDEX ix_remittance_advice_1 ON health.remittance_advice (tenant_id, run_id);
CREATE INDEX ix_remittance_advice_2 ON health.remittance_advice (tenant_id, provider_id);
CREATE INDEX ix_fraud_case_1 ON health.fraud_case (tenant_id, status);
CREATE INDEX ix_journal_1 ON finance.journal (tenant_id, source_event_id);
CREATE INDEX ix_journal_2 ON finance.journal (tenant_id, period_code);
CREATE INDEX ix_subledger_entry_1 ON finance.subledger_entry (tenant_id, account_code, accounting_date);
CREATE INDEX ix_subledger_entry_2 ON finance.subledger_entry (tenant_id, business_ref_type, business_ref_id);
CREATE INDEX ix_posting_exception_1 ON finance.posting_exception (tenant_id, status);
CREATE INDEX ix_gl_export_batch_1 ON finance.gl_export_batch (tenant_id, period_code);
CREATE INDEX ix_reconciliation_break_1 ON finance.reconciliation_break (tenant_id, run_id);
CREATE INDEX ix_reconciliation_break_2 ON finance.reconciliation_break (tenant_id, status);
CREATE INDEX ix_commission_entitlement_1 ON commission.commission_entitlement (tenant_id, intermediary_code, status);
CREATE INDEX ix_commission_entitlement_2 ON commission.commission_entitlement (tenant_id, source_type, source_id);
CREATE INDEX ix_clawback_1 ON commission.clawback (tenant_id, entitlement_id);
CREATE UNIQUE INDEX uq_commission_statement_1 ON commission.commission_statement (tenant_id, intermediary_code, period_code);
CREATE INDEX ix_cession_1 ON reinsurance.cession (tenant_id, treaty_code, period_start);
CREATE INDEX ix_cession_2 ON reinsurance.cession (tenant_id, source_type, source_id);
CREATE INDEX ix_recovery_1 ON reinsurance.recovery (tenant_id, claim_number);
CREATE INDEX ix_recovery_2 ON reinsurance.recovery (tenant_id, status);
CREATE UNIQUE INDEX uq_bordereau_1 ON reinsurance.bordereau (tenant_id, treaty_code, period_code);
CREATE UNIQUE INDEX uq_regulatory_return_run_1 ON reporting.regulatory_return_run (tenant_id, return_code, period_code);
CREATE INDEX ix_report_run_1 ON reporting.report_run (tenant_id, report_code, created_at);
CREATE INDEX ix_user_account_1 ON iam.user_account (tenant_id, idp_code, external_subject);
CREATE INDEX ix_user_role_assignment_1 ON iam.user_role_assignment (tenant_id, user_id);
CREATE INDEX ix_user_role_assignment_2 ON iam.user_role_assignment (tenant_id, role_code);
CREATE INDEX ix_session_1 ON iam.session (tenant_id, user_id, expires_at);
CREATE INDEX ix_privileged_access_grant_1 ON iam.privileged_access_grant (tenant_id, valid_to);
CREATE INDEX ix_audit_event_1 ON audit.audit_event (tenant_id, target_type, target_id);
CREATE INDEX ix_audit_event_2 ON audit.audit_event (tenant_id, actor_id, occurred_at);

-- Row-level security and partitions
ALTER TABLE integration.idempotency_key ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.idempotency_key FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.idempotency_key
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE integration.inbox_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.inbox_message FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.inbox_message
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE integration.webhook_delivery ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.webhook_delivery FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.webhook_delivery
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE integration.dead_letter_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.dead_letter_message FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.dead_letter_message
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE integration.external_call_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.external_call_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.external_call_log
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE integration.external_call_log_2027_01 PARTITION OF integration.external_call_log FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE integration.external_call_log_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.external_call_log_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.external_call_log_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE integration.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE sdm.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE product.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE product.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON product.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE rating.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE rating.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rating.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE rules.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE rules.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rules.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE commission.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON commission.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reinsurance.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE reinsurance.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reinsurance.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reporting.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reporting.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE audit.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE sdm.change_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.change_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.change_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE sdm.import_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.import_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.import_batch
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE sdm.import_error ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.import_error FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.import_error
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE sdm.config_package ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.config_package FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.config_package
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.party ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.individual ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.individual FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.individual
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.organisation ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.organisation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.organisation
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.party_identifier ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party_identifier FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party_identifier
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.address ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.address FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.address
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.contact_point ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.contact_point FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.contact_point
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.party_relationship ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party_relationship FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party_relationship
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.party_role ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party_role FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party_role
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.consent_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.consent_record FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.consent_record
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.duplicate_candidate ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.duplicate_candidate FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.duplicate_candidate
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE party.merge_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.merge_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.merge_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE product.simulation_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE product.simulation_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON product.simulation_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE product.simulation_result ENABLE ROW LEVEL SECURITY;
ALTER TABLE product.simulation_result FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON product.simulation_result
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE rating.rating_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE rating.rating_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rating.rating_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE rating.rating_request_2027_01 PARTITION OF rating.rating_request FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE rating.rating_request_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE rating.rating_request_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rating.rating_request_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE rating.rating_result_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE rating.rating_result_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rating.rating_result_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE rules.decision_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE rules.decision_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rules.decision_log
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE rules.decision_log_2027_01 PARTITION OF rules.decision_log FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE rules.decision_log_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE rules.decision_log_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON rules.decision_log_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.process_instance ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.process_instance FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.process_instance
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.task ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.task FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.task
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.approval ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.approval FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.approval
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.timer ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.timer FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.timer
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.case_note ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_note FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_note
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.document ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.document FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.document
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.document_access_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.document_access_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.document_access_log
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE document.document_access_log_2027_01 PARTITION OF document.document_access_log FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE document.document_access_log_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.document_access_log_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.document_access_log_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.generation_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.generation_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.generation_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.delivery ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.delivery FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.delivery
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE document.legal_hold ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.legal_hold FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.legal_hold
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.billing_account ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.billing_account FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.billing_account
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.premium_schedule ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.premium_schedule FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.premium_schedule
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.premium_instalment ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.premium_instalment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.premium_instalment
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.invoice ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.invoice FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.invoice
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.invoice_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.invoice_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.invoice_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.adjustment ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.adjustment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.adjustment
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.arrears_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.arrears_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.arrears_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE billing.refund_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.refund_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.refund_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.payment ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.payment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.payment
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.payment_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.payment_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.payment_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.payment_allocation ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.payment_allocation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.payment_allocation
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.provider_callback ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.provider_callback FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.provider_callback
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.bank_statement_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.bank_statement_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.bank_statement_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.reconciliation_exception ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.reconciliation_exception FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.reconciliation_exception
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE payment.payout_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.payout_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.payout_batch
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.claim_register ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_register FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.claim_register
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.claim_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.claim_evidence
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.claim_communication ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_communication FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.claim_communication
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.fraud_indicator_hit ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.fraud_indicator_hit FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.fraud_indicator_hit
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.claim_payment_instruction ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_payment_instruction FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.claim_payment_instruction
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE claims.claim_financial_movement ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_financial_movement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON claims.claim_financial_movement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.proposal ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.proposal FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.proposal
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.proposal_insured_life ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.proposal_insured_life FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.proposal_insured_life
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.proposal_cover ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.proposal_cover FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.proposal_cover
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.disclosure_answer ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.disclosure_answer FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.disclosure_answer
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.underwriting_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.underwriting_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.underwriting_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.underwriting_requirement ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.underwriting_requirement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.underwriting_requirement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.underwriting_decision_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.underwriting_decision_item FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.underwriting_decision_item
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy_insured_life ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_insured_life FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_insured_life
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy_benefit ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_benefit FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_benefit
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.beneficiary ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.beneficiary FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.beneficiary
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.premium_schedule ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.premium_schedule FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.premium_schedule
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.lapse_reinstatement ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.lapse_reinstatement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.lapse_reinstatement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.cash_value ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.cash_value FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.cash_value
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy_loan ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_loan FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_loan
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.policy_loan_transaction ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.policy_loan_transaction FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.policy_loan_transaction
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.claim ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.claim FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.claim
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.claim_benefit_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.claim_benefit_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.claim_benefit_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.maturity_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.maturity_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.maturity_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.group_scheme ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.group_scheme FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.group_scheme
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.group_member ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.group_member FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.group_member
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE life.valuation_extract_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.valuation_extract_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.valuation_extract_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.quote ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.quote FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.quote
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.proposal ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.proposal FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.proposal
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.underwriting_referral ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.underwriting_referral FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.underwriting_referral
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.risk_object ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.risk_object FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.risk_object
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.policy ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.policy FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.policy
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.policy_risk ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.policy_risk FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.policy_risk
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.coverage ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.coverage FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.coverage
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.endorsement ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.endorsement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.endorsement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.cancellation ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cancellation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cancellation
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.claim ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.claim FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.claim
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.claim_party ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.claim_party FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.claim_party
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.loss_assessment ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.loss_assessment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.loss_assessment
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.repair_estimate ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.repair_estimate FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.repair_estimate
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.recovery_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.recovery_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.recovery_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.salvage_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.salvage_item FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.salvage_item
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.exposure_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.exposure_snapshot FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.exposure_snapshot
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.scheme ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.scheme FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.scheme
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.membership ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.membership FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.membership
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.member_dependant ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.member_dependant FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.member_dependant
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.membership_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.membership_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.membership_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.benefit_accumulator ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.benefit_accumulator FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.benefit_accumulator
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.eligibility_decision ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.eligibility_decision FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.eligibility_decision
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE health.eligibility_decision_2027_01 PARTITION OF health.eligibility_decision FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE health.eligibility_decision_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.eligibility_decision_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.eligibility_decision_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.preauthorisation ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.preauthorisation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.preauthorisation
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.preauth_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.preauth_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.preauth_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.medical_claim ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.medical_claim FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.medical_claim
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.medical_claim_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.medical_claim_line FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.medical_claim_line
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.utilisation_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.utilisation_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.utilisation_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.provider_payment_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.provider_payment_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.provider_payment_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.remittance_advice ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.remittance_advice FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.remittance_advice
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE health.fraud_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.fraud_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.fraud_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.journal ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.journal FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.journal
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE finance.journal_2027_01 PARTITION OF finance.journal FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE finance.journal_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.journal_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.journal_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.subledger_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.subledger_entry FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.subledger_entry
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE finance.subledger_entry_2027_01 PARTITION OF finance.subledger_entry FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE finance.subledger_entry_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.subledger_entry_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.subledger_entry_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.posting_exception ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.posting_exception FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.posting_exception
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.gl_export_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.gl_export_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.gl_export_batch
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.reconciliation_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.reconciliation_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.reconciliation_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE finance.reconciliation_break ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.reconciliation_break FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.reconciliation_break
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE commission.commission_entitlement ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.commission_entitlement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON commission.commission_entitlement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE commission.clawback ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.clawback FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON commission.clawback
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE commission.commission_statement ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.commission_statement FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON commission.commission_statement
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reinsurance.cession ENABLE ROW LEVEL SECURITY;
ALTER TABLE reinsurance.cession FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reinsurance.cession
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reinsurance.recovery ENABLE ROW LEVEL SECURITY;
ALTER TABLE reinsurance.recovery FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reinsurance.recovery
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reinsurance.bordereau ENABLE ROW LEVEL SECURITY;
ALTER TABLE reinsurance.bordereau FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reinsurance.bordereau
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reporting.regulatory_return_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.regulatory_return_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reporting.regulatory_return_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE reporting.report_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON reporting.report_run
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE analytics.fact_written_premium ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.fact_written_premium FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON analytics.fact_written_premium
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE analytics.fact_claims_incurred ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.fact_claims_incurred FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON analytics.fact_claims_incurred
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE tenant.onboarding_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant.onboarding_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant.onboarding_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE tenant.tenant_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant.tenant_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant.tenant_status_history
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.user_account ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.user_account FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.user_account
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.user_role_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.user_role_assignment FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.user_role_assignment
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.session ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.session FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.session
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.privileged_access_grant ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.privileged_access_grant FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.privileged_access_grant
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE iam.access_review ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.access_review FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.access_review
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE audit.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit.audit_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit.audit_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Example monthly partition. A scheduled job creates partitions ahead of time.
-- Each partition gets the same policy, because querying a partition directly does not apply the parent's policy.
CREATE TABLE audit.audit_event_2027_01 PARTITION OF audit.audit_event FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');
ALTER TABLE audit.audit_event_2027_01 ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit.audit_event_2027_01 FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit.audit_event_2027_01
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- Notes
-- subscription_id references ref webhook subscription: checked by the owning service, no cross-schema foreign key (DBA-005)
-- resolved_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- maker_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- checker_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- submitted_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- approved_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- status_code references ref_customer_status: checked by the owning service, no cross-schema foreign key (DBA-005)
-- reviewed_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- merged_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- started_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- request_id references partitioned rating.rating_request: no foreign key because this table does not carry calculated_at; integrity is checked by the owning service
-- assignee_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- maker_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- checker_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- author_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- accessor_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- placed_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- payer_party_id references party.party: checked by the owning service, no cross-schema foreign key (DBA-005)
-- approved_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- payment_id references payment.payment: checked by the owning service, no cross-schema foreign key (DBA-005)
-- invoice_id references billing.invoice: checked by the owning service, no cross-schema foreign key (DBA-005)
-- assigned_to references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- released_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- document_id references document.document: checked by the owning service, no cross-schema foreign key (DBA-005)
-- document_id references document.document: checked by the owning service, no cross-schema foreign key (DBA-005)
-- approval_id references workflow.approval: checked by the owning service, no cross-schema foreign key (DBA-005)
-- payment_id references payment.payment: checked by the owning service, no cross-schema foreign key (DBA-005)
-- subledger_journal_id references finance.journal: checked by the owning service, no cross-schema foreign key (DBA-005)
-- assigned_to references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- recorded_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- decided_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- clinical_reviewer references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- reviewer_id references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- resolved_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- approved_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- requested_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)
-- changed_by references iam.user_account: checked by the owning service, no cross-schema foreign key (DBA-005)