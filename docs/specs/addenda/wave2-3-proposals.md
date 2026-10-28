# Proposals for Wave 2 batch 3: record-scoped history, batch approval of imports, and staff display names

**For sign-off. Nothing here is built.** Two changes to how access and governance work, asked for on 25 October 2026: a record's history for the people who work on the record (OQ-111 follow-up), approving a static data import as one change (front-end request BR-016), and showing staff names where the API returns a user ID (BR-021, added 26 October 2026).

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

### Proposal: an import is approved as one change request (signed off 27 October 2026, with the hash binding below)
- **Submitting an import** (`POST /static-data/imports/{id}/submit`, If-Match) makes one change request with action `IMPORT`, naming the batch. One Workflow approval follows, of the table's tier (`STATIC_DATA_T1` or `STATIC_DATA_T2`); an import is always one table, so it is always one tier. Tier 2 with maker-checker switched off applies at once, as a single draft does today.
- **The checker sees every line** (`GET /static-data/imports/{id}/lines`): each line's proposed record, whether it creates a code or changes one, and the version in force it would replace.
- **Bound to the complete import (Ade's condition, 27 October 2026):** the approval carries a hash of the whole batch: every line's proposed record, its action and the version it replaces, in line order (`ApprovalSubject.Hash`, as payout batches use). Approval applies the batch only if its hash still matches. Any change to a line after submission (an edit, a line added or removed) needs a new approval.
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
  - every static data table gains `import_batch_id`, so a draft knows its import;
  - `sdm.import_batch` keeps its statuses: as built, an import's state is its change request's status, and each line's.
- **Tests:**
  - a 100-row import approved with one decision;
  - one rejected, then corrected and approved;
  - one line changed underneath: nothing applied, parked;
  - Tier 2 with maker-checker off;
  - a draft of a submitted batch refused on its own.

## 3. Staff display names for user IDs (BR-021)

### What is built today
- Many views return a user ID that people see: about 25 fields in the core and static data documents, among them `requestedBy`, `decidedBy` and `approvedBy` (approvals and change requests), `makerId` and `checkerId`, `assigneeId` and `assignedTo` (tasks and cases), and `releaseRequestedBy` and `releasedBy` (legal holds).
- The only read that turns an ID into a name is `GetUser` (`IDENTITY_USERS`), a user administration screen with `user.read`. Ordinary staff do not hold it, so the front end shows raw IDs.
- `iam.user_account.display_name` is required on every user, so each ID the API returns has a name.

### Proposal: a lookup, not names on every view
- **`POST /api/v1/identity/users/display-names`** with `{ "ids": ["…", …] }`, at most 100 IDs, returning `[{ "id", "displayName" }]` for the IDs that are users of the **caller's own tenant**. An ID not in the tenant (or not a user) is left out of the answer, never an error, so the lookup does not confirm whether an ID exists elsewhere. POST because the list is long; it changes nothing and needs no `Idempotency-Key`.
- **Under a basic staff function, `IDENTITY_DISPLAY_NAMES`** (an action on no screen of its own, with a new permission `user.display_name.read`), which every staff template holds. It returns the display name only: no username, email, status, roles or subject.
- **Why a lookup and not a name on each view:** the names live in Identity; adding them to every view means every module reads Identity's users (a cross-module call per row) or copies names that then go stale when a user is renamed. One cached lookup per screen keeps modules apart (CLAUDE.md, modules talk only through contracts) and names current.
- **Portal users: proposed no.** A portal user (member, broker, provider) sees only their own records; the staff IDs on them (who handled a claim, who decided a complaint) are not theirs to resolve to a person. Where a portal view needs a name, it shows a role ("Claims assessor") or a team, decided per view. The function is STAFF-only, so the portal never holds it.
- **Platform operators:** the operator's own views already show platform users; an operator acting as a tenant resolves that tenant's names through the same lookup, audited as any cross-tenant read.
- **Not personal data in logs:** names are returned, never logged or put in events; the audit entry records the count of IDs only.

### Decided (Ade, 28 October 2026)
- The lookup as proposed, **with no function grant**: any signed-in staff user may call it, for their own tenant only, up to 100 IDs a call.
- Portal users cannot resolve staff names; their views show a role or team.
- A disabled user's name is still returned.
- The ID of a platform operator who acted in the tenant resolves to the label "Platform operator", never a personal name.

### As first proposed
1. The lookup as proposed, with a new function every staff template holds.
2. Portal users may not resolve staff names (proposed), or they may for named views.
3. Whether a removed (disabled) user's name is still returned (proposed yes, since history shows who did what).

## 4. Legal holds on a party, a policy, a claim or a case (D-34), Wave 2 batch 4

**Decided (Ade, 27 October 2026):**
- The scopes are party, policy, claim and case. Payments are covered through their policy or claim.
- A hold on a party blocks anonymisation through all its linked records.

### What is built today
- **Holds on documents only.** A legal hold sits on one document (`document.legal_hold`, scope `DOCUMENT`) and stops it being disposed of. Releasing it needs a second person (`LEGAL_HOLD_RELEASE`, OQ-111).
- **The anonymisation check already counts both kinds.** It counts a hold on any document of the party, its policies, memberships and claims. It also counts a hold whose scope is the party or one of those records itself (`scope_id`). Holds of the new scopes will therefore block anonymisation with no change to the check.

### Why not in batch 3
- **A new aggregate:** a record hold belongs to no document, so it needs an aggregate of its own in Documents, alongside the document's holds.
- **Place and release endpoints:** the release is a `LEGAL_HOLD_RELEASE` request, with its four approval endings and the refused grant (ApprovalEndingsTests).
- **Two new functions,** flagged privileged like the document ones, plus role template changes.
- **Target validation:** checking that the party, policy or claim exists needs a record-scope query in Party, Life, General and Health. OQ-118 proposes the same queries for cases.

### Proposal
1. **Place a hold:** `POST /documents/legal-holds` with `scopeType` (`PARTY`, `LIFE_POLICY`, `GENERAL_POLICY`, `HEALTH_MEMBERSHIP`, `LIFE_CLAIM`, `GENERAL_CLAIM`, `HEALTH_CLAIM`), `scopeId` and `reasonCode` (`document.ref_legal_hold_reason_codes`).
   - Function `LEGAL_HOLDS_PLACE`, privileged.
   - The target is checked through its module's record query: 404 if absent, 403 out of scope.
2. **List holds:** `GET /documents/legal-holds?scopeType=&scopeId=&status=`, with cursor paging.
3. **Release a hold:** `POST /documents/legal-holds/{id}/release`, a `LEGAL_HOLD_RELEASE` approval as for a document hold.
   - Function `LEGAL_HOLDS_RELEASE`, privileged.
   - The compliance officer is the checker.
4. **What a hold on a record covers:** the record and every document whose business reference is that record. Disposal of those documents is refused while it is in force, and so is the party's anonymisation.
5. **As built: a table of its own,** `document.record_hold` (V0066). `document.legal_hold` belongs to the document aggregate, and EF cannot map a second hold type onto it.

### To decide
1. The scopes as listed, or also payments and cases.
2. Whether a hold on a party also covers every record that party is linked to (proposed: yes for anonymisation, which the check already does; no for document disposal, where only the party's own documents are held).

