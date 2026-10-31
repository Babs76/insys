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
-- V0113: Wave 6 batch 6.3 (addenda/finance-and-compliance.md §4.8, §4.9; F058, F076; OQ-158, OQ-160). Fraud cases for Life and General, as Health's, seen only by holders of the
-- fraud function; bulk jobs for parties, all or nothing.
-- =====================================================================
-- OQ-054 (Ade, 31 October 2026): a party with no policy, membership, claim or case in force or open is dormant, and its retention counts from the last one ending.
ALTER TABLE party.party ADD COLUMN dormancy_checked_on date;
CREATE INDEX ix_party_dormancy_check ON party.party (tenant_id, dormancy_checked_on NULLS FIRST, id) WHERE status IN ('ACTIVE', 'DORMANT');

-- A case now keeps a party's data, so it needs a period: the same working default as the others, for compliance to confirm.
ALTER TABLE party.cfg_record_retention_policies DROP CONSTRAINT cfg_record_retention_policies_ck1;
ALTER TABLE party.cfg_record_retention_policies ADD CONSTRAINT cfg_record_retention_policies_ck1 CHECK (record_kind IN ('POLICY', 'CLAIM', 'PAYMENT', 'CASE'));
INSERT INTO party.cfg_record_retention_policies
  (tenant_id, code, version, effective_from, record_status, record_source, created_by, change_reason, record_kind, jurisdiction, period, basis)
VALUES (NULL, 'DEFAULT-CASE', 1, DATE '2026-10-01', 'EFFECTIVE', 'OQ-120 working default', 'migration (V0114)',
        'OQ-054: cases count towards dormancy and retention; working default, for compliance to confirm before go-live', 'CASE', 'ZA', interval '7 years',
        'Working default: the period the other record kinds have');
