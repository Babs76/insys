# Runbooks

Operating procedures for the INSYS Core back end. Each one describes what the code does today. Where the specifications leave a procedure undefined, the runbook says so and names the open question; those parts are proposals until they are decided.

| Runbook | When to use it |
|---|---|
| [deployment.md](deployment.md) | Installing, upgrading and applying database migrations |
| [monitoring.md](monitoring.md) | Health endpoints, outbox lag, partitions, background jobs and telemetry |
| [dead-letters-and-webhooks.md](dead-letters-and-webhooks.md) | Events that consumers could not process, and partner webhooks that could not be delivered |
| [tenants-and-access.md](tenants-and-access.md) | Tenant lifecycle, users and roles, privileged access, access reviews |
| [money-operations.md](money-operations.md) | Unknown payments, reconciliation, payouts, accounting periods, posting exceptions, commission, reinsurance and regulatory returns |
| [static-data.md](static-data.md) | Changing reference data and configuration through versions and approvals |
| [security-incidents.md](security-incidents.md) | Suspected compromise, cutting off access, key rotation |
| [backup-and-recovery.md](backup-and-recovery.md) | Backups, restore and recovery (a proposal; the targets are open) |

Conventions used in every runbook:

- Every call is `/api/v1/…` with a bearer token whose `tenant_id` is the tenant being worked on. Platform calls (tenancy, global static data) need a token for the platform operator tenant (`Platform:OperatorTenantId`).
- Each call names the permission it needs, as `[permission]`. Permissions come from the caller's IAM roles, cached for up to 30 seconds (`Identity:PermissionCache:Duration`).
- Calls that change a versioned record need `If-Match` with the record's ETag from a GET; without it the answer is 428, and with a stale one 412. Calls that create records or move money need an `Idempotency-Key`; repeating a call with the same key, within `Idempotency:Retention` (24 h), returns the first result.
  - A 5xx answer is not kept, so the same call can be retried with the same key.
  - A 4xx answer is kept, so a corrected request needs a new key.
  - A different request with the same key is refused with 422 `IDEMPOTENCY_KEY_REUSED`.
- Errors are ProblemDetails with a `code`; the catalogue is `integration.ref_error_code_catalogue` (`db/migrations/R__error_code_catalogue.sql`, readable at `GET /api/v1/integration/static/error-code-catalogue`), with `retryable` per code.
- Every change is audited (`GET /api/v1/audit/events` [audit_event.read]).
