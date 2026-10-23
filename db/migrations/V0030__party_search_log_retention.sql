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
-- V0030: retention of the party search access log (Phase 8 Wave 0, OQ-073 decided 23 October 2026). The Security
-- retention class, per tenant (audit.cfg_audit_retention_policies): full entries for 12 months, then the plain-text name
-- filter removed (POPIA s14 minimisation); deleted 5 years after the search (FICA s23, LTIA PPR rule 16.4(b)), unless a
-- legal hold covers them. The log stays insert-only for insys_party: these two functions, run with the owner's rights,
-- are the only way rows change, and row-level security still limits them to the caller's tenant.
-- =====================================================================
CREATE TABLE party.search_log_hold (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  searched_by                varchar(80),
  reason                     varchar(200) NOT NULL,
  placed_by                  varchar(80) NOT NULL,
  placed_at                  timestamptz NOT NULL,
  released_by                varchar(80),
  released_at                timestamptz,
  CONSTRAINT pk_search_log_hold PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_search_log_hold_release CHECK ((released_at IS NULL) = (released_by IS NULL))
);
COMMENT ON COLUMN party.search_log_hold.searched_by IS 'The searcher whose entries the hold keeps; NULL holds the tenant''s whole log.';
ALTER TABLE party.search_log_hold ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.search_log_hold FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.search_log_hold
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- Removes the plain-text name filter from the caller's tenant's entries made before p_before. Returns how many.
CREATE OR REPLACE FUNCTION party.minimise_search_log(p_before timestamptz)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  changed integer;
BEGIN
  UPDATE party.party_search_log SET name_filter = NULL
  WHERE tenant_id = current_setting('app.tenant_id')::uuid AND searched_at < p_before AND name_filter IS NOT NULL;
  GET DIAGNOSTICS changed = ROW_COUNT;
  RETURN changed;
END
$$;

-- Deletes the caller's tenant's entries made before p_before that no active legal hold covers. Returns how many.
CREATE OR REPLACE FUNCTION party.purge_search_log(p_before timestamptz)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  removed integer;
BEGIN
  DELETE FROM party.party_search_log l
  WHERE l.tenant_id = current_setting('app.tenant_id')::uuid AND l.searched_at < p_before
    AND NOT EXISTS (
      SELECT 1 FROM party.search_log_hold h
      WHERE h.tenant_id = l.tenant_id AND h.released_at IS NULL AND (h.searched_by IS NULL OR h.searched_by = l.searched_by));
  GET DIAGNOSTICS removed = ROW_COUNT;
  RETURN removed;
END
$$;

REVOKE ALL ON FUNCTION party.minimise_search_log(timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION party.purge_search_log(timestamptz) FROM PUBLIC;
-- Grants: db/roles/module_grants.sql gives insys_party EXECUTE on these and DML on search_log_hold.
