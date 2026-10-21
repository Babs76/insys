# Build progress

| Phase | Scope | Status |
|---|---|---|
| 0 Foundation | Solution, building blocks, migration runner, compose, tests, CI | Done, accepted |
| 0 review items 1–8 | CI on postgres:18, partitions, inboxes, module roles, permissions, global static data, maker-checker, error codes | **Done, awaiting review** |
| 1 Platform | Tenancy, Identity, Audit, Static Data and generator | **Done, awaiting review** (21 Oct 2026) |
| 2 Shared services | Party, Product Factory, Rating, Rules, Workflow, Documents | Not started |
| 3 Money | Billing, Payments, Finance, Claims Framework | Not started |
| 4 First domain | `FIRST_DOMAIN` (ask first: OQ-001) | Not started |
| 5 Other domains | The remaining two domains | Not started |
| 6 Enterprise | Distribution, Reinsurance, Compliance, Analytics, Integration | Not started |
| 7 Hardening | Security suite, OpenAPI review, performance, runbooks | Not started |

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
INSYS_UPDATE_OPENAPI=1 dotnet test             # accept an intended API contract change
INSYS_UPDATE_SNAPSHOTS=1 dotnet test           # regenerate the error code seed after adding [ErrorCode] constants
```
