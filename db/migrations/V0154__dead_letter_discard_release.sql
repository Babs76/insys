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
-- V0154: DE-007 (Ade, 24 November 2026). Discarding a dead letter needs a reason and releases what it held back: its event's dead-lettered
-- deliveries in the ledger are discarded with that reason, so later events for the same records stop waiting behind them (ADR-022). A money
-- message's discard is approved by a second person first (Workflow, DEAD_LETTER_DISCARD); the request is kept on the dead letter meanwhile.
-- =====================================================================
ALTER TABLE integration.dead_letter_message
  ADD COLUMN discard_reason       varchar(300),
  ADD COLUMN discard_requested_by uuid,
  ADD COLUMN discard_requested_at timestamptz,
  ADD COLUMN discard_subject_hash varchar(64),
  ADD COLUMN discard_approved_by  uuid,
  ADD CONSTRAINT ck_dead_letter_message_discard_request CHECK (
    (discard_requested_by IS NULL) = (discard_requested_at IS NULL) AND (discard_requested_by IS NULL) = (discard_subject_hash IS NULL));

COMMENT ON COLUMN integration.dead_letter_message.discard_reason IS
  'DE-007: why the message was discarded, or why its discard was requested; never the payload. Null otherwise.';
COMMENT ON COLUMN integration.dead_letter_message.discard_requested_by IS
  'DE-007: the operator (iam.user_account) who asked to discard a money message, kept once approved; null when no discard needed approval, or after a request ended without approval. With resolution NULL, the dead letter reads DiscardRequested.';
COMMENT ON COLUMN integration.dead_letter_message.discard_requested_at IS
  'DE-007: when the discard of a money message was requested.';
COMMENT ON COLUMN integration.dead_letter_message.discard_subject_hash IS
  'DE-007: SHA-256 (hex) of what the discard approval covers (the dead letter, its message, the reason and the request time); Workflow returns it with its decision.';
COMMENT ON COLUMN integration.dead_letter_message.discard_approved_by IS
  'DE-007: the second person (iam.user_account) who approved the discard of a money message; resolved_by is the requester.';

-- The dead letter's reason (300) is kept on the deliveries it releases.
ALTER TABLE integration.parked_delivery ALTER COLUMN discard_reason TYPE varchar(300);
COMMENT ON COLUMN integration.parked_delivery.discard_reason IS
  'Why the delivery was discarded: an operator''s reason for discarding it, or (DE-007) the reason its dead letter was discarded.';
