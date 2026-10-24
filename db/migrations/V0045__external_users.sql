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
-- V0045: external users (docs/specs/addenda/external-user-access.md, signed off 24 October 2026).
-- - An IAM user is linked to exactly one party, intermediary or provider, under maker-checker: pending until a second
--   person approves it in Workflow, then active until ended. At most one current link per user.
-- - Authentication policies name the portal clients they apply to, and whether MFA is required there.
-- - A party registered through a portal that matches an existing party waits for staff review (POPIA: the portal
--   user is told the same either way).
-- =====================================================================
CREATE TABLE iam.user_subject_link (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  user_id                    uuid NOT NULL,
  kind                       varchar(12) NOT NULL,
  party_id                   uuid,
  intermediary_code          varchar(15),
  provider_id                varchar(15),
  status                     varchar(10) NOT NULL,
  requested_by               uuid NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  ended_by                   uuid,
  ended_at                   timestamptz,
  CONSTRAINT pk_user_subject_link PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_user_subject_link_user FOREIGN KEY (tenant_id, user_id) REFERENCES iam.user_account (tenant_id, id),
  CONSTRAINT ck_user_subject_link_kind CHECK (kind IN ('PARTY', 'INTERMEDIARY', 'PROVIDER')),
  CONSTRAINT ck_user_subject_link_status CHECK (status IN ('PENDING', 'ACTIVE', 'ENDED', 'REJECTED')),
  CONSTRAINT ck_user_subject_link_key CHECK (
    (kind = 'PARTY' AND party_id IS NOT NULL AND intermediary_code IS NULL AND provider_id IS NULL)
    OR (kind = 'INTERMEDIARY' AND intermediary_code IS NOT NULL AND party_id IS NULL AND provider_id IS NULL)
    OR (kind = 'PROVIDER' AND provider_id IS NOT NULL AND party_id IS NULL AND intermediary_code IS NULL))
);
CREATE UNIQUE INDEX uq_user_subject_link_current ON iam.user_subject_link (tenant_id, user_id) WHERE status IN ('PENDING', 'ACTIVE');
ALTER TABLE iam.user_subject_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.user_subject_link FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.user_subject_link
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

ALTER TABLE iam.cfg_authentication_and_session_policies ADD COLUMN client_ids text[];
ALTER TABLE iam.cfg_authentication_and_session_policies ADD COLUMN mfa_required boolean;

ALTER TABLE party.party ADD COLUMN review_status varchar(15);
ALTER TABLE party.party ADD COLUMN registered_by varchar(40);
