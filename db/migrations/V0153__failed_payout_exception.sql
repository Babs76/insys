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
-- V0153: DE-008 and DE-031 (Ade, 24 November 2026). A failed outbound payment raises a reconciliation exception of type PAYMENT_FAILED (PAY-005),
-- which INSYS resolves itself once the record it paid is paid by a later attempt, or its module gives the payout up; the resolution says which.
-- =====================================================================
ALTER TABLE payment.reconciliation_exception ADD COLUMN resolution varchar(300);
COMMENT ON COLUMN payment.reconciliation_exception.resolution IS
  'DE-008: why INSYS resolved the exception itself (paid by a later attempt, or the payout given up, with the module''s reason); null when a person resolved it.';
