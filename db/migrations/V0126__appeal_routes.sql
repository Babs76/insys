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
-- V0126: appeal routes as static data (country readiness survey, section 3; Nigeria blocker). A case type names any route in the
-- list, not only PPR_NFO or CMS; a route says which lines' claim decisions prefer it. The two South African routes stay as global
-- rows, so existing tenants choose as before. A tenant's own row wins over a global one with the same route code.
-- =====================================================================
CREATE TABLE workflow.ref_appeal_routes (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  import_batch_id                uuid,
  route_code                     varchar(20) NOT NULL,
  name                           varchar(200) NOT NULL,
  preferred_lines                text[] NOT NULL,
  CONSTRAINT ref_appeal_routes_pk PRIMARY KEY (id),
  CONSTRAINT ref_appeal_routes_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_appeal_routes_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_appeal_routes_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_appeal_routes_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_appeal_routes_ck1 CHECK (preferred_lines <@ ARRAY['LIFE', 'GENERAL', 'HEALTH']::text[]),
  CONSTRAINT ref_appeal_routes_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE'))
);
CREATE INDEX ref_appeal_routes_lookup ON workflow.ref_appeal_routes (tenant_id, code, effective_from);
ALTER TABLE workflow.ref_appeal_routes ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.ref_appeal_routes FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON workflow.ref_appeal_routes FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON workflow.ref_appeal_routes FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON workflow.ref_appeal_routes FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON workflow.ref_appeal_routes FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON workflow.ref_appeal_routes TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.ref_appeal_routes
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.ref_appeal_routes IS 'Appeal routes. REF, scope GT, Tier 2. Where a decision can be taken outside the insurer, and the lines whose claim decisions prefer the route. Case types name a route by route_code; readiness lists one that is not in force.';
COMMENT ON COLUMN workflow.ref_appeal_routes.preferred_lines IS 'preferred_lines: LIFE, GENERAL, HEALTH. A claim on one of these lines takes a dispute case type with this route first. Sample: LIFE, GENERAL';

INSERT INTO workflow.ref_appeal_routes (tenant_id, code, version, effective_from, record_status, created_by, change_reason, route_code, name, preferred_lines) VALUES
  (NULL, 'PPR_NFO', 1, DATE '2026-01-01', 'ACTIVE', 'V0126', 'OQ-101', 'PPR_NFO', 'Policyholder Protection Rules and the National Financial Ombud Scheme', ARRAY['LIFE', 'GENERAL']),
  (NULL, 'CMS', 1, DATE '2026-01-01', 'ACTIVE', 'V0126', 'OQ-101', 'CMS', 'Council for Medical Schemes', ARRAY['HEALTH']);

ALTER TABLE workflow.cfg_case_types ALTER COLUMN appeal_route TYPE varchar(20);
ALTER TABLE workflow.cfg_case_types DROP CONSTRAINT cfg_case_types_ck4;
ALTER TABLE workflow.cfg_case_types ADD CONSTRAINT cfg_case_types_ck4 CHECK (appeal_route ~ '^[A-Z][A-Z0-9_]*$');
