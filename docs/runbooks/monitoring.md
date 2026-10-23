# Monitoring

## Health endpoints

None of the health endpoints needs a token, and none is rate-limited.

| Endpoint | Checks | Use for |
|---|---|---|
| `/health/live` | Nothing beyond the process answering | Liveness probe: restart the process if it fails |
| `/health/ready` | `postgres`, `outbox-lag`, `partitions` | Readiness probe: stop sending traffic while it is Unhealthy |
| `/health` | Every check, with its description and data | Dashboards and investigation |

### `postgres`

Whether the database answers on the API role. If it fails, check the database, the network and the `ConnectionStrings:Template` credentials.

### `outbox-lag`

The age of the oldest event that has not been published yet, across every module outbox.

- Degraded above `Outbox:Lag:Warning` (1 minute by default). Unhealthy above `Outbox:Lag:Failure` (5 minutes).
- The data gives `lagSeconds`, `pending` and `slowestOutbox`.
- Dead-lettered events are not counted here. They appear as dead letters (see [dead-letters-and-webhooks.md](dead-letters-and-webhooks.md)).

**When it is Degraded or Unhealthy:**

1. Check that the relay is running: `Outbox:RelayEnabled` is on, and the host logs show the relay polling.
2. Look at `slowestOutbox`. A burst, such as a bulk import or a rebuild, clears without action; watch that `pending` falls.
3. If `pending` does not fall, look in the logs for exceptions from that module's consumers. An event whose consumer keeps failing is retried up to `Outbox:MaxAttempts` (10) with back-off from `Outbox:BaseBackoff` (5 s), and is then dead-lettered.
4. While an event waits, the record it describes is committed, but its consequences in other modules are not there yet: postings, accumulators, projections and webhooks. Expect those reads to be behind until the lag clears.

### `partitions`

How far ahead the eight monthly-partitioned tables are covered (ADR-005).

- The Audit job creates partitions `Audit:Partitions:MonthsAhead` months ahead (3), every 12 hours.
- The check warns when fewer than `WarnBelowMonths` months (2) remain.

**When it warns:**

1. Check that `Audit:Partitions:Enabled` is on, and look in the logs for errors from `PartitionMaintenance`.
2. The job calls `audit.ensure_monthly_partitions` (SECURITY DEFINER), which gives every new partition its parent's tenant policy. The Audit role has no DDL rights of its own, so do not create partitions by hand as a module role.
3. If the job cannot run, an owner can call the function directly: `SELECT * FROM audit.ensure_monthly_partitions(3);`.
4. A write into a month that has no partition fails. Treat a horizon under one month as urgent.

## Background jobs

The Payments job runs every 15 minutes; the Billing, General, Life, Health and Product Factory jobs run every hour.

- They make the daily transitions as a service identity, one record per transaction. Examples: policies coming into force or lapsing, preauthorisations expiring, payments timing out to UNKNOWN.
- A record the command refuses is logged with its ID and error code, and the job moves on to the next one. It is tried again on the next run.
- An unexpected exception stops that run. Only the exception type is logged, so that no personal data reaches the logs. The next run starts again from the beginning.
- The webhook job runs every 30 seconds.

Look in the logs for repeated warnings about the same record ID, and for `… jobs failed` errors.

## Telemetry

- Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces, metrics and logs over OTLP. Without it, logs go to the console only.
- Medical and identity data never appears in logs, events or exceptions (CLAUDE.md, non-negotiables). Report any that does as a security incident (see [security-incidents.md](security-incidents.md)).

## Signals worth alerting on

| Signal | Source | Suggested threshold |
|---|---|---|
| `/health/ready` Unhealthy | Probe | Any, for more than 2 minutes |
| Outbox lag Degraded | `/health` data | More than 10 minutes in an hour |
| New dead letters | `GET /api/v1/integration/dead-letters?status=DeadLettered` [dead_letter.read] | Any |
| Webhook deliveries FAILED | `GET /api/v1/integration/webhook-deliveries?status=Failed` [webhook_delivery.read] | Any |
| Reconciliation exceptions open | `GET /api/v1/payments/reconciliation-exceptions` [reconciliation.read] | Older than one business day |
| Posting exceptions | `GET /api/v1/finance/posting-exceptions` [posting_exception.read] | Any, before a period close |
| HTTP 429 | Logs, or the OTLP metrics | Sustained: `RateLimiting:PermitsPerMinute` (1,200 per tenant, OQ-013) may be too low |
| HTTP 5xx | Logs, or the OTLP metrics | Any sustained rate |

The thresholds are suggestions. The specifications define no alerting targets.
