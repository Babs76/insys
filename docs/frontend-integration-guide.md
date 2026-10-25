# Front-end integration guide

Answers to questions the front end raises that the OpenAPI documents cannot answer by themselves (requests 53 to 56, from the UI prototype). The contract is `docs/api/`; changes are in `docs/api/contract-changes.md`. Where the specifications do not say, this guide says what INSYS does today and names the open question.

## 1. Rejecting an approval
- **How to reject:** `POST /api/v1/workflow/approvals/{id}/reject` with `{ "reason": "…" }` (required, up to 2,000 characters). Workflow keeps the reason on the approval (`decisionReason`) and tells the owning module, which moves its record (eventual, within one relay pass). Only Static Data passes the reason on; elsewhere, show it from the approval (OQ-113).
- **What rejection does, by approval type** (as built; "final" means the record cannot be resubmitted, so the requester starts a new one):

  | Approval type | Record: waiting → after rejection | What the requester can do next |
  |---|---|---|
  | `CLAIM_SETTLEMENT` (General settlement request) | Claim stays `UnderAssessment`; the pending settlement is cleared | Request settlement again (`POST /general/claims/{id}/settlement`) or decline the claim |
  | `LIFE_CLAIM` | Claim stays `InAssessment`; approved amount and payees cleared | Propose approval again, reassess, or decline |
  | `LIFE_UNDERWRITING` | Case stays `InAssessment`; the proposed decision cleared (§4) | Propose again, or refer |
  | `ENDORSEMENT` | `PendingApproval` → `Rejected` (final) | Draft a new endorsement |
  | `CLAIM_PAYMENT` (new instruction) | `PendingApproval` → `Rejected` (final) | Draft a new instruction, with a **new** `instructionRef` |
  | `CLAIM_PAYMENT` (new payee or bank details on retry or redirect) | Instruction keeps its status; the pending payee details are cleared | Retry or redirect again |
  | `REFUND` | `Requested` → `Rejected` (final) | Raise a new refund |
  | `COMMISSION_STATEMENT` | Statement back to `Draft` | Prepare the statement again (`POST /distribution/statements`); its lines stay on the draft until then |
  | `PERIOD_REOPEN` | Period stays `SoftClose`; the reopen request is cleared | Request reopening again, or close |
  | `PRODUCT_PUBLICATION` | Version back to `Draft`; its simulations are marked stale | Edit, validate, simulate and submit again |
  | `STATIC_DATA_T1`, `STATIC_DATA_T2` | Version back to `DRAFT` | Edit and submit again |
  | `PRIVILEGED_ROLE`, `EXTERNAL_SUBJECT_LINK`, `TENANT_ADMINISTRATOR` | `PendingApproval` → `Rejected` (final) | Request again |
  | `PARTY_MATCH_RESOLUTION` (portal registration) | Party back to `PENDING_REVIEW` | Resolve the match again |
  | `BANK_ACCOUNT_VERIFICATION` | Account `PendingVerification` → `Rejected` (final) | Add another account. **Defect:** the party's previous account in that currency was already superseded and is not restored (D-3) |
  | `PROVIDER_PAYMENT_RUN` | Run stays `Calculated` | **Defect:** nothing; the run and its claims are stuck (D-2) |
  | `PAYOUT_RELEASE` (payout batch) | Batch stays `Built` | **Defect:** nothing; the batch and its payments are stuck (D-1) |

- **The pattern:** where the domain model has a reject transition the record takes it; where it has none, the record **returns to where it was before it was submitted** (a claim to assessment, a statement or version to draft) and the request can be made again. The three defects below are the exceptions.
- **Defects found while answering this** (the front end should not offer the action until they are fixed; see the batch report):
  - **D-1** `PAYOUT_RELEASE`: Payments ignores the rejection; the batch stays `Built` and its payments cannot go into another batch.
  - **D-2** `PROVIDER_PAYMENT_RUN`: the run stays `Calculated` and its claims stay assigned to it.
  - **D-3** `BANK_ACCOUNT_VERIFICATION`: the superseded account is not restored.
  - **D-4** An approval that **expires** (14 days by default) or is withdrawn tells no module: the record stays waiting for good.
  - **D-5** A rejection whose subject or maker check fails in the module is only logged: the approval shows `REJECTED` while the record still waits.
- **Not approvals:** a Compliance regulatory return's approval and Health preauthorisation and claim decisions are the records' own transitions, not Workflow approvals.

## 2. Who maintains static data in a tenant
- **Scope decides who maintains a table; tier decides who approves a change** (ADR-003, `runbooks/static-data.md`).
  - **Scope `T`** (tenant, 180 tables): the tenant's own staff, through the generated endpoints `/api/v1/<module>/static/<table>`.
  - **Scope `G`** (global, 37 tables): only the platform operator, through `…/global`. A tenant reads them and cannot change them.
  - **Scope `GT`** (36 tables): the platform keeps global defaults (`…/global`); a tenant adds or overrides its own rows (plain path).
- **Tiers** (SDM-003): tier 1 (137 tables) always needs a second person, who also needs `STATIC_DATA_T1` authority once the tenant configures it; tier 2 (94) needs a second person unless the tenant switches that off (itself approved by a second person); tier 3 (22) is approved on submission.
- **Who, by function** (ADR-026): each catalogue module has three functions, `SD_{MODULE}_VIEW`, `SD_{MODULE}_MAINTAIN` (draft, edit, submit, activate, retire, import) and `SD_{MODULE}_APPROVE` (approve, reject). A role is granted the modules it looks after. The `x-function` of each static data operation names its module's function, and the menu shows the Configuration sub-domains a user holds.
- **Default roles:** INSYS seeds none beyond the first tenant administrator (`TENANT_ADMIN`, with the functions the two platform administrators agree, OQ-099). Which roles maintain which modules by default is not specified; the OFT-ZA seed (Workstream F, GAP-004) will define them. Until then a tenant administrator builds the roles. **OQ-111** proposes default role templates.

