/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Build
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
-- A tenant and a user for the smoke test only, as a first customer's set-up would leave them. tenant and subject come from run.sh.
SELECT iam.adopt_function_catalogue();
INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at) VALUES (:'tenant', 'SMOKE', 'Smoke test', 'A', 'ACTIVE', now());
INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
  VALUES (NULL, 'SMOKE:LIFE', 1, DATE '2026-01-01', 'EFFECTIVE', 'smoke', 'smoke', 'SMOKE', 'LIFE', true);
INSERT INTO party.cfg_due_diligence_settings (tenant_id, code, version, effective_from, record_status, created_by, change_reason, gates_enabled, adverse_media_enabled)
  VALUES (:'tenant', 'DEFAULT', 1, DATE '2026-01-01', 'EFFECTIVE', 'smoke', 'smoke', false, false);
INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
  VALUES (:'tenant', 'ZA-NID', 1, DATE '2026-01-01', 'ACTIVE', 'smoke', 'smoke', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$');
INSERT INTO document.ref_document_categories (tenant_id, code, version, effective_from, record_status, created_by, change_reason, category_code, label, classification, domain, allowed_roles)
  VALUES (:'tenant', 'KYC', 1, DATE '2026-01-01', 'ACTIVE', 'smoke', 'smoke', 'KYC', 'Evidence', 'PERSONAL', 'ALL', '{}');
INSERT INTO iam.user_account (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, username, display_name, idp_code, external_subject, status, mfa_enrolled)
  VALUES (:'tenant', :'tenant', 1, now(), 'smoke', now(), 'smoke', 'smoke', 'Smoke', 'KEYCLOAK', :'subject', 'ACTIVE', true);
INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code)
  VALUES (:'tenant', 'SMOKE', 1, DATE '2026-01-01', 'EFFECTIVE', 'smoke', 'smoke', 'SMOKE');
INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
  SELECT :'tenant', 'SMOKE:' || f, 1, DATE '2026-01-01', 'EFFECTIVE', 'smoke', 'smoke', 'SMOKE', f
  FROM iam.functions_within(ARRAY['party.read', 'party.create', 'party.update', 'document.read', 'document.create', 'user.read', 'user.create']) AS f;
INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, role_code, valid_from, assigned_by, status)
  VALUES (:'tenant', gen_random_uuid(), 1, now(), 'smoke', now(), 'smoke', :'tenant', 'SMOKE', DATE '2026-01-01', :'tenant', 'ACTIVE');
