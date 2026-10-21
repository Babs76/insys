-- =====================================================================
-- Grants for the module roles (OQ-004 resolved, DBA-002, DBA-004).
-- Applied in every environment after migrations, by the owner.
--
-- * Each module role has USAGE and DML on its own schemas only. The module and
--   schema list is db/roles/module_roles.csv.
-- * Insert-only tables get SELECT and INSERT only (DBA-009, DBA-016).
-- * insys_api (the API building block) may use only integration.idempotency_key.
-- * insys_platform_admin may maintain static data tables and write module
--   outboxes, for global static data changes only.
-- * Default privileges give partitions created later by the owner (the Audit
--   module's partition job, through SECURITY DEFINER functions) the same grants.
-- =====================================================================
DO $$
DECLARE
  m record;
  s text;
  t record;
BEGIN
  FOR m IN SELECT * FROM (VALUES
    ('insys_integration', ARRAY['integration', 'channel']),
    ('insys_sdm',         ARRAY['sdm', 'refdata', 'ops']),
    ('insys_party',       ARRAY['party']),
    ('insys_product',     ARRAY['product']),
    ('insys_rating',      ARRAY['rating']),
    ('insys_rules',       ARRAY['rules']),
    ('insys_workflow',    ARRAY['workflow']),
    ('insys_document',    ARRAY['document']),
    ('insys_billing',     ARRAY['billing']),
    ('insys_payment',     ARRAY['payment']),
    ('insys_claims',      ARRAY['claims']),
    ('insys_life',        ARRAY['life']),
    ('insys_general',     ARRAY['general']),
    ('insys_health',      ARRAY['health']),
    ('insys_finance',     ARRAY['finance']),
    ('insys_commission',  ARRAY['commission']),
    ('insys_reinsurance', ARRAY['reinsurance']),
    ('insys_reporting',   ARRAY['reporting']),
    ('insys_analytics',   ARRAY['analytics']),
    ('insys_tenant',      ARRAY['tenant']),
    ('insys_iam',         ARRAY['iam']),
    ('insys_audit',       ARRAY['audit'])
  ) AS v(role_name, schemas)
  LOOP
    FOREACH s IN ARRAY m.schemas LOOP
      EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', s, m.role_name);
      EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', s, m.role_name);
      EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', s, m.role_name);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', s, m.role_name);
    END LOOP;
  END LOOP;

  -- Insert-only: audit events (DBA-016). Finance journal tables join this list in Phase 3 (DBA-009).
  FOR t IN
    SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'audit' AND c.relname LIKE 'audit\_event%' AND c.relkind IN ('r', 'p')
  LOOP
    EXECUTE format('REVOKE UPDATE, DELETE ON %I.%I FROM insys_audit', t.nspname, t.relname);
  END LOOP;
  ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE UPDATE, DELETE ON TABLES FROM insys_audit;

  -- Partition maintenance through owner-defined functions only (V0005).
  GRANT EXECUTE ON FUNCTION audit.ensure_monthly_partitions(integer) TO insys_audit;
  GRANT EXECUTE ON FUNCTION audit.partition_horizon() TO insys_audit;

  -- The API building block: idempotency keys only (INT-003, DBA-008).
  GRANT USAGE ON SCHEMA integration TO insys_api;
  GRANT SELECT, INSERT, UPDATE, DELETE ON integration.idempotency_key TO insys_api;

  -- Platform administrators: static data tables (ref_, cfg_, mst_) and the outboxes their changes publish to.
  -- Row-level security still limits them to global rows (tenant_id IS NULL) through the V0002 platform policies.
  FOR t IN
    SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relkind = 'r' AND (c.relname ~ '^(ref|cfg|mst)_' OR c.relname = 'outbox_event')
      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO insys_platform_admin', t.nspname);
    IF t.relname = 'outbox_event' THEN
      EXECUTE format('GRANT INSERT ON %I.%I TO insys_platform_admin', t.nspname, t.relname);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %I.%I TO insys_platform_admin', t.nspname, t.relname);
    END IF;
  END LOOP;
END $$;
