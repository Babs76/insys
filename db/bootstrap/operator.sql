/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Database.Bootstrap
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
-- Bootstrap of the platform operator (run once per environment, as the owner).
-- Creates the operator tenant, its first administrator and the PLATFORM_ADMIN
-- role. This is the one change that cannot go through maker-checker, because
-- no checker exists yet; a second administrator must be provisioned through
-- the API before any static data can be approved (SDM-003). OQ-031.
-- Placeholders: {{OPERATOR_TENANT_ID}}, {{ADMIN_SUBJECT}}, {{ADMIN_USER_ID}}.
-- =====================================================================
BEGIN;
SELECT set_config('app.tenant_id', '{{OPERATOR_TENANT_ID}}', true);

INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at)
VALUES ('{{OPERATOR_TENANT_ID}}', 'OPERATOR', 'Platform operator', 'C', 'ACTIVE', now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO iam.user_account (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                              username, display_name, idp_code, external_subject, status, mfa_enrolled)
VALUES ('{{OPERATOR_TENANT_ID}}', '{{ADMIN_USER_ID}}', 1, now(), 'bootstrap', now(), 'bootstrap',
        'platform.admin', 'Platform administrator', 'OIDC', '{{ADMIN_SUBJECT}}', 'INVITED', true)
ON CONFLICT DO NOTHING;

-- The role holds the functions that need only these permissions (ADR-026), in force with it.
INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by,
                                               approved_at, approved_by, change_reason, role_code)
VALUES ('{{OPERATOR_TENANT_ID}}', 'PLATFORM_ADMIN', 1, current_date, 'EFFECTIVE', 'bootstrap', now(), 'bootstrap',
        'Bootstrap of the platform operator', 'PLATFORM_ADMIN')
ON CONFLICT DO NOTHING;

INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, approved_at, approved_by,
                                               change_reason, role_code, function_code)
SELECT '{{OPERATOR_TENANT_ID}}', 'PLATFORM_ADMIN:' || f, 1, current_date, 'EFFECTIVE', 'bootstrap', now(), 'bootstrap',
       'Bootstrap of the platform operator', 'PLATFORM_ADMIN', f
FROM iam.functions_within(ARRAY['tenant.read', 'tenant.create', 'tenant.update',
                                'user.read', 'user.create', 'user.update',
                                'static_data.read', 'static_data.create', 'static_data.update', 'static_data.approve',
                                'change_request.read', 'audit_event.read', 'dead_letter.read', 'dead_letter.update']) AS f
ON CONFLICT DO NOTHING;

-- The first platform administrator is the one assignment made without a second person: there is no one else yet
-- (OQ-031). Every later privileged assignment waits for approval in Workflow (OQ-038).
INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                      user_id, role_code, valid_from, assigned_by, status)
SELECT '{{OPERATOR_TENANT_ID}}', gen_random_uuid(), 1, now(), 'bootstrap', now(), 'bootstrap',
       '{{ADMIN_USER_ID}}', 'PLATFORM_ADMIN', current_date, '{{ADMIN_USER_ID}}', 'ACTIVE'
WHERE NOT EXISTS (SELECT 1 FROM iam.user_role_assignment
                  WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND user_id = '{{ADMIN_USER_ID}}' AND role_code = 'PLATFORM_ADMIN');
COMMIT;
