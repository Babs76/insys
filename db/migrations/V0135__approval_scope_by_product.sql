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
-- V0135: scope batch 1 (F103; OQ-173, OQ-176). An approval about a record scoped by product alone, which has no organisation node of its own (a product
-- version), is judged by the checker's product scope only: a branch-limited checker isn't refused it for want of a node. Existing approvals keep the full rule.
ALTER TABLE workflow.approval ADD COLUMN org_node_applies boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN workflow.approval.org_node_applies IS 'False for a record scoped by product only (a product version, OQ-173): the checker''s organisation scope does not apply.';
