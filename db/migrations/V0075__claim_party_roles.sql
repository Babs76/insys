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
-- V0075: a General claim party's role comes from a tenant-maintained list (OQ-122, decided 28 October 2026).
-- general.cfg_claim_party_roles is CFG, scope T. Each existing tenant gets the starter list as drafts, for its claims
-- team to confirm (submit and activate) or change; a role is accepted only once it is in force.
-- It has every static table's columns, import_batch_id (V0067) included.
-- =====================================================================
CREATE TABLE general.cfg_claim_party_roles (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
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
  label                          varchar(60) NOT NULL,
  CONSTRAINT cfg_claim_party_roles_pk PRIMARY KEY (id),
  CONSTRAINT cfg_claim_party_roles_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_claim_party_roles_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_claim_party_roles_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_claim_party_roles_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_claim_party_roles_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_claim_party_roles_lookup ON general.cfg_claim_party_roles (tenant_id, code, effective_from);
CREATE INDEX cfg_claim_party_roles_import ON general.cfg_claim_party_roles (import_batch_id) WHERE import_batch_id IS NOT NULL;
ALTER TABLE general.cfg_claim_party_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.cfg_claim_party_roles FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.cfg_claim_party_roles
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON general.cfg_claim_party_roles
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE general.cfg_claim_party_roles IS 'Claim party roles. CFG, scope T, Tier 2. The roles a party may take on a General claim (general.claim_party.role); a role is at most 15 characters. Added by OQ-122 (28 October 2026); starter list seeded as drafts for the claims team to confirm.';

-- The starter list, as drafts in every existing tenant.
INSERT INTO general.cfg_claim_party_roles (tenant_id, code, version, effective_from, record_status, created_by, change_reason, sort_order, label)
SELECT t.id, s.code, 1, DATE '2026-01-01', 'DRAFT', 'V0075', 'OQ-122 starter list, for the claims team to confirm', s.sort_order, s.label
FROM tenant.tenant t
CROSS JOIN (VALUES
  ('CLAIMANT', 1, 'Claimant'),
  ('INSURED', 2, 'Insured'),
  ('THIRD_PARTY', 3, 'Third party'),
  ('DRIVER', 4, 'Driver'),
  ('WITNESS', 5, 'Witness'),
  ('ASSESSOR', 6, 'Assessor'),
  ('REPAIRER', 7, 'Repairer'),
  ('LEGAL_REP', 8, 'Legal representative'),
  ('PAYEE', 9, 'Payee')) AS s(code, sort_order, label);
