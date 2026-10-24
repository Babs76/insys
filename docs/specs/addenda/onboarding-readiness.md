# Addendum: onboarding readiness checks (TEN-008)

**Proposed 24 October 2026, for sign-off. Not built.**

- **Basis:**
  - TEN-008: "Tenant onboarding, suspension, reactivation, and termination MUST be governed by defined workflows."
  - The onboarding steps, in order: `PLATFORM_REFERENCE_DATA`, `TENANT_REGISTRATION`, `ORGANISATION_AND_ACCESS`, `FINANCIAL_FOUNDATIONS`, `SHARED_OPERATIONAL_SETUP`, `PAYMENTS_AND_DISTRIBUTION`, `DOMAIN_REFERENCE_DATA`, `PRODUCT_FACTORY`, `REINSURANCE`, `INTEGRATION_AND_REPORTING`, `END_TO_END_TESTS`.
- **The gap:**
  - `CompleteOnboardingStep` checks only the order. The one exception is `ORGANISATION_AND_ACCESS`, which needs the first administrator.
  - So a tenant can complete every step and be activated with nothing configured. Its first policy then fails or parks downstream (ADR-022).

## The proposal

### 1. A readiness check per step
- Completing a step runs that step's checks in the tenant, on its business date (ADR-025).
- Each check is a named item of configuration that must be **in force** (static data in force, or a transactional record in the right state).
- If any check fails, the step is refused with **422 `ONBOARDING_STEP_NOT_READY`**, listing every failing check with its code and what is missing. Nothing changes.
- `GET /api/v1/tenancy/tenants/{id}/readiness` lists every step's checks and their results without completing anything, so the operator can see what is left.

### 2. The checks
Static data is read through each module's own query interface (Contracts), never across schemas.

| Step | Check code | Must be in force in the tenant |
|---|---|---|
| `PLATFORM_REFERENCE_DATA` | `GLOBAL_REFERENCE_DATA` | Global currencies, countries, languages and time zones (the Static data module's G tables) have at least one row each |
| `TENANT_REGISTRATION` | `TIME_ZONE` | A time zone: given at registration or in locale defaults (ADR-025) |
| | `LOCALE_DEFAULTS` | `tenant.cfg_locale_defaults`: base currency, languages, default language |
| `ORGANISATION_AND_ACCESS` | `FIRST_ADMINISTRATOR` | An active tenant administrator (as today, OQ-067) |
| | `ROLES` | `iam.cfg_roles_and_permission_sets`: at least one role besides `TENANT_ADMIN` |
| | `ORGANISATION_STRUCTURE` | `tenant.mst_organisation_structure`: a root legal entity (needed for access scopes, F103) |
| | `AUTHENTICATION_POLICY` | `iam.cfg_authentication_and_session_policies`, tenant or global |
| `FINANCIAL_FOUNDATIONS` | `CHART_OF_ACCOUNTS` | `finance.mst_chart_of_accounts`: at least one account |
| | `POSTING_RULES` | `finance.cfg_posting_rules`: a rule for every transaction type in `finance.ref_transaction_types` the tenant's products raise (premium due, payment received, refund paid, claim reserve, claim paid) |
| | `BASE_CURRENCY` | The locale defaults' base currency, with exchange rates for any other currency its products use |
| | `OPEN_PERIOD` | An accounting period open on the business date (`finance.accounting_period`) |
| `SHARED_OPERATIONAL_SETUP` | `BUSINESS_CALENDAR` | `workflow.ref_business_calendars`, tenant or global |
| | `WORK_QUEUES` | `workflow.cfg_task_types_and_work_queues` |
| | `APPROVAL_AUTHORITY` | `workflow.cfg_delegation_and_approval_authority` for every maker-checker approval type in use (refunds, payout batches, claim instructions, endorsements, settlements) |
| | `DOCUMENT_TEMPLATES` | `document.cfg_document_templates`: at least one in force in the default language |
| `PAYMENTS_AND_DISTRIBUTION` | `COLLECTION_METHODS` | `billing.ref_collection_methods`, `billing.ref_billing_frequencies`, `billing.cfg_document_numbering_schemes`, `billing.cfg_arrears_ageing_buckets` |
| | `PAYMENT_METHODS` | `payment.ref_payment_methods`: a collect and a pay method in the base currency |
| | `PAYMENT_PROVIDER` | `payment.mst_payment_provider_and_bank_register` supporting those methods, with `payment.ref_payment_status_mappings`, and a callback endpoint issued (ADR-023) |
| | `TENANT_BANK_ACCOUNT` | `payment.mst_tenant_bank_accounts` |
| `DOMAIN_REFERENCE_DATA` | `CLAIMS_SETUP` | `claims.cfg_claim_numbering_schemes` for each domain the tenant sells in, and `claims.ref_reserve_types` |
| | `DOMAIN_CODES` | Each domain's own reference tables the tenant needs, for example Life claim types and benefit types; Health plans; General cancellation reasons |
| `PRODUCT_FACTORY` | `PRODUCT_PUBLISHED` | At least one product version published and available (`product.cfg_product_definitions_and_versions`, `tenant.cfg_tenant_product_availability`) |
| | `PRODUCT_TEMPLATES` | Every published product has a template assigned for each document type it issues (`product.cfg_product_template_assignments`) |
| | `RATING` | Every published product's rating tables are in force and its rating test cases pass |
| `REINSURANCE` | `TREATIES` | Treaties and cession rules for the tenant's products, **or** the operator records "no reinsurance" with a reason |
| `INTEGRATION_AND_REPORTING` | `GL_EXPORT_LAYOUT` | `finance.cfg_gl_export_layouts` (F055) |
| | `WEBHOOKS_OR_NONE` | Webhook subscriptions, **or** "none" recorded with a reason |
| `END_TO_END_TESTS` | `SMOKE_RUN` | A recorded end-to-end run in the tenant: quote, policy, invoice, collection, journal and claim registration. Proposed as a step the operator records, with its reference, until an automated run exists |

### 3. Activation
`ActivateTenant` stays refused until every step is complete (as today). With these checks, that now means the configuration exists, not only that the steps were ticked.

### 4. What does not change
- The step order and the API to complete a step.
- A tenant already active is not re-checked. The readiness view still reports its gaps, for repair.

## Open points for sign-off
- **Exact minimums.** For example, `POSTING_RULES` lists the transaction types a tenant's products raise. Whether every transaction type in the catalogue must have a rule, or only those the products use, is proposed here as "only those used".
- **Which steps may be skipped with a recorded reason:** proposed for `REINSURANCE` and webhooks only.
- **Whether `END_TO_END_TESTS` should be automated** before it is required.

## Consequences
- **Tenancy:** a readiness service that asks each module through new query interfaces in its Contracts ("is X in force for tenant T on day D"); the readiness endpoint; the new error code.
- **Each module:** one small query per check.
- **Tests:** a step refused while its configuration is missing, and completed once it exists.
