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
-- V0151: the money group, Health and case redress (Ade, 24 November 2026). Each module alters only its own schema.
-- DE-008, Health: a remittance's payment status (NONE for an advice with no payment, PENDING, PAID, FAILED), the failed payment's reference and
--   reason (recorded from payment.PaymentFailed), the payout attempt, and a reissue waiting for a second person (approval type PAYOUT_RETRY). A run
--   reaches RECONCILED only when every remittance with an amount is PAID.
-- DE-009, Health: the credit notes Billing issues on a recovery's invoice, as the recovery's insert-only history of credits.
-- DE-008, Workflow: a redress whose payout failed is PAYMENT_FAILED (14 characters, so status grows from 12), with the failed payment's reference,
--   the payout attempt and a retry waiting for a second person (PAYOUT_RETRY); the steps go to the case's history.
-- Business references and money only: no clinical or identity data.
-- =====================================================================

ALTER TABLE health.remittance_advice
  ADD COLUMN payment_status          varchar(10) NOT NULL DEFAULT 'PENDING',
  ADD COLUMN attempt                 smallint NOT NULL DEFAULT 1,
  ADD COLUMN failed_payment_id       uuid,
  ADD COLUMN failed_payment_ref      varchar(25),
  ADD COLUMN failure_reason          varchar(200),
  ADD COLUMN payout_bank_account_id  uuid,
  ADD COLUMN reissue_requested_by    uuid,
  ADD COLUMN reissue_reason          varchar(500),
  ADD COLUMN reissue_bank_account_id uuid,
  ADD COLUMN reissue_approval_id     uuid;
UPDATE health.remittance_advice SET payment_status = CASE WHEN payment_id IS NOT NULL THEN 'PAID' WHEN amount = 0 THEN 'NONE' ELSE 'PENDING' END;
ALTER TABLE health.remittance_advice
  ADD CONSTRAINT ck_remittance_advice_payment_status CHECK (payment_status IN ('NONE', 'PENDING', 'PAID', 'FAILED')),
  ADD CONSTRAINT ck_remittance_advice_attempt CHECK (attempt >= 1),
  ADD CONSTRAINT ck_remittance_advice_failed CHECK (payment_status <> 'FAILED' OR failed_payment_ref IS NOT NULL),
  ADD CONSTRAINT ck_remittance_advice_reissue CHECK (reissue_requested_by IS NULL OR reissue_reason IS NOT NULL);
COMMENT ON COLUMN health.remittance_advice.payment_status IS 'DE-008: NONE (offsets took all of it, gap 2), PENDING, PAID, or FAILED until a reissue a second person approves (PAYOUT_RETRY).';
COMMENT ON COLUMN health.remittance_advice.failed_payment_ref IS 'DE-008: the last failed payment''s reference, which reissuing the remittance asks the person to type.';
COMMENT ON COLUMN health.remittance_advice.attempt IS 'DE-008: the payout attempt, 1 for the run''s own and 2 or more for each reissue (payment.PayoutRequested).';

CREATE TABLE health.provider_recovery_credit (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  recovery_id                uuid NOT NULL,
  invoice_id                 uuid NOT NULL,
  credit_note_id             uuid NOT NULL,
  credit_note_number         varchar(20) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  invoice_outstanding        numeric(18,2) NOT NULL,
  settled_amount             numeric(18,2) NOT NULL,
  balance_before             numeric(18,2) NOT NULL,
  balance_after              numeric(18,2) NOT NULL,
  outcome                    varchar(10) NOT NULL,
  credited_on                date NOT NULL,
  CONSTRAINT pk_provider_recovery_credit PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_provider_recovery_credit FOREIGN KEY (tenant_id, recovery_id) REFERENCES health.provider_recovery (tenant_id, id),
  CONSTRAINT uq_provider_recovery_credit UNIQUE (tenant_id, recovery_id, credit_note_id),
  CONSTRAINT ck_provider_recovery_credit_amounts CHECK (amount > 0 AND invoice_outstanding >= 0 AND settled_amount >= 0 AND balance_before >= 0 AND balance_after >= 0),
  CONSTRAINT ck_provider_recovery_credit_outcome CHECK (outcome IN ('PARTIAL', 'WITHDRAWN')),
  CONSTRAINT ck_provider_recovery_credit_partial CHECK (outcome <> 'PARTIAL' OR (invoice_outstanding > 0 AND settled_amount = 0))
);
CREATE INDEX ix_provider_recovery_credit_1 ON health.provider_recovery_credit (tenant_id, recovery_id);
COMMENT ON TABLE health.provider_recovery_credit IS 'DE-009: each Billing credit note on a recovery''s invoice. PARTIAL lowered the balance; WITHDRAWN (nothing outstanding) settled what the invoice collected and left the rest owed, the recovery OPEN again. Insert-only.';
ALTER TABLE health.provider_recovery_credit ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.provider_recovery_credit FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.provider_recovery_credit
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER provider_recovery_credit_immutable BEFORE UPDATE OR DELETE ON health.provider_recovery_credit
  FOR EACH ROW EXECUTE FUNCTION health.refuse_history_change();

ALTER TABLE workflow.case_redress
  ALTER COLUMN status TYPE varchar(14),
  DROP CONSTRAINT ck_case_redress_status,
  ADD CONSTRAINT ck_case_redress_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'PAID', 'PAYMENT_FAILED')),
  ADD COLUMN attempt                 smallint NOT NULL DEFAULT 1,
  ADD COLUMN failed_payment_id       uuid,
  ADD COLUMN failed_payment_ref      varchar(25),
  ADD COLUMN failure_reason          varchar(200),
  ADD COLUMN payout_bank_account_id  uuid,
  ADD COLUMN retry_requested_by      uuid,
  ADD COLUMN retry_reason            varchar(500),
  ADD COLUMN retry_bank_account_id   uuid,
  ADD COLUMN retry_approval_id       uuid,
  ADD CONSTRAINT ck_case_redress_attempt CHECK (attempt >= 1),
  ADD CONSTRAINT ck_case_redress_failed CHECK (status <> 'PAYMENT_FAILED' OR failed_payment_ref IS NOT NULL),
  ADD CONSTRAINT ck_case_redress_retry CHECK (retry_requested_by IS NULL OR (retry_reason IS NOT NULL AND retry_bank_account_id IS NOT NULL));
COMMENT ON COLUMN workflow.case_redress.failed_payment_ref IS 'DE-008: the last failed payment''s reference, which paying the redress again asks the person to type.';

ALTER TABLE workflow.case_event
  DROP CONSTRAINT ck_case_event_type,
  ADD CONSTRAINT ck_case_event_type CHECK (event_type IN ('RECEIVED', 'ACKNOWLEDGED', 'ASSIGNED', 'UPDATED', 'LINKED', 'MESSAGE_SENT', 'MESSAGE_RECEIVED',
    'EXTENDED', 'ESCALATED', 'AWAITING_COMPLAINANT', 'RESUMED', 'DECIDED', 'REDRESS_REQUESTED', 'REDRESS_APPROVED', 'REDRESS_REJECTED', 'REDRESS_PAID',
    'REDRESS_PAYMENT_FAILED', 'REDRESS_RETRY_REQUESTED', 'REDRESS_RETRY_APPROVED', 'REDRESS_RETRY_REJECTED',
    'REFERRED_TO_OMBUD', 'OMBUD_OUTCOME', 'SHARED_WITH_BROKER', 'REOPENED', 'WITHDRAWN', 'CLOSED', 'LEGAL_HOLD_PLACED', 'LEGAL_HOLD_RELEASED'));
