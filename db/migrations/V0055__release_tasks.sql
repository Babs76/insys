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
-- V0055: release tasks (Wave 2 batch 1). A task a release must run once per tenant, recorded so it never runs twice.
-- The first is Analytics' rebuild by business date (ADR-025, Wave 1 batch 4): facts written before then were dated by
-- the UTC day of the event. The host runs pending tasks for each tenant after it starts; the row is written in the
-- same transaction as the task's work, so a task that fails runs again at the next start, and one that succeeded never does.
-- =====================================================================
CREATE TABLE analytics.release_task (
  tenant_id                  uuid NOT NULL,
  task_code                  varchar(60) NOT NULL,
  completed_at               timestamptz NOT NULL,
  detail                     varchar(200),
  CONSTRAINT pk_release_task PRIMARY KEY (tenant_id, task_code)
);
ALTER TABLE analytics.release_task ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.release_task FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON analytics.release_task
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE analytics.release_task IS 'Release tasks run once per tenant (Wave 2 batch 1): ANALYTICS_REBUILD_BY_BUSINESS_DATE.';
