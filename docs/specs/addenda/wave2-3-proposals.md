# Proposals for Wave 2 batch 3: record-scoped history and batch approval of imports

**For sign-off. Nothing here is built.** Two changes to how access and governance work, asked for on 25 October 2026: a record's history for the people who work on the record (OQ-111 follow-up), and approving a static data import as one change (front-end request BR-016).

## 1. A record's history, for whoever may read the record

### What is built today (confirmed)
- `GET /audit/records/{targetType}/{targetId}` (`GetRecordHistory`, function `AUDIT_RECORD_HISTORY`) needs only `audit_event.read`. It passes `targetType` and `targetId` to the same query as the audit trail (`ListAuditEvents`), filtered by the tenant's own rows (row-level security) and nothing else.
- It does **not** check that the caller may read the record. There is no F103 product or branch scope, no Health restriction (HLT-008: medical information only with `medical_claim.read`), and no relationship check for an external user. Anyone holding `audit_event.read` reads the history of any record in the tenant by type and ID.
- That is why OQ-111 now gives it to the audit roles only. The operational templates lost it, and want it back for the records they work on.

### Proposal
- **A second operation, `GET /audit/records/{targetType}/{targetId}/mine`, with a new function `RECORD_HISTORY`** (STAFF audience, not privileged). It returns the same entries, but only when the caller may read the record itself. `AUDIT_RECORD_HISTORY` stays as it is, for the audit roles.
- **The owning module decides who may read the record.** A new public interface in building blocks (`IRecordReadCheck`: `Covers(targetType)`, `CanRead(targetType, targetId)`). Each module implements it for its own record types by running its own read query as the caller: the same permission, F103 scope, HLT-008 rule and external-user relationship as reading the record through its own endpoint. Audit only asks; it never reads another module's data.
- **Deny by default.** A record type with no check registered, or one the caller cannot read, is **404** (not 403), so the answer does not reveal that the record exists. The permission is the record's own read permission, checked by its module; `audit_event.read` is not needed.
- **What an entry shows is unchanged.** Audit entries already keep identity and medical values out of their details (IAM-006). A Health record's history is readable only by those who may read the Health record.
- **The templates:** `RECORD_HISTORY` joins every operational template, and the audit-read rule (only audit roles hold `audit_event.read` functions) stays as tested.
- **Tests:**
  - Every record type in `AuditTargetTypes` has a read check, or is listed as audit-roles-only with a reason (an architecture test).
  - Out of scope is 404.
  - A Health claim's history without `medical_claim.read` is 404.
  - A portal user reaches only their own records' history, or none (the proposal is staff-only).
- **To build:**
  - the interface;
  - a check in each module that owns audited records (about 20);
  - the operation and its function;
  - the template change.
  - No migration.

## 2. Approving a static data import as one change (BR-016)

### What is built today
- An import (`sdm.import_batch`) validates every line and creates all its drafts at once, all or nothing.
- Each draft is then its own change request (`sdm.change_request`, one per record), submitted and approved one by one. About 100 channel permission rows means about 100 submits and 100 approvals.

### Proposal: an import is approved as one change request
- **Submitting an import** (`POST /static-data/imports/{id}/submit`, If-Match) makes one change request with action `IMPORT`, naming the batch. One Workflow approval follows, of the table's tier (`STATIC_DATA_T1` or `STATIC_DATA_T2`); an import is always one table, so it is always one tier. Tier 2 with maker-checker switched off applies at once, as a single draft does today.
- **The checker sees every line** (`GET /static-data/imports/{id}/lines`): each line's proposed record, whether it creates a code or changes one, and the version in force it would replace.
- **All or nothing:**
  - Approval approves every version in one transaction.
  - If any line can no longer be applied (its code changed since), nothing is applied. The approval is not applied and parks (D-5).
  - Rejection, expiry or withdrawal return every draft to `DRAFT` with the batch's latest rejection (OQ-113), and the maker can correct the drafts and submit the batch again.
- **The drafts belong to the batch while it is submitted.** One cannot be submitted alone, and the batch cannot be submitted while one of its drafts is in its own change request.
- **Governance is unchanged in substance.** A second person still approves every line, sees each one, and approves them knowingly (SDM-003). Only the number of decisions changes: one per import instead of one per row. Each version's `change_request_id` names the batch's change request, so the audit trail still shows who approved each version.
- **Schema:**
  - `sdm.change_request` gains `import_batch_id`, and `IMPORT` among its actions;
  - `record_key` holds the batch number;
  - `after` holds a summary (row count, codes);
  - `sdm.import_batch` gains the statuses `SUBMITTED`, `APPROVED` and `REJECTED`.
- **Tests:**
  - a 100-row import approved with one decision;
  - one rejected, then corrected and approved;
  - one line changed underneath: nothing applied, parked;
  - Tier 2 with maker-checker off;
  - a draft of a submitted batch refused on its own.
