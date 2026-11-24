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
-- V0152: commission statements that pay nothing, or less than nothing, and statements whose payout failed (Ade, 24 November 2026;
-- OQ-062 negative statements, ADR-014, DE-008).
-- 1. A statement approved with a net amount of exactly zero closes as CLOSED_NIL: no payout.
-- 2. One approved with a negative net is CARRIED_FORWARD on the business date it was approved (carried_on): what the intermediary owes
--    (carried_amount, positive) is an opening line on its next statements (opening_amount there), each taking what its commission
--    covers (carry_on_statement_id, carry_on_statement_amount while that statement waits for approval and payment; carry_settled_amount
--    once it is paid or closed). Absorbed in full, it is SETTLED_BY_CARRY_FORWARD.
-- 3. A carried amount still owed may be invoiced to the intermediary (COMMISSION_CLAWBACK_INVOICE, a second person in Workflow): once
--    the intermediary is terminated, or once it has been owed for the tenant's clawback_invoice_after_days (default 90, no row) on
--    commission.cfg_broker_change_settings. Granted, the statement is INVOICED (invoiced_on, invoiced_amount) and Billing invoices it.
-- 4. DE-008: a statement whose payout failed is PAYMENT_FAILED with the failed payment's reference (failed_payment_ref); a retry
--    (PAYOUT_RETRY) returns it to APPROVED with the next payout_attempt.
-- pending_action is the action waiting for Workflow's decision (PAYOUT_RETRY or COMMISSION_CLAWBACK_INVOICE), with who asked, when and
-- why; action_approval_id is the approval that last decided one, so a redelivered decision changes nothing.
-- Statements already APPROVED with a net of zero or less (the dead end the sweep of 24 November 2026 found) are moved to the state they
-- now reach, and their payable entitlements are paid, as the carry-forward or nil close settles them.
-- =====================================================================

ALTER TABLE commission.commission_statement
  ALTER COLUMN status TYPE varchar(25),
  DROP CONSTRAINT ck_commission_statement_status,
  ADD CONSTRAINT ck_commission_statement_status CHECK (status IN ('DRAFT', 'APPROVED', 'PAID', 'CLOSED_NIL', 'CARRIED_FORWARD', 'SETTLED_BY_CARRY_FORWARD',
    'INVOICED', 'PAYMENT_FAILED')),
  ADD COLUMN opening_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN carried_on date,
  ADD COLUMN carried_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN carry_settled_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN carry_on_statement_id uuid,
  ADD COLUMN carry_on_statement_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN invoiced_on date,
  ADD COLUMN invoiced_amount numeric(18,2),
  ADD COLUMN failed_payment_ref varchar(25),
  ADD COLUMN payout_attempt integer NOT NULL DEFAULT 1,
  ADD COLUMN pending_action varchar(30),
  ADD COLUMN pending_requested_at timestamptz,
  ADD COLUMN pending_requested_by uuid,
  ADD COLUMN pending_reason varchar(500),
  ADD COLUMN action_approval_id uuid,
  ADD CONSTRAINT ck_commission_statement_carry CHECK (opening_amount >= 0 AND carried_amount >= 0 AND carry_settled_amount >= 0 AND carry_on_statement_amount >= 0
    AND carry_settled_amount + carry_on_statement_amount <= carried_amount AND (carry_on_statement_id IS NULL) = (carry_on_statement_amount = 0)),
  ADD CONSTRAINT ck_commission_statement_carried CHECK (status NOT IN ('CARRIED_FORWARD', 'SETTLED_BY_CARRY_FORWARD', 'INVOICED')
    OR (carried_on IS NOT NULL AND carried_amount > 0)),
  ADD CONSTRAINT ck_commission_statement_invoiced CHECK ((status = 'INVOICED') = (invoiced_on IS NOT NULL AND invoiced_amount > 0)),
  ADD CONSTRAINT ck_commission_statement_failed CHECK (status <> 'PAYMENT_FAILED' OR failed_payment_ref IS NOT NULL),
  ADD CONSTRAINT ck_commission_statement_attempt CHECK (payout_attempt >= 1),
  ADD CONSTRAINT ck_commission_statement_pending CHECK ((pending_action IS NULL OR pending_action IN ('PAYOUT_RETRY', 'COMMISSION_CLAWBACK_INVOICE'))
    AND (pending_action IS NULL) = (pending_requested_at IS NULL) AND (pending_action IS NULL) = (pending_requested_by IS NULL)),
  ADD CONSTRAINT fk_commission_statement_carry_on FOREIGN KEY (tenant_id, carry_on_statement_id) REFERENCES commission.commission_statement (tenant_id, id);

CREATE INDEX ix_commission_statement_carried ON commission.commission_statement (tenant_id, intermediary_code, currency, carried_on) WHERE status = 'CARRIED_FORWARD';

