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
-- V0054: the first tenant administrator's request names functions (OQ-099, ADR-026; Wave 1 batch 4).
-- The column holds function codes. A request made before this release is converted to the functions that need only
-- its permissions (iam.functions_within), as roles were; one whose permissions no function grants keeps them as they were.
-- =====================================================================
ALTER TABLE iam.tenant_administrator_request RENAME COLUMN permissions TO functions;
ALTER TABLE iam.tenant_administrator_request RENAME CONSTRAINT ck_tenant_administrator_request_permissions TO ck_tenant_administrator_request_functions;
UPDATE iam.tenant_administrator_request r
   SET functions = converted.codes
  FROM (SELECT x.tenant_id, x.id, ARRAY(SELECT f FROM iam.functions_within(x.functions) AS f ORDER BY f) AS codes
          FROM iam.tenant_administrator_request x) converted
 WHERE converted.tenant_id = r.tenant_id AND converted.id = r.id AND cardinality(converted.codes) > 0;
COMMENT ON COLUMN iam.tenant_administrator_request.functions IS 'The functions the tenant''s TENANT_ADMIN role is granted (ADR-026, OQ-099).';
