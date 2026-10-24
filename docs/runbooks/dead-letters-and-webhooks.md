# Dead letters and webhooks

## Dead letters (INT-006)

A dead letter is a message that could not be delivered after all its retries:

- **`outbox:<schema>`**: an event whose consumers kept failing. The outbox relay retries it `Outbox:MaxAttempts` times (10), with back-off from `Outbox:BaseBackoff` (5 s), and then dead-letters it.
- **`webhook:<subscription>`**: a partner webhook whose retries are used up (see below).

The dead letter keeps:

- the original payload, which carries references only, never medical or identity data (INT-007);
- the exception type (never the exception message);
- the number of attempts and when the message first failed.

### Find them

```
GET /api/v1/integration/dead-letters?status=DeadLettered&source=outbox:general   [dead_letter.read]
GET /api/v1/integration/dead-letters/{id}                                         [dead_letter.read]
```

The list is paged with `cursor` and `limit`. The GET returns an ETag, which replay and discard need as `If-Match`.

### Decide

1. **Find the cause** before replaying. The error names the exception type. The host logs at the time of `firstFailedAt` show which consumer failed, keyed by the event ID (`messageRef`).
   - Missing configuration (a document template, a posting rule, static data not yet in force): fix the configuration first.
   - A defect: deploy the fix first.
   - A partner or database outage that is over: nothing to fix.
2. **Replay** an `outbox:` dead letter once the cause is fixed:
   ```
   POST /api/v1/integration/dead-letters/{id}/replay   If-Match: "<etag>"   [dead_letter.update]
   ```
   - This delivers the event again to every consumer. Consumers deduplicate through their inbox (INT-005), so the consumers that had already processed it skip it, and only the failed one does the work.
   - Delivery happens before the dead letter's own commit (OQ-019). If the replay call fails, it is safe to replay again.
3. **Discard** when the event should never be processed, for example a test event or a record corrected by hand:
   ```
   POST /api/v1/integration/dead-letters/{id}/discard  If-Match: "<etag>"   [dead_letter.update]
   ```
   Discarding means that event's consequences never happen in the consumers that had failed. Record why in the ticket; the API takes no reason.

Rules for both replay and discard:

- Only an identified operator (a user, not a service identity) can replay or discard.
- Both are audited.
- A dead letter is resolved once. A second replay or discard answers 409 `INVALID_STATE_TRANSITION`.
- A `webhook:` dead letter **cannot be replayed** (409). See [Failed webhooks](#failed-webhooks).

### Events that need tenant configuration (GAP-034, ADR-022)

A consumer that finds the tenant's configuration missing **parks** its delivery instead of failing. It shows at `GET /api/v1/integration/parked-deliveries`, with the code and item that name what is missing, and later events for the same record wait behind it. It is retried when configuration is published in the tenant, every 15 minutes, or from `POST /api/v1/integration/parked-deliveries/{id}/retry` (or `/retry` with a code). It is never dropped: discarding needs a reason and is audited. Escalation and alarms are in `deployment.md` (Cloud Eye) and ADR-022.

| Event | Consumer | Parks with | Needs, in the tenant |
|---|---|---|---|
| `GeneralPolicyIssued`, `LifePolicyIssued` | Billing | `COLLECTION_METHOD_NOT_IN_FORCE` <method>, `BILLING_FREQUENCY_NOT_IN_FORCE` <frequency> | The policy's collection method (`billing.ref_collection_methods`) and billing frequency (`billing.ref_billing_frequencies`) in force |
| `HealthMemberEnrolled` | Billing | As above (frequency `MONTHLY`) | As above |
| `GeneralPolicyIssued`, `GeneralPolicyEndorsed`, `LifePolicyIssued` | Documents | `DOCUMENT_TEMPLATE_NOT_IN_FORCE` <product>:<document types> | A template assigned to the product for each document type (`product.cfg_product_template_assignments`), in force in the recipient's language chain |
| `GeneralRenewalInvited`, `ProviderPaymentRunReleased` | Documents | `DOCUMENT_TEMPLATE_NOT_IN_FORCE` <template code> | The template in force in the recipient's languages |
| `LifePolicyIssued` | Reinsurance, Analytics | `BILLING_FREQUENCY_NOT_IN_FORCE` <frequency> | The billing frequency in force |
| `GeneralPolicyIssued`, `LifePolicyIssued` | Reinsurance | `TREATY_NOT_APPLICABLE` <error code> | A treaty for the product that can be applied |
| `InvoiceOverdue` | Billing | `ARREARS_AGEING_BUCKET_NOT_IN_FORCE` <days overdue> | An arrears ageing bucket (`billing.cfg_arrears_ageing_buckets`) covering that many days; once published, the parked opening of the account's arrears case is retried (request 46) |
| `GeneralPolicyCancelled` | Billing | `ADJUSTMENT_REASON_NOT_IN_FORCE` `CANCELLATION` | The reason code, or `CANCELLATION`, in `billing.ref_adjustment_and_reversal_reason_codes` |
| Refund, claim payment, remittance or commission released | Payments | `PAYOUT_METHOD_NOT_IN_FORCE` <currency>, `PAYOUT_PROVIDER_NOT_IN_FORCE` <method> | A payout method for the currency, and a provider supporting it |
| `GeneralPolicyEndorsed` | Billing | (fails, then waits in order) | The policy's premium schedule, from its issue: an endorsement waits behind a parked issue |

Anything else a consumer fails on is still a failure: retried with back-off, then dead-lettered, while later events for its record wait behind it. Outbox lag is not part of readiness, so a stuck event does not take the instance out of service.

## Webhooks

Partner subscriptions (`integration.cfg_webhook_subscriptions`) list the event types a partner receives.

- For every published event of those types, Integration creates one delivery per subscription in force. The delivery body is `{eventId, eventType, eventVersion, occurredAt, data}`, signed in `X-Insys-Signature` as `sha256=<HMAC-SHA256 of the body>`. The key is the subscription's secret reference, resolved from `Integration:Secrets:<ref>` (OQ-066).
- The webhook job runs every `Integration:Webhooks:JobInterval` (30 s), with a timeout of `Timeout` (10 s) per request.
- **Retries:**
  - A 2xx response delivers it.
  - Anything else, including a timeout, is retried by the subscription's retry policy (`integration.cfg_retry_and_dead_letter_policies`). The default is five attempts, exponential from one minute, at most an hour apart (OQ-066).
  - When the attempts are used up, the delivery is Failed and dead-lettered as `webhook:<subscription>`.

### Pending deliveries

```
GET  /api/v1/integration/webhook-deliveries?status=Pending&subscriptionId=WH-1   [webhook_delivery.read]
POST /api/v1/integration/webhook-deliveries/{id}/retry                           [webhook_delivery.update]
```

- The list shows each delivery's latest attempt: `attempt`, `httpStatus` and `error`.
- `retry` makes a **pending** delivery due at the next run of the job, instead of at its back-off time. Use it once the partner says it is back up.
- A delivered or failed delivery cannot be retried (409).

### Failed webhooks

A delivery cannot go from Failed back to Pending: `domain-model.json` defines only Pending → Delivered and Pending → Failed. The schema also allows only one delivery per subscription and event.

**There is therefore no way to resend a failed webhook.** Until OQ-066 is decided:

1. Tell the partner which events they missed: the dead letters' `messageRef` values are the event IDs.
2. Have the partner fetch the current state through the API.
3. Discard the dead letter.

## Integration with external systems

Calls to partners (payment providers, reinsurers, regulators) go through adapters, which are stubs in this build. Each call is logged in `integration.external_call_log` with its operation and partner code.
