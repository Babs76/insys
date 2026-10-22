# ADR-007: The reference rating engine

- **Status:** Accepted (Phase 2; the user chose "Reference engines")
- **Date:** 21 October 2026
- **Requirements:** SRS §7.3, PRD-005, PRD-008, LIF-002

## Context
The SRS and the static data catalogue define the rating configuration: factor definitions with bands, rating tables, premium parameters, discount and loading schedules, premium bounds, taxes and levies, rounding rules and a test case library. They do not define the algorithm that combines them. Rating must still be deterministic and reproducible (LIF-002) and record the versions it used (PRD-008).

## Decision
One documented, deterministic algorithm, `RatingEngine` in `Insys.Rating.Domain`, over the configuration in force on the rating date (today unless the request names one). Each step is a result line holding the premium after that step:

1. **Tables apply to a product version when it has a premium parameter with the table's code.** Parameters are matched to the version by `product_version = "{code} v{version}"`, the catalogue's sample format.
2. **Exposure:** `sum_insured`, `sum_assured` or `exposure` from the inputs. It is needed only when a table applies.
3. **Each table multiplies the premium** by its value for the factor values, in table code order:
   - Dimensions are factor codes.
   - A factor's value comes from its `source_attribute` in the inputs, banded when the factor has bands ("18-24", "60+").
   - Values are stored as `[{"key": [...], "value": n}]`. LINEAR interpolation works for one numeric dimension.
4. **Other parameters, in code order:** FACTOR parameters multiply; PERCENT parameters load by that percentage.
5. **Discounts and loadings named in the input `adjustments`,** in stacking order:
   - PERCENT applies to the running premium; AMOUNT is added or taken away.
   - A cap limits the total of that type to that percentage of the premium before adjustments.
6. **Fees and bounds:** AMOUNT parameters (fees) are added, then the premium bounds for the product, currency and frequency apply.
7. **Rounding and tax:**
   - Rounding at stage FINAL_PREMIUM; EACH_FACTOR rounding, if configured, applies after every step.
   - Then each tax for the input `jurisdiction`, rounded by its own rule, on GROSS_PREMIUM or NET_PREMIUM.

**Inputs:** they are flattened to dotted names by the application, so the domain sees no JSON.

**Errors, never a silent zero:**
- a missing input, or a combination with no table value;
- an unknown adjustment, or a missing final rounding rule;
- a levy on commission.

**Rating version:** the highest version among the tables used; 0 when none was used.

**Recording:**
- A calculation records its inputs, as-at date, product version, rating version and breakdown in `rating.rating_request` and `rating.rating_result_line`.
- A simulation (`IPremiumSimulator`, Rating.Contracts) rates the same way and records nothing. Product Factory uses it to run the test case library (PRD-005).

**Cache:** configuration is cached per instance (ADR-006) for 10 minutes, keyed by tenant, product version, currency and day. The tenant's entries are dropped on `ProductVersionPublished` and on any rating static data activation.

**Step order (Phase 2 review, item 5):** steps 4 to 6 can be put in another order per product version, with `STEP_*` premium parameters (OQ-052). Tables always come first, rounding and tax last.

## Consequences
- The algorithm is a proposal. Actuaries must confirm the step order, the cap rule and the treatment of PERCENT parameters (OQ-052).
- Product-specific rating can replace the reference engine behind `IRatingConfigurationReader` and `RatingEngine` without changing the records.
