using Insys.Testing;

namespace Insys.IntegrationTests.TestModule;

/// <summary>
/// Creates the test_mod schema as the owner, following the core schema's conventions (DBA-001, DBA-002, DBA-006,
/// DBA-007), with its own role insys_test_mod that has DML on test_mod only (DBA-004).
/// </summary>
public static class TestModuleSchema
{
    public static Task Create(TestDatabase database) => database.ExecuteAsOwner($"""
        CREATE SCHEMA test_mod;
        CREATE TABLE test_mod.widget (
          tenant_id uuid NOT NULL, id uuid NOT NULL, row_version bigint NOT NULL,
          created_at timestamptz NOT NULL, created_by varchar(80) NOT NULL, updated_at timestamptz NOT NULL, updated_by varchar(80) NOT NULL,
          name varchar(40) NOT NULL,
          CONSTRAINT pk_widget PRIMARY KEY (tenant_id, id));
        CREATE TABLE test_mod.consumption (
          tenant_id uuid NOT NULL, id uuid NOT NULL, event_id uuid NOT NULL, widget_id uuid NOT NULL, actor varchar(80) NOT NULL,
          CONSTRAINT pk_consumption PRIMARY KEY (tenant_id, id));
        CREATE TABLE test_mod.outbox_event (LIKE integration.outbox_event INCLUDING ALL);
        CREATE TABLE test_mod.inbox_message (LIKE integration.inbox_message INCLUDING ALL);
        DO $$
        DECLARE t text;
        BEGIN
          FOREACH t IN ARRAY ARRAY['test_mod.widget', 'test_mod.consumption', 'test_mod.outbox_event', 'test_mod.inbox_message'] LOOP
            EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
            EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
            EXECUTE format($p$CREATE POLICY tenant_isolation ON %s
              USING (tenant_id = current_setting('app.tenant_id')::uuid)
              WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, t);
          END LOOP;
        END $$;
        GRANT USAGE ON SCHEMA test_mod TO {PostgresServer.TestModuleRole};
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA test_mod TO {PostgresServer.TestModuleRole};
        """);
}
