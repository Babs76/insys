# Money operations

Procedures for the people who run payments, reconciliation, finance, commission, reinsurance and regulatory reporting.

Rules that apply throughout:

- Anything that moves money is approved by a second person in Workflow (SRS-002), never by its maker. The checker must also have the authority for the approval type and amount (ADR-009).
- Posted financial records are never changed. A correction is a reversal (FIN-002, FIN-003).

Approvals are found and decided in Workflow:

```
GET  /api/v1/workflow/approvals?…                [approval.read]
POST /api/v1/workflow/approvals/{id}/approve     [approval.approve]
POST /api/v1/workflow/approvals/{id}/reject      [approval.approve]   with a reason
```

## Unknown payment outcomes (PAY-005)

A payment that was sent but has had no answer becomes **Unknown**:

- either when the provider gives no answer;
- or when the Payments job finds it pending for longer than `Payments:Jobs:UnknownAfter` (24 h by default; the time is not specified, OQ-058).

An Unknown payment is never assumed to have succeeded. It raises an UNKNOWN_OUTCOME reconciliation exception.

1. Take the exception: `POST /api/v1/payments/reconciliation-exceptions/{id}/assign` [reconciliation.update]. Only the person who takes an exception can resolve it.
2. Find out what happened: from the provider's portal, the bank statement or the payee.
3. Decide the payment: `POST /api/v1/payments/payments/{id}/resolve` [payment.resolve], with `{ "succeeded": true|false, "evidence": "…" }`.
   - Either way, the payment raises the same event a provider's answer would: PaymentSucceeded or PaymentFailed.
   - Allocation, posting and the paying process then follow as usual.
4. Close the exception: `POST …/reconciliation-exceptions/{id}/resolve`. This is refused while the payment is still Unknown.

A succeeded payment that turns out to be wrong is reversed with `POST /api/v1/payments/payments/{id}/reverse` [payment.resolve], together with its allocations. It is never edited.

## Provider callback endpoints (ADR-023)

A provider reports payment outcomes to its tenant's callback address, `POST /api/v1/payments/callbacks/{endpointKey}`, with no token. The endpoint key names the tenant and provider; the signature proves the provider.

- **Issue:** `POST /api/v1/payments/callback-endpoints` `{ "providerCode": "<code>", "requireClientToken": false }` [`payment_callback_endpoint.update`]. The response holds the callback path (with its key) and the signing secret, **once**. Give both to the provider through its secure onboarding channel, never by email or chat. Neither can be shown again; if lost, rotate.
- **What the provider sends:**
  - `X-Callback-Timestamp`: Unix seconds;
  - `X-Callback-Id`: unique per callback;
  - `X-Signature`: HMAC-SHA256 of `timestamp.callbackId.body` under the secret, hex in lower case.

  A timestamp more than 5 minutes off is refused, and so is a repeated ID (409 `CALLBACK_REPLAYED`).
- **Rotate:** issue again for the same provider. The current endpoint becomes `Retiring` and is still accepted for 7 days (`Payments:Callbacks:Overlap`), so the provider can move. After that it is refused.
- **Revoke** at once, for a leak: `POST /api/v1/payments/callback-endpoints/{id}/revoke`. Then issue a new one.
- **Optional client credentials:** for a provider that supports them, set `requireClientToken`. The provider must then also send a Keycloak client-credentials token for the tenant, from a confidential client whose service account holds only `payment_callback.create` (ADR-017).
- **Firewall:** allow the callback path only from each provider's published addresses at the WAF, as a second layer.
- **Refusals** are logged by endpoint ID only (never the key, secret or signature), and recorded in `payment.provider_callback` with `signature_valid = false` when the key was known.

## Bank statements and reconciliation (PAY-003)

1. Import the statement: `POST /api/v1/payments/bank-statements` [reconciliation.update], with `{ accountRef, lines: [...] }`. The lines are matched to payments within the matching tolerance in force (the lowest one, OQ-058).
2. Lines that do not match, or match more than one payment, become reconciliation exceptions: `GET /api/v1/payments/reconciliation-exceptions` [reconciliation.read].
3. For each one, take it, investigate it, and act through the normal commands. For example, record the payment, reverse a duplicate, or allocate a received payment by hand (`POST /api/v1/payments/payments/{id}/allocate` [payment.update]). Then resolve the exception.
   - Resolving an exception records who closed it and when. It does not change any money by itself.

## Payouts

Payouts are made for claims, refunds, commission and provider remittances.

