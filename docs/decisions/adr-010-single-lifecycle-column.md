# ADR-010: record_status is the single lifecycle of static data

- **Status:** Accepted (Phase 2 review, item 3d; resolves OQ-053)
- **Date:** 22 October 2026
- **Requirements:** SDM-002, PRD-002

## Context
Four static tables have a `status` column beside the standard `record_status`:
- `product.cfg_product_definitions_and_versions`;
- `integration.cfg_api_catalogue_entries`;
- `integration.cfg_event_schema_versions`;
- `finance.cfg_accounting_calendar_and_periods`.

Two columns for one lifecycle drift apart. Once a version is approved, `sdm.guard_static_version` allows only `record_status` and `effective_to` to change. So the duplicate froze at the state a product version was published in (OQ-053).

## Decision
- **One lifecycle:** `record_status` is the lifecycle of these tables.
- **The `status` columns are deprecated:** V0012 makes them nullable and comments them as deprecated. The static data generator leaves them out, so they are neither read nor written.
- **Accounting periods:** the calendar's `status` column is deprecated the same way. The calendar defines a period's code and dates; the AccountingPeriod aggregate (`finance.accounting_period`, V0012) owns its state.
- **The columns are kept for now, not dropped.** Dropping them needs the generator to read migrations after V0002, which it does not yet do. A later migration can drop them once nothing depends on them.

## Consequences
- API clients no longer send or receive `status` for these tables.
- Existing values remain in the database, but nothing reads them.
