# Open questions and decisions

Gaps and conflicts found while building, with the sources involved and the resolution used until someone decides. Precedence when sources disagree: SRS requirement text, then the SQL migrations, then `domain-model.json`, then the HTML specs (PROMPT §1). Structural decisions are in the ADRs in this folder.

Status: **Open** means a decision is needed; **Resolved** means decided (by review or ADR); **Resolved (proposal)** means the code follows the proposal shown and can change if it is rejected.

## Still open at the end of Phase 7

Each item below needs a decision. The code follows the proposal shown until one is made, and each is marked `TODO(spec)` where it applies.

| ID | Decision needed | Proposal the code follows | Suggested owner |
|---|---|---|---|
| OQ-010 | Outbox retry limit and back-off | 10 attempts, 5 s doubling | Architecture |
| OQ-011 | Idempotency key retention | 24 hours | Architecture |
| OQ-012 | Page size limits | 50 by default, 200 at most | Architecture |
| OQ-013 | API rate limits | 1,200 requests a minute per tenant | Architecture, with tenants' volumes |
| OQ-016 | Whose calendar date "today" is | UTC; the tenant's `time_zone` is not yet used | Business |
| OQ-020 | Field protection key custody | DEW KMS envelope encryption decided (ADR-018), to build in Phase 8 Workstream D; rotating the lookup hash key is still open | Security |
| OQ-023 | Database server time zone | Every server runs in UTC (DBA-013) | Operations |
| OQ-025 | Impact analysis and simulation before Tier 1 activation | None; test changes in a non-production tenant | Product |
| OQ-030 | Data migration for an isolation profile change | A migration reference is required and recorded; no migration is built | Architecture |
| OQ-035 | Static data composite business keys | The client sends `code` | Data governance |
| OQ-036 | Published to Effective for generic configuration items | Both treated as in force; product versions are moved by their job | Data governance |
| OQ-037 | Identity provider to `idp_code` mapping | Match on `external_subject`; a subject in two IdPs is refused | Security |
| OQ-041 | Early revocation of a privileged access grant | Not reachable; revoke roles or disable the user | Security |
| OQ-042 | Access reviews of service accounts | That scope is refused | Security |
| OQ-043 | What a privileged access grant permits | Recorded and expired only; it confers no permissions | Security |
| OQ-049 | Escalation targets and the AMOUNT_ABOVE and MANUAL triggers | The target role is the queue; those triggers are unused | Operations |
| OQ-051 | How a rule set decides | The most severe outcome wins; eligibility is not assumed | Underwriting |
| OQ-052 | Rating algorithm details | ADR-007, with the step order configurable; actuarial test cases needed | Actuarial |
| OQ-054 | Party dormancy, retention, merge of roles, duplicate matching | Not built; anonymise is a permissioned command | Business, Compliance |
| OQ-058 | Gap-free numbering, allocation rules, premium tax, provider outbox, Unknown timeout | ID-based references, oldest-due first, tax 0, Unknown after 24 h | Finance, Architecture |
| OQ-059 | General: short-period refunds, reinstatement rules, valuation | Refused or as stated in OQ-059 | Underwriting |
| OQ-060 | Health: edit rules (item 10), member claims (item 11) | ADR-012; item 11 configurable (`member_claim_basis`) | Business, Actuarial |
| OQ-061 | Life and Health: utilisation measures, reversal recoveries, cover-changing alterations, loan disbursement, scheme renewal term, monthly frequency code, member contributions | Not built, or refused | Business |
| OQ-062 | Commission: tax, overrides, negative statements | ADR-014; zero tax | Distribution, Finance |
| OQ-063 | Reinsurance: stop loss, PML, Health cessions, ceded premium decrease, bordereaux as documents | ADR-015 | Reinsurance |
| OQ-064 | Compliance: metric formulas, returns as documents | ADR-015; a metric is the sum of its measure | Compliance |
| OQ-065 | Analytics: Health projections, currencies in keys, claim cause codes | ADR-015 | Business intelligence |
| OQ-066 | Integration: secret store and transport, resending a failed webhook, exposure currency | ADR-015; a failed webhook is discarded only | Architecture |
| OQ-067 | The first administrator of a new tenant | Built as proposed (Wave 0, GAP-019): two platform administrators; the `TENANT_ADMIN` permission list is agreed per tenant, since no requirement fixes it | Security, Operations |
| OQ-068 | Languages: which tenant language is the default, the form of `labels`, where API messages are translated, how documents choose a language | The first of `cfg_locale_defaults.languages` is the default; `labels` maps language code to text; translations live in the UI label bundles keyed by `message_key`; documents use the party's language, then the tenant default, then English (addendum) | Business, Compliance |
| OQ-069 | How many database connections the API may open per instance, and whether to put a pooler in front of RDS | Three instances assumed; option A (about 116 per instance, RDS 16 GB) or B (about 232, RDS 32 GB), both at 45% of `max_connections`; PgBouncer only if load testing needs it | Architecture, Operations |

Also pending business sign-off, though built as accepted: ADR-012 (Health adjudication), ADR-014 (commission) and ADR-015 (reinsurance, reporting, analytics and integration).

## All questions

| ID | Phase | Topic | Status |
|---|---|---|---|
| OQ-001 | 4 | First domain to build | Resolved: General first (22 Oct 2026) |
| OQ-002 | 0 | Shared platform tables in the integration schema | Resolved: inbox per module schema (V0004), idempotency keys stay in integration (review item 3) |
| OQ-003 | 0 | Partitions before January 2027 do not exist | Resolved (V0003, with RLS checked by tests) |
| OQ-004 | 0 | One application role versus one role per module | Resolved: one role per module (ADR-001) |
| OQ-005 | 0 | Creating partitions ahead of need | Resolved: Audit module job (ADR-005) |
| OQ-006 | 0 | Error code catalogue has no seed rows | Resolved: seeded from code (review item 8) |
| OQ-007 | 0 | Permission operations for replay and discard | Resolved (proposal) |
| OQ-008 | 0 | `MessageDeadLettered` event | Resolved (proposal) |
| OQ-009 | 0 | Outbox has no failure columns | Resolved (proposal) |
| OQ-010 | 0 | Retry limit and back-off | Open |
| OQ-011 | 0 | Idempotency key retention | Open |
| OQ-012 | 0 | Page size limits | Open |
| OQ-013 | 0 | API rate limits | Open |
| OQ-014 | 0 | Identity provider claims | Resolved: sub and tenant_id only; permissions from IAM per request (review item 5) |
| OQ-015 | 0 | Auditing authorisation denials | Resolved: denied requests are audited (ADR-004) |
| OQ-016 | 0 | Time zone for business dates | Open |
| OQ-017 | 0 | Event type names | Resolved (proposal) |
| OQ-018 | 0 | Missing If-Match | Resolved (proposal) |
| OQ-019 | 0 | Replay delivers before commit | Resolved (proposal) |
| OQ-020 | 0 | Field protection key custody | Partly resolved: DEW KMS (ADR-018); hash key rotation open |
| OQ-021 | 0 | Test server version | Resolved: CI-equivalent runs on postgres:18 |
| OQ-022 | 1 | The tenant register has no row_version | Resolved (proposal): V0006 |
| OQ-023 | 1 | Server time zone and partition bounds | Open |
| OQ-024 | 1 | Tier 2 maker-checker switch per tenant | Resolved: per-tenant setting, default on, audited (V0008) |
| OQ-025 | 1 | Impact analysis and simulation before Tier 1 activation | Open |
| OQ-026 | 1 | Bulk import endpoint path | Resolved (proposal) |
| OQ-027 | 1 | PrivilegedAccessGrant and AccessReview aggregates | Resolved: V0007 and both aggregates built |
| OQ-028 | 1 | Events for rejection and retirement of static data changes | Resolved (proposal) |
| OQ-029 | 1 | Tenant onboarding steps | Resolved (proposal) |
| OQ-030 | 1 | Approved data migration for an isolation profile change | Open |
| OQ-031 | 1 | Bootstrapping the platform operator | Resolved (proposal) |
| OQ-032 | 1 | Permission cache across several API instances | Resolved: ADR-006, single-instance guard |
| OQ-033 | 1 | `sdm.change_request.status` is too short for PENDING_APPROVAL | Resolved: widened in V0007 |
| OQ-034 | 1 | The API role needs more than INSERT on idempotency keys | Resolved (proposal) |
| OQ-035 | 1 | Static data business keys | Open |
| OQ-036 | 1 | Configuration items: Published to Effective | Open |
| OQ-037 | 1 | Identity provider to idp_code mapping | Open |
| OQ-038 | 1 | Maker-checker for privileged user administration | Resolved: pending assignments approved in Workflow (V0013) |
| OQ-039 | 1 | Repository inside iCloud Drive | Resolved: moved; CI refuses conflict copies |
| OQ-040 | 1 | The privileged access grant table had no concurrency columns | Resolved (proposal): V0007 |
| OQ-041 | 1 | REVOKED status for privileged access grants | Open |
| OQ-042 | 1 | Access reviews of service accounts | Open |
| OQ-043 | 1 | What a privileged access grant permits | Open |
| OQ-044 | 1 | When access review revocations take effect | Resolved (proposal) |
| OQ-045 | 1 | Who decides access review items | Resolved (proposal) |
| OQ-046 | 1 | Maker-checker for the Tier 2 setting itself | Resolved: switch-off approved in Workflow (V0013) |
| OQ-047 | 2 | `workflow.approval` could not hold a pending approval | Resolved: V0010 |
| OQ-048 | 2 | Process state and SLA timer encoding | Resolved (proposal) |
| OQ-049 | 2 | SLA business calendars and escalation targets | Calendars resolved; escalation targets open |
| OQ-050 | 2 | Approval authority for static data changes | Resolved (proposal): ADR-009 |
| OQ-051 | 2 | Rule set outcomes and scope | Open: underwriting decision pending |
| OQ-052 | 2 | Rating algorithm details | Open: actuarial decision pending; step order configurable |
| OQ-053 | 2 | Product versions kept in a static data table | Resolved: ADR-010, V0012 |
| OQ-054 | 2 | Party lifecycle triggers, retention and merge | Open |
| OQ-055 | 2 | Consent withdrawal records | Resolved (proposal) |
| OQ-056 | 2 | Document state, storage, rendering and retention | Resolved (proposal): V0011; stubs |
| OQ-057 | 3 | Journal reversal and posted-record immutability | Resolved (proposal): V0014, ADR-011 |
| OQ-058 | 3 | Money gaps: numbering, tolerances, taxes, providers, timeouts | Open |
| OQ-059 | 4 | General domain: schema gaps (V0016) and unspecified rules | Partly resolved by the Phase 4 review (V0017) |
| OQ-060 | 5 | Health adjudication rules the specs do not define (tariffs, networks, co-payments) | Resolved (proposal): ADR-012, pending business sign-off; item 11 a configurable business decision |
| OQ-061 | 5 | Life and Health: smaller gaps found while building | Resolved by the Phase 5 review, item 5, from existing static data; two business decisions configurable |
| OQ-062 | 6 | Commission: plan assignment, basis, clawback, tax and payee not defined | Resolved (proposal): ADR-014, accepted to build 23 Oct 2026, pending business sign-off |
| OQ-063 | 6 | Reinsurance: treaty arithmetic, recovery basis and bordereau output not defined | Resolved (proposal): ADR-015, accepted to build 23 Oct 2026, pending business sign-off |
| OQ-064 | 6 | Compliance: return content and approval, report runs not defined | Resolved (proposal): ADR-015, accepted to build 23 Oct 2026, pending business sign-off |
| OQ-065 | 6 | Analytics: projection dimensions and the event history to rebuild from | Resolved (proposal): ADR-015, accepted to build 23 Oct 2026, pending business sign-off |
| OQ-066 | 6 | Integration, General renewal and exposure snapshot: smaller gaps | Resolved (proposal): ADR-015, accepted to build 23 Oct 2026, pending business sign-off |
| OQ-067 | 7 | The first administrator of a new tenant | Resolved (proposal, built in Wave 0): pending business sign-off |
| OQ-068 | 8 | Languages: tenant default, labels, message catalogue, document language | Resolved (proposal): `docs/specs/addenda/localisation.md`, pending business sign-off |
| OQ-069 | 8 | Database connection capacity: 23 pools per API instance against the RDS limit | Starting assumption: 3 instances, about 116 each, RDS 16 GB; revisit after load testing |
| OQ-070 | 8 | Document content purposes: free text or a code list | Open: free text today |
| OQ-071 | 8 | A rules dry run that records no decision | Open: proposal for the product configuration wave |
| OQ-072 | 8 | How long a parked event delivery may wait before it is escalated, and who owns the escalation | Decided 23 Oct 2026: per tenant; money consumers 4 business hours then 1 business day, others 1 then 3 business days; never dropped (ADR-022 decision 6) |
| OQ-073 | 8 | Retention of the party search access log | Decided 23 Oct 2026: Security class; full for 12 months, then minimised; deleted after 5 years; legal hold overrides |
| OQ-074 | 8 | Product and organisation scopes on permissions (F103, SRS-003) | Resolved 23 Oct 2026: `docs/specs/addenda/access-scopes.md` signed off; scoped role assignments refused until enforcement is merged |
| OQ-075 | 8 | Channel transaction permissions (F005) | Resolved 23 Oct 2026: `docs/specs/addenda/channel-permissions.md` signed off |
| OQ-076 | 8 | Aggregation and risk limits (F062) | Resolved 23 Oct 2026: `docs/specs/addenda/aggregation-limits.md` signed off |
| OQ-077 | 8 | How a payment provider authenticates its callbacks (GAP-035) | Resolved 23 Oct 2026: ADR-023 revision 2 |
| OQ-078 | 8 | Business use of the operator tenant | Resolved: ADR-024 |
| OQ-079 | 8 | A Life death claim's reserve at registration | Resolved 24 Oct 2026: initial reserve from the sum assured, built |
| OQ-080 | 8 | What a product scope means for Health records (F103) | Resolved: `docs/specs/addenda/health-plan-products.md` built and merged (`537f4cb`) |
| OQ-081 | 8 | Scope rules for shared billing accounts and approvals (F103) | Resolved 24 Oct 2026: confirmed, with account figures from in-scope schedules only; built |
| OQ-082 | 8 | Choices the channel addendum leaves open (F005) | Resolved 24 Oct 2026: confirmed as built |

---

### OQ-001 First domain to build
- **Sources:** PROMPT settings `FIRST_DOMAIN: ASK`; SRS §30.
- **Resolved (22 October 2026):** asked before Phase 4. General is built first; Life and Health follow in Phase 5.

### OQ-002 Shared platform tables in the integration schema
- **Resolved by the Phase 0 review, item 3:**
  - V0004 creates `<schema>.inbox_message` in every module schema. The inbox insert runs in the consumer's transaction, in the consumer's own schema.
  - Idempotency keys stay in `integration.idempotency_key`, owned by the API building block (role `insys_api`).

### OQ-003 Partitions before January 2027 do not exist
- **Resolved:** V0003 creates 2026–2027 partitions. Each has RLS enabled and forced, with the tenant policy. A test queries a partition directly as a module role and sees only its own tenant.

### OQ-004 One application role versus one role per module
- **Resolved:** ADR-001.

### OQ-005 Creating partitions ahead of need
- **Resolved:** ADR-005. Partitions are kept 3 months ahead, with a health warning below 2 months.

### OQ-006 Error code catalogue has no seed rows
- **Resolved by the Phase 0 review, item 8.** The `[ErrorCode]` constants in code are the single source:
  - `db/migrations/R__error_code_catalogue.sql` is generated from them. It is a repeatable script that the runner re-applies when it changes.
  - Tests check that the file matches the code and that the database matches the file.
  - Existing rows are never changed. A changed status for an existing code needs a new version (SDM-002).
  - The seed runs as the owner and assumes a superuser or BYPASSRLS owner, because global rows are platform data.