1. Batch the payouts waiting for a purpose and currency: `POST /api/v1/payments/payout-batches` [payout_batch.create], with `{ purpose, currency }`.
2. A second person approves the batch in Workflow.
3. Release it: `POST /api/v1/payments/payout-batches/{id}/release` [payout_batch.release]. Each payment goes to its provider. Each payee is paid to their verified bank account (ADR-013).
4. Watch the payments. Failures and Unknown outcomes are handled as above.

A payout with no payment method in force for its currency is skipped and logged (OQ-058). Search the logs after each release.

## Accounting periods (Finance)

| Transition | Call [accounting_period.update] | Effect |
|---|---|---|
| Future → Open | `POST /api/v1/finance/periods/{periodCode}/open` | The period accepts postings. It must be defined in the accounting calendar. |
| Open → Soft close | `POST …/{periodCode}/soft-close` | No more postings |
| Soft close → Closed | `POST …/{periodCode}/close` | Final |
| Soft close → Open | `POST …/{periodCode}/reopen` | Only after a second person approves it in Workflow |

**Month end:**

1. Clear the posting exceptions: `GET /api/v1/finance/posting-exceptions` [posting_exception.read]. These are business events that could not be posted (FIN-007).
   - Fix the cause first: a missing posting rule, a missing account, or a closed period.
   - Then post the event again with `POST …/posting-exceptions/{id}/retry` [journal.reverse]. You can give `{ "accountingDate": "yyyy-MM-dd" }` to post it into an open period.
2. Check the trial balance: `GET /api/v1/finance/periods/{periodCode}/trial-balance` [journal.read].
3. Soft-close the period. Clear any exceptions that appear late. Then close it.

## Commission (ADR-014, a proposal)

1. Commission is earned when a premium is paid in full. An entitlement is clawed back when its policy is cancelled or lapses (`clawback_rule` UNEARNED). You can also claw one back by hand: `POST /api/v1/distribution/entitlements/{id}/claw-back` [commission.update].
2. Each month, prepare each intermediary's statement: `POST /api/v1/distribution/statements` [commission_statement.create], with `{ intermediaryCode, periodCode, currency }`.
   - The statement holds the payable entitlements not yet on a statement, less any clawbacks to recover.
   - Preparing again replaces a draft.
3. A second person approves the statement in Workflow (DST-004).
   - A rejection returns the statement to draft.
   - Once approved, the statement is paid through a COMMISSION payout batch (see Payouts), and the payout's success settles it.

Tax on commission, overrides and negative statements are not specified, so statements carry zero tax (OQ-062).

## Reinsurance (ADR-015, a proposal)

- **Cessions** are calculated automatically when a policy is issued or changed: `GET /api/v1/reinsurance/cessions?…` [cession.read]. Superseded cessions are kept.
- **Recoveries** are identified when a claim is settled.
  1. Bill each one to the reinsurer: `POST /api/v1/reinsurance/recoveries/{id}/bill` [ri_recovery.update].
  2. Record what the reinsurer paid: `POST …/{id}/settle`, with `{ amount, settledOn }`. A difference stays visible on the recovery for follow-up (REI-003).
- **Bordereaux** are prepared one per treaty and period.
  1. Prepare: `POST /api/v1/reinsurance/bordereaux` [bordereau.create], with the layout and period.
  2. Send: `POST …/{id}/send` [bordereau.update].
  3. Agree: `POST …/{id}/agree`, once the reinsurer agrees.

  Preparing the same treaty and period twice is refused (`RI_BORDEREAU_EXISTS`).

## Regulatory returns (ADR-015, a proposal)

1. Prepare the return for the period it covers: `POST /api/v1/compliance/returns` [regulatory_return.create]. There is one return per code and period, and its values come from the definition's field mapping.
2. Review it: `POST …/{id}/review` [regulatory_return.update]. The reviewer must not be the preparer.
3. Approve it: `POST …/{id}/approve` [regulatory_return.approve]. This is a compliance officer who is neither the preparer nor the reviewer.
4. Submit it: `POST …/{id}/submit`. Then record the regulator's answer:
   - `…/accept`;
   - or `…/reject` with the regulator's reason. Then `…/correct`, which calculates the values again and returns the return to Prepared, and go round steps 2–4 again.

## Analytics

The reports at `GET /api/v1/analytics/written-premium` and `claims-incurred` [analytics.read] are projections built from events.

- If they look wrong after a defect is fixed, rebuild them: `POST /api/v1/analytics/projections/rebuild` [analytics.update]. This clears the tenant's projections and replays the events Analytics consumed.
- Rebuilding can be repeated: each run starts from empty projections.
