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
-- V0140: scope batch 4 (F103, OQ-169, read sweep). A process, its tasks and its case notes follow the record they are about, and so does a document
-- filed against a scoped record; an unattached document keeps its classification only. Each holds its record's product and node, set when it starts
-- or is stored, as an approval does (V0135), so a list is filtered in the query.
--   is_scoped false: a record type declared unscoped (a party, an operations queue), or a document not attached to a scoped record.
--   is_scoped true with neither product nor node: no scope found, an unrestricted reader's only (fail closed, OQ-176).
-- The backfill below reads each record's scope as its module's source does.
-- =====================================================================
ALTER TABLE workflow.process_instance
  ADD COLUMN is_scoped boolean NOT NULL DEFAULT false,
  ADD COLUMN product_code varchar(15),
  ADD COLUMN org_node_code varchar(15),
  ADD COLUMN org_node_applies boolean NOT NULL DEFAULT true;

ALTER TABLE document.document
  ADD COLUMN is_scoped boolean NOT NULL DEFAULT false,
  ADD COLUMN product_code varchar(15),
  ADD COLUMN org_node_code varchar(15);

-- Processes: every type not declared unscoped is scoped, failing closed until a record's scope is found.
UPDATE workflow.process_instance SET is_scoped = true
WHERE business_ref_type NOT IN ('PARTY', 'DATA_SUBJECT_REQUEST', 'PARTY_RETENTION', 'FRAUD_INDICATOR', 'PARKED_DELIVERY', 'PARTY_MATCH');

UPDATE workflow.process_instance p SET product_code = s.product_code, org_node_code = s.org_node_code
FROM (
  SELECT tenant_id, id, product_code, org_node_code, 'GENERAL_QUOTE' AS t FROM general.quote
  UNION ALL SELECT r.tenant_id, r.id, x.product_code, x.org_node_code, 'GENERAL_RENEWAL' FROM general.renewal r JOIN general.policy x ON x.tenant_id = r.tenant_id AND x.id = r.policy_id
  UNION ALL SELECT r.tenant_id, r.id, x.product_code, x.org_node_code, 'GENERAL_RETENTION_CASE' FROM general.retention_case r JOIN general.policy x ON x.tenant_id = r.tenant_id AND x.id = r.policy_id
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'HEALTH_MEMBERSHIP' FROM health.membership
  UNION ALL SELECT i.tenant_id, i.id, m.product_code, m.org_node_code, 'HEALTH_CARE_ACTIVITY' FROM health.care_plan_item i
    JOIN health.care_plan c ON c.tenant_id = i.tenant_id AND c.id = i.care_plan_id
    JOIN health.complex_case k ON k.tenant_id = c.tenant_id AND k.id = c.complex_case_id
    JOIN health.membership m ON m.tenant_id = k.tenant_id AND m.id = k.membership_id
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'CASE' FROM workflow.service_case
) s
WHERE p.is_scoped AND s.tenant_id = p.tenant_id AND s.id = p.business_ref_id AND s.t = p.business_ref_type;

-- A follow-up of an approval, and any other process about an approval's record, takes the approval's scope.
UPDATE workflow.process_instance p SET product_code = a.product_code, org_node_code = a.org_node_code, org_node_applies = a.org_node_applies, is_scoped = a.is_scoped
FROM workflow.approval a
WHERE p.product_code IS NULL AND p.org_node_code IS NULL AND p.is_scoped AND a.tenant_id = p.tenant_id AND a.business_ref_type = p.business_ref_type
  AND a.business_ref_id = p.business_ref_id;

-- Documents filed against a scoped record take its scope (the record types Documents' legal-hold targets resolve); the rest stay unattached.
UPDATE document.document d SET is_scoped = true, product_code = s.product_code, org_node_code = s.org_node_code
FROM (
  SELECT tenant_id, id, product_code, org_node_code, 'LIFE_POLICY' AS t FROM life.policy
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'LIFE_CLAIM' FROM life.claim
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'GENERAL_POLICY' FROM general.policy
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'GENERAL_CLAIM' FROM general.claim
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'CASE' FROM workflow.service_case
  UNION ALL SELECT tenant_id, id, product_code, org_node_code, 'HEALTH_MEMBERSHIP' FROM health.membership
  UNION ALL SELECT c.tenant_id, c.id, m.product_code, m.org_node_code, 'HEALTH_CLAIM' FROM health.medical_claim c
    JOIN health.membership m ON m.tenant_id = c.tenant_id AND m.id = c.membership_id
) s
WHERE s.tenant_id = d.tenant_id AND s.id = d.business_ref_id AND s.t = d.business_ref_type;

-- Filed against a scoped record type whose record isn't found: fail closed, as a new one would be.
UPDATE document.document SET is_scoped = true
WHERE NOT is_scoped AND business_ref_type IN ('LIFE_POLICY', 'LIFE_CLAIM', 'GENERAL_POLICY', 'GENERAL_CLAIM', 'CASE', 'HEALTH_MEMBERSHIP', 'HEALTH_CLAIM');

CREATE INDEX ix_process_instance_scope ON workflow.process_instance (tenant_id, product_code, org_node_code) WHERE is_scoped;
CREATE INDEX ix_document_scope ON document.document (tenant_id, product_code, org_node_code) WHERE is_scoped;
