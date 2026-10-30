# Backlog

Work decided but not yet planned into a batch, by the wave it is meant for. Each item says who decided it and what holds until it is built.

## Wave 4

### B-001: a General endorsement that changes a policy's intermediary (broker of record change)
- **Decided by:** Ade, 28 October 2026, from BR-092.
- **What:** a General endorsement type that moves a policy to another intermediary from its effective date, as Life's `INTERMEDIARY` alteration does for Life policies (external-user access §2, "Servicing intermediary"). Access moves with it at the effective date: the new broker gains the policy, its claims and documents, and the client's insured items; the old broker loses them.
- **Until then:** a broker services a client now as BR-092 defines it (the client's live General policy, or with none its latest quote, under the broker's codes; or the broker registered the client). The intermediary changes only when the client's policy with one broker ends and another's is issued.
- **To decide when planned:** whether the change needs underwriting or a second person, how commission splits across the effective date, and whether a change dated in the future shows to both brokers until then.

## Deferred from Waves 5b and 6 (Ade, 29 October 2026)

### B-002: solvency and capital reporting extracts (F063)
- **Decided by:** Ade, 29 October 2026, accepting the Waves 3 to 6 plan.
- **What:** extracts that support solvency and capital reporting (SAM in South Africa). They are not in the SRS.
- **Until then:** the regulatory returns (Compliance) and the reinsurance exposure snapshot are what INSYS reports.

### B-003: premium financing (F079)
- **Decided by:** Ade, 29 October 2026.
- **What:** premiums paid through a financing arrangement, with the financier as payer and its own schedule. They are not in the SRS.
- **Until then:** a policy's payer is a party paying on the billing schedule.

### B-004: wellness programmes, treatment compliance and treatment outcomes (F089 to F091)
- **Decided by:** Ade, 29 October 2026.
- **What:** wellness programme benefits (F089), monitoring treatment compliance (F090), and monitoring treatment outcomes where the data exists (F091). They are not in the SRS.
- **Until then:** benefit categories and limits cover what a plan configures; Wave 5b.4 builds disease management, preventive screening and vaccinations (F086 to F088).

## Engineering, for a quiet stretch (Ade, 29 October 2026)

### B-005: one end-to-end test host per class
- **Decided by:** Ade, 29 October 2026, after measuring CI (OQ-129).
- **What:** the end-to-end process tests start a host per test (38 per dated job, each building its router in about 79 seconds on a runner). One host per test class, with each test in its own tenant and codes, as the integration tests already share theirs, would cut the dated jobs from about 22 minutes to 8 to 10.
- **Why not now:** a CI run is bounded by the integration shards (about 18 to 20 minutes), so it saves about 3 minutes a run, for 1 to 2 days of work and some isolation risk. Paid 8-core runners were also declined (about $300 to $400 a month for about 8 minutes a run).
- **When:** a quiet stretch, such as while an addendum awaits review, or the hardening phase. Sooner only if the OQ-129 stall recurs or an integration shard passes 30 minutes.
- **Until then:** the stall logging stays on the dated jobs, and their 30-minute limit is unchanged.


## Onboarding readiness, not this wave (Ade, 30 October 2026)

### B-006: warn when an approval type's deciding roles have too few active users
- **Decided by:** Ade, 30 October 2026, after the 4.2 approval authority readiness item.
- **What:** a readiness warning, not a blocker, for each approval type the tenant can raise whose deciding roles (as `APPROVAL_AUTHORITY` finds them: authority, `approval.approve` and the type's own permission) are held by no active user, and a second warning when only one active user holds them, since maker-checker (SRS-002) then cannot decide that person's own requests.
- **Until then:** `APPROVAL_AUTHORITY` checks that a role can decide each type, not that anyone holds the role; staffing the roles is left to the tenant.

## Wave 4 follow-ups (decided 30 October 2026, on the 4.3b and 4.3c reports)

Estimates are working days for one developer, including unit, integration and end-to-end tests, snapshots and the batch report; CI and review time are not counted.

### B-007: a premium adjustment endorsement (General)
- **Decided:** 30 October 2026, option (b) of the 4.3b report, as a general feature, not retention-only.
- **What:** a General endorsement action `PREMIUM_ADJUSTMENT` that changes a policy's annual premium by a per cent (a discount or a loading) from its effective date for the rest of the period, without re-rating the risk. It is approved like any endorsement (maker-checker, within the approver's authority) and billed through the existing `GeneralPolicyEndorsed` path (Billing's `AmendSchedule`; instalments already invoiced are not changed). An accepted mid-term retention discount drafts one for the case's owner to submit.
- **Estimate:** 2 to 3 days.
- **Until then:** the manual route stands: the case owner records the accepted offer and makes the change by hand, then closes the case.

### B-008: a billing frequency change endorsement (General)
- **Decided:** 30 October 2026, option (b) of the 4.3b report, as a general feature, not retention-only.
- **What:** a General endorsement action that changes a policy's billing frequency from its next uninvoiced instalment, to a frequency the product allows (`cfg_product_billing_frequencies`), with that frequency's instalment loading. Billing amends the schedule's frequency and instalments from that date; invoices already issued stand. Approved like any endorsement. An accepted payment-terms retention offer drafts one.
- **Estimate:** 3 to 4 days (Billing's schedule change by frequency is new; the endorsement side follows B-007).
- **Until then:** the manual route stands (a billing change by hand).

### B-009: recover commission already paid when a broker of record changes (tenant setting)
- **Decided:** 30 October 2026, on 4.3c's question 1.
- **What:** a tenant setting for a broker of record change when the outgoing broker's commission on the period is already paid on a settled statement:
  - `RECOVER` (default): the unearned part (the days from the change's effective date) is offset against the outgoing broker's next statement and allocated to the incoming broker as a broker share.
  - `KEEP`: paid commission stays with the outgoing broker (today's behaviour).
  - With `RECOVER`, when the outgoing broker has no later commission to offset, the amount is recorded as owed by the broker and is visible to finance. It is never dropped.
- **Needs:** a change to the commission lifecycle in `domain-model.json` (a paid entitlement gains a partial recovery), the setting (a configuration table and migration), the offset on statement generation, the broker debt record and a finance view of it.
- **Estimate:** 4 to 6 days.
- **Until then:** as built in 4.3c: a paid entitlement is logged, not recovered; the incoming broker gets nothing for those days.

### B-010: a contact party for each intermediary, told of a broker of record change
- **Decided:** 30 October 2026, on 4.3c's question 2.
- **What:** a contact party on the intermediary register (`commission.mst_intermediary_register`; a migration, the static data catalogue and generated code). Once a broker of record change is approved, both the incoming and the outgoing broker's contact party are sent a notice (a new notification type, with a template per tenant).
- **Estimate:** 2 to 3 days.
- **Until then:** the pending change shows on the policy and the endorsement, and staff tell the brokers.
