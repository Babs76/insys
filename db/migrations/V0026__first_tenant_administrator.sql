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
-- V0026: a platform operator's request for a new tenant's first administrator (Phase 8 Wave 0, GAP-019, OQ-067).
-- The request is the operator tenant's row: a platform administrator names the user and the TENANT_ADMIN role's
-- permissions, and a second platform administrator approves it in Workflow (SRS-002). On approval the Identity module
-- creates, in the new tenant, the TENANT_ADMIN role definition, the user and an active assignment, in one transaction.
-- =====================================================================
CREATE TABLE iam.tenant_administrator_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  target_tenant_id           uuid NOT NULL,
  username                   varchar(120) NOT NULL,
  display_name               varchar(120) NOT NULL,
  idp_code                   varchar(15) NOT NULL,
  external_subject           varchar(120) NOT NULL,
  mfa_enrolled               boolean NOT NULL,
  role_code                  varchar(30) NOT NULL,
  permissions                text[] NOT NULL,
  requested_by               uuid NOT NULL,
  status                     varchar(20) NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  user_id                    uuid,
  CONSTRAINT pk_tenant_administrator_request PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_tenant_administrator_request_status CHECK (status IN ('PENDING_APPROVAL', 'PROVISIONED', 'REJECTED')),
  CONSTRAINT ck_tenant_administrator_request_checker CHECK (decided_by IS NULL OR decided_by <> requested_by),
  CONSTRAINT ck_tenant_administrator_request_decided CHECK ((status = 'PENDING_APPROVAL') = (decided_at IS NULL)),
  CONSTRAINT ck_tenant_administrator_request_user CHECK ((status = 'PROVISIONED') = (user_id IS NOT NULL)),
  CONSTRAINT ck_tenant_administrator_request_permissions CHECK (cardinality(permissions) > 0)
);
-- One open request per tenant at a time.
CREATE UNIQUE INDEX uq_tenant_administrator_request_pending ON iam.tenant_administrator_request (tenant_id, target_tenant_id)
  WHERE status = 'PENDING_APPROVAL';
CREATE INDEX ix_tenant_administrator_request_target ON iam.tenant_administrator_request (tenant_id, target_tenant_id);
ALTER TABLE iam.tenant_administrator_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.tenant_administrator_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.tenant_administrator_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Grants: db/roles/module_grants.sql gives insys_iam its schema's tables.
