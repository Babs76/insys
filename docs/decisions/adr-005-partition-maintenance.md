# ADR-005: Partition maintenance by the Audit module

- **Status:** Accepted (item 2 of the Phase 0 review; resolves OQ-005)
- **Date:** 21 October 2026
- **Requirements:** DBA-018, DBA-023

## Decision
- **The functions (V0005):** `audit.ensure_monthly_partitions(months)` and `audit.partition_horizon()`.
  - They are `SECURITY DEFINER`, owned by the migration owner, and run with a fixed `search_path` and UTC.
  - They accept only the eight partitioned tables.
  - Every partition they create gets row-level security, enabled and forced, with the parent's tenant policy.
  - Only `insys_audit` may execute them.
- **The job:** the Audit module runs one at start-up and every 12 hours, creating partitions 3 months ahead.
- **The health check:** reports Degraded (a warning) when any table is covered for less than 2 months.
- **Grants:** default privileges give new partitions the module grants.
