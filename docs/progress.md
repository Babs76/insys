# Build progress

| Phase | Scope | Status |
|---|---|---|
| 0 Foundation | Solution, building blocks, migration runner, compose, tests, CI | Done, accepted |
| 0 review items 1–8 | CI on postgres:18, partitions, inboxes, module roles, permissions, global static data, maker-checker, error codes | Done, accepted |
| 1 Platform | Tenancy, Identity, Audit, Static Data and generator | Done, accepted |
| 1 review items 1–6 | Repository move, V0007 to V0009, access aggregates, cache guard, OpenAPI split, Tier 2 setting, outbox lag | Done, accepted |
| 2 Shared services | Party, Product Factory, Rating, Rules, Workflow, Documents | Done, accepted with conditions (CI green: 979e207) |
| 2 review items 1–6 | CI fix, maker-checker gaps (V0013), schema gaps (V0012, ADR-010), SLA calendars, rating step order, expression limits | Done (CI green: 51bc94d) |
| 3 Money | Billing, Payments, Finance, Claims Framework | Done (CI green: 7f86c0e) |
| 4 First domain | General (OQ-001) | Done, accepted with follow-ups (CI green: 8f2eda7) |
| 4 review items 1–5 | Approval subjects, payee details, inclusive periods, quote decline, arithmetic, cover start | Done, accepted |
| 5 Other domains | Payee bank accounts (ADR-013), product columns, Health defaults (ADR-012), Life and Health | Done, accepted with follow-ups |
| 5 review items 1–5 | Bank account verification in Workflow, skipped tests, underwriting authority, Health concurrency, OQ-061 | Done, accepted |
| 6 Enterprise | Distribution, Reinsurance, Compliance, Analytics, Integration; General auto-renewal and the exposure snapshot job | Done, accepted (CI green: 413564b) |
| 7 Hardening | Security suite, OpenAPI review, performance smoke tests, README and runbooks | Done, accepted (CI green: 3f1092c) |
| 8 Workstream A | Platform ADRs (016 to 020), functional coverage verified | Done, accepted (checkpoint A) |
| 8 Workstream B | Languages: tenant languages, labels, localised ProblemDetails, documents, preferred language; CI split into three jobs | Done, accepted |
| 8 CI and startup | Shared test hosts, template databases, router warm-up gate with 503 NOT_READY, development signing key allow-list | Done (CI green: b6a9978) |
| 8 Workstream C | Source file headers on 913 files, SourceHeaderTests and CI check, ADR-021 | Done, accepted |
| 8 Workstream D | DEW KMS envelope encryption, OBS document storage, Keycloak realm and compose service | Done, accepted |
| 8 Wave 0 batch 1 (front end) | GAP-002, 003, 005, 006, 007, 008, 019, 020, 021, 025, 030 and per-record history; contract changes in `docs/api/contract-changes.md` | Done, merged (`844b668`) |
| 8 Wave 0 batch 2 (front end) | GAP-031 (schema IDs), 034 (readiness; ADR-022 proposed), 022, 023, 024, 027, 029, 032, 033 and request 24 | Done, merged (`bcc8ebc`) |
| 8 Wave 0 batch 3 | ADR-022 revision 2; Cloud Eye alarms; party search log access (OQ-073); F009, F023, F055 built; F103, F005, F062 proposals (OQ-074 to OQ-076) | **Done on `wave0-3`, awaiting review** (23 Oct 2026) |

## Phase 1 review items

1. **Repository move:** the repository is now in `~/Development`. A clean build and the full test suite pass there, no conflict copies remain, and CI fails on any `* 2.*` file.
2. **V0007:**
   - `sdm.change_request.status` is widened, and PENDING_APPROVAL is back.
   - Privileged access grants get a requester, status and decision, with approver ≠ requester.
   - `iam.access_review_item` is added.

   The PrivilegedAccessGrant and AccessReview aggregates are built, with tests.
3. **Permission cache:** ADR-006 records that it is per instance. A startup guard (an advisory lock) refuses a second instance until `Identity:PermissionCache:AllowMultipleInstances` is set.
4. **OpenAPI documents:** split into core, static-data and platform-operator. Each has its own snapshot in `docs/api`, and every endpoint is in exactly one.
5. **Tier 2 maker-checker:** a per-tenant setting (V0008), on by default and audited. Tier 1 is always on.
6. **Outbox relay lag:** reported on `/health` and `/health/ready` through `integration.outbox_lag` (V0009). Degraded after `Outbox:Lag:Warning` (1 minute); Unhealthy and not ready after `Outbox:Lag:Failure` (5 minutes).

