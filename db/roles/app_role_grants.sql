-- =====================================================================
-- Grants for the application role in local, test and CI databases.
-- Not a migration: grants are environment-specific (V0001 header).
-- {{APP_ROLE}} is replaced by the migration runner (--app-role).
--
-- The role is a non-owner without BYPASSRLS (DBA-002), so row-level security
-- applies. It is one role for the whole modular monolith; per-module roles
-- with write access only to their own schema (DBA-004) are OQ-004.
-- Insert-only tables (DBA-009, DBA-016) get no UPDATE or DELETE.
-- =====================================================================
DO $$
DECLARE
  s text;
  t text;
BEGIN
  FOR s IN
    SELECT nspname FROM pg_namespace
    WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'public')
  LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO {{APP_ROLE}}', s);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO {{APP_ROLE}}', s);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO {{APP_ROLE}}', s);
  END LOOP;

  -- Insert-only: audit (DBA-016). Finance journal tables are added in Phase 3 (DBA-009).
  FOR t IN
    SELECT n.nspname || '.' || c.relname
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'audit' AND c.relname LIKE 'audit\_event%' AND c.relkind IN ('r', 'p')
  LOOP
    EXECUTE format('REVOKE UPDATE, DELETE ON %s FROM {{APP_ROLE}}', t);
  END LOOP;
END $$;
