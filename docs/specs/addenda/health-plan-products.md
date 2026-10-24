# Addendum: linking Health plans to Product Factory products

**Proposed 24 October 2026, for sign-off. Not built.** Answers OQ-080.

- **Basis:**
  - SRS-003 (access by product and organisation) and `addenda/access-scopes.md`;
  - the Product Factory (PRD-001 onwards), whose `product.cfg_product_definitions_and_versions.domain` already allows `HEALTH`;
  - OQ-080 (decided 24 October 2026: keep the fail-safe meanwhile, and propose this).
- **Today:**
  - A membership is on a plan and option (`health.cfg_plans_and_options`: `plan_code`, `option_code`, `benefit_schedule`, `network`). Nothing links a plan to a product, and Health never asks the Product Factory whether anything is on sale.
  - So a product scope reaches no Health record, and the shared catalogue, product availability per tenant and product templates do not cover Health.

## The proposal

### 1. Every plan option names its product
- **New field:** `product_code` on `health.cfg_plans_and_options`, by migration, with the static data catalogue updated. The product must be one of domain `HEALTH`.
- **One product per plan:** every option of a plan names the same product. Options are tiers of one product, not separate products.
- **Checked on submit** (a static record validator, as for free cover bands):
  - the product exists with domain `HEALTH`;
  - all of the plan's options name the same product.
- **Required** for a new version of a plan option. Existing versions keep no product until a new version gives one (§4).

### 2. What the Product Factory holds for a Health product
- **What it holds:**
  - the definition, versions and lifecycle (draft to published);
  - availability per tenant and channel;
  - product templates (membership certificate, benefit schedule letter);
  - the product's benefit categories as its `coverages`.
- **What stays in Health's own static data:** contributions (`health.cfg_contribution_rules`), benefit limits, tariffs, networks and waiting periods. A Health product has no rating tables, so publishing it does not need rating test cases.

### 3. Enrolment and plan changes
- **Enrolment:** the plan option's product must be on sale to the tenant on the start date (the Product Factory's sellable products, as Life and General use). The membership records the product code and version. Otherwise it is refused with `PRODUCT_NOT_AVAILABLE` (existing code).
- **Plan change:** the new plan's product must be on sale on the effective date. A change to a plan of another product changes the membership's product, and the caller's scope must cover the new product too (F103 creation rule).
- **Following the membership:** preauthorisations, medical claims, cases, Billing schedules and approvals take the membership's product, as they already take its node.

### 4. Existing data
- **Migration:** adds `product_code` to `health.membership`, `health.cfg_plans_and_options`, and Health's Billing schedules and invoices (already there, empty for Health).
- **Backfill:** a membership whose plan option has a product takes it.
- **Until a plan has a product:** its memberships keep no product, and product-scoped users still see none of them (the fail-safe). `/health` lists, per tenant, the plans in force with no product, until there are none.
- **Onboarding readiness** (if that addendum is signed off): `DOMAIN_REFERENCE_DATA` checks that every Health plan in force names a published Health product.

### 5. What changes for users
- **Scoped users:** a user scoped to a Health product sees that product's memberships, claims and contributions. A user scoped to other products still sees no Health records.
- **The front end:** the plan option form gains the product; the membership view shows it. Nothing else changes.

## Tests (when built)
- Submitting a plan option whose product is missing, not `HEALTH`, or differs from the plan's other options is refused.
- Enrolling on a plan whose product is not on sale is refused with `PRODUCT_NOT_AVAILABLE`.
- A membership and its claims, preauthorisations and Billing schedule carry the plan's product.
- A user scoped to that product sees them; a user scoped to another product does not; a user scoped to a node sees them by node as today.
- A plan with no product: its memberships stay hidden from product-scoped users, and `/health` reports the plan.

## Open points for sign-off
- **One product per plan, options as tiers:** proposed here. The alternative is one product per option.
- **Re-pricing:** whether a new product version should pin contribution rules, as Life pins rating. Proposed: no, contributions stay in Health's static data by scheme and category.
