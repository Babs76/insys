# Addendum: Life policy values, the actuary's brief (OQ-052)

**For Ade's review (27 October 2026). Once approved, this becomes the actuary's brief.** Nothing here is built.

## The decision and where things stand
**Decided (Ade, 27 October 2026):** an actuary defines the basis per Life product for:
- cash value;
- surrender value;
- paid-up value;
- maturity value;
- with them, the policy loan limit and loan interest.

Until the basis is delivered and accepted, INSYS refuses these operations and **does not approximate**.

**What INSYS does today, on `main`:**

| Operation | Today |
|---|---|
| Surrender (`POST /life/policies/{id}/surrender`) | Refused: 409 `LIFE_VALUE_NOT_CONFIGURED` |
| Make paid-up (`POST /life/policies/{id}/paid-up`) | Refused: 409 `LIFE_VALUE_NOT_CONFIGURED` |
| Mature (`POST /life/policies/{id}/mature`) | Refused: 409 `LIFE_VALUE_NOT_CONFIGURED` |
| Issue a policy loan (`POST /life/policies/{id}/loans`) | Refused in practice: no policy has a cash value (`life.cash_value` is never written), so 422 `LOAN_EXCEEDS_CASH_VALUE`. Products without a loan rule get 422 `LOAN_NOT_SUPPORTED` (LIF-007). |
| Loan repayment, and offset against a benefit | Built (no value needed) |
| Loan interest accrual | Not built: `AccrueInterest` has no job, and `life.cfg_policy_loan_rates` holds a rate and compounding that nothing applies |
| Maturity case | Not built: the `MaturityCase` aggregate has none of its four commands, and no endpoints |

**Static data that exists but is not used for values:**
- `life.cfg_surrender_value_scales`: scale, product version, policy year, factor, formula reference.
- `life.cfg_loan_eligibility_limits`: product, maximum % of cash value, minimum duration.
- `life.cfg_policy_loan_rates`: rate code, rate, compounding.
- `life.ref_maturity_settlement_options`.

## 1. The extension point (proposal)
**One interface for every product, with the basis chosen by the product version.**

