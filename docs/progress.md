# Build progress

| Phase | Scope | Status |
|---|---|---|
| 0 Foundation | Solution, building blocks, migration runner, compose, tests, CI | Done, accepted |
| 0 review items 1–8 | CI on postgres:18, partitions, inboxes, module roles, permissions, global static data, maker-checker, error codes | **Done, awaiting review** |
| 1 Platform | Tenancy, Identity, Audit, Static Data and generator | Done, accepted |
| 1 review items 1–6 | Repository move, V0007 to V0009, access aggregates, cache guard, OpenAPI split, Tier 2 setting, outbox lag | **Done, awaiting review** |
| 2 Shared services | Party, Product Factory, Rating, Rules, Workflow, Documents | In progress |
| 3 Money | Billing, Payments, Finance, Claims Framework | Not started |
| 4 First domain | `FIRST_DOMAIN` (ask first: OQ-001) | Not started |
| 5 Other domains | The remaining two domains | Not started |
| 6 Enterprise | Distribution, Reinsurance, Compliance, Analytics, Integration | Not started |
| 7 Hardening | Security suite, OpenAPI review, performance, runbooks | Not started |

## Phase 1 review items

1. **Repository move:** the repository is now in `~/Development`. A clean build and the full test suite pass there, no conflict copies remain, and CI fails on any `* 2.*` file.
2. **V0007:**
   - `sdm.change_request.status` is widened, and PENDING_APPROVAL is back.
   - Privileged access grants get a requester, status and decision, with approver ≠ requester.
   - `iam.access_review_item` is added.

   The PrivilegedAccessGrant and AccessReview aggregates are built, with tests.
3. **Permission cache:** ADR-006 records that it is per instance. A startup guard (an advisory lock) refuses a second instance until `Identity:PermissionCache:AllowMultipleInstances` is set.
4. **OpenAPI documents:** split into core, static-data and platform-operator. Each has its own snapshot in `docs/api`, and every endpoint is in exactly one.
5. **Tier 2 maker-checker:** a per-tenant setting (V0008), on by default and audited. Tier 1 is always on.
6. **Outbox relay lag:** reported on `/health` and `/health/ready` through `integration.outbox_lag` (V0009). Degraded after `Outbox:Lag:Warning` (1 minute); Unhealthy and not ready after `Outbox:Lag:Failure` (5 minutes).

## Phase 2: what exists

**Building blocks**
- **Expression language (ADR-008):** governed conditions for rules and workflow guards. A missing variable is an error.
- **`ApprovalRequested` (ADR-009):** how any module asks Workflow for an approval.
- **Static data:** the generator can map read-only endpoints for tables an aggregate manages.
- **Tests:** `DrainOutbox` now relays until nothing is left, so chains of events complete.

**Modules**
- **Workflow:**
  - **Approval:** maker-checker plus authority by role, type and amount (SRS-002, WFL-004), with expiry, using V0010.
  - **ProcessInstance:** versioned definitions (WFL-001) and transition guards. The owning module's validation is called and fails closed (WFL-002). Tasks can be assigned, reassigned and escalated, with SLA timers (WFL-003). State is stored, so processes survive restarts (WFL-005).
  - Case notes, and a background job for expiry and timers.
- **Static Data approvals moved to Workflow:** the same approve and reject paths are now decided as Workflow approvals (Phase 0 item 7).
- **Rules:** EvaluateRuleSet for eligibility and underwriting. Each decision is recorded in `rules.decision_log` with its rule set version and inputs (LIF-002).
- **Rating:**
  - The reference engine (ADR-007). Each calculation is recorded with its breakdown, product version and rating version (PRD-008).
  - Dry-run simulation through `IPremiumSimulator`.
  - A configuration cache, dropped on ProductVersionPublished.
- **Product Factory:**
  - ProductDefinition's RegisterProduct.
  - The ProductVersion lifecycle: Draft → Validated → Submitted → Approved → Published → Effective → Withdrawn or Retired.
  - Validation against the coverage library and benefit dependencies (PRD-004, PRD-009), and simulation of the rating test cases (PRD-005).
  - Approval through Workflow (PRD-006), immutability after publication (PRD-002), new versions (PRD-003), effective dates (PRD-007).
  - Revalidation when product static data changes.
