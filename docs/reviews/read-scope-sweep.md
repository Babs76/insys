# Read-scope sweep (F103, addenda/access-scopes.md §1 and §4)

**At `main` commit `06ecae1`, 15 November 2026. Read-only: nothing was changed by the sweep.** Asked by Ade on 15 November: every module's record reads (single, list, and the Contracts query interfaces other modules call) checked for records that know their product, branch or relationship, and whether each applies the reader's scope for its permission.

## How it was done
- Three read-only research passes over the code, one per group of modules, each told to verify in code, cite file and line, name a test only if seen, and mark inference. Their tables follow as written, under "Pass A", "Pass B" and "Pass C".
- Every MISSING and PARTIAL row was then re-checked in the code by the main session, except the Rules rows (inferred by the pass). No test was run for the sweep.
- **Verdicts:** OK, MISSING (knows its scope, applies none), PARTIAL (product only, single but not list, filtered after the page is cut, or another permission's scope), NOT SCOPED (by §1), UNCLEAR (the addendum doesn't settle it; these became OQ-168 to OQ-177).

## Summary
- About 75 rows already apply the scope §1 requires, mostly in General, Life, Health, ClaimsFramework, Billing and Payments.
- **MISSING (11 rows):** Health accumulators; Party's policy export; Workflow approvals of types with no scope source (non-renewal, refund, case redress, legal-hold release, Health credit note and recovery), which also skip the scope on deciding; Workflow processes and tasks; documents attached to a record; Billing's `IAllocationPlanner` on payment allocation; Rules decisions (inferred).
- **PARTIAL (7 rows):** filtered after the page is cut (`GET /general/renewals`, `GET /health/payment-runs`, `GET /documents/legal-holds`); the decidable approvals list uses `approval.read`'s scope, not `approval.approve`'s; `GET /portal/health/claims` (relationship only, unlike the single read); `GET /billing/refunds/{id}` on an account with no schedules; Rating calculations, refused to branch-limited readers after batch 7.3.
- **UNCLEAR:** decided by Ade as OQ-168 to OQ-177 (`docs/decisions/open-questions.md`).

## Ade's decisions (15 November 2026)
| OQ | Decision |
|---|---|
| OQ-168 | Calculations and rule decisions take their subject's branch. Product only until that's built. |
| OQ-169 | Documents and holds attached to a record are its child records. Unattached documents keep classification only. |
| OQ-170 | Rule decisions, fraud cases and reconciliation exceptions are scoped through the record they reference. Finance journals aren't scoped. |
| OQ-171 | Reinsurance cessions and recoveries are scoped by product. Bordereaux aren't scoped. |
| OQ-172 | Reference records are visible. Totals across products (projection totals, compliance returns, report runs, aggregation alerts) are for unrestricted readers only. |
| OQ-173 | Changes and approvals of products are scoped. Reading product definitions is not. |
| OQ-174 | Commission entitlements, recoveries and statements are payouts: not scoped. |
| OQ-175 | Portal users are scoped by relationship only, the same on every portal route. |
| OQ-176 | Fail closed. Cases and approvals get a scope source from their subject. |
| OQ-177 | Follow §4: 404 for one record, filtering for lists. |

**The order of fixes (Ade):** the Rating regression first (product only, OQ-168); then a write sweep; then, one batch each, the unscoped approval types and the writes, the Health accumulators, the policy export, Workflow processes and tasks and documents (OQ-169), and the paging faults and other partial rows. Each read is filtered in the query, never after the page is cut.

## Pass A: General, Life, Rating, ProductFactory, Rules, Distribution

Commit `06ecae1` (origin/main). Paths are relative to `src/Modules/` unless they start with `tests/` or `db/`.
How the building block works (`BuildingBlocks.Application/Context/AccessScopes.cs`, `ExecutionScope.cs:111-121`):
- `execution.InScope(record, product, node)` checks `CurrentScope`, which is `ScopeFor(RequestPermission)`. For a read it returns null, so the route answers 404.
- `ScopeFor(p)` is Unrestricted for a service actor, and `None` when the caller doesn't hold `p`.
- `PermissionGrant.Allows` treats a null node as outside any grant that has `OrgNodes`, and a null product as outside any grant that has `Products`.

### General

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /general/quotes/{id} | quote.read (`GeneralPermissions.QuoteRead`) | General quote | product yes / node yes (`ProductCode`, `OrgNodeCode` on the quote) | §1: product + node | `GeneralRepository.Quote` -> `Scoped` (`General/Insys.General.Infrastructure/Persistence/GeneralStores.cs:52-53,110`) | `tests/Insys.EndToEndTests/GeneralProcessTests.cs:232` `ScopedAssignments_SeeAndActOnlyWithinTheirOrganisationAndProduct_F103` | OK |
| GET /general/quotes | QuoteRead | quote list | same | §1: product + node | `ScopeFilter.InScope` in the query (`GeneralLists.cs:42`) | same test (`GeneralProcessTests.cs:250-253,268`) | OK |
| GET /general/proposals/{id} | QuoteRead | proposal | product / node through its quote | §1: product + node (child inherits) | `OfQuote` loads the quote's product and node -> `InScope` (`GeneralStores.cs:58-59,98-108`) | none found for the single read | OK |
| GET /general/proposals | QuoteRead | proposal list | through its quote | §1 | quotes subquery `InScope` (`GeneralLists.cs:144-145`) | `tests/Insys.IntegrationTests/GeneralListsTests.cs:92` `Proposals_FilterByQuoteAndStatus_ThroughTheirQuotesScope_BR034` | OK |
| GET /general/policies/{id} | policy.read (`PolicyRead`) | policy | product / node on the policy | §1 | `Policy` -> `Scoped` (`GeneralStores.cs:72-73,112`) | none found for General policy single read (General F103 E2E covers quotes) | OK |
| GET /general/policies | PolicyRead | policy list | same | §1 | `InScope` (`GeneralLists.cs:66`) | none found | OK |
| GET /general/policies/{id}/endorsements | endorsement.read | endorsements | through the policy | §1 (child) | handler loads the scoped policy first, 404 otherwise (`General/Insys.General.Application/Lists.cs:110-114`); the list itself is unfiltered (`GeneralLists.cs:164`), which is fine because it is pinned to that policy | `GeneralListsTests.cs:107` `APolicysEndorsements_..._AndAPolicyOutsideScopeIsNotFound_BR034` | OK |
| GET /general/endorsements/{id} | endorsement.read | endorsement | through the policy | §1 (child) | `Endorsement` -> owner policy's product and node -> `InScope` (`GeneralStores.cs:81-89`) | none found | OK |
| GET /general/claims/{id} | claim.read | General claim | product / node on the claim | §1 | `Claim` -> `Scoped` (`GeneralStores.cs:93-94,114`) | none found for /general (`tests/Insys.IntegrationTests/ClaimsScopeTests.cs:46` covers /claims/claims, another module) | OK |
| GET /general/claims | ClaimRead | claim list | same | §1 | `InScope` (`GeneralLists.cs:89`) | `GeneralListsTests.cs:142` (claim register, within scope) | OK |
| GET /general/claims/{id}/assessments, GET /general/assessments, GET /general/assessments/{id} | ClaimRead | loss assessment | through the claim | §1 (child) | list: claims subquery `InScope` (`GeneralLists.cs:177-178`), plus a 404 for an out-of-scope claim (`Lists.cs:116-119`); single: loads the scoped claim (`ClaimHandling.cs:527-532`) | `GeneralListsTests.cs:124` `Assessments_..._OnlyOnClaimsInScope_BR034` | OK |
| GET /general/claims/{id}/recoveries, GET /general/recoveries/{id} | ClaimRead | recovery case | through the claim | §1 (child) | the scoped claim is loaded first (`ClaimHandling.cs:646-660`); the list is pinned to the claim (`GeneralLists.cs:202`) | `tests/Insys.IntegrationTests/ClaimRecoveriesTests.cs:53` `AClaimsRecoveries_AreListedOnceEach_AndOnlyWithinScope_BR032` | OK |
| GET /general/renewals/{id} | PolicyRead | renewal | through the policy | §1 (child) | `Renewal` requires the scoped `Policy` (`GeneralStores.cs:272-275`) | none found | OK |
| GET /general/renewals | PolicyRead | renewal list | through the policy | §1: filtered in the query so paging holds | filtered after `Take(limit)`, one `Policy()` load per row (`GeneralStores.cs:327-342`). A page with out-of-scope rows comes back short, and `Page` returns no cursor when `items.Count < size` (`General/Insys.General.Api/GeneralModule.cs:663`), so later in-scope renewals are never reached | none found | PARTIAL |
| GET /general/retention-cases/{id} | retention.read (`RetentionRead`) | retention case | through the policy (it also stores `ProductCode`) | §1 (child of policy) | `RetentionCase` requires the scoped `Policy` (`GeneralStores.cs:293-296`) | none found | OK |
| GET /general/retention-cases | RetentionRead | retention list | through the policy | §1 | policies subquery `InScope` (`GeneralStores.cs:304-305`) | none found | OK |
| GET /general/risk-objects/{id} | risk.read (`RiskRead`) | risk object | no product, no node; it has an owner party and can be attached to several policies | not in §1's list (it is neither a party nor a policy record) | nothing (`GeneralStores.cs:50`, `Underwriting.cs:262-265`) | none found | UNCLEAR |
| GET /general/risk-objects | RiskRead | risk object list | same | unclear | unfiltered, except that `policyId` must be an in-scope policy (`GeneralLists.cs:110-138`, scope at :130) | `GeneralListsTests.cs:69` `RiskObjects_..._ThePolicyWithinScope_BR034` (policy filter only) | UNCLEAR |
| GET /general/aggregation-alerts, GET /general/aggregation-alerts/{id} | aggregation_alert.read | aggregation alert (zone and peril exposure) | no product, no node; it covers all products in the tenant | not in §1 | nothing (`General/Insys.General.Infrastructure/Persistence/GeneralDbContext.cs:471,514`); exposure is tenant-wide by design (`GeneralStores.cs:213`) | none found | UNCLEAR |
| GET /portal/general/policies, GET /portal/general/policies/{id}, GET /portal/general/policies/{id}/loss-codes | PolicyRead (loss codes: ClaimRead); party, broker | policy | product / node; relationship: policyholder or intermediary codes | relationship + F103 | list: `InScope` + holder or broker codes (`GeneralLists.cs:239-252`); single: scoped `Policy` + `Related.Policy` (`Portal.cs:206-209,276-281`) | relationship: `tests/Insys.EndToEndTests/GeneralProcessTests.cs:1110,1149,1570`, `tests/Insys.IntegrationTests/PortalListTests.cs:165,189`; F103 on portal: none found | OK |
| GET /portal/general/quotes, GET /portal/general/quotes/{id} | QuoteRead; broker | quote | product / node; relationship: intermediary code | relationship + F103 | `InScope` + codes (`GeneralLists.cs:255-266`); scoped `Quote` + `Related.Quote` (`Portal.cs:215-218`) | none found | OK |
| GET /portal/general/claims, GET /portal/general/claims/{id} | ClaimRead; party, broker | claim | product / node; relationship through the policy | relationship + F103 | `InScope` + through the policy (`GeneralLists.cs:269-282`); scoped claim and policy + `Related.Policy` (`Portal.cs:220-224`) | `GeneralProcessTests.cs:1111`, `tests/Insys.IntegrationTests/CaseTests.cs:329` (relationship) | OK |
| GET /portal/general/renewals, GET /portal/general/renewals/{id} | PolicyRead; party, broker | renewal | through the policy; relationship | relationship + F103 | policies `InScope` + relationship inside the query (`GeneralLists.cs:221-236`); `Visible` (`RenewalResponses.cs:221-232`) | none found | OK |
| GET /portal/general/risk-objects, GET /portal/general/risk-objects/{id} | RiskRead; broker | risk object | relationship: the broker currently services the owner (BR-092) | relationship (F103 unclear, as for the staff rows) | `CurrentlyServices` (`Portal.cs:260-270,306-318`; `GeneralLists.cs:284-308`) | `GeneralProcessTests.cs:1492,1497,1518` | OK (relationship) |
| Contract `IGeneralClaimRecipients.ForClaim` | none (called from a Communications trigger) | claim's notice recipient | claim | inferred: event-driven consumer, runs as the service, unrestricted | own data source, tenant given (`ClaimRecipients.cs:36`) | n/a | NOT SCOPED (by §1) (inferred: not on a caller request) |
| Contract `IPolicyDistribution.For` (General impl) | none (called by Distribution, Analytics and Reinsurance) | policy distribution | product / node returned | inferred: called from consumers and jobs | own data source (`General/Insys.General.Infrastructure/Persistence/PolicyDistribution.cs:35`) | n/a | NOT SCOPED (by §1) (inferred: consumers) |

