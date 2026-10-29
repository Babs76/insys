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
-- V0088: a workflow definition's domain validation is optional (BR-096). No module provides one for cases, and the
-- onboarding seed writes none, so a definition made through the governed API must be able to name none too: its tasks
-- then move on the transition guards alone. An empty value already meant none; it becomes NULL.
-- =====================================================================
ALTER TABLE workflow.cfg_workflow_definitions ALTER COLUMN domain_validation DROP NOT NULL;
UPDATE workflow.cfg_workflow_definitions SET domain_validation = NULL WHERE domain_validation = '';
COMMENT ON COLUMN workflow.cfg_workflow_definitions.domain_validation IS 'domain_validation: Ref Service, optional (BR-096): the owning module''s validation of a transition, or NULL for none. Sample: general.endorsement.validate';
