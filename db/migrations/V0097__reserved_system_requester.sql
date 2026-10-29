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
-- =====================================================================
-- V0097: BR-109 (Ade, 30 October 2026). The system requester (00000000-0000-0000-0000-00000000005e) names the platform as
-- the requester of an approval raised with no person acting. It is reserved: no user account may ever have it, so it can
-- never be assigned a role, sign in, or decide anything.
-- =====================================================================

ALTER TABLE iam.user_account
  ADD CONSTRAINT ck_user_account_not_system_requester CHECK (id <> '00000000-0000-0000-0000-00000000005e'::uuid);

-- One waiting approval per record (BR-109: a re-raised enhanced due diligence approval never stands beside the one it
-- replaces). Workflow already treats a second request for a record with one waiting as a duplicate; this makes it hold
-- when two requests arrive at once.
CREATE UNIQUE INDEX ux_approval_one_requested ON workflow.approval (tenant_id, business_ref_type, business_ref_id) WHERE status = 'REQUESTED';
