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
-- V0062: a tenant's administrators (front-end requests BR-013 and BR-018, decided 25-26 October 2026).
-- BR-013: a new tenant gets two first administrators, so one can approve what the other does (a privileged role, a
-- static data change); onboarding readiness needs both active. A platform operator may also request an additional
-- administrator for an existing tenant, approved by a second platform administrator, never inside the tenant.
-- BR-018: a request is Provisioned only once each administrator's identity is linked. The request's own status is
-- PENDING_APPROVAL, APPROVED or REJECTED; whether an approved request's identities are awaited, parked, failed or all
-- linked is read from the users and the delivery ledger when the request is read, since they live in the new tenant.
-- =====================================================================

CREATE TABLE iam.tenant_administrator_person (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  request_id                 uuid NOT NULL,
  position                   smallint NOT NULL,
  username                   varchar(120) NOT NULL,
  display_name               varchar(120) NOT NULL,
  idp_code                   varchar(15) NOT NULL,
  external_subject           varchar(120),
  email                      varchar(254),
  mfa_enrolled               boolean NOT NULL,
  user_id                    uuid,
  CONSTRAINT pk_tenant_administrator_person PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_tenant_administrator_person_request FOREIGN KEY (tenant_id, request_id) REFERENCES iam.tenant_administrator_request (tenant_id, id) ON DELETE CASCADE,
  CONSTRAINT uq_tenant_administrator_person_position UNIQUE (tenant_id, request_id, position),
  CONSTRAINT uq_tenant_administrator_person_username UNIQUE (tenant_id, request_id, username),
  CONSTRAINT ck_tenant_administrator_person_identity CHECK (external_subject IS NOT NULL OR email IS NOT NULL)
);
ALTER TABLE iam.tenant_administrator_person ENABLE ROW LEVEL SECURITY;
ALTER TABLE iam.tenant_administrator_person FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON iam.tenant_administrator_person
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE iam.tenant_administrator_person IS 'The people a tenant administrator request names (BR-013): two for a new tenant, one for an additional administrator; user_id once approved.';

-- Existing requests keep their one person, as position 1.
INSERT INTO iam.tenant_administrator_person (tenant_id, id, request_id, position, username, display_name, idp_code, external_subject, email, mfa_enrolled, user_id)
SELECT tenant_id, gen_random_uuid(), id, 1, username, display_name, idp_code, external_subject, email, mfa_enrolled, user_id
FROM iam.tenant_administrator_request;

ALTER TABLE iam.tenant_administrator_request
  DROP CONSTRAINT ck_tenant_administrator_request_status,
  DROP CONSTRAINT ck_tenant_administrator_request_user,
  DROP CONSTRAINT ck_tenant_administrator_request_identity;
UPDATE iam.tenant_administrator_request SET status = 'APPROVED' WHERE status = 'PROVISIONED';
ALTER TABLE iam.tenant_administrator_request
  DROP COLUMN username,
  DROP COLUMN display_name,
  DROP COLUMN idp_code,
  DROP COLUMN external_subject,
  DROP COLUMN email,
  DROP COLUMN mfa_enrolled,
  DROP COLUMN user_id,
  ADD COLUMN kind varchar(24) NOT NULL DEFAULT 'FIRST_ADMINISTRATORS',
  ADD CONSTRAINT ck_tenant_administrator_request_kind CHECK (kind IN ('FIRST_ADMINISTRATORS', 'ADDITIONAL_ADMINISTRATOR')),
  ADD CONSTRAINT ck_tenant_administrator_request_status CHECK (status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED'));
ALTER TABLE iam.tenant_administrator_request ALTER COLUMN kind DROP DEFAULT;
-- An additional administrator joins the tenant's existing TENANT_ADMIN role: the request names no functions.
ALTER TABLE iam.tenant_administrator_request
  DROP CONSTRAINT ck_tenant_administrator_request_functions,
  ADD CONSTRAINT ck_tenant_administrator_request_functions CHECK ((kind = 'FIRST_ADMINISTRATORS') = (cardinality(functions) > 0));
COMMENT ON COLUMN iam.tenant_administrator_request.status IS 'PENDING_APPROVAL, APPROVED or REJECTED. An APPROVED request reads as AwaitingIdentity, IdentityParked, IdentityFailed or Provisioned from its people''s identities (BR-018).';
