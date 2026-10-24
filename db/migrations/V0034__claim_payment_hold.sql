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
-- V0034: a held claim payment instruction (Phase 8 Wave 0). An automatic release that is refused (the reserve would go
-- below zero, or the payee's bank account changed after approval) no longer stops silently: the instruction stays
-- APPROVED with the reason, the detail a person needs to put it right, and when; the claims role gets a Workflow task.
-- =====================================================================
ALTER TABLE claims.claim_payment_instruction
  ADD COLUMN held_reason varchar(30),
  ADD COLUMN held_detail varchar(200),
  ADD COLUMN held_at timestamptz,
  ADD CONSTRAINT ck_claim_payment_instruction_held CHECK ((held_reason IS NULL) = (held_at IS NULL));
