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
-- INSYS Core · Per-tenant governance settings (Phase 1 review, item 5).
-- SDM-003: Tier 2 changes require maker-checker "unless the tenant switches it
-- off". The setting defaults to on; Tier 1 is always maker-checker. Owned by the
-- Static Data module; every change is audited. One row per tenant.
-- =====================================================================
CREATE TABLE sdm.tenant_governance_setting (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  tier2_maker_checker        boolean NOT NULL DEFAULT true,
  CONSTRAINT pk_tenant_governance_setting PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_tenant_governance_setting UNIQUE (tenant_id)
);
ALTER TABLE sdm.tenant_governance_setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.tenant_governance_setting FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.tenant_governance_setting
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
