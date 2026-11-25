# Addendum: my work, and the step after an approval (dead-end groups A and B)

**Built on branch `group-ba` (25 November 2026), from the dead-end sweep: DE-025, DE-028, DE-029, DE-032, DE-004 (task part), DE-044, DE-045, DE-015 (activation), DE-023 (follow-up), DE-057 and DE-063 (the "told" part).** Code: `src/Modules/Workflow/Insys.Workflow.Application/MyWork.cs`.

## 1. How a task is addressed
Every task (`workflow.task`) has a queue code and may have an assignee. The queue code is either a role code (V0081: parked deliveries, held claim payments, referrals, renewal reviews, aggregation reviews and escalations route to a role) or a named queue (`PARTY_REVIEW`, `COMPLIANCE`, `HEALTH_MEMBERSHIP`, `REQUESTER`, or one the tenant's task types and work queues configure, `DEFAULT` when none does). Nothing in the configuration maps a named queue to a role.

A task is **the caller's** when:
- it is assigned to them (whatever its scope: it was given to them); or
- it is unassigned, its process does not exclude them (the maker of a release step, SRS-002), its record is in their scope (F103), and either
  - its queue is one of their roles in effect, or
  - their roles grant a function that does what the task asks (`WorkServers`).

| Process | Served by the holders of |
|---|---|
| PARKED_DELIVERY (DE-028) | RETRY_PARKED_DELIVERY |
| CLAIM_PAYMENT_HELD, CLAIM_PAY_RELEASE (DE-029) | CLAIMS_RELEASE_INSTRUCTION |
| PARTY_MATCH_REVIEW (DE-032) | PARTY_RESOLVE_MATCH |
| DUE_DILIGENCE_HIT_REVIEW / _SANCTIONS / _REVIEW | PARTY_REVIEW_SCREENING_HIT / PARTY_RECORD_FIC_REPORT / PARTY_COMPLETE_DUE_DILIGENCE_REVIEW |
| HEALTH_ARREARS_TERMINATION | HEALTH_MEMBERSHIP_TERMINATE |
| HEALTH_PROVIDER_MEASURE_REVIEW (DE-004) | HEALTH_RECORD_PROVIDER_QUALITY_REVIEW (in no template yet: DE-004's role decision) |
| HEALTH_PROGRAMME_REVIEW, HEALTH_CARE_ACTIVITY_FOLLOW_UP, HEALTH_RECOVERY_REVIEW | HEALTH_RECORD_PROGRAMME_REVIEW, HEALTH_RECORD_CARE_PROGRESS, HEALTH_RECOVERIES |
| DATA_SUBJECT_REQUEST, PARTY_RETENTION_REVIEW | PARTY_DATA_SUBJECT_REQUESTS, PARTY_ANONYMISE |
| GENERAL_RETENTION | GENERAL_WORK_RETENTION_CASE |
| PAYOUT_BATCH_RELEASE / PAYMENT_RUN_RELEASE / PAYOUT_BATCH_BUILD | PAYMENTS_RELEASE_BATCH / HEALTH_RELEASE_RUN / PAYMENTS_BUILD_BATCH |
| POLICY_ISSUE | LIFE_ISSUE_POLICY, GENERAL_ISSUE_POLICY |

Referrals, renewal reviews, aggregation and configuration reviews are served by their role-coded queue only: they go to the role whose authority covers them. A queue a tenant configures reaches nobody unless its code is a role code (OQ-202).

## 2. The operations
| Operation | Function | Permission |
|---|---|---|
| `GET /workflow/tasks/mine` | WORKFLOW_MY_TASKS (screen) | `own_task.read` |
| `POST /workflow/tasks/mine/{taskId}/claim` | WORKFLOW_CLAIM_MY_TASK | `own_task.update` |
| `POST /workflow/tasks/mine/{taskId}/complete` | WORKFLOW_COMPLETE_MY_TASK | `own_task.update` |
| `GET /workflow/approvals/mine` | WORKFLOW_MY_REQUESTS (screen) | `own_approval.read` |
| `POST /workflow/approvals/mine/{id}/withdraw` | WORKFLOW_WITHDRAW_MY_REQUEST | `own_approval.withdraw` |

All five are in the process area **My work** of the WORKFLOW sub-domain, not privileged, and in every staff role template by one rule in `RoleTemplates.Expand` (screens only for the read-only auditors). `own_task.read` shows only the caller's tasks, never the full list (`WorkflowListOpenTasks`, `process.read`).

My tasks is a cursor page, earliest due first. Each item has the task, its record (`businessRefType`, `businessRefId`, `screen`), the approval it follows (with its status), a batch's purpose and currency for a "build a payout batch" task, and `allowedTransitions` (WorkflowProcessView's). Completing takes `toState`, one of them; with only one, the body may be left out ("done"). Completing an unassigned task the caller serves takes it for them.

My requests is a cursor page of the caller's approvals, newest first, with the status and the decision. Withdraw from it needs no WORKFLOW_APPROVALS (the approval page's own withdraw route stays as it was).