### OQ-007 Permission operations for replay and discard
- **Sources:** `iam.ref_permission_catalogue.operation` is READ, CREATE, UPDATE, APPROVE or DELETE.
- **Proposal:** replay and discard use `dead_letter.update`.

### OQ-008 `MessageDeadLettered` event
- **Proposal:** not raised; nothing consumes it (PROMPT §5).

### OQ-009 Outbox has no failure columns
- **Proposal:** back-off is computed from `updated_at` and `publish_attempts`. Rows at the limit stay unpublished and are dead-lettered. `first_failed_at` uses `created_at`.

### OQ-010 Retry limit and back-off
- **Proposal:** `Outbox:MaxAttempts` 10 and `Outbox:BaseBackoff` 5 seconds, doubling each time.

### OQ-011 Idempotency key retention
- **Proposal:** 24 hours.
- **Risk:** a crash between commit and completion leaves the key IN_PROGRESS until it expires. Retries are then refused rather than run twice, so the failure is safe.

### OQ-012 Page size limits
- **Proposal:** 50 by default, 200 maximum.

### OQ-013 API rate limits
- **Proposal:** 1,200 requests a minute per tenant.

### OQ-014 Identity provider claims
- **Resolved by the Phase 0 review, item 5.** Access tokens carry `sub` and `tenant_id` only.
- **How permissions are resolved:** `IPrincipalResolver` (the Identity module) finds the user by `external_subject` on every request. It takes the permissions from that user's effective role assignments and the role versions in force today.
- **Caching:** results are cached for 30 seconds per tenant and subject.
- **Eviction:** after a role, lock or disable change commits, and again when `RoleAssigned`, `RoleRevoked`, `UserDisabled` or `TenantSuspended` arrives.
- **Tests:** a revoked role and a disabled user are denied on the next request with the same token. A `permission` claim in a token is ignored.

### OQ-015 Auditing authorisation denials
- **Resolved:** a 403 is written to `audit.audit_event` with outcome DENIED, through `IAuditSink` (ADR-004).

### OQ-016 Time zone for business dates
- **Gap:** whose calendar date "today" is.
- **Current state:** `IClock.UtcToday`. The tenant's `cfg_locale_defaults.time_zone` exists as static data, but the resolver doesn't use it yet.

### OQ-017 Event type names
- **Proposal:** `{contextId}.{EventName}`.

### OQ-018 Missing If-Match
- **Proposal:** a missing If-Match returns 428; an unreadable or weak ETag returns 412.

### OQ-019 Replay delivers before commit
- **Proposal:** delivery first, then commit. Consumers are idempotent, so a repeated delivery is harmless.

### OQ-020 Field protection key custody
- **Open:** keys are read from configuration until a KMS or HSM is chosen (SRS §30).
- **Phase 8 decision (ADR-018):** Huawei Cloud DEW KMS in af-south-1, with envelope encryption: data keys are encrypted by a KMS master key, and each value keeps its key ID. The local provider stays for development and CI. **Still open:** rotating `HashKey`, which means recomputing every `_hash` column.

### OQ-021 Test server version
- **Resolved:** the full suite runs on Testcontainers `postgres:18`. GitHub Actions is blocked on the account, so CI-equivalent runs are local for now.

### OQ-022 The tenant register has no row_version
- **Sources:** DBA-011 says mutable rows use optimistic concurrency; V0001 gives `tenant.tenant` no `row_version`.
- **Proposal:** V0006 adds `row_version`, `updated_at` and `updated_by`.

### OQ-023 Server time zone and partition bounds
- **Gap:** V0003 created its partition bounds in the server's time zone. V0005 creates new ones in UTC. On a server not running in UTC, the boundary between the two would overlap.
- **Proposal:** run every database server in UTC (DBA-013). Local and CI servers do.

### OQ-024 Tier 2 maker-checker switch per tenant
- **Resolved by the Phase 1 review, item 5:** `sdm.tenant_governance_setting` (V0008) holds the switch: one row per tenant, `tier2_maker_checker` on by default.
- **API:** `GET /api/v1/static-data/settings` and `PUT /api/v1/static-data/settings/tier2-maker-checker` (If-Match). Every change is audited with before and after values.
- **Effect:** when the switch is off, a Tier 2 change is approved on submission with no checker, as Tier 3 is. Tier 1 cannot be switched off.

### OQ-025 Impact analysis and simulation before Tier 1 activation
- **Sources:** SDM-010, SDM-011.
- **Gap:** there are no products, policies or simulations to analyse until Phases 2 to 4.
- **Current state:** activation does not check impact.

### OQ-026 Bulk import endpoint path
- **Sources:** PROMPT §7 names `POST /api/v1/static-data/imports`. With ADR-001, only the owning module can write its rows.
- **Proposal:**
  - Imports are `POST /api/v1/{module}/static/{entity}/imports` (and `/global/imports`): all or nothing, every rejected line reported.
  - The Static Data module records the batches, which are listed at `GET /api/v1/static-data/imports`.
  - Imported rows are drafts that then go through approval (SDM-018).

### OQ-027 PrivilegedAccessGrant and AccessReview aggregates
- **Resolved by the Phase 1 review, item 2:** V0007 adds the grant's requester, status, decision and nullable approver (approver ≠ requester), and the `iam.access_review_item` table.
- **Built as in domain-model.json:**
  - PrivilegedAccessGrant: RequestAccess, ApproveAccess, RejectAccess, ExpireAccess. Time-limited, with the purpose recorded and the approver different from the requester (TEN-007). An expiry job records the "Window ends" transition.
  - AccessReview: StartReview, ConfirmAccess, RevokeAccess, CompleteReview. Every item is decided before completion, and revoked roles end when the review completes (IAM-005).

### OQ-028 Events for rejection and retirement of static data changes
- **Gap:** the model lists the RejectChange and RetireCode commands without events.
- **Proposal:** `sdm.StaticDataChangeRejected` is added; without it the version could not return to draft. Retirement publishes nothing, so the change request stays DEPRECATED.

### OQ-029 Tenant onboarding steps
- **Proposal:** the eleven steps of the static data specification's tenant load sequence (§8), completed in order with the proposed command `CompleteOnboardingStep`. Activation requires all of them (TEN-008).

### OQ-030 Approved data migration for an isolation profile change
- **Gap:** the model says such a change "requires an approved data migration", but no migration approval exists yet.
- **Current state:** the command requires a migration reference, which is recorded in the status history.

### OQ-031 Bootstrapping the platform operator
- **Proposal:** `db/bootstrap/operator.sql`, run through `--bootstrap-operator`. It creates the operator tenant, the first administrator and the PLATFORM_ADMIN role, outside maker-checker because no checker exists yet.
- **Note:** a second administrator must be provisioned before any static data can be approved.

### OQ-032 Permission cache across several API instances
- **Resolved:** ADR-006. A revocation is immediate on one instance and takes up to the cache lifetime on others. A startup guard refuses a second instance until evictions are distributed.

### OQ-033 `sdm.change_request.status` is too short for PENDING_APPROVAL
- **Resolved:** V0007 widens the column to varchar(20), and the stored value is PENDING_APPROVAL as in the specification.

### OQ-034 The API role needs more than INSERT on idempotency keys
- **Sources:** review item 4 grants "INSERT on integration.idempotency_key".
- **Proposal:** SELECT, INSERT, UPDATE and DELETE on that table only. Replaying, completing and releasing a key need all four.

### OQ-035 Static data business keys
- **Sources:** V0002 says "for composite natural keys, the service composes it from the key fields", but the catalogue doesn't identify the key fields.
- **Current state:** the client sends `code`.

### OQ-036 Configuration items: Published to Effective
- **Gap:** nothing yet moves a Published configuration item to Effective on its effective date. The resolver treats both as in force.
- **Phase 2:** product versions now do: the Product Factory job makes a Published version Effective on its effective date. Generic configuration items are still open.

### OQ-037 Identity provider to idp_code mapping
- **Gap:** users are matched by `external_subject` only. A subject present in two identity providers is refused until a mapping from token issuer to `idp_code` exists (SRS §30).

### OQ-038 Maker-checker for privileged user administration
- **Sources:** SRS-002.
- **Gap:** role assignment is not yet maker-checker.
- **Resolved (Phase 2 review, item 2a):**
  - **Which roles are privileged:** a role in any authentication policy's `mfa_required_roles`, or one holding a permission that such a role holds. Copying a privileged role's permissions into a new role does not avoid the approval.
  - **Pending until approved:** assigning a privileged role creates a pending assignment (V0013: `status`, `approved_by`, `decided_at`) and a Workflow approval of type `PRIVILEGED_ROLE`. The assignment gives no permission until ApprovalGranted.
  - **The checker:** never the requesting administrator (also a database CHECK), and must hold `PRIVILEGED_ROLE` authority (ADR-009).
  - **Bootstrap:** the first platform administrator is the one assignment made without a second person (OQ-031).

### OQ-039 Repository inside iCloud Drive
- **Resolved:** the repository moved to `~/Development`. A clean build and the full test suite pass there, and no conflict copies remain.
- **Guard:** CI fails when any file matches `* 2.*` (or a directory matches `* 2`).

### OQ-040 The privileged access grant table had no concurrency columns
- **Gap:** with V0007 a grant changes after insert (approve, reject, expire), but the table had no `row_version`, `updated_at` or `updated_by`, which DBA-011 requires for mutable rows.
- **Resolution (proposal):** V0007 adds them, as V0006 did for the tenant register.

### OQ-041 REVOKED status for privileged access grants
- **Gap:** the V0007 CHECK allows REVOKED (review item 2), but domain-model.json has no transition or command that reaches it.
- **Current state:** not reachable. Add a RevokeAccess transition to the model if early revocation of a grant is wanted.

### OQ-042 Access reviews of service accounts
- **Gap:** the Service accounts scope (catalogue `iam.cfg_access_review_schedules`) has nothing to snapshot, because service accounts (IAM-004) are not modelled as users yet.
- **Current state:** StartReview refuses that scope.

### OQ-043 What a privileged access grant permits
- **Gap:** the grant records who, why and when (TEN-007), but no table says which permissions it confers.
- **Current state:** the grant is managed and expired but does not yet change the resolved permissions. Decide whether a grant names a role, or is a fixed "support access" role.

### OQ-044 When access review revocations take effect
- **Proposal:** revocations take effect when the review completes, through Identity consuming its own `AccessReviewCompleted`. The model lists only this event for AccessReview. All the revocations of one review are applied in one inbox transaction, so a review is never half applied; this is one event reaction changing several UserAccount aggregates.

### OQ-045 Who decides access review items
- **Proposal:** only the assigned reviewer (`access_review.reviewer_id`) confirms, revokes and completes. The schema implies this but does not state it.

### OQ-046 Maker-checker for the Tier 2 setting itself
- **Gap:** switching Tier 2 maker-checker off is audited but needs no second person. SRS-002 lists sensitive operations; whether this setting is one is not stated.
- **Proposal:** once Workflow Approval exists (Phase 2), route the switch-off through it.
- **Resolved (Phase 2 review, item 2b):**
  - Switching Tier 2 maker-checker off is a Tier 1 change. It is held as pending (V0013) and asks Workflow for a `STATIC_DATA_T1` approval from a second person.
  - Switching it on takes effect at once, and cancels a pending switch-off.

### OQ-047 `workflow.approval` could not hold a pending approval
- **Sources:** V0001 made `checker_id`, `decision` and `decided_at` NOT NULL, so only decided approvals could be stored. domain-model.json gives Approval the states Requested, Approved, Rejected and Expired.
- **Resolved (user decision):** V0010, the same approach as V0007, adds:
  - `status`, `requested_at`, `expires_at`, `currency` and `decision_reason`;
  - the concurrency columns;
  - a CHECK that a decided approval has a checker and a decision time.

### OQ-048 Process state and SLA timer encoding
- **Gap:** `process_instance.state` (varchar(30)) holds both the definition state and the process status (Running, Suspended, Completed, Cancelled), and the model gives no separate column. `timer.timer_type` (varchar(20)) cannot reference the task it times.
- **Proposal:**
  - The state is the definition state while Running. Otherwise it is prefixed `SUSPENDED:`, `COMPLETED:` or `CANCELLED:`. Definition states are therefore limited to 20 characters.
  - An SLA timer has type `SLA` and shares its task's id.

