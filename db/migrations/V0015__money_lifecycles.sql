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
-- INSYS Core · Billing, Payments and Claims framework lifecycles (Phase 3)
-- States are domain-model.json's, upper-case with underscores. Gaps found while
-- building, each recorded as OQ-058: payments did not say what they pay or which
-- payout batch they belong to, and a claim could be registered twice.
-- =====================================================================

ALTER TABLE billing.billing_account ADD CONSTRAINT ck_billing_account_status CHECK (status IN ('ACTIVE', 'IN_ARREARS', 'CLOSED'));
ALTER TABLE billing.invoice
  ADD CONSTRAINT ck_invoice_status CHECK (status IN ('DRAFT', 'ISSUED', 'PART_PAID', 'PAID', 'OVERDUE', 'CREDITED')),
  ADD CONSTRAINT ck_invoice_document_type CHECK (document_type IN ('INVOICE', 'CREDIT_NOTE')),
  ADD CONSTRAINT ck_invoice_totals CHECK (total_amount = net_amount + tax_amount AND net_amount >= 0 AND tax_amount >= 0),
  ADD CONSTRAINT ck_invoice_credit_note CHECK ((document_type = 'CREDIT_NOTE') = (reverses_invoice_id IS NOT NULL));
ALTER TABLE billing.premium_instalment ADD CONSTRAINT ck_premium_instalment_status CHECK (status IN ('SCHEDULED', 'INVOICED', 'CANCELLED'));
ALTER TABLE billing.arrears_case ADD CONSTRAINT ck_arrears_case_status CHECK (status IN ('OPEN', 'CLEARED'));
ALTER TABLE billing.refund_request ADD CONSTRAINT ck_refund_request_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'PAID'));
ALTER TABLE billing.refund_request ADD COLUMN requested_by uuid, ADD COLUMN decided_by uuid,
  ADD CONSTRAINT ck_refund_request_checker CHECK (decided_by IS NULL OR decided_by <> requested_by);

-- What a payment pays (FIN-001) and the payout batch it is released in.
ALTER TABLE payment.payment
  ADD COLUMN business_ref_type varchar(30),
  ADD COLUMN business_ref_id   uuid,
  ADD COLUMN payout_batch_id   uuid,
  ADD CONSTRAINT ck_payment_status CHECK (status IN ('INITIATED', 'PENDING', 'SUCCEEDED', 'FAILED', 'UNKNOWN', 'REVERSED')),
  ADD CONSTRAINT ck_payment_direction CHECK (direction IN ('IN', 'OUT')),
  ADD CONSTRAINT ck_payment_amount CHECK (amount > 0),
  ADD CONSTRAINT ck_payment_business_ref CHECK ((business_ref_type IS NULL) = (business_ref_id IS NULL));
CREATE INDEX ix_payment_4 ON payment.payment (tenant_id, business_ref_type, business_ref_id);
CREATE INDEX ix_payment_5 ON payment.payment (tenant_id, payout_batch_id);
ALTER TABLE payment.payment ADD CONSTRAINT fk_payment_payout_batch_id FOREIGN KEY (tenant_id, payout_batch_id) REFERENCES payment.payout_batch (tenant_id, id);
ALTER TABLE payment.payment_allocation ADD CONSTRAINT ck_payment_allocation_amount CHECK (amount > 0);
ALTER TABLE payment.payout_batch
  ADD COLUMN approved_by uuid,
  ADD COLUMN built_by    uuid,
  ADD CONSTRAINT ck_payout_batch_status CHECK (status IN ('BUILT', 'APPROVED', 'RELEASED', 'SETTLED')),
  ADD CONSTRAINT ck_payout_batch_checker CHECK (approved_by IS NULL OR approved_by <> built_by);
ALTER TABLE payment.bank_statement_line ADD CONSTRAINT ck_bank_statement_line_match CHECK (match_status IN ('UNMATCHED', 'MATCHED', 'EXCEPTION'));

-- One claim number per domain claim: a repeat registration returns the same number (CLM-005).
DROP INDEX claims.ix_claim_register_1;
CREATE UNIQUE INDEX uq_claim_register_domain_claim ON claims.claim_register (tenant_id, domain, domain_claim_id);
ALTER TABLE claims.claim_financial_movement
  ADD CONSTRAINT ck_claim_financial_movement_type CHECK (movement_type IN ('RESERVE', 'PAYMENT', 'RECOVERY'));
-- One payment instruction per claim and instruction reference (PAY-001).
ALTER TABLE claims.claim_payment_instruction ADD COLUMN instruction_ref varchar(60), ADD COLUMN approved_amount numeric(18,2),
  ADD CONSTRAINT ck_claim_payment_instruction_amount CHECK (amount > 0 AND (approved_amount IS NULL OR amount <= approved_amount));
CREATE UNIQUE INDEX uq_claim_payment_instruction_ref ON claims.claim_payment_instruction (tenant_id, claim_number, instruction_ref);

-- A business event that could not be posted is kept with the exception, so an operator can post it again
-- once the cause is fixed (FIN-007). The event's amounts and references only; no personal data.
ALTER TABLE finance.posting_exception
  ADD COLUMN business_event jsonb,
  ADD COLUMN resolved_at timestamptz,
  ADD COLUMN journal_id uuid,
  ADD CONSTRAINT ck_posting_exception_status CHECK (status IN ('OPEN', 'RESOLVED')),
  ADD CONSTRAINT ck_posting_exception_resolved CHECK ((status = 'RESOLVED') = (resolved_at IS NOT NULL));

-- What has been allocated to an invoice so far, so its settled status reflects the allocations (BIL-003).
ALTER TABLE billing.invoice ADD COLUMN allocated_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD CONSTRAINT ck_invoice_allocated CHECK (allocated_amount >= 0 AND allocated_amount <= total_amount);
-- The product version a schedule bills, so its invoices can name it (BIL-002).
ALTER TABLE billing.premium_schedule ADD COLUMN product_version_ref varchar(30) NOT NULL DEFAULT '';
ALTER TABLE billing.premium_schedule ALTER COLUMN product_version_ref DROP DEFAULT;
-- The reserve a claim payment draws from, and its attempt (a retry with new details is a new payment, PAY-001).
ALTER TABLE claims.claim_payment_instruction
  ADD COLUMN reserve_type varchar(15) NOT NULL DEFAULT 'INDEMNITY',
  ADD COLUMN attempt smallint NOT NULL DEFAULT 1,
  ADD COLUMN released_by uuid,
  ADD COLUMN drafted_by uuid;
ALTER TABLE claims.claim_payment_instruction ALTER COLUMN reserve_type DROP DEFAULT;
ALTER TABLE claims.claim_evidence ADD CONSTRAINT ck_claim_evidence_status CHECK (status IN ('REQUESTED', 'RECEIVED'));
