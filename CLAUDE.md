# CLAUDE.md: INSYS Core back end

Persistent instructions for Claude Code in this repository. The full build brief is in `PROMPT.md`.

## What this is
INSYS Core is a multi-tenant insurance administration platform for Life, General and Health insurance. This repository is the C# back end: a .NET 10 modular monolith on PostgreSQL 18.

## Sources of truth, in order of precedence
1. `docs/srs/Unified_Insurance_Platform_-_INSYS.pdf` and `docs/specs/requirements.json`
2. `db/migrations/*.sql` (the schema is fixed; change it only with a new numbered migration)
3. `docs/specs/domain-model.json`
4. `docs/specs/database-tables.json`, `docs/specs/static-data-catalogue.json`, `docs/specs/html/`

Query the large files with `jq` or `grep`; do not read them whole. For example:
```bash
jq '.contexts[] | select(.id=="life") | .aggregates[].name' docs/specs/domain-model.json
jq '.srs["LIF-008"]' docs/specs/requirements.json
grep -n "CREATE TABLE life.policy " db/migrations/V0001__core_schema.sql
```
Record conflicts and gaps in `docs/decisions/open-questions.md`. Never invent requirements; mark code `// TODO(spec):`.

## Commands
```bash
docker compose up -d postgres         # local PostgreSQL 18
dotnet build -warnaserror
dotnet test                           # integration tests need Docker (Testcontainers)
dotnet run --project src/Host/Insys.Api
dotnet run --project tools/Insys.StaticData.Generator   # regenerates static data code
```

## Architecture rules
- One module per bounded context under `src/Modules/<Context>/`, with Domain, Application, Infrastructure, Api and Contracts projects.
- Modules talk only through Contracts: integration events via the outbox, or public query interfaces. Never reference another module's internals.
- One DbContext per module, mapped only to that module's schema. No cross-schema joins or foreign keys.
- A command changes one aggregate in one transaction. Rules that span aggregates are made consistent through events.

## Non-negotiables
- Tenant comes only from the authenticated principal. Every transaction starts with `set_config('app.tenant_id', @t, true)`. EF query filters are a second layer.
- Events go through `<schema>.outbox_event` in the same transaction. Consumers deduplicate through the inbox.
- Commands that create records or move money require `Idempotency-Key`.
- `row_version` is mapped as a concurrency token, with ETag and If-Match.
- Maker-checker for sensitive operations, with checker different from maker (SRS-002).
- Audit sensitive operations (IAM-006). Medical and identity data never appears in logs, events or exceptions.
- Money is decimal plus currency. Never use float or double for money.
- Posted financial records are never updated; corrections are reversals (FIN-002, FIN-003).

## Packages
- Check the current version and licence on nuget.org before adding any package. OSI-approved licences only.
- Do not use: MediatR, AutoMapper, MassTransit v9 or later, FluentAssertions v8 or later, EF Core migrations.

## Testing
- Name tests after the rule and include the requirement ID where one exists.
- Integration tests use Testcontainers `postgres:18` with the real migrations, connecting as a non-owner role so row-level security applies.
- Never report tests as passing without running them. Paste the real counts.

## Working style
- Work phase by phase as `PROMPT.md` describes. Stop after each phase with the report format given there.
- Keep `docs/progress.md` current.
- Small, reviewable commits with clear messages.
