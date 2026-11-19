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
-- V0146: Wave 7, batch 7.5. AI-1's review (AI Assist SRS §4.2, AIA-019, AIA-020): the configurator accepts, edits or rejects each proposed value, and an
-- edit is recorded against the AI's proposal, which is kept as it was. A version's own field decided this way is written to the Draft as the user, through
-- Product Factory's own edit (AIA-017); everything else is recorded for the configurator and the approver. The provenance report reads these rows.
-- =====================================================================
ALTER TABLE assist.draft_item
  ADD COLUMN decision varchar(8),
  ADD COLUMN decided_by varchar(80),
  ADD COLUMN decided_at timestamptz,
  ADD COLUMN decision_note varchar(500),
  ADD COLUMN edited_value jsonb,
  ADD CONSTRAINT ck_draft_item_decision CHECK (decision IS NULL OR decision IN ('ACCEPTED', 'EDITED', 'REJECTED')),
  ADD CONSTRAINT ck_draft_item_decided CHECK ((decision IS NULL) = (decided_by IS NULL) AND (decision IS NULL) = (decided_at IS NULL)),
  -- AIA-019: an edit records the person's value and why, beside the AI's proposal (value), which never changes.
  ADD CONSTRAINT ck_draft_item_edited CHECK ((decision = 'EDITED') = (edited_value IS NOT NULL) AND (decision IS DISTINCT FROM 'EDITED' OR decision_note IS NOT NULL)),
  -- A dropped proposal failed INSYS's checks, so it is never decided; a Missing one is only filled, by an edit.
  ADD CONSTRAINT ck_draft_item_not_decided_dropped CHECK (check_result = 'PASSED' OR decision IS NULL),
  ADD CONSTRAINT ck_draft_item_missing_filled CHECK (flag <> 'MISSING' OR decision IS NULL OR decision = 'EDITED');
COMMENT ON COLUMN assist.draft_item.decision IS 'AIA-019: ACCEPTED, EDITED (edited_value, with decision_note) or REJECTED; null while undecided.';
COMMENT ON COLUMN assist.draft_item.edited_value IS 'The configurator''s value, recorded against the AI''s proposal (value), which is kept unchanged.';
CREATE INDEX ix_product_draft_run_version ON assist.product_draft_run (tenant_id, product_code, draft_version) WHERE draft_version IS NOT NULL;