COMMENT ON COLUMN commission.commission_statement.status IS 'DRAFT, APPROVED, PAID, CLOSED_NIL (approved with a net of zero), CARRIED_FORWARD (approved with a negative net), SETTLED_BY_CARRY_FORWARD (what it carried was absorbed by later statements), INVOICED (what it carried was invoiced to the intermediary) or PAYMENT_FAILED (its payout failed; DE-008) (V0152).';
COMMENT ON COLUMN commission.commission_statement.opening_amount IS 'V0152: the opening line: what this statement takes of earlier carried-forward statements of the intermediary, in its gross.';
COMMENT ON COLUMN commission.commission_statement.carried_on IS 'V0152: the business date the statement was approved with a negative net and carried forward; its age is measured from it.';
COMMENT ON COLUMN commission.commission_statement.carried_amount IS 'V0152: what the intermediary owes from this statement, the negative net as a positive amount.';
COMMENT ON COLUMN commission.commission_statement.carry_settled_amount IS 'V0152: what later statements, paid or closed, have absorbed of carried_amount.';
COMMENT ON COLUMN commission.commission_statement.carry_on_statement_id IS 'V0152: the later statement that takes part of carried_amount while it waits for approval and payment.';
COMMENT ON COLUMN commission.commission_statement.carry_on_statement_amount IS 'V0152: what carry_on_statement_id takes.';
COMMENT ON COLUMN commission.commission_statement.invoiced_on IS 'V0152: the business date the carried amount still owed was invoiced to the intermediary (COMMISSION_CLAWBACK_INVOICE granted).';
COMMENT ON COLUMN commission.commission_statement.invoiced_amount IS 'V0152: the amount invoiced: carried_amount less carry_settled_amount at the grant.';
COMMENT ON COLUMN commission.commission_statement.failed_payment_ref IS 'V0152, DE-008: the reference (payment.payment.payment_ref) of the payout that failed; the person retrying types it.';
COMMENT ON COLUMN commission.commission_statement.payout_attempt IS 'V0152, DE-008: 1 for the first payout, 2 or more for each granted retry (PayoutRequested.Attempt).';
COMMENT ON COLUMN commission.commission_statement.pending_action IS 'V0152: PAYOUT_RETRY or COMMISSION_CLAWBACK_INVOICE while that action waits for Workflow''s decision.';
COMMENT ON COLUMN commission.commission_statement.pending_reason IS 'V0152: why the pending action was asked for, as typed (plain text, no personal data).';
COMMENT ON COLUMN commission.commission_statement.action_approval_id IS 'V0152: the Workflow approval that last decided a pending action, so a redelivered decision changes nothing.';

-- The tenant's N (Ade: default 90): how long a carried amount is owed before it may be invoiced, with the other commission settings.
ALTER TABLE commission.cfg_broker_change_settings ADD COLUMN clawback_invoice_after_days integer;
ALTER TABLE commission.cfg_broker_change_settings ADD CONSTRAINT cfg_broker_change_settings_ck2 CHECK (clawback_invoice_after_days BETWEEN 1 AND 3650);
COMMENT ON TABLE commission.cfg_broker_change_settings IS 'Commission settings of the tenant (named for the first). CFG, scope T, Tier 1. What happens to commission already paid when a broker of record changes (B-009): RECOVER (the default, with no row) or KEEP; and after how many days a carried-forward statement''s amount still owed may be invoiced to the intermediary (V0152, Ade 24 November 2026: 90 with no row or no value). Module: Distribution; sub-module: Commission. Proposed owner role: FIN. Provenance: B-009, OQ-062.';
COMMENT ON COLUMN commission.cfg_broker_change_settings.clawback_invoice_after_days IS 'clawback_invoice_after_days: days a carried-forward commission amount is owed before it may be invoiced (V0152); empty means 90. Sample: 90';

-- The dead end found by the sweep: approved, nothing to pay, never Paid. Nil closes; negative carries forward from the day it was approved.
UPDATE commission.commission_entitlement e SET status = 'PAID', row_version = e.row_version + 1, updated_at = now(), updated_by = 'V0152'
FROM commission.commission_statement s
WHERE s.tenant_id = e.tenant_id AND s.id = e.statement_id AND s.status = 'APPROVED' AND s.net_amount <= 0 AND e.status = 'PAYABLE';
UPDATE commission.commission_statement SET status = 'CLOSED_NIL', row_version = row_version + 1, updated_at = now(), updated_by = 'V0152'
WHERE status = 'APPROVED' AND net_amount = 0;
UPDATE commission.commission_statement SET status = 'CARRIED_FORWARD', carried_on = updated_at::date, carried_amount = -net_amount, row_version = row_version + 1,
  updated_at = now(), updated_by = 'V0152'
WHERE status = 'APPROVED' AND net_amount < 0;
