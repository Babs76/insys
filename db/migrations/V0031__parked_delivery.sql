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
-- V0031: parked deliveries (ADR-022, accepted 23 October 2026; OQ-072 decided). A consumer whose tenant configuration
-- is missing parks its delivery here instead of failing; later events for the same record to that consumer wait
-- behind it, so order per record is kept; a delivery that is failing also holds its record's later events. Parked
-- deliveries are never dropped automatically: discarding one is a manual, audited action with a reason.
-- parking_setting is the tenant's escalation configuration (ADR-022 decision 6); with no row the defaults apply.
-- =====================================================================
CREATE TABLE integration.parked_delivery (
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
  event_version              smallint NOT NULL,
  occurred_at                timestamptz NOT NULL,
  aggregate_type             varchar(60) NOT NULL,
  aggregate_id               uuid NOT NULL,
  correlation_id             uuid NOT NULL,
  causation_id               uuid,
  payload                    jsonb NOT NULL,
  status                     varchar(14) NOT NULL,
  ledger_owned               boolean NOT NULL,
  prerequisite_code          varchar(60),
  prerequisite_item          varchar(80),
  parked_at                  timestamptz,
  attempts                   integer NOT NULL,
  failures                   integer NOT NULL,
  last_attempt_at            timestamptz,
  next_attempt_at            timestamptz,
  retry_requested_at         timestamptz,
  escalation_level           smallint NOT NULL,
  next_escalation_at         timestamptz,
  escalated_at               timestamptz,
  resolved_at                timestamptz,
  discarded_by               varchar(80),
  discard_reason             varchar(200),
  CONSTRAINT pk_parked_delivery PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_parked_delivery_event UNIQUE (tenant_id, consumer, event_id),
  CONSTRAINT ck_parked_delivery_status CHECK (status IN ('PARKED', 'WAITING', 'FAILING', 'RETRYING', 'DEAD_LETTERED', 'RESOLVED', 'DISCARDED')),
  CONSTRAINT ck_parked_delivery_parked CHECK (status <> 'PARKED' OR (prerequisite_code IS NOT NULL AND parked_at IS NOT NULL)),
  CONSTRAINT ck_parked_delivery_discard CHECK ((status = 'DISCARDED') = (discard_reason IS NOT NULL)),
  CONSTRAINT ck_parked_delivery_level CHECK (escalation_level BETWEEN 0 AND 2),
  CONSTRAINT ck_parked_delivery_attempts CHECK (attempts >= 0 AND failures >= 0)
);
COMMENT ON TABLE integration.parked_delivery IS 'ADR-022: deliveries to one consumer that are parked for missing configuration, waiting behind an earlier delivery for the same record, or failing.';
COMMENT ON COLUMN integration.parked_delivery.ledger_owned IS 'True when this ledger retries a failing delivery; false while the outbox relay still retries the event.';
COMMENT ON COLUMN integration.parked_delivery.failures IS 'Consecutive failures; the ledger dead-letters a delivery it retries after Outbox:MaxAttempts of them. A park resets it.';
COMMENT ON COLUMN integration.parked_delivery.payload IS 'The event payload, which INT-007 keeps free of sensitive data.';
-- The record-key check made before each delivery (ADR-022 decision 3), over open deliveries only.
CREATE INDEX ix_parked_delivery_record ON integration.parked_delivery (tenant_id, aggregate_type, aggregate_id, consumer)
  WHERE status IN ('PARKED', 'WAITING', 'FAILING', 'RETRYING', 'DEAD_LETTERED');
CREATE INDEX ix_parked_delivery_event ON integration.parked_delivery (tenant_id, event_id);
CREATE INDEX ix_parked_delivery_status ON integration.parked_delivery (tenant_id, status, prerequisite_code);
ALTER TABLE integration.parked_delivery ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.parked_delivery FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.parked_delivery
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE integration.parking_setting (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  configuration_owner_role   varchar(40) NOT NULL,
  calendar_code              varchar(30),
  money_first                varchar(20) NOT NULL,
  money_second               varchar(20) NOT NULL,
  other_first                varchar(20) NOT NULL,
  other_second               varchar(20) NOT NULL,
  CONSTRAINT pk_parking_setting PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_parking_setting_tenant UNIQUE (tenant_id)
);
COMMENT ON TABLE integration.parking_setting IS 'ADR-022 decision 6: a tenant''s escalation of parked deliveries. Durations are ISO 8601 in business time (PT4H, P1D).';
ALTER TABLE integration.parking_setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE integration.parking_setting FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON integration.parking_setting
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