### OQ-049 SLA business calendars and escalation targets
- **Calendars resolved (Phase 2 review, item 4):** an SLA runs on its business calendar (`workflow.ref_business_calendars`):
  - working days and hours, in the calendar's time zone;
  - the public holidays of its holiday calendar (`refdata.ref_public_holiday_calendars`, through Static Data's `IHolidayCalendar`), with "Next Monday if Sunday" observed;
  - whole days in an SLA duration are working days;
  - an SLA whose calendar is not in force runs on the clock, and a warning is logged.
- **Still open:**
  - Escalation rules name a `target_role`, and tasks have a `queue_code`. The role code is used as the queue, and must be at most 20 characters.
  - Rules with triggers `AMOUNT_ABOVE` and `MANUAL` are not used yet.

### OQ-050 Approval authority for static data changes
- **Gap:** WFL-004 says authority covers every approval. But `workflow.cfg_delegation_and_approval_authority` is itself Tier 1 static data, so its first rows could never be approved.
- **Proposal (ADR-009):** static data approvals are governed by `static_data.approve`, with maker and checker different (SDM-003), until the tenant has authority configured for `STATIC_DATA_T{n}`. From then on that authority applies.

### OQ-051 Rule set outcomes and scope
- **Gaps:** the catalogue defines rule rows with a condition and an outcome, but not how a set of them decides.
- **Status:** open, pending a decision by underwriting (Phase 2 review, item 5). The code follows the proposal below until then.
- **Proposal:**
  - The most severe outcome among the rules that fire is the decision: INELIGIBLE > REFER > ELIGIBLE, and DECLINE > REFER > ACCEPT.
  - Eligibility is not assumed: when no eligibility rule fires, the outcome is INELIGIBLE. Underwriting defaults to ACCEPT.
  - Acceptance and referral rules apply to their product, else to every product of their domain, else (with neither) to every product: `product_code` and `domain`, added by V0102 (BR-113, GAP-117, 30 October 2026). Until then they had no scope, and a Life rule failed every General renewal review. Their `threshold` is available to the condition as `threshold`.
  - A rule whose input is missing refers the decision to a person, listed among the fired rules as `RULE (no input name)` (BR-113). An input of the wrong kind still fails the evaluation.
  - Decline conditions apply when their product is empty or matches.
  - The decision log's `ruleset_code` is the eligibility rule set code, or `UW:{product}` for underwriting.

### OQ-052 Rating algorithm details
- **Status:** open, pending actuarial decisions (Phase 2 review, item 5).
- **Proposal:** ADR-007.
- **Step order is configurable per product version:** premium parameters `STEP_FACTORS`, `STEP_PERCENT`, `STEP_ADJUSTMENTS`, `STEP_FEES` and `STEP_BOUNDS` give each step's position. Tables (with the exposure) always come first, rounding and tax last.
- **Test cases:** the cases written so far are engineering cases. Actuary-supplied cases are still needed (`docs/rating/test-cases.md`).
- **Still to confirm:**
  - the step order and the cap rule;
  - PERCENT parameters as loadings;
  - test case inputs that carry `currency` and `frequency`;
  - the `"{code} v{n}"` product version reference;
  - levies on commission, which are refused.
- **Preloading:** the rating cache is not preloaded on publication, because a product version does not list its currencies. It is dropped, and the next calculation loads it.
- **Premium allocation to coverages (from OQ-059, Phase 4 review):** rating gives one premium per policy. General spreads it over the coverages in proportion to their sums insured, with any rounding difference on the largest. This stays until the rating algorithm gives a premium per coverage.

### OQ-053 Product versions kept in a static data table
- **Resolved (Phase 2 review, item 3d):** ADR-010. `record_status` is the single lifecycle; V0012 makes the duplicate `status` columns nullable and deprecated, and nothing writes them.
- **Sources:** domain-model.json maps ProductVersion to `product.cfg_product_definitions_and_versions` (static).
- **Proposal:**
  - **Lifecycle:** the ProductVersion aggregate owns the rows. The generator maps only the read endpoints for this table, and the ProductVersion lifecycle replaces the generic static lifecycle.
  - **The `status` column** duplicates `record_status`. It is kept equal until approval; after that `sdm.guard_static_version` allows only `record_status` and `effective_to` to change (SDM-002), so `status` keeps the state the version was published in.
  - **Family and class** have no column; they are kept in `labels`. ReclassifyProduct is not built: its effect on published versions, which are immutable, is not defined.
  - **Superseded versions:** on approval, the version being replaced is closed at the new version's effective date, as static data approvals do.
  - **Approval settings:** from `cfg_product_approval_workflow_settings`, `simulation_required` is used and defaults to true. Approver roles and minimum approvers are not used: one checker with PRODUCT_PUBLICATION authority approves.
  - **Simulation:** a run with no test cases does not count as passed. Runs go stale when the configuration changes.

### OQ-054 Party lifecycle triggers, retention and merge
- **Gaps:**
  - **Dormancy:** "No activity" and "New activity" (Active ↔ Dormant) are not defined, so nothing makes a party dormant.
  - **Retention:** no retention period is defined for parties. AnonymiseParty is a permissioned command for a Deceased or Dormant party, and since D-34 (27 October 2026) it checks the retention of the party's records first: see OQ-120.
  - **Merge:** the merged party points to the survivor and keeps a history snapshot without identifier numbers. Moving its roles and relationships to the survivor is not specified.
  - **Duplicate candidates:** `party.duplicate_candidate` and the matching rules are not used yet.
  - **Death claims:** consuming `LifeClaimRegistered` comes with the Life domain.

### OQ-055 Consent withdrawal records
- **Gap:** the ledger is append-only, but `consent_record` has `withdrawn_at`.
- **Proposal:** a withdrawal is a new record with `granted = false` and `withdrawn_at` set; earlier records are never changed. The latest record per purpose is the current consent.

### OQ-056 Document state, storage, rendering and retention
- **Gap:** `document.document` had no column for the Document states.
- **Resolved:** V0011 adds `status` (GENERATED, DELIVERED, DISPOSED) and `disposed_at`, with CHECKs that a disposed document has a disposal time and no legal hold.
- **Stubs until decided:**
  - storage on the local file system, one folder per tenant (`Documents:Storage:Root`);
  - a plain-text renderer that lists the template's merge fields;
  - a delivery stub that records a provider reference without sending.
- **Retention:** the date is set only for schedules triggered at CREATION. Policy end and claim closure set it when those events exist (Phase 4/5). A document without a retention date is never disposed of, and every disposal method deletes the content.
- **Not yet:**
  - the legal hold reason's authority role is not checked;
  - consumers for LifePolicyIssued, InvoiceIssued and the other generation events come with their modules.

### OQ-057 Journal reversal and posted-record immutability
- **Gap:** domain-model.json gives Journal the transition `Posted → Reversed`, but FIN-002 says a posted journal is never overwritten. `fk_journal_reverses_journal_id` also required a reversal to share the original's accounting date, so a journal in a closed period could not be reversed.
- **Resolved (proposal), V0014 and ADR-011:**
  - **Status stays POSTED:** a reversal is a new journal that references the original. The original is never changed (status always POSTED), and "reversed" means a later journal reverses it.
  - **Reversal date:** a reversal posts on its own accounting date, in an open period, with the original's rule versions.
  - **Constraints:** the foreign key is replaced by a trigger, which checks that the original exists and is reversed at most once.
  - **Balance and immutability:** both are enforced in the database. Deferred triggers check the balance at commit. Revoked grants and triggers refuse UPDATE and DELETE.

### OQ-058 Money gaps found in Phase 3
Each item is built as stated and marked `TODO(spec)` in code.
- **Numbering:**
  - **Claim, invoice, payment and journal numbers** are the scheme's prefix plus an ID-based suffix. Sequences with check digits and reset rules (`sequence_length`, `reset_rule`, `check_digit`) need a gap-free counter per tenant, which is not specified.
  - **Claim registration** is unique per domain and domain claim (V0015), and a repeat returns the same number (CLM-005).
  - **Phase 7 fix:** the ID-based suffix was taken from the start of a UUIDv7, which is its timestamp, so records created in the same millisecond could get the same reference (found by the adjudication smoke test as 409s on membership numbers). `References.Of` now takes the suffix from the random end of the ID, for every reference built this way (Health and Life numbers, General policies and quotes, claim registration, billing accounts and invoices). 5,000 IDs give 5,000 distinct references (`ReferencesTests`).
- **Allocation:**
  - `billing.cfg_allocation_rules` (tolerance, overpayment handling) is not applied. A payment is allocated oldest-due first, never above the amount received. Anything left over stays unallocated on the payment.
  - The payments matching tolerance uses the lowest tolerance in force.
- **Premium tax:** taxes on invoices are not specified, so invoices carry tax 0.
- **Credit notes:**
  - Those whose reason needs approval are refused until the approval flow for adjustments is specified.
  - System credit notes record `approved_by` as the nil UUID.
- **Providers:**
  - **No real integrations:** none are specified. The `SIMULATOR` provider stands in for development and tests: an amount ending in .13 is rejected, .99 gets no answer (Unknown), and otherwise the payment is pending until an HMAC-signed callback.
  - **Provider called inside the transaction:** the provider is called within the payment's transaction, so a crash after sending and before commit leaves the payment Initiated. The idempotency key goes to the provider, which must deduplicate. An outbox for provider calls is the proper fix.
- **Payment timeouts:**
  - **Unknown after 24 hours:** a pending payment with no answer becomes Unknown after `Payments:Jobs:UnknownAfter` (default 24 hours, not specified). Unknown goes to a reconciliation exception and a person, never to success (PAY-005).
  - **Unmatched statement lines** become reconciliation exceptions too.
- **Payouts:** a payout with no PAY method in force for its currency was skipped and logged. **Resolved (ADR-022 decision 7, GAP-038):** it is parked, alerted and escalated on the money schedule, and retried when a payout method is published.
- **Transaction types:** Finance maps events to its own codes, for example PREMIUM_DUE, PAYMENT_RECEIVED and CLAIM_PAID (`TransactionTypes`). `finance.ref_transaction_types` is not yet checked against them.
- **Schedules:** open-ended premium schedules generate instalments one year ahead; how far ahead is not specified.
- **Claim payment above the remaining reserve:** a reserve can be lowered after a payment is released. If the payment then exceeds the remaining reserve when it completes, it is not recorded as a movement; it needs an exception queue.

### OQ-059 General domain: schema gaps and unspecified rules (Phase 4)
**Phase 4 review decisions (V0017):**
- **Policy period:** `period_end` is inclusive, with cover to the end of that day. Pro rata uses days from the effective date to the end date, both included, over the period's days. A renewal starts the day after the end and lasts the same whole months (or days). Tests cover the first day, the last day and a leap year.
- **Declined referral:** a referred quote can be declined with a reason. Referred → Declined is terminal, and the event is GeneralQuoteDeclined (domain-model.json updated).
- **Validation rules:** the expression language has bounded arithmetic, `year()` and `today()` (ADR-008). The catalogue's sample `year <= current_year + 1` works.
- **Cover start:** a product version sets `coverStartsOn`, ISSUE or FIRST_PAYMENT. With FIRST_PAYMENT the policy stays Issued, and nothing is covered, until Billing's InvoiceSettled for its first invoice. Cover then starts from that day.
- **Short-period refunds:** still refused until scales are configured.
- **Premium allocation:** proportional allocation stays, flagged under OQ-052.
- **Done in Phase 6 (ADR-015):** auto-renewal and renewal invitations (GEN-004), and the exposure snapshot job.

**Before the Phase 4 review:**

**Schema gaps, resolved by V0016:**
- **Quote versions:**
  - **Conflict:** V0001 has both `uq_quote_1 (quote_number, quote_version)` and `uq_quote_quote_number (quote_number)`. The second allows only one version. domain-model.json says "changes create a new version".
  - **Resolved:** the SQL contradicts itself, so V0016 drops the single-column constraint. A new version keeps the quote number.
  - **Draft quotes:** a draft is not rated yet, so its premium and rating request are nullable until it is rated (a CHECK).
- **Quoted risks and coverages:** QuotedRisk and QuotedCoverage have no tables, so V0016 adds `general.quote_risk` and `general.quote_coverage`. Both are insert-only, with RLS.
- **Claim number:** V0001 makes `general.claim.claim_number` NOT NULL, but the process allocates it at RegisterClaim, after the first notification. It is now null only while the claim is FIRST_NOTIFICATION (a CHECK).
- **Missing columns:** V0016 adds them for:
  - the settlement (amount, payee, reserve type, requester, approval);
  - the decision and its rationale (CLM-003, also a CHECK);
  - endorsement rating and approval;
  - cancellation history for reinstatement (one cancellation in effect at a time);
  - proposal declarations;
  - referral decisions;
  - how the policy is billed;
  - the repair estimate's loss assessment.
- **Status CHECKs:** added for quote, policy, proposal, referral, risk object and repair estimate.

**Rules not specified, built as stated and marked `TODO(spec)`:**
- **Rating:**
  - **Premium frequency:** the rating result is taken as the annual premium. `PremiumCalculated` carries no frequency.
  - **Coverage premiums:** rating gives one premium. It is spread over the coverages in proportion to their sums insured, with any rounding difference on the largest, so the policy premium equals the sum of the coverage premiums.
- **Instalments:** monthly and other instalments are the annual premium divided by the periods, rounded half up. The total may differ from the annual premium by a few cents.
- **Refund scale:** short-period refunds have no scale configured, so cancellations and endorsements with SHORT_PERIOD are refused.
- **Reinstatement:** permitted before the period ends. The product rules that permit it are not specified.
- **Reinstated billing:** a reinstated policy's schedule is not restarted by Billing, because no consumer of GeneralPolicyReinstated is specified.
- **Quote validity:** counts from issue (`cfg_quote_validity_periods`). With none configured, a quote is not issued.
- **Assessor panels:** the product's line is its `class` label, else its family. Panel parties are matched by party ID.
- **Settlement:**
  - the assessed amount less the excess, capped at the coverage's sum insured and its limits and sublimits;
  - valuation rules beyond that are not specified;
  - Workflow's approval authority is used (ADR-009), so `general.cfg_settlement_authority_limits` duplicates it and is not read.
- **Cancellation refund in Billing:**
  - credited against the policy's invoices, latest first, with the cancellation reason as the adjustment reason (else CANCELLATION);
  - a refund above what was invoiced is left for a refund request.
- **Documents at issue:** the policy schedule and certificate are generated, as the process names them; they are not configured. Endorsement documents come from the endorsement type's `documents` list.
- **Not run yet:**
  - **Renewal jobs:** auto-renewal and renewal invitations (`cfg_renewal_rules`).
  - **ProductVersionPublished:** "Refresh product availability". Availability is read live through `ISellableProducts`, so there is nothing to refresh.
  - **Exposure snapshot:** refreshed daily since Phase 6 (ADR-015).
  - **Commission and reinsurance:** built in Phase 6 (ADR-014, ADR-015).
- **Numbering:** quote and policy numbers are a prefix and an ID-based suffix, as in OQ-058.

### OQ-060 Health adjudication rules the specs do not define (Phase 5)
**Defined by the specs, and to be built as stated:**
- plans and options (benefit schedule, network);
- benefit schedules (limit, frequency, co-payment rule);
- co-payment rules (percent or amount, network status, cap);
- general and condition waiting periods, with exception categories;
- limit definitions (annual, lifetime or per event; per member or per family);
- provider register, contracts (provider, network, dates), tariff schedules (code, price, modifiers, dates) and network definitions (providers, plans);
- network restriction rules (not covered, co-payment, or covered at tariff);
- preauthorisation-required lists and validity periods;
- adjudication edit rules (condition, outcome, reason) and adjustment reasons;
- utilisation, high-cost and repeat-service rules;
- payment run calendars;
- fraud indicators;
- dependant types and age limits.

**Not defined, so claims cannot be adjudicated without inventing rules:**
1. **Benefit category of a claim line:** a line has a tariff code and a diagnosis. Nothing maps a tariff, procedure or service code to a benefit category. Without the category there is no limit, co-payment, waiting period or accumulator to apply.
2. **Which tariff applies:** `cfg_tariff_schedules` has no provider, contract, network or plan. With several contracts and networks, the price for a given provider and member is not determined. Contracts point to a terms document only.
3. **Tariff modifiers:** what a modifier such as AFTER_HOURS does to the price (a factor or an amount) is not defined.
4. **Allowed amount:**
   - is it the lesser of claimed and tariff?
   - what is the out-of-network price under "Covered at tariff", and under "Co-payment" (which tariff)?
   - is the member liable for the difference over tariff (the OVER_TARIFF wording suggests so)?
5. **Co-payment:**
   - the order of tariff, co-payment and limit;
   - whether the cap is per line, per claim, per event or per year;
   - "deductible" appears in the table name, but no deductible rule (annual, accumulating) is defined.
6. **Limits:**
   - is the benefit period a calendar year or the membership year?
   - how is a "per family" limit shared between the principal and dependants?
   - which limit applies when both the benefit schedule and `cfg_limit_definitions` set one?
7. **Exclusions:** `product.cfg_exclusion_library` has wording only. No diagnosis or procedure codes say what a claim line is excluded for.
8. **Condition-specific waiting periods:** no diagnosis codes say what a condition is.
9. **Preauthorisation:**
   - how a claim is matched to its preauthorisation (member, provider, procedure, dates within validity, amount tolerance);
   - what happens to a claim needing preauthorisation without one;
   - how much is reserved on the accumulator, and in which category.
10. **Edit rules:** the sample condition ("same member, provider, tariff and service date within 30 days") needs claim history, which the condition language cannot read. Which history facts edit rules may use is not defined.
11. **Member reimbursement claims (HLT-006):** whether they are priced at tariff and paid to the member, and with which co-payment.
12. **Schema gap (fixable):** `health.benefit_accumulator` has `used_amount` but no `reserved_amount`, though "used plus reserved never exceeds the limit". A migration will add it.

**Proposal while waiting:** build Health except adjudication, as the specs define:
- schemes, membership and dependants;
- eligibility enquiries;
- the benefit accumulator (reserve, consume, release, with optimistic locking);
- preauthorisation (clinical review, expiry) and claim submission and validation;
- utilisation and fraud cases;
- provider payment runs.

Adjudication, and the steps of "Health care to provider payment" that need it, wait for answers to items 1 to 9.

**Resolved (proposal, Phase 4 follow-ups item 3):** items 1 to 9 and 12 take the product owner's defaults, recorded in ADR-012 as proposed, pending business sign-off, each configurable (V0019: two new static tables and new columns). Worked examples per step are in `AdjudicationWorkedExamples`, labelled as engineering cases pending actuarial confirmation. Items 10 (edit rules read the line's own facts only; duplicates pend) and 11 (member reimbursement claims pend for a person, and are not paid by provider runs) stay open.

**Phase 5 review, item 5:**
- **Item 10, resolved:** edit rules may read the person's claim history within the rule's `window_days` (V0020), which supports EDIT-DUP-30 (ADR-012).
- **Item 11, kept open as a business decision, now configurable:** the plan's `member_claim_basis` (V0020). The default, REFERENCE_TARIFF, prices member claims at the reference tariff and pays the principal member's verified account through payment runs; exceptions pend. PEND sends them all to a person.

