-- =====================================================================
-- INSYS Core · Outbox relay lag for health checks (Phase 1 review, item 6).
-- Returns, per module outbox, the occurred_at of the oldest event still
-- waiting to be published and how many are waiting. Dead-lettered rows
-- (publish_attempts at the limit) are excluded: they are reported through
-- integration.dead_letter_message instead. Outboxes have forced row-level
-- security, so the function reads them one tenant at a time with
-- app.tenant_id set, and needs no BYPASSRLS. It returns only counts and a
-- timestamp, never row data. EXECUTE is granted to the API role.
-- =====================================================================
CREATE OR REPLACE FUNCTION integration.outbox_lag(p_max_attempts smallint)
RETURNS TABLE (outbox text, oldest_pending timestamptz, pending bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  s text;
  t uuid;
  row_oldest timestamptz;
  row_count bigint;
  previous text := current_setting('app.tenant_id', true);
BEGIN
  FOR s IN SELECT schemaname FROM pg_tables WHERE tablename = 'outbox_event' ORDER BY schemaname LOOP
    outbox := s;
    oldest_pending := NULL;
    pending := 0;
    FOR t IN SELECT id FROM tenant.tenant LOOP
      PERFORM set_config('app.tenant_id', t::text, true);
      EXECUTE format('SELECT min(occurred_at), count(*) FROM %I.outbox_event WHERE published_at IS NULL AND publish_attempts < $1', s)
        INTO row_oldest, row_count USING p_max_attempts;
      pending := pending + row_count;
      IF row_oldest IS NOT NULL AND (oldest_pending IS NULL OR row_oldest < oldest_pending) THEN
        oldest_pending := row_oldest;
      END IF;
    END LOOP;
    RETURN NEXT;
  END LOOP;
  PERFORM set_config('app.tenant_id', coalesce(previous, ''), true);
END $$;
REVOKE ALL ON FUNCTION integration.outbox_lag(smallint) FROM PUBLIC;