### Life

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /life/proposals/{id} | proposal.read (`LifePermissions.ProposalRead`) | Life proposal | product / node on the proposal | §1 | `Proposal` -> `Scoped` (`Life/Insys.Life.Infrastructure/Persistence/LifeStores.cs:54-56,151`) | `tests/Insys.EndToEndTests/LifeProcessTests.cs:709` `ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103` | OK |
| GET /life/proposals | ProposalRead | proposal list | same | §1 | `InScope` (`LifeLists.cs:42`) | same test | OK |
| GET /life/proposals/{id}/underwriting-case, GET /life/underwriting-cases/{id} | UnderwritingRead | underwriting case | through the proposal | §1 (child) | `OfProposal` -> `InScope` (`LifeStores.cs:58-63,159-169`) | none found | OK |
| GET /life/underwriting-cases | UnderwritingRead | case list | through the proposal | §1 | join to in-scope proposals (`LifeLists.cs:106-127`) | none found | OK |
| GET /life/policies/{id} | PolicyRead | Life policy | product / node | §1 | `Policy` -> `Scoped` (`LifeStores.cs:71,153`) | `LifeProcessTests.cs:709` (404 for the policy in branch B) | OK |
| GET /life/policies | PolicyRead | policy list | same | §1 | `InScope` (`LifeLists.cs:54`) | same test | OK |
| GET /life/loans/{id} | LoanRead | policy loan | through the policy | §1 (child) | owner policy -> `InScope` (`LifeStores.cs:81-91`) | none found | OK |
| GET /life/loans | LoanRead | loan list | through the policy | §1 | join to in-scope policies (`LifeLists.cs:92-103`) | none found | OK |
| GET /life/claims/{id} | ClaimRead | Life claim | product / node | §1 | `Claim` -> `Scoped` (`LifeStores.cs:108,155`) | none found | OK |
| GET /life/claims | ClaimRead | claim list | same | §1 | `InScope` (`LifeLists.cs:68`) | none found | OK |
| GET /life/group-schemes/{id}, GET /life/group-schemes | SchemeRead | group scheme | product / node on the scheme | inferred: a §1 root record (it carries its own product and node) | `Scheme` -> `Scoped` (`LifeStores.cs:118-119,157`); list `InScope` (`LifeLists.cs:81`) | none found | OK |
| GET /portal/life/policies, GET /portal/life/policies/{id} | PolicyRead; party, broker | policy | product / node; relationship: holder or insured life, or broker codes | relationship + F103 | `InScope` + relationship (`LifeLists.cs:131-153`); scoped `Policy` + relationship (`Life/Insys.Life.Application/Portal.cs:82-90`) | none found for F103 on portal | OK |
| GET /portal/life/claims, GET /portal/life/claims/{id} | ClaimRead; party, broker | claim | product / node; relationship: claimant, holder, beneficiary, or broker through the policy | relationship + F103 | `InScope` + relationship (`LifeLists.cs:157-183`); scoped claim and policy + `Related` (`Portal.cs:132-137`) | `LifeProcessTests.cs:524` (outsider gets 404, relationship) | OK |
| Contracts `ILifeClaimRecipients`, `IPolicyDistribution` (Life impl) | none | n/a | n/a | inferred: consumers | own data source (`Life/Insys.Life.Infrastructure/Persistence/PolicyDistribution.cs:35`) | n/a | NOT SCOPED (by §1) (inferred) |

### Rating

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /rating/calculations/{id} | premium_calculation.read (`RatingPermissions.CalculationRead`) | premium calculation | product yes (`ProductCode`); node: none stored; the subject (quote, proposal or renewal) has one, but in another module | §1 doesn't list calculations. By §1's "child records inherit", the node should be the subject's: unclear | `ScopeFor(CalculationRead).Allows(view.ProductCode, null)` (`Rating/Insys.Rating.Application/Calculations.cs:246-251`) | `tests/Insys.IntegrationTests/AssistExplanationTests.cs:249` `AUserRefusedTheCalculation_IsRefusedItsExplanation_TheSameWay_AndGetsNoStoredOne_AIA003_F103` (product scope only) | UNCLEAR (product: OK; node: see note) |
| GET /rating/calculations?subjectType&subjectId | CalculationRead | calculation list | same | same | the same `Allows(product, null)`, applied after the store's `Take` (`Calculations.cs:254-262`). Safe for products because one subject has one product (all of a page or none) | none found | UNCLEAR (as above) |
| Contract `IRecordedCalculations.Get` (called by Assist `Explainer`, `Assist/Insys.Assist.Application/Explanations.cs:190`) | the caller's (Assist's), but it checks `ScopeFor(CalculationRead)`, not the caller's `CurrentScope` | calculation | as above | the reader's scope for premium_calculation.read (AIA-003) | dispatches `GetCalculation` (`Calculations.cs:325-331`), so the same check applies. A caller without premium_calculation.read gets `AccessScope.None`, so null and then 404 (`ExecutionScope.cs:111-114`) | `AssistExplanationTests.cs:249` | UNCLEAR (as above) |
| Contracts `IProductTaxRates`, `IPremiumSimulator` | n/a | configuration and dry runs | n/a | not scoped (config, no record) | n/a | n/a | NOT SCOPED (by §1) |

