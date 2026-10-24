# Addendum: dashboard figures (GAP-046, front-end request 43)

**Proposed 24 October 2026, for sign-off. Nothing is built.**

- **Asked for:** server-computed headline figures, so the browser never adds up rows:
  - written premium and claims incurred for a period;
  - regulatory returns by status and due;
  - failing deliveries.
- **Basis:**
  - ANL-001 (operational and management reporting);
  - the projections Analytics already keeps (`analytics.fact_written_premium`, `analytics.fact_claims_incurred`);
  - ADR-022 (parked deliveries) and the dead-letter runbook;
  - F103 (`addenda/access-scopes.md`): figures from scoped records count only what the caller may see.

## The rules every figure follows
- **Defined by the server.** Each figure is returned with its code. Its definition is the one below, and it is listed in the OpenAPI description, so the UI shows a figure and never derives one.
- **Money stays per currency.** A money figure is a list of `{currency, amount}`. Amounts in different currencies are never added together, and no conversion is made (none is specified).
- **Scoped.** Figures built from product- and branch-scoped records count only the caller's scope (F103). External users get none of these figures.
- **Owned by the module that holds the data.** Modules do not read each other's tables, so there is one summary endpoint per module rather than one dashboard endpoint. The front end lays out the panels.
- **Dated.** Every response says `asOf`, the moment it was computed. A projection-based figure also says `refreshedAt`, its projection's last update.

## The proposed figures

### Analytics: `GET /api/v1/analytics/summary?from=yyyy-MM-dd&to=yyyy-MM-dd` [`analytics.read`]

| Code | Value | Source | Definition |
|---|---|---|---|
| `WRITTEN_PREMIUM` | Money per currency | `analytics.fact_written_premium.written_premium` | The sum over `date_key` from `from` to `to`, both inclusive. That is the premium of General and Life policies issued, net of endorsement changes and cancellation refunds, on the day each happened. |
| `POLICIES_WRITTEN` | Count | `analytics.fact_written_premium.policy_count` | The policies issued in the same days, less those cancelled. |
| `CLAIMS_INCURRED` | Money per currency | `analytics.fact_claims_incurred.incurred` | The sum over the months from `from`'s month to `to`'s month, both inclusive. That is the net change in claim reserves (Claims Framework). Payments draw a reserve down, so they do not change it. |
| `CLAIMS_REPORTED` | Count | `analytics.fact_claims_incurred.claim_count` | The claims given a number in those months. |

- **Scope:** both projections carry `product_code`, and written premium also `branch_code`, so F103 applies to each row. The claims projection has no branch, so a branch-scoped user would see claims incurred for their products in every branch. **Decision needed (1):** either leave `CLAIMS_INCURRED` and `CLAIMS_REPORTED` out for branch-scoped users, or add `branch_code` to the claims projection with a migration and a rebuild (`POST /analytics/projections/rebuild`).
- **Not covered:** Health contributions and Health medical claims, which Analytics does not project. **Decision needed (2):** Health either follows later, as new projections, or is shown as "not included".

### Compliance: `GET /api/v1/compliance/returns/summary?fromPeriod=yyyy-MM&toPeriod=yyyy-MM` [`regulatory_return.read`]

| Code | Value | Source | Definition |
|---|---|---|---|
| `RETURNS_BY_STATUS` | Count per status | `reporting.regulatory_return_run` | Returns whose period is in the range, counted by status: Prepared, Reviewed, Approved, Submitted, Accepted, Rejected. |
| `RETURNS_AWAITING_ACTION` | Count | Same | Prepared, Reviewed, Approved and Rejected: the returns a person still has to act on. |

- **"Due" cannot be defined yet.** `reporting.cfg_regulatory_return_definitions` has a frequency but no submission deadline, and the SRS gives none. **Decision needed (3):** add an OQ, and later a `submission_days` column (days after the period end), or leave "due" out. Until that is decided, no "overdue returns" figure is proposed.

### Integration: `GET /api/v1/integration/deliveries/summary` [`dead_letter.read`]

| Code | Value | Source | Definition |
|---|---|---|---|
| `DELIVERIES_PARKED` | Count per prerequisite code | `integration.parked_delivery` | Status PARKED: waiting for configuration (ADR-022). Grouped by code, for example `BILLING_FREQUENCY_NOT_IN_FORCE`, so the panel says what to configure. |
| `DELIVERIES_FAILING` | Count | Same | Status FAILING, RETRYING or WAITING: being retried, or held behind one that is. |
| `DEAD_LETTERS_OPEN` | Count | `integration.dead_letter_message` | No `resolution` yet (not replayed or discarded). |
| `WEBHOOK_DELIVERIES_FAILING` | Count | `integration.webhook_delivery` | The last attempt failed and it has not been delivered since. |
| `OLDEST_PARKED_AT` | Timestamp or null | `integration.parked_delivery.parked_at` | The oldest delivery still parked, for an "attention since" label. |

These figures are tenant-wide (they are operations figures, not business records) and need `dead_letter.read`, as the existing dead-letter and parked-delivery views do.

### Not asked for, offered only if wanted
- **Billing:** open arrears cases and the amount overdue, per currency (`billing.arrears_case`, status OPEN), scoped like accounts. A partly-scoped account is left out, as OQ-081 decided.
- **Payments:** payments with an Unknown outcome awaiting a decision (PAY-005).

## What sign-off decides
1. The figures and definitions above, and the one-endpoint-per-module shape.
2. Claims for branch-scoped users: left out, or `branch_code` added to the claims projection.
3. Health: later projections, or "not included" for now.
4. Return due dates: an OQ and a submission-days setting, or no "due" figure.
5. Whether to add the two optional figures.

## Consequences when built
- **New endpoints:** three read endpoints, each a SQL aggregate over its module's own tables, with F103 predicates where they apply.
- **Migrations:** only if decisions 2 or 4 add columns.
- **Tests:** each figure against seeded rows, with a scoped user seeing only their part, money per currency, and an external user refused.
