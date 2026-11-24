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
-- V0150: the money group (Ade, 24 November 2026).
--  * DE-008: a refund whose payout failed is PAYMENT_FAILED, with the failed payment's reference and reason. It is paid again only on a
--    second person's PAYOUT_RETRY approval (each retry one more payout attempt, to the same or another of the payer's payable accounts), or
--    CANCELLED, its money staying with the account as credit.
--  * DE-010 (OQ-058): a credit note whose adjustment reason requires approval waits as a credit_note_request for its CREDIT_NOTE approval,
--    then is issued for what the invoice still asks for (never more), lapses when nothing is, or is cleared when not granted.
-- =====================================================================
ALTER TABLE billing.refund_request DROP CONSTRAINT ck_refund_request_status;
ALTER TABLE billing.refund_request ALTER COLUMN status TYPE varchar(15);
ALTER TABLE billing.refund_request
  ADD CONSTRAINT ck_refund_request_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'PAID', 'PAYMENT_FAILED', 'CANCELLED')),
  ADD COLUMN payout_attempt integer NOT NULL DEFAULT 1,
  ADD COLUMN failed_payment_id uuid,
  ADD COLUMN failed_payment_ref varchar(25),
  ADD COLUMN failure_reason varchar(200),
  ADD COLUMN retry_pending boolean NOT NULL DEFAULT false,
  ADD COLUMN retry_requested_by uuid,
  ADD COLUMN retry_bank_account_id uuid,
  ADD COLUMN retry_payee_bank_hash varchar(64),
  ADD COLUMN retry_reason varchar(200),
  ADD COLUMN retry_decided_by uuid,
  ADD COLUMN retry_approval_id uuid,
  ADD COLUMN cancel_reason varchar(200),
  ADD COLUMN cancelled_by uuid,
  ADD CONSTRAINT ck_refund_request_attempt CHECK (payout_attempt >= 1),
  ADD CONSTRAINT ck_refund_request_failed CHECK (status <> 'PAYMENT_FAILED' OR failed_payment_ref IS NOT NULL),
  ADD CONSTRAINT ck_refund_request_retry CHECK (NOT retry_pending OR (status = 'PAYMENT_FAILED' AND retry_requested_by IS NOT NULL AND retry_bank_account_id IS NOT NULL
                                                                      AND retry_reason IS NOT NULL)),
  ADD CONSTRAINT ck_refund_request_retry_checker CHECK (retry_decided_by IS NULL OR retry_decided_by <> retry_requested_by),
  ADD CONSTRAINT ck_refund_request_cancelled CHECK ((status = 'CANCELLED') = (cancel_reason IS NOT NULL AND cancelled_by IS NOT NULL));
COMMENT ON COLUMN billing.refund_request.payout_attempt IS 'DE-008: 1 for the first payout, one more for each PAYOUT_RETRY granted.';
COMMENT ON COLUMN billing.refund_request.failed_payment_ref IS 'DE-008: the last failed payout''s reference, which a retry or cancellation must be given, typed.';
COMMENT ON COLUMN billing.refund_request.retry_pending IS 'DE-008: a PAYOUT_RETRY approval is waiting.';

CREATE TABLE billing.credit_note_request (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  invoice_id                 uuid NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  reason_code                varchar(15) NOT NULL,
  status                     varchar(10) NOT NULL,
  requested_by               uuid NOT NULL,
  requested_at               timestamptz NOT NULL,
  decided_by                 uuid,
  approval_id                uuid,
  ending                     varchar(12),
  credit_note_id             uuid,
  credited_amount            numeric(18,2),
  CONSTRAINT pk_credit_note_request PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_credit_note_request_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES billing.invoice (tenant_id, id),
  CONSTRAINT fk_credit_note_request_credit_note FOREIGN KEY (tenant_id, credit_note_id) REFERENCES billing.invoice (tenant_id, id),
  CONSTRAINT ck_credit_note_request_status CHECK (status IN ('PENDING', 'ISSUED', 'LAPSED', 'REJECTED')),
  CONSTRAINT ck_credit_note_request_amount CHECK (amount > 0 AND (credited_amount IS NULL OR (credited_amount >= 0 AND credited_amount <= amount))),
  CONSTRAINT ck_credit_note_request_checker CHECK (decided_by IS NULL OR decided_by <> requested_by),
  CONSTRAINT ck_credit_note_request_issued CHECK ((status = 'ISSUED') = (credit_note_id IS NOT NULL)),
  CONSTRAINT ck_credit_note_request_ending CHECK ((status = 'REJECTED') = (ending IS NOT NULL))
);
CREATE INDEX ix_credit_note_request_invoice ON billing.credit_note_request (tenant_id, invoice_id);
COMMENT ON TABLE billing.credit_note_request IS 'DE-010 (OQ-058, Ade, 24 November 2026): a credit note whose reason requires approval, waiting for or decided by its CREDIT_NOTE approval.';
ALTER TABLE billing.credit_note_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing.credit_note_request FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing.credit_note_request
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
