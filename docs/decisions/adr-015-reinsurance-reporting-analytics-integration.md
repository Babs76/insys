# ADR-015: Reinsurance arithmetic, regulatory returns, projections and partner webhooks

- **Status:** Proposed, pending business sign-off. The defaults of OQ-063 to OQ-066, accepted to build on 23 October 2026.
- **Date:** 23 October 2026
- **Requirements:** REI-001 to REI-003, INT-004 to INT-007, GEN-004; §17, §20, §22, §24

## Reinsurance (OQ-063)
- **Cession (CalculateCession)** on GeneralPolicyIssued and LifePolicyIssued under each proportional treaty whose cession rule names the product and whose period covers the policy's start, or under a facultative arrangement for the policy number instead.
  - QUOTA_SHARE: `retention` is the retained percent; the ceded sum insured is the rest, at most `capacity`.
  - SURPLUS: `retention` is an amount; the ceded sum insured is what exceeds it, at most `capacity`.
  - Ceded premium: the same share of the annual premium. A Life premium is annualised by the billing frequency's periods per year.
  - PROBABLE_MAXIMUM_LOSS and STOP_LOSS are refused (no data).
- **Reproducible and linked:** a cession records the gross sum insured and premium, the share, the treaty and rule versions, and the policy (V0021; REI-001, REI-002). It is insert-only.
- **RecalculateCession** on GeneralPolicyEndorsed: a new cession superseding the old, with the premium change. General events now carry the sum insured; General claim events carry the policy.
- **Recovery (IdentifyRecovery, BillRecovery)** on GeneralClaimSettled and LifeClaimSettled:
  - proportional: what was paid × the ceded share of the cession in effect;
  - EXCESS_OF_LOSS: what the claim paid above the retention, at most the capacity.
  - Billed at once through the reinsurer adapter.
  - SettleRecovery records what the reinsurer paid; the difference stays visible (REI-003).
  - Health claims are not ceded.
- **Bordereau** per treaty and period (monthly or quarterly by its layout): the cessions calculated in the period as CSV, in the layout's field order. Sent through the reinsurer adapter (the treaty's first participant), then agreed. *Deviation:* kept on the bordereau rather than as a document.
- **Postings:** PREMIUM_CEDED (increases only; a decrease has no transaction type yet), RI_RECOVERABLE and RI_RECOVERED.

## Compliance and reporting (OQ-064)
- **PrepareReturn** on a final AccountingPeriodClosed, for each return definition whose frequency ends with the period, or by a person.
  - `field_mapping` values: `account:<code>` is the account's movement in the period (Finance's `ILedgerBalances`); `metric:<code>` is the metric's total over the return's months (Analytics' `IProjectionTotals`; the metric's source names the projection measure, and its formula is not evaluated).
- **Lifecycle:** reviewed, approved by a compliance officer (`regulatory_return.approve`) who is neither the preparer nor the reviewer, then submitted through the regulator adapter. The regulator's answer is recorded; a rejected return is corrected back to Prepared with its values calculated again.
- **Report runs:** allowed to users holding one of the catalogue's roles; exported only where `export_allowed`. A run's result is the totals of the metrics named, kept with the run.

## Analytics (OQ-065)
- Every consumed event becomes a minimal entry in `analytics.event_log` (V0021; once per event) and is applied to its projection.
  - **Written premium:** the annual premium at issue, plus endorsement deltas, less cancellation refunds.
  - **Claims incurred:** reserve changes, by the claim's policy product. A claim's registration counts it.
  - **Dimensions:** channel INTERMEDIARY or DIRECT; branch ALL; cause UNKNOWN.
- **Rebuild:** clears the tenant's projections and replays its log.
- Health is not projected, and one currency per tenant is assumed (the projection keys have none).

## Integration (OQ-066)
- **Webhooks:** a wildcard bus subscription (`AnyIntegrationEvent`) creates one delivery per subscription in force that lists the event type. The delivery stores the body it sends (event id, type, version, time and payload), so every attempt sends the same.
  - The webhook job sends due deliveries, signed `X-Insys-Signature: sha256=<HMAC-SHA256 of the body>` with the key named by `signing_key_ref` (resolved from configuration, standing in for the secret store).
  - A 2xx delivers it. Otherwise it is retried by the subscription's retry policy (fixed or exponential back-off; default 5 attempts from 1 minute, at most an hour apart). When the attempts are used up it fails and is dead-lettered (INT-006).
  - RetryDelivery attempts a pending delivery now.
  - *Deviation:* DeliveryAttempt is the delivery's latest attempt; webhook_delivery has one row per delivery.
- **Partner adapters:** `IReinsurerAdapter` and `IRegulatorAdapter` with stub implementations that accept every call and record it in `integration.external_call_log`.

## General renewal and exposure (OQ-066)
- **Renewal invitation:** sent once, `notice_days` before the period ends (`general.policy.renewal_invited_on`, V0021). Documents generates it from `invitation_template`.
- **Auto-renewal:** at the period end, a product whose rule is `auto_renew` without `re_rating` is renewed (RenewPolicy). One that re-rates is renewed through a new quote.
- **Exposure snapshot:** daily; the sum insured of risks attached to policies in force, by the risk object's zone (UNKNOWN when none) and the zone's peril (`general.ref_risk_zones`).

## Consequences
Every default is configuration. The engineering cases in the Phase 6 tests stand until the business signs off.
