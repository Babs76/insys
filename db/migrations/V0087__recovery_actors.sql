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
-- V0087: who billed a reinsurance recovery and who recorded its settlement (BR-049). Null when a job billed it on
-- identifying it, and for recoveries before this migration.
-- =====================================================================
ALTER TABLE reinsurance.recovery ADD COLUMN billed_by uuid;
ALTER TABLE reinsurance.recovery ADD COLUMN settled_by uuid;
COMMENT ON COLUMN reinsurance.recovery.billed_by IS 'BR-049: the user who billed the recovery; null when billed automatically.';
COMMENT ON COLUMN reinsurance.recovery.settled_by IS 'BR-049: the user who recorded the reinsurer''s settlement.';
