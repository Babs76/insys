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
-- INSYS Core · Inbox per module schema (OQ-002, DBA-007, INT-005)
-- Each consuming module records processed events in its own schema, in the
-- same transaction as the handler's changes, so a module's database role
-- never writes another module's schema (DBA-004). Columns match
-- integration.inbox_message, which stays as the Integration module's inbox.
--
-- V0001 creates no outbox in the tenant and analytics schemas. The Tenancy
-- module publishes TenantSuspended, and both modules publish static data and
-- audit events through their outbox, so tenant.outbox_event and
-- analytics.outbox_event are added here with the same columns as every other
-- outbox.
-- =====================================================================
DO $$
DECLARE
  s text;
BEGIN
  FOREACH s IN ARRAY ARRAY[
    'sdm', 'party', 'product', 'rating', 'rules', 'workflow', 'document', 'billing', 'payment', 'claims',
    'life', 'general', 'health', 'finance', 'commission', 'reinsurance', 'reporting', 'analytics',
    'tenant', 'iam', 'audit'
  ] LOOP
    EXECUTE format($t$
      CREATE TABLE %1$I.inbox_message (
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
      )$t$, s);
    EXECUTE format('CREATE UNIQUE INDEX uq_inbox_message_1 ON %I.inbox_message (tenant_id, consumer, event_id)', s);
    EXECUTE format('ALTER TABLE %I.inbox_message ENABLE ROW LEVEL SECURITY', s);
    EXECUTE format('ALTER TABLE %I.inbox_message FORCE ROW LEVEL SECURITY', s);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON %I.inbox_message
      USING (tenant_id = current_setting('app.tenant_id')::uuid)
      WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, s);
  END LOOP;
END $$;

DO $$
DECLARE
  s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['tenant', 'analytics'] LOOP
    EXECUTE format($t$
      CREATE TABLE %1$I.outbox_event (
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
      )$t$, s);
    EXECUTE format('CREATE INDEX ix_outbox_event_1 ON %I.outbox_event (tenant_id, published_at) WHERE published_at IS NULL', s);
    EXECUTE format('ALTER TABLE %I.outbox_event ENABLE ROW LEVEL SECURITY', s);
    EXECUTE format('ALTER TABLE %I.outbox_event FORCE ROW LEVEL SECURITY', s);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON %I.outbox_event
      USING (tenant_id = current_setting('app.tenant_id')::uuid)
      WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, s);
  END LOOP;
END $$;
