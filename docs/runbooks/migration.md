# Migrating a customer

In this order, because each step names what the one before returned. Every file loads whole or not at all, against its control totals, at most 2,000 rows, and a rejected line 0 means the whole file. Split a larger book into several files under one batch code.

1. **Parties:** `POST /party/bulk-jobs/imports`, individuals and organisations, with `controlCount`.
2. **Policies and memberships:** `POST /general/migration/policies`, `/life/migration/policies`, `/health/migration/memberships`. Keep the ids each returns. Let the outbox drain: Billing opens the accounts from it.
3. **Health benefit use to date:** `POST /health/migration/accumulators`, **before any claim on those memberships.** One row per member, category, scope and period. A row's period is worked out here from the plan's benefit year and the membership's start, so the source's benefit year must agree: check that first. `limit` must be the plan's limit for that scope, since a later claim keeps the limit it finds.
4. **Outstanding premiums:** `POST /billing/migration/outstanding-premiums`.
5. **Open General and Life claims:** `POST /general/migration/claims`, `/life/migration/claims`. Reserves are carried without posting.
6. **Opening balances:** `POST /finance/migration/opening-balances`, once per batch. The accounting period must be open. They hold the reserves and receivables in the ledger, so nothing above posts.
7. **Open Health claims:** not imported. Resubmit each through `POST /health/claims`, with `migrationBatch` and `sourceReference`, so it's adjudicated by the same rules. Then validate and adjudicate as any claim. Do this after step 3, so limits are right. **Expect differences from the source's expected outcome:** review any claim paying differently before release.
8. **Reconcile:** `POST /integration/migration-batches/{code}/reconciliation`, with the source system's totals. Kinds: `GENERAL_POLICY`, `LIFE_POLICY`, `HEALTH_MEMBERSHIP`, `HEALTH_ACCUMULATOR` (amount used), `HEALTH_OPEN_CLAIM` (amount claimed), `GENERAL_CLAIM`, `LIFE_CLAIM`, `CLAIM_RESERVE`, `OUTSTANDING_PREMIUM`, and `OPENING_BALANCE:{account}` (debits less credits). Every difference is listed; nothing is changed. Go live only with `reconciled: true`, or with each difference explained and signed off.

This has run only on test rows through the API, never on a real customer's file.
