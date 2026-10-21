# Build progress

| Phase | Scope | Status |
|---|---|---|
| 0 Foundation | Solution, building blocks, migration runner, compose, tests, CI | **Done, awaiting review** (21 Oct 2026) |
| 1 Platform | Tenancy, Identity, Audit, Static Data and generator | Not started |
| 2 Shared services | Party, Product Factory, Rating, Rules, Workflow, Documents | Not started |
| 3 Money | Billing, Payments, Finance, Claims Framework | Not started |
| 4 First domain | `FIRST_DOMAIN` (ask first: OQ-001) | Not started |
| 5 Other domains | The remaining two domains | Not started |
| 6 Enterprise | Distribution, Reinsurance, Compliance, Analytics, Integration | Not started |
| 7 Hardening | Security suite, OpenAPI review, performance, runbooks | Not started |

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
INSYS_APP_ROLE_PASSWORD=insys_app_local dotnet run --project tools/Insys.DbMigrator -- \
  --connection "Host=localhost;Port=5432;Database=insys;Username=insys_owner;Password=insys_owner_local" --app-role insys_app
dotnet run --project src/Host/Insys.Api
dotnet test                                    # integration tests use Testcontainers postgres:18
INSYS_TEST_PG="Host=…;Username=postgres;Database=postgres" dotnet test   # or an existing server, without Docker
INSYS_UPDATE_OPENAPI=1 dotnet test             # accept an intended API contract change
```
