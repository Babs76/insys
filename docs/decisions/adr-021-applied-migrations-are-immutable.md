# ADR-021: Applied migrations are never edited once a production database exists

- **Status:** Accepted (Phase 8 brief, Workstream C, 23 October 2026)
- **Date:** 23 October 2026
- **Requirements:** DBA-017 (forward-only migrations)

## Context
The migration runner records each applied migration with its SHA-256 checksum (`public.schema_migration`). It refuses to continue if an applied file has changed.

Workstream C adds the OpenFactor source file header to every SQL file, `db/migrations/V0001` to `V0021` included. That changes every checksum. No production database exists yet, so this one change is allowed: local and CI databases are recreated from the edited files.

## Decision
- **The header change is the last edit to any applied migration.**
- **From the first production database onwards, an applied migration is never edited,** for any reason: comments, formatting and headers included. A correction is a new numbered migration.
- The runner's checksum check stays. It is what enforces this, and it must never be weakened or bypassed in production.
- Repeatable scripts (`R__*.sql`, such as the generated error code catalogue) are re-run when their content changes. That is by design, and it is not affected.

## Consequences
- **Every database migrated before this change refuses the new files** with a checksum error and must be recreated. That means:
  - the local development database (`docker compose`);
  - any other development database built from this repository, such as the front end's `insys-web-live` stack.

  CI databases are created fresh on every run.
- New migration files carry the header from the start (`tools/source-headers.py`, `SourceHeaderTests`).
