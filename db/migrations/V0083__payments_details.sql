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
-- V0083: what the payments screens need to show (BR-051, BR-052, BR-053): a payment's failure reason, a payout batch's
-- build time and release approval, and when a reconciliation exception was raised and resolved. Build and raise times
-- of existing rows are taken from when they were created; earlier failures, approvals and resolutions stay unknown.
-- =====================================================================
ALTER TABLE payment.payment ADD COLUMN failure_reason varchar(200);
COMMENT ON COLUMN payment.payment.failure_reason IS 'BR-053: why the payment failed, as the provider, its callback or the operator gave it; null unless failed.';

ALTER TABLE payment.payout_batch ADD COLUMN built_at timestamptz;
ALTER TABLE payment.payout_batch ADD COLUMN approval_id uuid;
UPDATE payment.payout_batch SET built_at = created_at;
COMMENT ON COLUMN payment.payout_batch.built_at IS 'BR-051: when the batch was built.';
COMMENT ON COLUMN payment.payout_batch.approval_id IS 'BR-051: the Workflow approval that approved the batch''s release; null until approved.';

ALTER TABLE payment.reconciliation_exception ADD COLUMN raised_at timestamptz;
ALTER TABLE payment.reconciliation_exception ADD COLUMN resolved_at timestamptz;
UPDATE payment.reconciliation_exception SET raised_at = created_at;
ALTER TABLE payment.reconciliation_exception ALTER COLUMN raised_at SET NOT NULL;
COMMENT ON COLUMN payment.reconciliation_exception.raised_at IS 'BR-052: when the exception was raised.';
COMMENT ON COLUMN payment.reconciliation_exception.resolved_at IS 'BR-052: when it was resolved; null until then.';
