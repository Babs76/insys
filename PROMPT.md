# INSYS Core back-end build: Claude Code master prompt

Paste the section **"Prompt"** into Claude Code from the root of this repository. Claude Code builds in phases and stops after each one for review. To continue, say: `Continue with Phase <n>`.

---

## Settings to confirm before starting

Edit these values before the first run. Claude Code must read them from this file.

```yaml
ROOT_NAMESPACE: Insys                 # C# root namespace and project prefix
TARGET_FRAMEWORK: net10.0             # .NET 10 LTS, supported to 14 Nov 2028
DATABASE: PostgreSQL 18               # decided; schema in db/migrations
FIRST_DOMAIN: ASK                     # Life | General | Health. SRS §30 open decision. ASK = stop and ask before Phase 4
IDENTITY_PROVIDER: OIDC (TBD)         # JWT bearer; issuer and audience come from configuration
MESSAGE_BROKER: none                  # in-process bus behind an interface until SRS §30 decides
API_STYLE: Minimal APIs, URL versioning /api/v1
```

---

## Prompt

You are the lead engineer building the back-end API services for **INSYS Core**, a multi-tenant insurance administration platform for Life, General and Health insurance. Build it in C# on .NET 10 across every layer, module, sub-module and component defined in the specifications in this repository.

### 1. Sources of truth

Read these before writing code. Use `jq` and `grep` to query the large files; do not load them whole.

| File | What it gives you |
|---|---|
| `docs/srs/Unified_Insurance_Platform_-_INSYS.pdf` | The SRS v1.0. Business requirements and architecture decisions. |
| `docs/specs/requirements.json` | All 117 SRS requirement IDs with text, plus proposed SDM and DBA requirements. |
| `docs/specs/domain-model.json` | 23 bounded contexts, 60 aggregates with entities, value objects, invariants, commands, events and state transitions; context relationships; 6 cross-context processes; glossary. |
| `db/migrations/V0001__core_schema.sql` | Core transactional schema: 154 tables in 22 schemas, tested on PostgreSQL. |
| `db/migrations/V0002__static_data_schema.sql` | 252 static data tables. Its header explains the translation rules. |
| `docs/specs/database-tables.json` | The core tables as structured data (columns, keys, references, samples). |
| `docs/specs/static-data-catalogue.json` | 253 static data entities with fields, scope, tier and owner. |
| `docs/specs/html/*.html` | Human-readable versions of all the above. |

**Precedence when sources disagree:** SRS requirement text, then the SQL migrations, then `domain-model.json`, then the HTML specs. Never resolve a conflict silently. Record it in `docs/decisions/open-questions.md` with both sources and your proposed resolution, follow the higher-precedence source, and mention it in your phase report.

**Do not invent requirements.** If something the code needs is not specified (a validation limit, a rounding rule, a status), implement it as configuration or static data, mark it `// TODO(spec):` with a short explanation, and list it in `docs/decisions/open-questions.md`.

### 2. Architecture

Build a **modular monolith**: one deployable API host, with each bounded context as an isolated module. The SRS warns against premature microservices (§29) and asks for modular boundaries first. The module boundaries must be strict enough that any module could later be extracted into its own service.

```
src/
  Host/Insys.Api/                         composition root, middleware, OpenAPI, health, telemetry
  BuildingBlocks/
    Insys.BuildingBlocks.Domain/          Entity, AggregateRoot, ValueObject, DomainEvent, Result, Money, Guard
    Insys.BuildingBlocks.Application/     ICommandHandler, IQueryHandler, dispatcher, validation, ITenantContext, IClock
    Insys.BuildingBlocks.Infrastructure/  EF Core base context, tenant interceptor, outbox, inbox, idempotency, audit writer
    Insys.BuildingBlocks.Api/             endpoint conventions, ProblemDetails, ETag/If-Match, pagination, auth policies
    Insys.BuildingBlocks.Contracts/       IIntegrationEvent, event envelope (§23.2 metadata)
  Modules/<Context>/
    Insys.<Context>.Domain/               aggregates, entities, value objects, domain events, domain services
    Insys.<Context>.Application/          commands, queries, handlers, validators, policies (event reactions)
    Insys.<Context>.Infrastructure/       DbContext for the module's schema, EF mappings, repositories, adapters
    Insys.<Context>.Api/                  endpoint registration for the module
    Insys.<Context>.Contracts/            integration events and public query interfaces other modules may use
tools/Insys.StaticData.Generator/         code generator for static data (Phase 1)
tests/
  Insys.<Context>.Tests/                  unit tests per module
  Insys.IntegrationTests/                 Testcontainers PostgreSQL 18, real migrations
  Insys.ArchitectureTests/                dependency rules
  Insys.EndToEndTests/                    the six cross-context processes
db/migrations/                            forward-only SQL migrations
```

