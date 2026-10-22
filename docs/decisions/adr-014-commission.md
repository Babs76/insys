# ADR-014: Commission: plan assignment, accrual, clawback and statements

- **Status:** Proposed, pending business sign-off. The defaults of OQ-062, accepted to build on 23 October 2026. Each is configuration (V0021), not code.
- **Date:** 23 October 2026
- **Requirements:** DST-001 to DST-004; processes "General quote to cash" (AccrueCommission) and "Missed premium to lapse" (ClawBack)

## Context
The specs define intermediaries, permissions, plans and statements, but not how a plan is chosen, what it is a percentage of, when commission is payable, what a clawback takes back, or who is paid (OQ-062).

## Decision
- **Plan:** `cfg_intermediary_product_permissions.plan_code` (V0021) names the plan an intermediary earns on a product. No plan: no commission (logged).
- **Whose commission:** the policy's intermediary, read from General or Life through `IPolicyDistribution` (a public query each policy domain implements). Health memberships earn none.
- **Accrual (AccrueCommission)** on InvoiceIssued, at the plan version in force on the invoice's issue date (DST-002), recording the plan version and the invoice (DST-003):
  - PREMIUM: the invoice's net amount × rate %, rounded half away from zero to cents;
  - FIXED: the rate as an amount per invoice;
  - POLICY_COUNT: the rate as an amount on the policy's first invoice only.
- **Payable (MarkPayable)** when the invoice is paid in full (InvoiceSettled). *Deviation:* domain-model.json names PaymentAllocated; a partial allocation does not make commission payable.
- **Clawback** on GeneralPolicyCancelled and LifePolicyLapsed, by the plan's `clawback_rule` (V0021: NONE or UNEARNED, the default): each entitlement on the policy whose invoice falls due on or after the cancellation or lapse date is clawed back in full. A clawback always references its entitlement. Only Accrued and Payable entitlements are clawed back: domain-model.json gives Paid no transition.
- **Statements (PrepareStatement)** per intermediary and month: the payable entitlements not yet on a statement, less the clawbacks to recover. Recovery is needed when an entitlement was clawed back after its statement was approved but before it was paid. A statement and the entitlements it takes change in one transaction, so no entitlement is on two statements (the same deviation as ADR-012's payment runs).
  - A second person approves it in Workflow (COMMISSION_STATEMENT, within delegation authority; DST-004). The approval subject covers the net amount, the payee and the bank account.
  - A rejection returns it to draft, to be prepared again. An entitlement clawed back while its statement is a draft leaves the draft: nothing was paid.
  - **Payee:** `mst_intermediary_register.party_id` (V0021); paid to that party's verified bank account (ADR-013) through a payout batch (purpose COMMISSION). The payout's success settles the statement and its entitlements ("Payout settled", "Statement settled").
- **Postings:** COMMISSION_ACCRUED, COMMISSION_CLAWBACK and COMMISSION_PAID through PostBusinessEvent.
- **Intermediary lifecycle:** the register is Tier 1 static data. Suspend, Reinstate and Terminate are new register versions through static data maker-checker. A new guard (`IStaticChangeGuard`) refuses versions that break the lifecycle. An approved suspension raises IntermediarySuspended. General quotes and Life proposals through a suspended or terminated intermediary, or one without a permission in force for the product, are refused (DST-001).

## Still open
- Tax on commission (statements carry zero); overriding commission on the hierarchy; collecting a statement whose clawbacks exceed its commission.

## Consequences
- Every default is static data, changed through maker-checker (ADR-003). Engineering cases in `CommissionTests` and `EnterpriseProcessTests` stand until the business signs off.