- **Party:**
  - Individuals and organisations (PTY-001); addresses, contact points, encrypted identifiers that are unique per type and country, relationships, and many roles (PTY-002).
  - Death, merge with history (PTY-004), and anonymisation.
  - ConsentLedger (PTY-005). Events carry no personal data.
- **Documents:**
  - Generation from template versions, with the template and product versions recorded (DOC-002), and upload.
  - Tenant-scoped storage (DOC-001); storage, rendering and delivery are stubs (OQ-056).
  - Delivery, legal holds, and disposal after retention.
  - Audited access to sensitive documents (DOC-003), with role checks for highly sensitive ones (DOC-004). Uses V0011.

**New migrations:**
- V0010: approval lifecycle columns.
- V0011: document status.

**Decisions:** ADR-007, ADR-008 and ADR-009; OQ-047 to OQ-056.

**End to end:** the "Product publication" process passes with its three compensations:
- validation errors keep Draft;
- a failed simulation case blocks submission;
- a rejected approval returns the version to Draft.

**Tests (21 Oct 2026):** 509 in total: 504 passed, 5 skipped (the end-to-end processes of later phases), 0 failed.

## Phase 1: what exists

**Phase 0 review items**
1. **CI:** the workflow runs on postgres:18 through Testcontainers. GitHub Actions is blocked on the account (see `open-questions.md`, OQ-021), so the same steps run locally on a clean clone.
2. **Partitions:** V0003 partitions are checked for forced RLS. A partition queried directly as a module role returns only the tenant's rows. The Audit module's job keeps partitions 3 months ahead and warns below 2 (ADR-005).
3. **Inboxes:** V0004 gives every module schema an `inbox_message`. Idempotency keys stay in `integration`.
4. **Roles:** one role and connection per module (ADR-001), with tests that a module role cannot write another module's schema.
5. **Permissions:** tokens carry sub and tenant_id only. Permissions are resolved per request from IAM, cached for 30 seconds, and evicted on change. A revoked role is denied on the next request.
6. **Global static data:** written only on the platform administrator connection, only from `/global` endpoints, and only by the operator tenant. Tests cover the API and the database.
7. **Maker-checker:** Tier 1 maker-checker through `sdm.change_request`, with the checker different from the maker (ADR-003).
8. **Error codes:** the catalogue is seeded from the `[ErrorCode]` constants by a generated repeatable migration.

**Modules**
- **Tenancy:** the Tenant aggregate, with its onboarding case (11 steps in load order) and status history. Commands: register, complete onboarding step, activate, suspend, reactivate, terminate, change isolation profile. Operator-only endpoints. Publishes `TenantSuspended`.
- **Identity:** the UserAccount aggregate, with role assignments and sessions. Commands: provision, assign role, revoke role, lock, unlock, disable. Enforces MFA for privileged roles and segregation-of-duties rules from static data. Provides the per-request principal resolver and its cache. PrivilegedAccessGrant and AccessReview wait on OQ-027.
- **Audit:** records every module's audit events and denied requests, offers audit queries, and runs the partition job and health check.
- **Static Data:** StaticDataChange (`sdm.change_request`) and ImportBatch, consumers for the owning modules' events, approve and reject for all tables, and change request and import queries.
- **All 22 modules** now exist with their own DbContext, role and generated static data. 18 of them have no aggregates yet.

**Static data:** `tools/Insys.StaticData.Generator` generates all 252 tables: entity, input with validation from the CHECK constraints, EF mapping, and endpoints. Each table supports:
- list and get as at a date, and versions;
- create draft, edit draft (maker only), submit, activate, deprecate, retire;
- all-or-nothing import;
- `/global` variants for scopes G and GT.

**New migrations:**
- V0004: inboxes in every module schema, plus `tenant` and `analytics` outboxes.
- V0005: partition functions.
- V0006: tenant register concurrency columns.
- `R__error_code_catalogue.sql`: the generated error code seed.

## Phase 0: what exists

**Solution** `Insys.slnx`: .NET 10 (SDK 10.0.203), nullable on, warnings as errors, central package versions in `Directory.Packages.props` (each package checked on nuget.org for version and an OSI licence).

