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

INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by,
                                               approved_at, approved_by, change_reason, role_code, permissions)
VALUES ('{{OPERATOR_TENANT_ID}}', 'PLATFORM_ADMIN', 1, current_date, 'EFFECTIVE', 'bootstrap', now(), 'bootstrap',
        'Bootstrap of the platform operator', 'PLATFORM_ADMIN',
        ARRAY['tenant.read', 'tenant.create', 'tenant.update',
              'user.read', 'user.create', 'user.update',
              'static_data.read', 'static_data.create', 'static_data.update', 'static_data.approve',
              'change_request.read', 'audit_event.read', 'dead_letter.read', 'dead_letter.update'])
ON CONFLICT DO NOTHING;

INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                      user_id, role_code, valid_from, assigned_by)
SELECT '{{OPERATOR_TENANT_ID}}', gen_random_uuid(), 1, now(), 'bootstrap', now(), 'bootstrap',
       '{{ADMIN_USER_ID}}', 'PLATFORM_ADMIN', current_date, '{{ADMIN_USER_ID}}'
WHERE NOT EXISTS (SELECT 1 FROM iam.user_role_assignment
                  WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND user_id = '{{ADMIN_USER_ID}}' AND role_code = 'PLATFORM_ADMIN');
COMMIT;
