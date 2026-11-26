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
-- V0162: groups D, F and C of the dead-end sweep (docs/reports/2026-11-24-dead-end-sweep.md; Ade, 24-25 November 2026).
-- DE-059: a party or policy export waiting for approval (TO_APPROVE) can be refused by the approver, with a reason, or cancelled by
-- who asked for it; nothing leaves either way. The job keeps who refused it and why.
-- =====================================================================
ALTER TABLE party.bulk_job DROP CONSTRAINT ck_bulk_job_status;
ALTER TABLE party.bulk_job ADD CONSTRAINT ck_bulk_job_status CHECK (status IN ('DONE', 'REJECTED', 'TO_APPROVE', 'APPROVED', 'REFUSED', 'CANCELLED'));
ALTER TABLE party.bulk_job ADD COLUMN refused_by uuid;
ALTER TABLE party.bulk_job ADD COLUMN refusal_reason varchar(500);
ALTER TABLE party.bulk_job ADD CONSTRAINT ck_bulk_job_refusal CHECK ((status = 'REFUSED') = (refused_by IS NOT NULL AND refusal_reason IS NOT NULL));
