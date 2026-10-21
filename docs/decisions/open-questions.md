# Open questions and decisions

Gaps and conflicts found while building, with the sources involved and the resolution used until someone decides. Precedence when sources disagree: SRS requirement text, then the SQL migrations, then `domain-model.json`, then the HTML specs (PROMPT §1). Structural decisions are in the ADRs in this folder.

Status: **Open** means a decision is needed; **Resolved** means decided (by review or ADR); **Resolved (proposal)** means the code follows the proposal shown and can change if it is rejected.

| ID | Phase | Topic | Status |
|---|---|---|---|
| OQ-001 | 4 | First domain to build | Open |
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
| OQ-020 | 0 | Field protection key custody | Open |
| OQ-021 | 0 | Test server version | Resolved: CI-equivalent runs on postgres:18 |
| OQ-022 | 1 | The tenant register has no row_version | Resolved (proposal): V0006 |
| OQ-023 | 1 | Server time zone and partition bounds | Open |
| OQ-024 | 1 | Tier 2 maker-checker switch per tenant | Open |
| OQ-025 | 1 | Impact analysis and simulation before Tier 1 activation | Open |
| OQ-026 | 1 | Bulk import endpoint path | Resolved (proposal) |
| OQ-027 | 1 | PrivilegedAccessGrant and AccessReview aggregates | Open |
| OQ-028 | 1 | Events for rejection and retirement of static data changes | Resolved (proposal) |
| OQ-029 | 1 | Tenant onboarding steps | Resolved (proposal) |
| OQ-030 | 1 | Approved data migration for an isolation profile change | Open |
| OQ-031 | 1 | Bootstrapping the platform operator | Resolved (proposal) |
| OQ-032 | 1 | Permission cache across several API instances | Open |
| OQ-033 | 1 | `sdm.change_request.status` is too short for PENDING_APPROVAL | Resolved (proposal) |
| OQ-034 | 1 | The API role needs more than INSERT on idempotency keys | Resolved (proposal) |
| OQ-035 | 1 | Static data business keys | Open |
| OQ-036 | 1 | Configuration items: Published to Effective | Open |
| OQ-037 | 1 | Identity provider to idp_code mapping | Open |
| OQ-038 | 1 | Maker-checker for privileged user administration | Open |
| OQ-039 | 1 | Repository inside iCloud Drive | Open |

---

### OQ-001 First domain to build
- **Sources:** PROMPT settings `FIRST_DOMAIN: ASK`; SRS §30.
- **Proposal:** none. Phase 4 will stop and ask.

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

### OQ-021 Test server version
- **Resolved:** the full suite runs on Testcontainers `postgres:18`. GitHub Actions is blocked on the account, so CI-equivalent runs are local for now.

### OQ-022 The tenant register has no row_version
- **Sources:** DBA-011 says mutable rows use optimistic concurrency; V0001 gives `tenant.tenant` no `row_version`.
- **Proposal:** V0006 adds `row_version`, `updated_at` and `updated_by`.

### OQ-023 Server time zone and partition bounds
- **Gap:** V0003 created its partition bounds in the server's time zone. V0005 creates new ones in UTC. On a server not running in UTC, the boundary between the two would overlap.
- **Proposal:** run every database server in UTC (DBA-013). Local and CI servers do.

### OQ-024 Tier 2 maker-checker switch per tenant
- **Sources:** SDM-003 says "unless the tenant switches it off". No setting for this exists in the catalogue.
- **Current state:** always on.

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
- **Sources:** domain-model.json against V0001.
- **Gap:**
  - `iam.privileged_access_grant` has no status or requester column, and `approved_by` is NOT NULL, so the Requested state can't be stored.
  - `iam.access_review` has no table for its ReviewItem entity.
- **Proposal:** add the columns and table in a new migration, then build both. Deferred until that is agreed.

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
- **Gap:** eviction is in-process. Other instances see a change when its event reaches them, or within the 30-second cache lifetime.
- **Proposal:** when a message broker is chosen, broadcast evictions to every instance.

### OQ-033 `sdm.change_request.status` is too short for PENDING_APPROVAL
- **Sources:** `status varchar(15)`.
- **Proposal:** store PENDING. Widen the column in a later migration if the full name is wanted.

### OQ-034 The API role needs more than INSERT on idempotency keys
- **Sources:** review item 4 grants "INSERT on integration.idempotency_key".
- **Proposal:** SELECT, INSERT, UPDATE and DELETE on that table only. Replaying, completing and releasing a key need all four.

### OQ-035 Static data business keys
- **Sources:** V0002 says "for composite natural keys, the service composes it from the key fields", but the catalogue doesn't identify the key fields.
- **Current state:** the client sends `code`.

### OQ-036 Configuration items: Published to Effective
- **Gap:** nothing yet moves a Published configuration item to Effective on its effective date. The resolver treats both as in force.

### OQ-037 Identity provider to idp_code mapping
- **Gap:** users are matched by `external_subject` only. A subject present in two identity providers is refused until a mapping from token issuer to `idp_code` exists (SRS §30).

### OQ-038 Maker-checker for privileged user administration
- **Sources:** SRS-002.
- **Gap:** role assignment is not yet maker-checker. It moves to Workflow Approval in Phase 2, with static data approvals.

### OQ-039 Repository inside iCloud Drive
- **Issue:** the repository is under `~/Documents`, which iCloud Drive syncs. iCloud created about 1,400 conflict copies such as `Foo 2.cs` and `bin/Debug 2`; the duplicate source files broke builds until removed.
- **Proposal:** move the repository out of iCloud-synced folders, or turn off "Desktop & Documents" syncing. The generator now updates files in place, and the test helpers ignore copies with spaces in their names.