- **The calculator:** `IPolicyValueCalculator` computes one value for a policy as at a date. The value kind is `CASH`, `SURRENDER`, `PAID_UP` or `MATURITY`. It returns:
  - the amount;
  - the basis code and version used;
  - the inputs it read;
  - a trace, one line per step with the factor or table cell used and the amount (like rating's `rating_request_line`).
- **Loan rules use the same calculator:** the loan limit is computed from the `CASH` value it returns, and so is the interest.
- **A basis is static data, effective-dated and versioned, under maker-checker as Tier 1:** `life.cfg_value_bases`. It has three methods, all behind the one interface:
  - **`TABLE`:** value tables keyed by any of policy duration (years and months), age at entry or attained age, sex if the actuary needs it, term, and premium term. Each table has a value per unit of sum assured or of annual premium, and an interpolation rule between keys (none, linear by month, or the actuary's).
    - New table: `life.cfg_value_tables`.
    - `cfg_surrender_value_scales` becomes its surrender case, or is retired; your choice, see the decisions below.
  - **`FORMULA`:** a named formula from a closed list INSYS implements, for example a prospective reserve less a surrender penalty. The basis supplies the parameters: interest, mortality table reference, expense loadings, penalty scale.
  - **`CALCULATOR`:** a product-specific calculator in code, registered under a code, for a product neither of the other two can express. It still takes its parameters from the basis and still records its trace. It is used only when the actuary says so for that product.
- **Bound to the product version:** a product version names its value basis code (`value_basis_code`). A policy is valued on the basis version in force for its product version at the valuation date, unless the actuary says an in-force book keeps its basis at issue. That is one of the questions for the actuary below.
- **Every value is recorded with its basis version,** like a rating result: a new table, `life.value_calculation`, holds:
  - the policy;
  - the kind;
  - the as-at date;
  - the basis code and version;
  - the inputs (JSON);
  - the amount, the currency and the trace lines.

  Values a person sees (quotes), values paid (surrender, maturity), the paid-up sum assured, and cash values used for loans are all recorded calculations. `life.cash_value` is written from them, with the basis in its existing `scale_code` and `scale_version`.
- **Quote, then act:**
  - `GET` quote endpoints give a recorded value with a validity period.
  - Surrender, paid-up, maturity and loan issue act on a quote still valid. They are approved by a second person within the existing authority, as Life claims are.
  - Money goes out through Payments, with any loan balance offset first.
- **Until a product has a basis accepted,** its operations keep refusing with `LIFE_VALUE_NOT_CONFIGURED`. LIF-007's per-product enablement stays: a product without a value basis does not support the operations.

## 2. What the actuary delivers, per product
| Item | What we need |
|---|---|
| **Products and versions** | Which products and product versions have each value (cash, surrender, paid-up, maturity) and policy loans, and from which duration. |
| **Method** | Per value: `TABLE`, a `FORMULA` (name it, with every parameter), or a product calculator (specified step by step). |
| **Inputs** | Every input and its source: sum assured, premium and frequency, term, premium term, duration in force (and how part years count), age at entry or attained age, sex, rider benefits, indexation, bonuses if any. |
| **Table layouts** | Columns, keys and units, as files we can load (one table per value kind per basis version), with the interpolation rule between keys. |
| **Rounding** | Per value: at which step, to how many places, and the direction (half up, half even, down). Also the currency, if values can be in more than the product's. |
| **Premiums in arrears** | How unpaid premiums change each value: deducted with or without interest; whether a lapsed policy has a value and for how long; whether grace counts. |
| **Policy loans** | The loan limit as a share of which value, and its minimum duration. The interest rate basis (fixed, or by reference), compounding, when interest is charged, and what happens when the balance exceeds the value: lapse, notice or offset. How the balance is deducted on surrender, maturity or death. |
| **Paid-up** | How the paid-up sum assured is computed, the minimum paid-up value, and whether riders stop. |
| **Reinstatement** | Whether values continue from before the lapse, restart, or are recomputed. Whether arrears paid on reinstatement restore the value. |
| **Maturity** | The maturity benefit (sum assured, bonuses, fund value), the maturity date rule, the settlement options (`ref_maturity_settlement_options`) and any option-specific values, and the notice period before maturity. |
| **Basis changes** | Whether a new basis version applies to policies in force or only to new business, from when, and how a changed value is communicated. |
| **Acceptance test cases** | Per product and per value, worked cases INSYS must reproduce exactly. Each gives the inputs, the as-at date, every intermediate step and the result. Include at least: early duration, mid-term, the last year, in arrears, after reinstatement, with a loan outstanding, paid-up, and at maturity. These become automated tests, as the rating cases do (`docs/rating/test-cases.md`). |
| **Sign-off** | Who signs each basis version as approved for use, as the maker-checker on its static data. |

## 3. Maturity's missing endpoints, and the rest of the plan
**Maturity case** (`MaturityCase`: `IdentifyMaturity`, `NotifyPolicyholder`, `ChooseOption`, `ApproveMaturity`; none built):
- **A daily job** identifies policies reaching maturity within the notice period, and opens a maturity case per policy (`IdentifyMaturity`).
- `GET /life/maturity-cases?status=&dueFrom=&dueTo=` and `GET /life/maturity-cases/{id}`, scoped like policies (F103).
- `POST /life/maturity-cases/{id}/notify` (`NotifyPolicyholder`): the policyholder notice through Communications, with the quoted maturity value.
- `POST /life/maturity-cases/{id}/option` (`ChooseOption`): the settlement option, the payee and their verified bank account.
- `POST /life/maturity-cases/{id}/approve` (`ApproveMaturity`): a second person, within authority. Then the policy is Matured, any loan is offset, and the payment is instructed through Payments.
- The `/mature` route on the policy is replaced by the case.

**Elsewhere:**
- **Quotes:** `GET /life/policies/{id}/values?kind=&asOf=` (a recorded calculation).
- **Surrender and paid-up** act on a valid quote, with approval.
- **Loan interest:** a daily `AccrueInterest` job, and the loan limit from the recorded cash value.
- **New functions:** the maturity screen and its actions, the values screen, and approval types for surrender and maturity. The existing loan functions stay.

**Which wave (proposal, for your decision):** a Life values batch in the first wave that starts after the basis for at least one product is accepted. Its order:
1. The interface, the basis tables, the value calculation record and the quotes.
2. Surrender and paid-up.
3. The maturity case and loan interest.

It is built against the actuary's test cases, product by product. Nothing is built before the basis arrives, beyond the extension point, if you want that first.

## Decisions for Ade
1. The extension point as proposed: one interface, `TABLE`, `FORMULA` or `CALCULATOR`, every value recorded with its basis version.
2. Whether `cfg_surrender_value_scales` becomes the surrender case of the new value tables, or is retired.
3. Whether surrender and maturity each get an approval type of their own, or share one.
4. The wave for the Life values batch, and whether the extension point is built before the basis arrives.
