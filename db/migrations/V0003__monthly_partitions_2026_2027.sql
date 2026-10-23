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
-- INSYS Core · Monthly partitions for 2026 and 2027 (DBA-018, DBA-023)
-- V0001 creates one example partition (January 2027) per partitioned table.
-- Rows dated before it, including audit events written today, have no
-- partition and are rejected. This migration creates the monthly partitions
-- for January 2026 to December 2027 that do not exist yet, each with the
-- same row-level security policy as its parent. No default partition is
-- created: DBA-023 requires a row with no matching partition to be rejected.
-- Later partitions are created ahead of need by a maintenance job
-- (see docs/decisions/open-questions.md, OQ-005).
-- =====================================================================
DO $$
DECLARE
  parent text;
  month_start date;
  partition_name text;
BEGIN
  FOREACH parent IN ARRAY ARRAY[
    'integration.external_call_log',
    'rating.rating_request',
    'rules.decision_log',
    'document.document_access_log',
    'health.eligibility_decision',
    'finance.journal',
    'finance.subledger_entry',
    'audit.audit_event'
  ] LOOP
    month_start := DATE '2026-01-01';
    WHILE month_start < DATE '2028-01-01' LOOP
      partition_name := parent || '_' || to_char(month_start, 'YYYY_MM');
      IF to_regclass(partition_name) IS NULL THEN
        EXECUTE format('CREATE TABLE %s PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
                       partition_name, parent, month_start, (month_start + INTERVAL '1 month')::date);
        EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', partition_name);
        EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', partition_name);
        EXECUTE format($p$CREATE POLICY tenant_isolation ON %s
                          USING (tenant_id = current_setting('app.tenant_id')::uuid)
                          WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, partition_name);
      END IF;
      month_start := (month_start + INTERVAL '1 month')::date;
    END LOOP;
  END LOOP;
END $$;
