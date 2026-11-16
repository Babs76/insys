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
-- V0136: Wave 7, batch 7.7. The SETUP change set (AIA-086, D-11, decided 1 November 2026): a tenant's setup applied as one change, one import batch per
-- table through the existing import path (BR-016), with one approval by a second person bound to a hash of every batch, applied in dependency order and
-- resumable. Only for a tenant still onboarding; the tenant can't go live while a set is open (activation guard, Tenancy).
--  * sdm.setup_change_set: the set (its maker, hash, status, checker, when it was approved and applied, how it ended if not approved).
--  * sdm.setup_change_set_batch: its batches in order, each an import (sdm.import_batch) and its change request, with the lines' hash.
--  * sdm.change_request gains setup_set_id: a batch's change request takes no approval of its own; the set's one approval decides it.
--  * assist.setup_run gains SUBMITTING (completeness passed, values locked, batches loading), and the set it was submitted as.
-- =====================================================================

CREATE TABLE sdm.setup_change_set (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  set_number                 varchar(20) NOT NULL,
  source_ref                 uuid NOT NULL,
  maker_id                   uuid NOT NULL,
  hash                       char(64) NOT NULL,
  status                     varchar(12) NOT NULL,
  checker_id                 uuid,
  approved_at                timestamptz,
  applied_at                 timestamptz,
  ending                     varchar(10),
  CONSTRAINT pk_setup_change_set PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_setup_change_set_number UNIQUE (tenant_id, set_number),
  CONSTRAINT ck_setup_change_set_status CHECK (status IN ('SUBMITTED', 'APPLYING', 'APPLIED', 'NOT_APPROVED')),
  CONSTRAINT ck_setup_change_set_checker CHECK (checker_id IS NULL OR checker_id <> maker_id),
  CONSTRAINT ck_setup_change_set_approved CHECK ((status IN ('APPLYING', 'APPLIED')) = (approved_at IS NOT NULL AND checker_id IS NOT NULL)),
  CONSTRAINT ck_setup_change_set_applied CHECK ((status = 'APPLIED') = (applied_at IS NOT NULL)),
  CONSTRAINT ck_setup_change_set_ending CHECK ((status = 'NOT_APPROVED') = (ending IS NOT NULL))
);
COMMENT ON TABLE sdm.setup_change_set IS 'AIA-086: a tenant''s setup as one change. One approval (Tier 1) by someone other than the maker, bound to hash; applied batch by batch.';
COMMENT ON COLUMN sdm.setup_change_set.source_ref IS 'The setup it came from: an Assist setup run (assist.setup_run.id).';
COMMENT ON COLUMN sdm.setup_change_set.hash IS 'SHA-256 over every batch (order, table, batch, line count, the lines'' hash): the approval''s subject.';
ALTER TABLE sdm.setup_change_set ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.setup_change_set FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.setup_change_set
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE sdm.setup_change_set_batch (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  set_id                     uuid NOT NULL,
  seq                        integer NOT NULL,
  table_name                 varchar(80) NOT NULL,
  entity_code                varchar(40) NOT NULL,
  batch_id                   uuid NOT NULL,
  change_request_id          uuid NOT NULL,
  line_count                 integer NOT NULL,
  hash                       char(64) NOT NULL,
  applied_at                 timestamptz,
  CONSTRAINT pk_setup_change_set_batch PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_setup_change_set_batch_seq UNIQUE (tenant_id, set_id, seq),
  CONSTRAINT uq_setup_change_set_batch_table UNIQUE (tenant_id, set_id, table_name),
  CONSTRAINT uq_setup_change_set_batch_batch UNIQUE (tenant_id, batch_id),
  CONSTRAINT fk_setup_change_set_batch_set FOREIGN KEY (tenant_id, set_id) REFERENCES sdm.setup_change_set (tenant_id, id),
  CONSTRAINT ck_setup_change_set_batch_lines CHECK (line_count > 0)
);
COMMENT ON TABLE sdm.setup_change_set_batch IS 'AIA-086: one table''s import in a SETUP change set, applied in seq order; applied_at once every line is in force.';
ALTER TABLE sdm.setup_change_set_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE sdm.setup_change_set_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sdm.setup_change_set_batch
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

ALTER TABLE sdm.change_request ADD COLUMN setup_set_id uuid;
ALTER TABLE sdm.change_request ADD CONSTRAINT ck_change_request_setup CHECK (setup_set_id IS NULL OR action = 'IMPORT');
CREATE INDEX ix_change_request_setup_set ON sdm.change_request (tenant_id, setup_set_id) WHERE setup_set_id IS NOT NULL;
COMMENT ON COLUMN sdm.change_request.setup_set_id IS 'AIA-086: for a batch of a SETUP change set, the set whose one approval decides it.';

ALTER TABLE assist.setup_run DROP CONSTRAINT ck_setup_run_status;
ALTER TABLE assist.setup_run ADD CONSTRAINT ck_setup_run_status CHECK (status IN ('DRAFT', 'SUBMITTING', 'SUBMITTED', 'APPLIED'));
ALTER TABLE assist.setup_run
  ADD COLUMN attempt integer NOT NULL DEFAULT 0,
  ADD COLUMN set_id uuid,
  ADD COLUMN submitted_by varchar(80),
  ADD COLUMN submitted_at timestamptz;
ALTER TABLE assist.setup_run ADD CONSTRAINT ck_setup_run_set CHECK ((status IN ('SUBMITTING', 'SUBMITTED', 'APPLIED')) = (set_id IS NOT NULL AND submitted_by IS NOT NULL));
COMMENT ON COLUMN assist.setup_run.attempt IS 'AIA-086: how many times the run has been submitted; each attempt''s batches have their own ids.';
COMMENT ON COLUMN assist.setup_run.set_id IS 'AIA-086: the SETUP change set (sdm.setup_change_set) the run is submitted as; cleared if the set is not approved.';
