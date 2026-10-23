# ADR-022: Park an event whose tenant configuration is missing, instead of retrying it as a failure

- **Status:** **Accepted** (revision 3, 23 October 2026). Revision 2 was approved for building with three additions: GAP-038 payouts (decision 7), a time limit on each delivery attempt (decision 8), and escalation per tenant (decision 6, OQ-072 decided). Readiness (decision 1) is built; parking is built on `wave0-4`.
- **Date:** 23 October 2026
- **Requirements:** INT-005 (duplicate delivery tolerated), INT-006 (controlled retry, monitoring and recovery), PAY-001 (payments traceable), TEN-004 (tenant isolation), NFR-001 (availability), NFR-012 (observability)

## Context

### How events are delivered today
- **The relay** (`OutboxProcessor`) runs on every instance every `Outbox:PollInterval` (2 s):
  - It works tenant by tenant, active tenants only, and module outbox by module outbox.
  - For each, it takes up to `Outbox:BatchSize` (100) unpublished events in `occurred_at` order, with `FOR UPDATE SKIP LOCKED`.
  - It leaves out any event that is still waiting out its back-off.
- **Publishing an event** (`InProcessEventBus`) runs every consumer of it, one after another, each in its own transaction with its inbox row. If any consumer fails, it collects the failure, runs the remaining consumers, and then throws.
  - The relay then marks the event failed (`publish_attempts` + 1).
  - On the next due attempt, the event goes to every consumer again. Those that succeeded skip it, because their inbox already has it (INT-005).
- **Back-off** is `Outbox:BaseBackoff` × 2^(n−1): 5 s, 10 s, 20 s and so on. After `Outbox:MaxAttempts` (10), about 43 minutes in all, the event is dead-lettered (`outbox:<schema>`) and replayed by hand.

### What a retried event holds up today
| Other events | Held up? |
|---|---|
| **For the same consumer, same tenant** | **No.** While an event waits out its back-off, the relay leaves it out and publishes the events after it, for every record. So **order per record is not kept**. A `GeneralPolicyEndorsed` can reach Billing before the `GeneralPolicyIssued` that failed there, and fail in turn ("no premium schedule yet"). It recovers only because it is retried too. |
| **Other consumers of the same event** | **No.** They run on every attempt and succeed. Their inbox makes the repeats no-ops. |
| **For the same tenant, other modules** | **No.** Each module's outbox is a separate batch. |
| **For other tenants** | **Not blocked, only delayed.** The relay is one loop per instance that goes through tenants in turn, so each failing attempt adds its consumers' running time to that pass for everyone. A consumer that fails fast costs milliseconds. One that fails slowly, on a timeout, costs that timeout on every attempt. |
| **The instance** | **Was taken out of service** until decision 1: outbox lag counted towards `/health/ready`. |

### Why retrying does not help here
Many consumer failures are a tenant's missing configuration, not a fault:

| Consumer | Needs, in the tenant |
|---|---|
| Billing | The collection method and billing frequency in force; for an endorsement, the premium schedule; for a cancellation, the reason code |
| Documents | A template assigned to the product for each document type, in the recipient's languages |
| Reinsurance, Analytics | The billing frequency in force |

The full table is in `docs/runbooks/dead-letters-and-webhooks.md`. Retrying cannot create configuration. It only waits, until it dead-letters. Finance already treats its own case as work rather than failure: a posting with no rule becomes a `PostingException`.

## Decision

### 1. Readiness says only whether the instance can serve requests (built)
`/health/ready` checks the database (`postgres`), the audit partitions (`partitions`) and the router (`endpoints`). Outbox lag is on `/health`, and is alarmed on (`docs/runbooks/monitoring.md`; Cloud Eye in `docs/runbooks/deployment.md`).