### OQ-061 Life and Health: smaller gaps found while building (Phase 5)
Each is built as shown and marked `TODO(spec)` in code; none changes an amount the specs define.
- **Life**
  - **Eligibility rule set per product:** the proposal accepts the outcome of any eligibility rule set evaluated for it and records its code and version (LIF-002); nothing links a product to the rule set it must pass.
  - **Authority by role:** underwriting decisions and claims are approved in Workflow within the delegation limits for LIFE_UNDERWRITING and LIFE_CLAIM; `life.cfg_claims_authority_limits` (by claim type) and the alteration types' authority roles are not applied.
  - **Alterations:** only alterations without cover or premium changes are applied (the servicing intermediary); others need re-rating (OQ-052) or re-underwriting.
  - **Arrears on reinstatement:** whether arrears from before the lapse must be paid is not specified; billing resumes from the reinstatement date.
  - **Missed premiums** are counted as invoiced instalments past their due date whose invoice is unpaid (Billing's IPolicyArrears). PaymentAllocated carries no policy, so a payment in grace is seen through InvoiceSettled.
  - **Loan disbursement** has no payment flow; **group scheme renewal** moves the renewal date by a year.
  - **Commission clawback and reinsurance cession:** built in Phase 6 (ADR-014, ADR-015).
- **Health**
  - **Eligibility response codes:** outcomes are recorded as ELIGIBLE, NOT_ELIGIBLE and NOT_FOUND; their mapping to the tenant's response codes is not specified.
  - **Contributions:** billed monthly to the payer named at enrolment, for scheme members (rate × members); the employer share split and individual members' contributions are not specified.
  - **Conditionally approved authorisations** have no transitions in domain-model.json, so they are not matched to claims.
  - **Utilisation rules** have free-text measures and are not evaluated; reviews open on high-cost thresholds and by people.
  - **Remittance advice documents** are not generated yet; **claim reversal recoveries** are raised as an event only.

**Resolved by the Phase 5 review, item 5 (22 October 2026), from existing static data:**
- **Life claim authority:** Workflow approves Life claims within `life.cfg_claims_authority_limits` for the claim's type, by the checker's roles (an `IApprovalAuthoritySource` for LIFE_CLAIM), instead of the delegation table. Maker and checker still differ.
- **Alteration authority:** an alteration is applied only by a user holding the alteration type's `authority_role` (`LIFE_AUTHORITY_MISSING` otherwise).
- **Health contribution split:** `employer_share` on the contribution rule. The employer's part per member (rounded to cents) is billed to the scheme sponsor's party (`mst_sponsor_register.party`) on its own schedule (source domain HEALTH_ER); the rest to the payer. A share without a sponsor party refuses the enrolment.
- **Eligibility responses:** each internal outcome (ELIGIBLE, NOT_ELIGIBLE, NOT_FOUND, WAITING_PERIOD) maps to the tenant's response code through `ref_eligibility_response_codes.outcome` (V0020); an outcome without a code refuses the enquiry. WAITING_PERIOD is a covered person in a general waiting period.
- **Product eligibility rule set:** `eligibility_ruleset_code` on product versions (V0020). A proposal records eligibility only from that rule set; a product version without one accepts none.
- **Edit rules with claim history:** see OQ-060 item 10.
- **Preauthorisation:** a conditional approval reserves benefit, can be matched to a claim (Utilised), expires or is cancelled, and becomes Approved when its conditions are met (ConfirmConditionsMet; domain-model.json updated).
- **Remittance advice:** Documents generates one per remittance on ProviderPaymentRunReleased, from the template of the remittance advice format in force (first by sort order, then code); Health attaches the document to the remittance. Releasing a run needs a format in force.

**Kept open as business decisions, now configurable:**
- **Arrears on reinstatement:** the product version's `reinstatement_arrears` (V0020): NONE (default) reinstates without them; REQUIRED refuses reinstatement while instalments are overdue (Billing's arrears).
- **Member reimbursement:** see OQ-060 item 11.

**Still open:** utilisation rule measures, claim reversal recoveries, alterations that change cover or premium (OQ-052), loan disbursement, group scheme renewal term, the monthly billing frequency code and individual members' contributions.

### OQ-062 Commission: what the specs do not define (Phase 6)
**Accepted to build (23 October 2026), pending business sign-off:** ADR-014. Deviations: commission is payable when the invoice is paid in full (not on each PaymentAllocated); a paid entitlement is not clawed back (domain-model.json gives Paid no transition). Still open: tax on commission, overriding commission, collecting a negative statement.

**Defined:** intermediaries (register, hierarchy, product permissions), commission plans (code, basis PREMIUM / POLICY_COUNT / FIXED, rate, a free-text clawback rule), statement templates; entitlements record the plan version and source transaction (DST-002, DST-003); statements are approved before payment (DST-004); accrual on InvoiceIssued, payable on PaymentAllocated, clawback on GeneralPolicyCancelled and LifePolicyLapsed.

