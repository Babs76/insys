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
-- V0089: the ledger's partitions cover every open accounting period (Ade, 29 October 2026). finance.journal and
-- finance.subledger_entry are partitioned on accounting_date, a business date. Postings take the business date they
-- happened on, but a reversal or a retried posting names its own date in any open period, and a period may be opened
-- any distance ahead, while partition maintenance (V0005) creates only a few months beyond today. Opening a period
-- therefore creates its months' partitions first, through this owner-defined function, as maintenance does.
-- =====================================================================
CREATE OR REPLACE FUNCTION finance.ensure_ledger_partitions(p_from date, p_until date)
RETURNS SETOF text
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
SET TimeZone = 'UTC' AS $$
DECLARE
  parent text;
  month_start date;
  last_month date;
  partition_name text;
BEGIN
  IF p_from IS NULL OR p_until IS NULL OR p_until < p_from OR p_until > p_from + INTERVAL '36 months' THEN
    RAISE EXCEPTION 'a ledger partition range is a from and an until date at most 36 months apart';
  END IF;
  last_month := date_trunc('month', p_until)::date;
  FOREACH parent IN ARRAY ARRAY['finance.journal', 'finance.subledger_entry'] LOOP
    month_start := date_trunc('month', p_from)::date;
    WHILE month_start <= last_month LOOP
      partition_name := parent || '_' || to_char(month_start, 'YYYY_MM');
      IF to_regclass(partition_name) IS NULL THEN
        EXECUTE format('CREATE TABLE %s PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
                       partition_name, parent, month_start, (month_start + INTERVAL '1 month')::date);
        EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', partition_name);
        EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', partition_name);
        EXECUTE format($p$CREATE POLICY tenant_isolation ON %s
                          USING (tenant_id = current_setting('app.tenant_id')::uuid)
                          WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, partition_name);
        RETURN NEXT partition_name;
      END IF;
      month_start := (month_start + INTERVAL '1 month')::date;
    END LOOP;
  END LOOP;
END $$;
REVOKE ALL ON FUNCTION finance.ensure_ledger_partitions(date, date) FROM PUBLIC;
