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
-- V0050: aggregation alerts are recorded so they can be listed (GAP-051, F062 §1, Wave 1 batch 1).
-- One row per zone, peril, currency and day the daily exposure snapshot reaches the limit's alert level; the unique key
-- keeps the alert to once a day however often the snapshot runs. Each alert is announced
-- (general.AggregationLimitApproached), and Workflow keeps one open review task per zone and peril.
-- - alert_at_pct: the alert level that applied, in per cent of max_exposure (the limit's own, or the platform default).
-- =====================================================================
CREATE TABLE general.aggregation_alert (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  zone                       varchar(15) NOT NULL,
  peril_code                 varchar(10) NOT NULL,
  currency                   char(3) NOT NULL,
  exposure                   numeric(20,2) NOT NULL,
  max_exposure               numeric(20,2) NOT NULL,
  alert_at_pct               numeric(5,2) NOT NULL,
  as_at                      date NOT NULL,
  raised_at                  timestamptz NOT NULL,
  CONSTRAINT pk_aggregation_alert PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_aggregation_alert_day UNIQUE (tenant_id, zone, peril_code, currency, as_at),
  CONSTRAINT ck_aggregation_alert_level CHECK (alert_at_pct > 0 AND alert_at_pct <= 100 AND max_exposure > 0)
);
CREATE INDEX ix_aggregation_alert_zone ON general.aggregation_alert (tenant_id, zone, peril_code, as_at);
ALTER TABLE general.aggregation_alert ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.aggregation_alert FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.aggregation_alert
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE general.aggregation_alert IS 'A zone and peril at or above its alert level on a day (GAP-051, F062 §1).';
