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
-- V0158: "my work" (DE-025, DE-028, DE-029, DE-032, DE-044) and a task for the step after an approval (DE-045, DE-015, DE-023).
-- A process can follow an approval: the approval it follows, a person who may not work its task (the maker of a release step, SRS-002),
-- and, for a "build a payout batch" step, the batch's purpose and currency. Each is null for every other process.
-- A proposal records the staff user who submitted it, so its "issue the policy" task goes to them (null when a portal user or the
-- platform submitted it, and for proposals submitted before this migration: their task goes to the issuing queue).
-- =====================================================================
ALTER TABLE workflow.process_instance
  ADD COLUMN approval_id uuid,
  ADD COLUMN excluded_user_id uuid,
  ADD COLUMN payout_purpose varchar(20),
  ADD COLUMN payout_currency char(3);

-- My tasks (GET /workflow/tasks/mine): open tasks assigned to the caller, and unassigned ones by queue (ix_task_1) or by process.
CREATE INDEX ix_task_open_assignee ON workflow.task (tenant_id, assignee_id, due_at) WHERE status = 'OPEN';
CREATE INDEX ix_process_instance_approval ON workflow.process_instance (tenant_id, approval_id) WHERE approval_id IS NOT NULL;
-- My requests (GET /workflow/approvals/mine).
CREATE INDEX ix_approval_maker ON workflow.approval (tenant_id, maker_id, id);

ALTER TABLE life.proposal ADD COLUMN submitted_by uuid;
ALTER TABLE general.proposal ADD COLUMN submitted_by uuid;