**Rating note: the null org node.** `Allows(product, null)` (`AccessScopes.cs:39-40`) fails every grant that has `OrgNodes`. So for a **branch-scoped** reader, meaning every premium_calculation.read grant they hold has `scope_branches`:
- `GET /rating/calculations/{id}` answers 404 for **every** calculation, including those of quotes and proposals in the reader's own branch.
- `GET /rating/calculations` returns an empty list.
- Assist's premium explanation is refused (404) for every calculation.

It doesn't help that the reader can open the subject quote. Only a reader who also holds a grant without a branch scope (it may still have a product scope) sees calculations. This is consistent with the building block's rule that a null node is outside a node scope, and with "ALL" in analytics (`tests/Insys.EndToEndTests/EnterpriseProcessTests.cs:358-363`). But it probably isn't intended for branch underwriters, so it is raised as a question below. No test covers a branch-scoped calculation reader (none found).

### ProductFactory

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /products, GET /products/{productCode} | product.read (`ProductPermissions.ProductRead`) | product definition | it is the product; no node | §1 doesn't list product definitions (configuration). Whether a product-scoped role sees only its products' definitions is unclear | nothing (`ProductFactory/Insys.ProductFactory.Application/Products.cs:434,476`) | none found | UNCLEAR |
| GET /products/{code}/versions, GET /products/{code}/versions/{n} | ProductRead | product version | product | same | nothing (`Products.cs:418,431`) | none found | UNCLEAR |
| GET /products/{code}/versions/{n}/simulations | ProductRead | simulation run | product | same | nothing (`Products.cs:482`) | none found | UNCLEAR |
| Contracts `ISellableProducts`, `IPublishedProducts`, `IProductDomains`, `IExclusionCodes` | n/a | product configuration | product | configuration | n/a (`ProductFactory/Insys.ProductFactory.Contracts/SellableProducts.cs:47-84`) | n/a | NOT SCOPED (by §1) |

### Rules

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /rules/decisions/{id} | rule_decision.read (`RulesPermissions.DecisionRead`) | rule decision (`rules.decision_log`) | product: only implicitly. For an underwriting rule set, `ruleset_code` is the product code (`Rules/Insys.Rules.Application/Decisions.cs:62`); an eligibility rule set's code is not. The kind isn't stored (`db/migrations/V0001__core_schema.sql:867-881`). Node: none; the subject (a Life proposal or underwriting case) has one | §1 doesn't list decisions. By analogy with approvals, they "follow the record they are about" (the subject). Unclear | nothing (`Decisions.cs:115-119`, `Rules/Insys.Rules.Infrastructure/Persistence/RulesStores.cs:47`) | none found | MISSING (inferred, for underwriting decisions) / UNCLEAR (node, eligibility) |
| GET /rules/decisions?subjectType&subjectId | DecisionRead | decision list | same | same | nothing (`Decisions.cs:121-125`, `RulesStores.cs:51`) | none found | MISSING (inferred) / UNCLEAR |
| Contract `IUnderwritingAuthorities` | n/a | authority matrix (configuration) | product | configuration | n/a (`Rules/Insys.Rules.Contracts/RulesContracts.cs:63`) | n/a | NOT SCOPED (by §1) |

### Distribution

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /distribution/intermediaries/{code}/permissions/{productCode} | intermediary.read | intermediary register and permissions (configuration) | product given in the request | configuration | nothing (`Distribution/Insys.Distribution.Application/Commission.cs:304`) | n/a | NOT SCOPED (by §1) |
| GET /distribution/entitlements/{id}, GET /distribution/entitlements | commission.read (`DistributionPermissions.CommissionRead`) | commission entitlement | product yes (`product_code`, nullable, `db/migrations/V0021__phase6_enterprise.sql:51-53`); `policy_ref`; no node; relationship: intermediary code | §1 doesn't list entitlements. The "commission payouts" addition puts them outside branch scope, read with commission_payout.read, but it covers payouts (Payments), not entitlements | nothing (`Commission.cs:288-293`; `Distribution/Insys.Distribution.Infrastructure/Persistence/DistributionStores.cs:50,122`) | none found | UNCLEAR |
| GET /distribution/commission-recoveries | CommissionRead | commission recovery | product: no; `policy_ref` and entitlement; relationship: intermediary | same as entitlements | nothing (`Commission.cs:257-261`, `DistributionStores.cs:86`) | none found | UNCLEAR |
| GET /distribution/statements, GET /distribution/statements/{id} | statement.read (`StatementRead`) | commission statement | no product (it spans products), no node; relationship: intermediary | inferred: a commission payout's basis, so outside branch scope | nothing (`Commission.cs:295-302`, `DistributionStores.cs:95,154`) | none found | NOT SCOPED (by §1) (inferred: "commission payouts") |
| GET /portal/distribution/statements, GET /portal/distribution/statements/{id} | StatementRead; broker | commission statement | relationship: the broker's codes, sub-brokers included; drafts hidden | relationship | list: `codes.Contains` and not Draft (`DistributionStores.cs:344-354`); single: `Who.Brokers(...)` and not Draft (`Distribution/Insys.Distribution.Application/Portal.cs:65-69`) | none found | OK (relationship) |
| Contract `IIntermediaries` | n/a | register | n/a | configuration | n/a | n/a | NOT SCOPED (by §1) |

### (a) MISSING / PARTIAL
- General `GET /general/renewals`: scope is applied after `Take(limit)` (`GeneralStores.cs:327-342`). Pages come back short, and the cursor stops early (`GeneralModule.cs:663`), so in-scope renewals can be unreachable. PARTIAL.
- Rules `GET /rules/decisions/{id}`: no scope. Inferred: an underwriting decision knows its product through `ruleset_code` (`Decisions.cs:115-119`). MISSING.
- Rules `GET /rules/decisions`: no scope (`Decisions.cs:121-125`). MISSING (inferred, as above).

### (b) UNCLEAR: questions
- Rating: should a calculation inherit its subject's org node? Today, with a null node, a branch-scoped `premium_calculation.read` reader gets 404 on every calculation, an empty list, and a refused Assist explanation, even for its own branch's quotes (`Calculations.cs:249,261`).
- Rules: are rule decisions scoped by their subject (like approvals), and if so, where do the product and node come from? Eligibility rule sets don't carry the product, and the decision has no node or kind column.
- General risk objects: they are shared across policies and owned by a party. Are they unscoped like parties, or scoped through the policies they are attached to?
- General aggregation alerts: these are tenant-wide zone and peril exposure. Are they unscoped, or only for unrestricted readers?
- ProductFactory: does a product scope limit which product definitions, versions and simulation runs a product-scoped role can read?
- Distribution entitlements and commission recoveries: they know a product (entitlements) or a policy. Is product scope applied, or are they unscoped like commission payouts?
- Distribution statements: is a commission statement a "commission payout" for the 24 October addition? I treated it as NOT SCOPED.

