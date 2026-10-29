# Wave 2 batch 7: the front end's Life and Health requests

29 October 2026. Branch `wave2-7`, from `main` at `c2a5870` (batch 6). Under Ade's standing approval (29 October 2026): it merges once every item is built and tested, CI is green on the exact head, and nothing needs a decision.

## Scope, in build order

| # | Request | What gets built | Proof (tests) |
|---|---|---|---|
| 1 | BR-062, BR-063 loans | `GET /life/loans` (by policy and status, cursor), on the loans screen. `LoanView.currency` is the policy's currency. | Life process test: a loan is listed by policy and by status; its view and list carry the currency |
| 2 | BR-065 claim decision, cash value | `LifeClaimView` gains `approvalId`, `decidedBy`, `decidedAt`, `notifiedAt` and `reserveType`. `LifePolicyView` gains `cashValue` and `cashValueDate`: the latest `life.cash_value` row, which stays null until the actuarial basis (OQ-052) is defined. | The claim's approval and decision show on its view; the policy view has no cash value until one is stored, then shows that value |
| 3 | BR-074 underwriting cases | `GET /life/underwriting-cases` (by status, underwriter and decision pending, cursor), on the underwriting screen. | Listed by status and by pending decision |
| 4 | BR-075, BR-086 proposal and policy | `ProposalView.policyId` once converted; `LifePolicySummary.proposalId`; `LifeListPolicies` filters by `proposalId`; `LifeIssuePolicy` declares `x-eventual-effects` (the proposal reads Converted once the issue event is handled). | Issue a policy, then the proposal names it and the list finds it by proposal |
| 5 | BR-076 waiting decision | `CaseView` gains `requestedBy` (who proposed the decision) and `approvalId`. | A proposed decision shows its proposer and its approval |
| 6 | BR-066, BR-077, BR-078 Life contract | Life's code fields name their static data table (`x-code-list`) or list their values: claim type, cause, relationship, alteration, loan rate, reserve type, decision reason, decision terms, evidence type, frequency, collection method, scheme type. The decision reason's 20-character limit is stated. `x-eventual-effects` goes on capture (rating and eligibility), submit (the case opening and the underwriting rules) and issue. Rating's and Rules' subject types are listed. | Contract test over the named fields and operations |
| 7 | BR-067 Health schemes | `GET /health/schemes` (by status, cursor), on the schemes screen. | Listed by status |
| 8 | BR-068, BR-070 membership | `currency` on the membership, accumulator and preauthorisation views (the membership's). `MembershipView` gains its lifecycle events (type, date, reason), payer, billing account and currency. | Enrol, suspend and reinstate: the view shows the events, payer, account and currency |
| 9 | BR-069 search | `POST /health/memberships/search`: the member number goes in the body only. The same filters as the list, with the same function. | Found by member number from the body |
| 10 | BR-072 preauthorisation and codes | `PreauthorisationView` gains `clinicalReviewer` and `decidedBy`. Diagnosis codes are medical information (HLT-008): `GET /health/preauthorisations/{id}/clinical` gives them only to holders of the clinical review permission. `GET /health/eligibility-decisions` (by member number in a POST body, cursor) reads recorded decisions. Plan, option, dependant type, service type, collection method and sponsor codes name their tables. | Reviewer and decider shown; a non-clinical caller gets 403 on the clinical read, a reviewer gets the codes; eligibility decisions listed |
| 11 | BR-079 Health decisions and people | `MedicalClaimView`: `approvedBy`, `approvedAt`. `PaymentRunView`: `approvalId`, `approvedBy`, `releasedAt`, and each remittance's advice `documentId` (its `paymentId` leads to the payout batch; a run has none of its own). Utilisation and fraud cases: `openedAt`, `concludedAt`. | Claim approval and run release show their people and times |
| 12 | BR-080 domain model | `ProviderPaymentRun` Calculated → Rejected (`RejectRun`) in `domain-model.json`. | Domain model consistency test |
| 13 | BR-081 my authority | `GET /workflow/approvals/my-authority` also lists each module's own limits for the caller's roles (Life claims, by claim type), and the static data approval types the delegation table does not configure, which the approve permission decides. Each row says its `basis` (`DELEGATION`, `MODULE_LIMIT`, `PERMISSION`) and what it `appliesTo` (`claimType=DEATH`). | A Life claims limit and the static data fallback show for the role that holds them, and not for others |

## Not in this batch
- **BR-082, BR-083:** these wait for the actuarial basis (OQ-052).
- **BR-089, BR-090, BR-096, BR-097:** other modules, though BR-089 and BR-096 are group A. They are placed in the Waves 3–6 plan.

## How it runs
- Each item gets its own commit, its tracker row and its contract changes.
- Before hand-back: targeted tests, plus `OperatorTenantTests` and `FunctionAccessTests`. The full suite runs only when the machine is quiet.
