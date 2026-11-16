# Write-scope sweep at main af17e03 (15 November 2026), full tables

# Write-scope sweep W1: Payments, Billing, Workflow, Finance, Reinsurance

Tree: `sweep-tree`, detached at `origin/main` `af17e03`. Read-only. Paths are under `src/Modules/` unless they start with `src/BuildingBlocks` or `tests/`.

**Mechanism (checked).** `AccessScopeChecks.InScope` throws `OutOfScopeException` on a non-GET request (`src/BuildingBlocks/Insys.BuildingBlocks.Application/Context/AccessScopes.cs:89-108`); `CurrentScope` is the scope of the route's own permission (`ExecutionScope.cs:117-120`). `PlaceNew` checks product and node for a creation (`AccessScopes.cs:115-130`). No handler in these five modules calls `PlaceNew` (grep). Finance and Reinsurance apply no scope anywhere (grep for `InScope|CurrentScope|PlaceNew|ScopeFor` finds nothing).

## Payments

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /payments/payments | payment.create (`PaymentsModule.cs:86-94`) | new payment against `BusinessRefType/Id` (invoice, account, refund, claim instruction…) | through its business record, via `IBusinessRecordScopes` (`PaymentScopes.Covers`, `Payments.Application/Payments.cs:704-714`) | creation (§4) plus the additions: a payment follows its invoice, account or refund (whole) and its claim, so the referenced record must be in scope | none: `InitiatePaymentHandler` (`Payments.cs:386-398`) calls `PaymentStarter.Start` (`:311-340`), which never checks the business record's scope | none found | **MISSING** |
| POST /payments/payments/{id}/resolve | payment.resolve (`PaymentsModule.cs:135-150`) | payment (its business record) | as above | action (§4): 403 OUT_OF_SCOPE when the payment's record is outside | none: unscoped `payments.Get` (`Payments.cs:426`; store `Payments.Infrastructure/Persistence/PaymentsStores.cs:45`); `PaymentScopes` not injected (`Payments.cs:415`). The UNKNOWN_PAYMENT_RESOLUTION approval it raises (businessRefType PAYMENT, `PaymentsStores.cs:257-259`) has no `IApprovalScopeSource`, so it is unscoped in Workflow too | none found | **MISSING** |
| POST /payments/payments/{id}/reverse | payment.resolve (`PaymentsModule.cs:152-157`) | payment | as above | action (§4) | none: unscoped `payments.Get` (`Payments.cs:458`); no `PaymentScopes` in `ReversePaymentHandler` (`:447`) | none found | **MISSING** |
| POST /payments/payments/{id}/allocate | payment.update (`PaymentsModule.cs:159-164`) | payment, and the account's invoices it is allocated to | as above; invoices carry product and node | action (§4); OQ-081: allocation across an account is account-wide (whole account) | none: unscoped `payments.Get` (`Payments.cs:475`); `Allocation.Allocate` calls Billing's `IAllocationPlanner.Plan` (`:510`), which is raw SQL over every open invoice of the account with no scope (`Billing.Infrastructure/Persistence/BillingStores.cs:222-260`) | none found | **MISSING** |
| POST /payments/payout-batches | payout.create (`PaymentsModule.cs:168-177`) | new batch of every waiting payout of a purpose and currency | through each payment's record | addition: building a batch needs all of it in scope | `scopes.Of(waiting)`, 403 OUT_OF_SCOPE when only part is covered (`Payments.cs:526-531`) | none found for scope (only the unrestricted maker builds one, `tests/Insys.EndToEndTests/EnterpriseProcessTests.cs:1052`) | **OK** |
| POST /payments/payout-batches/{id}/release | payout.release (`PaymentsModule.cs:216-223`) | batch and its payments | as above | addition: releasing needs all of it | `scopes.Of(all)`: 404 when none covered, 403 OUT_OF_SCOPE when part (`Payments.cs:561-568`); unscoped `payments.Batch` load (`:556`) is gated by that | none found | **OK** |
| POST /payments/bank-statements | reconciliation.update (`PaymentsModule.cs:225-231`) | bank statement lines; creates reconciliation exceptions | statement lines: no product or node | not scoped: a bank statement is tenant-wide (inferred); the exceptions it raises are scoped through their payment (OQ-170) when read or acted on | none (`Payments.cs:587-613`) | n/a | **NOT SCOPED** (inferred: tenant-wide statement) |
| POST /payments/reconciliation-exceptions/{id}/assign; …/{id}/resolve | reconciliation.update (`PaymentsModule.cs:247-259`) | reconciliation exception (optional `PaymentId`) | through its payment's business record | OQ-170: scoped through the record it references (the payment) | none: unscoped `payments.Exception` (`Payments.cs:626`, `:646`; store `PaymentsStores.cs:145`) | none found | **MISSING** |
| POST /payments/callback-endpoints; POST /callback-endpoints/{id}/revoke | callback_endpoint.update (`PaymentsModule.cs:336-365`) | provider callback endpoint | configuration | not scoped (§1: configuration) | none | n/a | **NOT SCOPED** |