## 3. Authority bands
- **Where they are configured** (all tenant static data, tier 1 or 2, versioned and approved):

  | Table | What it limits | Used by |
  |---|---|---|
  | `workflow.cfg_delegation_and_approval_authority` (`role_code`, `transaction_type` = the approval type, `limit_value`) | Who may approve each approval type, and up to what amount | Workflow, for every approval (WFL-004): no row for the checker's roles is 403 `AUTHORITY_MISSING`; an amount above the highest of their limits is 403 `AUTHORITY_EXCEEDED`. Types without an amount (an endorsement, a period reopen) need a row; its limit is not used |
  | `rules.cfg_authority_matrices` (`role_code`, `product_code`, `max_sum_assured`, `decision_types`) | Whether a Life underwriter may decide a case alone | Life underwriting (§4) |
  | `life.cfg_claims_authority_limits` (`role`, `claim_type`, `limit_value`) | Whether a Life claims assessor may approve a claim alone | Life claims (above it, a second person approves in Workflow) |
  | `life.cfg_alteration_types.authority_role` | Who may apply a policy alteration | Life policy alterations |

- **Bands are by role and approval type, not by product or branch.** A checker sees and decides only approvals within their own F103 scope (their role assignment's products and branches), so scope narrows who can act; the amount does not vary by product.
- **Limits have no currency;** they are compared with the approval's amount in the tenant's currency. A multi-currency tenant would need limits per currency (**OQ-112**).
- **`delegation_allowed`** is stored but not yet used: nobody can delegate their authority today (OQ-112).
- **`payment.cfg_payment_release_limits`** exists in the schema but nothing reads it; payment release uses the Workflow table above (OQ-112).
- **Sample values for OFT Insurance South Africa** (Workstream F, not loaded yet). These are samples for the seed and for demonstrations, **for the business to confirm** (OQ-112), in rand:

  | Approval type | Role | Limit |
  |---|---|---|
  | `CLAIM_SETTLEMENT` (General) | `CLAIMS_MANAGER` | 250 000 |
  | | `HEAD_OF_CLAIMS` | 2 000 000 |
  | `LIFE_CLAIM` | `CLAIMS_MANAGER` | 500 000 |
  | | `HEAD_OF_CLAIMS` | 5 000 000 |
  | `CLAIM_PAYMENT` | `FINANCE_OFFICER` | 100 000 |
  | | `FINANCE_MANAGER` | 1 000 000 |
  | `REFUND` | `FINANCE_OFFICER` | 25 000 |
  | | `FINANCE_MANAGER` | 250 000 |
  | `PAYOUT_RELEASE`, `COMMISSION_STATEMENT` | `FINANCE_MANAGER` | 5 000 000 |
  | `PROVIDER_PAYMENT_RUN` | `FINANCE_MANAGER` | 10 000 000 |
  | `LIFE_UNDERWRITING` (sum assured) | `SENIOR_UNDERWRITER` | 5 000 000 |
  | | `CHIEF_UNDERWRITER` | 20 000 000 |
  | `ENDORSEMENT` | `SENIOR_UNDERWRITER` | (no amount) |
  | `PERIOD_REOPEN` | `FINANCE_MANAGER` | (no amount) |
  | `PRIVILEGED_ROLE`, `EXTERNAL_SUBJECT_LINK`, `STATIC_DATA_T1` | `TENANT_ADMIN` | (no amount) |

  Underwriting alone (`rules.cfg_authority_matrices`): `UNDERWRITER` up to 1 000 000 sum assured, standard decisions only; `SENIOR_UNDERWRITER` up to 5 000 000, every decision type. Life claims alone (`life.cfg_claims_authority_limits`): `CLAIMS_ASSESSOR` up to 100 000 per claim type; `CLAIMS_MANAGER` up to 500 000.

## 4. A Life underwriting decision above the underwriter's authority
- **How it is decided** (`POST /life/underwriting-cases/{id}/decision`): the underwriter decides alone when the decision matches the underwriting rules' outcome and their roles' authority matrix covers the product, the total sum assured and every decision code. Otherwise (an override of the rules, or above authority) the decision is **proposed**, and a second person approves it in Workflow (approval type `LIFE_UNDERWRITING`, amount the sum assured).
- **How it is held while it waits:** the case stays `InAssessment`, and the case view says **`decisionPending: true`**, with the proposed outcome, decision code, reason and terms, and who proposed it. The domain model has no state for it; INSYS does not add one, because the case is still being assessed until a second person agrees.
  - While a decision is pending, the case refuses another decision, a referral and a newly recorded rule outcome (409 `UNDERWRITING_DECISION_PENDING`).
  - The approval names the case (`businessRefType` `LIFE_UNDERWRITING_CASE`, `businessRefId` the case ID), so the Inbox can link to it.
- **When it is approved:** the case becomes `Decided`, with the checker as decider and their authority role; the proposal continues.
- **When it is rejected:** the proposed decision is cleared (`decisionPending: false`) and the case stays `InAssessment`. The underwriter can gather more evidence, refer the case, or propose a decision again. The rejection's reason is on the approval (`decisionReason`).
- **If the approval expires**, the case stays pending today (D-4 in §1).
