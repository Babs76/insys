# ADR-012: Health adjudication defaults

- **Status:** Proposed, pending business sign-off. The defaults below were given by the product owner in the Phase 4 review, item 3, to answer OQ-060 items 1 to 9 and 12. Each is configuration (static data or a plan setting), not code, so the business can change them without a release. Worked-example tests are engineering cases pending actuarial confirmation.
- **Date:** 22 October 2026
- **Requirements:** HLT-004 to HLT-007, DBA-011, §11.1 H

## Context
The specs define plans, benefit schedules, co-payment rules, limits, waiting periods, providers, contracts, tariffs, networks and preauthorisation lists, but not how they combine to price and pay a claim line (OQ-060).

## Decision

### Static data and schema (V0019)
| Change | Where | Tier |
|---|---|---|
| Benefit category mapping: code system and a code or code range to a benefit category, effective-dated, with a fallback row per provider type | new `health.cfg_benefit_category_mappings` | 1 |
| Tariff modifiers: code, type (PERCENT_UPLIFT, FIXED_ADD, MULTIPLIER), value, applies to (tariff codes or ranges; empty means all), effective dates | new `health.cfg_tariff_modifiers` | 1 |
| A tariff row belongs to a tariff schedule | `cfg_tariff_schedules.schedule_code` | 1 |
| Contracts and networks reference a tariff schedule | `cfg_provider_contracts.tariff_schedule_code`, `cfg_network_definitions.tariff_schedule_code` | 1 |
| A plan has a reference tariff schedule and a benefit year | `cfg_plans_and_options.reference_tariff_schedule_code`, `benefit_year` (CALENDAR or MEMBERSHIP_YEAR, default CALENDAR) | 1 |
| A provider has a type, for the category fallback, and a Party record, for payment (ADR-013) | `mst_provider_register.provider_type`, `party_id` | 1 |
| Co-payment and deductible rules: kind (CO_PAYMENT or DEDUCTIBLE), the cap's basis, an optional plan | `cfg_co_payment_and_deductible_rules.rule_kind`, `cap_basis`, `plan_code` | 1 |
| Limits for one plan, or for all plans when empty | `cfg_limit_definitions.plan_code` | 1 |
| Exclusions and condition-specific waiting periods carry diagnosis and procedure codes or ranges (`A00-A09`) | `product.cfg_exclusion_library` and `health.cfg_health_waiting_period_rules`: `diagnosis_codes`, `procedure_codes` | 1 |
| Accumulator: reserved amount, family or member scope, deductible met, no limit when only a deductible is tracked (OQ-060 item 12) | `health.benefit_accumulator` | – |
| Claim lines: modifiers, benefit category, deductible, network status, member liability, the matched preauthorisation line | `health.medical_claim_line` | – |

