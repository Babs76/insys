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
-- V0093: one time source (Ade, 29 October 2026). Logic that decides by time uses the application's clock, passed in as
-- a parameter, never the database's now() or current_date against times the application wrote.
-- * payment and communication.resolve_callback_endpoint take p_now: a RETIRING endpoint is usable while its
--   retires_at (written from the application clock) is after it.
-- * audit.ensure_monthly_partitions takes p_today: the partitions ahead are those of the application's months, the
--   months its rows are dated in.
-- RecordTimeSourceTests checks that no function, view or policy in force compares with the database's clock again.
-- =====================================================================
DROP FUNCTION payment.resolve_callback_endpoint(char);
CREATE FUNCTION payment.resolve_callback_endpoint(p_key_hash char(64), p_now timestamptz)
RETURNS TABLE (tenant_id uuid, endpoint_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  t uuid;
  previous text := current_setting('app.tenant_id', true);
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    RETURN QUERY
      SELECT e.tenant_id, e.id FROM payment.callback_endpoint e
      WHERE e.key_hash = p_key_hash
        AND (e.status = 'ACTIVE' OR (e.status = 'RETIRING' AND e.retires_at > p_now));
    EXIT WHEN FOUND;
  END LOOP;
  PERFORM set_config('app.tenant_id', coalesce(previous, ''), true);
END $$;
REVOKE ALL ON FUNCTION payment.resolve_callback_endpoint(char, timestamptz) FROM PUBLIC;

DROP FUNCTION communication.resolve_callback_endpoint(char);
CREATE FUNCTION communication.resolve_callback_endpoint(p_key_hash char(64), p_now timestamptz)
RETURNS TABLE (tenant_id uuid, endpoint_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  t uuid;
  previous text := current_setting('app.tenant_id', true);
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    RETURN QUERY
      SELECT e.tenant_id, e.id FROM communication.callback_endpoint e
      WHERE e.key_hash = p_key_hash
        AND (e.status = 'ACTIVE' OR (e.status = 'RETIRING' AND e.retires_at > p_now));
    EXIT WHEN FOUND;
  END LOOP;
  PERFORM set_config('app.tenant_id', coalesce(previous, ''), true);
END $$;
REVOKE ALL ON FUNCTION communication.resolve_callback_endpoint(char, timestamptz) FROM PUBLIC;
-- Grants: db/roles/module_grants.sql gives each module's role EXECUTE on its resolver.

DROP FUNCTION audit.ensure_monthly_partitions(integer);
CREATE FUNCTION audit.ensure_monthly_partitions(p_today date, p_months_ahead integer)
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
  IF p_today IS NULL THEN
    RAISE EXCEPTION 'p_today is the application''s UTC date';
  END IF;
  IF p_months_ahead IS NULL OR p_months_ahead < 1 OR p_months_ahead > 24 THEN
    RAISE EXCEPTION 'p_months_ahead must be between 1 and 24';
  END IF;
  last_month := (date_trunc('month', p_today) + make_interval(months => p_months_ahead))::date;
  FOREACH parent IN ARRAY audit.partitioned_tables() LOOP
    month_start := date_trunc('month', p_today)::date;
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
REVOKE ALL ON FUNCTION audit.ensure_monthly_partitions(date, integer) FROM PUBLIC;
