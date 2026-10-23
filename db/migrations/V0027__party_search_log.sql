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
-- V0027: an access log for party searches (Phase 8 Wave 0, GAP-022; PTY-003, PTY-005). Each search records who
-- searched, when, its filters and how many parties it returned. An identifier is recorded by its keyed hash only,
-- never its number. Insert-only (db/roles/module_grants.sql revokes UPDATE and DELETE).
-- =====================================================================
CREATE TABLE party.party_search_log (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  searched_at                timestamptz NOT NULL,
  searched_by                varchar(80) NOT NULL,
  name_filter                varchar(200),
  identifier_doc_type        varchar(15),
  identifier_country         char(2),
  identifier_hash            char(64),
  role_filter                varchar(20),
  status_filter              varchar(12),
  result_count               integer NOT NULL,
  CONSTRAINT pk_party_search_log PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_party_search_log_identifier CHECK ((identifier_hash IS NULL) = (identifier_doc_type IS NULL)),
  CONSTRAINT ck_party_search_log_result_count CHECK (result_count >= 0)
);
CREATE INDEX ix_party_search_log_1 ON party.party_search_log (tenant_id, searched_by, searched_at);
ALTER TABLE party.party_search_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party_search_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party_search_log
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
