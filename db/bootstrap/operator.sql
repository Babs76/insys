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
-- Bootstrap of the platform operator (run once per environment, as the owner; safe to run again).
-- Creates the operator tenant, its two platform administrators and the PLATFORM_ADMIN role, and gives the role the
-- approval authority the operator's own approvals need (WFL-004). BR-061: two distinct named people, from deployment
-- configuration, the platform's equivalent of a new tenant's two first administrators (BR-013). PLATFORM_ADMIN holds
-- TERMINATE_TENANT, so it is privileged and assigning it to anyone else needs a second person's approval: with one
-- administrator nothing could ever be approved. The two assignments below are the one change that cannot go through
-- maker-checker, because no checker exists yet (OQ-031). The runner refuses fewer than two, or the same person twice.
-- Run again, it completes the pair (an environment bootstrapped with one administrator before BR-061), and refuses a
-- configured person who would make a third: more administrators are added through the API, with approval.
-- Placeholders: {{OPERATOR_TENANT_ID}}, {{ADMINn_USER_ID}}, {{ADMINn_SUBJECT}}, {{ADMINn_USERNAME}}, {{ADMINn_NAME}} (n = 1, 2).
-- =====================================================================
BEGIN;
SELECT set_config('app.tenant_id', '{{OPERATOR_TENANT_ID}}', true);

INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at)
VALUES ('{{OPERATOR_TENANT_ID}}', 'OPERATOR', 'Platform operator', 'C', 'ACTIVE', now())
ON CONFLICT (id) DO NOTHING;

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
                                'change_request.read', 'audit_event.read', 'dead_letter.read', 'dead_letter.update',
                                -- BR-061: the Workflow inbox, to decide the other administrator's requests.
                                'approval.read', 'approval.approve']) AS f
ON CONFLICT DO NOTHING;

-- Approval authority for what platform administrators approve in the operator tenant (WFL-004): another platform
-- administrator's privileged role (PLATFORM_ADMIN itself), and a new tenant's first administrators. Without a row, no
-- one could approve either, and adding one is itself a change that needs approval.
INSERT INTO workflow.cfg_delegation_and_approval_authority (tenant_id, code, version, effective_from, record_status, created_by,
                                                            approved_at, approved_by, change_reason, role_code, transaction_type, limit_value, delegation_allowed)
SELECT '{{OPERATOR_TENANT_ID}}', 'PLATFORM_ADMIN:' || t, 1, current_date, 'EFFECTIVE', 'bootstrap', now(), 'bootstrap',
       'Bootstrap of the platform operator (BR-061)', 'PLATFORM_ADMIN', t, 0, false
FROM unnest(ARRAY['PRIVILEGED_ROLE', 'TENANT_ADMINISTRATOR']) AS t
ON CONFLICT DO NOTHING;

-- The audit table is partitioned by month; a new environment's partitions may not exist before the host first starts.
SELECT audit.ensure_monthly_partitions(3);

DO $$
DECLARE
  admins CONSTANT jsonb := '[
    {"id": "{{ADMIN1_USER_ID}}", "subject": "{{ADMIN1_SUBJECT}}", "username": "{{ADMIN1_USERNAME}}", "name": "{{ADMIN1_NAME}}"},
    {"id": "{{ADMIN2_USER_ID}}", "subject": "{{ADMIN2_SUBJECT}}", "username": "{{ADMIN2_USERNAME}}", "name": "{{ADMIN2_NAME}}"}
  ]';
  admin jsonb;
  admin_id uuid;
  held integer;
  before_run integer;
BEGIN
  SELECT count(*) INTO before_run FROM iam.user_role_assignment
  WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE';
  FOR admin IN SELECT * FROM jsonb_array_elements(admins) LOOP
    SELECT id INTO admin_id FROM iam.user_account
    WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND (external_subject = admin->>'subject' OR username = admin->>'username');
    IF admin_id IS NULL THEN
      admin_id := (admin->>'id')::uuid;
      INSERT INTO iam.user_account (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                    username, display_name, idp_code, external_subject, status, mfa_enrolled)
      VALUES ('{{OPERATOR_TENANT_ID}}', admin_id, 1, now(), 'bootstrap', now(), 'bootstrap',
              admin->>'username', admin->>'name', 'OIDC', admin->>'subject', 'INVITED', true);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM iam.user_role_assignment
                   WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND user_id = admin_id AND role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE') THEN
      SELECT count(*) INTO held FROM iam.user_role_assignment
      WHERE tenant_id = '{{OPERATOR_TENANT_ID}}' AND role_code = 'PLATFORM_ADMIN' AND status = 'ACTIVE';
      IF held >= 2 THEN
        RAISE EXCEPTION 'The operator already has two platform administrators; % (%) would be a third. Add administrators through the API, where a second person approves (BR-061).',
          admin->>'username', admin->>'subject';
      END IF;
      INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                            user_id, role_code, valid_from, assigned_by, status)
      VALUES ('{{OPERATOR_TENANT_ID}}', gen_random_uuid(), 1, now(), 'bootstrap', now(), 'bootstrap',
              admin_id, 'PLATFORM_ADMIN', current_date, admin_id, 'ACTIVE');
      -- Audited (IAM-006): the bootstrap made this assignment without a second person, and on a rerun it completed an
      -- environment that had fewer than two administrators.
      INSERT INTO audit.audit_event (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation,
                                     target_type, target_id, outcome, occurred_at, correlation_id, details)
      VALUES ('{{OPERATOR_TENANT_ID}}', gen_random_uuid(), now(), 'bootstrap', 'IDENTITY', 'SERVICE', 'bootstrap', 'BootstrapPlatformAdministrator',
              'UserAccount', admin_id::text, 'SUCCESS', now(), gen_random_uuid(),
              jsonb_build_object('username', admin->>'username', 'displayName', admin->>'name', 'role', 'PLATFORM_ADMIN',
                                 'completedExistingEnvironment', before_run > 0, 'reference', 'BR-061'));
    END IF;
  END LOOP;
END $$;
COMMIT;
