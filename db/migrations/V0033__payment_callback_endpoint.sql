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
-- V0033: provider callback endpoints (ADR-023 revision 2, OQ-077 closed 23 October 2026). A callback address per tenant
-- and provider carries an unguessable endpoint key; only the key's SHA-256 hash is stored. The signing secret is stored
-- encrypted by the application (field protection, DEW KMS in the cloud). callback_receipt keeps each endpoint's
-- callback IDs for the replay window. resolve_callback_endpoint finds an endpoint by key hash across tenants, one
-- tenant at a time with app.tenant_id set (as integration.outbox_lag does), so no table loses row-level security.
-- =====================================================================
CREATE TABLE payment.callback_endpoint (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_code              varchar(15) NOT NULL,
  key_hash                   char(64) NOT NULL,
  secret_enc                 bytea NOT NULL,
  status                     varchar(10) NOT NULL,
  require_client_token       boolean NOT NULL,
  issued_at                  timestamptz NOT NULL,
  issued_by                  varchar(80) NOT NULL,
  retires_at                 timestamptz,
  revoked_at                 timestamptz,
  revoked_by                 varchar(80),
  CONSTRAINT pk_callback_endpoint PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_callback_endpoint_key UNIQUE (key_hash),
  CONSTRAINT ck_callback_endpoint_status CHECK (status IN ('ACTIVE', 'RETIRING', 'REVOKED')),
  CONSTRAINT ck_callback_endpoint_retiring CHECK (status <> 'RETIRING' OR retires_at IS NOT NULL),
  CONSTRAINT ck_callback_endpoint_revoked CHECK ((status = 'REVOKED') = (revoked_at IS NOT NULL))
);
COMMENT ON COLUMN payment.callback_endpoint.key_hash IS 'SHA-256 of the endpoint key, hex. The key itself is never stored.';
COMMENT ON COLUMN payment.callback_endpoint.secret_enc IS 'The HMAC signing secret, encrypted by field protection (ADR-018). Never logged.';
CREATE INDEX ix_callback_endpoint_provider ON payment.callback_endpoint (tenant_id, provider_code, status);
ALTER TABLE payment.callback_endpoint ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.callback_endpoint FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.callback_endpoint
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE payment.callback_receipt (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  endpoint_id                uuid NOT NULL,
  callback_id                varchar(100) NOT NULL,
  received_at                timestamptz NOT NULL,
  CONSTRAINT pk_callback_receipt PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_callback_receipt UNIQUE (tenant_id, endpoint_id, callback_id)
);
CREATE INDEX ix_callback_receipt_received ON payment.callback_receipt (tenant_id, endpoint_id, received_at);
ALTER TABLE payment.callback_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment.callback_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON payment.callback_receipt
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- The tenant and endpoint for a key hash, if it is usable now (ACTIVE, or RETIRING before its cut-over). Nothing else.
CREATE OR REPLACE FUNCTION payment.resolve_callback_endpoint(p_key_hash char(64))
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
        AND (e.status = 'ACTIVE' OR (e.status = 'RETIRING' AND e.retires_at > now()));
    EXIT WHEN FOUND;
  END LOOP;
  PERFORM set_config('app.tenant_id', coalesce(previous, ''), true);
END $$;
REVOKE ALL ON FUNCTION payment.resolve_callback_endpoint(char) FROM PUBLIC;
-- Grants: db/roles/module_grants.sql gives insys_payment EXECUTE on it, and DML on both tables.
