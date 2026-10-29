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
-- V0091: identity verification (F001, addenda/due-diligence.md §2 to §4.1, signed off 29 October 2026). Providers are
-- partners of two new types behind adapters. Every check run on a party is kept: by a provider (its reference) or by a
-- person against a document (the document's ID). Personal: a check holds no number, only the identifier it is about.
-- =====================================================================
ALTER TABLE integration.mst_partner_register DROP CONSTRAINT mst_partner_register_ck1;
ALTER TABLE integration.mst_partner_register ADD CONSTRAINT mst_partner_register_ck1 CHECK (partner_type IN ('BROKER_SYSTEM', 'BANK', 'PAYMENT_PROVIDER', 'PROVIDER_SYSTEM', 'REINSURER', 'GL', 'OTHER', 'IDENTITY_VERIFICATION', 'SCREENING'));

CREATE TABLE party.due_diligence_check (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  check_type                 varchar(25) NOT NULL,
  outcome                    varchar(12) NOT NULL,
  identifier_id              uuid,
  partner_code               varchar(15),
  provider_ref               varchar(80),
  document_id                uuid,
  reason                     varchar(200),
  checked_at                 timestamptz NOT NULL,
  checked_by                 uuid,
  CONSTRAINT pk_due_diligence_check PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_due_diligence_check_party FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id),
  CONSTRAINT ck_due_diligence_check_type CHECK (check_type IN ('IDENTITY', 'ADDRESS', 'SANCTIONS', 'PEP', 'ADVERSE_MEDIA', 'BENEFICIAL_OWNERSHIP', 'SOURCE_OF_FUNDS')),
  CONSTRAINT ck_due_diligence_check_outcome CHECK (outcome IN ('PASSED', 'FAILED', 'HIT', 'WAIVED')),
  CONSTRAINT ck_due_diligence_check_source CHECK ((partner_code IS NOT NULL) <> (document_id IS NOT NULL))
);
CREATE INDEX ix_due_diligence_check_1 ON party.due_diligence_check (tenant_id, party_id, checked_at);
COMMENT ON TABLE party.due_diligence_check IS 'F001: each due diligence check run on a party, by a provider (partner_code, provider_ref) or by a person against a document (document_id, checked_by).';
ALTER TABLE party.due_diligence_check ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.due_diligence_check FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.due_diligence_check
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
