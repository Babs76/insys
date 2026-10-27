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
-- V0066: legal holds on a record itself (D-34, decided 27 October 2026): a party, a Life or General policy, a Health
-- membership, a Life, General or Health claim, or a case. Payments are covered through their policy or claim. A hold
-- keeps the record's documents from disposal, keeps a held case from disposal, and blocks the anonymisation of the party
-- it is on or whose linked record it is on. Releasing one needs a second person (LEGAL_HOLD_RELEASE, OQ-111), as for a
-- document's hold. document.legal_hold stays the document aggregate's own holds.
-- =====================================================================

CREATE TABLE document.record_hold (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  scope_type                 varchar(30) NOT NULL,
  scope_id                   uuid NOT NULL,
  reason_code                varchar(15) NOT NULL,
  placed_by                  uuid NOT NULL,
  placed_at                  timestamptz NOT NULL,
  released_at                timestamptz,
  released_by                uuid,
  release_approval_id        uuid,
  release_requested_by       uuid,
  release_requested_at       timestamptz,
  release_reason             varchar(200),
  CONSTRAINT pk_record_hold PRIMARY KEY (tenant_id, id),
  CONSTRAINT ck_record_hold_scope CHECK (scope_type IN ('PARTY', 'LIFE_POLICY', 'GENERAL_POLICY', 'HEALTH_MEMBERSHIP', 'LIFE_CLAIM', 'GENERAL_CLAIM',
    'HEALTH_CLAIM', 'CASE')),
  CONSTRAINT ck_record_hold_release_request CHECK (
    (release_requested_by IS NULL) = (release_requested_at IS NULL) AND (release_requested_by IS NULL) = (release_reason IS NULL)),
  CONSTRAINT ck_record_hold_release_pending CHECK (released_at IS NULL OR release_requested_by IS NULL)
);
CREATE INDEX ix_record_hold_1 ON document.record_hold (tenant_id, scope_id) WHERE released_at IS NULL;
CREATE INDEX ix_record_hold_2 ON document.record_hold (tenant_id, scope_type, scope_id);
COMMENT ON TABLE document.record_hold IS 'D-34: a legal hold on a record itself. In force while released_at is null; released only through a LEGAL_HOLD_RELEASE approval (OQ-111).';
COMMENT ON COLUMN document.record_hold.scope_id IS 'The held record: party.party, life.policy, general.policy, health.membership, life.claim, general.claim, health.medical_claim or workflow.service_case.';

ALTER TABLE document.record_hold ENABLE ROW LEVEL SECURITY;
ALTER TABLE document.record_hold FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON document.record_hold
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
