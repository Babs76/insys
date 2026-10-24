# Addendum: aggregation and risk limits at issue and endorsement (F062)

**Signed off 23 October 2026** (ADR-019); closes OQ-076. **Built in Wave 0b batch 3** (24 October 2026):
- **Zone limits:** a quote over one is referred with reason `AGG_LIMIT` (the referral reason column holds 15 characters), and issues once an underwriter approves.
- **Endorsements:** the approval request names the limit exceeded.
- **The alert:** on at 80% of each limit by default (24 October 2026), or the limit's own `alert_at_pct` (each tenant's limits are its own static data); the platform default is `General:Aggregation:AlertAt`.
- **Settlement:** over the remaining aggregate it is refused.
- **Alerts recorded and reviewed (Wave 1 batch 1, GAP-051):**
  - Each alert is kept in `general.aggregation_alert`, once a day per zone, peril and currency, and listed at `GET /general/aggregation-alerts`.
  - Workflow keeps one open `AGGREGATION_REVIEW` task per zone and peril, in the queue of `General:Aggregation:AlertRole` (OQ-085).
- **Reinstatement:** automatic and on-request reinstatement are not defined, so every aggregate stops settlement when used up, as `NONE` does, until the rule is chosen.

- **Basis:** static data
  - `general.cfg_aggregation_zones_and_limits`: zone, peril, `max_exposure`, currency;
  - `general.cfg_aggregate_limit_definitions`: coverage, period (policy period or annual), `aggregate_amount`, `reinstatement` (none, automatic, on request).

  `general.exposure_snapshot` sums in-force sum insured by zone and peril daily (ADR-015). Quotes can be referred to an underwriter, and endorsements are approved before they apply.
- **What the code does today:** neither table is read. The snapshot is computed but checked against nothing.
- **What is undefined:**
  - what happens when a limit would be exceeded;
  - what "alert" means, and from what level;
  - how an aggregate claims limit and its reinstatement work.

## 1. Zone and peril exposure (`cfg_aggregation_zones_and_limits`)
- **Measure.** A zone and peril's exposure is the sum insured of in-force policies' risks there. It is computed live at the check, not from the daily snapshot, so that two policies issued on the same day are both counted.
- **At quote issue:** if this quote's risks would take a zone and peril past `max_exposure`, the quote is **referred** (reason `AGGREGATION_LIMIT`) instead of issued. An underwriter decides through the existing referral flow, as for any referral. The quote is not refused outright, because the limit is a capacity control that an authorised underwriter may exceed.
- **At endorsement:** an endorsement that adds sum insured past the limit needs approval before it applies, as other endorsements do. The approval request says which limit it exceeds.
- **Alert:** when a zone and peril's exposure reaches `Aggregation:AlertAt` of its limit, an `AggregationLimitApproached` event and a log line are raised once a day per zone and peril, for operations and reinsurance. The level is a business choice (OQ-076); none is set until then.

## 2. Aggregate limits on claims (`cfg_aggregate_limit_definitions`)
A coverage's settlements in the period (the policy period, or the policy year for `Annual`) cannot exceed `aggregate_amount` (GEN-008).

| `reinstatement` | Effect |
|---|---|
| None | Settlement stops at the aggregate |
| Automatic | The aggregate is restored once. How often, and at what premium, is not specified (OQ-076) |
| On request | As None, until a reinstatement endorsement restores it |

Settlement over the remaining aggregate is refused (`AGGREGATE_LIMIT_EXCEEDED`), with the amount remaining.

## Consequences
- **Quote issue and endorsement** read the limits and the live exposure. One indexed query per zone and peril, on `general.policy_risk` joined to `general.risk_object`.
- **Settlement** reads the coverage's settled total in the period.
- **No migration** for zone limits. Reinstatement, once defined, may need one.
