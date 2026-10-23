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
-- INSYS Core · Fixes to three schema gaps in the original specification
-- (Phase 1 review, item 2).
--  a. sdm.change_request.status is widened so it can hold PENDING_APPROVAL (OQ-033).
--  b. iam.privileged_access_grant can hold the PrivilegedAccessGrant states of
--     domain-model.json: requester, status, a nullable approver and the decision.
--     row_version, updated_at and updated_by are added because the grant now
--     changes after insert (DBA-011, OQ-040).
--  c. iam.access_review_item holds the ReviewItem entity of AccessReview (OQ-027).
-- =====================================================================

-- a.
ALTER TABLE sdm.change_request ALTER COLUMN status TYPE varchar(20);

-- b. Any existing grants were approved by definition (approved_by was NOT NULL).
ALTER TABLE iam.privileged_access_grant
  ADD COLUMN requested_by     uuid,
  ADD COLUMN status           varchar(12),
  ADD COLUMN decided_at       timestamptz,
  ADD COLUMN rejection_reason text,
  ADD COLUMN row_version      bigint NOT NULL DEFAULT 1,
  ADD COLUMN updated_at       timestamptz,
  ADD COLUMN updated_by       varchar(80);
UPDATE iam.privileged_access_grant
SET requested_by = user_id, status = 'APPROVED', decided_at = created_at, updated_at = created_at, updated_by = created_by;
ALTER TABLE iam.privileged_access_grant
  ALTER COLUMN requested_by SET NOT NULL,
  ALTER COLUMN status SET NOT NULL,
  ALTER COLUMN updated_at SET NOT NULL,
  ALTER COLUMN updated_by SET NOT NULL,
  ALTER COLUMN approved_by DROP NOT NULL,
  ADD CONSTRAINT ck_privileged_access_grant_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'EXPIRED', 'REVOKED')),
  ADD CONSTRAINT ck_privileged_access_grant_approver CHECK (approved_by IS NULL OR approved_by <> requested_by),
  ADD CONSTRAINT ck_privileged_access_grant_window CHECK (valid_to > valid_from),
  ADD CONSTRAINT fk_privileged_access_grant_requested_by FOREIGN KEY (tenant_id, requested_by) REFERENCES iam.user_account (tenant_id, id);
CREATE INDEX ix_privileged_access_grant_2 ON iam.privileged_access_grant (tenant_id, status, valid_to);

-- c.
CREATE TABLE iam.access_review_item (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  review_id                  uuid NOT NULL,
  user_id                    uuid NOT NULL,
  role_code                  varchar(30) NOT NULL,
  decision                   varchar(12) NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  CONSTRAINT pk_access_review_item PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_access_review_item_decision CHECK (decision IN ('PENDING', 'CONFIRMED', 'REVOKED')),
  CONSTRAINT ck_access_review_item_decided CHECK ((decision = 'PENDING') = (decided_by IS NULL))
);
ALTER TABLE iam.access_review_item ADD CONSTRAINT fk_access_review_item_review_id
  FOREIGN KEY (tenant_id, review_id) REFERENCES iam.access_review (tenant_id, id);
ALTER TABLE iam.access_review_item ADD CONSTRAINT fk_access_review_item_user_id
  FOREIGN KEY (tenant_id, user_id) REFERENCES iam.user_account (tenant_id, id);
ALTER TABLE iam.access_review_item ADD CONSTRAINT fk_access_review_item_decided_by
  FOREIGN KEY (tenant_id, decided_by) REFERENCES iam.user_account (tenant_id, id);
CREATE INDEX ix_access_review_item_1 ON iam.access_review_item (tenant_id, review_id);
ALTER TABLE iam.access_review_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.access_review_item FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.access_review_item
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
