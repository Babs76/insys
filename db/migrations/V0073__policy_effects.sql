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
-- V0073: the records a General policy's issue sets off in other modules, as they arrive (BR-038): its invoices
-- (Billing's InvoiceIssued), its documents (Documents' DocumentGenerated) and its reinsurance cessions (Reinsurance's
-- CessionCalculated). General records each once, from the event; the policy read lists them, so a client sees the issue's
-- effects land without polling each module.
-- =====================================================================
CREATE TABLE general.policy_effect (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  policy_id                  uuid NOT NULL,
  effect_type                varchar(20) NOT NULL,
  record_id                  uuid NOT NULL,
  CONSTRAINT pk_policy_effect PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_policy_effect_record UNIQUE (tenant_id, effect_type, record_id),
  CONSTRAINT ck_policy_effect_type CHECK (effect_type IN ('INVOICE', 'DOCUMENT', 'CESSION'))
);
CREATE INDEX ix_policy_effect_policy ON general.policy_effect (tenant_id, policy_id);
ALTER TABLE general.policy_effect ADD CONSTRAINT fk_policy_effect_policy_id FOREIGN KEY (tenant_id, policy_id) REFERENCES general.policy (tenant_id, id);
COMMENT ON TABLE general.policy_effect IS 'BR-038: a record another module made for a General policy (an invoice, a document or a cession), recorded from its event.';

ALTER TABLE general.policy_effect ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.policy_effect FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.policy_effect
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
