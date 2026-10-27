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
-- V0065: two money controls (Ade, 27 October 2026).
-- * A payout batch keeps the hash of what its release approval covers (each payment's id, amount, currency, payee and
--   bank account, as built), so a release is refused if the batch changed after approval. Null for batches built before.
-- * BR-042: resolving an Unknown payment as succeeded without a matched bank statement line is a request (who, when, the
--   evidence) that waits for a second person's UNKNOWN_PAYMENT_RESOLUTION approval; the approval that resolved it is kept.
-- =====================================================================

ALTER TABLE payment.payout_batch
  ADD COLUMN subject_hash varchar(64);
COMMENT ON COLUMN payment.payout_batch.subject_hash IS 'What the release approval covers (ApprovalSubject); ReleaseBatch refuses a batch whose payments no longer match it.';

ALTER TABLE payment.payment
  ADD COLUMN resolution_requested_by uuid,
  ADD COLUMN resolution_requested_at timestamptz,
  ADD COLUMN resolution_evidence varchar(500),
  ADD COLUMN resolution_approval_id uuid,
  ADD CONSTRAINT ck_payment_resolution_request CHECK (
    (resolution_requested_by IS NULL) = (resolution_requested_at IS NULL) AND (resolution_requested_by IS NULL) = (resolution_evidence IS NULL)),
  ADD CONSTRAINT ck_payment_resolution_pending CHECK (resolution_requested_by IS NULL OR status = 'UNKNOWN');
COMMENT ON COLUMN payment.payment.resolution_requested_by IS 'BR-042: who asked for this Unknown payment to be resolved as succeeded, while a second person''s approval is awaited.';
COMMENT ON COLUMN payment.payment.resolution_approval_id IS 'BR-042: the UNKNOWN_PAYMENT_RESOLUTION approval that resolved the payment as succeeded.';