**Building blocks** (`src/BuildingBlocks`)
- Domain: `Entity`, `AggregateRoot` (domain events, `RowVersion`), `ValueObject`, `IDomainEvent`, `Result` and `Error` with `ErrorKind`, `ErrorCodes`, `Guard`, `StateMachine` (`INVALID_STATE_TRANSITION`), `Money`, `CurrencyCode`, `RoundingRule`, `ConfigurationVersionRef`, `MakerChecker`.
- Contracts: `IIntegrationEvent`, `[IntegrationEvent("{context}.{Name}")]`, `EventEnvelope` (§23.2 metadata).
- Application: commands and queries, our own `Dispatcher` (no MediatR), FluentValidation pipeline, `ExecutionScope` (tenant and actor, set once, only from the principal or a service identity), `IClock`, `IEventBus`, `IIntegrationEventHandler`, `IDomainEventMapper`, `IDeadLetterSink`, `IAuditTrail`, `IFieldProtector`, `IIdempotencyStore`, `IStaticDataResolver` (interface only; Phase 1).
- Infrastructure: `ModuleDbContext` (own schema, snake-case columns, tenant query filter, row_version, audit columns, outbox write in SaveChanges), `TenantTransactionInterceptor` (`set_config('app.tenant_id', …, true)` first in every transaction), transactional command and read-only query pipeline, audit writer (in-transaction on success, separate transaction on failure), outbox relay with per-tenant polling, back-off and dead letters, in-process event bus, inbox, PostgreSQL idempotency store, AES-256-GCM field protector, health check.
- Api: ProblemDetails with stable `code`, exception mapping (including PostgreSQL errors to 409), JWT bearer (OIDC authority or a development key), permission policies (`RequirePermission`), tenant requirement as the fallback policy, `Idempotency-Key` filter, ETag and If-Match helpers, cursor pagination, per-tenant rate limiting, URL versioning `/api/v1`, OpenAPI document with bearer security and `x-permission`.

**Host** `src/Host/Insys.Api`: composition root, OpenTelemetry (OTLP when configured), `/health/live`, `/health/ready`, `/openapi/v1.json`. The OpenAPI document is generated at build time and committed as `docs/api/openapi-v1.json`.

**Sample module** Integration › DeadLetter (tenant-scoped, the Phase 0 sample endpoint): commands `DeadLetterMessage`, `ReplayMessage`, `DiscardMessage`; states Dead-lettered → Replayed or Discarded; INT-006 operator check; audit on replay and discard. Endpoints:
- `GET /api/v1/integration/dead-letters` (cursor, limit, status, source)
- `GET /api/v1/integration/dead-letters/{id}` (ETag)
- `POST /api/v1/integration/dead-letters/{id}/replay` (If-Match)
- `POST /api/v1/integration/dead-letters/{id}/discard` (If-Match)

**Database**
- `tools/Insys.DbMigrator`: forward-only runner with SHA-256 checksums, advisory lock and an out-of-order guard (DBA-017); optional grants for a non-owner app role.
- `db/migrations/V0003__monthly_partitions_2026_2027.sql`: monthly partitions with RLS (OQ-003).
- `db/roles/app_role_grants.sql`: local, test and CI grants (OQ-004).
- `docker-compose.yml`: PostgreSQL 18.

**Tests**: building blocks unit tests, Integration module unit tests, architecture tests, integration tests (migrations, RLS as the non-owner role, outbox, relay, inbox, dead letters, idempotency, API tenant isolation, OpenAPI contract), and end-to-end placeholders for the six processes (skipped until their phases).

**CI** `.github/workflows/ci.yml`: restore, build with `-warnaserror`, test with Testcontainers `postgres:18`, and a check that the OpenAPI document is committed.

## How to run

```bash
docker compose up -d postgres
INSYS_ROLE_PASSWORD=insys_local dotnet run --project tools/Insys.DbMigrator -- \
  --connection "Host=localhost;Port=5432;Database=insys;Username=insys_owner;Password=insys_owner_local" --roles db/roles \
  --bootstrap-operator 0199f000-0000-7000-8000-000000000001 --admin-subject <your IdP subject>
dotnet run --project src/Host/Insys.Api
dotnet run --project tools/Insys.StaticData.Generator          # regenerate static data code after V0002 or catalogue changes
dotnet test                                    # integration tests use Testcontainers postgres:18
INSYS_TEST_PG="Host=…;Username=postgres;Database=postgres" dotnet test   # or an existing server, without Docker
INSYS_UPDATE_OPENAPI=1 dotnet test             # accept intended API contract changes (docs/api/openapi-*.json)
INSYS_UPDATE_SNAPSHOTS=1 dotnet test           # regenerate the error code seed after adding [ErrorCode] constants
```