### Adjudication of a claim line, in order
1. **Benefit category (a):** the most specific effective mapping for the line's code system and tariff code: an exact code before a range, a narrower range before a wider one. When none matches, the fallback row for the provider's type. When neither matches, the line pends (`NO_BENEFIT_CATEGORY`).
2. **Network status:** in network when the plan's network lists the provider, or the provider has an effective contract with that network on the service date.
3. **Tariff (b):** in network, the first schedule that prices the tariff code on the service date: the provider's contract schedule, then the network's schedule, then the plan's reference schedule. Out of network, the plan's reference schedule. No price pends the line (`NO_TARIFF`).
4. **Modifiers (c):** the line's modifiers, each applied only when it is effective, applies to the tariff code, and is allowed by the tariff row's `modifiers` list when that list is set; otherwise the line pends (`MODIFIER_NOT_ALLOWED`). They apply in `sort_order`, then code: PERCENT_UPLIFT multiplies by (1 + value / 100), FIXED_ADD adds the value, MULTIPLIER multiplies by the value. Unit price after modifiers × quantity, rounded half away from zero to cents.
5. **Allowed amount (d):** in network, min(claimed, tariff after modifiers). Out of network, by the plan's network restriction rule: NOT_COVERED rejects the line (`OUT_OF_NETWORK`); CO_PAYMENT and COVERED_AT_TARIFF allow min(claimed, reference tariff after modifiers). CO_PAYMENT applies the OUT_OF_NETWORK co-payment rule; COVERED_AT_TARIFF the IN_NETWORK one. Without a restriction rule, out-of-network lines pend (`NO_NETWORK_RULE`).
6. **Waiting periods and exclusions (g):** fixed at enrolment on the membership. A line in a general waiting period is rejected (`WAITING_PERIOD`) unless its category is one of the rule's exceptions. A line whose diagnosis or procedure falls in a condition-specific waiting period's codes is rejected the same way. A line whose diagnosis or procedure falls in an exclusion's codes is rejected (`EXCLUDED`).
7. **Deductible (e):** the DEDUCTIBLE rule for the category and network status: deductible = min(allowed, rule value − deductible already met this benefit year by the member).
8. **Co-payment (e):** on (allowed − deductible): PERCENT is value % of it, AMOUNT is the value, but never more than it. The cap limits the co-payment per `cap_basis`: PER_LINE (default), PER_CLAIM or PER_YEAR (co-payments already taken this benefit year by the member, kept on the accumulator).
9. **Limits (f):** paid = min(allowed − deductible − co-payment, remaining of every applicable limit). Remaining = limit − used − reserved. Applicable limits:
   - the plan's benefit schedule row for the category (PER_EVENT caps the claim; PER_YEAR and LIFETIME are member accumulators);
   - each limit definition for the category and plan (or all plans): ANNUAL or LIFETIME, per member or per family. A family limit is one pool on the principal's membership, with the principal as beneficiary, alongside each member's own sub-limit.

   Where several limits apply, the most restrictive remaining amount wins. Two limits on the same accumulator (same scope and period) keep the lower limit.
10. **Member liability (e):** claimed − paid.
11. **Preauthorisation (h):** a claim matches a preauthorisation when the member, patient and provider match, the line's code is on an authorised line, and the service date is within validity. On a match, the matched line's reservation is released and the paid amount consumed. An allowed amount above the authorised amount pends the line for review (`OVER_AUTHORISED`). A line that needs preauthorisation (the plan's required list: service is the tariff code or category, above the threshold) without a match pends (`PREAUTH_REQUIRED`).
12. **Benefit year (f):** per plan; CALENDAR (default) is 1 January to 31 December; MEMBERSHIP_YEAR runs from the membership start date's anniversary. Lifetime accumulators run from the membership start to 9999-12-31.

### Reservation and consumption
- An approved preauthorisation reserves its authorised amount on the accumulators of each authorised line's category (PreauthorisationApproved, handled in its own transaction). Expiry and cancellation release what is left. A matched claim releases the matched line's reservation for the allowed amount and consumes what it pays; the authorisation is then Utilised and releases the rest.
- **A pended claim changes no accumulator.** Its lines are recalculated when a reviewer answers the pended lines ("Information received"), and only an adjudication without pended lines consumes benefit.
- **One transaction for the claim and its accumulators (a deviation from "one aggregate per transaction"):** "used plus reserved never exceeds the limit" must hold for the amount paid, so adjudication writes the claim and the accumulators it consumes together. The accumulators keep `row_version` (DBA-011). On a conflict the command returns `CONCURRENCY_CONFLICT` and nothing is written; the command is safe to send again, and the caller retries (the process's "adjudication retries").
- **One transaction for a payment run and its claims (the same deviation):** calculating a run assigns each included claim to it, so no claim is paid by two runs (HLT-009).
- **A reversed claim** gives back what it consumed on the accumulators whose period includes its service date, in the same transaction as the reversal.

### Still open (OQ-060)
- **Item 10, edit rules:** conditions may read the line's own facts only (claimed, allowed, quantity, category, tariff code). Duplicate detection (§11.1 H: same member, patient, provider, tariff code and service date as a claim not rejected or reversed) pends the line (`DUPLICATE`) instead of rejecting it.
- **Item 11, member reimbursement claims:** pended for manual adjudication (`MEMBER_CLAIM`).
- **Which exclusions apply to a plan:** no plan-exclusion link exists; every Health exclusion with codes applies to every plan of the tenant. `TODO(spec)`.

## Consequences
- Every default above is data. Changing one is a static data change through maker-checker (ADR-003), not a release.
- The worked examples in `tests/Insys.Health.Tests/AdjudicationWorkedExamples.cs` are labelled "engineering case, pending actuarial confirmation".
