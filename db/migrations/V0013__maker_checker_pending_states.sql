-- =====================================================================
-- INSYS Core · Pending states for two maker-checker gaps (SRS-002; Phase 2
-- review item 2; closes OQ-038 and OQ-046). Approvals are decided in Workflow
-- (ADR-009); these columns hold the change until ApprovalGranted.
-- =====================================================================

-- A privileged role assignment waits for a second person. Only ACTIVE assignments
-- give permissions. Existing assignments are active.
ALTER TABLE iam.user_role_assignment
  ADD COLUMN status      varchar(20) NOT NULL DEFAULT 'ACTIVE',
  ADD COLUMN approved_by uuid,
  ADD COLUMN decided_at  timestamptz,
  ADD CONSTRAINT ck_user_role_assignment_status CHECK (status IN ('PENDING_APPROVAL', 'ACTIVE', 'REJECTED')),
  ADD CONSTRAINT ck_user_role_assignment_checker CHECK (approved_by IS NULL OR approved_by <> assigned_by),
  ADD CONSTRAINT ck_user_role_assignment_pending CHECK (status <> 'PENDING_APPROVAL' OR (decided_at IS NULL AND approved_by IS NULL)),
  ADD CONSTRAINT ck_user_role_assignment_rejected CHECK (status <> 'REJECTED' OR decided_at IS NOT NULL);
ALTER TABLE iam.user_role_assignment ALTER COLUMN status DROP DEFAULT;

-- Switching Tier 2 maker-checker off is a Tier 1 change: it is held here until a
-- second person approves it. Switching it on needs no approval.
ALTER TABLE sdm.tenant_governance_setting
  ADD COLUMN pending_tier2_maker_checker boolean,
  ADD COLUMN pending_requested_by        uuid,
  ADD COLUMN pending_requested_at        timestamptz,
  ADD CONSTRAINT ck_tenant_governance_setting_pending CHECK (
    (pending_tier2_maker_checker IS NULL) = (pending_requested_by IS NULL)
    AND (pending_requested_by IS NULL) = (pending_requested_at IS NULL)),
  ADD CONSTRAINT ck_tenant_governance_setting_pending_off CHECK (pending_tier2_maker_checker IS DISTINCT FROM true);
