# Open questions and decisions

Gaps and conflicts found while building, with the sources involved and the resolution used until someone decides. Precedence when sources disagree: SRS requirement text, then the SQL migrations, then `domain-model.json`, then the HTML specs (PROMPT §1).

Status: **Open** means a decision is needed; **Resolved (proposal)** means the code follows the proposal shown and can change if it is rejected.

| ID | Phase | Topic | Status |
|---|---|---|---|
| OQ-001 | 4 | First domain to build | Open |
| OQ-002 | 0 | Shared platform tables in the integration schema | Resolved (proposal) |
| OQ-003 | 0 | Partitions before January 2027 do not exist | Resolved (V0003) |
| OQ-004 | 0 | One application role versus one role per module | Open |
| OQ-005 | 0 | Creating partitions ahead of need | Open |
| OQ-006 | 0 | Error code catalogue has no seed rows | Resolved (proposal) |
| OQ-007 | 0 | Permission operations for replay and discard | Resolved (proposal) |
| OQ-008 | 0 | `MessageDeadLettered` event | Resolved (proposal) |
| OQ-009 | 0 | Outbox has no failure columns | Resolved (proposal) |
| OQ-010 | 0 | Retry limit and back-off | Open |
| OQ-011 | 0 | Idempotency key retention | Open |
| OQ-012 | 0 | Page size limits | Open |
| OQ-013 | 0 | API rate limits | Open |
| OQ-014 | 0 | Identity provider claims | Open |
| OQ-015 | 0 | Auditing authorisation denials | Open |
| OQ-016 | 0 | Time zone for business dates | Open |
| OQ-017 | 0 | Event type names | Resolved (proposal) |
| OQ-018 | 0 | Missing If-Match | Resolved (proposal) |
| OQ-019 | 0 | Replay delivers before commit | Resolved (proposal) |
| OQ-020 | 0 | Field protection key custody | Open |
| OQ-021 | 0 | Local test server version | Resolved (note) |

---

### OQ-001 First domain to build
- **Sources:** PROMPT settings `FIRST_DOMAIN: ASK`; SRS §30 (open decision).
- **Gap:** Phase 4 builds one of Life, General or Health first; the SRS leaves the choice open.
- **Proposal:** none. Phase 4 will stop and ask before starting.

### OQ-002 Shared platform tables in the integration schema
- **Sources:** V0001 puts `idempotency_key` and `inbox_message` in the `integration` schema. PROMPT §2 and DBA-004 say each DbContext maps only its own schema, and only the owning module writes its schema. PROMPT §4 says idempotency keys go in `integration.idempotency_key` and every consumer uses the inbox.
- **Conflict:** every module must write rows in tables owned by the Integration module.
- **Resolution (proposal):** neither table is mapped by any DbContext. The building blocks write them with SQL: the inbox row in the consuming module's transaction (so it commits with the consumer's change), and idempotency keys in their own short transactions. Each module's database role needs INSERT on `integration.inbox_message`; the idempotency store needs its own grant. A later alternative is a per-schema inbox, which needs a new migration.

### OQ-003 Partitions before January 2027 do not exist
- **Sources:** V0001 creates one partition per partitioned table, for January 2027. DBA-023 rejects rows with no partition. Today is 21 October 2026.
- **Gap:** every audit event, journal and rating request written before 2027 would be rejected.
- **Resolution:** `V0003__monthly_partitions_2026_2027.sql` creates the monthly partitions for January 2026 to December 2027 with the parent's policy. No default partition, as DBA-023 requires.