### (c) Inferred
- Public query interfaces that take `tenantId` and read on their module's own data source (`IGeneralClaimRecipients`, `IPolicyDistribution`, `ILifeClaimRecipients`, `IUnderwritingAuthorities`, `IIntermediaries`, `ISellableProducts`): I assumed they are called from consumers and jobs (service actor, unrestricted), not on a caller's read. I didn't trace every caller.
- Life group schemes are treated as §1 root records because they carry their own product and node.
- An underwriting decision's `ruleset_code` equals the product code; this is taken from `EvaluateRuleSet`'s parameter doc (`Decisions.cs:62`).
- Commission statements are taken to fall under the "commission payouts" addition.
- Portal routes also apply F103 `CurrentScope` (the external user's role assignment) on top of the relationship. No test exercises F103 on a portal route (none found).

## Pass B: Health, ClaimsFramework, Billing, Payments, Finance, Reinsurance

Commit `06ecae1` (detached `origin/main`). Read-only. All paths are under `src/Modules/` unless they start with `src/BuildingBlocks`, `tests/` or `db/`.

**How the mechanism works here (checked).** `IModule.cs:179-183` (`src/BuildingBlocks/Insys.BuildingBlocks.Api`) sets the request's permission, and sets `isRead` only for HTTP GET or HEAD. `CurrentScope` is the scope of that permission (`ExecutionScope.cs:117-120`). So an `InScope` miss on a GET gives 404 even when the handler is dispatched as a command, as the `/clinical` routes are. A POST search is not a "read": the channel gate runs on it, and the scope is narrowed to the products the channel allows (`ExecutionScope.cs:119`, `IModule.cs:186-200`).

### Health

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /health/memberships; POST /health/memberships/search | membership.read (`HealthPermissions.MembershipRead`) | membership | product and node on the membership (`Membership.cs:321,342`) | §1: memberships by product and node | `query.InScope(...)` in the list query, `Health.Infrastructure/Persistence/HealthLists.cs:44` | `HealthProcessTests.ScopedAssignments_SeeOnlyMembershipsWithinTheirOrganisation_AndProductScopesReachNone_F103_OQ080` | **OK** |
| GET /health/memberships/{id} | MembershipRead | membership | as above | §1 | `HealthStores.cs:60-61`, which calls `Scoped`, which calls `execution.InScope` (`HealthStores.cs:474`) | same test; `HealthProcessTests.HealthPlans_AreSoldAsProducts_AndProductScopesReachTheirMemberships_OQ080` | **OK** |
| GET /health/memberships/{id}/accumulators | AccumulatorRead | benefit accumulators of a membership | via its membership (child record) | §1: inherits the membership's scope | nothing. `GetAccumulatorsHandler` (`Health.Application/Members.cs:434-439`) calls `MembershipCurrency` and `AccumulatorsOf` (`HealthStores.cs:113-117`), and neither loads the membership through `Scoped` | none found | **MISSING** |
| POST /health/eligibility-decisions/search | MembershipRead | eligibility decision | by member number, through the membership | §1 (child of the membership) | in-scope member numbers used as the filter, `HealthLists.cs:162-163` | none found | **OK** |
| GET /health/preauthorisations; GET /health/claims; GET /health/utilisation-cases | PreauthRead / ClaimRead / CaseRead | preauthorisation, medical claim, utilisation case | through `MembershipId` | §1: medical claims (and their kin) by the scheme's product and the node | `MembershipsInScope.Contains(...)` in the query, `HealthLists.cs:58,73,88` | none found | **OK** |
| GET /health/preauthorisations/{id}; GET /health/claims/{id}; GET /health/utilisation-cases/{id}; GET /health/preauthorisations/{id}/clinical (PreauthReview); GET /health/claims/{id}/clinical (ClinicalRead) | as listed | as above | through the membership | §1 | `OfMembership` then `InScope`, `HealthStores.cs:119-123,133-137,163-167,476-486` (the clinical handlers load through the same store: `Care.cs:291,461`) | none found | **OK** |
| GET /health/fraud-cases; GET /health/fraud-cases/{id} | CaseRead | Health fraud case | a CLAIM subject leads to the claim, then to the membership | follows its claim (Health's choice; §1 is silent on fraud cases) | `HealthLists.cs:101-105`; `HealthStores.cs:170-180`. Non-CLAIM subjects are unscoped, but only CLAIM is ever opened (`Care.cs:542`) | none found | **OK** |
| GET /health/complex-cases; GET /complex-cases/{id}; GET /complex-cases/{id}/clinical | ComplexCaseRead | complex case | through the membership | §1 (child of the membership) | `HealthStores.cs:293-309`; clinical through `ComplexCases.cs:305` | none found | **OK** |
| GET /health/referrals; GET /referrals/{id} (ReferralRead); GET /referrals/{id}/clinical (ClinicalRead) | as listed | clinical referral | through the membership | §1 | `HealthStores.cs:316-320,378-388`; clinical `ComplexCases.cs:441` | none found | **OK** |
| GET /health/chronic-registrations, /{id} (ChronicRead), /{id}/clinical (ClinicalRead) | as listed | chronic registration | through the membership | §1 | `HealthStores.cs:322-343`; clinical `ChronicRegistrations.cs:217` | none found | **OK** |
| GET /health/programme-enrolments, /{id} (ProgrammeRead), /{id}/clinical (ClinicalRead) | as listed | programme enrolment | through the membership | §1 | `HealthStores.cs:351-370`; clinical `ProgrammeEnrolments.cs:167` | none found | **OK** |
| GET /health/payment-runs/{id} | RunRead | provider payment run | product and node of each claim's membership | follows its claims (like the claim-payout addition) | `GetPaymentRunHandler`, `Runs.cs:228-249` (404 when nothing is visible, and a partial view otherwise) | `HealthProcessTests.HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009`; `HealthTests.PaymentRun_ShowsOnlyRemittancesWhoseClaimsAreAllInScope_F103` | **OK** |
| GET /health/payment-runs | RunRead | payment runs | as above | §4: filtered so that paging stays correct | filtered in memory **after** `Take(q.Limit)` (`HealthLists.cs:129-141`). A page that drops runs comes back short, and `Page` then gives no next cursor (`HealthModule.cs:599`), so later in-scope runs can't be reached | none found | **PARTIAL** (paging) |
| GET /health/remittances/{id} | RunRead | remittance | its run's claims, through their memberships | follows its claims | `ProviderRecoveries.cs:383-396` (`RunVisibility`) | none found | **OK** |
| GET /health/schemes; GET /health/schemes/{id} | SchemeRead | scheme | no product or node column; only memberships carry the plan's product | unclear: §1 scopes memberships "by the scheme's product", but doesn't say whether the scheme itself is scoped | none, deliberately (`HealthLists.cs:146-155` "BR-067 … not scoped"; `Members.cs:148-151`) | none found | **UNCLEAR**: schemes aren't in §1 and carry no product |
| GET /health/providers/{providerId}/measures; GET /health/providers/{providerId}/quality-reviews | ProviderRead | provider measures and reviews | provider-level, figures drawn from claims of every product | unclear | none (`ProviderMonitoring.cs:217-242`; `HealthStores.cs:251-257`) | none found | **UNCLEAR**: provider-level aggregates across products |
| GET /health/provider-statements; GET /health/provider-statements/{id} | ProviderRead | provider statement (lines name claim numbers, with each claim's recovery status) | provider; the lines reference claims of any product | unclear | none (`ProviderMonitoring.cs:311-317`; `HealthStores.cs:281-287,443-453`) | none found | **UNCLEAR**: provider-level, mixes products |
| GET /health/provider-recoveries, /{id}; GET /health/provider-credit-notes, /{id} | RecoveryRead / CreditNoteRead | provider recovery, credit note | provider-level across products | unclear | any restricted scope is refused with **403 OUT_OF_SCOPE**, on reads too (`ProviderRecoveries.cs:212-219,270-274`), not 404 or a filtered list | none found | **UNCLEAR**: blanket refusal instead of the §4 read and list rules |
| GET /portal/health/memberships; GET /portal/health/memberships/{id} | MembershipRead (member portal) | membership | relationship: principal or dependant party, plus product and node | relationship, and scope | `HealthLists.cs:177-188` (scope and party filter); `Portal.cs:159-163` (scoped store and party check) | `HealthProcessTests.ExternalUsers_ProvidersAndMembersSeeOnlyTheirOwn_ExternalUserAccess` | **OK** |
| GET /portal/health/claims/{id}; GET /portal/health/preauthorisations/{id} | ClaimRead / PreauthRead (portal) | claim, preauthorisation | relationship: the provider's own; or the member as patient or principal | relationship, and scope | scoped store (`HealthStores.cs:119-137`) and relationship check (`Portal.cs:174-193,216-219`) | the ExternalUsers test above (relationship) | **OK** |
| GET /portal/health/claims | ClaimRead (portal) | claims | relationship | relationship, and scope as for the single read | relationship only: `ProviderClaims` and `MemberClaims` (`HealthLists.cs:190-210`) apply no `InScope`, while the single read does | the ExternalUsers test above (relationship only) | **PARTIAL** (list and single read differ) |
| GET /portal/health/chronic-registrations | ChronicRead (member) | chronic registrations | relationship: patient or principal | relationship | `MemberChronicRegistrations`, `HealthStores.cs:345-347` (relationship only) | none found | **OK** (relationship; see question b6) |
| GET /portal/health/referrals; GET /portal/health/referrals/{id}/clinical | ReferralRead (provider) | referral | relationship: referring or referred provider, plus scope | relationship, and scope | `ClinicalReferrals(… providerId …)` with `MembershipsInScope` (`HealthStores.cs:378-388`); clinical `ComplexCases.cs:460` (`Concerns(provider)`) | none found | **OK** |
| GET /portal/health/remittances, /remittances/{id}; GET /portal/health/recoveries; GET /portal/health/credit-notes | RunRead / RecoveryRead / CreditNoteRead (provider) | remittances, recoveries, credit notes | relationship: the provider's own | relationship | provider filter only: `HealthLists.cs:213-223`; `ProviderRecoveries.cs:370-381,398` | none found | **OK** (relationship; see question b6) |
| Contracts `IProviderDirectory` | (none: service call) | provider register entry | static register | not scoped (static data) | `HealthPaymentScopes.cs` `ProviderDirectory` | n/a | **NOT SCOPED (by §1)** |
| Contracts `IHealthNoticeRecipients` | (none: called by Communications consumers and Health's own handlers) | the notice's recipient party | party | not scoped (parties) | `NoticeRecipients.cs:42` | n/a | **NOT SCOPED (by §1)** |

### ClaimsFramework

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /claims/claims (register search) | ClaimRead | claim registration | product and node recorded at registration | §1: claims | `InScope` in the query, `ClaimsFramework.Infrastructure/Persistence/ClaimsStores.cs:51` | none found (the scope test covers only the single reads) | **OK** |
| GET /claims/claims/{claimNumber}; /communications (ClaimRead); /financials (FinancialsRead) | as listed | claim, its correspondence log, its movements | as above | §1 | `ByNumber`, which calls `InScope` (`ClaimsStores.cs:100-104`); handlers go through `Loaded.Claim` (`Claims.cs:333-334,660-691`) | `ClaimsScopeTests.RegisteredClaim_IsReadOnlyWithinTheProductAndNodeItWasRegisteredWith_F103` | **OK** |
| GET /claims/claims/{claimNumber}/payment-instructions | InstructionRead | claim payment instruction | follows its claim | §1: claim payment instructions by the claim's product | claim loaded in scope first (`Claims.cs:697-702`), then its instructions (`ClaimsStores.cs:118`) | the `ClaimsScopeTests` test above | **OK** |
| GET /claims/payment-instructions/{id} | InstructionRead | instruction | follows its claim | §1 | `OfClaim`, which calls `InScope` (`ClaimsStores.cs:121-122,157-169`) | none found | **OK** |
| GET /claims/fraud-cases; GET /claims/fraud-cases/{id} | FraudCaseRead | Life or General fraud case | domain plus claim number or policy reference (`FraudCase.cs:56-60`); product derivable (inferred) | unclear | none (`ClaimsStores.cs:88-94`; `FraudCases.cs:133-149`) | none found | **UNCLEAR**: §1 is silent on fraud cases; Health's fraud cases follow their claim, these don't |
| GET /claims/fraud-indicators | ClaimUpdate | fraud indicator definitions | configuration | not scoped (static data) | none (`Claims.cs:177-184`) | n/a | **NOT SCOPED (by §1)** |
| Contracts `IClaimFraudReferrals.ReferredClaims` | (caller's request: General `ReviewRenewal`) | count of a policy's referred claims | the policy | follows the policy | applies nothing itself (`ClaimDecisionReasons.cs:47`); the caller loads the policy through General's scoped store first (`General.Application/RenewalReview.cs:110`) | none found | **OK** (inferred: scoped by the caller's load) |
| Contracts `IClaimDecisionReasons` | n/a | reason codes | static data | not scoped | n/a | n/a | **NOT SCOPED (by §1)** |

(`IClaimRegistry` only writes; `ClaimsRecordScopes` is itself a source of scopes, `ClaimsRecordScopes.cs:38-51`.)

### Billing

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /billing/accounts | AccountRead | billing account | through its premium schedules' product and node | §1, OQ-081: listed if any part is in scope, showing that part only | `Billing.Infrastructure/Persistence/BillingStores.cs:93-97` (in query); `AccountView.From` trims schedules and arrears (`Billing.cs:140-147`) | none found | **OK** |
| GET /billing/accounts/{id} | AccountRead | account | as above | OQ-081 | `BillingStores.cs:50-57`; `Billing.cs:577-591` (refunds only when the whole account is in scope) | `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103` | **OK** |
| GET /billing/accounts/{id}/invoices | InvoiceRead | invoices | each invoice's product and node | §1 | account checked first, then each invoice filtered (`Billing.cs:610-619`; the list isn't paged) | `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103` | **OK** |
| GET /billing/invoices/{id} | InvoiceRead | invoice or credit note | product and node on the invoice | §1 | `InScope`, `BillingStores.cs:60-64` | same Life test | **OK** |
| GET /billing/refunds | RefundRead | refund | the whole account's schedules | OQ-081: whole account | `BillingStores.cs:125-131` (every schedule in scope, and at least one schedule) | none found | **OK** |
| GET /billing/refunds/{id} | RefundRead | refund | as above | OQ-081 | `BillingStores.cs:74-87`: `count == countInScope`, which is true when the account has **no** schedules, so any scoped user sees the refund while the list (`:130` `Any`) and Payments (`Payments.cs:710`, `Count > 0`) hide it | none found | **PARTIAL** (edge case: an account with no schedules) |
| GET /billing/summary | AccountRead | OPEN_ARREARS figure | the accounts' schedules | OQ-081: accounts wholly in scope only | `Billing.cs:173` (`AccountScopes.Whole`) | none found | **OK** |
| GET /portal/billing/invoices; GET /portal/billing/invoices/{id} | InvoiceRead (member, broker) | invoice | relationship: the payer, or the broker's policies; plus product and node | relationship, and scope | `BillingPortal.cs:39-56` (`InScope` and relationship); `Billing.Application/Portal.cs:67-88` | `GeneralProcessTests.ExternalUsers_SeeAndActOnlyWithinTheirRelationship_ThroughTheirPortal_ExternalUserAccess` | **OK** |
| Contracts `IAllocationPlanner.Plan` | caller's request on POST /payments/payments/{id}/allocate (also consumers) | the account's open invoices | each invoice's product and node | OQ-081: allocation across an account is account-wide | none: raw SQL over every open invoice of the account (`BillingStores.cs:222-260`); `AllocatePaymentHandler` doesn't check scope either (`Payments.cs:470-483`) | none found | **MISSING** (on the user path) |
| Contracts `IPolicyArrears` | jobs and consumers; Life reinstatement (a user command) | a policy's arrears | the policy | follows the policy | applies nothing itself (`BillingStores.cs:268`); the user caller loads the policy through Life's store first (`Life.Application/Policies.cs:241-262`) | none found | **OK** (inferred: scoped by the caller's load) |

### Payments

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /payments/payments/{id} | PaymentRead | payment | business reference to an invoice, account, refund, claim instruction or Health remittance; scopes read through `IBusinessRecordScopes` | additions: payments follow their invoice, account or refund (all of it) and their claim; commission payouts only with `commission_payout.read` | `PaymentScopes.Covers` (`Payments.Application/Payments.cs:668-711`) in `GetPaymentHandler` (`:739-744`); sources `BillingRecordScopes.cs:48-55`, `ClaimsRecordScopes.cs:38-51`, `HealthPaymentScopes.cs:40-53` | `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103`; `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002` (commission) | **OK** |
| GET /payments/payments | PaymentRead | payments | as above | §4 list | `Payments.cs:754-781` (filtered in memory, reading on until the page is full, so paging stays correct) | `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103` | **OK** |
| GET /payments/summary | PaymentRead | PAYMENTS_UNKNOWN figure | as above | in scope only | `Payments.cs:726-736` | none found | **OK** |
| GET /payments/payout-batches; GET /payout-batches/{id}; GET /payout-batches/{id}/payments | PayoutRead | payout batch and its items | its payments' records | addition: items follow their claim; a batch shows only its in-scope part | `Payments.cs:786-797,813-870` (404 when nothing is in scope; count and total of the visible part) | `EnterpriseProcessTests.QuoteToCash_...` (commission batch 404 without `commission_payout.read`); none for claim scope | **OK** |
| GET /payments/reconciliation-exceptions; GET /reconciliation-exceptions/{id} | ReconciliationRead | reconciliation exception | optional `PaymentId` (a payment, which may be scoped); bank statement line | unclear | none (`Payments.cs:873-889`; `PaymentsStores.cs:145-150`); the view shows the amount and currency of a payment possibly out of scope (`Payments.cs:198-205`) | none found | **UNCLEAR** |
| GET /payments/callback-endpoints; GET /callback-endpoints/{id} | CallbackEndpointRead | provider callback endpoint | configuration | not scoped (platform/configuration) | none | n/a | **NOT SCOPED (by §1)** |
| Contracts `ITenantPaymentProviders` | n/a | provider configuration | configuration | not scoped | n/a | n/a | **NOT SCOPED (by §1)** |

### Finance

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /finance/journals/{id}; GET /finance/journals | JournalRead | journal and subledger lines | no product or node; each line carries `business_ref_type/id` (INVOICE, MEDICAL_CLAIM, CLAIM_MOVEMENT…) (`db/migrations/V0001__core_schema.sql:2264-2301`) | unclear | none (`Finance.Application/Ledger.cs:443-463`) | none found | **UNCLEAR**: the ledger isn't in §1, but its lines name scoped records |
| GET /finance/periods; /periods/{periodCode} (PeriodRead); /periods/{periodCode}/trial-balance (JournalRead) | as listed | accounting period, trial balance | tenant-wide | not scoped (carries no product or node) | none (`Ledger.cs:465-490,513`) | n/a | **NOT SCOPED (by §1)** |
| GET /finance/posting-exceptions; GET /posting-exceptions/{id} | PostingExceptionRead | posting exception (event id, type, error) | none | not scoped | none (`Ledger.cs:502-511`) | n/a | **NOT SCOPED (by §1)** |
| GET /finance/tax-returns; /tax-returns/{id}; /tax-returns/{id}/content | TaxReturnRead | tax return | tenant-wide | not scoped | none (`TaxReturns.cs:161-182`) | n/a | **NOT SCOPED (by §1)** |
| GET /finance/gl-exports; /gl-exports/{id}; /gl-exports/{id}/content | GlExportRead | GL export batch | tenant-wide | not scoped | none (`GlExport.cs:191-199`) | n/a | **NOT SCOPED (by §1)** |
| Contracts `ILedgerBalances` (Compliance returns) | n/a | account movement | tenant-wide | not scoped | `LedgerBalances.cs:34` | n/a | **NOT SCOPED (by §1)** |

### Reinsurance

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /reinsurance/cessions | CessionRead | cession | **product** (`Reinsurance.Domain/Cessions.cs:147`) and its policy (`SourceId`); no node | unclear: §1's list doesn't name cessions, though "every record whose module knows its product" could include them | none (`ReinsuranceStores.cs:77-103`; `Reinsurance.cs:179-182`) | none found | **UNCLEAR** |
| GET /reinsurance/recoveries; GET /reinsurance/recoveries/{id} | RecoveryRead | reinsurance recovery | its claim number and policy (`Recoveries.cs:69-75`); no product or node column | unclear | none (`ReinsuranceStores.cs:71,132-152`; `Reinsurance.cs:187-191`) | none found | **UNCLEAR** |
| GET /reinsurance/bordereaux; /bordereaux/{id}; /bordereaux/{id}/content | BordereauRead | bordereau (treaty by period, aggregating cessions) | treaty-level, across products | unclear | none (`ReinsuranceStores.cs:105-130,154`; `Reinsurance.cs:184-194`) | none found (`EnterpriseProcessTests` reads it as the unrestricted maker only) | **UNCLEAR** |

### Additions after sign-off, checked against Billing and Payments

- **Shared billing accounts (OQ-081).**
  - *A partial caller sees only their part:* met. `BillingStores.cs:50-57,93-97` and `AccountView.From` (`Billing.cs:140-147`) drop the out-of-scope schedules and all arrears. The invoices list is filtered per invoice (`Billing.cs:617`). Tested in `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103`.
  - *Account-wide operations need the whole account:* met. `AccountScopes.RequireWhole` (`Billing.cs:185-188`), the summary (`Billing.cs:173`), and refunds list and read (`BillingStores.cs:74-87,125-131`). There is one gap: the refund read with an account that has no schedules (above).
  - *Payments against invoices, accounts and refunds follow them:* met for reads. `PaymentScopes.Covers` needs every scope of the record (`Payments.cs:701-711`, with `BillingRecordScopes.cs:48-55` for INVOICE, BILLING_ACCOUNT and REFUND_REQUEST). Not met for allocation through `IAllocationPlanner` (MISSING above).
- **Claim payouts.**
  - *Payments and batch items follow their claim:* met. The sources are `ClaimsRecordScopes.cs:38-51` (instruction, then claim) and `HealthPaymentScopes.cs:40-53` (remittance, then the claims' memberships).
  - *Building or releasing a batch needs all of it in scope:* met. `BuildBatchHandler` refuses OUT_OF_SCOPE when only part is in scope (`Payments.cs:528-530`), and `ReleaseBatchHandler` does the same, giving 404 when nothing is in scope (`Payments.cs:563-567`).
  - *A batch's approval is scoped when its payments share one scope:* met. `PayoutApprovalScopes.cs:41-64` uses the single shared scope, and otherwise (null, null), which only an unrestricted checker covers.
- **Commission payouts.** Met. A commission payment is outside branch scope and visible only with `commission_payout.read` (`Payments.cs:674-677`). Tested in `EnterpriseProcessTests.QuoteToCash_...`.

### (a) MISSING and PARTIAL, one line each
1. Health `GET /health/memberships/{id}/accumulators`: MISSING. No membership scope check (`Members.cs:434-439`, `HealthStores.cs:113-117`).
2. Health `GET /health/payment-runs`: PARTIAL. Scope is filtered after `Take(limit)` (`HealthLists.cs:129`), so a short page ends paging and later in-scope runs are lost.
3. Health `GET /portal/health/claims`: PARTIAL. The list applies relationship only (`HealthLists.cs:190-210`); the single read also applies product and node (`HealthStores.cs:133-137`).
4. Billing `GET /billing/refunds/{id}`: PARTIAL (edge case). On an account with no schedules the "whole" check passes for any scoped caller (`BillingStores.cs:84-86`); the list and Payments hide that refund.
5. Billing `IAllocationPlanner` on `POST /payments/payments/{id}/allocate`: MISSING. It allocates across every open invoice of the account with no scope check (`BillingStores.cs:222-260`, `Payments.cs:470-483`).

### (b) UNCLEAR questions, one line each
1. Are Health schemes scoped by product or node? They carry neither, and the code says BR-067 leaves them unscoped (`HealthLists.cs:146`).
2. Health provider-level records (measures, quality reviews, statements) aggregate claims of every product: should a scoped caller see them, filtered, or not at all?
3. Health provider recoveries and credit notes refuse any restricted caller with 403 even on reads (`ProviderRecoveries.cs:213`). Is that acceptable, rather than §4's 404 for one record and filtering for lists?
4. ClaimsFramework fraud cases (Life and General): should they follow their claim or policy, as Health's do?
5. Payments reconciliation exceptions: should one linked to a payment follow that payment's scope?
6. Portal routes: does a portal user's role-assignment product or node scope apply on top of the relationship? Today some portal routes apply it (memberships, single claim, referrals, invoices) and some don't (claims list, chronic registrations, remittances, recoveries, credit notes).
7. Payments whose business reference no scope source covers (CASE_REDRESS, or none) are visible to every `payment.read` holder (`Payments.cs:703-706`). Are compliance redress payouts scoped?
8. Finance journals: the ledger is not in §1, but its lines reference scoped invoices and claims. Should journal reads follow their business references?
9. Reinsurance cessions (which know their product), recoveries (which know their claim and policy) and bordereaux (aggregates): is §1's "every record whose module knows its product" meant to include Reinsurance?

### (c) Inferred
- The ClaimsFramework fraud case's product can be derived through `ClaimNumber` or `PolicyRef`; I didn't trace that join in code.
- `IClaimFraudReferrals` and `IPolicyArrears` are OK only because their user-request callers load the policy through a scoped store first (`RenewalReview.cs:110`, `Life Policies.cs:241-262`); other callers are jobs or consumers.
- Health's POST searches (`/memberships/search`, `/eligibility-decisions/search`) are treated as non-reads. They go through the channel gate, and their scope is narrowed to the products the channel allows (`IModule.cs:182-200`, `ExecutionScope.cs:119`). I inferred the effect on a product-limited channel; I didn't test it.
- Outside the read brief: `PaymentScopes` is injected only into the build, release, summary, get, list and batch handlers. The `AllocatePayment` handler (`Payments.cs:470`) and, from the grep, the `ResolveUnknown` and `ReversePayment` handlers don't check the payment's scope.

## Pass C: Workflow, Documents, Communications, Analytics, Assist, Compliance, Audit, Integration, Party, Identity, Tenancy, StaticData

Modules: Workflow, Documents, Communications, Analytics, Assist, Compliance, Audit, Integration, Party, Identity, Tenancy, StaticData.
Paths are relative to `src/Modules/` unless they start with `tests/`. "CurrentScope" is the scope of the route's own permission.

### Workflow

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /workflow/approvals, GET /workflow/approvals/{id} | approval.read | Approval | Yes: `ProductCode`, `OrgNodeCode`, `IsScoped`, copied at request time from the record it is about, through `IApprovalScopeSource` (Workflow/Insys.Workflow.Application/Approvals.cs:600-605; Workflow/Insys.Workflow.Domain/Approvals/Approval.cs:137-149) | §1: "approvals follow the record they are about" | List: `query.InScope(CurrentScope, ProductCode, OrgNodeCode)`, with approvals where `!IsScoped` left visible (Workflow/Insys.Workflow.Infrastructure/Persistence/WorkflowStores.cs:72-76). Single: `Allows` check, otherwise null, then 404 (WorkflowStores.cs:101-105; Approvals.cs:561-564) | tests/Insys.IntegrationTests/WorkflowTests.cs:233 `ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103` (404 outside scope, list filtered) | OK (the mechanism). The coverage of approval types is a separate row below |
| (same routes) approvals whose type has no scope source | approval.read | Approval about GENERAL_RENEWAL (non-renewal of a policy), REFUND_REQUEST (billing), CASE_REDRESS (a case), RECORD_LEGAL_HOLD / DOCUMENT_LEGAL_HOLD (a hold on a scoped record), Health CREDIT_NOTE / RECOVERY | The record it is about knows its product and node, but no `IApprovalScopeSource` covers these types, so `IsScoped=false`. Sources exist only for Life underwriting and claims, General endorsements and settlements, the Claims instruction, the Payments payout batch and the Health run (Life/…/LifeApprovalScopes.cs:45-53, General/…/GeneralApprovalScopes.cs:44-52, ClaimsFramework/…/ClaimsApprovalScopes.cs:44-49, Payments/…/PayoutApprovalScopes.cs:44, Health/…/HealthPaymentScopes.cs:62) | §1: follow the record | Shown to every reader, because the `!a.IsScoped` branch applies (WorkflowStores.cs:75, 103). The approve and reject routes are not scope-checked for these types either (Approvals.cs:213) | none found | MISSING |
| GET /workflow/approvals?decidable=true | approval.read | Approval | As above | §4: a checker needs the permission in scope for the record. Deciding uses approval.approve | `CanApprove` uses `CurrentScope`, which on this route is approval.read's scope, not approval.approve's (Approvals.cs:250-251, 495-540; WorkflowStores.cs:107-118) | Partly: WorkflowTests.cs:233 (approve inside scope) | PARTIAL (uses a different permission's scope) |
| GET /workflow/approvals/my-authority | approval.read | The caller's own authority rows | No | Not scoped (configuration for the caller) | n/a | n/a | NOT SCOPED (by §1) |
| GET /workflow/processes, GET /workflow/processes/{id} (with its tasks and case notes) | process.read | ProcessInstance with its tasks and case notes | Not stored, but every instance has `BusinessRefType`/`BusinessRefId` pointing at its record: General quote referrals, General renewals, held claim payments, Health memberships, General retention cases, service cases and so on (Workflow/Insys.Workflow.Application/Processes.cs:436-959; Approvals.cs:179) | §1: "tasks follow the record they are about" | Nothing: plain queries (Workflow/Insys.Workflow.Infrastructure/Persistence/WorkflowStores.cs:140-173) | none found | MISSING |
| GET /workflow/tasks | process.read | Open ProcessTask | As above, through its process | §1: tasks follow the record | Nothing (WorkflowStores.cs:175-196) | none found | MISSING |
| GET /workflow/cases, GET /workflow/cases/summary | case.read | ServiceCase | Yes: `ProductCode` and `OrgNodeCode`, placed from the linked scoped record when the case is opened (Workflow/Insys.Workflow.Application/Cases.cs:559-570) | §1, as records about scoped records. Inferred: the addendum does not list cases | `Visible()`: `InScope(CurrentScope, …)` in the query, with a case that has neither product nor node left visible (Workflow/Insys.Workflow.Infrastructure/Persistence/CaseStores.cs:278-285; used at 165, 217) | none found (CaseTests.cs covers only the portal) | OK |
| GET /workflow/cases/complaints-report | case.report_read | Aggregate over ServiceCase | As above | As above | `Visible()` with report_read's scope (CaseStores.cs:243-248) | none found | OK |
| GET /workflow/cases/{id}, GET /workflow/cases/{id}/redress/{redressId} | case.read | ServiceCase, CaseRedress | As above. Redress inherits from its case | §4: 404 outside scope | Get: `access.InScope(c)`, otherwise null, then 404 (CaseStores.cs:190-194; Cases.cs:439). Redress: `access.Load` (403 inside) collapses to 404 (Cases.cs:846-852) | none found | OK |
| GET /portal/cases, GET /portal/cases/{id} | case.read (portal) | ServiceCase | Relationship: complainant or on-behalf-of party, the broker's intermediary codes or brokered policies that are shared, the provider | Relationship | In the query (CaseStores.cs:298-325); single read through `PortalCaseRelation.Sees` (Workflow/Insys.Workflow.Application/CasePortal.cs:148-155, 191-195) | tests/Insys.IntegrationTests/CaseTests.cs:246 `APortalMember_RaisesAndSeesOnlyTheirOwnCases_AndAppealsADecision_Cases5` | OK |
| GET /portal/cases/options | case.read (portal) | Case types for the audience | No | Configuration | n/a | n/a | NOT SCOPED (by §1) |
| Contracts: `IApprovalHistory` (Latest, Waiting, Requested) | caller's | The latest or waiting approval of a record | Keyed by the caller's record | Follows the record | No check of its own (WorkflowStores.cs:467+). Inferred: every caller (Documents, General, Life, Billing …) calls it only after loading its own record, so it is gated by that load | n/a | OK (inferred: gated by the caller's scoped load) |
| Contracts: `IProviderCaseCounts`, `ICaseNotices`, `IClaimAppealRoutes` | caller's | Counts, notice data, configuration | No product read is returned to a user | n/a | n/a | n/a | NOT SCOPED (by §1) |

### Documents

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /documents?businessRefType&businessRefId, GET /documents/{id}, GET /documents/{id}/content | document.read | Document (a policy's schedule, a claim's letters …) | Not on the document itself. Its `BusinessRefType`/`BusinessRefId` names a policy, claim and so on, which knows its product and node. The module can already resolve that, since `RecordHoldTargets.ScopesOf` uses `ILegalHoldTargets` | §1: "child records inherit [the node] from their root". Inferred: documents attached to a scoped record follow it | Nothing. Get (Documents/Insys.Documents.Application/Documents.cs:536-541), list (Documents.cs:544-555) and content (Documents.cs:484-530) check only classification, purpose and the allowed role (DOC-003, DOC-004) | none found | MISSING |
| GET /documents/legal-holds, GET /documents/legal-holds/{id} | legal_hold.manage | RecordHold on a scoped record (a policy, claim, case …) | Yes, through the held record's module (`ILegalHoldTargets`) | §1: follows the record | Single: `targets.Visible`, otherwise 404 (Documents/Insys.Documents.Application/RecordHolds.cs:189-196, 106-107). List: filtered after paging, in memory (RecordHolds.cs:199-210), and the endpoint ends the cursor when a page comes back short (Documents/Insys.Documents.Api/DocumentsModule.cs:221). So hidden holds shorten pages and can end paging early | tests/Insys.IntegrationTests/LegalHoldReleaseTests.cs:127 `ARecordsHold_IsPlacedOnARecordThatExistsInScope_…_D34` (list returns 0 outside scope) | PARTIAL (not filtered in the query, so paging breaks, contrary to §4) |
| GET /portal/documents, GET /portal/documents/{id}/content | document.read (portal) | Document | Relationship of the business record, through `IPortalRecordRelations` | Relationship | `RecordRelated` plus the category's allowed roles (Documents/Insys.Documents.Application/Portal.cs:81-93, 96-106) | none found | OK |
| Contracts: `IRecordHolds`, `IDocumentOwnership` | caller's | Hold flags; the document owner for F001 | Called after the caller's own load | n/a | n/a | n/a | NOT SCOPED (by §1) |

### Communications

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /communications/notifications, GET /communications/notifications/{id} | notification.read | Notification (a claim's letters, renewal notices, arrears reminders …) | Yes: `ProductCode` and `OrgNodeCode`, copied from the source record by its trigger (Communications/Insys.Communications.Application/Triggers/*.cs; Communications/Insys.Communications.Application/Queueing.cs:112) | §1: follows the source record | `Scoped` = `InScope(CurrentScope, …)` for both the list and the single read, which gives 404 (Communications/Insys.Communications.Infrastructure/Persistence/CommunicationsStores.cs:101, 103-142, 144-145; Communications/Insys.Communications.Application/Requests.cs:247) | tests/Insys.IntegrationTests/CommunicationsTests.cs:339 `Staff_SeeOnlyNotificationsWithinTheirScope_F103` | OK |
| GET /communications/parties/{partyId}/preferences | preference.read | Party preferences | No | Not scoped (party data, PTY-003) | n/a | n/a | NOT SCOPED (by §1) |
| GET /communications/callback-endpoints | callback_endpoint.read | Provider endpoint | No | Configuration | n/a | n/a | NOT SCOPED (by §1) |
| GET /portal/communications/notifications, GET /portal/communications/notifications/{id}, GET /portal/communications/preferences, GET /portal/communications/devices | notification.read, preference.read, device.update (portal) | Own notifications, preferences, devices | Relationship: the recipient party | Relationship | `RecipientPartyId == partyId`, sent or delivered only (CommunicationsStores.cs:147-160) | none found | OK |

### Analytics

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /analytics/written-premium, GET /analytics/claims-incurred, GET /analytics/summary | projection.read | Fact rows | Yes: `product_code` and `branch_code`, which is the policy's node, or `ALL` when it has none (Analytics/Insys.Analytics.Application/Projections.cs:208-210) | §1 (facts of scoped records) | `ScopeSql.Predicate(CurrentScope, "product_code", "branch_code")` in every query (Analytics/Insys.Analytics.Infrastructure/Persistence/ProjectionStore.cs:161, 181, 200, 255, 304) | tests/Insys.EndToEndTests/EnterpriseProcessTests.cs:268 `QuoteToCash_…` (a branch-scoped user sees 0 on the summary). No test for the other two routes | OK |
| Contracts: `IProjectionTotals.Total` | caller's (Compliance) | Tenant-wide metric totals | Yes, on the facts | Unclear | No scope: sums the whole tenant on Analytics' own connection (ProjectionStore.cs:317-345) | none found | UNCLEAR (see Compliance) |

### Assist

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| POST /assist/explanations/premiums | assist.explain_premium, also calculation.read | Recorded rating calculation (through Rating's `IRecordedCalculations`) | Product yes; no node on calculations | Scope of the calculation's read permission | `RecordedCalculations.Get` dispatches `GetCalculation`, which checks `ScopeFor(calculation.read).Allows(ProductCode, null)` (Rating/Insys.Rating.Application/Calculations.cs:249, 325-333). A stored explanation is reused only after that check (Assist/Insys.Assist.Application/Explanations.cs:190-243) | tests/Insys.IntegrationTests/AssistExplanationTests.cs:249 `AUserRefusedTheCalculation_IsRefusedItsExplanation_TheSameWay_AndGetsNoStoredOne_AIA003_F103` | OK (product. A branch-scoped user is refused every calculation because the node is null) |
| POST /assist/explanations/refusals | assist.explain_refusal | Error-catalogue wording | Takes `recordType` and `recordId` but never reads the record (only for basedOn and the audit) | n/a | n/a | n/a | NOT SCOPED (by §1) |
| GET /assist/settings, GET /assist/usage | assist.settings_manage | Tenant settings, usage | No | Configuration | n/a | n/a | NOT SCOPED (by §1) |

### Compliance

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /compliance/returns, GET /compliance/returns/summary, GET /compliance/returns/{id} | return.read | Regulatory return (tenant level) | No product or node on the record. Its values come from ledger balances and tenant-wide projection totals (Compliance/Insys.Compliance.Application/Compliance.cs:186-204) | Unclear: not listed in §1, and its figures span products | None | n/a | UNCLEAR |
| GET /compliance/report-runs, GET /compliance/report-runs/{id}, GET /compliance/report-runs/{id}/export | report.read | ReportRun (stored totals) | No. The totals are tenant-wide, from `IProjectionTotals` (Compliance.cs:370-374). Access is gated by the report's roles (Compliance.cs:363, 462) | Unclear | Roles only | n/a | UNCLEAR |

### Integration

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /integration/dead-letters, GET /integration/dead-letters/{id}, GET /integration/parked-deliveries, GET /integration/parked-deliveries/{id}, GET /integration/deliveries/summary, GET /integration/webhook-deliveries | dead_letter.read, webhook.read | Operational delivery records | Not on the record. The dead-letter detail carries the event `Payload`, and parked deliveries carry `AggregateType`/`AggregateId`, which can be a policy or claim (Integration/Insys.Integration.Application/DeadLetters/Ports.cs:40; Integration/Insys.Integration.Application/Parking/ParkedDeliveries.cs:39-42) | Unclear: operational records within a tenant, holding scoped records' event data | None | n/a | UNCLEAR |
| GET /integration/parked-deliveries/by-tenant | platform dead_letter.read | Counts per tenant | No | Platform record | n/a | n/a | NOT SCOPED (by §1) |
| GET /integration/disclosures, GET /portal/disclosures, GET /integration/parking-setting | disclosure.read, … | Reference text, configuration | No | Static or configuration | n/a | n/a | NOT SCOPED (by §1) |
| Contracts: `IRecordDeliveries`, adapters | caller's | By aggregate, after the caller's own load | n/a | n/a | n/a | n/a | NOT SCOPED (by §1) |

### Audit

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /audit/events, GET /audit/records/{targetType}/{targetId} | audit.read | Audit event | No product or node (only target type and id) | Not scoped (inferred: a platform or audit record) | None (Audit/Insys.Audit.Api/AuditModule.cs:56-79) | n/a | NOT SCOPED (by §1) |

### Party

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| GET /parties/{id} and its bank-accounts, identity-checks, consents and due-diligence sub-resources, GET /party/due-diligence, GET /party/search-log, the data-subject-request routes (/dsr), GET /portal/party/parties…, GET legal holds | party.* | Party and its data | No (parties are shared across products) | §1: not scoped (PTY-003) | n/a | n/a | NOT SCOPED (by §1) |
| GET /integration/bulk-jobs, GET /party/bulk-jobs/{id} | bulk_job.read | BulkJob (counts) | No | Not scoped | n/a | n/a | NOT SCOPED (by §1) |
| GET /party/bulk-jobs/{id}/content for a POLICY_EXPORT job | bulk_job.read | CSV of every General, Life and Health policy and membership | Yes: each row has `product_code`, and the policy has its node | §1: policies and memberships are scoped by product and node | Nothing: `IPolicyExportRows.All(tenantId)` reads every policy in the tenant, for example `SELECT … FROM general.policy` (Party/Insys.Party.Application/BulkJobs.cs:221-229, 266-279; General/Insys.General.Infrastructure/Persistence/GeneralMigrationTotals.cs:49-50). The request count at BulkJobs.cs:215 is unscoped too | none found | MISSING |

### Identity, Tenancy, StaticData

| Route (METHOD path) | Permission | Record | Knows product? / node? / relationship? (how) | Scope it should apply (§1 says / not scoped / unclear) | What it applies today (file:line) | Test | Verdict |
|---|---|---|---|---|---|---|---|
| Identity: GET /identity/me, GET /identity/me/menu, roles, templates, users, subject-link, role-assignments/scoped, grants, reviews, requests | user.read and others | IAM records. Assignments carry `scope_products` and `scope_branches` as the scope definition itself, not as an owning product | No owning product or node | Not scoped (platform or IAM) | n/a | n/a | NOT SCOPED (by §1) |
| Tenancy: GET /tenancy/readiness, GET /tenants, GET /tenants/{id}, GET /tenants/{id}/readiness | tenant.* | Tenant | No | Platform record | n/a | n/a | NOT SCOPED (by §1) |
| StaticData: GET /static-data/changes…, GET /static-data/settings, GET /static-data/imports | static_data.* | Static data governance | No | Static data | n/a | n/a | NOT SCOPED (by §1) |

### Counts by verdict

| Module | OK | MISSING | PARTIAL | NOT SCOPED (by §1) | UNCLEAR |
|---|---|---|---|---|---|
| Workflow | 6 | 3 | 1 | 2 | 0 |
| Documents | 1 | 1 | 1 | 1 | 0 |
| Communications | 2 | 0 | 0 | 2 | 0 |
| Analytics | 1 | 0 | 0 | 0 | 1 |
| Assist | 1 | 0 | 0 | 2 | 0 |
| Compliance | 0 | 0 | 0 | 0 | 2 |
| Integration | 0 | 0 | 0 | 3 | 1 |
| Audit | 0 | 0 | 0 | 1 | 0 |
| Party | 0 | 1 | 0 | 2 | 0 |
| Identity, Tenancy, StaticData | 0 | 0 | 0 | 3 | 0 |

### (a) MISSING and PARTIAL
- **MISSING**, Workflow approvals: approvals of types with no `IApprovalScopeSource` are unscoped (`IsScoped=false`), so every reader sees them and the scope is not checked on approve or reject. The types are GENERAL_RENEWAL, REFUND_REQUEST, CASE_REDRESS, RECORD_LEGAL_HOLD, DOCUMENT_LEGAL_HOLD and the Health credit-note and recovery types (WorkflowStores.cs:75,103; Approvals.cs:600-605).
- **PARTIAL**, GET /workflow/approvals?decidable=true: decides "can approve" with approval.read's scope, not approval.approve's (Approvals.cs:251).
- **MISSING**, GET /workflow/processes and /processes/{id}: no scope, although every process names a scoped business record (WorkflowStores.cs:140-173).
- **MISSING**, GET /workflow/tasks: no scope (WorkflowStores.cs:175-196).
- **MISSING**, GET /documents, /documents/{id} and /documents/{id}/content: no scope from the business record the document is attached to (Documents.cs:484-555).
- **PARTIAL**, GET /documents/legal-holds: scope is filtered after paging, so pages come back short and the cursor ends early (RecordHolds.cs:199-210; DocumentsModule.cs:221).
- **MISSING**, GET /party/bulk-jobs/{id}/content for a policy export: every policy and membership in the tenant, whatever the reader's scope (BulkJobs.cs:221-229, 266-279).

### (b) UNCLEAR, as open questions
- Are regulatory returns and compliance report runs, whose figures are tenant-wide totals across products, scoped for a product- or branch-scoped reader, or tenant-level by nature?
- Should `IProjectionTotals` apply the caller's scope, as the Analytics routes do, or stay tenant-wide for regulatory reporting?
- Are a tenant's integration dead letters and parked deliveries "platform records"? The dead-letter detail carries the payloads of scoped records' events.
- Cases with neither a product nor a node, and approvals with `IsScoped=false`, are shown to every scoped reader. Elsewhere a record with a null node is outside a branch-scoped grant. Is that intended for records not about a scoped record?
- Are documents and record holds attached to a policy or claim "child records" under §1 (another module's records about the root)? Documents assume no today; holds and notifications assume yes.

### (c) Inferred
- Cases count as scoped under §1 although they are not listed there. They carry product and node, placed from their linked record.
- `IApprovalHistory` and `IRecordDeliveries` are gated by the caller's own scoped load of the record. I did not check every caller.
- Documents should inherit the scope of their business record, by §1's "child records inherit".
- Audit events are platform records.
- The business-ref types covered by each module's scope source were read from the `Sql()` switches. Whether other modules emit further approval types was checked only by grepping `ApprovalRequested(`.
