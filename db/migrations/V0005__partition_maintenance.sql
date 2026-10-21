-- =====================================================================
-- INSYS Core · Partition maintenance (OQ-005, DBA-018, DBA-023)
-- Module roles have no DDL rights and never own tables (DBA-002), so the
-- Audit module's scheduled job creates partitions through these functions.
-- They run with the owner's rights (SECURITY DEFINER), accept only the eight
-- partitioned tables, and give every new partition the parent's tenant
-- policy. Month boundaries are in UTC (DBA-013); servers must also run in
-- UTC, because V0003 used the server's time zone (OQ-023). EXECUTE is granted to the Audit module role by the environment's
-- grant script, never to PUBLIC.
-- =====================================================================
CREATE OR REPLACE FUNCTION audit.partitioned_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE AS $$
  SELECT ARRAY[
    'integration.external_call_log',
    'rating.rating_request',
    'rules.decision_log',
    'document.document_access_log',
    'health.eligibility_decision',
    'finance.journal',
    'finance.subledger_entry',
    'audit.audit_event'
  ]
$$;

-- Creates monthly partitions from the current month to p_months_ahead months
-- ahead, for every partitioned table. Returns the partitions it created.
CREATE OR REPLACE FUNCTION audit.ensure_monthly_partitions(p_months_ahead integer)
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
  IF p_months_ahead IS NULL OR p_months_ahead < 1 OR p_months_ahead > 24 THEN
    RAISE EXCEPTION 'p_months_ahead must be between 1 and 24';
  END IF;
  last_month := (date_trunc('month', current_date) + make_interval(months => p_months_ahead))::date;
  FOREACH parent IN ARRAY audit.partitioned_tables() LOOP
    month_start := date_trunc('month', current_date)::date;
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

-- The end of the furthest existing partition for each partitioned table.
CREATE OR REPLACE FUNCTION audit.partition_horizon()
RETURNS TABLE (parent text, covered_until date)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
  SELECT p.parent,
         max(left((regexp_match(pg_get_expr(c.relpartbound, c.oid), 'TO \(''([^'']+)''\)'))[1], 10)::date)
  FROM unnest(audit.partitioned_tables()) AS p(parent)
  LEFT JOIN pg_inherits i ON i.inhparent = p.parent::regclass
  LEFT JOIN pg_class c ON c.oid = i.inhrelid
  GROUP BY p.parent
$$;

REVOKE ALL ON FUNCTION audit.ensure_monthly_partitions(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION audit.partition_horizon() FROM PUBLIC;
