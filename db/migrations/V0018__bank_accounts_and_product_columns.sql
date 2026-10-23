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
-- INSYS Core · Payee bank accounts, and product settings as columns (Phase 5, items 1 and 2)
-- 1. A party's bank accounts (a gap in the original specification): the account number is encrypted by the
--    field protector, with a keyed hash for matching and the last four digits for display. A change is a new
--    row and the previous one is SUPERSEDED, never updated in place. Settlements, payment instructions, refunds
--    and payouts reference the account by id; no request or event carries an account number.
-- 2. A product version's family, class and when cover starts are columns, no longer keys in the labels JSON,
--    which holds display text only.
-- =====================================================================

CREATE TABLE party.bank_account (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  account_number_enc         bytea NOT NULL,
  account_number_hash        char(64) NOT NULL,
  account_number_last4       char(4) NOT NULL,
  bank_code                  varchar(20) NOT NULL,
  branch_code                varchar(20),
  account_holder_name        varchar(140) NOT NULL,
  account_type               varchar(15) NOT NULL,
  currency                   char(3) NOT NULL,
  verified_at                timestamptz,
  verification_ref           varchar(60),
  verified_by                uuid,
  status                     varchar(25) NOT NULL,
  superseded_by_id           uuid,
  CONSTRAINT pk_bank_account PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_bank_account_party_id FOREIGN KEY (tenant_id, party_id) REFERENCES party.party (tenant_id, id),
  CONSTRAINT fk_bank_account_superseded_by_id FOREIGN KEY (tenant_id, superseded_by_id) REFERENCES party.bank_account (tenant_id, id),
  CONSTRAINT ck_bank_account_status CHECK (status IN ('PENDING_VERIFICATION', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT ck_bank_account_type CHECK (account_type IN ('CURRENT', 'SAVINGS', 'TRANSMISSION', 'OTHER')),
  CONSTRAINT ck_bank_account_verified CHECK ((status = 'ACTIVE') <= (verified_at IS NOT NULL AND verification_ref IS NOT NULL)),
  CONSTRAINT ck_bank_account_superseded CHECK ((status = 'SUPERSEDED') = (superseded_by_id IS NOT NULL))
);
-- One current account per party and currency, checked at commit: a change inserts the new account and then supersedes
-- the old one (which points to it) in the same transaction.
ALTER TABLE party.bank_account ADD CONSTRAINT ex_bank_account_current
  EXCLUDE USING btree (tenant_id WITH =, party_id WITH =, currency WITH =) WHERE (status <> 'SUPERSEDED') DEFERRABLE INITIALLY DEFERRED;
CREATE INDEX ix_bank_account_hash ON party.bank_account (tenant_id, account_number_hash);
ALTER TABLE party.bank_account ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.bank_account FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.bank_account
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE party.bank_account IS 'A party''s bank accounts. Added in Phase 5 (a gap in the original specification). Account numbers are encrypted; a change is a new row.';
COMMENT ON COLUMN party.bank_account.account_number_enc IS 'AES-256-GCM, by the application''s field protector. Never logged or published.';
-- Grants: db/roles/module_grants.sql gives insys_party its schema's tables.

-- What is paid is the account, by id; the approval covers the id and the account number's keyed hash.
ALTER TABLE general.claim ADD COLUMN settlement_bank_account_id uuid;
ALTER TABLE claims.claim_payment_instruction
  ADD COLUMN bank_account_id uuid,
  ADD COLUMN pending_bank_account_id uuid;
ALTER TABLE billing.refund_request ADD COLUMN payer_party_id uuid, ADD COLUMN bank_account_id uuid, ADD COLUMN payee_bank_hash varchar(64);
ALTER TABLE payment.payment ADD COLUMN bank_account_id uuid;

-- Product settings as columns. Existing labels are carried over, then removed from the JSON.
ALTER TABLE product.cfg_product_definitions_and_versions ADD COLUMN family_code varchar(15);
ALTER TABLE product.cfg_product_definitions_and_versions ADD COLUMN class_code varchar(15);
ALTER TABLE product.cfg_product_definitions_and_versions ADD COLUMN cover_starts_on varchar(15) NOT NULL DEFAULT 'ISSUE';
ALTER TABLE product.cfg_product_definitions_and_versions ADD CONSTRAINT cfg_product_definitions_and_versions_ck2 CHECK (cover_starts_on IN ('ISSUE', 'FIRST_PAYMENT'));
-- The static data guard allows only record_status and effective_to to change on approved rows, so it is set aside
-- for this one-off move of existing values.
ALTER TABLE product.cfg_product_definitions_and_versions DISABLE TRIGGER USER;
UPDATE product.cfg_product_definitions_and_versions
SET family_code = labels ->> 'family',
    class_code = labels ->> 'class',
    cover_starts_on = coalesce(labels ->> 'cover_starts_on', 'ISSUE'),
    labels = nullif(labels - 'family' - 'class' - 'cover_starts_on', '{}'::jsonb)
WHERE labels ?| ARRAY['family', 'class', 'cover_starts_on'];
ALTER TABLE product.cfg_product_definitions_and_versions ENABLE TRIGGER USER;