**Not defined, and the proposal (configurable, pending business sign-off):**
1. **Which plan applies.** Nothing links a plan to an intermediary or product. *Proposal:* a `plan_code` column on `cfg_intermediary_product_permissions` (intermediary × product → plan), via a migration.
2. **Whose commission an invoice earns.** InvoiceIssued carries the policy but not its intermediary. *Proposal:* General and Life expose a public query (the policy's intermediary code, product, line); Health memberships earn none.
3. **Basis.** *Proposal:* PREMIUM = the invoice's net amount (excluding tax) × rate %; FIXED = the rate as an amount per invoice; POLICY_COUNT = the rate as an amount on a policy's first invoice only. Rounded half away from zero to cents. The plan version in force on the invoice's issue date applies.
4. **When it is payable.** *Proposal:* when the invoice is fully paid (InvoiceSettled), not on each partial allocation.
5. **Clawback rule** (free text). *Proposal:* codes NONE or UNEARNED (default). UNEARNED claws back, in full, each entitlement on the policy whose invoice falls due on or after the cancellation or lapse date, whether accrued, payable or paid; a paid one is recovered on the next statement as a negative line.
6. **Statement tax.** `commission_statement.tax_amount` has no rule. *Proposal:* 0 until a tax rule is configured (TODO).
7. **Payee.** The register has no party or bank account. *Proposal:* a `party_id` column on `mst_intermediary_register`; statements are paid to that party's verified account (ADR-013) through Payments, after a second person approves the statement in Workflow (COMMISSION_STATEMENT, within delegation authority).
8. **Overriding commission** on the hierarchy has no rate. *Proposal:* not calculated (TODO).
9. **Intermediary lifecycle.** The intermediary is Tier 1 static data. *Proposal:* Appoint, Grant, Suspend, Reinstate and Terminate are register and permission changes through static data maker-checker; Distribution checks transitions (Appointed ↔ Suspended, either → Terminated), raises IntermediarySuspended when a suspension takes effect, and General and Life refuse new business through a suspended intermediary or one without a current permission for the product (DST-001).

### OQ-063 Reinsurance: what the specs do not define (Phase 6)
**Accepted to build (23 October 2026), pending business sign-off:** ADR-015. Still open: stop loss, probable maximum loss, Health cessions, a transaction type for a decrease in ceded premium, bordereaux as documents.

**Defined:** reinsurers, treaties (type QUOTA_SHARE / SURPLUS / EXCESS_OF_LOSS / STOP_LOSS, retention as text, capacity, period, participants), cession rules (treaty, products, allocation basis), facultative arrangements, bordereau layouts; cessions are reproducible (REI-002) and linked (REI-001); recoveries are traceable and reconcilable (REI-003); one bordereau per treaty and period.

**Not defined, and the proposal:**
1. **Treaty arithmetic.** *Proposal:*
   - QUOTA_SHARE: `retention` is the retained percent ("30"); ceded sum insured = sum insured × (100 − retention) %, at most `capacity`.
   - SURPLUS: `retention` is an amount; ceded sum insured = min(max(sum insured − retention, 0), capacity).
   - Ceded premium = premium × ceded sum insured / sum insured (allocation basis SUM_INSURED); PREMIUM basis applies the same share to premium directly; PROBABLE_MAXIMUM_LOSS is not supported (no PML data).
   - EXCESS_OF_LOSS: no cession; per claim, recoverable = min(max(paid − retention, 0), capacity).
   - STOP_LOSS: not supported (aggregate loss ratio not defined).
   - Facultative arrangements: when a policy's risk reference matches, the arrangement's share is ceded instead of the treaty.
2. **Sum insured and policy on events.** GeneralPolicyIssued has no sum insured and GeneralClaimSettled no policy. *Proposal:* add them (optional fields, compatible).
3. **Recovery.** *Proposal:* proportional treaties recover paid × ceded share of the latest cession for the claim's policy; Health claims are not ceded until a treaty names Health products.
4. **Billing and settling recoveries.** *Proposal:* BillRecovery posts the recoverable to Finance and sends it through a reinsurer adapter (stub); SettleRecovery records the amount received; the difference stays visible for reconciliation.
5. **Bordereau output.** *Proposal:* a CSV of the layout's fields over the cessions (or recoveries) in the period, stored as a document; SendBordereau goes through the reinsurer adapter (stub); AgreeBordereau records the reinsurer's agreement.
6. **Finance.** *Proposal:* new transaction types for ceded premium and reinsurance recoverable, posted through PostBusinessEvent like the others; their posting rules are static data.

### OQ-064 Compliance and reporting: what the specs do not define (Phase 6)
**Accepted to build (23 October 2026), pending business sign-off:** ADR-015. Still open: metric formulas (a metric is the sum of its source measure), returns and report results as documents.

1. **Return content.** `field_mapping` (jsonb) has no defined form. *Proposal:* `{"field": "account:<code>"}` takes the account's period balance from Finance's trial balance; `"metric:<code>"` takes an analytics projection total. The return is generated as a document.
2. **When.** *Proposal:* PrepareReturn on a final AccountingPeriodClosed for each return definition whose frequency ends with that period (monthly every month, quarterly at quarter end, annual at year end).
3. **Approval.** *Proposal:* ReviewReturn and ApproveReturn need the compliance permissions; the approver differs from the reviewer and the preparer. SubmitReturn goes through a regulator adapter (stub); its answer moves the return to Accepted or Rejected; a rejected return is corrected back to Prepared.
4. **Report runs.** *Proposal:* the catalogue's roles decide who may run a report; a run stores its result as a document; export only where `export_allowed`.

### OQ-065 Analytics: what the specs do not define (Phase 6)
**Accepted to build (23 October 2026), pending business sign-off:** ADR-015. Still open: Health projections, currencies in the projection keys, cause codes on claim events.

1. **Dimensions.** `fact_written_premium` has channel and branch; no event carries either. *Proposal:* channel = INTERMEDIARY when the policy has an intermediary, otherwise DIRECT; branch = ALL until branches exist.
2. **Measures.** *Proposal:* written premium = annual premium at issue, plus endorsement deltas, less cancellation refunds, by the transaction date; claims incurred = paid plus the change in outstanding reserve, by month and cause code (UNKNOWN when none).
3. **Rebuild.** Inboxes do not keep payloads, so there is nothing to rebuild from. *Proposal:* Analytics keeps its own log of the events it consumes (a new table), and a rebuild clears the facts and replays that log.

### OQ-066 Integration, General renewal and exposure: smaller gaps (Phase 6)
**Accepted to build (23 October 2026), pending business sign-off:** ADR-015. Still open: the production secret store and transport, replaying a failed webhook (Failed has no transition in domain-model.json), the exposure snapshot's currency.

**Phase 7:** replaying a `webhook:` dead letter used to deserialise the webhook body as an event envelope and publish it, which delivered nothing useful. It is now refused (409 `INVALID_STATE_TRANSITION`), and such a dead letter can only be discarded. *Proposal:* a replay of a failed webhook makes its delivery Pending again with its attempts reset (a new transition, Failed → Pending on ReplayMessage); the one-delivery-per-subscription-and-event constraint stays.

1. **Webhooks.** *Proposal:* deliver the subscribed event types to the endpoint, signed with HMAC-SHA256 using the key named by `signing_key_ref` (resolved by a secret store stub); retry by the subscription's retry and dead-letter policy (exponential or fixed back-off); when retries are exhausted the delivery is Failed and dead-lettered.
2. **Partner adapters.** Interfaces with stub implementations (reinsurer, regulator, general ledger), each call recorded in `integration.external_call_log`.
3. **Renewal job (GEN-004).** *Proposal:* `notice_days` before the period end, a renewal invitation document (`invitation_template`); at the period end, an `auto_renew` policy without `re_rating` is renewed (RenewPolicy); one that needs re-rating gets an invitation only.
4. **Exposure snapshot.** *Proposal:* a daily job sums the sum insured of in-force policies' risk objects by `risk_zone` (UNKNOWN when empty) and covered peril, as at the day.

### OQ-067 The first administrator of a new tenant (Phase 7)
- **Sources:** TEN-008 and OQ-029 (onboarding step ORGANISATION_AND_ACCESS); OQ-031 (the operator bootstrap).
- **Gap:** ProvisionUser and AssignRole act in the caller's own tenant, and a new tenant has no users, so nothing in the API can create its first administrator. The operator bootstrap covers only the operator tenant. Found while writing the tenants runbook.
- **Current state:** not built. The runbook says an owner inserts the first administrator as `operator.sql` does, as a recorded change.
- **Proposal:** a platform command, `ProvisionTenantAdministrator`, for the platform operator only, allowed while the tenant is Onboarding. It creates the tenant's first user and a TENANT_ADMIN role assignment, approved by a second platform administrator in Workflow (SRS-002), and completes the ORGANISATION_AND_ACCESS step.
- **Built (Wave 0, GAP-019, pending business sign-off):** `POST /api/v1/identity/tenant-administrators` and V0026. Choices made in building it:
  - Roles are tenant-scoped static data, so a new tenant has no `TENANT_ADMIN` role. The request carries the role's permissions, and the approval creates the role definition in force with them. No requirement fixes the list.
  - The tenant must have no users yet.
  - On approval, the new tenant's rows are written in the inbox transaction with row-level security switched to that tenant for that block only (`ModuleDbContext.WithinTenant`, operator callers only, in Identity and Tenancy).
  - `assigned_by` is the new user itself, as in the operator bootstrap, because the schema requires a user of the same tenant. The requester is on the request, and the checker is `approved_by`.
  - The administrator acts once the tenant is Active, like all its users.
  - Keycloak: the user is created in Keycloak first, by a Keycloak administrator, with `tenant_id`; the request names its `sub`. INSYS does not call the Keycloak admin API.

### OQ-068 Languages and localisation (Phase 8, Workstream B)
- **Sources:** SRS §1.3. Static data: `tenant.cfg_locale_defaults.languages`, `refdata.ref_languages`, `channel.ref_ui_label_and_translation_bundles`, `labels` on every static data table, and template and assignment languages. Also `party.individual.preferred_language`.
- **Gap:** no requirement says which language is a tenant's default, what `labels` holds, where API message texts are translated, or how a document's language is chosen.
- **Proposal (built, pending business sign-off):** `docs/specs/addenda/localisation.md`.
  - The first configured language is the default.
  - `Accept-Language` is honoured when the tenant supports the language. Otherwise the tenant default applies, then English.
  - `labels` is `{language: text}`, and reads return `displayLabel` and `displayLabelLanguage`.
  - ProblemDetails titles (and details, where translated) come from the UI label bundles keyed by the error code's `message_key`, and the code stays unchanged.
  - Responses carry `Content-Language`.
  - Documents are in the party's language, then the tenant default, then English.
  - `PUT /api/v1/party/parties/{id}/preferred-language` sets an individual's preferred language.

### OQ-069 Database connection capacity (Phase 8, 23 October 2026)
- **Sources:** ADR-001 (one database role per module), ADR-006 (one API instance until permission evictions are shared), ADR-016 (RDS for PostgreSQL in af-south-1) and ADR-020 (load testing before go-live).
- **What the code does:**
  - Each API instance opens one Npgsql pool per module role, plus one for `insys_api`. That is **23 pools** (`db/roles/module_roles.csv`), and each is built from `ConnectionStrings:<module>` or the shared template.
  - Npgsql's default `Maximum Pool Size` is 100, so one instance can open up to **2,300 connections**. Normal traffic opens far fewer: a request uses one or two modules' pools, and connections idle out after 300 s.
  - The tests use 10 per pool.
- **RDS limit:** Huawei's RDS for PostgreSQL FAQ gives the default `max_connections` as min((memory − shared_buffers) / 16 MB, 5,000).

  | Memory | Default `max_connections` |
  |---|---|
  | 8 GB | 384 |
  | 16 GB | 768 |
  | 32 GB | 1,536 |
  | 64 GB | 3,072 |

  A few of these are reserved for the superuser and RDS's own agents. RDS for PostgreSQL has **no managed database proxy**; Huawei documents client-driver read/write splitting instead.
- **Worst case against the limit:**

  | API instances | Default pools (100 each) | Proposed pools (below) |
  |---|---|---|
  | 1 (today, ADR-006) | 2,300 | about 230 |
  | 2 | 4,600 | about 460 |
  | 4 | 9,200 | about 920 |

  The migration runner and the background jobs share these pools; the jobs run in the same process.
- **Proposal, to be confirmed by load testing (ADR-020):**
  1. Set `Maximum Pool Size` explicitly in each module's connection string:
     - 20 for the hot paths: `insys_api`, `insys_iam`, `insys_payment`, `insys_billing`, `insys_rating`, `insys_health`, `insys_general` and `insys_integration`;
     - 5 for the rest.

     That is about 230 per instance. Keep `Connection Idle Lifetime` at 300 s or less.
  2. **Size RDS for 2× the planned peak.** A 32 GB instance (1,536) holds four instances with the proposed pools and leaves headroom for replicas, maintenance and the migration runner.
  3. **Add PgBouncer** (transaction pooling, on CCE) only if load testing needs more instances or smaller RDS.
     - It works with this design: the tenant is set with `set_config(..., true)`, which lasts only for the transaction.
     - Npgsql needs `No Reset On Close=true`, and prepared statements must stay off or use PgBouncer 1.21's prepared-statement support.
  4. **Alert** when connections pass 80% of `max_connections`, using RDS metrics in Cloud Eye.
- **Starting assumption (23 October 2026, pending load testing): three API instances.** There are two options for the hosting decision. Sizes are `Maximum Pool Size` per module role, set in each role's connection string.

  | Roles | Option A: about 120 per instance | Option B: about 230 per instance |
  |---|---|---|
  | Hot: `insys_api`, `insys_iam`, `insys_payment`, `insys_billing`, `insys_rating`, `insys_health`, `insys_general`, `insys_integration` (8) | 8 each = 64 | 15 each = 120 |
  | Medium: `insys_party`, `insys_product`, `insys_claims`, `insys_life`, `insys_finance`, `insys_workflow`, `insys_document` (7) | 4 each = 28 | 8 each = 56 |
  | Low: `insys_sdm`, `insys_rules`, `insys_commission`, `insys_reinsurance`, `insys_reporting`, `insys_analytics`, `insys_tenant`, `insys_audit` (8) | 3 each = 24 | 7 each = 56 |
  | **Per instance** | **116** | **232** |
  | **Three instances** | **348** | **696** |
  | RDS class and default `max_connections` | 16 GB, 768 | 32 GB, 1,536 |
  | Share of the limit used | 45% | 45% |

  - Both leave the same share of the limit free, for a fourth instance during a rolling deployment, the migration runner, RDS agents and read replicas.
  - **Option A** is cheaper. Under a burst, requests wait for a pooled connection; Npgsql waits up to its 15-second `Timeout`, then fails with an error. Load testing must check the hot pools under peak.
  - **Option B** doubles every pool and the RDS class, at higher cost.
  - Either option needs ADR-006's single-instance guard lifted first: permission evictions must be shared between instances.
- **Starting assumption, recorded 23 October 2026: option A.** Three API instances, about 116 connections per instance (the option A pool sizes), on the RDS 16 GB class (768 connections). To be revisited after load testing (ADR-020).
- **Sources:**
  - [Maximum number of connections, RDS for PostgreSQL FAQ](https://support.huaweicloud.com/intl/en-us/rds-pg_faq/rds_faq_0055.html)
  - [Read/write splitting for RDS for PostgreSQL](https://support.huaweicloud.com/intl/en-us/rds-pg_faq/rds_faq_0017.html)
- **Built (Wave 0, GAP-039, 23 October 2026):**
  - The option A pool sizes are the defaults (`DatabasePools`, 116 per instance).
  - A startup guard checks them against `Database:MaxConnections` (less `Database:Reserve`, divided by `Database:Instances`).
  - A busy pool or database answers 503 `DATABASE_BUSY`.
  - The arithmetic is in `docs/runbooks/deployment.md`.

### OQ-070 Document content purposes (Phase 8 Wave 0, front-end request 14, GAP-026)
- **Sources:** DOC-001, DOC-003 (access to sensitive documents is auditable), DOC-004.
- **Gap:** reading a sensitive document's content needs a `purpose`, which the access log records. No requirement and no static data table lists the allowed purposes, so it is free text (at most 60 characters). The front end asks for an enumeration.
- **Current state:** free text. The OpenAPI says so.
- **Proposal (pending business sign-off):** a tenant static data list of document access purposes, which `ReadDocumentContent` validates against. Its codes are a business decision, so none are invented here.

### OQ-071 A rules dry run (Phase 8 Wave 0, front-end request 16, GAP-028)
- **Sources:** PRD-005 (simulation and test cases before publication), CLM-003 and LIF-002 (decisions traceable).
- **Gap:** every rule evaluation records a decision. Rating has `simulate`, which records nothing; Rules has no equivalent, so testing a rule set leaves recorded decisions.
- **Current state:** recording every evaluation is intended for decisions made in business processes.
- **Proposal:** `POST /api/v1/rules/decisions/simulate`, which evaluates without recording, for authoring and product simulation. It belongs with the product configuration work, not Wave 0, because the front end can already test through recorded evaluations.

### OQ-072 Escalating a parked event delivery (Phase 8 Wave 0, ADR-022)
- **Sources:** INT-006 (controlled retry, monitoring and recovery); WFL (tasks and SLAs).
- **Gap:** ADR-022 parks a delivery whose tenant configuration is missing, instead of dead-lettering it. Nothing sets how long it may stay parked before someone must act, or who that is. `integration.cfg_retry_and_dead_letter_policies` governs retries (attempts, back-off, delays), not parking.
- **Proposal (pending business sign-off):** escalate after 1 business day by the tenant's business calendar, overridable per tenant, with a Workflow task in the tenant's operations queue.
- **Decided (23 October 2026, Ade):** configured per tenant, with these defaults. Every park alerts platform operations at once and is listed per tenant. Money consumers: a Workflow task to the tenant's configuration-owner role after 4 business hours, then the tenant administrator and the platform operator after 1 business day. Other consumers: after 1 business day, then 3. Business hours come from the tenant's calendar in `workflow.ref_business_calendars`, else Monday to Friday 08:00 to 17:00 in the tenant's time zone. A parked event is never dropped or dead-lettered automatically: discarding one is manual, needs a reason and is audited. **Closed:** ADR-022 decision 6.

### OQ-073 Retention of the party search access log (Phase 8 Wave 0, GAP-022)
- **Sources:** PTY-003, PTY-005 (authorised purposes, privacy); IAM-006 (audit); OQ-054 (party retention, open).
- **What exists:**
  - Static data has `audit.cfg_audit_retention_policies`: a period per event class (`Security`, `Financial`, `Operational`) and jurisdiction. Its values are tenant configuration, and none are defined.
  - `document.cfg_retention_schedules` covers documents only.
  - Nothing enforces audit retention yet.
- **Gap:** `party.party_search_log` records who searched for parties, with which filters. Neither the SRS nor static data gives it a period or says which retention class it belongs to.
- **Current state:**
  - Kept indefinitely, and nothing is deleted.
  - Readable only through `GET /api/v1/party/search-log`, which needs `audit_event.read` (the audit roles).
  - Insert-only for the Party module's database role.
- **Options:**
  - (a) Treat it as a `Security` audit record, retained by that class's period for the tenant's jurisdiction.
  - (b) Give it a retention period of its own in static data.

  Either is a business decision. It belongs with OQ-054's party retention.
- **Decided (23 October 2026, Ade):** the `Security` class, configured per tenant in `audit.cfg_audit_retention_policies`. Full entries are kept for 12 months. After that the plain-text name filter is removed, keeping who, when, the filter types, the identifier hash and the result count. Entries are deleted 5 years after the search date, and a legal hold overrides deletion. **Basis:** aligned with the FICA s23 and LTIA PPR rule 16.4(b) five-year minimums, and POPIA s14 minimisation; compliance to confirm before go-live. Built as the party search log retention job (`SearchLogRetentionJob`, daily; V0030 holds and the owner-run minimise and purge functions; `POST /api/v1/party/search-log/holds`). General retention, OQ-054, is still open. **Closed.**

### OQ-074 Product and organisation scopes on permissions (Phase 8 Wave 0, F103)
- **Sources:** SRS-003; `tenant.mst_organisation_structure`; `scope_products` and `scope_branches` on roles and role assignments.
- **Gap:**
  - No transactional record carries a branch or organisation.
  - The specifications do not say how a record gets one, whether a scope covers the nodes below it, how scopes apply to reads, or what a transaction category is.
  - Product scopes are stored and shown on `/identity/me`, but not enforced: a product-scoped role gives its permissions for every product.
- **Proposal (pending business sign-off):** `docs/specs/addenda/access-scopes.md`. Not built until approved, because it changes every domain module's repositories and adds a column to each scoped record.
- **Resolved (23 October 2026):** `docs/specs/addenda/access-scopes.md` is signed off, and its build is planned (Wave 0b). Until enforcement is merged, role assignments with product or organisation scopes are refused (`SCOPES_NOT_ENFORCED`), existing ones are listed for review, and `/identity/me` marks scopes as not enforced. **Closed.**

### OQ-075 Channel transaction permissions (Phase 8 Wave 0, F005)
- **Sources:** static data `channel.cfg_channel_definitions` and `channel.cfg_channel_transaction_permissions`; domain model (a Life proposal's product on its channel).
- **Gap:** no code reads either table. The specifications do not say how a request's channel is known (a channel in the body cannot be trusted), what a `transaction_type` is, or what `requires_approval` means where a command has no approval flow.
- **Proposal (pending business sign-off):** `docs/specs/addenda/channel-permissions.md`. The channel comes from the token's OAuth client, which needs a new `client_ids` field. Transaction types are permission codes. It is enforced on domain commands. Not built until approved.
- **Resolved (23 October 2026):** `docs/specs/addenda/channel-permissions.md` is signed off, and its build is planned (Wave 0b). **Closed.**

### OQ-076 Aggregation and risk limits (Phase 8 Wave 0, F062)
- **Sources:** static data `general.cfg_aggregation_zones_and_limits` and `general.cfg_aggregate_limit_definitions`; GEN-008; the exposure snapshot (ADR-015).
- **Gap:** no code reads either table. Undefined:
  - whether exceeding a zone limit blocks, refers or only alerts;
  - from what level to alert;
  - how claims aggregates reinstate.
- **Proposal (pending business sign-off):** `docs/specs/addenda/aggregation-limits.md`:
  - quotes over a zone limit are referred to an underwriter;
  - endorsements over it need approval;
  - an alert level is set once it is chosen;
  - settlement stops at the claims aggregate, with reinstatement as proposed there.

  Not built until approved.
- **Resolved (23 October 2026):** `docs/specs/addenda/aggregation-limits.md` is signed off, and its build is planned (Wave 0b). The alert level (`Aggregation:AlertAt`) and the automatic reinstatement rule stay unset until they are chosen. **Closed.**

### OQ-077 How a payment provider authenticates its callbacks (Phase 8 Wave 0, F5 finding 2, GAP-035)
- **Sources:** PAY-003, PAY-005, TEN-002; ADR-017 (confidential clients for service integrations).
- **Gap:** the callback endpoint needs a user's bearer token as well as the body's HMAC signature. A provider cannot hold a user token, the signing secret is one per provider for every tenant, and nothing stops a replay. The specifications do not say how a provider authenticates.
- **Proposal (pending sign-off):** ADR-023. OAuth 2.0 client credentials, one Keycloak client per tenant and provider, whose service account is an IAM service identity holding only `payment_callback.create`; the signature stays mandatory, keyed per tenant and provider from the secret store, over a timestamp checked to 5 minutes; a WAF allow-list as defence in depth. Not built until approved.
- **Resolved (23 October 2026):** approved with a change, and ADR-023 is revised (revision 2). The main mechanism is a callback address per tenant and provider holding an unguessable endpoint key, mapped server side to the tenant and provider. The tenant is set only from that key plus a valid HMAC signature keyed to that tenant and provider. The signature covers a timestamp (tolerance window) and the callback's ID (repeats refused). There is no user bearer token. Client credentials are an optional extra. Keys and secrets are rotatable, encrypted (DEW) and never logged. The firewall allow-list stays as a second layer. **Closed.**

### OQ-078 Business use of the operator tenant (Phase 8 Wave 0, F5 finding 3)
- **Sources:** TEN-001, TEN-002, TEN-007; ADR-003, ADR-017.
- **Gap:** nothing stops a user of the operator tenant with business permissions from creating parties, policies, claims or payments there. The specifications do not say whether the operator tenant may hold business data.
- **Proposal (pending sign-off):** ADR-024. Business modules refuse the operator tenant with 403 `OPERATOR_TENANT_NOT_BUSINESS`, reads as well as changes, through one endpoint filter per business module checked by a sweep test; Tenancy, Identity, Static data, Audit, Integration and Workflow stay open to it. Not built until approved.
- **Resolved (23 October 2026):** ADR-024 is signed off. It is built in Wave 0 batch 5 and merged only after the front end's live tests have moved to a business tenant. **Closed.**

### OQ-079 Setting a Life death claim's reserve at registration (Phase 8 Wave 0)
- **Sources:**
  - CLM-004 (reserves as financial movements);
  - GEN-006 lists reserves among General claim steps;
  - LIF-006 and LIF-008 (Life claims);
  - the database specification ("running reserve = sum of movements").
- **What the code does:**
  - a Life claim's payment instruction is released only within the claim's Claims Framework reserve (`RESERVE_BELOW_ZERO`);
  - nothing sets that reserve: a person sets it through `/claims/claims/{claimNumber}/reserves`.
- **Gap:** the specifications do not say whether a Life death claim's reserve should be set automatically at registration from the sum assured (or the benefit's amount), or set by an assessor.
- **Options:**
  1. **Automatic at registration:** the reserve is set from the death benefit's sum assured on the policy, by benefit, with a reserve type from static data.
  2. **Automatic at assessment:** set from the assessed amount.
  3. **Manual, as today:** documented as a step before payment. A refused release is now held with its reason and a task to the claims role, so it no longer stops silently.
- **Not built** until decided.
- **Resolved (24 October 2026, decided by Ade):**
  - **The rule:** a Life lump-sum claim (death, and disability where the benefit is a fixed sum) gets its initial reserve automatically at registration, equal to the sum assured of each claimed cover. The assessor adjusts it with a reason, under the existing Claims Framework reserve rules.
  - **Built:**
    - lump sum means the benefit type's `payout_type` is `LUMP_SUM` in `life.ref_life_benefit_types`;
    - the claimed covers are the insured life's benefits of that type in force on the event date;
    - the reserve type is `Life:Claims:InitialReserveType`, `INDEMNITY` by default;
    - the reserve is set through the Claims Framework at registration, once (a repeat registration leaves it);
    - every reserve move now needs a reason (`ClaimsMoveReserve`), which the audit trail records.
  - **Note:** the Claims Framework has no reserve authority limit today: the authority rules apply when a payment is approved and released, not when a reserve moves. Any reserve limit by role is a requirement to add.
  - **Closed.**

### OQ-080 What a product scope means for Health records (Phase 8 Wave 0b, F103)
- **Sources:**
  - SRS-003 (access by product and organisation);
  - `docs/specs/addenda/access-scopes.md` §1: "memberships and medical claims (Health, by the scheme's product)".
- **What the code and schema have:**
  - a Health scheme (`health.scheme`) has a sponsor and no product;
  - a membership has a plan and option (`health.cfg_plan_definitions`), not a Product Factory product. Health never calls the Product Factory's sellable products.
- **Gap:** "the scheme's product" does not exist, so a product scope (`scope_products`, which holds Product Factory product codes) has nothing to match on a Health record.
- **Options:**
  1. **A plan is the product for Health:** a product scope lists plan codes, matched against the membership's plan (and a medical claim's or preauthorisation's membership).
  2. **Link plans to products:** a `product_code` on `health.cfg_plan_definitions`, and scope by that product.
  3. **Health is organisation-scoped only:** a grant restricted to products never covers a Health record.
- **Built meanwhile (fail safe):** Health records are scoped by organisation node. A grant restricted to products does not cover them, because a record with no product is outside any product scope. A grant with no product scope is unaffected.
- **Decision (24 October 2026, Ade):** keep the fail-safe for now. An addendum linking each Health plan to a Product Factory product of domain Health is proposed for sign-off (`docs/specs/addenda/health-plan-products.md`, on the next batch's branch). Not built until signed off.
- **Signed off (24 October 2026, Ade):** `docs/specs/addenda/health-plan-products.md`. Planned in Wave 0b after F062. The fail-safe stays until it is merged; then this question is closed.
- **Built (Wave 0b batch 4):**
  - plan options name their product, and enrolment needs it on sale;
  - memberships and what follows them carry it;
  - plans without one stay fail-safe and are listed on `/health`.

  **Closed** (merged, `537f4cb`, 24 October 2026).

### OQ-081 Two scope rules the access-scopes addendum leaves open (Phase 8 Wave 0b, F103)
- **Source:** `docs/specs/addenda/access-scopes.md` §1 and §4.
- **1. Billing accounts.**
  - **The addendum:** it scopes "billing accounts and invoices for a policy" by the policy's product.
  - **The gap:** an account belongs to a payer and can bill several policies, of different products and nodes.
  - **Built:**
    - an invoice is scoped by its own policy's product and node;
    - an account, and its refunds, are in scope when **any** of its schedules is;
    - the account's invoice list shows only the invoices in scope;
    - a schedule created by hand has no product or node, so it is outside every restricted scope.
  - **Alternatives:**
    - in scope only when **all** of its schedules are;
    - accounts not scoped, like parties.
- **2. Approvals.**
  - **The addendum:** "a checker must have the permission in scope for the record".
  - **The gap:** it does not say which permission.
  - **Built:**
    - the checker's scope for the approval permission they use (`approval.read` to see it, `approval.approve` to decide it) must cover the record's product and node;
    - Workflow records them when the approval is requested, from the module that owns the record.
  - **Alternative:** the scope of the record's own permission, for example `claim_payment.release` for a payment instruction.
- **Confirm or change.** Either change is local to Billing's repository or Workflow's decision.
- **Resolved (24 October 2026, decided by Ade):**
  - **Approvals:** confirmed as built. A checker must be in scope for the approval they decide.
  - **Billing accounts:** confirmed, with a condition: a user never sees another branch's or product's figures through a shared account.
  - **Built for the condition:**
    - a caller who covers only part of an account sees only its schedules in scope;
    - the account-level arrears are left out, and the view says `partlyInScope`;
    - the account's invoice list shows only invoices in scope;
    - account-wide operations need the whole account in scope (ageing, closing, issuing due invoices, refunds, new schedules), else 403 `OUT_OF_SCOPE`;
    - a refund is seen only by a caller covering the whole account;
    - payments against an invoice, an account or a refund are shown only when the caller covers all of that record (`IBusinessRecordScopes`).
  - **Tested** with one account billing policies in two branches.
  - **Closed.**


### OQ-082 Choices the channel addendum leaves open (Phase 8 Wave 0b, F005)
- **Source:** `docs/specs/addenda/channel-permissions.md` §3.
- **Built as follows; confirm or change:**
  1. **Several roles:** the caller's roles combine. A transaction and product are allowed when **any** of the roles has an allowed row. A row with `allowed = false` grants nothing but does not override another role's allowed row.
  2. **Approval with several roles:** a product needs approval only when every allowed row covering it requires approval. One row allowing it without approval is enough.
  3. **An empty `product_code`** on a row means every product the channel enables.
  4. **Health records** have no product (OQ-080). On a channel that limits products, a Health command is refused with `CHANNEL_NOT_ALLOWED`. On a channel that enables every product, with a row for every product, it is allowed.
  5. **Life proposals:** where the tenant has channels, the request's own channel is recorded, and the `channelCode` in the body is ignored. The body field stays for tenants without channels.
- **Not cached:** the channel tables are read on each channel-scoped command, in one small query. A cache like the permission cache (ADR-006) can follow if load needs it.
- **Resolved (24 October 2026, Ade):** confirmed as built:
  - roles combine;
  - approval is required only when every allowed row requires it;
  - Health is refused on product-limited channels until OQ-080 is built;
  - Life proposals record the token's channel.

  **Closed.**

### OQ-083 Regulatory return submission deadlines (Phase 8 Wave 0b, GAP-046)
- **Source:** `docs/specs/addenda/dashboard-figures.md`, decision 4.
- **The gap:** neither the SRS nor `reporting.cfg_regulatory_return_definitions` says by when each return must be submitted to its regulator, so no return can be shown as due or overdue.
- **Decided (24 October 2026, Ade):** the deadline becomes tenant configuration on each return definition, set by compliance. No deadline is invented in code, seed data or tests other than as test data.
- **Open:** the actual deadline of each return the tenants file (for example the Prudential Authority's quarterly and annual returns), and whether a deadline counts calendar or business days after the period end.

### OQ-084 What a member sees of a Health membership's claims (Phase 8 Wave 0b, external-user access)
- **Source:** `docs/specs/addenda/external-user-access.md` §2: Health records are in scope for "the principal member or a dependant of the membership, or the patient on a preauthorisation or medical claim".
- **Built:** a member sees the medical claims where they are the patient; the principal also sees every claim of the membership; a dependant does not see the principal's or another dependant's claims. The view leaves out diagnosis codes.
- **Why:** medical data is the most sensitive personal information (POPIA section 26); an adult dependant's claims are not obviously the principal's to see, but the principal is responsible for the membership and its statements.
- **Confirm or change:** for example, a dependant aged 18 or over hidden from the principal, or the principal seeing only claim totals.

### OQ-085 Which role reviews aggregation alerts (Phase 8 Wave 1, GAP-051)
- **Source:** front-end request 49 (GAP-051), and your direction of 24 October 2026: "open a Workflow task for the underwriting role".
- **The gap:** no underwriting role is defined in the specifications, the static data catalogue or the role seed. Roles are each tenant's own, and General's referrals are decided by permission, not routed to a role.
- **Built:** the role is a platform setting, `General:Aggregation:AlertRole`, set to `UNDERWRITING`. Its tasks go to that queue. Empty, alerts are recorded and listed, but no task is opened.
- **Confirm:** the role code, and whether each tenant sets its own, for example as a column on `general.cfg_aggregation_zones_and_limits` or a tenant setting.
- **Decided (25 October 2026, Ade):** `UNDERWRITING` is the default, and each tenant can change it. **Closed** (the tenant setting is built with Communications).

### OQ-086 Which claim statuses notify the claimant (Phase 8 Wave 1, F101, F031)
- **Source:** `docs/specs/addenda/communications.md` §6.3. F101 is "Not specified" in the coverage list, and the SRS has no notification requirements.
- **Proposed:**
  - Life: Registered, Approved, Declined, Settled.
  - General: Registered, Approved, Declined, Settled, Closed.
  - Health (the member): Adjudicated, Reversed, and preauthorisation approved or rejected.
  - Amounts appear only on settlement, and for Health only as a total.
- **Confirm or change.**
- **Decided (25 October 2026, Ade):** as proposed. **Open for compliance:** whether a decline notice must give the reasons and the policyholder's rights under the Policyholder Protection Rules.

### OQ-087 Opting out, and POPIA section 69 choices (Phase 8 Wave 1, F078)
- **Source:** `communications.md` §4 and §7. The section 69 summary there is for compliance to confirm.
- **Proposed:** service notices cannot be opted out of entirely (one channel stays). Marketing needs consent from every party, so the section 69(3) customer exception is not used.
- **Confirm:**
  - both choices;
  - that the "prescribed manner and form" for a consent request is Form 4 of the POPIA Regulations, 2018;
  - whether the Consumer Protection Act section 11 and ECTA section 45 add anything.
- **Decided (25 October 2026, Ade):** as proposed. **Open for compliance:** Form 4, CPA section 11 and ECTA section 45.

### OQ-088 Messaging providers and push devices (Phase 8 Wave 1, F095–F098)
- **Source:** `communications.md` §3. SRS §20.2 names "Email, SMS, approved messaging providers" but no provider. Push (F097) and WhatsApp (F098) have no basis in the specifications.
- **Built in Wave 1:** each channel behind an adapter, with stubs.
- **Open:** the providers for each tenant, and whether a portal mobile app will exist to register devices for push.
- **Decided (25 October 2026, Ade):** stubs only for now. Providers are chosen later. Push stays stubbed until a portal app exists.

### OQ-089 Reminder schedules (Phase 8 Wave 1, F099, F100)
- **Source:** `communications.md` §6.1–6.2.
- **The gap:**
  - Arrears buckets give the reminder and final-notice actions, but nothing gives a reminder before the due date.
  - `general.cfg_renewal_rules` has one `notice_days` and no reminders between invitation and renewal.
  - Health and Life scheme renewals have no template or recipient.
- **Open:** whether pre-due reminders are wanted, and their days; renewal reminder days (proposed: `reminder_days` on the renewal rule); scheme renewal notices.
- **Decided (25 October 2026, Ade):** `reminder_days` on the General renewal rules, empty by default. No pre-due premium reminders in Wave 1. Scheme renewals wait until they are specified. **Closed.**

### OQ-090 One list of channel codes (Phase 8 Wave 1)
- **Source:** the Wave 1 research.
- **The gap:** channel codes differ across tables:
  - `document.cfg_delivery_channel_settings`: `EMAIL`, `SMS`, `POST`, `PORTAL`;
  - `life.cfg_notification_lead_times`: `EMAIL`, `SMS`, `POST`;
  - `health.cfg_remittance_advice_formats`: `EMAIL`, `PORTAL`, `FILE`;
  - `claims.claim_communication.channel`: free text.
- **Proposed:** a shared `communication.ref_channels`, aligned module by module when each is next changed.
- **Decided (25 October 2026, Ade):** agreed. **Closed.**

### OQ-091 Feature codes for function entitlements (function catalogue, ADR-026)
- **Source:** `docs/specs/addenda/function-catalogue.md` §2. `tenant.cfg_feature_entitlements.feature` has no seeded codes, so no function can name one yet.
- **Proposed:** every function starts with no required feature until the features are agreed.
- **Open:** the list of features, and which functions each one gates.
- **Decided (25 October 2026, Ade):** LIFE, GENERAL, HEALTH, REINSURANCE, BROKER_PORTAL, PROVIDER_PORTAL, CUSTOMER_PORTAL; a function with none is base platform. **Closed.**

### OQ-092 The menu's domains and sub-domains (function catalogue, ADR-026)
- **Source:** function-catalogue §6.
- **Proposed:** the areas and capabilities of the platform function list (`docs/traceability/functional-coverage.md`), plus a Configuration domain for static data and a Platform domain for the operator. The source list of 103 functions is not in the repository.
- **Open:** confirm the taxonomy and its labels.
- **Decided (25 October 2026, Ade):** the 12 domains and 54 sub-domains of `docs/specs/INSYS_NAVIGATION.md`, with process areas from the same file, plus Configuration; portal sub-domains under PORTALS. **Closed.**

### OQ-093 Static data functions per table or per sub-module (function catalogue, ADR-026)
- **Source:** function-catalogue §8.
- **Proposed:** per table (view, maintain, approve): about 880 functions, with per-table grants.
- **Alternative:** per catalogue sub-module: about 60, but with coarser grants.
- **Decided (25 October 2026, Ade):** per catalogue sub-module. **Closed.**

### OQ-094 How marketing is requested, and replies that stop it (Phase 8 Wave 1, communications)
- **Source:** the Wave 1 API review.
- **Built:** marketing can be requested only through `communication.NotificationRequested` from a module; there is no campaign operation. A STOP reply or an unsubscribe link has no inbound path; opting out is through preferences or Party's consent ledger.
- **Open:**
  - whether tenants run marketing campaigns in INSYS, and how they target them;
  - how replies such as SMS STOP are received, which depends on the providers (OQ-088);
  - what `{opt_out_contact}` is per tenant.

### OQ-095 Static data functions by catalogue module, not sub-module (function catalogue, ADR-026)
- **Source:** building the catalogue (Wave 1 batch 3).
- **The gap:** decision 4 says per catalogue sub-module, expected to be about 60. The catalogue has 251 sub-modules for 263 tables, so per sub-module is almost per table (about 750 functions). The "about 60" was the number of catalogue modules.
- **Built:** view, maintain and approve per catalogue module: the 58 modules that have tables, 174 functions, in one Configuration sub-domain per INSYS module (23).
- **Open:** confirm per module, or switch to per sub-module.
- **Decided (25 October 2026, Ade):** per catalogue module (174 functions). **Closed.**

### OQ-096 A role with only one of static_data.create and static_data.update (function catalogue §7)
- **Source:** the before-and-after test.
- **The gap:** a static data module's maintain function needs both permissions, so a role holding only one is given neither when converted. Every other permission is granted on its own by some function, so nothing else is lost.
- **Built:** least privilege: such a role loses that permission. No role the platform seeds has only one.
- **Open:** keep that, or give such roles the maintain function, which adds the other permission.
- **Decided (25 October 2026, Ade):** least privilege: such a role loses the permission it held alone. **Closed.**

### OQ-097 The code for a privileged role and a user without MFA (function catalogue §4)
- **Source:** function-catalogue §4 names `MFA_ENROLMENT_REQUIRED` (422).
- **The gap:** assigning a privileged role to a user without MFA is already refused with `MFA_REQUIRED` (409, IAM-003). Two codes for one rule would split the front end's handling.
- **Built:** a role holding a privileged function is privileged, and the existing `MFA_REQUIRED` (409) applies.
- **Open:** keep `MFA_REQUIRED`, or rename it to `MFA_ENROLMENT_REQUIRED` (422) for both cases (breaking).
- **Decided (25 October 2026, Ade):** `MFA_REQUIRED` (409) for both cases. **Closed.**

### OQ-098 Which background work a tenant can switch off (function catalogue §2)
- **Built:** JOB functions for the Life, General, Health, Reinsurance, Communications, Analytics, Distribution and Compliance event consumers, and for the Life, General, Health, Billing and Payments daily jobs, notification sending and webhook delivery (15 in all).
- **Not switchable:** what keeps the platform consistent or lawful: the outbox relay, parking, Finance postings, Billing and Payments consumers, Claims Framework, Party, Documents, Product Factory and Rating consumers, privileged access expiry, search log retention, key rotation and partition maintenance.
- **Open:** confirm the list.
- **Decided (25 October 2026, Ade):** the list is confirmed, with two conditions:
  - switching off a tenant's Billing or Payments daily job is a privileged function;
  - while any JOB function is off for a tenant, it shows in the operations view and in `/health` detail.
- **Built (Wave 1 batch 4):** the privileged function `TENANT_SWITCH_OFF_MONEY_JOBS`, checked when a version switching either job off is submitted; `JOB_FUNCTIONS_OFF` in the Integration operations figures; the `job-functions` check in `/health` detail. **Closed.**

### OQ-099 The first tenant administrator's request names permissions (function catalogue §7)
- **Built:** the request still names permissions, so the API is unchanged. They are converted to the functions that need only them, and a permission no such function grants is refused (422 `VALIDATION_FAILED` on `permissions`).
- **Open:** whether the request should name functions instead (breaking).
- **Decided (25 October 2026, Ade):** the request names functions instead of permissions (breaking, in `contract-changes.md`); nothing consumes the API yet.
- **Built (Wave 1 batch 4):** `functions` on the request (V0054 renames the column and converts requests already made); each must be a staff function in force. **Closed.**

### OQ-100 Function and menu labels (function catalogue §3)
- **Built:** English labels: domains and sub-domains from `INSYS_NAVIGATION.md`, functions made from their codes ("Life policies"). The menu returns a translation key and the label in the request's language.
- **Open:** the wording of function labels, and their translations.
- **Decided (25 October 2026, Ade):** generated English labels stay for now. The business reviews the wording before go-live, and translations need professional review (Workstream B). **Open: a pre-go-live item.**

### OQ-101 Which regime Health cases follow (Wave 2 cases, `addenda/cases.md` §6)
- **The gap:** a medical scheme settles disputes through its own rules and the Council for Medical Schemes (Medical Schemes Act s29(1)(j), s47, s48, s50); a health insurance product under the Insurance Acts follows the PPR and the NFO.
- **Open:** whether INSYS Health serves medical schemes, health insurance products, or both, which decides the Health claim appeal path (F032).
- **Decided (25 October 2026, Ade; the cases addendum signed off):** both, by configuration. Each case type names its appeal route; insurer tenants use the PPR and the NFO, medical-scheme tenants the CMS. **Closed.**

### OQ-102 Partial approval of a claim (F010)
- **Built:** a settlement below the claimed amount, with a rationale. **Open:** whether a partial approval is its own outcome.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** a partial approval is its own claim outcome (settled below the claimed amount, with a required reason), and the declined part sends `CLAIM_DECISION_RIGHTS`. **Closed.**

### OQ-103 Reopening Life and Health claims (F013)
- **Built:** General only. **Proposed:** Life and Health gain a reopen for reassessment after an upheld dispute or appeal (`addenda/cases.md` §2). **Open:** confirm, and their states.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** yes: Life and Health claims gain a reopen, for upheld disputes. **Closed.**

### OQ-104 Redress payments (PPR 18.8.3)
- **Open:** whether compensation and goodwill payments decided on a complaint are paid through INSYS (Payments, maker-checker) or only recorded.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** redress is paid through Payments, with maker-checker. **Closed.**

### OQ-105 Complaint reporting to the Authority (PPR 18.11)
- **Open:** the prescribed format and frequency; not verified. The report of `addenda/cases.md` §8 gives the 18.8.3 data per category.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** stays open, for compliance. **Open.**

### OQ-106 How long case records are kept
- **Open:** Rule 18 states no period; FAIS requires complaint records for at least 5 years. For compliance to set, with POPIA s14.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** 5 years after closure, configurable; a legal hold overrides it. For compliance to confirm. **Closed for the build.**

### OQ-107 Complaints taken by staff for a caller
- **Open:** whether a complaint staff record from a call or visit needs the complainant's confirmation before it counts.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** no confirmation step: the acknowledgement to the complainant is the confirmation. **Closed.**

### OQ-108 What a broker sees of its clients' cases
- **Proposed:** cases it raised, and those on records it brokers that staff mark shareable. **Open:** confirm.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** a broker sees only the cases it raised, unless staff mark a case shared with the broker. **Closed.**

### OQ-109 Privileged case functions
- **Proposed:** deciding an appeal and recording redress. **Open:** confirm.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** privileged: `CASE_DECIDE` on an appeal, `CASE_RECORD_REDRESS`, and `CASE_REOPEN` of a closed complaint. **Closed.**

### OQ-110 Onboarding readiness for cases
- **Open:** whether the case configuration (`addenda/cases.md` §10) is part of ORGANISATION_AND_ACCESS or a new step.
- **Decided (25 October 2026, Ade; the cases addendum signed off):** the case checks join `SHARED_OPERATIONAL_SETUP`; no new onboarding step. **Closed.**

### OQ-111 Default roles for static data maintenance (front-end request 54)
- **Built:** static data is maintained through `SD_{MODULE}_VIEW`, `_MAINTAIN` and `_APPROVE` functions granted to roles; scope decides who (tenant, platform, or both), tier decides approval. INSYS seeds no role beyond the first tenant administrator.
- **Proposed:** role templates per business area (product, underwriting, claims, finance, distribution, platform administration) with the static data modules each maintains, loaded by the OFT-ZA seed (Workstream F) and offered to new tenants. **Open:** confirm the templates.
- **Decided (25 October 2026, Ade):** draft default role templates (tenant administrator, underwriter, claims handler, finance officer and the others the catalogue supports) as optional starting points tenants copy and adjust; send them for sign-off; do not seed yet.
- **Drafted (Wave 2 batch 1):** `docs/specs/addenda/role-templates.md`, 20 templates generated from the catalogue, with suggested segregation-of-duties rules. **Open: for sign-off.**
- **Not signed off (25 October 2026, Ade):** revise so every template passes the SoD rules, review the privileged flag, move privileged work out of everyday templates, split the platform administrator and make terminating a tenant privileged; propose who reads Health clinical data and the search log.
- **Revised (Wave 2 batch 2):** the templates are defined in code (`tests/Insys.Testing/RoleTemplates.cs`) and `role-templates.md` is generated from them; `RoleTemplateTests` checks every template against every SoD rule and the privileged list. New: data protection officer, integration administrator, platform static data steward, and a Health auditor (proposed). `TERMINATE_TENANT` is flagged privileged. **Open, for sign-off:** the revised templates; the other 20 proposed privileged functions (flagging them makes roles holding them a `PRIVILEGED_ROLE` approval); the auditor question (a separate Health auditor is proposed).
- **Decided (25 October 2026, Ade):**
  - The 20 proposed privileged functions: approved; switched on in the same batch as the fix for front-end request BR-013 (a new tenant needs at least two administrators), not before, or every new tenant is stuck.
  - `HEALTH_AUDITOR`: approved as proposed; `AUDITOR` excludes medical information and the party search log.
  - The templates are signed off once two fixes are made: (1) the party search log only for audit roles. **Made:** only the audit roles (`AUDITOR`, `HEALTH_AUDITOR`, `COMPLIANCE_OFFICER`, `DATA_PROTECTION_OFFICER`) hold a function needing `audit_event.read`, tested; the search log is held by the compliance officer, the data protection officer and the Health auditor; the tenant and platform administrators no longer read the audit trail. (2) A second person for releasing a legal hold. **Proposed:** a new approval type `LEGAL_HOLD_RELEASE` rather than a segregation-of-duties rule (`role-templates.md`). **Decided (25 October 2026, Ade):** the tenant and platform administrators do not keep the audit trail; only the audit roles hold audit-read functions. `LEGAL_HOLD_RELEASE` is approved as a new approval type with the compliance officer as checker; the hold stays in force until approved; built in Wave 2 batch 3 with the BR-013 fix and the privileged flags, with its rows in the approval endings table. `AUDIT_RECORD_HISTORY` stays with the audit roles: `GetRecordHistory` was confirmed to check only `audit_event.read`, not that the caller may read the record. A record-scoped history read is proposed in `addenda/wave2-3-proposals.md` §1. **Open: that proposal.**
  - Deferred: OFT Insurance South Africa's templates and authority bands (Workstream F), tenants hiding templates, Keycloak step-up before the portals launch.
### OQ-112 Authority bands (front-end request 55)
- **Built:** `workflow.cfg_delegation_and_approval_authority` per role and approval type; `rules.cfg_authority_matrices` (underwriting alone) and `life.cfg_claims_authority_limits` (Life claims alone). The sample OFT-ZA values in `frontend-integration-guide.md` §3 are samples.
- **Open:**
  - the real bands, for the business to set (the samples are for the seed and demonstrations);
  - limits have no currency: needed per currency for a multi-currency tenant?
  - `delegation_allowed` is not used: is delegation of authority wanted, and how (to whom, for how long)?
  - `payment.cfg_payment_release_limits` (with `second_approver_above`) is not read: keep payment release on the Workflow table, or use it for a second approver above an amount?
- **Decided (25 October 2026, Ade):** authority limits carry a currency (the tenant's base currency by default); wire in or retire `payment.cfg_payment_release_limits` (propose which); delegation stays for later; OFT-ZA values stay samples for the business to confirm.
- **Built (Wave 2 batch 1):** `currency` on `workflow.cfg_delegation_and_approval_authority` (V0056); a limit counts only for approvals in its currency, and one without a currency is in the tenant's base currency.
- **Proposed: retire `payment.cfg_payment_release_limits`.** Payout release is a Workflow approval (`PAYOUT_RELEASE`), whose limits are the authority rows, now with a currency. The table's `second_approver_above` (a second checker above an amount) has no counterpart in the approval model, which has one checker; if two checkers above an amount are wanted, they belong in the Workflow authority model for every approval type, not in one Payments table. Retiring it removes its generated endpoints (a migration and a contract change after sign-off). **Open: confirm.**
- **Decided (25 October 2026, Ade):** retire the payment-release limits table through a forward migration. **Built (Wave 2 batch 2):** V0059 drops `payment.cfg_payment_release_limits`; its static data endpoints are gone. Delegation stays for later; the OFT-ZA values remain samples. **Open:** only delegation (later) and the real bands (the business).

### OQ-113 The rejection reason on the record
- **Built:** the checker's reason is kept on the Workflow approval only; the owning modules do not keep it (Static Data passes it on in its event but does not store it).
- **Open:** whether records show the latest rejection's reason themselves (a settlement request, a statement, a version), or the front end reads it from the approval.
- **Decided (25 October 2026, Ade):** yes; each record shows its latest rejection (who, when, the reason) itself.
- **Built (Wave 2 batch 1):** every record an approval is about shows `latestRejection {ending, approvalType, by, at, reason}` in its view, from Workflow's approval history; expiry and withdrawal count. **Closed.**

### OQ-114 Staff in-app notifications (front-end request 62, BR-003)
- **The gap:** the SRS has no notification requirements, and F101 is "Not specified". Communications sends email, SMS, push and WhatsApp to customers; nothing notifies staff inside the console.
- **Built:** the Workflow Inbox (tasks and approvals assigned to a user or their queues) is how staff learn of work, and the requester follow-up task (D-4) tells a maker their approval ended.
- **Open:** whether staff get in-app notifications, for which events, and whether they are read, dismissed or expire. Planned in a wave once specified.

### OQ-115 Case notices to a broker (Wave 2 cases, `addenda/cases.md` §7)
- **The gap:** a case a broker raised on a client's behalf should send the broker its notices too, but an intermediary has no party to send to: `commission.mst_intermediary_register` has no party, and Distribution has no public query for one.
- **Built:** the complainant gets every case notice; a provider gets its own disputes' notices. The broker gets none (`TODO(spec)` in Communications).
- **Proposed:** a Distribution public query `IIntermediaries.PartyOf(tenant, intermediaryCode, day)`, so no schema change, if intermediaries are parties; otherwise a `party_id` on the intermediary register. **Open.**

### OQ-116 Reasons in a Health claim's decision rights notice (OQ-102, HLT-008)
- **The gap:** PPR 17.6.3 says a repudiation notice gives reasons; HLT-008 keeps medical information to minimum necessary access. The Health `CLAIM_DECISION_RIGHTS` notice carries no reason and no amount but the total paid.
- **Open:** whether a Health partial approval's notice may carry its reason, for compliance to confirm.

- **Settled for declines (Ade, 29 October 2026):** any decision that declines all or part of a claim, Health included, sends `CLAIM_DECISION_RIGHTS` with the decline reason's customer wording. That is configured text, never the claim's reason text, lines or diagnoses, so HLT-008 holds. **Closed.**
### OQ-117 The representations period and time-bar (PPR 17.6.3)
- **The gap:** `CLAIM_DECISION_RIGHTS` should state the right to make representations within a period (at least 90 days) and any time-bar; nothing configures them, so the tenant's template wording carries them and `{representations_deadline}` is not offered on it.
- **Open:** whether these become tenant configuration (a period per claim domain), so the notice states a computed date.

### OQ-118 What remains of the case links (Wave 2 cases)
- **Built:** a link to an invoice, billing account or remittance is checked with its module and places the case in that record's product and branch (F103); portal users may link their own policies, invoices and remittances.
- **Not built:** checking a policy, claim or membership link (their modules have no record-scope query yet), so such a case is tenant-wide; a member disputing their own claim or membership from the portal; a case raised from an event (for example a claim decline), which the parking for missing timelines already allows for.
- **Proposed:** Wave 2 batch 3, after BR-013: record-scope and portal-relation queries in General, Life and Health, and a dispute raised from a claim decline if wanted. **Open: confirm.**

### OQ-119 A tenant's administrators (front-end requests BR-013 and BR-018)
- **The gap (BR-013, defect):** a new tenant's only administrator holds a privileged role, which no one else in the tenant can approve for a second person, and every static data change needs a second person too; with the approved privileged flags the tenant could configure nothing.
- **Decided (25–26 October 2026, Ade):**
  - The first-administrator request names two people. A platform administrator never approves anything inside the tenant.
  - Onboarding readiness requires at least two active administrators before activation. **"Active administrator" means:** an active TENANT_ADMIN assignment, the identity linked, and at least one successful authenticated request (the user's first request signs them in and sets `last_login_at`).
  - A platform operator may request an additional administrator for an existing tenant, approved by a second platform administrator, never inside the tenant.
  - BR-018: a request is Provisioned only once each administrator's Keycloak identity is linked. Until then it reads AwaitingIdentity, IdentityParked (the creation waits on Keycloak and is retried) or IdentityFailed (dead-lettered, or refused for good, for example a username conflict, with the reason); after the cause is fixed an operator's retry returns it to AwaitingIdentity. A user whose identity is not linked yet is refused with IDENTITY_NOT_LINKED (retryable, Retry-After), not FORBIDDEN; an unknown subject is not cached; linking evicts the user's cached permissions.
- **Built (Wave 2 batch 3):** V0062 (`iam.tenant_administrator_person`, the request's `kind`); `POST /identity/tenant-administrators` (two administrators), `POST …/additional`, `POST …/{id}/retry-identity`; the `FIRST_ADMINISTRATOR` readiness check counts active administrators.
- **Found while building the end-to-end test (26 October 2026), for confirmation:** two administrators were not enough on their own. Workflow refuses a checker with no authority row for the approval type (WFL-004, `APPROVAL_AUTHORITY_MISSING`), a new tenant has none, and adding one is itself a Tier 1 change that needs authority. **Built:** when the first administrators are provisioned, Workflow puts in force the tenant administrator's authority as `role-templates.md` lists it (`PRIVILEGED_ROLE`, `EXTERNAL_SUBJECT_LINK`, `STATIC_DATA_T1`, `STATIC_DATA_T2`, for `TENANT_ADMIN`), approved by the same two platform administrators' approval, as the role and its grants are (SDM-003). It is done only when the tenant has no authority row for the role. **Open: Ade to confirm.** Otherwise **closed.**

### OQ-120 When a party's retention has ended (front-end request D-34)
- **The gap (D-34, before go-live):** AnonymiseParty moved a Deceased or Dormant party to Anonymised ("Retention ended") without checking anything. The specification gives no retention period for policies, claims or payments themselves. Document retention schedules cover documents and audit retention covers audit events.
- **Design accepted (27 October 2026, Ade):**
  - Fail closed when no retention period is configured.
  - Records of parties merged into this one count.
  - The refusal is 409 `PARTY_ANONYMISATION_BLOCKED`, with `blocks` documented in `contract-changes.md`.
- **Working default (27 October 2026, Ade), for compliance to confirm before go-live. It is not a requirement:**
  - **The rule:** a party may be anonymised only when every linked policy, claim and payment has passed 7 years from the business date on which it reached its final status (from its status history), and no legal hold applies.
  - **Basis:** FICA s23 (at least 5 years from the end of the relationship or transaction) and Companies Act s24(1) (7 years for company records, including accounting records).
  - **Per-tenant configuration:** `party.cfg_record_retention_policies` (V0064; CFG, scope GT, Tier 1). It has one row per record kind: `POLICY` (policies and memberships), `CLAIM` and `PAYMENT`. The platform's rows hold the default, 7 years each, and a tenant's own rows replace them. Where several jurisdictions apply, the longest period wins.
- **Built (Wave 2 batch 3):**
  - **Final statuses:**
    - Life policy: TERMINATED, SURRENDERED, CLAIMED or MATURED. A lapsed policy can be reinstated, so it is open.
    - General policy: CANCELLED, EXPIRED or RENEWED.
    - Health membership: TERMINATED.
    - Life and General claims: CLOSED or DECLINED. A reopened claim is open again.
    - Health claim: PAID, REJECTED or REVERSED.
    - Payment: SUCCEEDED, FAILED or REVERSED.
  - **Status history:**
    - V0064 adds a status history for Life and General policies and claims, Health memberships and medical claims. A trigger writes it on every insert and status change, and it is insert-only. Payments already had one.
    - Records that existed before V0064 get one row: their current status, dated by their last change.
    - The date used is the business date, in the tenant's time zone, of the latest entry into the record's current final status.
  - **Audit evidence:** the audit evidence about the party is kept for the longest audit class (`audit.cfg_audit_retention_policies`) from its latest event, as D-34 asked. An event's class is not recorded.
  - **Legal holds:** a hold in force on a document of the party or of any of these records blocks. So would a hold scoped to the party or the record. Placing those is proposed for the next batch (`addenda/wave2-3-proposals.md` §4).
- **Not covered:** proposals and quotes, billing accounts, cases, communications and identity links name a party but were not in D-34. They do not block.
- **Erasure for a living party** stays with the Wave 6 data-subject requests. This command is only for a party that is deceased or dormant.
- **Open:**
  - Compliance to confirm the 7 years and the final statuses before go-live.
  - Whether audit evidence should keep blocking once the record periods are confirmed.

### OQ-121 Party code fields with no named list (BR-026, 28 October 2026)
- **The gap:** BR-026 names each Party code field's static data table in the contract (`x-code-list`). Five fields have no list in the catalogue to name:
  - **Title and gender:** they belong in `party.ref_personal_attribute_code_lists`, but the catalogue gives a list code only for marital status (`MARITAL_STATUS`). The contract names the table without a list (`x-code-list-filter`) for these two.
  - **Address type, consent capture channel and an organisation's industry code:** no table holds them. They are checked only for length; the contract declares nothing for them. Industry codes might be `general.ref_industry_classification_codes` (General's), but Party does not use it.
- **Working position:** none of the five is checked against a list, as before BR-026.
- **Open, for the business:**
  - the list codes for title and gender;
  - whether address type and capture channel are fixed lists (enums) or static data;
  - which classification an organisation's industry code uses.

### OQ-122 A General claim party's role has no list (BR-036, 28 October 2026)
- **The gap:** BR-036 declares the values or the code table of every General and claims code field the front end listed (its GAP-034). A claim party's `role` (`GeneralAddClaimParty`, `ClaimPartyView`) is checked only for length. The specification names no list for it, and INSYS itself writes none.
- **Working position:** the role is free text, as before. Tests use `CLAIMANT` and `WITNESS`.
- **Open, for the business:** whether claim party roles are a fixed list (an enum) or come from `party.ref_party_role_types` (its `GENERAL` domain roles), which would then also be checked.
- **Decided (28 October 2026, Ade):** a tenant-maintained static data table (CFG, tenant scope), seeded with a starter list for the claims team to confirm; the field names that table. **Built (Wave 2 batch 5):** V0075 adds `general.cfg_claim_party_roles` (`code`, `label`), seeded as drafts in each existing tenant: `CLAIMANT`, `INSURED`, `THIRD_PARTY`, `DRIVER`, `WITNESS`, `ASSESSOR`, `REPAIRER`, `LEGAL_REP` (a role is at most 15 characters, so "legal representative" is the label) and `PAYEE`. `GeneralAddClaimParty` accepts only a role in force (422 `GENERAL_CODE_NOT_IN_FORCE`), and the field declares `x-code-list`. **Open, for the claims team:** confirm or change the starter list. A tenant created after V0075 starts with no roles, so it adds or imports them during onboarding.

### OQ-123 to OQ-128 Customer due diligence (Wave 3, `addenda/due-diligence.md`, 29 October 2026)
- **The gap:** KYC, CDD with sanctions and PEP screening, and customer risk classification (F002, F003, F077) are not in the SRS. Identity verification (F001) has only a hand-recorded reference.
- **Proposed:** the addendum. It puts due diligence in Party, uses provider adapters with stubs, reviews hits in Workflow, and gates steps by configuration.
- **Open, for Ade (addendum §8):**
  - **OQ-123:** the gates, by domain and step.
  - **OQ-124:** the risk classes and review intervals.
  - **OQ-125:** when to re-screen.
  - **OQ-126:** who approves enhanced due diligence.
  - **OQ-127:** how staff are told of a duplicate.
  - **OQ-128:** how long due diligence records are kept.
- **Decided (29 October 2026, Ade):** the addendum is signed off.
  - **OQ-123:** the defaults stand; the Life issue gate is on by default for LIFE tenants, and readiness flags it if it is switched off.
  - **OQ-124:** 5, 3 and 1 years, tenant-configurable.
  - **OQ-125:** re-screen on list updates, before each payout, and monthly in full as a fallback.
  - **OQ-126:** `PARTY_APPROVE_ENHANCED_DD`, privileged.
  - **OQ-127:** a 409 with the candidates, and an override with a reason.
  - **OQ-128:** 7 years after the last policy, claim or payment ends; a legal hold overrides it.
  - Adverse media screening is off by default (POPIA special personal information, for compliance to confirm).

### OQ-129 The dated end-to-end jobs run slow on the 30th clock (CI, 29 October 2026)
- **Seen:** on 29 October, the CI job running the process flows on the 30th clock failed or ran long on three runs on two branches, while main had passed it three times that morning. Batch 2's first run timed out one request after 100 seconds in `TheLedger_IsFoundAndExplained` (GET `/openapi/core.json`), on the 30th and the 31st. BR-103's first run was cancelled at the job's 30-minute limit with no test reporting. Reruns on the same heads passed. A diagnostic run then passed every date, the 30th fastest (14 minutes).
- **Ruled out:**
  - **Batch 2:** main plus a small fix (BR-103) also stalled.
  - **Month-end work:** the stall logs show the same pattern on every date. The slow tests are CPU-bound in the test process with no database session active, no lock and no wait. Business dates 1 March and 1 April passed; 30 January also passed in the diagnostic run.
  - **The two clocks:** only the callback endpoints' cut-over compared a time the application wrote with the database's `now()`, on a path these tests do not take. It now uses the application's clock (V0093), as does partition maintenance; guard tests keep it so (`TimeSourceTests`, `RecordTimeSourceTests`).
  - **The OpenAPI document:** it builds in under half a second on both branches when the machine is not busy.
  - **A hang on a developer machine:** both branches pass the 30th clock locally on 2 CPUs (10 and 12 minutes).
- **What the logs do show:** CI runners have 2 cores. Each end-to-end test starts its own host, and a host takes 47 to 124 seconds to build its router on a runner. While several build at once, the thread pool stays busy (8 or 9 of 13 threads) with nothing in the database. A request that needs CPU then waits, and the API description document, built on request, is the largest. The stall logging stays on the dated jobs (`stall-<date>` artifacts). The 30-minute limit is not raised.
- **Open:** confirm which in-process work starves the request, then reduce it (hosts shared within a class, the router built once per process, or the OpenAPI document cached). The document endpoint is anonymous and builds the document on every request; cache it, or serve the committed document, before production (proposed, for Ade).
- **Status:** open, investigated alongside Wave 3 batch 3.

### OQ-130 to OQ-136 Renewals and retention (Wave 4, `addenda/renewals-and-retention.md`, 29 October 2026)
**Decided (Ade, 29 October 2026): every recommendation accepted** (the addendum's header gives each decision and the three additions). OQ-130 (a), AUTOMATIC default, the worse-terms threshold a tenant setting; OQ-131 (a), factor 1.0; OQ-132 31 days and (b); OQ-133 (a), no platform default; OQ-134 to OQ-136 (a).
- **OQ-130:** which products renew by invitation and which automatically (per product: automatic, on acceptance, on payment), and when worse terms need acceptance.
- **OQ-131:** how claims history affects renewal terms (a claims factor table and referral rules).
- **OQ-132:** non-renewal notice periods, and what happens when a non-renewal is decided too late for its notice.
- **OQ-133:** the source of the short-period refund scale (OQ-059's open point).
- **OQ-134 to OQ-136 (B-001):** whether a broker of record change needs a second person; commission across the effective date; who sees a future-dated change.

### OQ-137 Posting a claim's premium offset (Wave 4 batch 4.1, 29 October 2026)
- **The gap:** with a tenant's `deduct_arrears_from_claims`, a General claim in grace is paid less the policy's arrears, and Billing settles them from it. Finance posts the net claim payment (ClaimPaymentCompleted) but not the offset: claims payable against premium receivable. The claim's financials record the net payment.
- **Decided (Ade, 29 October 2026): post gross.**
  - **Postings:** the claim payment is recorded gross (claims paid = gross); the bank pays the net; a non-cash premium receipt for the deducted arrears clears the premium receivable. Each goes through posting rules: `CLAIM_PAID` at the gross, `PREMIUM_OFFSET` for the deduction. The tenant configures the accounts; finance to confirm them.
  - **Records:** the claim's financial records show gross, deduction and net. Reinsurance recoveries and claims figures use the gross. Commission treats the deducted premium as collected.
  - **Notice:** the customer's settlement notice shows gross, deduction and net.
  - **Setting:** the deduction stays off by default until a tenant configures it. **Closed; built in Wave 4 batch 4.1.**

### OQ-138 What ends a periodic due diligence review (GAP-113, 30 October 2026)
- **The gap:** the due diligence addendum (§4.3) said an expired party is not cleared but not what ends its review. As built, any fresh screening (the monthly automatic re-screen included) cleared it, and the system closed the review task; completing the task did nothing.
- **Decided (Ade, 30 October 2026):**
  - each customer risk class has a review mode, `PERSON` (default) or `AUTOMATIC`. `PERSON`: only a person with the review function ends the review, through Party, with a fresh screening as part of it; `AUTOMATIC`: a fresh screening ends it, with the system requester recorded as the reviewer;
  - the Workflow review task cannot be completed directly; it closes when the party clears;
  - the review does not redo identity or address checks; an identifier may record its document's expiry date, and a review cannot complete while the identity check's document has expired with no newer one of its kind on file (`DD_IDENTITY_DOCUMENT_EXPIRED`);
  - the reviewer counts as a contributor (cannot give that party's enhanced approval);
  - `next_review_on` counts from the day the review completes;
  - readiness: with the gates on, a role holds the review function.
- Recorded in the due diligence addendum. **Closed; built in its own batch after Wave 4 batch 4.2.**

### OQ-139 to OQ-146 Providers and care management (Wave 5a addendum, 30 October 2026)
**Decided (Ade, 30 October 2026), in `docs/specs/addenda/providers-and-care.md` §8. Every recommendation accepted, with three additions.**
- **OQ-139:** (a) deducted from later runs, invoiced by exception; 90 days before it goes to a person, tenant-set. Settles OQ-061's open point. **Addition:** only with a basis: an offset flag on the provider agreement, true by default for a medical scheme (Medical Schemes Act s 59(3)) and false otherwise. Without it, the recovery is invoiced. **Compliance still to confirm** applicability and any time limit.
- **OQ-140:** (a) a Health aggregate, using Workflow for tasks and approvals, changing the plan. Disputes stay Workflow cases.
- **OQ-141:** the addendum's measures; providers see nothing at first.
- **OQ-142:** a fixed INSYS CSV layout, plus manual entry.
- **OQ-143:** the case manager and holders of the clinical function only, every read audited. **Addition:** reassigning ends the previous case manager's access, tested.
- **OQ-144:** recorded only in this wave.
- **OQ-145:** a person decides each. **Addition:** never dropped; a write-off needs a second person and a reason.
- **OQ-146:** held from offsets until the dispute ends.

### OQ-147 to OQ-153, OQ-161 Benefit programmes (Wave 5b addendum, 31 October 2026)
**Decided (Ade, 31 October 2026), in `docs/specs/addenda/benefit-programmes.md` §8. Every recommendation accepted.**
- **OQ-147:** INSYS fields on the existing claim API. **Addition:** a switch adapter before go-live (B-012).
- **OQ-148:** record only, checked against the provider register for a pharmacist already in it. Compliance to confirm.
- **OQ-149:** paid at the plan's non-formulary co-payment, per plan, defaulting to rejected.
- **OQ-150:** a person with the clinical function decides each chronic registration.
- **OQ-151:** a per-plan percentage of days' supply, default 10%.
- **OQ-152:** the member pays above the reference price; generics are not required.
- **OQ-153:** a tenant-loaded list, with an import.
- **OQ-161:** the principal member does not see an adult dependant's medicines, chronic conditions or diagnoses; a minor's records stay visible to the principal member. **Compliance to confirm.**

### OQ-054, OQ-058, OQ-154 to OQ-160 Finance and compliance (Wave 6 addendum, 31 October 2026)
**Decided (Ade, 31 October 2026), in `docs/specs/addenda/finance-and-compliance.md` §8. Every recommendation accepted.**
- **OQ-054:** a tenant-set period per record type, from the relationship's end; compliance to give the defaults; anonymising stays a person's act.
- **OQ-058 (tax):** from the existing rates, by product and date.
- **OQ-154:** an exposure limit alerts only.
- **OQ-155:** payables stop at payouts and a report of what is due. F054 stays partial on purpose.
- **OQ-156:** VAT per product through the tax rate records; finance to confirm each.
- **OQ-157:** quote, inception, renewal and claim decision; compliance to confirm wording and points.  A missing text at one of these blocks readiness for each country a product is sold in; other points warn (Ade, 31 October 2026).
- **OQ-158:** parties and policies. **Addition:** open claims and outstanding premiums later (B-013).
- **OQ-159:** 30 days by default; compliance to confirm.
- **OQ-160:** investigators and holders of a fraud function only, every read audited.

### 5a.3 decisions (Ade, 31 October 2026)
All eight in `docs/reports/2026-10-30-wave5a-batch3.md` accepted as recommended, including the credit note posting (debit `1300`).
