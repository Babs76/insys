# ADR-001: One database role and connection per module

- **Status:** Accepted (directed in the Phase 0 review, item 4; resolves OQ-004)
- **Date:** 21 October 2026
- **Requirements:** DBA-002, DBA-004, DBA-005, TEN-004

## Decision
Each module connects with its own login role, `insys_<primary schema>`, that has DML on its own schemas only (`db/roles/module_roles.csv`, `db/roles/module_grants.sql`). Each module DbContext has its own data source from `ConnectionStrings:<Module>`, or, in development and tests, `ConnectionStrings:Template` with `{role}` replaced.

Further roles:
- **`insys_api`**, the API building block: `integration.idempotency_key` only. It needs SELECT, UPDATE and DELETE as well as INSERT, to replay, complete and release keys (OQ-034).
- **`insys_platform_admin_login`**, a member of `insys_platform_admin`: global static data rows and module outboxes only (ADR-003).

No role is a superuser, owns tables or has BYPASSRLS.

## Consequences
- **Writes across modules happen only through events.** Two of them were cross-module before:
  - **Inbox:** each module now has its own `<schema>.inbox_message` (V0004; OQ-002).
  - **Audit:** audit entries go through each module's outbox (ADR-004).
- **The outbox relay** drains each schema on that module's own data source.
- **Partitions** are created through owner-defined `SECURITY DEFINER` functions, because no module role has DDL rights (ADR-005).
- **Tests check it:** the integration tests show that a module role cannot touch another module's schema.
