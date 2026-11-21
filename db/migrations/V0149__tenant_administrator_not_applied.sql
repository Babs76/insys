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
-- V0149: OQ-099 follow-up (Ade, 21 November 2026): a tenant administrator request whose grant in Workflow cannot be applied (the tenant already
-- has users, its TENANT_ADMIN role is no longer in force, a username is taken, or its functions hold both sides of a platform baseline
-- segregation-of-duties rule) ends NOT_APPLIED, a final state, with the reason, instead of staying PENDING_APPROVAL for ever. decided_by is the
-- approval's checker, who differs from requested_by (ck_tenant_administrator_request_checker, unchanged), and decided_at is set
-- (ck_tenant_administrator_request_decided, unchanged). A NOT_APPLIED request no longer blocks a new one: uq_tenant_administrator_request_pending
-- covers PENDING_APPROVAL only.
-- The parked item of such a grant now names the request as well as the approval, "TENANT_ADMINISTRATOR:{requestId}:{approvalId}" (94
-- characters), so integration.parked_delivery.prerequisite_item grows from 80 to 120 characters.
-- =====================================================================

ALTER TABLE iam.tenant_administrator_request
  DROP CONSTRAINT ck_tenant_administrator_request_status,
  ADD CONSTRAINT ck_tenant_administrator_request_status CHECK (status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'NOT_APPLIED')),
  ADD COLUMN not_applied_reason varchar(500),
  ADD CONSTRAINT ck_tenant_administrator_request_not_applied CHECK ((status = 'NOT_APPLIED') = (not_applied_reason IS NOT NULL));

COMMENT ON COLUMN iam.tenant_administrator_request.status IS 'PENDING_APPROVAL, APPROVED, REJECTED or NOT_APPLIED. An APPROVED request reads as AwaitingIdentity, IdentityParked, IdentityFailed or Provisioned from its people''s identities (BR-018); NOT_APPLIED is a grant that could not be applied (V0149).';
COMMENT ON COLUMN iam.tenant_administrator_request.not_applied_reason IS 'OQ-099 follow-up (V0149): why a granted request could not be applied, plain text with no personal data; set exactly when status is NOT_APPLIED.';

ALTER TABLE integration.parked_delivery ALTER COLUMN prerequisite_item TYPE varchar(120);
