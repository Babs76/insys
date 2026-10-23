/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
        CREATE TABLE test_mod.ledger_consumption (
          tenant_id uuid NOT NULL, seq bigint GENERATED ALWAYS AS IDENTITY, event_id uuid NOT NULL, widget_id uuid NOT NULL, what varchar(40) NOT NULL,
          CONSTRAINT pk_ledger_consumption PRIMARY KEY (tenant_id, seq));
        CREATE TABLE test_mod.outbox_event (LIKE integration.outbox_event INCLUDING ALL);
        CREATE TABLE test_mod.inbox_message (LIKE integration.inbox_message INCLUDING ALL);
        DO $$
        DECLARE t text;
        BEGIN
          FOREACH t IN ARRAY ARRAY['test_mod.widget', 'test_mod.consumption', 'test_mod.ledger_consumption', 'test_mod.outbox_event', 'test_mod.inbox_message'] LOOP
            EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
            EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
            EXECUTE format($p$CREATE POLICY tenant_isolation ON %s
              USING (tenant_id = current_setting('app.tenant_id')::uuid)
              WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid)$p$, t);
          END LOOP;
        END $$;
        GRANT USAGE ON SCHEMA test_mod TO {PostgresServer.TestModuleRole};
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA test_mod TO {PostgresServer.TestModuleRole};
        GRANT USAGE ON ALL SEQUENCES IN SCHEMA test_mod TO {PostgresServer.TestModuleRole};
        """);
}
