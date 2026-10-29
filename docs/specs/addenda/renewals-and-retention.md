# Addendum: renewals and retention (Wave 4: F006, F007, F015–F019, F022, F025–F027, and B-001)

**For sign-off (Ade). Proposed 29 October 2026. Nothing in it is built before sign-off.** Section 8 lists the decisions, OQ-130 to OQ-136, each with a recommendation. Batches 4.1 to 4.3 (`waves3-6-plan.md`) follow it.

## 0. Why an addendum
What is built today, verified against the code and `docs/traceability/functional-coverage.md`:
- **Grace (F006):** Life only (`life.cfg_grace_period_rules`, LIF-005). General has no grace: a missed instalment raises `ArrearsDetected`, which nothing in General acts on. Health memberships can be suspended by hand, never on arrears.
- **Refunds (F007):** General cancellation refunds are pro rata less the fee. A reason whose `refund_basis` is `SHORT_PERIOD` is refused, because no scale is specified (OQ-059). Life surrender values wait on OQ-052 and are **not** in this wave.
- **Renewal (F018, F023, F024):** General renews per product (`general.cfg_renewal_rules`: `notice_days`, `auto_renew`, `re_rating`, `invitation_template`). The daily job invites the policyholder `notice_days` before the period ends, sends reminders, and on the end date either renews by itself (same risks, coverages and premium) or lets the policy expire. A product with `re_rating` refuses `renew` (`RENEWAL_NEEDS_RATING`): it is renewed by a new quote issued with `renewalOfPolicyId`, which has no test. The renewal is billed (proved in Wave 0).
- **Non-renewal (F025):** expiry at the period end is built. An insurer's decision not to renew, with notice, is not.
- **Health renewal:** `health.cfg_scheme_renewal_rules` (`notice_days`, `re_rating_basis`) exists but nothing uses it. The group scheme renewal term is an open point in OQ-080's follow-ups.
- **Not specified anywhere (F015, F016, F017, F019, F022, F026, F027):** renewal eligibility, risk and claims review, renewal underwriting, recorded acceptance, retention, and their reporting. CLAUDE.md forbids inventing requirements, so this addendum proposes them for you to decide.

The wave is General first. Health gets grace (4.1) and uses its scheme renewal rule for notice only (§4.2); experience re-rating of a Health scheme needs the actuarial basis (OQ-052) and is out of scope. Life renewals do not exist (Life policies are whole-term or term without renewal), so Life is in this wave only through its existing grace.

## 1. The rules it serves (summary, **for compliance to confirm**)
INSYS's first tenants are South African. This is a summary for design, not legal advice; compliance should confirm the numbers before go-live. Every number below is a tenant-configurable default, never a constant in code.
- **Short-term (non-life) policies, Policyholder Protection Rules under the Insurance Act:**
  - the insurer gives written notice before it cancels a policy or does not renew it, commonly 31 days unless the policy says more;
  - the insurer tells the policyholder of a change in premium or terms at renewal before it takes effect, with the same notice;
  - a grace period applies to a monthly premium: cover stays in force for the grace days, and a premium paid in them keeps the policy as if paid on time.
- **Long-term policies:** the existing Life grace (LIF-005) already follows its PPR; this wave does not change it.
- **Medical schemes (Medical Schemes Act and its regulations):** a scheme may suspend benefits when a contribution is not paid by its due date plus a short grace, and may end membership only after written notice. Health's grace (4.1) follows that shape.
- **Treating Customers Fairly:** a retention offer must not be a reason to delay a cancellation the policyholder has asked for. A cancellation request is processed as asked; an offer is made alongside it, never instead of it (§4.6).

## 2. Where it lives
- **General owns renewal and retention for its policies.** It already has the renewal rule, the invitation, the job and the lifecycle (`Renewed`, `Expired`). It gains:
  - a `Renewal` aggregate, one per expiring policy and period, holding the invitation, the review, the offered terms, the decision and the outcome;
  - grace on its instalments;
  - the short-period scale.