## Phase 8 Workstream B: languages (OQ-068, `docs/specs/addenda/localisation.md`)

- **Tenant languages:** Tenancy serves `ILanguageSettings` from `cfg_locale_defaults.languages`. The first language is the default, and a tenant without locale defaults gets English.
- **Request language:** `ExecutionScopeMiddleware` resolves it, only for a member of the tenant.
  - It honours `Accept-Language` (quality, primary subtag) when the tenant supports the language, else uses the tenant default.
  - The fallback chain is the request language, the tenant default, then English.
  - Every `/api` response carries `Content-Language`.
- **Static data:** reads return `displayLabel` and `displayLabelLanguage`, taken from `labels`, else from the table's `label` column. Writes refuse `labels` that are not `{code: text}`.
- **ProblemDetails:** titles, and details where translated, come from the UI label bundles keyed by `message_key`, through Integration's `IMessageCatalogue`. The `code` never changes.
- **Documents:** General, Life and Health generation picks the template in the party's language, then the tenant default, then English (`DocumentLanguages`). The language is recorded on the generation request. `GeneralPolicyEndorsed` and `GeneralRenewalInvited` now carry the policyholder.
- **Party:** `PUT /api/v1/party/parties/{id}/preferred-language`, checked against the language register (Static Data's `ILanguageRegister`).
- **Tests:** `LanguageChainTests` (4) and `LocalisationTests` (8).
- **CI:** three parallel jobs, each limited to 30 minutes: unit and architecture; integration; sweeps and end to end.

## Phase 7: what exists (hardening)

- **§27.4 security suite** (integration tests, on the non-owner roles):
  - `EndpointSecuritySweepTests` calls every API endpoint (more than 1,000 calls) without a token (401), without a tenant (403), without its permission (403) and with another tenant's token (403).
  - Every changing endpoint is refused to a user holding every read permission (privilege escalation).
  - Every platform endpoint is refused from a tenant, even with its permission.
- **Cross-tenant data:** `CrossTenantSuiteTests`.
  - Another tenant's records give 404 on reads, empty lists and searches, and 404 or 422 on changes, and are left unchanged.
  - The permission and rating caches are kept per tenant.
  - An event consumer acts only in its envelope's tenant.
- **Row-level security:** `EveryTenantTable_HasRowLevelSecurityEnabledAndForced_WithATenantPolicy_TEN004` checks every table with `tenant_id`, except the platform-global ones.
- **OpenAPI review:** `OpenApiReviewTests` checks that every operation:
  - is named, summarised, tagged and permissioned;
  - documents its success with a schema;
  - documents 401, 403, 429 and 500, plus 404, 409 and 422 where they apply, as ProblemDetails;
  - declares If-Match with 412 and 428.

  To get there: `StandardProblems` on the v1 group, `RequireIfMatch()`, `.Produces<T>` on every module endpoint, and named records in place of anonymous bodies.
- **Performance smoke tests** against NFR-002 and NFR-003 (`Latency`, `INSYS_LATENCY_LOG`). They cover rating, payment callback and allocation, and adjudication against the benefit accumulators, run one at a time and 8 at a time. These are not load tests.
- **Fixes:**
  - `References.Of`: references from the random end of a UUIDv7, not its timestamp (OQ-058).
  - Replaying a `webhook:` dead letter is refused (OQ-066).
- **Documentation:**
  - the system `README.md`;
  - `docs/runbooks/`: deployment, monitoring, dead letters and webhooks, tenants and access, money operations, static data, security incidents, backup and recovery;
  - the still-open questions with their proposals, at the top of `docs/decisions/open-questions.md`, including the new OQ-067.

## Phase 6: what exists (enterprise)

The defaults of OQ-062 to OQ-066 were accepted to build (ADR-014, ADR-015, V0021), pending business sign-off.
- **Distribution** (`commission`, DST-001 to DST-004): intermediaries, permissions and plans as static data, with a lifecycle guard on the register; General quotes and Life proposals refused through a suspended or unpermitted intermediary; commission accrued on invoices at the plan version in force, payable when the invoice is paid, clawed back on cancellation or lapse; monthly statements approved in Workflow and paid to the intermediary's verified account.
- **Reinsurance** (`reinsurance`, REI-001 to REI-003): quota share and surplus cessions (or facultative), recalculated on endorsement; proportional and excess of loss recoveries on settled claims, billed through the reinsurer adapter and settled; bordereaux per treaty and period.
- **Compliance** (`reporting`): regulatory returns prepared on a closed period from the ledger and the projections, reviewed, approved by another compliance officer, submitted through the regulator adapter; report runs under the catalogue's roles.
- **Analytics** (`analytics`): written premium and claims incurred projections from events, rebuilt from Analytics' own event log.
- **Integration**: partner webhooks (signed, retried by policy, dead-lettered), stub reinsurer and regulator adapters logging to `external_call_log`.
- **General:** renewal invitations and auto-renewal (GEN-004), and the daily exposure snapshot.
- **Finance** posts commission, ceded premium and reinsurance recoveries.

**Processes (end to end):** `EnterpriseProcessTests`.

## Phase 5 review: what changed

1. **Bank account verification (ADR-013) through Workflow:** adding or changing an account requests a BANK_ACCOUNT_VERIFICATION approval (subject: party, account and hash only; `bank_account.verify` required of the checker). The account is ACTIVE only on ApprovalGranted, REJECTED on ApprovalRejected; a further change withdraws the pending approval (new Workflow state Withdrawn) and requests a new one. The separate verify endpoint is gone.
2. **Skipped tests:** the three Phase 0 process placeholders are deleted; the eight OQ-051/052 Life tests remain skipped.
3. **Life underwriting:** one underwriter decides alone when the decision matches the rule outcome and is within their authority matrix (`rules.cfg_authority_matrices`); overrides and decisions above authority go to a second person in Workflow. All are audited.
4. **Health concurrency (ADR-012):** membership then accumulators locked by id; one payment run per claim enforced in the database (V0020); server-side retry of adjudication on conflicts (`Commands:ConflictRetry:MaxAttempts`, 3).
5. **OQ-061 from existing static data (V0020):** Life claim authority by claim type, alteration authority roles, the Health employer share, eligibility response mapping, product eligibility rule sets, edit rules with history, conditional preauthorisations, remittance advice documents; arrears on reinstatement and member reimbursement made configurable.

## Phase 5: what exists (Life and Health)

**Before Phase 5 (the Phase 4 follow-ups):**
- **Payee bank accounts (ADR-013, V0018):** `party.bank_account`, encrypted, superseded on change and audited. Settlements, payment instructions, refunds and payouts reference the account by id; Payments decrypts it only when sending. A changed account fails the payment and needs a fresh approval.
- **Product settings as columns (V0018):** family, class and cover start; labels hold display text only.
- **Health defaults (ADR-012, V0019):** benefit category mappings and tariff modifiers (new static tables), tariff schedules on contracts, networks and plans, benefit year, cost-share kinds and cap bases, limits per plan, code lists on exclusions and waiting periods, and the accumulator's reservation.

**Life** (`life`, §9, LIF-001 to LIF-008):
- **Proposals:** capture on a product version on sale, ages from Party, nominations within the product's rule; PremiumCalculated quotes them (spread over covers, OQ-052); RuleDecisionRecorded records eligibility; submission needs ELIGIBLE.
- **Underwriting cases:** opened on submission with the rule set version (LIF-002); evidence requested, received or waived; the decision and its terms are approved by a second person in Workflow.
- **Policies:** issued on the accepted terms (exclusions carried; loadings and postponements refused, OQ-052); beneficiaries changed by date; alterations checked against state; missed premiums from Billing start grace; the daily job lapses after grace with enough missed payments; reinstatement within the window, with evidence; termination after it; benefits close on settled claims.
- **Loans** within the product's share of the cash value; repayment and offset against a claim.
- **Claims:** notified, registered through the Claims Framework after the LIF-008 checks (status, insured life, benefit, coded exclusions, claimant), assessed, approved by a second person with payees and shares, paid per payee by the Claims Framework, settled when paid.
- **Group schemes:** members by category (eligibility rule, salary multiple), free cover limits by size band, evidence above them.
- **Skipped (OQ-051/052):** automatic underwriting outcomes, loaded premiums, surrender, paid-up and maturity values, loan interest, cash value valuation, group rating — eight skipped tests name them.

**Health** (`health`, §11, HLT-001 to HLT-009):
- **Schemes and memberships:** sponsor schemes; enrolment by category and plan, waiting periods fixed at enrolment; dependants within age limits; plan changes, suspension, reinstatement and termination; contributions billed monthly (Billing).
- **Eligibility enquiries** recorded (partitioned table).
- **Preauthorisation:** request, clinical review, approval within the available benefit (reserved), rejection, modification, cancellation and expiry (released).
- **Claims:** submission (fraud indicators may open a case), validation, adjudication by `AdjudicationEngine` (ADR-012), pending lines reviewed, approval, reversal.
- **Accumulators:** member and family pools, used, reserved, deductible met, co-payments taken.
- **Utilisation and fraud cases,** and **provider payment runs:** cut-off, calculation into remittances to providers' verified accounts, approval in Workflow, release to Payments, reconciliation; Finance posts claims paid.

**Processes (end to end):** `LifeProcessTests` ("Life proposal to policy", "Missed premium to lapse", a death claim to settlement) and `HealthProcessTests` ("Health care to provider payment"), with their compensations.

## Phase 4 review: what changed

1. **Phase 2 conditions,** each with its tests (all passing):
   - **OQ-038:** `MakerCheckerTests.PrivilegedRole_IsPendingAndGivesNoPermission_UntilASecondPersonApproves_OQ038`; `UserAccountTests.PrivilegedAssignment_IsPending_UntilASecondPersonApproves_SRS002` and `RejectedPrivilegedAssignment_NeverApplies`.
   - **OQ-046:** `MakerCheckerTests.SwitchingTier2Off_NeedsASecondPersonsApproval_OQ046`; `StaticDataChangeTests.SwitchingTier2Off_WaitsForASecondPerson_OQ046` and `RejectedSwitchOff_LeavesTier2On`.
   - **V0012:** `SchemaGapTests.StatusColumns_HoldTheirLongestState`, `LossAssessment_HasItsFiguresFromReportedOnwards`, `AccountingPeriods_AllowOneOpenPeriodPerDate` and `DeprecatedStatusColumns_AreNullable_ADR010`.
   - **Balanced journals:** `PostedFinancialRecordsTests.BalancedJournal_IsAccepted`, `UnbalancedJournal_IsRefusedAtCommit`, `OneSidedJournal_IsRefusedAtCommit` and `JournalMixingCurrencies_IsRefusedAtCommit`.
   - **No UPDATE or DELETE on posted tables:** `PostedFinancialRecordsTests.ModuleRoles_HoldNoUpdateOrDelete_OnPostedTables_IncludingPartitions_DBA009`, `ModuleRole_CannotChangePostedRecords_FIN002` and `EvenTheOwner_CannotChangeAPostedJournal_TheTriggerRefuses`.
2. **Regression tests against the real schema, independent of General:**
   - **Billing:** `BillingPersistenceTests` covers credit notes and their adjustments.
   - **Payments:** `PaymentPersistenceTests` covers reversals, with reversing allocation rows, and no re-allocation. Allocating a payment that has not succeeded is now refused.
3. **Approvals bound to their subject (ADR-009, V0017):**
   - **Hashes:** Workflow keeps the requester's subject hash and returns it with the decision.
   - **Settlement standing in for the payment approval:** a General settlement approval covers payee, bank-details hash, amount, currency and reserve type, and stands in for the payment instruction's approval.
   - **New payee or bank details:** after a failed payment they need a new approval before release.
   - **Payout batches:** keep their own release approval.
   - **Tests:** `ClaimsTests` (four new) and the claim process test.
4. **OQ-059:**
   - inclusive policy periods, with boundary tests (first day, last day, leap year);
   - Referred → Declined with GeneralQuoteDeclined;
   - bounded arithmetic in the expression language (ADR-008), tested by `ExpressionArithmeticTests`;
   - cover starting on first payment (a product setting; InvoiceSettled starts cover);
   - short-period refunds still refused;
   - proportional allocation flagged under OQ-052.
5. **Built in Phase 6 (ADR-015):** auto-renewal and the exposure snapshot job.

## Phase 4: what exists (General)

**General** (`general`, §10, GEN-001 to GEN-008):
- **RiskObject:** attributes validated against the type's attribute schema version (GEN-005); a valuation basis whenever a value is set.
- **GeneralQuote:**
  - versioned with the same number;
  - risks and coverages within the product's coverages and limits (§7.2);
  - rated only from Rating's recorded calculation (PremiumCalculated, PRD-008);
  - referral to an underwriter and the decision;
  - issued for the product's validity period, and accepted only within it (GEN-001).
- **GeneralProposal:** declarations on an accepted quote; converted when the policy is issued.
- **GeneralPolicy:**
  - issued from the accepted quote version, with several risk objects (GEN-002);
  - coverage premiums that sum to the policy premium;
  - in force from its start;
  - renewal (rule-driven; a re-rated renewal is a new quote), cancellation with a pro rata refund, reinstatement and expiry (GEN-004).
- **GeneralEndorsement:**
  - effective within the period (GEN-003);
  - rated through Rating, with the premium change by the type's method;
  - approved by a second person in Workflow before the policy changes;
  - its document is attached.
- **GeneralClaim:**
  - first notification, then registration with cover validation (GEN-006), the number allocated by the Claims Framework (CLM-005), and fraud indicators evaluated;
  - assessor appointment;
  - a settlement with the excess and limits applied (GEN-008), approved by a second person within authority (SRS-002);
  - decline with a rationale (CLM-003);
  - settled on ClaimPaymentCompleted; closed; reopened.
- **LossAssessment:** an assessor on an approved panel (GEN-007), panel repairers' estimates, a report with the excess the coverage's schedule applies, acceptance or further work.
- **RecoveryCase:** demand, salvage and its disposal, a recovery recorded as a claim financial movement (CLM-004), write-off, closure.
- **Jobs:** periods start and end; quotes expire.
- **API:** `/general`, 47 endpoints.

**Other modules:**
- **Billing** consumes GeneralPolicyIssued (account and schedule, first invoice), GeneralPolicyEndorsed (schedule amended) and GeneralPolicyCancelled (schedule stopped, refund credited).
- **Documents** consumes GeneralPolicyIssued (schedule and certificate) and GeneralPolicyEndorsed (the endorsement type's documents), with the product's templates.
- **Claims Framework:**
  - `IClaimRegistry`: the claim number and fraud evaluation at registration;
  - consumes GeneralClaimApproved (an instruction that reuses the settlement's approval, released within the reserve) and RecoveryRecorded (a recovery movement).
- **Product Factory:** `ISellableProducts`: the product versions on sale, their limits and templates.

**Database:** V0016 (OQ-059). Two new tables, `general.quote_risk` and `general.quote_coverage`.

**Phase 3 defects found and fixed by the Phase 4 process tests:**
- a credit note's adjustment was inserted before the credit note (`fk_adjustment_credit_note_id`);
- a payment reversal pointed its allocations to a row that did not exist (`fk_payment_allocation_reversed_by_id`); reversals now write reversing allocation rows.

**Tests:**
- **Unit tests:** General, with every lifecycle checked against domain-model.json.
- **End to end:** "General quote to cash" and "General claim to settlement", with their compensations: document generation dead-lettered and regenerated by replay; a payment reversed and the invoice reopened; a settlement approval rejected; a payout failed and re-released to corrected details. Also an endorsement and a cancellation.
- **Tenant isolation:** General's API and its module role.

## Phase 3: what exists

**Finance** (`finance`):
- **Journal:** PostBusinessEvent and ReverseJournal. Debits equal credits in one currency; every line stores its posting rule code and version, its base amount and FX rate.
- **AccountingPeriod:** Future → Open → Soft close → Closed. Reopening a soft-closed period waits for a second person's approval in Workflow.
- **PostingRuleResolver:** exactly one rule in force on the accounting date whose condition holds.
- **PostingException:** an event that cannot be posted is kept with its payload and can be retried, optionally on another date (FIN-007).
- **Policies:** these events are posted: InvoiceIssued, CreditNoteIssued, PaymentReceived, PaymentSucceeded (refunds), PaymentReversed, ClaimReserveChanged, ClaimPaymentCompleted and ClaimRecoveryRecorded.
- **API:** `/finance`: journals, reversal, periods, trial balance, posting exceptions.

**Billing** (`billing`):
- **BillingAccount:** schedules and instalments (BIL-001), arrears by ageing bucket.
- **Invoice:** issue, allocate, reverse an allocation, overdue. A credit note is a separate document (BIL-004).
- **RefundRequest:** approved by a second person before any payment (SRS-002).
- **Jobs:** a billing job issues due invoices and ages accounts.
- **API:** `/billing`.

**Payments** (`payment`):
- **Payment:**
  - one per idempotency key (PAY-001);
  - every status change kept in an insert-only history (PAY-002);
  - signed provider callbacks;
  - Unknown outcomes go to a reconciliation exception and a person, never to success (PAY-005);
  - allocation never above the amount received;
  - reversal with its allocations.
- **PayoutBatch:** approved by someone other than its builder before release.
- **ReconciliationException and bank statement matching** (PAY-003).
- **Provider:** the `SIMULATOR` provider (OQ-058).
- **API:** `/payments`.

**Claims Framework** (`claims`):
- **ClaimRegistration:** one claim number per domain claim (CLM-005), evidence, communications, fraud indicators.
- **ClaimFinancials:** reserves, payments and recoveries as insert-only movements; a reserve never goes below zero (CLM-004).
- **ClaimPaymentInstruction:** within the approved amount; released only after Workflow approval; a retry is a new payment attempt.
- **API:** `/claims`.

**Database:**
- **V0014:**
  - deferred balance triggers on journals;
  - insert-only posted tables, with revoked grants and triggers;
  - reversal checks;
  - reopen request columns.
- **V0015:** CHECKs on the money lifecycles, plus the columns the aggregates need: payment business reference and batch, instruction reference and attempt, refund and batch makers and checkers, invoice allocated amount, and posting exception payload.
- **Decisions:** ADR-011; OQ-057 and OQ-058.

**Tests:**
- **Unit tests:** Finance, Billing, Payments and Claims Framework, including lifecycles checked against domain-model.json.
- **Database tests:** an unbalanced, one-sided or mixed-currency journal is refused at commit; module roles hold no UPDATE or DELETE on posted tables; even the owner is refused by the trigger; a journal is reversed at most once.
- **Mapping test:** every column a DbContext maps exists in the schema.
- **API flow tests:**
  - premium billed, collected, confirmed by a signed callback, allocated and posted with the posting rule version in force on the accounting date;
  - an Unknown outcome never treated as succeeded until a person resolves it;
  - a refund and its payout batch, each needing a second person;
  - a claim payment released only after approval, paid, and drawn from the reserve.

## Phase 2 review: what changed

1. **CI:** 5b7ee26 failed 3 of 509 tests. The OpenAPI contract tests timed out after HttpClient's default 100 seconds while the 1 400-path static data document was built on the GitHub runner. The tests now allow 10 minutes; 979e207 is green.
2. **Maker-checker (SRS-002), V0013:**
   - **Privileged roles:** a privileged role assignment is pending until a second person approves it in Workflow (OQ-038).
   - **Tier 2 switch-off:** switching Tier 2 maker-checker off is a Tier 1 change, approved the same way (OQ-046).
   - **Tests:** one person alone cannot complete either.
3. **V0012:**
   - status columns widened to varchar(30), with CHECKs from domain-model.json;
   - the loss assessment lifecycle;
   - `finance.accounting_period`, with one OPEN period per date. Its aggregate, including reopening with approval, comes in Phase 3;
   - the duplicate `status` columns are deprecated (ADR-010).
4. **SLA timers:** they run on business calendars, with public holidays (OQ-049).
5. **Rating and rules:**
   - the rating step order is configurable per product version (`STEP_*` parameters);
   - the cases so far are marked as engineering cases (`docs/rating/test-cases.md`);
   - OQ-051 and OQ-052 stay open for underwriting and actuarial decisions.
6. **Expressions:** limits of 4 000 characters, 32 levels of nesting and 10 000 evaluation steps, plus a seeded fuzz test. Two crash paths were fixed (a truncated list; a malformed or oversized number).

## Phase 2: what exists

**Building blocks**
- **Expression language (ADR-008):** governed conditions for rules and workflow guards. A missing variable is an error.
- **`ApprovalRequested` (ADR-009):** how any module asks Workflow for an approval.
- **Static data:** the generator can map read-only endpoints for tables an aggregate manages.
- **Tests:** `DrainOutbox` now relays until nothing is left, so chains of events complete.

**Modules**
- **Workflow:**
  - **Approval:** maker-checker plus authority by role, type and amount (SRS-002, WFL-004), with expiry, using V0010.
  - **ProcessInstance:** versioned definitions (WFL-001) and transition guards. The owning module's validation is called and fails closed (WFL-002). Tasks can be assigned, reassigned and escalated, with SLA timers (WFL-003). State is stored, so processes survive restarts (WFL-005).
  - Case notes, and a background job for expiry and timers.
- **Static Data approvals moved to Workflow:** the same approve and reject paths are now decided as Workflow approvals (Phase 0 item 7).
- **Rules:** EvaluateRuleSet for eligibility and underwriting. Each decision is recorded in `rules.decision_log` with its rule set version and inputs (LIF-002).
- **Rating:**
  - The reference engine (ADR-007). Each calculation is recorded with its breakdown, product version and rating version (PRD-008).
  - Dry-run simulation through `IPremiumSimulator`.
  - A configuration cache, dropped on ProductVersionPublished.
- **Product Factory:**
  - ProductDefinition's RegisterProduct.
  - The ProductVersion lifecycle: Draft → Validated → Submitted → Approved → Published → Effective → Withdrawn or Retired.
  - Validation against the coverage library and benefit dependencies (PRD-004, PRD-009), and simulation of the rating test cases (PRD-005).
  - Approval through Workflow (PRD-006), immutability after publication (PRD-002), new versions (PRD-003), effective dates (PRD-007).
  - Revalidation when product static data changes.
- **Party:**
  - Individuals and organisations (PTY-001); addresses, contact points, encrypted identifiers that are unique per type and country, relationships, and many roles (PTY-002).
  - Death, merge with history (PTY-004), and anonymisation.
  - ConsentLedger (PTY-005). Events carry no personal data.
- **Documents:**
  - Generation from template versions, with the template and product versions recorded (DOC-002), and upload.
  - Tenant-scoped storage (DOC-001); storage, rendering and delivery are stubs (OQ-056).
  - Delivery, legal holds, and disposal after retention.
  - Audited access to sensitive documents (DOC-003), with role checks for highly sensitive ones (DOC-004). Uses V0011.

**New migrations:**
- V0010: approval lifecycle columns.
- V0011: document status.

**Decisions:** ADR-007, ADR-008 and ADR-009; OQ-047 to OQ-056.

**End to end:** the "Product publication" process passes with its three compensations:
- validation errors keep Draft;
- a failed simulation case blocks submission;
- a rejected approval returns the version to Draft.

**Tests (21 Oct 2026):** 509 in total: 504 passed, 5 skipped (the end-to-end processes of later phases), 0 failed.

## Phase 1: what exists

**Phase 0 review items**
1. **CI:** the workflow runs on postgres:18 through Testcontainers. GitHub Actions is blocked on the account (see `open-questions.md`, OQ-021), so the same steps run locally on a clean clone.
2. **Partitions:** V0003 partitions are checked for forced RLS. A partition queried directly as a module role returns only the tenant's rows. The Audit module's job keeps partitions 3 months ahead and warns below 2 (ADR-005).
3. **Inboxes:** V0004 gives every module schema an `inbox_message`. Idempotency keys stay in `integration`.
4. **Roles:** one role and connection per module (ADR-001), with tests that a module role cannot write another module's schema.
5. **Permissions:** tokens carry sub and tenant_id only. Permissions are resolved per request from IAM, cached for 30 seconds, and evicted on change. A revoked role is denied on the next request.
6. **Global static data:** written only on the platform administrator connection, only from `/global` endpoints, and only by the operator tenant. Tests cover the API and the database.
7. **Maker-checker:** Tier 1 maker-checker through `sdm.change_request`, with the checker different from the maker (ADR-003).
8. **Error codes:** the catalogue is seeded from the `[ErrorCode]` constants by a generated repeatable migration.

**Modules**
- **Tenancy:** the Tenant aggregate, with its onboarding case (11 steps in load order) and status history. Commands: register, complete onboarding step, activate, suspend, reactivate, terminate, change isolation profile. Operator-only endpoints. Publishes `TenantSuspended`.
- **Identity:** the UserAccount aggregate, with role assignments and sessions. Commands: provision, assign role, revoke role, lock, unlock, disable. Enforces MFA for privileged roles and segregation-of-duties rules from static data. Provides the per-request principal resolver and its cache. PrivilegedAccessGrant and AccessReview wait on OQ-027.
- **Audit:** records every module's audit events and denied requests, offers audit queries, and runs the partition job and health check.
- **Static Data:** StaticDataChange (`sdm.change_request`) and ImportBatch, consumers for the owning modules' events, approve and reject for all tables, and change request and import queries.
- **All 22 modules** now exist with their own DbContext, role and generated static data. 18 of them have no aggregates yet.

**Static data:** `tools/Insys.StaticData.Generator` generates all 252 tables: entity, input with validation from the CHECK constraints, EF mapping, and endpoints. Each table supports:
- list and get as at a date, and versions;
- create draft, edit draft (maker only), submit, activate, deprecate, retire;
- all-or-nothing import;
- `/global` variants for scopes G and GT.

**New migrations:**
- V0004: inboxes in every module schema, plus `tenant` and `analytics` outboxes.
- V0005: partition functions.
- V0006: tenant register concurrency columns.
- `R__error_code_catalogue.sql`: the generated error code seed.

## Phase 0: what exists

**Solution** `Insys.slnx`: .NET 10 (SDK 10.0.203), nullable on, warnings as errors, central package versions in `Directory.Packages.props` (each package checked on nuget.org for version and an OSI licence).

**Building blocks** (`src/BuildingBlocks`)
- Domain: `Entity`, `AggregateRoot` (domain events, `RowVersion`), `ValueObject`, `IDomainEvent`, `Result` and `Error` with `ErrorKind`, `ErrorCodes`, `Guard`, `StateMachine` (`INVALID_STATE_TRANSITION`), `Money`, `CurrencyCode`, `RoundingRule`, `ConfigurationVersionRef`, `MakerChecker`.
- Contracts: `IIntegrationEvent`, `[IntegrationEvent("{context}.{Name}")]`, `EventEnvelope` (§23.2 metadata).
- Application: commands and queries, our own `Dispatcher` (no MediatR), FluentValidation pipeline, `ExecutionScope` (tenant and actor, set once, only from the principal or a service identity), `IClock`, `IEventBus`, `IIntegrationEventHandler`, `IDomainEventMapper`, `IDeadLetterSink`, `IAuditTrail`, `IFieldProtector`, `IIdempotencyStore`, `IStaticDataResolver` (interface only; Phase 1).
- Infrastructure: `ModuleDbContext` (own schema, snake-case columns, tenant query filter, row_version, audit columns, outbox write in SaveChanges), `TenantTransactionInterceptor` (`set_config('app.tenant_id', …, true)` first in every transaction), transactional command and read-only query pipeline, audit writer (in-transaction on success, separate transaction on failure), outbox relay with per-tenant polling, back-off and dead letters, in-process event bus, inbox, PostgreSQL idempotency store, AES-256-GCM field protector, health check.
- Api: ProblemDetails with stable `code`, exception mapping (including PostgreSQL errors to 409), JWT bearer (OIDC authority or a development key), permission policies (`RequirePermission`), tenant requirement as the fallback policy, `Idempotency-Key` filter, ETag and If-Match helpers, cursor pagination, per-tenant rate limiting, URL versioning `/api/v1`, OpenAPI document with bearer security and `x-permission`.

**Host** `src/Host/Insys.Api`: composition root, OpenTelemetry (OTLP when configured), `/health/live`, `/health/ready`, `/openapi/v1.json`. The OpenAPI document is generated at build time and committed as `docs/api/openapi-v1.json`.

**Sample module** Integration › DeadLetter (tenant-scoped, the Phase 0 sample endpoint): commands `DeadLetterMessage`, `ReplayMessage`, `DiscardMessage`; states Dead-lettered → Replayed or Discarded; INT-006 operator check; audit on replay and discard. Endpoints:
- `GET /api/v1/integration/dead-letters` (cursor, limit, status, source)
- `GET /api/v1/integration/dead-letters/{id}` (ETag)
- `POST /api/v1/integration/dead-letters/{id}/replay` (If-Match)
- `POST /api/v1/integration/dead-letters/{id}/discard` (If-Match)

**Database**
- `tools/Insys.DbMigrator`: forward-only runner with SHA-256 checksums, advisory lock and an out-of-order guard (DBA-017); optional grants for a non-owner app role.
- `db/migrations/V0003__monthly_partitions_2026_2027.sql`: monthly partitions with RLS (OQ-003).
- `db/roles/app_role_grants.sql`: local, test and CI grants (OQ-004).
- `docker-compose.yml`: PostgreSQL 18.

**Tests**: building blocks unit tests, Integration module unit tests, architecture tests, integration tests (migrations, RLS as the non-owner role, outbox, relay, inbox, dead letters, idempotency, API tenant isolation, OpenAPI contract), and end-to-end placeholders for the six processes (skipped until their phases).

**CI** `.github/workflows/ci.yml`: restore, build with `-warnaserror`, test with Testcontainers `postgres:18`, and a check that the OpenAPI document is committed.

## How to run

```bash
docker compose up -d postgres
INSYS_ROLE_PASSWORD=insys_local dotnet run --project tools/Insys.DbMigrator -- \
  --connection "Host=localhost;Port=55433;Database=insys;Username=insys_owner;Password=insys_owner_local" --roles db/roles \
  --bootstrap-operator 0199f000-0000-7000-8000-000000000001 --admin-subject <your IdP subject>
dotnet run --project src/Host/Insys.Api
dotnet run --project tools/Insys.StaticData.Generator          # regenerate static data code after V0002 or catalogue changes
dotnet test                                    # integration tests use Testcontainers postgres:18
INSYS_TEST_PG="Host=…;Username=postgres;Database=postgres" dotnet test   # or an existing server, without Docker
INSYS_UPDATE_OPENAPI=1 dotnet test             # accept intended API contract changes (docs/api/openapi-*.json)
INSYS_UPDATE_SNAPSHOTS=1 dotnet test           # regenerate the error code seed after adding [ErrorCode] constants
```