## 3. The step after an approval (DE-045)
When an approval is **granted**, `ApprovalNextSteps` gives the next step a task. An approval type not listed opens none.

| Approval type (record) | Step (process) | Who | Closes on |
|---|---|---|---|
| STATIC_DATA_T1, STATIC_DATA_T2 (STATIC_DATA_CHANGE: a version or an import) | activate (STATIC_DATA_ACTIVATE) | assigned to the maker, who holds SD_<module>_MAINTAIN | `sdm.StaticDataVersionActivated` with the change request (an import's lines carry it) |
| CLAIM_PAYMENT (CLAIM_PAYMENT) | release the instruction (CLAIM_PAY_RELEASE) | queue CLAIMS_MANAGER, served by CLAIMS_RELEASE_INSTRUCTION; never the drafter | `claims.ClaimPaymentInstructed` (also closes CLAIM_PAYMENT_HELD) |
| PAYOUT_RELEASE (PAYOUT_BATCH) | release the batch (PAYOUT_BATCH_RELEASE) | queue FINANCE_MANAGER, served by PAYMENTS_RELEASE_BATCH; never the builder | `payment.PayoutReleased` |
| PROVIDER_PAYMENT_RUN (HEALTH_PAYMENT_RUN) | release the run (PAYMENT_RUN_RELEASE) | queue FINANCE_MANAGER, served by HEALTH_RELEASE_RUN; never the maker | `health.ProviderPaymentRunReleased` |
| REFUND, COMMISSION_STATEMENT, CASE_REDRESS, PAYOUT_RETRY (on a refund, statement, redress or remittance) | build a payout batch for its purpose and currency (PAYOUT_BATCH_BUILD) | queue FINANCE_OFFICER, served by PAYMENTS_BUILD_BATCH | `payment.PaymentBatched` (new) for the record |

Not approvals: an accepted **Life** proposal (`life.LifeProposalAccepted`) and an accepted **General** proposal (`general.GeneralProposalAccepted`, new) get POLICY_ISSUE, assigned to the staff user who submitted it (`submitted_by`, V0158), else in the queue LIFE_UNDERWRITER or GENERAL_UNDERWRITER, served by the issue function. It closes on `life.LifePolicyIssued` or `general.GeneralProposalConverted` (new).

## 4. Telling the requester (DE-044, DE-023)
- **Approved or rejected:** an informational APPROVAL_OUTCOME task, assigned to the requester, which they close by opening or completing it ("done"). None when the next step is already theirs (static data), and none for an approval the platform raised (BR-109).
- **Expired, withdrawn for them, or not applied:** the existing APPROVAL_FOLLOW_UP task (RequesterFollowUp), unchanged.
- **Withdrawn by them:** nobody is told.
- APPROVAL_FOLLOW_UP and APPROVAL_OUTCOME close themselves when the requester asks again for the record (a new approval). A not-applied approval's follow-up also closes when a retry of its parked delivery applies it.
- PARKED_DELIVERY tasks close when the delivery resolves or a person discards it (`integration.ParkedDeliveryDiscarded`, new).

No in-app notification mechanism exists, so the task is the mechanism.
