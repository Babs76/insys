# INSYS Core back end

INSYS Core is a multi-tenant insurance administration platform for Life, General and Health insurance. This repository is its back end: a .NET 10 modular monolith on PostgreSQL 18, built phase by phase from the SRS and its companion specifications (`PROMPT.md`, `CLAUDE.md`).

## Architecture in brief

- **One host, 22 modules.** Each bounded context is a module under `src/Modules/<Context>` with Domain, Application, Infrastructure, Api and Contracts projects. Modules talk only through their Contracts: integration events, and a few public query interfaces.
- **One schema and one database role per module** (ADR-001, ADR-002). No cross-schema joins or foreign keys. Every tenant table is under forced row-level security, and every transaction starts with `set_config('app.tenant_id', …)`.
- **Events through a transactional outbox** per schema, relayed in process. Consumers deduplicate through their own inbox, and a consumer that keeps failing is dead-lettered (INT-005, INT-006).
- **Maker-checker in Workflow** (ADR-009) for sensitive operations, with checker ≠ maker (SRS-002) and approval authority per role.
- **Static data** (254 tables) is generated code (`tools/Insys.StaticData.Generator`), governed by tiers, versions and approvals (ADR-003).

| Area | Modules |
|---|---|
| Platform | Tenancy, Identity, Audit, Static Data, Integration (dead letters, webhooks, partner adapters) |
| Shared services | Party, Product Factory, Rating, Rules, Workflow, Documents |
| Money | Billing, Payments, Finance, Claims Framework |
| Domains | General, Life, Health |
| Enterprise | Distribution and Commission, Reinsurance, Compliance and Reporting, Analytics |

## Prerequisites

- .NET SDK 10.0.203 or later (`global.json`)
- Docker, for the local database and for the integration tests (Testcontainers `postgres:18`)
- `jq`, to query the specifications

## Run it locally

```bash
docker compose up -d postgres
INSYS_ROLE_PASSWORD=insys_local dotnet run --project tools/Insys.DbMigrator -- \
  --connection "Host=localhost;Port=5432;Database=insys;Username=insys_owner;Password=insys_owner_local" --roles db/roles \
  --bootstrap-operator 0199f000-0000-7000-8000-000000000001 --admin-subject <your IdP subject>
dotnet run --project src/Host/Insys.Api
```

- The API serves `/api/v1/…`. The OpenAPI documents are at `/openapi/{core|static-data|platform-operator}.json`, with committed copies in `docs/api/`.
- Health endpoints: `/health/live` (process), `/health/ready` (database, outbox lag, partitions), `/health` (everything, with detail).
- `appsettings.Development.json` holds local-only settings: the role connection template, a development token signing key and field protection keys. Never use them anywhere else.

## Configuration

| Setting | Purpose | Default |
|---|---|---|
| `ConnectionStrings:Template` | Connection string with `{role}`, used by every module with its own role | none |
| `Authentication:Authority`, `Audience` | The OIDC issuer and audience; tokens carry only `sub` and `tenant_id` | none, `insys-core-api` |
| `Platform:OperatorTenantId` | The platform operator tenant, the only one allowed on platform endpoints | none |
| `FieldProtection:Keys`, `ActiveKeyId`, `HashKey` | AES-256-GCM keys for encrypted fields, and the HMAC key for lookups (OQ-020) | none |
| `Outbox:*` | Relay poll interval, batch size, attempts and back-off; lag warning and failure thresholds | 2 s, 100, 10, 5 s; 1 min, 5 min |
| `Idempotency:Retention` | How long Idempotency-Key results are kept | 24 h |
| `RateLimiting:PermitsPerMinute` | Requests per minute per tenant | 1,200 |
| `Commands:ConflictRetry:MaxAttempts` | Server-side retries of commands marked safe to repeat (ADR-012) | 3 |
| `Identity:PermissionCache`, `Identity:PrivilegedAccess` | Permission cache duration and single-instance guard (ADR-006); privileged access window | 30 s; 8 h |
| `Audit:Partitions` | Monthly partitions kept ahead, and the warning threshold (ADR-005) | 3 months, 2 |
| `Workflow:Approvals:DefaultExpiry` | How long an approval waits before it expires | 14 days |
| `Integration:Webhooks:*` | Transport (`http` or `stub`), job switch, interval and timeout | http, on, 30 s, 10 s |
| `Integration:Secrets:<ref>` | Webhook signing keys, standing in for a secret store | none |
| `<Module>:Jobs:Enabled`, `Interval` | The daily jobs of Billing, Payments, General, Life, Health and Product Factory | on, 1 h |
| `Documents:Storage:Root` | Folder of the development document store (OQ-056) | temp folder |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Exports traces, metrics and logs over OTLP when set | off |

## Build and test

```bash
dotnet build Insys.slnx -warnaserror
dotnet test --solution Insys.slnx                         # all suites; integration and end-to-end tests start postgres:18
dotnet test --project tests/Insys.EndToEndTests           # one project
INSYS_UPDATE_OPENAPI=1 dotnet test --project tests/Insys.IntegrationTests -- --filter-class "*OpenApiContract*"   # accept API changes
INSYS_UPDATE_SNAPSHOTS=1 dotnet test --project tests/Insys.IntegrationTests -- --filter-class "*ErrorCode*"       # regenerate the error catalogue
INSYS_LATENCY_LOG=latency.txt dotnet test …               # write the performance smoke tests' percentiles to a file
dotnet run --project tools/Insys.StaticData.Generator    # regenerate static data code after a schema or catalogue change
```

| Suite | What it covers |
|---|---|
| Module unit tests (`tests/Insys.<Module>.Tests`) | Invariants and every allowed and refused transition, checked against `domain-model.json` |
| `Insys.ArchitectureTests` | The dependency rules between modules and layers |
| `Insys.IntegrationTests` | Migrations, row-level security, module roles, outbox and inbox, idempotency, OpenAPI contract and review, and the §27.4 security suite. The suite sweeps every endpoint without a token, without a tenant, without its permission and with another tenant's token, and covers cross-tenant records, caches and event consumers. |
| `Insys.EndToEndTests` | The six processes of `domain-model.json` with their compensations, and the Phase 6 enterprise flows |
| Performance smoke tests | Rating, payment allocation and adjudication against the benefit accumulators, measured against NFR-002 and NFR-003. These are not load tests (NFR-015). |

CI (`.github/workflows/ci.yml`) builds with warnings as errors, runs every suite, and fails if an OpenAPI document is not committed and current.

## Documentation

| Path | Contents |
|---|---|
| `docs/progress.md` | What each phase built |
| `docs/decisions/` | Architecture decisions (ADR-001 to ADR-015) and the open questions, with the proposal each follows |
| `docs/runbooks/` | Operating procedures: deployment, monitoring, dead letters, tenants and access, money operations, static data, incidents, backup |
| `docs/api/` | The committed OpenAPI documents |
| `docs/specs/`, `docs/srs/` | The specifications this was built from (sources of truth, in `CLAUDE.md` order) |
| `PROMPT.md`, `CLAUDE.md` | The build brief and the repository rules |

## Status

Phases 0 to 7 are built. Many rules the specifications leave undefined follow proposals pending business, actuarial or underwriting sign-off: ADR-012, ADR-014 and ADR-015, and the open questions. Partner integrations (banks, payment providers, reinsurers, regulators, the GL) are interfaces with stubs.