Module names come from `domain-model.json` contexts, in PascalCase: Life, General, Health, ProductFactory, Rating, Rules, Party, ClaimsFramework, Workflow, Documents, StaticData, Billing, Payments, Finance, Distribution, Reinsurance, Compliance, Analytics, Integration, Tenancy, Identity, Audit. Portals and Channels has no module.

**Dependency rules, enforced by architecture tests:**
- Domain references only BuildingBlocks.Domain.
- Application references its own Domain, BuildingBlocks.Application and other modules' **Contracts only**.
- Infrastructure and Api reference their own module's projects and the matching BuildingBlocks.
- No module references another module's Domain, Application or Infrastructure.
- No DbContext maps tables outside its own module schema. No cross-schema SQL joins (DBA-004, DBA-005, SRS §21.3).
- Modules interact only through integration events (published via the outbox) or public query interfaces in Contracts.

### 3. Technology

Before adding any package, check its current stable version and its licence on nuget.org. Use only OSI-approved open-source licences. If a package needs a commercial licence, stop and ask.

| Concern | Use |
|---|---|
| Runtime | .NET 10 LTS, C# latest, nullable enabled, warnings as errors, `TreatWarningsAsErrors` |
| API | ASP.NET Core Minimal APIs, `Asp.Versioning.Http`, built-in OpenAPI, RFC 9457 ProblemDetails, built-in rate limiting |
| Data | EF Core 10 with `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, **database-first**: map to the existing tables. Do **not** use EF migrations. |
| Migrations | Apply `db/migrations/*.sql` in order with a small runner (DbUp or equivalent, licence checked). New changes are new numbered SQL files, never edits to applied ones (DBA-017). |
| Messaging | Own outbox relay and in-process bus behind `IEventBus`; broker adapter later (MESSAGE_BROKER) |
| Validation | FluentValidation (check licence) or the framework's built-in validation |
| Auth | JWT bearer (OIDC). Permission-based policies from the IAM permission catalogue |
| Observability | OpenTelemetry traces, metrics and logs; health checks |
| IDs and time | `Guid.CreateVersion7()` for new IDs; `TimeProvider` for time |
| Tests | xUnit, Testcontainers for .NET (`postgres:18`), NetArchTest.Rules, Shouldly |

**Do not use:** MediatR, AutoMapper, MassTransit v9 or later, FluentAssertions v8 or later (all moved to commercial licences), EF Core migrations, or any package with an unclear licence. Write the small dispatcher and mapping code yourself.

### 4. Cross-cutting building blocks (non-negotiable)

1. **Tenant isolation (TEN-002, TEN-004, DBA-001 to DBA-003).** Resolve the tenant only from the authenticated principal's `tenant_id` claim, or a service identity, never from the request body, route or headers. Every database transaction must first run `SELECT set_config('app.tenant_id', @tenantId, true)`. Implement this with an EF Core `DbTransactionInterceptor`, and make every handler run inside an explicit transaction, including reads. Add EF global query filters on `tenant_id` as a second layer. Caches, outbox and inbox records, events and logs all carry the tenant ID. A request with no tenant gets a 401 or 403, never data.
2. **Unit of work and outbox (§23.2, DBA-006).** A command handler changes one aggregate and writes that aggregate's events to its module's `<schema>.outbox_event` in the same transaction. A background relay publishes with retries and sets `published_at`.
3. **Inbox (INT-005, DBA-007).** Every event handler records `(consumer, event_id)` first and skips duplicates.
4. **Idempotency (INT-003, PAY-001, CLM-005).** Commands that create records or move money require an `Idempotency-Key` header, stored in `integration.idempotency_key` with a request hash. A replay returns the original response. Reusing a key with a different request returns 422.
5. **Event envelope (§23.2).** Include event ID, tenant ID, type, version, occurred-at, aggregate reference, correlation ID and causation ID. Payloads carry references, not sensitive values (INT-007).
6. **Optimistic concurrency (DBA-011).** Map `row_version` as a concurrency token. GET returns an `ETag`; updates require `If-Match`. A mismatch returns 412; a lost race returns 409.
7. **Maker-checker (SRS-002).** Sensitive operations create a Workflow `Approval`. The checker must differ from the maker and hold authority for the amount and type. The operation completes on `ApprovalGranted`.
8. **Audit (IAM-006).** Sensitive operations write to `audit.audit_event`: actor, tenant, operation, target, time and outcome, with masked details.
9. **Versions (PRD-008, DBA-010).** Transactions store the product, rating, rule set, posting rule, template, commission plan and cession rule versions they used, as the tables require.
10. **Money (DBA-012).** Use a `Money` value object (decimal amount plus ISO 4217 currency) with rounding from static data. Never use double or float for money or rates.
11. **Static data access.** A resolver returns the version of any static record effective at a given business date, with tenant overrides applied over global baselines (scope G, GT and T).
12. **Sensitive data (§24.1, HLT-008, DBA-014, DBA-015).** Encrypt identifier and bank numbers in the application (the `_enc`, `_hash` and `_last4` columns). Keep medical fields out of logs, events, exceptions and traces. Medical endpoints need a clinical-purpose permission.
13. **Errors.** Use ProblemDetails with stable error codes from the error code catalogue: validation 422, state conflict 409, not found 404, forbidden 403, precondition failed 412.

### 5. Domain implementation rules

- Implement every aggregate in `domain-model.json` for the module, with the listed entities and value objects. Aggregates have private setters and change only through methods named after the listed **commands**.
- Enforce every **invariant** in the aggregate, or in a domain service where it needs other data. Put the requirement ID in a code comment and in the test name, for example `LifePolicy_BeneficiaryShares_MustTotal100Percent`.
- Implement each aggregate's **state machine** exactly as its `stateTransitions` list. Reject any other transition with a 409 and the error code `INVALID_STATE_TRANSITION`.
- Raise **events** with exactly the names in the model. Events with provenance `S` are SRS events and must exist. Implement `P` events only when a context consumes them (`consumes`) or a process needs them.
- Implement every **policy** in `consumes` as an idempotent event handler in the consuming module.
- Respect the aggregate boundaries in `domain-model.json`, including the documented merges: LifeBenefit, LifeBeneficiary, LifePremiumSchedule and LifeCashValue live inside LifePolicy, and GeneralCoverage lives inside GeneralPolicy.
- Use the glossary terms in names, endpoints and messages.

### 6. API conventions

- Base path `/api/v1/{module}/{resource}`, for example `/api/v1/life/policies`, `/api/v1/general/claims`, `/api/v1/health/preauthorisations`.
- Queries are GET with cursor pagination (`?cursor=&limit=`) and filters.
- Commands that are not plain create or update are `POST /{resource}/{id}/{action}` in kebab case, named after the command: `POST /api/v1/life/policies/{id}/lapse`, `POST /api/v1/general/quotes/{id}/accept`.
- Creates return 201 with `Location`. Long-running work returns 202 with a status resource.
- Every endpoint declares its permission policy. Authorisation lives in the back end, never only in a UI (IAM-007).
- OpenAPI: one document with a tag per module, examples taken from the spec sample values, and a security scheme. Generate it at build time and commit it to `docs/api/openapi-v1.json`.
- Health and Preauthorisation endpoints never return medical fields to a caller without the clinical-purpose permission.

### 7. Static data (252 tables)

Do not hand-write 252 modules. In Phase 1, build `tools/Insys.StaticData.Generator`, which reads `static-data-catalogue.json` and the `CREATE TABLE` statements in `V0002__static_data_schema.sql`. The SQL is authoritative for column names and types. The generator produces, for each table:

- an EF entity and configuration in the owning module's Infrastructure project;
- a read model and endpoints under `/api/v1/{module}/static/{entity-kebab}`:
  - `GET` (list, with `?asAt=`) and `GET /{code}`;
  - `POST` (create a draft) and `PUT /{code}/versions/{version}` (edit a draft);
  - `POST /{code}/versions/{version}/submit`, `.../approve`, `.../activate`, `.../deprecate` and `.../retire`.

Changes go through `sdm.change_request` with maker-checker for Tier 1 (SDM-003). The database already enforces the lifecycle, non-overlapping periods and immutability; map those database errors to 409 ProblemDetails. Generated code is deterministic and committed. Also build `POST /api/v1/static-data/imports` for validated all-or-nothing bulk loads (SDM-012).

### 8. Testing (do not report a test as passing unless you ran it)

- **Unit:** every invariant and every allowed and disallowed state transition, per aggregate.
- **Integration:** Testcontainers `postgres:18`, applying both migrations, against the real schema. Cover repositories, outbox, inbox and idempotency.
- **Tenant isolation, SRS §27.4:** cross-tenant reads and writes through the API, cache, search and event consumers; missing tenant context; privilege escalation; document access. Run against the database as a non-owner role.
- **Architecture:** the dependency rules in section 2.
- **End to end:** the six processes in `domain-model.json` `processes`, including each listed compensation.
- **Contract:** the OpenAPI snapshot does not change without an explicit update.

Run `dotnet build` and `dotnet test` at the end of every phase and include the real results in the phase report.

### 9. Phases

Work one phase at a time. At the end of each phase: build, run all tests, update `docs/progress.md`, then **stop** and report. Wait for "Continue with Phase n" before going on.

| Phase | Scope | Exit criteria |
|---|---|---|
| **0 Foundation** | Solution and projects; building blocks from section 4; migration runner; `docker-compose.yml` with PostgreSQL 18; test projects; architecture tests; CI workflow (`.github/workflows/ci.yml`: build, test, Testcontainers) | Builds clean; both migrations apply to a fresh database; a sample tenant-scoped endpoint passes the isolation tests; architecture tests pass |
| **1 Platform** | Tenancy, Identity, Audit, Static Data module and the generator | Tenant onboarding workflow (TEN-008); permission policies; audit writes; all 252 static tables reachable through generated endpoints with maker-checker |
| **2 Shared services** | Party, Product Factory, Rating, Rules, Workflow (with Approval), Documents | The "Product publication" process passes end to end |
| **3 Money** | Billing, Payments, Finance, Claims Framework | Invoice to payment to allocation to journal works; journals balance; reversals only (FIN-002, FIN-003); PAY-005 unknown-outcome handling |
| **4 First domain** | FIRST_DOMAIN (ask if `ASK`) | Its processes pass end to end, including compensations |
| **5 Other domains** | The remaining two domains | Their processes pass end to end |
| **6 Enterprise** | Distribution and Commission, Reinsurance, Compliance and Reporting, Analytics projections, Integration (webhooks, dead letters, partner adapter interfaces with stub implementations) | Commission accrual and clawback; cession and recovery; projections rebuild from events |
| **7 Hardening** | Full §27.4 security suite; OpenAPI review; performance smoke test of the hot paths (benefit accumulator, payment allocation, rating); README and runbooks | All suites green; open questions listed with proposals |

Within a phase, build module by module. For each module, implement Domain, then Application, Infrastructure, Api and tests, and finish one module before starting the next.

### 10. Definition of done, per module

- [ ] Every aggregate, command, event, invariant and transition in `domain-model.json` is implemented or listed as an open question with a reason
- [ ] Every table in the module's schema is mapped, with no tables from other schemas
- [ ] Every endpoint has a permission policy, validation, ProblemDetails errors and an OpenAPI description
- [ ] Every money-moving or record-creating command honours `Idempotency-Key`
- [ ] Tenant isolation tests cover the module's endpoints
- [ ] Unit, integration and architecture tests pass, and the results are in the report
- [ ] No medical or identity data in logs, events or exceptions

### 11. Out of scope

Front ends; real calls to banks, payment providers, clearing houses, reinsurers, the GL or actuarial engines (build interfaces and stubs only); production infrastructure; choices still open in SRS §30, which belong in configuration and open questions.

### 12. Phase report format

```
## Phase <n> report
Built: <modules and components>
Tests: <counts from the actual run: passed / failed / skipped per suite>
Spec coverage: <aggregates, commands, events, endpoints implemented vs in spec>
Deviations: <anything that differs from the specs, and why>
Open questions: <new entries in docs/decisions/open-questions.md>
Next: <what Phase n+1 will do>
```

**Start now with Phase 0.** First read `CLAUDE.md`, this file's settings, and skim the specs with `jq`. Then post a short plan for Phase 0 and carry it out.
