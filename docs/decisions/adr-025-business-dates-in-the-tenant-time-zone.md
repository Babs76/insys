# ADR-025: Business dates are in the tenant's time zone; timestamps stay in UTC

- **Status:** **Accepted** (24 October 2026). Decided by Ade on GAP-044 (front-end request 41).
- **Requirements:** TEN-001 (tenant configuration), LIF-005, GEN-002, BIL-002, FIN-005 and every rule judged "on a date"; NFR-012
- **Related:** ADR-022 (the same tenant time zone, `tenant.cfg_locale_defaults.time_zone`, and the same default)

## Context
- **Until now** every module took "today" from `IClock.UtcToday`, the date in UTC. Each tenant has a time zone in its locale defaults.
- **For a South African tenant** (UTC+2), from 00:00 to 02:00 local time the UTC date is still yesterday. In that window:
  - a policy whose cover starts today was not yet in force;
  - a loss reported for today was "in the future";
  - a debit order due today was not yet due;
  - the month that had just ended could not be closed.

## Decision
1. **Business dates are judged in the tenant's time zone.** That covers:
   - inception and cover start;
   - event and loss dates;
   - due dates and arrears;
   - lapse, reinstatement and termination;
   - period open and close;
   - static data in force;
   - the daily jobs' run dates.

   **Timestamps stay in UTC** (`IClock.UtcNow`, every `*_at` column).
2. **`IBusinessDate`** (building blocks) gives `Today` for the current tenant, `TodayFor(tenantId)`, `DateOf(instant)` and the `Zone`.
   - The zone is `tenant.cfg_locale_defaults.time_zone` in force.
   - The default is `BusinessDate:DefaultTimeZone` (Africa/Johannesburg, as in ADR-022) when the tenant has no zone or it is not a known zone.
   - Zones are cached per tenant for a minute.
   - Jobs that span tenants use `TenantBusinessDates.TodayFor(tenantId)`, so each tenant's daily work runs on that tenant's date.
3. **`IClock.UtcToday` stays for technical dates only.** These are:
   - audit partition maintenance;
   - retention cut-offs computed from timestamps;
   - idempotency and cache expiry;
   - Identity, Tenancy, Audit and Integration, whose dates are about the platform rather than a tenant's business.
4. **A test-only business date for the daily jobs** (front-end request 40): `POST /api/v1/testing/daily-jobs` with `asOf`.
   - It runs the daily jobs for the caller's tenant, with every business date in that run being `asOf`.
   - It works only when `Testing:DailyJobRunner:Enabled` is set, in Development or Testing.
   - The host refuses to start if it is set in any other environment, like the development signing key.

## Consequences
- **One business day per tenant.** Two tenants in different zones can be on different business days at the same instant. The daily jobs handle each tenant on its own date.
- **The test clock:** tests can fix both the instant (`TimeProvider`) and, for the daily jobs, the business date.
- **Tests at the 00:00 to 02:00 Johannesburg boundary**, at 23:30 UTC, which is 01:30 the next day in Johannesburg, in `BusinessDateTests`:
  - a policy comes into force on its inception date;
  - a claim event on the first day of cover is accepted;
  - a debit order falls due;
  - the month that has just ended can be closed.
- **The front end** can default dates to the tenant's date. `/identity/me` already returns the tenant's locale; the zone is in its locale defaults.
