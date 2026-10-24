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
-- V0049: INSYS creates the Keycloak users of its own realm (docs/specs/addenda/keycloak-user-provisioning.md, signed off
-- 24 October 2026). A user provisioned in realm insys has an email (Keycloak sends the invitation there) and waits in
-- PENDING_IDENTITY, unable to sign in, until its Keycloak user is created and linked. A first-administrator request for
-- realm insys gives an email instead of an external subject.
-- =====================================================================
ALTER TABLE iam.user_account ADD COLUMN email varchar(254);
ALTER TABLE iam.user_account ALTER COLUMN status TYPE varchar(20);
ALTER TABLE iam.user_account
  ADD CONSTRAINT ck_user_account_status CHECK (status IN ('PENDING_IDENTITY', 'INVITED', 'ACTIVE', 'LOCKED', 'DISABLED')),
  ADD CONSTRAINT ck_user_account_identity CHECK (status = 'PENDING_IDENTITY' OR external_subject IS NOT NULL);
COMMENT ON COLUMN iam.user_account.email IS 'Where Keycloak sends the invitation, for users of INSYS''s own realm. Personal data: never logged or put in events.';

ALTER TABLE iam.tenant_administrator_request ADD COLUMN email varchar(254);
ALTER TABLE iam.tenant_administrator_request ALTER COLUMN external_subject DROP NOT NULL;
ALTER TABLE iam.tenant_administrator_request
  ADD CONSTRAINT ck_tenant_administrator_request_identity CHECK (external_subject IS NOT NULL OR email IS NOT NULL);