- **Health owns its grace and its scheme renewal notice.**
- **Rating** prices the renewal (the existing quote rating, with the policy's risks and the claims factor of §4.3). No new rating engine.
- **Rules** decide renewal eligibility and referral (the existing rules engine and its rule sets), so a tenant changes them without a release.
- **Claims** history is read through the existing Claims Framework contract (claims per policy and period, paid and outstanding), never its tables.
- **Communications** sends the invitation, reminders, notices and the non-renewal letter from tenant templates, as it does for renewal invitations today.
- **Distribution** keeps commission; B-001 adds a broker of record change to it (§4.8).
- **Analytics** gains the renewal and retention figures (§4.7).

## 3. What is recorded
### 3.1 New static data (tenant, Tier 2, through the generator)
| Table | Fields | Use |
|---|---|---|
| `general.cfg_grace_period_rules` | `product_code`, `frequency` (billing frequency), `grace_days`, `cover_during_grace` | General grace per product and frequency (4.1), shaped like Life's |
| `health.cfg_contribution_grace_rules` | `scheme` (or all), `grace_days`, `suspend_after_days`, `terminate_notice_days` | Health grace, suspension and notice (4.1) |
| `general.cfg_short_period_scales` | `scale_code`, `days_in_force_to`, `percent_of_annual_retained` | The short-period table (4.1, OQ-133): for a policy in force up to N days, the insurer keeps this percent of the annual premium |
| `general.cfg_renewal_review_rules` | `product_code`, `ruleset_code` (eligibility), `referral_ruleset_code`, `claims_lookback_years`, `review_days_before_invitation` | What the review checks and how far back (4.2) |
| `general.cfg_claims_experience_factors` | `product_code`, `claims_count_from`, `claims_count_to`, `loss_ratio_from`, `loss_ratio_to`, `factor` | The claims history factor applied at re-rating (4.3, OQ-131) |
| `general.cfg_non_renewal_reasons` | `reason_code`, `notice_days`, `customer_wording`, `requires_approval` | Insurer non-renewal with notice (4.3) |
| `general.cfg_retention_offers` | `offer_code`, `product_code`, `kind` (`DISCOUNT_PERCENT`, `EXCESS_CHANGE`, `COVER_CHANGE`, `PAYMENT_TERMS`), `value`, `max_uses_per_policy`, `authority_role` | What staff may offer to keep a policy (4.3) |
| `general.cfg_cancellation_reasons_and_refund_rules` (existing) | gains `short_period_scale` (nullable) | A `SHORT_PERIOD` reason names its scale |
| `general.cfg_renewal_rules` (existing) | gains `acceptance` (`AUTOMATIC`, `ON_ACCEPTANCE`, `ON_PAYMENT`), `review` (boolean) | How a renewal is accepted (OQ-130) and whether it is reviewed |

Each table states its value column for BR-105 (`x-code-list-column`).

### 3.2 New records
- `general.renewal`: policy, period end, status (§4.4), invitation date, review result, offered premium and terms, the quote it was rated by, the decision, who decided it, and the renewal policy when issued.
- `general.renewal_review_item`: each check's result (eligibility rule, risk change, claims, referral reason).
- `general.retention_case`: policy, trigger (`CANCELLATION_REQUESTED`, `NOT_RENEWING`, `LAPSE_RISK`), offers made and their outcome, owner, outcome (`RETAINED`, `LOST`, `WITHDRAWN`).
- `general.policy.grace_until` and Health's `membership.grace_until`, `suspended_for_arrears`.

## 4. The processes
### 4.1 Grace and short-period refunds (F006, F007; batch 4.1)
- **General grace:** on `ArrearsDetected` for a General policy (using the detection date and overdue count Billing now sends, BR-103), the policy enters grace until the detection date plus `grace_days` for its product and billing frequency. Cover continues if `cover_during_grace`. A payment that clears the arrears ends the grace. When grace ends with arrears still open, the policy is cancelled for non-payment from the day after the last paid period, with the reason the tenant maps to non-payment, and the refund rule of that reason. No grace rule in force: nothing changes from today (the arrears are left to staff), and readiness flags it for a tenant with the GENERAL feature.
- **Health grace:** on `ArrearsDetected` for a membership, the membership is in grace for `grace_days`; claims with a service date in grace are held (not declined) until the contribution is paid or grace ends. After `suspend_after_days`, the membership is suspended for arrears (the existing `SuspendMembership`, reason `ARREARS`), and held claims are declined with the scheme's arrears reason. A notice is sent; after `terminate_notice_days` with arrears still open, a Workflow task asks a person to terminate (never automatic).
- **Short-period refunds:** a cancellation reason with `refund_basis = SHORT_PERIOD` names its scale. The refund is the premium paid less the scale's retained percent of the annual premium for the days in force (the first band covering them), never less than zero, less the reason's fee. The worked example and the leap-year case are tests. A `SHORT_PERIOD` reason with no scale stays refused, as now.

### 4.2 The renewal review (F015, F016, F017, F019; batch 4.2)
For a product whose renewal rule has `review = true`, the daily job opens a `Renewal` `review_days_before_invitation` before the invitation date. The review runs:
1. **Eligibility (F015):** the product version is still on sale (or has a successor the tenant mapped); the policyholder's due diligence allows business (Wave 3 gates); the policy is not under a fraud case; the tenant's eligibility rule set passes (for example, no unpaid premium, the insured item's age under the product's limit). A failed check proposes non-renewal (§4.3) and opens a Workflow task; nothing is sent to the customer automatically.
2. **Changes in the insured risk (F016):** endorsements in the period, insured items changed or added, and their attribute changes against the original risk; the tenant's referral rule set decides which changes refer.
3. **Claims history (F017):** claims on the policy (and, if the tenant chooses, on the policyholder's other General policies) in the last `claims_lookback_years`: count, paid, outstanding reserve, and the loss ratio against earned premium. These feed the claims factor (§4.3) and the referral rules.
4. **Re-rating (F018):** a product with `re_rating` is rated as a renewal quote: the same rating as new business, with the policy's current risks, the renewal period, and the claims experience factor. Without `re_rating`, the premium stays as now (current behaviour, kept).
5. **Renewal underwriting (F019):** if a referral rule fires, the renewal goes to the underwriting queue as a referral, as a quote does today: the underwriter accepts (optionally with changed terms or loadings), or declines, which proposes non-renewal. The same underwriting authority limits apply.

The review's outcome is the offered terms: premium, excess, any changed cover. The invitation then carries them.

### 4.3 Offered terms, insurer non-renewal and retention (F018, F025, F026; batches 4.2 and 4.3)
- **Claims history and terms (OQ-131):** the claims factor multiplies the re-rated premium; the referral rules decide when a person must look. The factor table is the tenant's. Recommended default: no factor (1.0) until a tenant configures one, so nothing changes silently.
- **Insurer non-renewal (F025):** a person proposes it with a reason from `cfg_non_renewal_reasons`. If the reason `requires_approval`, it goes to a second person (Workflow, `GENERAL_NON_RENEWAL`). Once decided, the notice is sent at least the reason's `notice_days` before the period end; a decision too late for that notice is refused (422 `NON_RENEWAL_NOTICE_TOO_SHORT`), and the policy is renewed or the notice period extended, per OQ-132. At the period end the policy expires as `NotRenewedByInsurer` (a new reason on the existing transition).
- **Retention (F026):** a retention case opens when the policyholder asks to cancel, declines a renewal invitation, or the review flags a lapse risk (an invitation not accepted N days before the end). Staff record offers from `cfg_retention_offers`, within their authority. An accepted offer becomes an endorsement (mid-term) or changes the renewal terms. A cancellation request is processed as asked (§1); the retention case only records whether the customer changed their mind.

### 4.4 Acceptance (F022; batch 4.3)
The renewal's status: `REVIEWING` → `OFFERED` → `ACCEPTED` → `RENEWED`, or → `DECLINED_BY_CUSTOMER`, `NOT_RENEWED_BY_INSURER`, `LAPSED` (no acceptance by the end). How a renewal is accepted is the product's `acceptance` (OQ-130):
- `AUTOMATIC`: renews at the end unless the customer declines (today's `auto_renew = true`);
- `ON_ACCEPTANCE`: renews only once the customer (portal or broker) or staff record acceptance;
- `ON_PAYMENT`: renews when the first renewal premium is paid, within grace after the end date.

Acceptance is recorded with who accepted and through which channel. A renewal with changed terms (premium up by more than the tenant's threshold, or cover reduced) is `ON_ACCEPTANCE` whatever the product says, unless OQ-130 decides otherwise.

### 4.5 Health scheme renewals (batch 4.2)
A Health scheme's renewal sends its notice `notice_days` before the scheme year ends, with the next year's contribution table that the tenant loads. Experience or blended re-rating (`re_rating_basis`) needs the actuarial basis (OQ-052) and is not built: a scheme set to `EXPERIENCE` or `BLENDED` is listed on readiness as needing a manual contribution table.

### 4.6 Portals
- A policyholder sees their renewal invitation with the offered terms, and can accept or decline it (`ON_ACCEPTANCE`), as can the broker servicing the policy.
- Neither sees the review, the claims factor or the referral. A declined renewal says only that the policy will not be renewed, with the insurer's customer wording.

### 4.7 Reporting (F027; batch 4.3)
Analytics figures, by product and month of renewal date, following the rules of `dashboard-figures.md`:
- renewals due, invited, accepted, renewed, declined by customer, not renewed by insurer, lapsed;
- the renewal rate (renewed over due) and the retention rate (retained over retention cases closed);
- the average premium change at renewal;
- retention offers made and accepted, by kind.

### 4.8 B-001: broker of record change (batch 4.3)
A General endorsement type `INTERMEDIARY` moves the policy to another intermediary from its effective date. Access moves with it at that date (external-user access §2). Decisions OQ-134 to OQ-136.

## 5. What it gates
- A policy in General grace keeps its cover if the product says so; claims with a loss date in grace are processed normally once the arrears are paid, and held until then.
- A Health membership in grace holds claims; one suspended for arrears declines them.
- A renewal is not issued while its review is open or referred, or while the policyholder's due diligence does not allow it.

## 6. Readiness
- GENERAL tenants: a grace rule for each product and frequency on sale; a renewal rule for each product; a scale for each `SHORT_PERIOD` reason.
- HEALTH tenants: a contribution grace rule.

## 7. API (summary, when built)
- `GET /general/renewals` (work list), `GET /general/renewals/{id}`; `POST .../review` (re-run), `POST .../offer`, `POST .../accept`, `POST .../decline`; `POST /general/policies/{id}/non-renewal` (Workflow approval when required).
- `GET /general/retention-cases`, `POST /general/retention-cases`, `POST .../offers`, `POST .../outcome`.
- Portal: `GET /portal/renewals`, `POST /portal/renewals/{id}/accept|decline`.
- Events: `general.RenewalReviewed`, `RenewalOffered`, `RenewalAccepted`, `RenewalDeclined`, `PolicyNotRenewed`, `PolicyEnteredGrace`, `GraceEnded`; `health.MembershipEnteredGrace`, `MembershipSuspendedForArrears`.
- Functions and their permissions per the function catalogue; the review, offer and non-renewal are separate functions so a tenant can split them across roles.

## 8. Decisions
| OQ | Decision | Options | Recommendation |
|---|---|---|---|
| **OQ-130** | Which products renew by invitation and which automatically | (a) per product, `AUTOMATIC` / `ON_ACCEPTANCE` / `ON_PAYMENT`, tenant-set; (b) everything automatic as today | **(a)**, with `AUTOMATIC` as the default so nothing changes until a tenant chooses; any renewal with worse terms beyond a tenant threshold (default: premium up more than 15%, or cover reduced) needs acceptance |
| **OQ-131** | How claims history affects terms | (a) a claims factor table per product plus referral rules; (b) referral only, a person sets terms; (c) none in this wave | **(a)**, with factor 1.0 by default; referral rules for any claim over a tenant amount |
| **OQ-132** | Notice periods, and a late non-renewal | Notice per non-renewal reason, default 31 days; a late decision either (a) is refused and the policy renewed once more, or (b) extends cover until the notice runs out, pro rata | **31 days default; (b)**, which keeps the customer covered without forcing a full renewal |
| **OQ-133** | The short-period scale's source | (a) a tenant table of bands (§3.1); (b) a fixed industry table in platform static data that tenants copy; (c) a formula | **(a)**, with no platform default: a tenant must load its own scale before it can use `SHORT_PERIOD` (no invented numbers) |
| **OQ-134** | B-001: does a broker change need underwriting or a second person? | (a) a second person always; (b) only when the new intermediary is in another branch or has lower authority; (c) neither | **(a)**: a change of broker of record affects commission and access, so maker-checker |
| **OQ-135** | B-001: commission across the effective date | (a) old broker earns to the effective date, new from it, pro rata; (b) new broker from the next renewal | **(a)** |
| **OQ-136** | B-001: a future-dated change, before it takes effect | (a) only the current broker sees the policy until the date; (b) both see it | **(a)**, with the new broker told the change is pending |
| | **General grace defaults** | Per product and frequency | No platform default; readiness flags missing rules. Recommended starting values for compliance to confirm: 15 days for monthly, 30 for others, cover during grace |
| | **Health grace defaults** | | For compliance to confirm with the scheme rules: grace 3 days, suspension after 30, termination notice 30 days (a person terminates) |

## 9. How it is built
- **4.1:** General grace (V-migration, `ArrearsDetected` handler in General, policy `grace_until`, cancellation for non-payment), Health grace and suspension, the short-period scale and its refund. Readiness checks.
- **4.2:** the `Renewal` aggregate, the review job, eligibility and referral through Rules, claims history through Claims Framework, re-rating as a renewal quote (with the missing test for `renewalOfPolicyId`), renewal referrals in underwriting, Health scheme notice.
- **4.3:** acceptance (staff, broker and portal), insurer non-renewal with Workflow approval and notice, retention cases and offers, Analytics figures, B-001.
- Each item has its own commit and test; batches merge under the standing approval.

## 10. Tests (when built)
- Grace: a payment in grace keeps the policy; grace ending cancels from the right day; Health claims held in grace, declined after suspension; each on the dated CI clocks (28th, 30th, 31st, 29 February, 23:30 UTC).
- Short period: each band edge, a leap year, the fee, never below zero; refused without a scale.
- Review: each eligibility failure, a risk change that refers, a claims count that refers, a factor applied, a referral accepted with loading and declined.
- Acceptance: each acceptance mode, a worse-terms renewal needing acceptance, a lapse at the end.
- Non-renewal: notice sent at the right time; a late decision per OQ-132; approval when required.
- Retention: a cancellation processed as asked with an offer alongside; an accepted offer becomes an endorsement.
- B-001: access moves at the effective date; commission split; future-dated visibility per OQ-136.
- End to end: a General policy from invitation through review, re-rating, acceptance, renewal and billing; a Health scheme notice.