### OQ-004 One application role versus one role per module
- **Sources:** DBA-004 (only the module's service role writes its schema); the V0001 header (grants are environment-specific and not included).
- **Gap:** a modular monolith runs as one process with one connection pool.
- **Current state:** `db/roles/app_role_grants.sql` grants one non-owner role DML on all module schemas (without BYPASSRLS, and with insert-only on audit). Module boundaries are enforced by architecture tests, not by the database.
- **Options:** keep one role until a module is extracted; or one data source per module with its own role (stronger, more connections). Decision needed before production.

### OQ-005 Creating partitions ahead of need
- **Sources:** V0001 header; DBA-018; DBA-023.
- **Gap:** who creates partitions after December 2027, and how far ahead.
- **Proposal:** a scheduled maintenance job (Phase 7 runbook) that creates partitions 3 months ahead with the same policy, alerting if the furthest partition is less than 2 months out.

### OQ-006 Error code catalogue has no seed rows
- **Sources:** PROMPT §4.13 (codes from the error code catalogue); `integration.ref_error_code_catalogue` exists in V0002 with no rows.
- **Resolution (proposal):** stable codes are defined in `ErrorCodes` (max 30 characters to fit `error_code`) and will be loaded into the catalogue through the Static Data module in Phase 1.

### OQ-007 Permission operations for replay and discard
- **Sources:** `iam.ref_permission_catalogue` operation is `Enum{read, create, update, approve, delete}` (static-data-catalogue.json). INT-006 requires an authorised operator for replay.
- **Gap:** replay and discard are not in the operation list.
- **Resolution (proposal):** `dead_letter.read` for reads and `dead_letter.update` for replay and discard. Alternative: extend the enum with `execute`.

### OQ-008 `MessageDeadLettered` event
- **Sources:** domain-model.json gives DeadLetter the event `MessageDeadLettered` with provenance P. PROMPT §5 says P events are implemented only when consumed or needed by a process.
- **Resolution (proposal):** not raised; nothing consumes it. Add it when monitoring or analytics needs it (Phase 6).

### OQ-009 Outbox has no failure columns
- **Sources:** `<schema>.outbox_event` has `published_at` and `publish_attempts` but no status, last error or next attempt time; `integration.dead_letter_message.first_failed_at` is NOT NULL.
- **Resolution (proposal):** back-off is computed from `updated_at` and `publish_attempts`. A row that reaches the attempt limit stays unpublished, is skipped by the relay and is copied to `dead_letter_message`; `first_failed_at` uses the outbox row's `created_at` as the nearest available value. Error text stores exception types only, never messages, to keep sensitive values out (INT-007).

### OQ-010 Retry limit and back-off
- **Sources:** INT-006 ("controlled retry"), domain-model WebhookDelivery invariant ("up to the policy limit").
- **Gap:** no limit or schedule is specified.
- **Proposal:** configuration `Outbox:MaxAttempts` (10) and `Outbox:BaseBackoff` (5 seconds, doubling).

### OQ-011 Idempotency key retention
- **Sources:** INT-003, DBA-008; `idempotency_key.expires_at` exists.
- **Gap:** retention is not specified. Also: if the process stops after the operation commits but before the key is marked COMPLETED, retries get 409 IDEMPOTENCY_IN_PROGRESS until the key expires. This fails safe (the operation never runs twice).
- **Proposal:** `Idempotency:Retention` of 24 hours. For money-moving commands (Phase 3), also complete the key in the business transaction.

### OQ-012 Page size limits
- **Gap:** cursor pagination limits are not specified. **Proposal:** default 50, maximum 200.

### OQ-013 API rate limits
- **Sources:** NFR targets in the SRS are not per-tenant request rates.
- **Proposal:** fixed window per tenant (or per client IP without a tenant), `RateLimiting:PermitsPerMinute` 1200.

### OQ-014 Identity provider claims
- **Sources:** SRS §30 (identity provider open); TEN-002; IAM-007.
- **Assumption:** the access token carries `tenant_id`, `sub` (an `iam.user_account` ID for people) and one `permission` claim per permission code. Phase 1 may resolve permissions from role assignments instead of token claims.

### OQ-015 Auditing authorisation denials
- **Sources:** IAM-006 (outcome of sensitive operations), SRS §27.4.
- **Gap:** 403 responses from policies are not yet written to `audit.audit_event`; only command outcomes are. **Proposal:** add in Phase 1 with the Audit module.

### OQ-016 Time zone for business dates
- **Sources:** DBA-013 (business dates are plain dates); FIN-005.
- **Gap:** whose calendar date "today" is. **Proposal:** the tenant's configured time zone, from static data, in Phase 1. Until then `IClock.UtcToday`.

### OQ-017 Event type names
- **Sources:** domain-model.json; `ReconciliationExceptionRaised` appears in two contexts.
- **Resolution (proposal):** `event_type` is `{contextId}.{EventName}`, for example `integration.MessageDeadLettered`, using the exact event name from the model.

### OQ-018 Missing If-Match
- **Sources:** PROMPT §4.6 (mismatch 412, lost race 409); RFC 6585.
- **Resolution (proposal):** a missing If-Match returns 428 `PRECONDITION_REQUIRED`; an unreadable or weak tag returns 412.

### OQ-019 Replay delivers before commit
- **Sources:** INT-005, INT-006.
- **Resolution (proposal):** ReplayMessage delivers the event, then commits the Replayed state. If the commit fails, the next replay is harmless because consumers skip events they have already processed.

### OQ-020 Field protection key custody
- **Sources:** DBA-014; SRS §24.1; SRS §30.
- **Gap:** where encryption and hashing keys live and how they rotate. **Current state:** AES-256-GCM with key IDs in the ciphertext (so keys can rotate) and an HMAC-SHA256 lookup key, read from configuration. Production needs a KMS or HSM.

### OQ-021 Local test server version
- The machine that built Phase 0 has no Docker, so the integration suite ran against a local PostgreSQL 17.6 server through `INSYS_TEST_PG`. CI runs the same suite on `postgres:18` through Testcontainers. The migrations were written and tested by the specification authors on PostgreSQL 16.15.
