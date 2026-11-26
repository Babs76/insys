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
-- V0159: a draft nobody else could clear (DE-016, OQ-204; Ade, 25 November 2026). Anyone holding a table's maintain function
-- may discard another maker's draft once it has been untouched for N days, with a reason, audited. N is a tenant setting.
--  * sdm.tenant_governance_setting gains stale_draft_discard_days: N, 7 unless the tenant changes it, at least 1.
--    TODO(spec): no upper bound was given (OQ-204).
--  * Every static data table (each that sdm.guard_static_version guards, as V0067) gains edited_at: when the draft's maker
--    last edited it (EditDraft), null until then. A draft is untouched since the later of created_at and edited_at. Only a
--    draft is edited, so the column never changes on an approved version, and the guard's immutable columns are unchanged.
--    A static table created after this migration carries edited_at among its common columns, as import_batch_id.
-- =====================================================================
ALTER TABLE sdm.tenant_governance_setting
  ADD COLUMN stale_draft_discard_days integer NOT NULL DEFAULT 7,
  ADD CONSTRAINT ck_tenant_governance_setting_stale_draft_days CHECK (stale_draft_discard_days >= 1);
COMMENT ON COLUMN sdm.tenant_governance_setting.stale_draft_discard_days IS
  'DE-016, OQ-204: a draft untouched for this many days (24-hour periods) may be discarded by anyone holding its table''s maintain function, with a reason.';

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
    EXECUTE format('ALTER TABLE %I.%I ADD COLUMN IF NOT EXISTS edited_at timestamptz', t.schema_name, t.table_name);
    EXECUTE format('COMMENT ON COLUMN %I.%I.edited_at IS %L', t.schema_name, t.table_name,
      'DE-016, OQ-204: when the draft''s maker last edited it; null until then. Untouched since the later of created_at and edited_at.');
  END LOOP;
END $$;

-- OQ-204 (Ade, 25 November 2026): the maker is told when someone else discards their draft. Workflow opens an informational
-- STATIC_DRAFT_DISCARDED task assigned to them (as APPROVAL_OUTCOME tells a requester, DE-044); the process's notice holds what it tells:
-- the table, code and version, who discarded it, when, and the reason. Null for every other process.
ALTER TABLE workflow.process_instance ADD COLUMN notice jsonb;
COMMENT ON COLUMN workflow.process_instance.notice IS
  'OQ-204: what an informational task tells its assignee (a discarded draft: table, code, version, discarded by, when, reason); null otherwise.';
