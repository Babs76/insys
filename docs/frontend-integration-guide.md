# Front-end integration guide

Answers to questions the front end raises that the OpenAPI documents cannot answer by themselves (requests 53 to 56, from the UI prototype). The contract is `docs/api/`; changes are in `docs/api/contract-changes.md`. Where the specifications do not say, this guide says what INSYS does today and names the open question.

## 1. How an approval ends, and what happens to its record
**The rule** (signed off 25 October 2026): every way an approval can end (approved, rejected, expired, withdrawn) leaves its record in a defined state; nothing waits for good.

- **Four endings:**
  - **Approved** by a checker within authority (`POST /api/v1/workflow/approvals/{id}/approve`).
  - **Rejected** by a checker, with a reason (`…/reject` `{ "reason" }`).
  - **Expired:** no decision within the type's time limit (`workflow/static/approval-expiry`, an ISO 8601 duration per approval type; 14 days when none is in force). The requester gets a follow-up task in their work list (`GET /workflow/tasks?assigneeId=`, process `APPROVAL_FOLLOW_UP`).
  - **Withdrawn** by the requester, with a reason (`…/withdraw` `{ "reason" }`, If-Match; only the person who asked: 403 `APPROVAL_NOT_REQUESTER`), or by the owning module when what it was for changed (the requester then gets the follow-up task).
- **Rejected, expired and withdrawn are handled alike** by the record's module (eventual, within one relay pass). The record shows the latest of them itself as **`latestRejection`** `{ending, approvalType, by, at, reason}` (`by` is the checker; null when it expired or was withdrawn).
- **If a module cannot apply an ending** (the record changed in a way that refuses it), the delivery parks under ADR-022 with the code `APPROVAL_ENDING_NOT_APPLIED` and the approval type and ID as its item. It shows in the operations view and in `/health`, is escalated on the tenant's schedule and retried; it is never only logged.

| Approval type | Record while waiting | After approval | After rejection, expiry or withdrawal | What the requester can do next |
|---|---|---|---|---|
| `CLAIM_SETTLEMENT` (General) | Claim `UnderAssessment`, settlement pending | Settlement approved; payment follows | Claim stays `UnderAssessment`, settlement cleared | Request settlement again, or decline |
| `LIFE_CLAIM` | Claim `InAssessment`, `approvalPending` | Claim approved | Claim stays `InAssessment`, approval and payees cleared | Propose approval again, reassess, or decline |
| `LIFE_UNDERWRITING` | Case `InAssessment`, `decisionPending` | Case `Decided` | Case stays `InAssessment`, proposed decision cleared (§4) | Propose again, or refer |
| `ENDORSEMENT` | `PendingApproval` | `Approved`, then applied | `Rejected` (final) | Draft a new endorsement |
| `CLAIM_PAYMENT` (new instruction) | `PendingApproval` | `Approved`; the payment is created | `Rejected` (final) | Draft a new instruction with a new `instructionRef` |
| `CLAIM_PAYMENT` (payee change on retry or redirect) | Instruction keeps its status, new payee pending | New payee applied | New payee cleared | Retry or redirect again |
| `REFUND` | `Requested` | `Approved`; paid | `Rejected` (final) | Raise a new refund |
| `COMMISSION_STATEMENT` | `Draft`, submitted | `Approved`; paid out | `Draft` again; its entitlements and recoveries are released for the next statement | Prepare a statement again |
| `PERIOD_REOPEN` | `SoftClose`, reopen requested | Reopened | `SoftClose`, request cleared | Request again, or close |
| `PRODUCT_PUBLICATION` | Version `Submitted` | Published | Version `Draft`, simulations stale | Edit, validate, simulate, submit again |
| `STATIC_DATA_T1`, `STATIC_DATA_T2` | Version `PENDING_APPROVAL` | `APPROVED` | Version `DRAFT`, change `Rejected` | Edit and submit again |
| `PRIVILEGED_ROLE`, `EXTERNAL_SUBJECT_LINK`, `TENANT_ADMINISTRATOR` | `PendingApproval` | Active or provisioned | `Rejected` (final) | Request again |
| `PARTY_MATCH_RESOLUTION` | Match proposed | Applied | Party back to `PENDING_REVIEW` | Resolve the match again |
| `BANK_ACCOUNT_VERIFICATION` | New account `PendingVerification`; **the previous account stays active** | New account `Active`, previous `Superseded` | New account `Rejected`; previous stays `Active` | Add another account |
| `PROVIDER_PAYMENT_RUN` | Run `Calculated` | `Approved`, then released | Run `Rejected`; its claims are released for a new run | Cut off and calculate a new run |
| `PAYOUT_RELEASE` (payout batch) | Batch `Built` | `Approved`, then released | Batch `Rejected`; its payments return to the pool for a new batch (never in two live batches, never paid twice) | Build a new batch |

- **Not approvals:** a Compliance regulatory return's approval and Health preauthorisation and claim decisions are the records' own transitions.

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
- **Payment release limits:** `payment.cfg_payment_release_limits` was retired (V0059, OQ-112) because nothing read it; `payments/static/payment-release-limits` is gone. Payment release uses the Workflow table above.
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
- **When it is rejected:** the proposed decision is cleared (`decisionPending: false`) and the case stays `InAssessment`. The underwriter can gather more evidence, refer the case, or propose a decision again. The case shows the rejection as `latestRejection` (who, when, the reason).
- **If the approval expires or is withdrawn**, the same as a rejection applies, and the underwriter gets a follow-up task (§1).

## 5. MFA per client, and what a member without a one-time code sees (BR-008)

**How INSYS decides a token has a second factor.** The token's `amr` claim names one (`otp`, `mfa`, `hwk` or `swk`; each realm flow step sets its `amr` value), or its `acr` is 2 or more. Nothing else counts.

**Per client, the realm's flow decides; the console does not ask for it.**

| Client | Keycloak flow | Second factor |
|---|---|---|
| `insys-web` (staff) | The realm's browser flow | A one-time code for every user provisioned with `mfaEnrolled` (Keycloak asks them to set it up at first sign-in). A privileged role can only be assigned to an enrolled user (409 `MFA_REQUIRED`, IAM-003). |
| `broker-portal`, `provider-portal` | `portal-mfa-browser` | Always: password, then a one-time code. |
| `member-portal` | `member-browser` | A one-time code only when the member has set one up. Whether the tenant requires it is INSYS's setting (`identity/static/authentication-and-session-policies`: `mfaRequired` on a row whose `clientIds` include `member-portal`). |

**Step-up with `acr_values` is not configured.** The realm has no levels of assurance (no `acr.loa.map`, no step-up conditions), so asking for `acr_values` changes nothing today. If a screen ever needs a higher level than the client's flow gives, that is a realm change (a level-of-assurance mapping and a conditional flow) plus an INSYS rule naming the operations; raise it as a request.

**A member without a one-time code, where the tenant requires MFA.** Keycloak signs them in with their password (the member flow asks for a code only once one is set up), and every INSYS call then returns **403 `PORTAL_MFA_REQUIRED`**. The console should treat that code as "set up a one-time code": send the member back to Keycloak's authorisation endpoint with `kc_action=CONFIGURE_TOTP` (Keycloak's application-initiated action), where they scan the code and enter it; the token that comes back carries `amr` `otp`, and INSYS accepts it. Signing out and in again is not needed. Brokers and providers never see this code: their flow always asks for the one-time code.