## Billing

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /billing/accounts | account.create (`Billing.Api/BillingModule.cs:74-80`) | new billing account | no: an account is scoped only through its schedules, and a new one has none | creation (§4), but the record carries no product or node yet | none (`Billing.Application/Billing.cs:316-330`). A scoped creator then cannot act on it: `Account` refuses an account with no in-scope schedule (`BillingStores.cs:50-57`) | none found | **UNCLEAR** (an empty account has nothing to place) |
| POST /billing/accounts/{id}/schedules | account.update (`BillingModule.cs:97-105`) | the account; a new premium schedule for `PolicyRef` | account through schedules; a hand-made schedule gets no product or node (`Billing.Domain/Billing/BillingAccount.cs:152-158,376-399`) | account-wide (OQ-081) and creation (§4): the policy billed must be in scope | `AccountScopes.RequireWhole` (`Billing.cs:342`, defined `:185-188`) after the scoped load (`BillingStores.cs:50-57`). The new schedule's policy is never checked, and the schedule has no product or node | none found | **PARTIAL** (account checked; the policy billed isn't, and the schedule isn't placed) |
| PUT /billing/accounts/{id}/schedules/{scheduleId} | account.update (`BillingModule.cs:107-113`) | one schedule | product and node on the schedule | action (§4) on the schedule | `CurrentScope.Allows(schedule…)`, 403 OUT_OF_SCOPE (`Billing.cs:371-374`) | none found | **OK** |
| POST /billing/accounts/{id}/invoices; /age; /close; /refunds | invoice.create; account.update; account.update; refund.create (`BillingModule.cs:115-151`) | account (and new invoices, or a new refund) | through schedules | OQ-081: account-wide operations need the whole account | scoped load (`BillingStores.cs:50-57`), then `RequireWhole` (`Billing.cs:397`, `:495`, `:525`, `:549`). New invoices carry their schedule's product and node | `tests/Insys.EndToEndTests/LifeProcessTests.cs:766-769` (`/age` → 403 OUT_OF_SCOPE, `ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103`) | **OK** |
| POST /billing/invoices/{id}/credit-notes | invoice.create (`BillingModule.cs:165-171`) | invoice; new credit note | product and node on the invoice | action (§4) on the invoice | scoped `billing.Invoice` (`Billing.cs:455`; store `BillingStores.cs:60-64`, `InScope` → OUT_OF_SCOPE on a POST) | none found | **OK** |
| POST /billing/migration/outstanding-premiums | invoice.migrate (`BillingModule.cs:161-164`) | new migrated invoices, one per row's policy schedule | the schedule's product and node | creation (§4): each invoice's product and node in scope | `AccountForPolicy` → scoped `Account` (`PremiumMigration.cs:94`; `BillingStores.cs:155-157`), which passes when **any** schedule on the account is in scope; the row's own schedule isn't checked before `Invoice.Migrated` (`PremiumMigration.cs:125`) | none found | **PARTIAL** (a shared account lets a partial caller migrate invoices for an out-of-scope schedule) |
| Contracts `IAllocationPlanner.Plan` (from POST /payments/payments/{id}/allocate) | payment.update (caller's) | the account's open invoices | each invoice's product and node | OQ-081: whole account | none (`BillingStores.cs:222-260`) | none found | **MISSING** |

## Workflow

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| (event) approval request: `RequestApprovalPolicy` on `ApprovalRequested` | none (consumer, service) | new approval about a record | stamped from the subject through `IApprovalScopeSource` (`Workflow.Application/Approvals.cs:600-605`; `ScopeTo` sets `IsScoped`, `Workflow.Domain/Approvals/Approval.cs:144-149`; a source that finds nothing stamps (null, null), so only unrestricted checkers decide) | OQ-176: the approval gets a scope source from its subject | sources exist for LIFE underwriting and claim (`Life.Infrastructure/Persistence/LifeApprovalScopes.cs:45-53`), General endorsement and settlement (`GeneralApprovalScopes.cs:44-52`), claim instruction (`ClaimsApprovalScopes.cs:44-49`), PAYOUT_BATCH (`Payments.Infrastructure/Persistence/PayoutApprovalScopes.cs:42`), Health run (`HealthPaymentScopes.cs:62`) | n/a | **OK** (the mechanism; coverage in the next rows) |
| POST /workflow/approvals/{id}/approve; …/reject (scoped approvals) | approval.approve (`WorkflowModule.cs:175-211`) | approval, about its record | `IsScoped`, `ProductCode`, `OrgNodeCode` on the approval | §4 approvals: checker has the permission in scope for the record | unscoped load (`WorkflowStores.cs:52`), then `ApprovalDecision.Decide`: 403 OUT_OF_SCOPE when `IsScoped` and approval.approve's scope doesn't allow it (`Approvals.cs:213-216`) | `tests/Insys.IntegrationTests/WorkflowTests.cs:233` `ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103` (approve outside → 403 OUT_OF_SCOPE, inside → 204) | **OK** |
| approve/reject of approvals whose subject is scoped but has no scope source: GENERAL_RENEWAL (non-renewal), PAYMENT (unknown resolution), REFUND_REQUEST, CASE_REDRESS, RECORD_LEGAL_HOLD and DOCUMENT_LEGAL_HOLD, PRODUCT_VERSION | approval.approve | approval about a policy, payment, refund, case, hold on a record, product version | the subject knows its scope (policy; payment through its record; refund through the whole account; case; hold through its record, OQ-169; product, OQ-173); no `IApprovalScopeSource` covers these types (grep: only the five sources above) | OQ-176 (fail closed), OQ-081, OQ-169, OQ-173 | `IsScoped=false`, so `Decide` skips the scope check (`Approvals.cs:213`) and any checker with authority decides | none found | **MISSING** |
| approve/reject of approvals about unscoped subjects: ROLE_ASSIGNMENT, TENANT_ADMIN_REQUEST, SUBJECT_LINK, BANK_ACCOUNT, PARTY_MATCH, DUE_DILIGENCE*, SEARCH_LOG_HOLD, STATIC_DATA_*, ACCOUNTING_PERIOD, COMMISSION_STATEMENT | approval.approve | identity, party, static data, period, commission statement | no | not scoped: §1 (parties, static data, platform); OQ-170 (Finance); OQ-174 (commission statements) | none, correctly | n/a | **NOT SCOPED** |
| approve/reject of HEALTH_RECOVERY, HEALTH_CREDIT_NOTE approvals | approval.approve | provider recovery, provider credit note | provider-level, across members' products (read sweep B, (b)2-3) | unclear | none (`IsScoped=false`) | none found | **UNCLEAR** (provider-level records: no OQ settles them) |
| POST /workflow/approvals/{id}/withdraw | none: `RequireSignedInUser` (`WorkflowModule.cs:213-222`) | approval | as above | the requester only (`Approval.cs:287-290`); a route with no permission has no scope to apply | unscoped load (`Approvals.cs:456`); maker check only | none found | **UNCLEAR** (no permission on the route, so §4 has nothing to check; the maker may have lost scope since) |
| GET /workflow/approvals?decidable=true (the decidable check) | approval.read | approval | as above | §4: the checker's scope for approval.approve | `CanApprove` uses `CurrentScope`, which on this GET is approval.read's (`Approvals.cs:251`, called `:513`); the page is pre-filtered by approval.read's scope too (`WorkflowStores.cs:107-114`) | `WorkflowTests.cs:233` (no decidable case) | **PARTIAL** (approval.read's scope, not approval.approve's) |
| POST /workflow/cases | case.create (`CaseEndpoints.cs:181-201`) | new case, placed from its links | `PlaceAt` from the first link that a module answers for, and only INVOICE, BILLING_ACCOUNT, REMITTANCE (`Cases.cs:551-575`; `ServiceCase.cs:434-443`) | creation (§4): product and node in scope; OQ-176: a scope source from the subject | no `PlaceNew` or caller check: a caller may open a case linked to an out-of-scope invoice, and it is placed there. POLICY, CLAIM, MEMBERSHIP and PAYMENT links give no placement (`Cases.cs:577-583`), so the case is unscoped. A parent case is loaded through `access.Load` (`Cases.cs:620`) | none found | **MISSING** |
| POST /workflow/cases/{id}/acknowledge, /messages, /extend, /escalate, /start, /await-complainant, /resume, /decide, /close, /withdraw, /reopen, /ombud-referral, /ombud-outcome, /share-with-broker, /redress | case.update; case.decide; case.redress (`CaseEndpoints.cs:203-272`) | case (redress is its child) | `ProductCode`, `OrgNodeCode` on the case | action (§4); OQ-176: fail closed | `CaseAccess.Load` → 403 OUT_OF_SCOPE (`Cases.cs:442-457`, via `Change` `:820` and redress `:798`); but `InScope` lets a case with neither product nor node through for every caller (`Cases.cs:439`) | none found for staff scope | **PARTIAL** (unplaced cases are open to every scoped caller, against OQ-176) |
| POST /workflow/cases/{id}/links | case.update (`CaseEndpoints.cs:209-212`) | case; the linked record | as above | action on the case (§4); the linked record within the caller's scope | the case is checked (`Cases.cs:820`); the linked record isn't checked against the caller, and an unplaced case is placed at it (`Cases.cs:557-570`) | none found | **PARTIAL** (linked record not checked) |
| POST /portal/cases; /portal/cases/{id}/messages; /portal/cases/{id}/appeal | case.create (portal) (`CaseEndpoints.cs:306-341`) | new or own case | relationship | OQ-175: relationship only | every link within the relationship, 403 OUT_OF_SCOPE or 404 otherwise (`CasePortal.cs:228-238`); own case only (`CasePortal.cs:260-266,271-275`) | `tests/Insys.IntegrationTests/CaseTests.cs:246` `APortalMember_RaisesAndSeesOnlyTheirOwnCases_AndAppealsADecision_Cases5` | **OK** |
| POST /workflow/processes | process.create (`WorkflowModule.cs:225-235`) | new process instance about `BusinessRefType/Id` | through its business record (not stored) | §1: tasks follow their record; creation (§4) | none (`Processes.cs:183-206`) | none found | **MISSING** |
| POST /workflow/processes/{id}/suspend, /resume, /cancel, /notes, /tasks/{taskId}/assign, /complete, /escalate | process.update (`WorkflowModule.cs:265-310`) | process and its tasks, about their record (a case, a quote referral, a membership…) | through the business record | §1 and §4: action on the record's task | none: `ProcessAccess.Load` uses the unscoped `processes.Get` (`Processes.cs:167-179`; store `WorkflowStores.cs:128-129`). A case's process can be driven without the case's scope | none found | **MISSING** |

## Finance

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /finance/journals/{id}/reverse; POST /finance/posting-exceptions/{id}/retry; POST /finance/migration/opening-balances | journal.reverse; journal.reverse; journal.migrate (`Finance.Api/FinanceModule.cs:84-95,170-175`) | journal (and its lines' business references); posting exception; opening-balance journal | no product or node on the journal | OQ-170: Finance journals aren't scoped | none (no scope code in Finance, grep) | n/a | **NOT SCOPED** (OQ-170) |
| POST /finance/periods/{code}/open, /soft-close, /close, /reopen; POST /finance/tax-returns, /{id}/approve, /{id}/reject; POST /finance/gl-exports, /{id}/acknowledgement | period.update; tax_return.create/approve; gl_export.create/update (`FinanceModule.cs:99-114,130-145,182-239`) | accounting period, tax return, GL export batch | tenant-wide | not scoped (§1: no product or node) | none | n/a | **NOT SCOPED** |

## Reinsurance

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /reinsurance/recoveries/{id}/bill; …/{id}/settle | recovery.update (`Reinsurance.Api/ReinsuranceModule.cs:89-101`) | reinsurance recovery, about a claim and its policy | no product column; inferred: through `CessionId` (cession's `ProductCode`, `Reinsurance.Domain/Cessions.cs:147`) or `PolicyRef` (`Recoveries.cs:69-79`) | OQ-171: recoveries scoped by product | none: unscoped `repository.Recovery` (`Reinsurance.Application/Reinsurance.cs:204,216`; store `ReinsuranceStores.cs:71`) | none found | **MISSING** |
| POST /reinsurance/bordereaux; …/{id}/send; …/{id}/agree | bordereau.create; bordereau.update (`ReinsuranceModule.cs:103-143`) | bordereau (treaty by period) | treaty-level | OQ-171: bordereaux aren't scoped | none | n/a | **NOT SCOPED** (OQ-171) |

Cessions have no write route: they are created by consumers (`Reinsurance.cs`, cession policy), so there is no user command to check.

## (a) MISSING and PARTIAL, one line each
1. Payments POST /payments: MISSING. No check that the business record is in the creator's scope (`Payments.cs:311-340,386-398`).
2. Payments POST /payments/{id}/resolve: MISSING. Unscoped `payments.Get` (`Payments.cs:426`).
3. Payments POST /payments/{id}/reverse: MISSING. Unscoped `payments.Get` (`Payments.cs:458`).
4. Payments POST /payments/{id}/allocate: MISSING. Unscoped `payments.Get` (`Payments.cs:475`), and Billing's `IAllocationPlanner` applies no scope (`BillingStores.cs:222-260`).
5. Payments reconciliation exceptions assign/resolve: MISSING (OQ-170). Unscoped `payments.Exception` (`Payments.cs:626,646`).
6. Billing POST /accounts/{id}/schedules: PARTIAL. The account is checked whole; the policy billed isn't, and the hand-made schedule has no product or node (`Billing.cs:342`; `BillingAccount.cs:152-158`).
7. Billing POST /migration/outstanding-premiums: PARTIAL. An account passes when any of its schedules is in scope; the row's own schedule isn't checked (`PremiumMigration.cs:94,125`).
8. Billing `IAllocationPlanner`: MISSING (same as 4).
9. Workflow approvals about GENERAL_RENEWAL, PAYMENT, REFUND_REQUEST, CASE_REDRESS, RECORD_LEGAL_HOLD and DOCUMENT_LEGAL_HOLD, PRODUCT_VERSION: MISSING. No `IApprovalScopeSource`, so any checker decides them (`Approvals.cs:213,600-605`).
10. Workflow decidable=true: PARTIAL. Uses approval.read's scope, not approval.approve's (`Approvals.cs:251`).
11. Workflow POST /cases: MISSING. No creation check; only invoice, account and remittance links place the case (`Cases.cs:551-583`).
12. Workflow case actions: PARTIAL. A case with neither product nor node is open to every caller (`Cases.cs:439`), against OQ-176.
13. Workflow POST /cases/{id}/links: PARTIAL. The linked record isn't checked against the caller, and it places an unplaced case (`Cases.cs:557-570`).
14. Workflow POST /processes: MISSING (`Processes.cs:183-206`).
15. Workflow process and task actions: MISSING. Unscoped `processes.Get` (`Processes.cs:169`; `WorkflowStores.cs:128-129`).
16. Reinsurance recoveries bill/settle: MISSING (OQ-171). Unscoped `repository.Recovery` (`Reinsurance.cs:204,216`).

## (b) UNCLEAR, one line each
1. Billing POST /accounts: an empty account has no product or node to place, and a scoped creator can then not act on it. Should opening an account take a product or node, or be for unrestricted callers only?
2. Workflow HEALTH_RECOVERY and HEALTH_CREDIT_NOTE approvals: these are provider-level records across products, and no OQ says whether they are scoped.
3. Workflow POST /approvals/{id}/withdraw has no permission (`RequireSignedInUser`), so there is no scope to apply. Is maker-only enough when the maker's scope has since narrowed?

## (c) Inferred
- Bank statement import is treated as tenant-wide, with no OQ naming it; its exceptions are scoped through their payment by OQ-170.
- A reinsurance recovery's product would come from its cession (`CessionId`) or its policy. An excess-of-loss recovery may have no cession (`Reinsurance.cs:448`); that join isn't traced.
- `RequestApprovalPolicy` runs as a consumer (service identity). The maker's own command is scoped, or not, in its module; that isn't judged here.
- For PAYMENT approvals, the scope that should be stamped would be the payment's business record's, read through `IBusinessRecordScopes` as `PayoutApprovalScopes` does.

# Write-scope sweep W2 at origin/main af17e03: General, Life, Rating, ProductFactory, Rules, Distribution

Paths are under `src/Modules/` unless they start with `tests/`. "Scoped store" means a repository method that ends in `execution.InScope(...)`, which throws `OutOfScopeException` (403 OUT_OF_SCOPE) on a write request (`src/BuildingBlocks/Insys.BuildingBlocks.Application/Context/AccessScopes.cs:72-91`). Workflow decides approvals against the scope that the owning module's `IApprovalScopeSource` records (`Workflow/Insys.Workflow.Application/Approvals.cs:600-605`). A business reference type that no source covers gives an unscoped approval (`Approvals.cs:213`).

Shared stores used below:
- General (`General/Insys.General.Infrastructure/Persistence/GeneralStores.cs`), scoped:
  - `Quote` and `LatestQuoteVersion` (:52-56);
  - `Proposal` and `ProposalForQuote`, through the quote (:58-62, :98-108);
  - `Policy`, `PolicyFromQuote` and `RenewalOf` (:72-79);
  - `Endorsement`, through the policy (:81-90);
  - `Claim` and `ClaimByNumber` (:93-97);
  - `Renewal` and `RetentionCase`, which require the scoped `Policy` (:272-275, :293-296).
- General, **unscoped**:
  - `RiskObject` (:50);
  - `Assessment` (:116-117);
  - `Recovery` (:126-127);
  - `RenewalOfPeriod`, `LatestRenewal` and `OpenRetentionCase`. Each is called only after a scoped policy load.
- Life (`Life/Insys.Life.Infrastructure/Persistence/LifeStores.cs`), all scoped:
  - `Proposal` (:54-56);
  - `Case` and `CaseForProposal`, through the proposal (:58-63, :159-169);
  - `Policy` and `PolicyForProposal` (:71-74);
  - `Loan`, through the policy (:81-91);
  - `Claim` (:108);
  - `Scheme` (:118-119).

## General

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /general/quotes (version 1) | general_quote.create (QuoteCreate) | new quote | product: request; node: placed | §4 creation: product + node | `execution.PlaceNew(...)` (`General/Insys.General.Application/Underwriting.cs:381-389`) | `tests/Insys.EndToEndTests/GeneralProcessTests.cs:232` `ScopedAssignments_SeeAndActOnlyWithinTheirOrganisationAndProduct_F103` (create outside the scope refused, :264-266) | OK |
| POST /general/quotes with fromQuoteId (new version) | QuoteCreate | new version of an existing quote | product must equal the previous version's; node copied from it (`General/Insys.General.Domain/Underwriting/Quotes.cs:370,379`) | §4 action on the source quote (child keeps the node) | scoped `Quote` and `LatestQuoteVersion` (`Underwriting.cs:331,336`); PlaceNew is skipped because the node is inherited (:381) | none found | OK |
| POST /general/quotes/{id}/issue, /accept, /reject; POST /general/quotes/{id}/refer | general_quote.update | quote | product / node on the quote | §4 action | scoped `Quote` (`Underwriting.cs:434`, :499) | `GeneralProcessTests.cs:232` (issue gets OUT_OF_SCOPE, :257-259) | OK |
| POST /general/quotes/{id}/referral-decision | QuoteUnderwrite | quote referral | quote | §4 action | scoped `Quote` (`Underwriting.cs:532`) | none found | OK |
| POST /general/proposals | QuoteUpdate | new proposal for a quote | through the quote | §4 creation: child inherits the quote's scope | scoped `Quote` (`Underwriting.cs:572`) | none found | OK |
| POST /general/proposals/{id}/accept | QuoteUnderwrite | proposal | through the quote | §4 action | scoped `Proposal` (`Underwriting.cs:602`) | none found | OK |
| POST /general/policies | general_policy.create | new policy from a quote | node copied from the quote (`General/Insys.General.Domain/Policies/GeneralPolicy.cs:416`) | §2 (policy keeps the quote's node) + §4 | scoped `Quote` and `PolicyFromQuote`, and the scoped `Policy` for renewalOfPolicyId (`General/Insys.General.Application/PolicyAdministration.cs:118,123,149`) | none found | OK |
| POST /general/policies/{id}/renew | PolicyCreate | renewal policy | node copied from the expiring policy (`GeneralPolicy.cs:461`) | §2 + §4 | scoped `Policy` and `RenewalOf` (`PolicyAdministration.cs:179,184`) | none found | OK |
| POST /general/policies/{id}/cancel, /reinstate | PolicyCancel | policy | policy | §4 action | scoped `Policy` (`PolicyAdministration.cs:243,269`) | none found | OK |
| POST /general/policies/{id}/endorsements | EndorsementCreate | new endorsement | through the policy | §4 (child) | scoped `Policy` (`PolicyAdministration.cs:329`) | none found | OK |
| POST /general/endorsements/{id}/appointment-letter, /submit | EndorsementCreate | endorsement (and the document filed against it, OQ-169) | through the policy | §4 action (child) | scoped `Endorsement` (`PolicyAdministration.cs:305`; :449-450 also loads the scoped `Policy`); the document must be filed against this endorsement (:310) | none found | OK |
| Workflow approval GENERAL_ENDORSEMENT and GENERAL_CLAIM (settlement) | approval.approve (Workflow route) | approval about an endorsement or a claim | the policy's or the claim's product and node | §4 approvals / OQ-176 | `GeneralApprovalScopes` SQL (`General/Insys.General.Infrastructure/Persistence/GeneralApprovalScopes.cs:44-53`), checked at `Approvals.cs:213` | Workflow side on a seeded approval: `tests/Insys.IntegrationTests/WorkflowTests.cs:233` `ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103`; General's source: none found | OK |
| POST /general/policies/{id}/non-renewal | PolicyNonRenew | renewal (non-renewal request) | through the policy | §4 action | scoped `Policy` (`General/Insys.General.Application/RenewalResponses.cs:121`); the renewal comes from the unscoped `RenewalOfPeriod`, but only for that policy (:131) | none found | OK |
| Workflow approval GENERAL_RENEWAL (non-renewal, GENERAL_NON_RENEWAL) | approval.approve, plus PolicyNonRenew as the required permission | approval about a renewal | renewal -> policy (product / node) | §4 approvals / OQ-176: fail closed, scope from the subject | **none**: `GeneralApprovalScopes.Sql` has no case for `NonRenewalBusinessRefType` (`GeneralApprovalScopes.cs:44-53`; type at `General/Insys.General.Contracts/GeneralContracts.cs:75`), so `ScopeTo` is never called (`Approvals.cs:600-605`). The required permission is checked only with `HasPermission`, not in scope (`Approvals.cs:219`; requested at `GeneralStores.cs:575-577`) | none found | **MISSING** |
| POST /general/renewals/{id}/decision | QuoteUnderwrite | renewal | through the policy | §4 action | scoped `Renewal` (`General/Insys.General.Application/RenewalReview.cs:247`) | none found | OK |
| POST /general/renewals/{id}/accept, /decline | general_renewal.respond | renewal | through the policy | §4 action | scoped `Renewal` (`RenewalResponses.cs:180`) | none found | OK |
| POST /portal/general/renewals/{id}/accept, /decline | RenewalRespond; party, broker | renewal | through the policy; relationship | OQ-175 relationship | `Visible`: scoped `Renewal` and `Policy` + `Related.Policy` (`RenewalResponses.cs:201,224-228`) | `GeneralProcessTests.cs:822` (someone else's renewal) | OK (relationship; F103 also applied) |
| POST /general/retention-cases | RetentionUpdate | new retention case | through the policy | §4 creation (child) | scoped `Policy` (`General/Insys.General.Application/Retention.cs:108`) | none found | OK |
| POST /general/retention-cases/{id}/offers, /offers/{offerId}/answer, /outcome | RetentionUpdate | retention case | through the policy | §4 action | scoped `RetentionCase` (`Retention.cs:135`, :194 for answer and outcome) | none found | OK |
| POST /general/claims | ClaimCreate | new claim | product / node copied from the policy (`General/Insys.General.Application/ClaimHandling.cs:243`) | §4 creation (inherits) | scoped `Policy` (`ClaimHandling.cs:209`) | none found | OK |
| POST /portal/general/claims | ClaimCreate; party | new claim | as above; relationship: the policy | OQ-175 relationship | `PortalClaimAllowed`: scoped `Policy` + `Related.Policy` (`General/Insys.General.Application/Portal.cs:320-321`), then NotifyLoss | `GeneralProcessTests.cs:1152` (another's policy is 404) | OK (relationship) |
| POST /general/claims/{id}/register, /parties, /assessor, /settlement, /decline, /close, /reopen | ClaimCreate, ClaimUpdate or ClaimDecide | claim | claim | §4 action | scoped `Claim` and `Policy` through `ClaimContext.Load` (`ClaimHandling.cs:176-185`, used at :255, :319, :349), or scoped `Claim` directly (:299, :399, :420) | none found | OK |
| POST /general/assessments/{id}/start, /accept, /further-work | general_assessment.update | loss assessment | through the claim | §4 action (child of the claim) | **unscoped** `Assessment` (`GeneralStores.cs:116-117`); `ChangeAssessmentHandler` never loads the claim (`ClaimHandling.cs:441-461`) | none found (the routes are exercised only unscoped, e.g. `GeneralProcessTests.cs:528`) | **MISSING** |
| POST /general/assessments/{id}/estimates, /report | AssessmentUpdate | loss assessment | through the claim | §4 action (child) | unscoped `Assessment`, then the scoped `ClaimContext.Load` of its claim (`ClaimHandling.cs:469-474`, :501-506) | none found | OK |
| POST /general/claims/{id}/recoveries | RecoveryUpdate | new recovery case | through the claim | §4 creation (child) | scoped `Claim` and `Policy` (`ClaimHandling.cs:540,550`) | none found | OK |
| POST /general/recoveries/{id}/demand, /write-off, /close; POST /general/recoveries/{id}/salvage; POST /general/recoveries/{id}/salvage/{itemId}/dispose | RecoveryUpdate | recovery case | through the claim | §4 action (child) | **unscoped** `Recovery` (`GeneralStores.cs:126-127`); the claim is never loaded (`ClaimHandling.cs:570`, :589, :603) | none found | **MISSING** |
| POST /general/recoveries/{id}/recovered | RecoveryUpdate | recovery case | through the claim | §4 action | unscoped `Recovery`, then the scoped `ClaimContext.Load` (`ClaimHandling.cs:622-628`) | none found | OK |
| POST /general/migration/policies | PolicyMigrate | new migrated policies | product: per row; node: **never set** (`GeneralPolicy.Migrate`, `GeneralPolicy.cs:427-436`) | §4 creation + §2 node (root when none) | nothing: no `PlaceNew` and no product check (`General/Insys.General.Application/PolicyMigration.cs:70-170`) | none found | **MISSING** |
| POST /general/migration/claims | PolicyMigrate | new migrated claims | product / node from the policy (`General/Insys.General.Application/ClaimMigration.cs:121`) | §4 creation (inherits) | scoped `Policy` per row (`ClaimMigration.cs:89`; one out-of-scope row refuses the whole batch) | none found | OK |
| POST /general/risk-objects; PUT /general/risk-objects/{id}/attributes; POST .../revalue, /owner, /deactivate | RiskCreate, RiskUpdate | risk object | no product, no node; owned by a party | §1 is silent; no OQ | nothing (`Underwriting.cs:147-258`, unscoped `RiskObject` at `GeneralStores.cs:50`) | none found | UNCLEAR |
| POST /portal/general/risk-objects | RiskCreate; broker | new risk object for a client | relationship: the broker services the client | OQ-175 relationship | `PortalClientAllowed` (`Portal.cs:293`) | `GeneralProcessTests.cs:1498,1519` | OK (relationship) |
| POST /portal/general/quotes | QuoteCreate; broker | new quote | product / node; relationship | OQ-175 relationship | `PortalQuoteAllowed` (`Portal.cs:230-262`), then CreateQuote's `PlaceNew` | `GeneralProcessTests.cs:1140-1142,1493,1520` | OK (relationship; F103 also applied) |

## Life

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /life/proposals | life_proposal.create | new proposal | product: request; node: placed | §4 creation | `execution.PlaceNew(...)` (`Life/Insys.Life.Application/Proposals.cs:125,169`) | none found for a refused create (`tests/Insys.EndToEndTests/LifeProcessTests.cs:709` creates in scope) | OK |
| POST /life/proposals/{id}/submit, /withdraw | ProposalUpdate | proposal | proposal | §4 action | scoped `Proposal` (`Proposals.cs:180,202`) | `LifeProcessTests.cs:709` `ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103` (withdraw gets OUT_OF_SCOPE, :730-732) | OK |
| POST /life/underwriting-cases/{id}/evidence, /evidence/{requirementId}/received, /waive; POST /life/underwriting-cases/{id}/{route}; POST .../refer, /decision | UnderwritingUpdate, UnderwritingWaive or UnderwritingDecide | underwriting case | through the proposal | §4 action (child) | scoped `Case` through `Cases.Load` (`Life/Insys.Life.Application/Underwriting.cs:86-87`, used at :122-208); the decision also loads the scoped `Proposal` (:215) | none found | OK |
| Workflow approval LIFE_UNDERWRITING_CASE, LIFE_CLAIM | approval.approve | approval about a case or a claim | the proposal's or the claim's product and node | §4 approvals | `LifeApprovalScopes` (`Life/Insys.Life.Infrastructure/Persistence/LifeApprovalScopes.cs:46-55`) | Workflow side only (`WorkflowTests.cs:233`) | OK |
| POST /life/policies | PolicyCreate | new policy from a proposal | node copied from the proposal (`Life/Insys.Life.Domain/Policies/LifePolicy.cs:471`) | §2 + §4 | scoped `Proposal`, `PolicyForProposal` and `CaseForProposal` (`Life/Insys.Life.Application/Policies.cs:115,120,125`) | none found | OK |
| POST /life/policies/{id}/beneficiaries, /alterations, /reinstate, /{route} (policy values); POST /life/policies/{id}/loans | PolicyUpdate, LoanCreate | policy, or a new loan (child) | policy | §4 action / creation (child) | scoped `Policy` through `LifePolicies.Load` (`Policies.cs:105-106`, used at :187, :216, :248, :274, :287) | none found | OK |
| POST /life/loans/{id}/repayments | LoanUpdate | loan | through the policy | §4 action | scoped `Loan` (`Policies.cs:327`) | none found | OK |
| POST /life/claims | ClaimCreate | new claim | product / node copied from the policy (`Life/Insys.Life.Domain/Claims/LifeClaim.cs:269`) | §4 creation (inherits) | scoped `Policy` (`Life/Insys.Life.Application/Claims.cs:112`) | none found | OK |
| POST /life/claims/{id}/register, /assessment, /approval, /decline, /close | ClaimUpdate or ClaimDecide | claim | claim | §4 action | scoped `Claim` and `Policy` through `LifeClaims.Load` (`Claims.cs:93-98`, used at :158, :238, :259), or scoped `Claim` (:307, :328) | none found | OK |
| POST /life/group-schemes | SchemeCreate | new scheme | product: request; node: placed | §4 creation | `PlaceNew` (`Life/Insys.Life.Application/Groups.cs:140,159`) | none found | OK |
| POST /life/group-schemes/{id}/members; PUT .../members/{memberId}; POST .../evidence-accepted, /leave, /renew, /terminate | SchemeUpdate, UnderwritingDecide | scheme (members are its children) | scheme | §4 action | scoped `Scheme` (`Groups.cs:170,191,227`) | none found | OK |
| POST /life/migration/policies | PolicyMigrate | new migrated policies | product: per row; node: **never set** (`LifePolicy.Migrate`, `LifePolicy.cs:422-445`) | §4 creation + §2 | nothing: no `PlaceNew` and no product check (`Life/Insys.Life.Application/PolicyMigration.cs:64-170`) | none found | **MISSING** |
| POST /life/migration/claims | PolicyMigrate | new migrated claims | from the policy | §4 creation (inherits) | scoped `Policy` per row (`Life/Insys.Life.Application/ClaimMigration.cs:91`) | none found | OK |

## Rating

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /rating/calculations | premium_calculation.create (CalculationCreate) | new premium calculation about a subject (GENERAL_QUOTE, GENERAL_ENDORSEMENT, LIFE_PROPOSAL) | product: request; node: none (the subject has one, in another module) | OQ-168: the subject's branch later; **product now** | nothing: `CalculatePremiumHandler` rates and stores the calculation (`Rating/Insys.Rating.Application/Calculations.cs:149-169`). No product check, and the subject is not looked up. Its effect on the subject goes through consumers that run as the service: `Life/Insys.Life.Application/LifeEventPolicies.cs:66-79` and `General/Insys.General.Application/GeneralPolicies.cs:75-110`. Those consumers require only a matching product, version and currency (`Quotes.cs:409`; `Life/Insys.Life.Domain/Proposals/*:356`) | none found (the calculations are posted unscoped only: `tests/Insys.IntegrationTests/SharedServicesTests.cs:125`) | **MISSING** (product) |
| POST /rating/calculations/simulate | rating.simulate | none (nothing is recorded) | product given | not a record (§1) | n/a (`Calculations.cs:198-206`) | n/a | NOT SCOPED (by §1: no record) |

## ProductFactory (OQ-173: changes and approvals are scoped)

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /product-factory/products | product.create | new product (version 1) | product: the request's productCode; node: none | OQ-173: creation, product in scope | nothing (`ProductFactory/Insys.ProductFactory.Application/Products.cs:216-239`) | none found | **MISSING** |
| POST /product-factory/products/{productCode}/versions | ProductCreate | new draft version | product: the route | OQ-173 | nothing (`Products.cs:241-262`) | none found | **MISSING** |
| PUT .../versions/{n}; POST .../{n}/validate, /{n}/simulations | product.update | product version (simulation run as its child) | product | OQ-173 action | nothing (`Products.cs:264-328`); the store has no scope (no `execution` or `InScope` in `ProductFactory/Insys.ProductFactory.Infrastructure/Persistence/ProductStores.cs`) | none found | **MISSING** |
| POST .../{n}/submit, /{n}/withdraw, /{n}/retire | product.publish | product version | product | OQ-173 action | nothing (`Products.cs:330-399`) | none found | **MISSING** |
| Workflow approval PRODUCT_VERSION | approval.approve | approval about a product version | product | OQ-173: approvals scoped | no `IApprovalScopeSource` for PRODUCT_VERSION (only General, Life, ClaimsFramework, Payments and Health register one); requested at `ProductStores.cs:208-209`; the contract still says product versions are not scoped (`Workflow/Insys.Workflow.Contracts/WorkflowContracts.cs:116-121`) | none found | **MISSING** |

## Rules

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /rules/decisions | rule_decision.create (DecisionCreate) | new rule decision about a subject (Life proposal or underwriting case, General renewal) | product: only an underwriting rule set's code; node: none. The subject knows both, in another module | OQ-170: through the record it references; OQ-168: the subject's branch, product only until that is built | nothing: `EvaluateRuleSetHandler` doesn't look up the subject (`Rules/Insys.Rules.Application/Decisions.cs:87-113`). Life's consumer, running as the service, then records the outcome on any underwriting case named (`LifeEventPolicies.cs:89-91`), which decides whether one underwriter may decide alone | none found | **MISSING** |

## Distribution

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /distribution/entitlements/{id}/claw-back | commission.update | commission entitlement | product (nullable), policy_ref; no node | OQ-174: payout, not scoped | nothing (`Distribution/Insys.Distribution.Application/Commission.cs:264-290`) | n/a | NOT SCOPED (OQ-174) |
| POST /distribution/statements | statement.create | new commission statement | no product (spans products) | OQ-174: not scoped | nothing (`Commission.cs:198-260`) | n/a | NOT SCOPED (OQ-174) |
| Workflow approval COMMISSION_STATEMENT | approval.approve | approval about a statement | none | OQ-174: not scoped | no scope source (`Distribution/Insys.Distribution.Infrastructure/Persistence/DistributionStores.cs:336`) | n/a | NOT SCOPED (OQ-174) |

## (a) MISSING and PARTIAL
- General POST /general/assessments/{id}/start, /accept, /further-work: the assessment is loaded unscoped and its claim is never loaded (`ClaimHandling.cs:449`; `GeneralStores.cs:116`).
- General POST /general/recoveries/{id}/demand, /write-off, /close, /salvage, /salvage/{itemId}/dispose: the recovery is loaded unscoped and its claim is never loaded (`ClaimHandling.cs:570,589,603`; `GeneralStores.cs:126`).
- General non-renewal approval (GENERAL_RENEWAL): no scope source, so any checker holding approval.approve and policy.non_renew can decide it, whatever their scope (`GeneralApprovalScopes.cs:44-53`).
- General and Life POST /migration/policies: no `PlaceNew` and no product check; migrated policies get no node (`PolicyMigration.cs` in both modules).
- Rating POST /rating/calculations: no product scope check on creation (OQ-168) (`Calculations.cs:152-168`).
- ProductFactory: every change route (register, draft, edit, validate, simulate, submit, withdraw, retire) and the PRODUCT_VERSION approval apply no scope (OQ-173).
- Rules POST /rules/decisions: the subject's scope is not applied (OQ-170), and the outcome lands on the Life case unscoped (`Decisions.cs:87-113`).

## (b) UNCLEAR
- General risk-object writes: a risk object has no product or node and is owned by a party. §1 and OQ-168 to OQ-177 don't say whether it is unscoped like parties or scoped through its policies.

## (c) Inferred
- Portal routes apply F103 on top of the relationship check, through the same scoped stores. I assumed portal role assignments carry no product or branch scope, so the F103 check is a no-op there, as OQ-175 requires. I didn't verify the portal roles' assignments.
- ProductFactory: product definitions have no node, and `Allows(product, null)` fails every grant with `OrgNodes` (`AccessScopes.cs:39-40`). If the fix reuses `InScope(product, null)`, a branch-scoped product manager is refused everything. The fix needs a product-only check.
- Rating and Rules: the subject's effect is applied by consumers that run as the service (unrestricted), so the only place to apply scope is the POST itself.
- Approval decisions take the scope of approval.approve, the Workflow route's permission (`Approvals.cs:213`). They don't take the scope of the record's own permission (e.g. general_claim.decide). I treated that as §4-compliant ("the permission in scope for the record").

# Write-scope sweep W3: Health, ClaimsFramework, Documents, Communications, Party, Compliance, Integration, Assist, Analytics

Worktree `sweep-tree`, detached at `origin/main` `af17e03`. Read-only. Paths are under `src/Modules/` unless they start with `src/BuildingBlocks` or `tests/`.

**Mechanism (checked).** `InScope` (`src/BuildingBlocks/Insys.BuildingBlocks.Application/Context/AccessScopes.cs:85-104`) throws `OutOfScopeException` (403) on a non-GET request, against `CurrentScope`, the scope of the route's permission (`ExecutionScope.cs:117-120`). A query dispatched inside a POST is not a read, so a scoped store hit inside a command gives 403. Health's `HealthRepository` loads memberships through `Scoped` and every membership child (preauthorisation, claim, utilisation/fraud/complex case, referral, chronic registration, programme enrolment) through `OfMembership`, both calling `InScope` (`Health/Insys.Health.Infrastructure/Persistence/HealthStores.cs:60-64,119-180,293-356,474-486`). Schemes, runs, recoveries, credit notes and provider statements are loaded unscoped (`HealthStores.cs:55,182-186,398-399,434,281`). Workflow applies a checker's scope only to approvals whose type has an `IApprovalScopeSource` (`Workflow/Insys.Workflow.Application/Approvals.cs:601-605,213`).

## Health

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /health/memberships | MembershipCreate | new membership | product from the plan on sale, node placed | §4 creation | `execution.PlaceNew(product, command.OrgNodeCode, nodes, root)` `Health/Insys.Health.Application/Members.cs:187-192` | none for a refusal (`HealthProcessTests.ScopedAssignments_SeeOnlyMembershipsWithinTheirOrganisation_AndProductScopesReachNone_F103_OQ080` only enrols with `orgNodeCode`) | **OK** |
| POST /health/memberships/{id}/dependants; …/dependants/{dependantId}/remove; …/{suspend\|reinstate\|terminate…} (status routes) | MembershipUpdate | membership | product and node on it (`Membership.cs`) | §4 action | scoped load `health.Membership` `Members.cs:295,350,408` (`HealthStores.cs:60-61,474`) | none found | **OK** |
| POST /health/memberships/{id}/plan | MembershipUpdate | membership (new plan's product) | as above | §4 action, plus the new product in scope | scoped load `Members.cs:371`; new product `CurrentScope.Allows(product, membership.OrgNodeCode)` `Members.cs:388-391` | none found | **OK** |
| POST /health/migration/memberships | MembershipMigrate | new migrated memberships | product from the plan (`plans[i].ProductCode`); node never set | §4 creation (product and node in scope) | nothing: no `PlaceNew` or `Allows`; `HealthMembership.Migrate(…)` added with no node, `Health/Insys.Health.Application/MembershipMigration.cs:146-153`; the handler has no `IExecutionContext` (`MembershipMigration.cs:62`) | none found | **MISSING** |
| POST /health/migration/accumulators | MembershipMigrate | opening accumulators of a membership | through the membership | §4 action on the membership | scoped load `health.Membership(row.MembershipId)` `MembershipMigration.cs:212` | none found | **OK** |
| POST /health/eligibility | EligibilityCheck | eligibility decision about a membership | through the membership | §4 (child of membership) | scoped `health.MembershipByNumber` `Members.cs:460` (403 on a POST) | none found | **OK** |
| POST /health/schemes; POST /health/schemes/{id}/{renew\|terminate} | SchemeCreate / SchemeUpdate | scheme | no product or node on the scheme | not in §1; unclear | none, deliberately (`HealthStores.cs:55`, `Members.cs:114-146`) | n/a | **UNCLEAR** |
| POST /health/preauthorisations | PreauthCreate | new preauthorisation on a membership | through the membership | §4 creation (inherits membership) | scoped `health.Membership(command.MembershipId)` `Health/Insys.Health.Application/Care.cs:314` | none found | **OK** |
| POST /health/preauthorisations/{id}/review \| approve \| reject \| modify \| conditions-met \| cancel | PreauthReview | preauthorisation | through the membership | §4 action | `Apply` → scoped `health.Preauthorisation` `Care.cs:424` (`HealthStores.cs:119-123`) | none found | **OK** |
| POST /health/claims (staff) | ClaimCreate | new medical claim on a membership | through `command.MembershipId` | §4 creation (inherits membership) | nothing: `SubmitMedicalClaimHandler` never loads the membership; it builds and adds the claim (`Care.cs:487-516`). Only a `ReplacesClaimId` is loaded scoped (`Care.cs:497-499`). A fraud case may be opened on it too (`Care.cs:542`) | none found | **MISSING** |
| POST /health/claims/{id}/validate \| adjudicate \| review \| approve \| reject \| reverse | ClaimUpdate / ClaimApprove | medical claim | through the membership | §4 action | scoped `health.Claim` `Care.cs:585,646,775` (`HealthStores.cs:133-137`) | none found | **OK** |
| POST /health/utilisation-cases | CaseUpdate | new utilisation case on a membership | through the membership | §4 creation | scoped `health.Membership` `Health/Insys.Health.Application/Runs.cs:266` | none found | **OK** |
| POST /health/utilisation-cases/{id}/review \| conclude | CaseUpdate | utilisation case | through the membership | §4 action | scoped `health.UtilisationCase` `Runs.cs:303` (`HealthStores.cs:163-167`) | none found | **OK** |
| POST /health/fraud-cases/{id}/investigate \| conclude | CaseUpdate | Health fraud case | CLAIM subject → claim → membership | OQ-170 (through the referenced record) | scoped `health.FraudCase` `Runs.cs:320` (`HealthStores.cs:170-180`); non-CLAIM subjects unscoped, but only CLAIM is opened (`Care.cs:542`) | none found | **OK** |
| POST /health/complex-cases | ComplexCaseUpdate | new complex case | through the membership | §4 creation | scoped `health.Membership` `Health/Insys.Health.Application/ComplexCases.cs:192` | none found | **OK** |
| POST /health/complex-cases/{id}/case-manager \| {hold\|resume} \| close \| care-plan \| care-plan/items/{itemId}/progress \| notes | ComplexCaseUpdate | complex case | through the membership | §4 action | scoped `health.ComplexCase` `ComplexCases.cs:213,264` (`HealthStores.cs:293-297`) | none found | **OK** |
| POST /health/referrals (staff) | ReferralCreate | new clinical referral | through the membership | §4 creation | scoped `health.Membership` `ComplexCases.cs:350` | none found | **OK** |
| POST /health/referrals/{id}/decide \| consent \| consent/withdraw \| visits | ReferralDecide / ReferralConsent / ReferralCreate | clinical referral | through the membership | §4 action | scoped `health.ClinicalReferral` `ComplexCases.cs:376,393,410` (`HealthStores.cs:316-320`) | none found | **OK** |
| POST /health/chronic-registrations (staff) | ChronicCreate | new chronic registration | through the membership | §4 creation | scoped `health.Membership` `Health/Insys.Health.Application/ChronicRegistrations.cs:115` | none found | **OK** |
| POST /health/chronic-registrations/{id}/approve \| decline \| end | ChronicDecide / ChronicUpdate | chronic registration | through the membership | §4 action | scoped `health.ChronicRegistration` `ChronicRegistrations.cs:146,174,193` (`HealthStores.cs:322-326`) | none found | **OK** |
| POST /health/programme-enrolments; …/{id}/review \| end | ProgrammeCreate / ProgrammeUpdate | programme enrolment | through the membership | §4 creation / action | scoped `health.Membership` `Health/Insys.Health.Application/ProgrammeEnrolments.cs:93`; `health.ProgrammeEnrolment` `ProgrammeEnrolments.cs:119,143` | none found | **OK** |
| POST /health/payment-runs | RunUpdate | new, empty provider payment run | none until calculated | inferred: nothing to scope at opening; the run follows its claims | none (`Runs.cs:74-82`) | n/a | **OK** (inferred) |
| POST /health/payment-runs/{id}/calculate | RunUpdate | run, gathering the claims ready to pay | product and node of each claim's membership | claim-payout addition: building needs all of it in scope | `RunVisibility.CoversAll(…)` else 403 OUT_OF_SCOPE, `Runs.cs:113-119` | none found | **OK** |
| POST /health/payment-runs/{id}/cut-off \| release | RunUpdate | run | as above | addition: releasing needs all of it | `Apply`: 404 when none visible, 403 OUT_OF_SCOPE when partly, `Runs.cs:204-218` | `HealthProcessTests.HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009` (`tests/Insys.EndToEndTests/HealthProcessTests.cs:387-391`: branch-B release 404) | **OK** |
| Approval HEALTH_PAYMENT_RUN (decided in Workflow) | approval authority | run's approval | claims' memberships | addition: scoped when one scope | `PaymentRunApprovalScopes` `Health/Insys.Health.Infrastructure/Persistence/HealthPaymentScopes.cs:59-72`, registered `HealthInfrastructure.cs:84` | none found | **OK** |
| POST /health/provider-recoveries; …/{id}/invoice \| write-off \| reverse \| hold; POST /health/provider-credit-notes | RecoveryManage / CreditNoteCreate | provider recovery, credit note | provider-level, across products | fail closed (OQ-176, inferred) | refused unless `CurrentScope.IsUnrestricted`: `Health/Insys.Health.Application/ProviderRecoveries.cs:213,229,266,297` | none found | **OK** (stricter than §4: unrestricted only) |
| Approvals HEALTH_RECOVERY (write-off, finding) and HEALTH_CREDIT_NOTE (decided in Workflow) | approval authority | recovery or credit note | provider-level | OQ-176: approvals get a scope source from their subject; fail closed (needs unrestricted, as the maker does) | no `IApprovalScopeSource` for them: only the run's is registered (`HealthInfrastructure.cs:84`); requests raised at `HealthStores.cs:744-749`; so any checker with authority decides, scoped or not (`Approvals.cs:601-605`) | none found | **MISSING** |
| POST /health/providers/{providerId}/quality-reviews; POST /health/provider-statements; …/import; …/{id}/lines/{lineId}/resolve | ProviderUpdate | quality review; provider statement (lines name claims of any product) | provider-level | not in §1; unclear | none (`Health/Insys.Health.Application/ProviderMonitoring.cs:221-235,246-281,287-300`; `HealthStores.cs:281`) | none found | **UNCLEAR** |
| POST /portal/health/claims \| preauthorisations \| referrals \| chronic-registrations | ClaimCreate / PreauthCreate / ReferralCreate / ChronicCreate (provider portal) | new claim, preauthorisation, referral, registration | relationship: the provider is itself (`execution.External.ProviderId`) | OQ-175 (relationship) | provider forced from the principal `Health/Insys.Health.Api/HealthModule.cs:670-737`; member found by scoped `MembershipByNumber` `Health/Insys.Health.Application/Portal.cs:226` | `HealthProcessTests.ExternalUsers_ProvidersAndMembersSeeOnlyTheirOwn_ExternalUserAccess` (relationship, reads) | **OK** |
| POST /portal/health/credit-notes | CreditNoteCreate (provider portal) | credit note on the provider's own claim or remittance | relationship | OQ-175 | `ProviderRecoveries.cs:330-338,377-378` | none found | **OK** |

## ClaimsFramework

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /claims/claims | ClaimCreate | new claim registration | `ProductCode` and `OrgNodeCode` taken from the request body | §4 creation | nothing: caller-supplied product and node stored as given, no `PlaceNew` (`ClaimsFramework/Insys.ClaimsFramework.Application/Claims.cs:340-354`, command `Claims.cs:156-157`). A repeat returns the existing registration through the unscoped `ByDomainClaim` (`Claims.cs:344`; `ClaimsFramework/Insys.ClaimsFramework.Infrastructure/Persistence/ClaimsStores.cs:106-107`) | none found | **MISSING** |
| POST /claims/claims/{n}/evidence-requests \| evidence \| communications \| fraud-evaluations | ClaimUpdate | claim registration | product and node on it | §4 action | `Loaded.Claim` → `ByNumber` → `InScope` `Claims.cs:334,361,381,401,417`; `ClaimsStores.cs:100-104` | none found (`ClaimsScopeTests` covers GETs only) | **OK** |
| POST /claims/claims/{n}/reserves | FinancialsUpdate | claim financials | through the claim | §4 action | `Loaded.Claim` `Claims.cs:448` | none found | **OK** |
| POST /claims/claims/{n}/payment-instructions | InstructionCreate | new instruction of a claim | through the claim | §4 creation (inherits claim) | `Loaded.Claim` `Claims.cs:501`; a repeat ref through scoped `Instruction(claimNumber, ref)` `Claims.cs:507`, `ClaimsStores.cs:124-125` | none found | **OK** |
| POST /claims/payment-instructions/{id}/submit \| release; …/retry; …/redirect | InstructionCreate / InstructionRelease | instruction | its claim | §4 action | `Instruction(id)` → `OfClaim` → `InScope` `Claims.cs:548,607,641`; `ClaimsStores.cs:121-122,157-169` | none found | **OK** |
| Approval of a claim payment instruction (Workflow) | approval authority | instruction | its claim | §4 approvals | `ClaimsApprovalScopes` `ClaimsFramework/Insys.ClaimsFramework.Infrastructure/Persistence/ClaimsApprovalScopes.cs:35-55`, registered `ClaimsFrameworkInfrastructure.cs:54` | `WorkflowTests.ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103` (inferred: which approval type it uses not checked) | **OK** |
| POST /claims/fraud-cases | FraudCaseCreate | new Life/General fraud case | subject named by domain, claim number or policy reference; product and node derivable from the claim register (inferred) | OQ-170 / OQ-176 (scope from the subject) | nothing: the subject is not loaded or checked (`ClaimsFramework/Insys.ClaimsFramework.Application/FraudCases.cs:86-103`) | none found | **MISSING** |
| POST /claims/fraud-cases/{id}/assign \| conclude | FraudCaseUpdate | fraud case | as above | OQ-170 / OQ-176 | unscoped `cases.Get` `FraudCases.cs:111-125`; `ClaimsStores.cs:90` | none found | **MISSING** |

## Documents

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /documents/documents/generate; POST /documents/documents | DocumentCreate | new document attached to `BusinessRefType`/`BusinessRefId` (always required, `Documents/Insys.Documents.Application/Documents.cs:140,145`) | not on the document; the referenced record knows it (resolvable as `RecordHoldTargets` does through `ILegalHoldTargets`) | OQ-169: child of the record; creation in its scope | classification and category only: `Documents.cs:253-293` (generate), `Documents.cs:298-312` (store) | none found | **MISSING** |
| POST /documents/documents/{id}/deliveries | DocumentDeliver | document | through its business record | OQ-169 action | unscoped `documents.Get` `Documents.cs:318-340` (`Documents.cs:248`) | none found | **MISSING** |
| POST /documents/documents/{id}/legal-holds; …/legal-holds/{holdId}/release | LegalHoldManage | hold on one document | through the document's business record | OQ-169 action | none: `Documents.cs:347-365, 370-398` | none found | **MISSING** |
| POST /documents/documents/{id}/dispose | DocumentDispose | document | through its business record | OQ-169 action | none (holds checked, not scope): `Documents.cs:435-480` | none found | **MISSING** |
| POST /documents/legal-holds | LegalHoldManage | new record hold on a policy, claim, case… | through the held record's module (`ILegalHoldTargets`) | OQ-169 creation (child of the held record) | `targets.Check` → 404 / 403 OUT_OF_SCOPE `Documents/Insys.Documents.Application/RecordHolds.cs:92-103,130-134` | `LegalHoldReleaseTests.ARecordsHold_IsPlacedOnARecordThatExistsInScope_KeepsItsDocuments_AndIsReleasedByASecondPerson_D34` (`tests/Insys.IntegrationTests/LegalHoldReleaseTests.cs:149-152`) | **OK** |
| POST /documents/legal-holds/{id}/release | LegalHoldManage | record hold | as above | OQ-169 action | `targets.Check`, refused only on OUT_OF_SCOPE (a vanished record proceeds) `RecordHolds.cs:167-171` | none found (the test releases in scope only) | **OK** |
| Approvals DOCUMENT_LEGAL_HOLD and RECORD_LEGAL_HOLD release (Workflow) | approval authority | the hold, about a scoped record | through the record | §4 approvals; OQ-169, OQ-176 | no `IApprovalScopeSource` in Documents (none registered; requests raised `Documents/Insys.Documents.Infrastructure/Persistence/DocumentStores.cs:256`), so any checker decides | none found | **MISSING** |

## Communications

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| PUT /communications/parties/{partyId}/preferences/{typeCode} | PreferenceUpdate | party preference | no | not scoped (parties, PTY-003) | n/a (`Communications/Insys.Communications.Api/CommunicationsModule.cs:105-127`) | n/a | **NOT SCOPED** |
| POST /communications/callback-endpoints; …/{id}/revoke | CallbackEndpointUpdate | provider endpoint | no | not scoped (configuration) | n/a (`CommunicationsModule.cs:143-168`) | n/a | **NOT SCOPED** |
| POST /communications/callbacks/{endpointKey} | none (signed provider callback) | delivery status of a notification | the notification has product and node | not scoped: no user scope; the provider is authenticated by key (`CommunicationsModule.cs:170-178`) | n/a | n/a | **NOT SCOPED** |
| PUT /portal/communications/preferences/{typeCode}; POST /portal/communications/devices; DELETE …/devices/{id} | PreferenceUpdate / DeviceUpdate (portal) | own preferences and devices | relationship (self) | OQ-175 | n/a (`CommunicationsModule.cs:253-285`) | n/a | **NOT SCOPED** |

## Party (bulk jobs touching policies)

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /party/bulk-jobs/policy-exports | BulkJobCreate | new POLICY_EXPORT job over every General, Life and Health policy and membership | each row has `product_code`; policies have their node | §4 (scoped records) and OQ-172 (counts across products for unrestricted only): either rows in scope or unrestricted only | nothing: count from `IPolicyExportRows.All(tenantId)`, whole tenant, `Party/Insys.Party.Application/BulkJobs.cs:207-230` | none found | **MISSING** |
| POST /party/bulk-jobs/{id}/approve (POLICY_EXPORT job) | BulkJobApprove | the export's approval (maker-checker in Party, not Workflow) | as above | §4 approvals: the checker covers what the export holds | none: `BulkJobs.cs:232-253` | none found | **MISSING** |
| POST /party/bulk-jobs/imports; …/exports | BulkJobCreate | party import or export | no | not scoped (PTY-003) | n/a (`BulkJobs.cs:115-205`) | n/a | **NOT SCOPED** |
| Every other Party write (register, update, addresses, contact points, bank accounts, identifiers, relationships, roles, death, match resolution, anonymise, consents, merge, data-subject requests, due diligence, search-log holds, portal registration and DSR) | party.* | party and its data | no | not scoped (§1, PTY-003) | n/a (`Party/Insys.Party.Api/PartyModule.cs:164-650`) | n/a | **NOT SCOPED** |

## Compliance

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /compliance/report-runs | ReportCreate | new report run with stored tenant-wide metric totals | the facts do (product, branch); the totals span products | OQ-172: totals across products for unrestricted only | catalogue roles only; totals from `IProjectionTotals.Total` with no scope `Compliance/Insys.Compliance.Application/Compliance.cs:363-374` | none found | **MISSING** |
| POST /compliance/returns; POST /compliance/returns/{id}/correct | ReturnCreate / ReturnUpdate | regulatory return, values calculated | `metric:` fields are cross-product projection totals; `account:` fields are ledger movements | OQ-172 for metric fields; OQ-170 (Finance not scoped) for account fields | none: `ReturnValues.Calculate` `Compliance.cs:186-206`, called `Compliance.cs:223-270, 341` (correct) | none found | **MISSING** (metric fields) |
| POST /compliance/returns/{id}/review \| approve \| submit \| accept \| reject | ReturnUpdate / ReturnApprove | stored return | no product or node on it | unclear | none (`Compliance.cs:276-310`) | n/a | **UNCLEAR** |

## Integration

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /integration/migration-batches/{code}/reconciliation | MigrationReconcile | report of a batch's loaded counts and totals (General, Life, Health, Claims) | totals across products | OQ-172 (inferred to apply): unrestricted only | none: `IMigrationTotals.For` per module, `Integration/Insys.Integration.Application/MigrationReconciliation.cs:62-86` | none found | **MISSING** |
| POST /integration/disclosure-acknowledgements | DisclosureCreate | a party's acknowledgement, optionally naming a business record | the named record would know it | unclear | no check of the named record: `Integration/Insys.Integration.Application/Disclosures.cs:42,72-88` | none found | **UNCLEAR** |
| POST /portal/disclosure-acknowledgements | DisclosureCreate (member portal) | own acknowledgement | relationship (self, `Disclosures.cs:74`) | OQ-175 | n/a | n/a | **NOT SCOPED** |
| POST /integration/dead-letters/{id}/replay \| discard; POST /integration/parked-deliveries/{id}/retry; …/retry (by code); …/{id}/discard; POST /integration/webhook-deliveries/{id}/retry | DeadLetterUpdate / WebhookUpdate | operational delivery records carrying events about policies, claims… | not on the record | unclear | none (`DeadLetters/Commands.cs:74,111`; `Parking/ParkedDeliveries.cs:211,224`; `Webhooks/Webhooks.cs:168`) | n/a | **UNCLEAR** |
| PUT /integration/parking-setting | ParkingSettingUpdate | configuration | no | not scoped | n/a | n/a | **NOT SCOPED** |

## Assist

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /assist/explanations/premiums | ExplainPremium (also calculation.read) | new explanation of a recorded calculation | product yes; no node | OQ-168: product (branch once built) | 404 through Rating's scoped read: `Assist/Insys.Assist.Application/Explanations.cs:190-193`; `Rating/Insys.Rating.Application/Calculations.cs:245-250` (`ScopeFor(calculation.read).Allows(product, null)`) | `AssistExplanationTests.AUserRefusedTheCalculation_IsRefusedItsExplanation_TheSameWay_AndGetsNoStoredOne_AIA003_F103` | **OK** |
| POST /assist/explanations/refusals | ExplainRefusal | error-catalogue wording | record named but never read | not scoped | n/a | n/a | **NOT SCOPED** |
| PUT /assist/settings | SettingsManage | tenant settings | no | not scoped (configuration) | n/a | n/a | **NOT SCOPED** |
| POST /assist/explanations/{id}/feedback | signed-in user | feedback on an explanation shown to the caller | relationship (own) | not scoped | `ExplanationShownTo` else 404 `Explanations.cs:105-108` | n/a | **NOT SCOPED** |

## Analytics

| Route (METHOD path) | Permission | Record (and what it's about) | Knows product? / node? (how) | Scope it should apply (§4 / OQ-nnn / not scoped) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /analytics/projections/rebuild | ProjectionUpdate | clears and rebuilds every fact of the tenant | facts have product and branch | unclear (a tenant-wide maintenance action) | none (`Analytics/Insys.Analytics.Application/Projections.cs:201`) | n/a | **UNCLEAR** |

## (a) MISSING and PARTIAL
- Health POST /health/claims (staff): the claim's membership is never loaded, so a claim is created on any membership (`Care.cs:487-516`).
- Health POST /health/migration/memberships: no `PlaceNew` or product check, and no node set (`MembershipMigration.cs:146-153`).
- Health approvals HEALTH_RECOVERY and HEALTH_CREDIT_NOTE: no scope source, so a scoped checker decides what only an unrestricted maker may raise (`HealthInfrastructure.cs:84`).
- ClaimsFramework POST /claims/claims: product and node come from the request unchecked; a repeat returns an out-of-scope registration (`Claims.cs:340-354`).
- ClaimsFramework fraud cases (open, assign, conclude): not scoped through their subject (OQ-170, OQ-176) (`FraudCases.cs:86-125`).
- Documents generate, store, deliver, document legal hold place and release, dispose: not scoped through the business record (OQ-169) (`Documents.cs:253-480`).
- Documents hold-release approvals (both types): no scope source (`DocumentStores.cs:256`).
- Party policy export, request and approve: tenant-wide, scope never applied (`BulkJobs.cs:207-253`).
- Compliance report runs and return metric fields: cross-product totals with no unrestricted check (OQ-172) (`Compliance.cs:186-206,363-374`).
- Integration migration reconciliation: cross-product totals with no unrestricted check (`MigrationReconciliation.cs:62-86`).

## (b) UNCLEAR
- Health schemes (create, renew, terminate): no product or node, and §1 doesn't list them.
- Health provider quality reviews and statements (submit, import, resolve a line): provider-level; lines name claims of any product.
- Compliance return review, approve, submit, accept, reject: does acting on a stored cross-product return count as reading its totals (OQ-172)?
- Integration disclosure acknowledgement naming a business record: it is a party record, but it unblocks that record's issue without checking its scope.
- Integration dead-letter, parked-delivery and webhook retry and discard: operational records with scoped event data; no OQ covers them.
- Analytics projection rebuild: a tenant-wide rewrite of facts across products; no OQ covers it.

## (c) Inferred
- Portal users are assumed to have no product or node grants, so `CurrentScope` is unrestricted for them and the scoped `MembershipByNumber` in `PortalPatientOf` doesn't narrow them beyond relationship (OQ-175). Assignment data was not checked.
- An empty Health payment run (POST /payment-runs) has nothing to scope until calculated.
- Health recoveries and credit notes being unrestricted-only is read as "fail closed" (OQ-176).
- The Claims fraud case's product and node are derivable from the claim register by claim number or policy reference; not verified.
- OQ-172 is taken to apply to report-run, return and migration-reconciliation totals produced by commands.
- Workflow checks a checker's scope against `CurrentScope`, the decide route's permission (`Approvals.cs:213`), not the permission of the record the approval is about, as §4 says. This is Workflow's code, so the other sweep owns it.
- Assist's premium explanation: `Allows(product, null)` refuses every branch-scoped user (no node on calculations). Under OQ-168 ("product only until that's built") a node scope arguably shouldn't refuse yet. That is Rating's choice.

