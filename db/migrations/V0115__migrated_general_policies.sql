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
-- OQ-158, B-013: which migration batch a policy came from, and its number in the source system. Null for a policy INSYS issued.
ALTER TABLE general.policy ADD COLUMN migration_batch varchar(30), ADD COLUMN source_reference varchar(40);
CREATE UNIQUE INDEX uq_general_policy_source ON general.policy (tenant_id, source_reference) WHERE source_reference IS NOT NULL;
