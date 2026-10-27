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
-- V0067: a static data import is approved as one change (BR-016, signed off 27 October 2026 on three conditions: the
-- import is approved or rejected as a whole, never partly; the checker sees every line; the approval is bound to a hash
-- of the complete import, so any change needs a new approval).
--  * Every static data table (each that sdm.guard_static_version guards) gains import_batch_id: the import that created
--    the draft. A draft of an import is submitted, approved and activated only with the rest of its import.
--  * sdm.change_request gains import_batch_id and import_hash: an IMPORT change request names its batch and the hash of
--    its lines when submitted; approval applies only if the lines still hash the same.
--  * sdm.import_batch keeps its statuses: an import's state is its change request's status, and each line's.
-- =====================================================================

DO $$
DECLARE
  t record;
BEGIN
  FOR t IN
    SELECT DISTINCT n.nspname AS schema_name, c.relname AS table_name
    FROM pg_trigger g
    JOIN pg_class c ON c.oid = g.tgrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE g.tgfoid = 'sdm.guard_static_version'::regproc AND NOT g.tgisinternal
  LOOP
    EXECUTE format('ALTER TABLE %I.%I ADD COLUMN IF NOT EXISTS import_batch_id uuid', t.schema_name, t.table_name);
    EXECUTE format('COMMENT ON COLUMN %I.%I.import_batch_id IS %L', t.schema_name, t.table_name,
      'BR-016: the import that created this draft; it is submitted, approved and activated with the rest of its import.');
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I.%I (import_batch_id) WHERE import_batch_id IS NOT NULL',
      left(t.table_name, 50) || '_import', t.schema_name, t.table_name);
  END LOOP;
END $$;

ALTER TABLE sdm.change_request
  ADD COLUMN import_batch_id uuid,
  ADD COLUMN import_hash char(64),
  ADD CONSTRAINT ck_change_request_import CHECK ((action = 'IMPORT') = (import_batch_id IS NOT NULL) AND (import_batch_id IS NULL) = (import_hash IS NULL));
COMMENT ON COLUMN sdm.change_request.import_batch_id IS 'BR-016: for an IMPORT change request, the import approved as one change.';
COMMENT ON COLUMN sdm.change_request.import_hash IS 'BR-016: SHA-256 of the import''s lines when submitted; the approval applies only to lines that still hash the same.';
