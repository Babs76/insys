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
-- V0069: a party's identifier shows its last four characters, masked ("•••• 1234"), as a bank account does (BR-026,
-- DBA-014). The column is written when an identifier is added. Identifiers added before it have none: the number is
-- encrypted in the application, so SQL cannot fill it, and the application reads the last four from the number instead.
-- =====================================================================
ALTER TABLE party.party_identifier ADD COLUMN number_last4 varchar(4);

COMMENT ON COLUMN party.party_identifier.number_last4 IS
  'The last four characters of the number, for its masked display (BR-026, DBA-014); empty when the number has four or fewer, so the masked form never shows it whole. NULL for identifiers added before V0069.';