### 2. A consumer says when configuration is missing, and that delivery is parked
- **The signal.** A consumer throws `PrerequisiteMissingException(code, item)`, from the building blocks, for example `BILLING_FREQUENCY_NOT_IN_FORCE` with item `MONTHLY`. Any other exception is a failure and is retried as today.
- **Delivery per consumer.** The relay records each consumer's outcome for the event separately:
  - processed (its inbox row, as today);
  - failed (retried with back-off, then dead-lettered, as today);
  - parked.

  The event counts as published once no consumer is failing, so a parked consumer no longer holds up the others or counts as outbox lag.
- **The parked delivery** is a row in `integration.parked_delivery` (a new migration):
  - consumer, event ID and type, envelope (the event's payload, which INT-007 already keeps free of sensitive data);
  - record key (aggregate type and ID);
  - the code and item;
  - status: `PARKED`, `WAITING`, `RETRYING` or `RESOLVED`;
  - `parked_at`, the last retry, the attempt count, `escalated_at`, `row_version`.

  The table has tenant row-level security, like the rest of the platform. The consumer's own changes were rolled back, so nothing of it was applied.

### 3. Order: later events for a parked record wait behind it
- **The record key.** Before it delivers an event to a consumer, the relay checks whether that consumer has a `PARKED` or `WAITING` delivery for the same record (tenant, aggregate type, aggregate ID).
  - If it has, the new delivery is stored as `WAITING`, behind it, and not delivered.
  - If it has not, delivery goes ahead.

  Other records, other consumers and other tenants are not held up.
- **Release in order.** When the parked delivery is resolved, its record's `WAITING` deliveries are delivered in `occurred_at` order, in the same pass.
  - If one of them parks in turn, the rest wait behind it.
  - If one fails, it is retried as a failure, and the rest wait behind it too, so order is kept.
- **Order for failures as well.** The same rule applies while a delivery to a consumer is retrying as a failure. That closes today's gap, where a later event for the record overtakes the failing one.

### 4. What retries a parked delivery, and how it is never applied twice
- **Triggers.** A retry runs, for one tenant, on whichever comes first:
  1. **Configuration published.** `StaticDataVersionActivated` in the tenant retries its parked deliveries. Retrying them all keeps it simple: one that is still missing its prerequisite parks again, at no cost.
  2. **A periodic check**, every `Outbox:Parking:RetryInterval`. This catches configuration that does not come through static data, such as a premium schedule created by an earlier event.
  3. **A manual retry** from the operations view: `POST /api/v1/integration/parked-deliveries/{id}/retry` [`dead_letter.update`]. It retries one delivery, or all of a tenant's deliveries for one code.
- **Never applied twice.** Three guards:
  - **One retry at a time.** A retry takes the parked row with `FOR UPDATE SKIP LOCKED` and moves it to `RETRYING` (row version checked). Two triggers at once cannot both run it.
  - **The inbox.** The retry delivers through the consumer's normal inbox handler. Its transaction inserts the inbox row (`consumer`, `event_id`) with the consumer's changes. If the event was already processed, the insert finds the row and the handler is skipped (INT-005).
  - **The outcome.** The parked row's outcome is written after the consumer's transaction commits: `RESOLVED`, or `PARKED` again with the new code. If the instance stops between the two, the next retry finds the inbox row and only resolves the parked row.

### 5. Visibility per tenant, and an alert that names the tenant and what is missing
- **Operations view.**
  - `GET /api/v1/integration/parked-deliveries` [`dead_letter.read`], filtered by status, code, consumer and record. It returns each delivery's record, event type, code, item, parked since, attempts and escalation.
  - A tenant's users see their own tenant (row-level security).
  - The platform operator sees counts per tenant and code at `GET /api/v1/integration/parked-deliveries/by-tenant` [platform `dead_letter.read`, platform-operator document], and drills into one tenant.
- **Alert.**
  - `/health` gets a `parked-deliveries` check: Degraded while any delivery is parked, and Unhealthy once any is escalated. Its data lists each tenant code with its codes, items and counts, for example `OFT-ZA: BILLING_FREQUENCY_NOT_IN_FORCE MONTHLY ×3 (Billing)`.
  - The same is logged once per tenant and code when it first parks, as a structured event (`ParkedDeliveryRaised`) that the log alarm in Cloud Eye matches on.
  - The alert names the tenant and the missing configuration, never a party or payload.

### 6. Escalation, per tenant (OQ-072, decided 23 October 2026)
- **Immediately.** Every park alerts platform operations (the `/health` line and the `ParkedDeliveryRaised` log event) and is listed per tenant (decision 5).
- **Then, by consumer class.** Each tenant can configure these; until it does, the defaults below apply.

| Class | First escalation | Second escalation |
|---|---|---|
| Money: Billing, Payments, Finance, claim payments (Claims Framework), commission (Distribution), reinsurance | A Workflow task to the tenant's configuration-owner role after **4 business hours** | The tenant administrator and the platform operator, after **1 business day** |
| All other consumers | After **1 business day** | After **3 business days** |

- **Business time.**
  - If the tenant has a working calendar, business time follows it. That is a calendar in `workflow.ref_business_calendars` (working days, hours, holiday calendar, time zone), named in the tenant's parking settings.
  - Otherwise business time is Monday to Friday, 08:00 to 17:00, in the tenant's time zone (`tenant.cfg_locale_defaults.time_zone`), or Africa/Johannesburg when it has none.
  - It is computed with Workflow's `BusinessCalendar`, through a Workflow contract.
- **Where it is configured.** A per-tenant parking setting holds the configuration-owner role, the calendar code and the four durations. It is changed by a tenant administrator, and audited.
- **Never dropped.** A parked delivery is never dropped or dead-lettered automatically, however long it waits. Discarding one is a manual action (`POST …/parked-deliveries/{id}/discard`, `dead_letter.update`) that needs a reason, and it is audited.

### 7. Payouts that cannot start are parked (GAP-038; resolves the payout item of OQ-058)
A released claim payment, refund, commission statement or remittance becomes a payout in Payments. Today, if no payout method (or provider) is in force for its currency, it is **skipped with a log line, and the money is never paid**. Now Payments' consumer throws `PrerequisiteMissingException(PAYOUT_METHOD_NOT_IN_FORCE, <currency>)`, so the delivery is parked under this ADR:
- it is visible per tenant;
- it is alerted at once and escalated on the money schedule;
- it is retried when a payout method is published.

The payment is created then, exactly once.

### 8. A time limit on each delivery attempt
Each consumer's attempt runs under `Outbox:AttemptTimeout` (30 seconds by default). An attempt that overruns is cancelled and counts as a failure, retried with back-off. A slow failure in one tenant therefore costs every tenant's relay pass at most that long, not the full length of a remote timeout.

## Consequences
- An instance stays in service whatever one tenant's configuration is (TEN-004, NFR-001).
- Order per record is kept for each consumer. It is not kept today.
- Missing configuration becomes visible, owned work that clears itself, instead of a retry storm and a dead letter (INT-006).
- **Costs:**
  - per-consumer outcome tracking in the relay;
  - a table and its API;
  - one indexed lookup per delivery for the record-key check (tenant, consumer, aggregate type, aggregate ID, status);
  - consumers must throw `PrerequisiteMissingException` where configuration is missing. They are changed module by module, and until then a consumer behaves as today.

## Alternatives considered
- **Longer retries, or a lag threshold per module:** this only delays the dead letter.
- **Checking configuration before issuing a policy:** worth doing where the issuing module can, but it cannot cover every consumer, or configuration withdrawn later.
- **Reusing `dead_letter_message` for parked deliveries** (revision 1): dead letters have no record key or waiting state, so order could not be kept. A separate table keeps the two meanings apart.
- **Skipping the work:** this loses billing or documents without anyone knowing.

## Decided questions
- **OQ-072** (decided 23 October 2026): escalation per tenant, as in decision 6.
- **OQ-058, payouts** (resolved by decision 7): a payout that cannot start is parked, not skipped.
