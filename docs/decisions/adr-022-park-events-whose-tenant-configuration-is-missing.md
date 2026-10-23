# ADR-022: Park an event whose tenant configuration is missing, instead of retrying it as a failure

- **Status:** Proposed (Phase 8 Wave 0, GAP-034, 23 October 2026), pending review. The readiness part is built; parking is not.
- **Date:** 23 October 2026
- **Requirements:** INT-005 (idempotent consumers), INT-006 (retries and dead letters), TEN-004 (tenant isolation), NFR availability targets

## Context
The outbox relay publishes an event to every consumer of it in-process. If any consumer throws, the event is marked failed and retried, with back-off of `Outbox:BaseBackoff` × 2^(n−1). By default that is 5 s doubling, 10 attempts, about 43 minutes in all. Then the event is dead-lettered. Consumers that succeeded are not run again, because their inbox deduplicates.

Many consumers fail not because anything is broken but because a tenant has not configured something yet. For example, when a General policy is issued:

| Consumer | Needs, in the tenant |
|---|---|
| Billing (`GeneralPolicyBilling`) | The policy's collection method and billing frequency in force (`billing.ref_collection_methods`, `billing.ref_billing_frequencies`); for an endorsement, the policy's premium schedule; for a cancellation, the reason code or `CANCELLATION` in `billing.ref_adjustment_and_reversal_reason_codes` |
| Documents (`GeneralDocumentPolicies`) | A template assigned to the product for `POLICY_SCHEDULE` and `CERTIFICATE` in the recipient's language chain (`product.cfg_product_template_assignments`, `document.cfg_document_templates`); for a renewal, the invitation template |
| Reinsurance (`ReinsurancePolicies`) | The billing frequency in force; a cession only where a cession rule matches |
| Analytics (`ProjectionPolicies`) | The billing frequency in force |

Retrying cannot fix a missing configuration. It only waits, and two things go wrong while it does.

1. **One tenant took the whole instance out of service (fixed by this ADR).** `/health/ready` included outbox lag across every module and tenant. One event stuck in retries for more than 5 minutes made the instance Unhealthy, so a load balancer stopped sending it any traffic.
2. **The event is dead-lettered after about 43 minutes, wherever it was.** Someone must notice, add the configuration and replay it by hand. Meanwhile the lag alert fires for a tenant setup task.

Finance already handles its own case differently: a posting with no matching posting rule becomes a `PostingException` that is worked and resolved. It is not retried.

## Decision

### 1. Readiness says only whether the instance can serve requests (built)
`/health/ready` checks the database (`postgres`), the audit partitions (`partitions`) and the router (`endpoints`). Outbox lag stays on `/health`, Degraded after 1 minute and Unhealthy after 5, and is alerted on (`docs/runbooks/monitoring.md`). This needs no change to the outbox or inbox.

### 2. A consumer reports missing configuration, and its delivery is parked (proposed)
- **Signal.** A consumer throws `PrerequisiteMissingException`, from the building blocks, with a stable code and the missing item (for example `BILLING_FREQUENCY_NOT_IN_FORCE`, `MONTHLY`) instead of `InvalidOperationException`. Any other exception is a failure, as today.
- **Delivery per consumer.** The relay publishes to each consumer separately. The event is marked published once every consumer has either processed or parked it. A parked consumer no longer holds up the others, and the event no longer counts as lag.
- **Parked deliveries.** A parked delivery is recorded in `integration.dead_letter_message`, with source `consumer:<name>`, kind `PARKED`, the code and the envelope. It reuses the dead-letter register, its API, its permissions and its replay, rather than adding a table per module. Replay sends the event to that consumer only.
- **Retry once the configuration exists.** When static data is activated in the tenant (`StaticDataVersionActivated`), the tenant's parked deliveries are retried at once. A periodic retry (hourly by default) catches configuration that does not come through static data. A delivery that still lacks its prerequisite stays parked: it is not counted as an attempt and is never dead-lettered for it.
- **Alert.** `/health` gets a `parked-deliveries` check, Degraded while any is parked, with counts per tenant and code. The dead-letter list filters `kind=PARKED`, so tenant setup work is visible to operators and the front end.

## Consequences
- An instance stays in service whatever one tenant's configuration is (TEN-004).
- Missing configuration becomes a visible, self-clearing work item instead of a retry storm and a dead letter.
- The relay changes from all-or-nothing per event to per-consumer delivery. The inbox stays the deduplication guard, so a consumer still processes an event at most once.
- Consumers must say which of their failures are prerequisites. Until a consumer is changed, its failures behave as today.
- `dead_letter_message` gains `kind` (`FAILED`, `PARKED`), `consumer` and `reason_code` columns (a new migration).

## Alternatives considered
- **Longer retries, or a separate lag threshold per module.** This delays the problem and does not clear it.
- **Validate the configuration before issuing the policy.** It is right where it can be done (the General product check could), but it cannot cover every consumer, which are other modules' concerns, and it does not cover configuration that is withdrawn later.
- **Skip the work silently.** This loses billing or documents without anyone knowing.
