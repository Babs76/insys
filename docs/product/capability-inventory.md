# INSYS Core: verified capability inventory

**As of 6 November 2026, at `main` commit `d488b32`.** The fact base for the INSYS product guide (customers, partners, internal teams). Not itself the guide: it is written for the guide's author.

## How to read this
- **Every capability here was checked against the code and tests on `main` at `d488b32`.** Each BUILT item names one test that exists on main; all 538 test names cited were checked to exist by a script. Tests were not re-run for this inventory: their results are main's CI on `d488b32` (run 42190151149, all green).
- **Statuses, exactly one per capability:**
  - **BUILT:** implemented and covered by tests (one test named).
  - **BUILT, STAND-IN:** works, but its outside service is a stub (named).
  - **PARTLY BUILT:** what's missing is said.
  - **SPECIFIED, NOT BUILT:** in the SRS or domain model only.
  - **DEFERRED:** by a recorded decision (ADR, OQ, backlog item B-0nn, or go-live register row).
- **Nothing is described as working because the SRS says so.** "Inferred:" marks a conclusion drawn rather than verified; "(from docs)" marks a statement taken from a document and not checked in code.
- **IDs:** SRS requirement IDs (`LIF-008`, from SRS v1.0 of 21 October 2026); `F0nn` the 103 required functions (`docs/traceability/functional-coverage.md`); `BR-nnn` business requests; `OQ-nnn` open questions (`docs/decisions/open-questions.md`); `ADR-0nn` decisions (`docs/decisions/`); `B-0nn` backlog; **#n** a row of the go-live register (`docs/handover-backend.md`, "Go-live register").
- **Gathered by:** seven read-only research passes over the code, each told to verify in code and tests, then checked here: the cited tests' existence, the summary figures below, and the claims that disagree with existing documents (marked **Correction**).

## 1. Summary figures

All counted from the code or from main's CI at `d488b32`.

| Figure | Count | How it was counted |
|---|---|---|
| Modules (bounded contexts) | **24** | `src/Modules/`: Analytics, Assist, Audit, Billing, ClaimsFramework, Communications, Compliance, Distribution, Documents, Finance, General, Health, Identity, Integration, Life, Party, Payments, ProductFactory, Rating, Reinsurance, Rules, StaticData, Tenancy, Workflow |
| Lines of business | **3**: Life, General, Health | The product line code (`LIFE`, `GENERAL`, `HEALTH`); each has its own module |
| Functions in the catalogue (ADR-026) | **718** | `iam.ref_function_catalogue` after applying all 130 migrations to an empty PostgreSQL 18: 488 actions, 213 screens, 17 system jobs |
| … by audience | 409 staff only; 215 staff and platform operator; 12 platform operator only; 27 customer portal; 20 broker portal; 18 provider portal; 17 jobs (no audience) | same |
| … privileged | **33** | same; matches `RoleTemplates.Privileged` in code |
| Role templates | **25** | `docs/specs/addenda/role-templates.md`, generated from `RoleTemplates.cs` and snapshot-checked by `RoleTemplateTests`: 19 staff, 3 portal (broker, member, provider), 2 platform operator, plus the read-only auditor and Health auditor among the staff ones |
| Segregation-of-duties rules in code | **3** pairs | `RoleTemplates.SodRules` (see section 3) |
| API operations, business | **617** | 599 in the tenant API (`docs/api/openapi-core.json`) and 18 in the platform operator API that aren't static data. Counted from the committed OpenAPI documents, which `OpenApiContractTests` checks against the running API |
| … of which portal | 52 | `/api/v1/portal/...` in the tenant API |
| API operations, generic static data | **3,006** | 2,416 in the static data API (`openapi-static-data.json`) and 590 global static data operations in the platform operator API |
| Database tables | **552** (not counting partitions) | 185 tenant configuration (`cfg_`) tables and 95 reference (`ref_`) tables among them; in 27 schemas |
| SRS v1.0 requirements | **117** | `docs/specs/requirements.json`, `.srs` |

**Tests, from main's CI run 42190151149 on `d488b32` (all passed):**

| Suite | Tests | Notes |
|---|---|---|
| Unit and architecture (24 assemblies) | **1,978** | 10 skipped (8 in Life, 2 in Building Blocks). Largest: architecture 945, BuildingBlocks 166, Assist 131, Health 130, Communications 111, Workflow 98, General 68, Identity 54, Party 49 |
| Integration (API and PostgreSQL 18, Testcontainers) | **667** | Run in 4 shards (96, 179, 184, 196), plus sweeps (7) and the seed (5) |
| End to end (cross-module journeys) | **74** | 40 Life, Health and enterprise plus 34 General and sweeps. Each journey set is also run with the clock fixed on the 28th, 30th, 31st, 29 February and 23:30 UTC at month end (40 and 32 tests per date) |
| Deployment smoke (`deployment-smoke`) | 1 workflow | The Helm chart deployed to a kind cluster and exercised; green on `d488b32` (run 42182931444 on the same tree) |

## 2. Modules

The 24 modules, in this order: Life with pricing, rules and products; General with claims, reinsurance and distribution; Health with parties and compliance; the money modules; the platform modules; and the shared services. "Role templates" names the templates whose sub-domains include the module's functions (`docs/specs/addenda/role-templates.md`).

### Life (`Life`)
- **For:** individual Life policies from proposal to claim, plus employer group schemes: proposals and quotes, underwriting, issue, servicing, lapse and reinstatement, loans and claims. **Lines:** Life.
- **Capabilities:**
  - Capture a proposal on a product's benefits, get a quote from Rating, submit it (it needs an "eligible" rule decision and is then frozen), withdraw it. **BUILT** (SRS: LIF-001). Test: `LifeTests.SubmitProposal_NeedsAnEligibleRuleDecision_AndThenTheContentIsFrozen_LIF001`; also `LifeTests.QuotePremium_SpreadsTheQuoteOverCoversBySumAssured_AndRejectsAnotherProduct_LIF001`.
  - Underwriting case: request, receive or waive evidence, refer, answer a referral, and record the rule set version used so the decision can be reproduced. **BUILT** (LIF-002). Test: `LifeTests.UnderwritingCase_RecordsTheRuleSetVersion_LIF002_AndWaitsForMandatoryEvidence`.
  - Underwriting decision with maker-checker: the underwriter decides alone only when the decision matches the rule outcome and is within their authority matrix; otherwise a second person approves the same terms in Workflow. **BUILT** (SRS-002). Test: `LifeTests.Decision_IsApprovedByAnotherPerson_ForTheSameTerms_SRS002`; `LifeTests.OneUnderwriter_DecidesAlone_OnlyWhenTheDecisionMatchesTheRuleOutcome`.
  - Issue a policy from an accepted proposal, on the product and rating versions used, with exclusions and beneficiaries; refused until the policyholder is cleared by due diligence and inception disclosures are acknowledged. **BUILT, STAND-IN** (screening, identity verification) (LIF-003, PRD-008, OQ-123). Test: `LifeTests.IssuePolicy_ReferencesTheProductAndRatingVersions_CarriesExclusions_AndBeneficiaries_LIF003`; `LifeProcessTests.ALifePolicy_ForAnUnclearedPolicyholder_IsRefused_AndIssuesOnceTheyAreCleared_OQ123`.
  - End-to-end new business: quote, eligibility, underwriting, issue, billing and policy documents. **BUILT** (LIF-001, LIF-005). Test: `LifeProcessTests.LifeProposalToPolicy_QuotesChecksUnderwritesIssuesBillsAndDocuments_WithItsCompensations_LIF001_LIF005`.
  - Change beneficiaries from a date, with history; shares total 100% on every date, within the product's nomination rule. **BUILT**. Test: `LifeTests.ChangeBeneficiaries_KeepsHistory_AndSharesTotalOneHundredOnEveryDate`.
  - Policy alterations checked against policy state, the alteration type and the type's authority role. **PARTLY BUILT**: only alterations that change neither cover nor premium are applied (the servicing intermediary); cover or premium changes need re-rating (OQ-052) or re-underwriting (OQ-061) (LIF-004). Test: `LifeTests.AlterPolicy_IsCheckedAgainstTheStatesTheAlterationTypeAllows_LIF004`.
  - Grace, arrears and lapse from Billing's missed premiums; a payment in grace clears arrears; billing stops on lapse. **BUILT** (LIF-005). Test: `LifeProcessTests.MissedPremiumToLapse_GraceThenLapse_BillingStops_AndReinstatementResumesIt_LIF005`.
  - Reinstate a lapsed policy within the product's window, with evidence where required; a lapsed policy is terminated when the window passes. **BUILT** (LIF-005). Test: `LifeTests.Reinstatement_IsWithinTheWindow_WithEvidenceWhereRequired_ThenTheWindowEnds_LIF005`.
  - Policy loans: only for products that allow them, up to a share of the cash value, with repayments. **PARTLY BUILT**: no interest accrual (OQ-052), no payout of the loan amount (OQ-061), and no cash value is ever calculated (see below) (LIF-007). Test: `LifeTests.Loans_AreForProductsThatSupportThem_WithinTheShareOfCashValue_AndRepaid_LIF007`.
  - Show the policy's latest cash value and its date. **PARTLY BUILT**: the policy shows a value only if a row exists in `life.cash_value`; nothing in INSYS writes one, so it is null in normal use (OQ-052, register #22). Test: `LifeProcessTests.PolicyLoans_AreListedInThePolicysCurrency_AndThePolicyShowsItsLatestCashValue_BR062_BR063_BR065` (inserts the rows directly as the database owner).
  - Surrender, make paid-up, mature. **DEFERRED** (OQ-052, register #22): routes exist but always refuse until actuarial defines the values. Skipped tests: `LifeTests.SurrenderPolicy_PaysTheSurrenderValueLessLoans_OQ052`, `MakePaidUp_...`, `MaturityCase_...`.
  - Issue on loaded or postponed underwriting terms. **DEFERRED** (OQ-052): refused with `LOADED_TERMS_NOT_RATED`. Test: `LifeTests.IssuePolicy_WithALoading_IsRefusedUntilLoadingsCanBeRated_OQ052`.
  - Cancel a policy in the cooling-off period with a premium refund, or terminate at the policyholder's request. **DEFERRED** (register #32, planned after the Preview). No command exists; the lifecycle has no cancelled state, and `life.ref_termination_reason_codes` is static data used by no code. Not in the SRS.
  - Death and other benefit claims: notify, register (policy status, insured life, benefit, exclusions and claimant checked), assess, approve with payees (net of loans, split by beneficiary shares), decline, close. **BUILT, STAND-IN** (payments: payout goes through `SimulatorGateway`, register #13) (LIF-006, LIF-008, CLM-003). Test: `LifeProcessTests.DeathClaim_IsCheckedRegisteredApprovedAndPaidToTheBeneficiary_ThenThePolicyIsClaimed_LIF008`; `LifeTests.ClaimApproval_SplitsTheNetAmountBySharesAfterTheLoan_AndIsBoundToItsSubject_CLM003`.
  - Claim approval by a second person within the claims authority limits for the claim type; partial approval with reason and rights notice; a declined claim reopened when a dispute is upheld. **BUILT** (SRS-002, BR-081, OQ-102, OQ-103). Test: `LifeTests.ApprovalBelowTheAmountClaimed_IsAPartialApproval_WithAPartialReason_AndARightsNotice_OQ102`; `LifeTests.UpheldDispute_ReopensADeclinedClaimForAssessment_OQ103`.
  - Group schemes: set up a scheme, enrol, change and remove members (cover as a salary multiple; cover above the free cover limit, banded by scheme size, needs evidence), accept member evidence, renew, terminate. **PARTLY BUILT**: no group premium rating (OQ-052); renewal simply moves one year (OQ-061). Test: `LifeTests.GroupMembers_CoverIsAMultipleOfSalary_AndAboveTheFreeCoverLimitNeedsEvidence`; `FreeCoverBandTests.MemberEnrolled_WithTheBandsLimit_NeedsEvidenceOnlyAboveIt_GAP043`.
  - Bring in-force policies from a source system, all or nothing, against control totals; outstanding premiums count as missed. **BUILT** (OQ-158, B-013). Test: `LifeProcessTests.AMigratedLifePolicy_IsInForce_WithNoInvoiceOrDocument_OQ158`.
  - Bring open claims from a source system (`POST /life/migration/claims`). **PARTLY BUILT**: code and route exist; no test found that calls them.
  - Portals: members and brokers list and view only their own Life policies and claims. **BUILT** (BR-091). Test: `PortalListTests.MembersAndBrokers_ListOnlyTheirOwnRecords_AndNeverAnothersByID_BR091`.
  - Staff scoped to an organisation see and act only within it. **BUILT** (F103). Test: `LifeProcessTests.ScopedAssignments_SeeAndActOnlyWithinTheirOrganisation_F103`.
- **Key business rules:**
  - Maker-checker on underwriting decisions outside rule outcome or authority, and on all claim approvals; checker differs from maker (SRS-002). BUILT.
  - Every ending of an approval other than a grant puts the case or claim back in assessment, applied once (D-5). BUILT (`LifeTests.UnderwritingDecision_EveryEndingOtherThanApproval_KeepsTheCaseInAssessment_AndARedeliveryIsAlreadyApplied`).
  - Underwriting authority from `rules.cfg_authority_matrices`; claims authority from `life.cfg_claims_authority_limits` by claim type (BR-081). BUILT.
  - Beneficiary shares total 100% on every date. BUILT.
  - Cash, surrender and loan features only for products that support them (LIF-007). BUILT for loans; the values themselves are DEFERRED (OQ-052).
  - Lifecycles match the domain model. BUILT (`LifeTests.Lifecycles_MatchDomainModel`).
  - Automatic accept, refer or decline from the rule set alone. DEFERRED (OQ-051; skipped test `LifeTests.AutomaticUnderwriting_AcceptsReferOrDeclinesFromTheRuleSet_OQ051`). Today the rule outcome only decides whether one underwriter may decide alone.
- **Role templates:** `LIFE_UNDERWRITER` (`LIFE_NEW_BUSINESS`, `LIFE_UNDERWRITING`, `LIFE_POLICY_ADMIN`, `LIFE_POLICY_VALUES`, `LIFE_GROUP_SCHEMES`; approves `LIFE_UNDERWRITING`); `CLAIMS_HANDLER` and `CLAIMS_MANAGER` (`LIFE_CLAIMS`; the manager approves `LIFE_CLAIM`); `LINE_CONFIGURATION_MANAGER` (`CONFIG_LIFE`); `MEMBER` and `BROKER` portal templates (`CUSTOMER_PORTAL_LIFE_POLICIES`, `CUSTOMER_PORTAL_LIFE_CLAIMS`, `BROKER_PORTAL_LIFE_POLICIES`, `BROKER_PORTAL_LIFE_CLAIMS`); `AUDITOR` reads.
- **Tenant configures:** products (Product Factory) and static data such as `life.cfg_grace_period_rules`, `life.cfg_lapse_and_reinstatement_rules`, `life.cfg_alteration_types`, `life.cfg_nomination_rules`, `life.cfg_loan_eligibility_limits`, `life.cfg_policy_loan_rates`, `life.cfg_free_cover_limits`, `life.cfg_claims_authority_limits`, `life.cfg_policy_numbering_schemes`, `life.cfg_surrender_value_scales` (stored but not used, OQ-052); reference codes such as `life.ref_life_claim_types`, `life.ref_termination_reason_codes`. Authority limits: `life.cfg_claims_authority_limits`, `rules.cfg_authority_matrices`.
- **Outside services:** due diligence screening and identity verification at issue (through Party; stub, register #11); claim payouts (through the payments interface `IPaymentGateway`, only implementation `SimulatorGateway`, register #13); policy documents and notices (through Documents and Communications; email and SMS senders are stubs, register #6, #7).
- **Known gaps:**
  - Cooling-off cancellation and termination on request: register #32.
  - Cash values, surrender, paid-up and maturity values, loan interest, loaded premiums and group premium rating: OQ-052, register #22.
  - Automatic underwriting outcome: OQ-051.
  - Cover-changing alterations, loan payout, scheme renewal term: OQ-061.
  - Underwriters give the rule inputs themselves: register #33 (see Rules).
  - Underwriter or claims handler who also holds line configuration: register #34.
  - Life claim migration has no test (no register row found).
- **Notes for the writer:**
  - Inferred: because no cash value is ever written, in normal use a policy loan is refused (`LOAN_EXCEEDS_CASH_VALUE`, "The policy has no cash value"), so loans can't be used in practice until register #22 is closed. The loan rules are tested with a cash value supplied directly to the domain.
  - Disability, critical illness and other claim types (LIF-006) go through the same claim flow, driven by `life.ref_life_claim_types`; only the death claim is tested end to end (inferred for the others).
  - The cooling-off notice exists as disclosure wording (per register #32, not verified in code), but there is no cancellation behind it.

### Rating (`Rating`)
- **For:** calculates premiums from a product version's rating configuration, records each calculation with its breakdown, and dry-runs test cases for Product Factory. **Lines:** shared (Life and General use it).
- **Capabilities:**
  - Calculate a premium and record it with its inputs, as-at date, product version, rating version and step-by-step breakdown; list a subject's calculations. **BUILT** (PRD-008, LIF-002). Test: `SharedServicesTests.PremiumCalculation_IsRecordedWithItsBreakdown_AndIsTenantScoped_PRD008`.
  - Rate without recording (simulation), via the API and for Product Factory's test case runs. **BUILT** (PRD-005). Test: `SharedServicesTests.PremiumCalculation_IsRecordedWithItsBreakdown_AndIsTenantScoped_PRD008` (calls `/rating/calculations/simulate`); `ProductPublicationTests.ProductPublication_FromDraftToEffective_WithItsCompensations`.
  - The reference rating engine (ADR-007): rating tables times exposure, banded factors, linear interpolation, factor and percent parameters, named discounts and loadings with caps, fees, minimum and maximum premiums, rounding, then taxes and levies by jurisdiction. **BUILT**. Test: `RatingEngineTests.Premium_FollowsTheReferenceSteps`; `RatingEngineTests.LinearTable_Interpolates`; `RatingEngineTests.PremiumBelowTheMinimum_IsRaisedToIt`.
  - Step order configurable per product version, with the reference order as default. **BUILT** (OQ-052). Test: `RatingEngineTests.StepOrder_IsConfiguredPerProductVersion`.
  - Same inputs and configuration always give the same result. **BUILT** (LIF-002). Test: `RatingEngineTests.SameInputsAndConfiguration_GiveTheSameResult_LIF002`.
  - Missing inputs, missing table values, unknown adjustments or missing rounding rules are errors, never a zero premium. **BUILT** (ADR-007). Test: `RatingEngineTests.MissingInput_IsAnError_NotAZeroPremium`.
  - Rating on request from Life and General through events (`PremiumCalculated`, or `PremiumNotCalculated` with a reason). **BUILT** (BR-095). Test: `LifeProcessTests.LifeProposalToPolicy_QuotesChecksUnderwritesIssuesBillsAndDocuments_WithItsCompensations_LIF001_LIF005`; `GeneralProcessTests.ALoadedMonthlyQuote_IsBilledAsQuoted_TheYearTotallingTheLoadedPremium`.
  - Rating test case library; a case must carry currency and frequency. **BUILT** (PRD-005, GAP-024). Test: `RatingTestCaseTests.ATestCase_NeedsCurrencyAndFrequency_InItsInputs_GAP024`.
  - Product tax rates as the one source for Billing's invoice tax. **BUILT** (OQ-058, F094). Test: `GeneralProcessTests.AnInvoicesTax_IsPostedToItsAccount_AndAReturnTotalsIt_F094_OQ058`.
  - Rating configuration cached per tenant. **BUILT** (TEN-004). Test: `CrossTenantSuiteTests.RatingCache_IsKeptPerTenant_TEN004`.
  - Rating within its latency target. **BUILT** (NFR-003; smoke test only). Test: `RatingPerformanceSmokeTests.PremiumCalculation_StaysWithinTheRatingLatencyTarget_NFR003`.
  - Onboarding readiness check RATING (tables in force; a rating configuration for each sellable product version). **PARTLY BUILT**: code exists (`RatingReadiness`); no test found that checks this step's results.
- **Key business rules:**
  - Money is decimal with currency; rounding by currency rule (EACH_FACTOR and FINAL_PREMIUM stages). BUILT (`RatingEngineTests.Rounding_FollowsItsMethodAndStep`). Rounding by currency for specific markets: register #26.
  - Rating version recorded is the highest version among the tables used (ADR-007). BUILT.
  - Levies on commission are refused (OQ-052). BUILT as a refusal.
  - The algorithm is an engineering proposal awaiting an actuary: step order, cap rule, PERCENT parameters as loadings, and actuary-supplied test cases (OQ-052, register #22). Open.
- **Role templates:** `PRODUCT_MANAGER` (`RATING`, `CONFIG_RATING`); `PLATFORM_STATIC_DATA_STEWARD` for global baselines; `AUDITOR` reads.
- **Tenant configures:** `rating.cfg_rating_factor_definitions`, `rating.cfg_rating_tables`, `rating.cfg_premium_parameters`, `rating.cfg_discount_and_loading_schedules`, `rating.cfg_premium_bounds`, `rating.cfg_premium_tax_and_levy_rates`, `rating.cfg_rounding_and_currency_precision_rules`, `rating.cfg_rating_test_case_library`; `rating.ref_risk_classes`.
- **Outside services:** none.
- **Known gaps:** actuarial sign-off of the algorithm and real test cases (OQ-052, register #22); Life loaded premiums depend on it (see Life).
- **Notes for the writer:**
  - The rating engine works and is tested; what's open is whether its step order and loading rules match the customer's actuarial basis. Present it as "configurable reference engine, pending actuarial confirmation", not as an approved basis.
  - The expression language (ADR-008) belongs to Rules and Workflow, not Rating; Rating uses tables and parameters, not expressions.

### Rules (`Rules`)
- **For:** evaluates eligibility and underwriting rule sets written in a small condition language, records each decision with its rule set version and inputs, and holds underwriting authority matrices. **Lines:** shared (Life, General; Health reads its decision reason codes).
- **Capabilities:**
  - Evaluate a rule set and record the decision with its version and inputs, audited; a failed evaluation records nothing. **BUILT** (LIF-002, PRD-008). Test: `SharedServicesTests.RuleDecision_IsRecordedWithItsVersionAndInputs_AndAFailedEvaluationRecordsNothing_LIF002`.
  - Eligibility rule sets: the most severe outcome that fires is the decision; eligibility is never assumed when nothing fires. **BUILT** (OQ-051 proposal). Test: `RuleDecisionTests.MostSevereFiredOutcome_IsTheDecision_AndTheVersionIsRecorded_LIF002`; `RuleDecisionTests.Eligibility_IsNotAssumed_WhenNoRuleFires`.
  - Underwriting rule sets from acceptance and referral rules plus decline conditions; decline wins. **BUILT**. Test: `RuleDecisionTests.Underwriting_ReadsTheRulesThreshold_AndDeclineWins`.
  - A missing input refers the decision, naming the rule and input. **BUILT** (BR-113). Test: `RuleDecisionTests.MissingInput_RefersTheDecision_NamingTheRuleAndInput_BR113`.
  - Evaluation requested by another module without a person (renewal review), limited to that product's and domain's rules. **BUILT** (BR-113). Test: `GeneralProcessTests.AGeneralRenewalReview_EvaluatesOnlyItsProductsAndDomainsRules_NeverALifeRule_BR113`.
  - A subject's decision history, newest first. **BUILT** (BR-025). Test: `SubjectHistoryPagingTests.ASubjectsHistory_PagesNewestFirst_EveryRecordOnce_BR025`.
  - Underwriting authority matrices (role, product, maximum sum assured, decision types) for Life and General. **BUILT** (§7.4). Test: `LifeTests.OneUnderwriter_DecidesAlone_OnlyWhenTheDecisionMatchesTheRuleOutcome`; `OnboardingReadinessTests.AGeneralProductNoRoleMayDecideReferralsFor_IsListed_UntilOneMay`.
  - Readiness warning for underwriting rules not scoped to a product or domain. **BUILT** (BR-113). Test: `OnboardingReadinessTests.AnUnscopedUnderwritingRule_IsListedAsAWarning_ThatDoesNotStopTheStep_BR113`.
  - Condition language (ADR-008): AND/OR/NOT, comparisons, IN lists, BETWEEN, bounded arithmetic and dates, `today()`; length, nesting and step limits; malformed input fails safely. **BUILT**. Test: `ExpressionTests.CatalogueSample_EligibilityRuleSet`; `ExpressionLimitTests.Fuzz_MalformedInput_FailsSafely`; `ExpressionArithmeticTests.Arithmetic_StaysWithinTheLimits`.
  - Underwriting and eligibility decisions on a proposal or quote read the case's facts from the proposal itself. **PARTLY BUILT** (register #33): today `POST /rules/decisions` evaluates whatever inputs the caller sends (`EvaluateRuleSetHandler` uses `command.Inputs`), so an underwriter could choose inputs that keep a case within their own authority. It is recorded and audited (`EvaluateRuleSet`), so it can be found, but not prevented.
  - Evidence requirement grids, question bank and questionnaires. **PARTLY BUILT** (inferred): the tables are configurable static data, but no Rules or Life code reads them; Life evidence is requested by the underwriter by hand.
- **Key business rules:**
  - A missing variable is an evaluation error, never a silent false (ADR-008). BUILT.
  - The most severe outcome wins (OQ-051, open: underwriting's decision pending; code follows the proposal).
  - Inputs come from the caller (register #33). Open, blocks go-live.
- **Role templates:** `PRODUCT_MANAGER` (`RULES`, `CONFIG_RULES`); `PLATFORM_STATIC_DATA_STEWARD` for global baselines; `AUDITOR` reads. Inferred: Life and General underwriters reach rule decisions through their own modules' flows.
- **Tenant configures:** `rules.cfg_eligibility_rule_sets`, `rules.cfg_acceptance_and_referral_rules`, `rules.cfg_decline_conditions`, `rules.cfg_authority_matrices` (underwriting authority limits), `rules.cfg_evidence_requirement_grids`, `rules.cfg_question_bank_and_questionnaires`; `rules.ref_decision_reason_codes`.
- **Outside services:** none.
- **Known gaps:** register #33 (inputs from the caller); OQ-051 (how a rule set decides, unconfirmed); register #34 (no segregation rule stopping one user from both configuring rules and underwriting under them; the three segregation rules in `RoleTemplates.cs` cover claims payments, payout batches and bank accounts only).
- **Notes for the writer:** the language has no functions beyond `year()` and `today()`, no assignment and no loops, by design (ADR-008).

### Product Factory (`ProductFactory`)
- **For:** defines products and their versions, validates them, runs rating test cases, and publishes them through maker-checker approval; tells other modules which product versions can be sold. **Lines:** shared (Life, General, Health).
- **Capabilities:**
  - Register a product (version 1 in Draft), list products, view a product with all its versions. **BUILT** (PRD-001, BR-024). Test: `ProductPublicationTests.ProductPublication_FromDraftToEffective_WithItsCompensations`.
  - Edit a draft version (coverages, eligibility rule set, reinstatement arrears, effective date); editing a validated version returns it to draft. **BUILT**. Test: `ProductVersionTests.EditingAValidatedVersion_ReturnsItToDraft`; `ProductVersionTests.EligibilityRuleSetAndReinstatementArrears_AreEditedInDraft_AndCarriedToTheNextVersion`.
  - Validate a version: coverages in the library and of the product's domain, benefit dependencies, effective date not in the past. **BUILT** (PRD-004, PRD-007, PRD-009). Test: `ProductVersionTests.Validation_RefusesUnknownWrongDomainAndIncompatibleBenefits_PRD004_PRD009`.
  - Run the rating test case library against a validated version; a failed or missing run blocks submission (switchable per tenant). **BUILT** (PRD-005). Test: `ProductVersionTests.Submission_WithoutAPassedSimulation_IsRefused_PRD005`.
  - Submit for publication approval; a different person with `PRODUCT_PUBLICATION` authority approves in Workflow (self-approval refused); approval publishes, rejection returns to Draft. **BUILT** (PRD-006, SRS-002). Test: `ProductPublicationTests.ProductPublication_FromDraftToEffective_WithItsCompensations`; `ProductVersionTests.Submission_AsksWorkflowForApproval_PRD006`.
  - Published versions are immutable; a change is a new version that supersedes the old. **BUILT** (PRD-002, PRD-003). Test: `ProductVersionTests.PublishedVersion_IsImmutable_PRD002`; `ProductVersionTests.ChangeToAPublishedProduct_IsANewVersion_ThatSupersedesIt_PRD003`.
  - A published version becomes Effective on its effective date (daily job). **BUILT** (PRD-007). Test: `ProductVersionTests.Version_BecomesEffective_OnItsEffectiveDate`.
  - Withdraw a version from a date, only when nothing references it; retire an effective version. **BUILT** (BR-030). Test: `ProductVersionWithdrawalTests.APublishedVersionNotYetEffective_IsWithdrawnOnlyWhenNothingReferencesIt_AndNeverRetired_BR030`.
  - Sellable products for Life, General and Health: domain, line, coverages and limits, when cover starts, sale window from availability rules, document templates, exclusion codes. **BUILT** (inferred from code for the sale window). Test: `HealthPlanProductTests.APlanOption_NamesItsProduct_TheSameForEveryOptionOfThePlan_OQ080`; `LifeTests.Proposal_CoversOnlyTheProductsBenefits`.
  - Revalidation after a coverage library change returns an invalid version to draft. **BUILT**. Test: `ProductVersionTests.Revalidation_AfterALibraryChange_ReturnsAnInvalidVersionToDraft`.
- **Key business rules:**
  - Maker-checker on publication, checker differs from maker (PRD-006, SRS-002). BUILT.
  - Simulation required before submission unless the tenant's `product.cfg_product_approval_workflow_settings.simulation_required` says otherwise (PRD-005). BUILT.
  - Immutability and versioning (PRD-001 to PRD-003). BUILT.
  - Every transaction records the product version used (PRD-008). BUILT (see Life, Rating, Rules).
- **Role templates:** `PRODUCT_MANAGER` (`PRODUCT_FACTORY`, `CONFIG_PRODUCT_FACTORY`, plus Rating and Rules; approves `PRODUCT_PUBLICATION`), so one product manager drafts and another approves; `PLATFORM_STATIC_DATA_STEWARD` for global baselines; `AUDITOR` reads.
- **Tenant configures:** `product.cfg_coverage_and_benefit_library`, `product.cfg_benefit_dependency_rules`, `product.cfg_limit_and_deductible_schedules`, `product.cfg_exclusion_library`, `product.cfg_waiting_period_rules` (read by Health), `product.cfg_product_availability_rules`, `product.cfg_product_template_assignments`, `product.cfg_product_approval_workflow_settings`, `product.cfg_product_eligibility_criteria`; `product.ref_product_taxonomy`. Products themselves are rows of `product.cfg_product_definitions_and_versions` (ADR-010).
- **Outside services:** none.
- **Known gaps:** none found on the register. See notes.
- **Notes for the writer:**
  - Inferred: `approver_roles` and `min_approvers` in `product.cfg_product_approval_workflow_settings` are not read by any code; only `simulation_required` is. Who may approve comes from Workflow's approval authority for `PRODUCT_PUBLICATION`, and approval is one checker. Don't describe configurable approver roles or multiple approvers for publication.
  - Inferred: `product.cfg_product_eligibility_criteria` is not read by any code; eligibility is through the version's eligibility rule set in Rules.
  - The rating cache isn't preloaded on publication; the first calculation loads it (OQ-052, from docs, not verified in code).

### General insurance (`General`)
- **For:** short-term (property and casualty) business: insured items, quotes, policies, endorsements, renewals, claims, assessment, salvage and recovery. The product line is configuration (motor, property, marine, commercial, through risk object types and products). **Lines:** General.
- **Capabilities:**
  - Register insured items (risk objects) whose attributes follow a configurable schema per type, with valuation basis and currency, owner and deactivation. **BUILT** (SRS: GEN-002, GEN-005; BR-033, BR-092). Test: `GeneralTests.AttributeSchema_ValidatesTypesMandatoryFieldsAndRules_GEN005`.
  - Quote and rate: create quotes and versions, rated only by a matching Rating calculation, within product coverages and limits; issue, accept within validity, reject. **BUILT** (SRS: GEN-001, PRD-008). Test: `GeneralTests.Quote_IsAcceptedOnlyWithinItsValidity_GEN001`.
  - Refer a quote to an underwriter; the referrer cannot decide it; a declined referral is final. **BUILT** (BR-037). Test: `GeneralTests.TheReferrer_DoesNotDecideTheReferral_BR037`.
  - Proposal and policy issue from an accepted quote, with several risks per policy; the policy premium equals the sum of its coverage premiums; cover can start on first payment. **BUILT** (SRS: GEN-001, GEN-002). Test: `GeneralTests.Policy_IsIssuedOnlyFromAnAcceptedQuote_AndInForceOnlyFromItsStart`; end to end `GeneralProcessTests.GeneralQuoteToCash_WithItsCompensations`.
  - Endorsements effective within the policy period, applied only after a second person approves; includes premium adjustment, billing frequency change and broker of record change (with appointment letter). **BUILT** (SRS: GEN-003, SRS-002; B-001, B-007, B-008). Test: `GeneralTests.Endorsement_IsAppliedOnlyAfterASecondPersonApproves_SRS002`; `GeneralProcessTests.APremiumAdjustmentAndAFrequencyChange_AreApproved_AndBillingFollows_B007_B008`.
  - Cancel and reinstate: refund pro rata or by short-period scale, less a fee, by the reason's refund rule, credited through Billing. **BUILT** (SRS: GEN-004; F007). Test: `GeneralTests.Cancellation_RefundsProRata_LessTheFee_AndIsKeptOnReinstatement_GEN004`.
  - Grace period and non-payment: grace from the oldest unpaid instalment, notice once, cancellation for non-payment after grace; a loss in grace is accepted and its payment held for the arrears or paid net of them. **BUILT** (F006, OQ-137). Test: `GeneralProcessTests.ALossInGrace_IsAccepted_ItsPaymentHeldUntilPaid_OrPaidLessTheArrears_OrDeclinedIfGraceEndsUnpaid_F006`.
  - Renewals, run by a daily job per tenant business date: review (claims history, risk changes, eligibility, re-rating, referral), invitation and reminders, then automatic renewal, renewal on acceptance or on first payment; renewal billed. **BUILT** (SRS: GEN-004; F014 to F024; OQ-130, OQ-131). Test: `GeneralProcessTests.ARenewal_IsReviewedReRatedAndReferred_ThenOfferedInvitedAndRenewedOnTheOfferedTerms_F015_F019`; `EnterpriseProcessTests.RenewalJob_InvitesThenRenewsByItself_AndTheExposureSnapshotSumsByZoneAndPeril_AndTheRenewalIsBilled_GEN004_F023`.
  - Insurer non-renewal with reason, approval and notice; a late notice extends cover pro rata. **BUILT** (F025, OQ-132). Test: `RenewalAcceptanceTests.ALateNonRenewal_ExtendsCoverUntilTheNoticeRunsOut_AndARejectedOneChangesNothing_OQ132`.
  - Retention cases: opened on a cancellation request or lapse risk; offers from the tenant's list; outcome RETAINED, LOST or WITHDRAWN; figures counted. **BUILT** (F026, F027). Test: `RetentionTests.ARetentionDiscount_OnADeclinedRenewal_LowersItsPremiumAndAcceptsIt_F026`.
  - Accumulation control: zone and peril limits refer quotes over the limit; daily exposure snapshot by zone and peril; daily aggregation alerts. **BUILT** (F062, GAP-051). Test: `GeneralProcessTests.ZoneLimit_RefersAQuoteOverIt_AndNamesItToTheEndorsementApprover_F062`.
  - Claims: first notification of loss (staff, member portal), registration only with valid cover, claim parties, catastrophe event code; future loss dates refused by tenant date. **BUILT** (SRS: GEN-006; BR-124). Test: `GeneralTests.Claim_IsRegisteredOnlyWithValidCover_GEN006`.
  - Loss assessment: assessor appointed only from an approved panel; repairer estimates; report applies the coverage excess; accept or request further work. **BUILT** (SRS: GEN-007, GEN-008). Test: `GeneralTests.Assessment_NeedsAPanelAssessor_GEN007_AndReportsWithinItsLifecycle`.
  - Settlement: assessed amount less excess, capped by sum insured, limits, sublimits and remaining aggregate; second-person approval in Workflow; partial approval with reason and rights notice; decline with rationale; payment instructed and released automatically. **BUILT** (SRS: GEN-008, CLM-003, SRS-002; OQ-102). Test: `GeneralTests.Settlement_NeedsASecondPersonsApproval_AndRecordsItsRationale_SRS002_CLM003`.
  - Close and reopen claims; an upheld dispute reopens a declined or closed claim. **BUILT** (F013, OQ-103). Test: `GeneralTests.UpheldDispute_ReopensADeclinedOrClosedClaim_AndLeavesAnOpenOneAlone_OQ103`.
  - Salvage and third-party recovery: open a recovery, send demand, add and dispose salvage with proceeds, record money recovered (a claim financial movement), write off, close. **BUILT** (SRS: GEN-007, CLM-004; BR-032). Test: `GeneralTests.Recovery_IsRecordedForTheClaimsFramework_CLM004`; `ClaimRecoveriesTests.AClaimsRecoveries_AreListedOnceEach_AndOnlyWithinScope_BR032`.
  - Customer and broker portals: policies, claims, loss codes and loss notification (member), renewal answers, broker quotes priced as made, broker-registered insured items for its clients. **BUILT** (BR-091 to BR-095). Test: `GeneralProcessTests.ExternalUsers_SeeAndActOnlyWithinTheirRelationship_ThroughTheirPortal_ExternalUserAccess`.
  - Data migration of in-force policies and open claims, all or nothing, against control totals. **BUILT** (OQ-158, B-013). Test: `GeneralProcessTests.AMigrationBatch_ReconcilesAgainstTheSourceSystem_AndReportsEveryDifference_B013`.
- **Key business rules:**
  - Maker-checker on endorsements, settlements and non-renewals through Workflow; the checker is never the maker (SRS-002). BUILT (`GeneralTests.Settlement_NeedsASecondPersonsApproval_AndRecordsItsRationale_SRS002_CLM003`).
  - Settlement authority: Workflow's delegation and approval authority (`workflow.cfg_delegation_and_approval_authority`, approval type `CLAIM_SETTLEMENT`) decides. `general.cfg_settlement_authority_limits` exists but is **not read** by code (recorded in `open-questions.md` under OQ-059: "duplicates it and is not read"). Verified: no reference outside generated code.
  - Valuation rules beyond excess, sum insured, limits and aggregate are not specified (OQ-059; GEN-008 asks for "applicable valuation rules"). PARTLY BUILT.
  - Short-period cancellation without a configured scale is refused (OQ-059). BUILT (`GeneralTests.Cancellation_WithAnUnspecifiedScale_IsRefused`).
  - Recovery write-off is one person's action, with no reason and no second person (verified in `RecoveryCase.WriteOff()`; contrast Health's write-off, which needs both). No requirement ID found that demands more.
  - Quotes and policies through a suspended intermediary, or one without a product permission, are refused (DST-001, ADR-014). BUILT (`EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`).
- **Role templates:** `GENERAL_UNDERWRITER` (`GENERAL_RISK_OBJECTS`, `GENERAL_QUOTATION`, `GENERAL_POLICY_ADMIN`; approves `ENDORSEMENT`). `CLAIMS_HANDLER` and `CLAIMS_MANAGER` (`GENERAL_CLAIMS`, `GENERAL_RECOVERIES`; the manager approves `CLAIM_SETTLEMENT`). `COMPLIANCE_OFFICER` (`RISK_LIMITS`: aggregation alerts). `LINE_CONFIGURATION_MANAGER` (`CONFIG_GENERAL`). `BROKER` and `MEMBER` portal templates. `AUDITOR` reads all. **No template holds the `GENERAL_RENEWALS` sub-domain** (decide a renewal, record a renewal answer, propose non-renewal, retention cases): `GENERAL_DECIDE_RENEWAL`, `GENERAL_PROPOSE_NON_RENEWAL` and `GENERAL_WORK_RETENTION_CASE` appear in no template's function list in `role-templates.md`.
- **Tenant configures:** products (Product Factory) and rating; `general.cfg_risk_object_types_and_attribute_schemas`, `cfg_quote_validity_periods`, `cfg_policy_numbering_schemes`, `cfg_endorsement_types`, `cfg_cancellation_reasons_and_refund_rules`, `cfg_short_period_scales`, `cfg_grace_period_rules`, `cfg_renewal_rules`, `cfg_renewal_review_rules`, `cfg_renewal_settings`, `cfg_non_renewal_reasons`, `cfg_retention_offers`, `cfg_aggregation_zones_and_limits`, `cfg_aggregate_limit_definitions`, `cfg_claims_experience_factors`, `cfg_claim_party_roles`; panels `mst_adjuster_and_surveyor_panels`, `mst_repairer_and_assessor_panels`; `mst_catastrophe_event_register`; many `ref_` code lists (perils, loss causes, risk zones, vehicle directory, vessel types). Authority limits: Workflow's delegation table (above).
- **Outside services:** none called directly. Through other modules: claim payouts and refunds via Payments (`SimulatorGateway`, stand-in, row 13); notices via Communications (email and SMS stand-ins, rows 6 and 7); payee screening via Party (screening stand-in, row 11). Inferred from the event flows; not traced to each class here.
- **Known gaps:**
  - No default role template for staff renewal and retention work (`GENERAL_RENEWALS`); a tenant must build its own role. Not found in the register, OQs or backlog.
  - Static data tables with no code reading them (verified by grep of class and table names outside `*.g.cs`): `cfg_exposure_extract_layouts` (no exposure extract to actuaries), `cfg_package_section_definitions`, `cfg_indemnity_period_options`, `cfg_extension_and_condition_library`, `cfg_special_conditions_and_clause_library`, `cfg_settlement_authority_limits`. No OQ or backlog item found for the first five.
  - Rules evaluation takes facts from the caller, not the quote (register row 33).
- **Notes for the writer:**
  - `open-questions.md` still lists "Renewal jobs: not run yet" under OQ-059; that is stale. The daily job exists (`GeneralJobs` in `GeneralInfrastructure.cs`) and is tested end to end.
  - `functional-coverage.md` marks F015 to F027 "Missing" in its original table; later waves built them (tests above). Use the tests, not that table.
  - "Motor, property, marine" are product configuration, not separate code paths (inferred from the generic risk-object design and the `line` check on `cfg_settlement_authority_limits`).

### Claims framework (`ClaimsFramework`)
- **For:** the claims services shared by the lines: one claim register and numbering, evidence and correspondence, fraud screening and cases, reserves, and claim payment instructions. Each line keeps its own adjudication (CLM-002). **Lines:** shared (Life and General use payments through it; the register accepts HEALTH as a domain).
- **Capabilities:**
  - Cross-line claim register: allocate a claim number by the tenant's scheme; a repeat returns the same number; search by number, domain, policy and registration time, within the user's product and organisation scope. **BUILT** (SRS: CLM-001, CLM-005; BR-034; F103). Test: `ClaimsTests.ClaimNumber_CarriesTheSchemePrefix_CLM005`; `ClaimsScopeTests.RegisteredClaim_IsReadOnlyWithinTheProductAndNodeItWasRegisteredWith_F103`.
  - Evidence: request evidence by type; record evidence received as a stored document, fulfilling the request. **BUILT** (BR-039). Test: `ClaimsEvidenceAndFraudTests.RecordingEvidence_RequiresItsDocument_AndRequestingTakesOnlyTheType_BR039`.
  - Claim correspondence log (inbound and outbound). **BUILT** (F101). Test: `GeneralProcessTests.ClaimStatusNotice_ReachesThePolicyholder_AndTheClaimLogsIt_F101`.
  - Fraud screening: configurable indicators scored against claim facts; refer at a threshold; indicators that cannot be evaluated are skipped, recorded and raised to the configuration owner. **BUILT** (F009, BR-039). Test: `ClaimsTests.FraudIndicators_ReferTheClaim_WhenTheTotalReachesTheThreshold`.
  - Fraud cases on a Life or General claim or policy: hidden from portals and the subject, investigator assigned, only the investigator concludes, every read audited. **BUILT** (F058). Test: `FraudCaseTests.OnlyTheInvestigator_Concludes`; `GeneralProcessTests.AFraudCaseIsHidden_AnImportIsAllOrNothing_AndAnExportWaitsForASecondPerson_F058_F076_OQ160`.
  - Reserves by reserve type, with reason; never below zero; payments draw them down; recoveries recorded as movements. **BUILT** (SRS: CLM-004). Test: `ClaimsTests.Reserve_NeverGoesBelowZero_CLM004`.
  - Claim payment instructions: draft (maker), submit for a second person's approval in Workflow within authority, release to Payments within the outstanding reserve. Approved General settlements and Life claims create and release their instructions automatically. **BUILT, STAND-IN** (payments: `SimulatorGateway`, register row 13) (SRS: SRS-002, CLM-004, PAY-001). Test: `MoneyFlowTests.ClaimPayment_IsReleasedOnlyAfterApproval_AndDrawsDownTheReserve_SRS002_CLM004`.
  - Held releases: an automatic release refused for reserve, changed payee account, payee not cleared by due diligence, or premium arrears is held with its reason and a task for the claims role. **BUILT** (F006, OQ-125). Test: `MoneyFlowTests.AutomaticRelease_RefusedByTheReserve_IsHeldWithItsReason_AndTasksTheClaimsRole`; `GeneralProcessTests.AClaimPayment_ToAPayeeWithAnUnreviewedHit_IsHeld_AndReleasedWhenTheHitIsCleared_DueDiligence5`.
  - Failed payments: retried to the approved account at once as a new attempt; a new payee or bank account (retry or redirect) needs a new approval first. **BUILT** (SRS-002). Test: `ClaimsTests.NewPayeeOrBankAccount_AfterApproval_NeedsANewApproval_SRS002`.
  - Settlement paid net of policy arrears: reserve drawn by the gross, gross, deduction and net recorded. **BUILT** (OQ-137). Test: `ClaimsTests.ASettlementPaidLessTheArrears_DrawsTheReserveByTheGross_AndRecordsGrossDeductionAndNet_OQ137`.
- **Key business rules:**
  - **Claim payment maker-checker, three layers:**
    1. Workflow approval: the checker is never the maker and needs authority for `CLAIM_PAYMENT` at the amount, in the currency (SRS-002, WFL-004, OQ-112). BUILT (`ApprovalTests.AuthorityLimits_CountOnlyInTheirCurrency_OQ112`; `WorkflowTests.Approval_IsDecidedBySomeoneElse_WithinTheirAuthority_WFL004`). The approval covers payee, bank account and amount by hash; a changed subject is refused (`ClaimsTests.AnInstructionApprovedByAnApproval_IsGrantedByIt_AndAChangedOneIsNot_D5`).
    2. Functions: `CLAIMS_DRAFT_INSTRUCTION` (permission `claim_payment.create`) is in `CLAIMS_HANDLER`; `CLAIMS_RELEASE_INSTRUCTION` (`claim_payment.release`) is in `CLAIMS_MANAGER`; each template excludes the other. Segregation rule "Who prepares a claim payment does not release it" in `RoleTemplates.SodRules`. BUILT (`RoleTemplateTests.EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111`). Holding `claim_payment.release` through another function (the handler's `CLAIMS_RETRY_INSTRUCTION` and `CLAIMS_REDIRECT_INSTRUCTION` carry it) does not grant release, because each operation also checks its function (ADR-026). BUILT (`FunctionAccessTests.PermissionHeldThroughAnotherFunction_DoesNotGrantTheOperationsFunction_ADR026`).
    3. User level: assigning a role is refused when the user would hold both functions of a BLOCK rule across their roles (`UserAccountTests.AssignRole_CombiningBlockedPermissions_IsRefused_SRS002`; `IdentityTests.AssignRole_ThatCombinesBlockedFunctions_IsRefused_SRS002`). **But** the rules it checks are the tenant's `iam.cfg_segregation_of_duties_conflict_rules`, not the code's `SodRules` list. I found no migration or code that loads the claim-payment pair into that table, so it applies only when the tenant configures it (inferred from grep; check onboarding).
    - The release step itself does not compare releaser with drafter (verified in `ClaimPaymentInstruction.ReleaseInstruction`); separation relies on the approval and the functions.
  - On a manually drafted instruction, the "approved amount" it must stay within comes from the request body (`DraftInstruction.ApprovedAmount`), not from the line's decision (verified in `DraftInstructionHandler`). The second person's approval and the reserve still bound it. Automatic instructions take the approved settlement amount.
  - **Claims authority limits:** General settlement (`CLAIM_SETTLEMENT`) and claim payment (`CLAIM_PAYMENT`) use Workflow's `cfg_delegation_and_approval_authority`. Life claims (`LIFE_CLAIM`) use `life.cfg_claims_authority_limits` by claim type and role (`ClaimsAuthority`, an `IApprovalAuthoritySource`). BUILT (`WorkflowTests.MyAuthority_ListsModuleLimitsAndTheStaticDataPermission_AsApprovingChecksThem_BR081`).
  - A payment is a positive amount to two decimals, never above the approved amount (PAY-001); one instruction per claim and reference. BUILT (`ClaimsTests.Instruction_AboveTheApprovedAmount_IsRefused`).
  - Fraud cases raise no events, so nothing tips off the subject. BUILT (`FraudCaseTests.ACase_RaisesNoEvent_SoNothingTipsOffItsSubject`).
- **Role templates:** `CLAIMS_HANDLER` (maker: `CLAIM_REGISTER`, `CLAIM_FINANCIALS`, without `CLAIMS_RELEASE_INSTRUCTION`); `CLAIMS_MANAGER` (releases, without `CLAIMS_DRAFT_INSTRUCTION`; approves `CLAIM_SETTLEMENT`, `LIFE_CLAIM`, `CLAIM_PAYMENT`); `FINANCE_MANAGER` also approves `CLAIM_PAYMENT`. Fraud case functions sit in `CLAIM_REGISTER`, so both claims templates hold them. `AUDITOR` reads.
- **Tenant configures:** `claims.cfg_claim_numbering_schemes`, `claims.cfg_fraud_indicator_definitions`; reference lists `ref_reserve_types`, `ref_evidence_types`, `ref_claim_decision_reason_codes`, `ref_claim_status_model`. Approval authority in Workflow (above); the role that gets held-release tasks is an option (`HeldReleaseRole`, default `CLAIMS_MANAGER`).
- **Outside services:** payouts through Payments, `SimulatorGateway` (stand-in, row 13); payee rescreening through Party's screening adapter `StubPartnerAdapters` (stand-in, row 11).
- **Known gaps:**
  - Nothing pays money out for real (row 13).
  - Whether fraud cases may be opened only manually is awaiting compliance (row 20).
  - The tenant's segregation-of-duties rule for draft and release is not seeded (see above; not in the register). Row 34 says segregation is checked "per role, not across a user's roles"; the code checks across the user's roles, against the tenant's rules.
- **Notes for the writer:** Health claims run their own payment runs in Health. I did not verify that Health claims are numbered through this register; the API accepts `HEALTH`. Inferred.

### Reinsurance (`Reinsurance`)
- **For:** automatic cession of policies to treaties or facultative arrangements, recoveries on settled claims, and bordereaux to reinsurers. Arithmetic per ADR-015 (Proposed, pending business sign-off). **Lines:** General and Life; Health is not ceded.
- **Capabilities:**
  - Cede a policy at issue under each proportional treaty whose cession rule names the product (quota share: retained percent; surplus: amount above retention; both up to capacity), or under a facultative arrangement for the policy; ceded premium in the same share, a Life premium annualised. Each cession records the gross figures and the treaty and rule versions, insert-only. **BUILT** for General (SRS: REI-001, REI-002; ADR-015). Test: `ReinsuranceTests.QuotaShare_CedesTheRestOfTheRetention_UpToCapacity_AndRecordsWhatItWasCalculatedFrom_REI001_REI002`; end to end `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`. Life: a handler exists (`LifePolicyIssued`); no Life cession test found.
  - Recalculate on endorsement: a new cession supersedes the old, with the premium change; a missing rule parks the event. **BUILT** (REI-002; ADR-022). Test: `ReinsuranceTests.Recalculation_IsANewCessionSupersedingTheOld_WithThePremiumChange`; `ConsumerParkingTests.Reinsurance_ParksARecalculationOnEndorsement_WhenTheCessionRuleIsNotInForce_ADR022`.
  - Identify recoveries on a settled General or Life claim (proportional share of what was paid; excess of loss above retention up to capacity) and bill them at once to the reinsurer; rebill by a person. **BUILT, STAND-IN** (reinsurer: `StubPartnerAdapters`, register row 12) (REI-003). Test: `EnterpriseProcessTests.Cancellation_ClawsBackUnearnedCommission_AndASettledClaimRecoversTheCededShare_REI003`.
  - Record what the reinsurer paid; the difference from the recoverable stays visible. **BUILT** (REI-003). Test: `ReinsuranceTests.Recoveries_AreTraceableAndReconcilable_REI003`.
  - Bordereaux per treaty and period (monthly or quarterly by layout): prepare as CSV in the layout's field order, one per treaty and period, download, send to the treaty's first participant, record agreement. **BUILT, STAND-IN** (reinsurer: `StubPartnerAdapters.SendBordereau`, row 12) (F051; BR-047). Test: `ReinsuranceTests.Bordereau_ListsTheTreatysCessionsInTheLayoutsFields_ForItsPeriod`.
  - Ledger postings: premium ceded, reinsurance recoverable, reinsurance recovered (through Finance). **BUILT** (REI-001). Test: `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002` (checks the CESSION journal).
  - Search cessions by policy, treaty or month; recoveries by claim or status; bordereaux by treaty, period, status. **BUILT** (BR-047 to BR-049). Test: none found that targets the filters; the lists are called in `EnterpriseProcessTests.Cancellation_ClawsBackUnearnedCommission_AndASettledClaimRecoversTheCededShare_REI003` and `CrossTenantSuiteTests` (inferred coverage only).
  - Stop loss and probable-maximum-loss bases. **DEFERRED** (ADR-015, OQ-063: refused, "no data"). Test of the refusal: `ReinsuranceTests.QuotaShare_CedesTheRestOfTheRetention_UpToCapacity_AndRecordsWhatItWasCalculatedFrom_REI001_REI002` (PML refused at line 79).
  - Return of ceded premium when a recalculation cedes less. **DEFERRED** (OQ-063; `TODO(spec)` in Finance: no transaction type, so nothing is posted).
  - Health cessions. **DEFERRED** (OQ-063; `TODO(spec)` in `ReinsurancePolicies`).
- **Key business rules:** cessions insert-only and reproducible (REI-002). Recoveries keep recoverable, billed and settled amounts (REI-003). One bordereau per treaty and period (`RI_BORDEREAU_EXISTS`). No second person on bordereau send or agree, or on recovery settlement (verified: no approval flow on these endpoints; none required by a requirement ID found). ADR-015 status: Proposed, pending business sign-off; its defaults are engineering cases.
- **Role templates:** `REINSURANCE_OFFICER` (`REINSURANCE`, `CONFIG_REINSURANCE`; 4 screens, 7 actions). `AUDITOR` reads. No approvals.
- **Tenant configures:** `reinsurance.cfg_treaty_definitions` (type, retention, capacity, period), `cfg_cession_rules` (product, allocation basis), `cfg_bordereaux_layouts` (fields, frequency), `mst_reinsurer_register`, `mst_facultative_arrangements`.
- **Outside services:** reinsurer adapter `IReinsurerAdapter` (`SendBordereau`, `BillRecovery`), implemented only by `StubPartnerAdapters` in Integration. It accepts every call with a `STUB-` reference and logs it in `integration.external_call_log`. **Stand-in** (register row 12).
- **Known gaps:** real reinsurer adapter (row 12, "If" sent electronically); stop loss, PML, ceded premium decrease, Health cessions, bordereaux kept as documents (OQ-063); reinsurance exposure monitoring against treaty capacity not found (F052 lead: snapshot built, no monitoring; zone alerts exist in General).
- **Notes for the writer:** because the stub accepts everything, every recovery is "billed" automatically in a demo. Life cession arithmetic is in code, with no test seen.

### Distribution and commission (`Distribution`)
- **For:** intermediary records and product permissions, commission accrual, clawback, statements and payment, broker of record changes, and the broker portal's statements. Rules per ADR-014 (Proposed, pending business sign-off). **Lines:** General and Life; Health memberships earn no commission (ADR-014).
- **Capabilities:**
  - Intermediary register with product permissions; suspend, reinstate and terminate as register versions through static data maker-checker, guarded by the lifecycle; check whether an intermediary may sell a product today; quotes through a suspended one are refused. **BUILT** (SRS: DST-001). Test: `CommissionTests.IntermediaryChanges_FollowItsLifecycle_DST001`; `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`.
  - Accrue commission on each issued invoice by the plan version in force (premium percent, fixed amount, or per policy on the first invoice), recording plan version and invoice; payable when the invoice is paid in full. **BUILT** (SRS: DST-002, DST-003; ADR-014). Test: `CommissionTests.Accrual_RecordsThePlanVersionAndSourceTransaction_ByTheBasis_DST002_DST003`.
  - Claw back unearned commission on General cancellation (and Life lapse), by the plan's rule; manual clawback; a clawback always references its entitlement; paid entitlements are not clawed back. **BUILT** for General (ADR-014). Test: `EnterpriseProcessTests.Cancellation_ClawsBackUnearnedCommission_AndASettledClaimRecoversTheCededShare_REI003`; `CommissionTests.Clawback_AlwaysReferencesItsEntitlement_AndAPaidEntitlementIsNotClawedBack`. Life lapse: the handler exists; no test found.
  - Monthly statements per intermediary: payable entitlements less clawbacks to recover, approved by a second person within authority, paid to the intermediary's verified bank account through a payout batch; a rejection returns it to draft. **BUILT, STAND-IN** (payments: `SimulatorGateway`, row 13) (SRS: DST-004; ADR-013). Test: `CommissionTests.Statement_IsApprovedByASecondPersonBeforePayment_AndRecoversClawbacksOnTheNextOne_DST004`.
  - Broker of record change: commission split pro rata at the change date; commission already paid is recovered from the outgoing broker unless the tenant keeps it; both brokers are told once it is approved. **BUILT** (B-001, B-009, B-010). Test: `EnterpriseProcessTests.ABrokerOfRecordChange_NeedsTheAppointmentLetterAndASecondPerson_MovesAtItsDate_AndSplitsCommissionProRata_B001`; `EnterpriseProcessTests.PaidCommission_IsRecoveredOnABrokerChange_UnlessTheTenantKeepsIt_B009`.
  - Broker portal: the broker's and its sub-brokers' approved and paid statements, without approval or bank details. **BUILT** (external-user access). Test: `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`.
  - Tax on commission. **PARTLY BUILT**: statements carry a tax field set to zero (`TODO(spec)`, OQ-062).
  - Overriding commission on the distribution hierarchy, and collecting a statement whose clawbacks exceed its commission. **DEFERRED** (ADR-014 "Still open", OQ-062; a net-negative statement is approved without a payout).
- **Key business rules:** commission statement maker-checker in Workflow (`COMMISSION_STATEMENT`; DST-004), with the approval subject covering net amount, payee and account (`CommissionTests.AStatementApprovedByAnApproval_IsGrantedByIt_AndAChangedOneIsNot_D5`). No entitlement on two statements (one transaction). Amounts rounded half away from zero to cents (ADR-014). Postings COMMISSION_ACCRUED, COMMISSION_CLAWBACK, COMMISSION_PAID (ADR-014; not separately verified in tests here). ADR-014 status: Proposed, pending business sign-off.
- **Role templates:** `DISTRIBUTION_ADMINISTRATOR` (`INTERMEDIARIES`, `COMMISSION`, `CONFIG_DISTRIBUTION`; prepares statements); `FINANCE_MANAGER` approves `COMMISSION_STATEMENT`; `BROKER` portal template sees statements; `AUDITOR` reads.
- **Tenant configures:** `commission.mst_intermediary_register` (with payee party), `cfg_intermediary_product_permissions` (names the plan), `cfg_commission_plans` (basis, rate, clawback rule, versions), `cfg_broker_change_settings` (B-009), `cfg_distribution_hierarchy` (used for sub-broker visibility). `cfg_statement_templates` and `ref_licence_and_appointment_types` are not read by code (verified by grep).
- **Outside services:** commission payouts through Payments, `SimulatorGateway` (stand-in, row 13); broker change notices through Communications (email stand-in, row 6; inferred channel).
- **Known gaps:** real payouts (row 13); commission tax, overrides, negative statements (OQ-062); no check of intermediary licences or appointments (`ref_licence_and_appointment_types` unused; no OQ found).
- **Notes for the writer:** "within delegation authority" for statements comes from Workflow's authority table, as for claims. Life commission uses the same accrual path through `IPolicyDistribution`; only General is tested end to end.

### Health (`Health`)
- **For:** medical scheme and health insurance administration: schemes, members and dependants, eligibility, preauthorisation, medical and pharmacy claims, managed care, provider payments and recoveries. **Lines:** Health.
- **Capabilities:**
  - Register employer schemes and sponsors, and list them by number or sponsor. **BUILT** (HLT-002; BR-067). Test: `HealthProcessTests.HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009`. Scheme renewal notice to the sponsor: `HealthProcessTests.ASchemesSponsor_IsToldOfItsRenewal_ByTheRulesNotice_OncePerYear`.
  - Enrol a principal member and add or remove dependants within each dependant type's age limit (with a student extension). The scheme contribution is split by employer share, and the employer's part is billed to the sponsor through Billing. **BUILT** (HLT-002). Tests: `HealthTests.Dependants_StayWithinTheirTypesAgeLimit_WithTheStudentExtension_HLT002`, `HealthTests.SchemeContributions_AreSplitByTheEmployerShare_TheEmployersPartBilledToTheSponsor`.
  - Membership lifecycle with contribution grace: unpaid contributions hold claims (they are not declined), the membership is suspended after the grace days, a person is asked to terminate it, and payment reinstates it. **BUILT** (F006). Test: `HealthTests.ContributionGrace_HoldsClaims_SuspendsAfterItsDays_AsksAPersonToTerminate_AndPaymentReinstates_F006`; end to end `HealthProcessTests.UnpaidContributions_HoldClaimsInGrace_ReleaseThemOncePaid_AndSuspendThenAskAPersonToTerminate_F006`.
  - Member eligibility enquiries, with each decision recorded and searchable by member number. **BUILT** (HLT-003; BR-069, BR-072). Test: `HealthProcessTests.HealthCareToProviderPayment_…_HLT002_HLT009` (the eligibility steps).
  - Preauthorisation: request, clinical review, approval (also conditional), rejection, cancellation and expiry. An approval reserves benefit on the member's and family's accumulators, and expiry or cancellation releases it. **BUILT** (HLT-005). Tests: `HealthTests.Preauthorisation_ReservesWhenApproved_AndReleasesWhatItHoldsWhenItExpires_HLT005`, `HealthTests.AConditionalApproval_ReservesAndCanBeUsed_OrBecomesApprovedWhenItsConditionsAreMet`.
  - Medical claims from providers, from members (reimbursement) and through the provider portal, validated for membership, a service date not after the business date, codes and format. **BUILT** (HLT-006, HLT-007; BR-124). Tests: `HealthTests.Claim_ValidationFailure_RejectsIt_AndAPendedLinePendsTheWholeClaim_HLT007`, `HealthTests.AClaim_WithAServiceDateAfterTheBusinessDate_IsRefused_ButTodayIsAccepted_BR124`.
  - Automatic adjudication of each claim line (ADR-012), in this order: benefit category mapping, network status, tariff (contract, then network, then reference schedule), modifiers, allowed amount, waiting periods and exclusions, deductible, co-payment with caps, limits (per event, annual, lifetime, family pool), member liability, and the preauthorisation match. Edit rules check claim history (for example the 30-day duplicate rule), and high-cost lines are flagged. **BUILT** (HLT-004, HLT-007; ADR-012 is *Proposed*, pending business sign-off). Test: `AdjudicationWorkedExamples` (for example `AdjudicationWorkedExamples.E_DeductibleThenCappedCoPaymentThenTheLimit_AndTheMemberPaysTheRest`, `AdjudicationWorkedExamples.EditRules_ReadTheClaimHistory_OnlyWithinTheirWindow_EDIT_DUP_30`).
  - Pended-claim review by a person, then approval or decline. A full or partial decline sends the member a decision-rights notice in the reason's customer wording. An upheld appeal sends a rejected claim back to validation. **BUILT** (OQ-102, OQ-103, BR-127). Tests: `HealthTests.AFullDeclineByStaff_SendsTheDecisionRights_WithTheReasonsCustomerWording_OQ102`, `HealthTests.UpheldAppeal_SendsARejectedClaimBackToValidation_OQ103`.
  - Benefit accumulators: used plus reserved never exceeds the limit. Concurrent claims against one family limit never exceed it and never deadlock (lock order, server retry). **BUILT** (HLT-007; DBA-011; ADR-012). Tests: `HealthTests.Accumulator_UsedPlusReservedNeverExceedsTheLimit_HLT007`, `HealthProcessTests.ConcurrentClaims_AgainstOneFamilyLimit_NeverExceedIt_AndNeverDeadlock`, latency `HealthProcessTests.Adjudication_AgainstTheBenefitAccumulators_StaysWithinTheStandardLatencyTarget_NFR002`.
  - Claim reversal and reprocessing. A reversal returns the benefit use, deductible and co-payment on the member's and family's accumulators (never below zero), and posts only the reversed amount to the ledger. **BUILT** (FIN-003). Tests: `HealthProcessTests.AReversedClaim_GivesBackDeductibleAndCopayment_OnItsOwnAndTheFamilysAccumulators_AndAReprocessedClaimTakesThemAgain`, `HealthProcessTests.AReversalOfPartOfAPaidClaim_PostsOnlyTheReversedAmount`, `HealthTests.ReversingConsumption_GivesBackUseDeductibleAndCopayment_NeverBelowZero`.
  - Pharmacy and medication claims through the claim API and the provider portal. Rules covered: formulary and tier, reference price (the member pays the difference), tier co-payment, maximum quantity and days' supply, prescription validity and repeats, early refill with the plan's tolerance, per-ingredient and per-product medication limits, and medicines that need authorisation. **BUILT** (F039–F047, F092, F093; OQ-149, OQ-151, OQ-152). Tests: `PharmacyWorkedExamples` (for example `PharmacyWorkedExamples.TheSameIngredient_BeforeTheSupplyHasRunOut_Pends_F047_OQ151`), `HealthProcessTests.APharmacyClaim_RunsFromReceiptThroughAPaymentRun_AndWhatItDispensedIsClinical_F039_F045_HLT008`.
  - Real-time pharmacy switch (claims answered at the point of dispensing). **DEFERRED** (B-012; go-live register #14; OQ-147). No interface or stub exists in code: grep finds no switch adapter in `src/`. Until it is built, pharmacy claims are adjudicated after the event.
  - Fraud cases: at claim submission the tenant's fraud indicators are scored, and a case opens automatically at the threshold. A case is investigated and then concluded only by its own investigator. **BUILT** (F009, F058). Tests: `HealthProcessTests.MemberClaim_IsPricedAtTheReferenceTariff_AndPaidToThePrincipalsVerifiedAccountInARun` (case opened at submission), `HealthTests.Cases_AreConcludedOnlyByTheirReviewerOrInvestigator`, `FraudIndicatorDefinitionTests.AnIndicatorWithInvalidLogic_IsRefusedWhenDrafted_F009`. **Not built:** a Health fraud case opened by hand. There is no `POST /health/fraud-cases`, and register #20 lists it as an open question.
  - Utilisation cases: high-cost lines open a review, which its reviewer concludes. **BUILT** (§11.1 I; BR-079). Tests: `HealthProcessTests.HealthCareToProviderPayment_…_HLT002_HLT009` (utilisation case steps), `HealthTests.Cases_AreConcludedOnlyByTheirReviewerOrInvestigator`.
  - Provider payment runs: open, cut-off, calculate (one remittance per provider to its verified account), approval in Workflow bound to the run's total, release, and remittance advice. A rejected run frees its claims for a new run, and no claim is paid by two runs. **BUILT, STAND-IN** (payments: Payments' `SimulatorGateway`) (HLT-009; F103). Tests: `HealthTests.PaymentRun_TotalIsTheSumOfRemittances_AndTheApprovalIsBoundToIt_HLT009`, `HealthTests.RejectedPaymentRun_EndsRejected_KeepsItsRemittances_AndReleasesItsClaimsForANewRun_D2`.
  - Member reimbursement priced at the plan's reference tariff and paid in a run to the principal member's verified bank account. **BUILT, STAND-IN** (payments: `SimulatorGateway`) (HLT-006; ADR-012, ADR-013). Test: `HealthProcessTests.MemberClaim_IsPricedAtTheReferenceTariff_AndPaidToThePrincipalsVerifiedAccountInARun`.
  - Provider recoveries and credit notes. A reversal or finding raises a recovery, which is approved by a second person and then either offset against the next run (never more than the balance or the remittance) or invoiced. A dispute holds it until its case closes. A write-off needs a reason and a second person. A recovery raised in error is reversed and the money taken is repaid once. A member's overpayment always goes to a person. A provider credit note needs a second person. **BUILT** (F048–F050; OQ-139, OQ-145, OQ-146; BR-120). Tests: `ProviderRecoveryTests` (for example `ProviderRecoveryTests.AWriteOff_NeedsAReasonAndASecondPerson_AndARefusalLeavesItOwed_Addition3`), `HealthProcessTests.ARecovery_WithNoOffsetFlagIsInvoiced_AndADisputeCaseHoldsOneUntilItCloses_F049_F050_OQ146`.
  - Provider monitoring: monthly measures judged against thresholds (a breach asks a person), quality reviews, and provider statements imported (fixed CSV) or submitted, matched line by line and reconciled. **BUILT** (F028–F030; OQ-142). Tests: `ProviderMonitoringTests.AStatement_IsMatchedLineByLine_AndReconciledOnceEachExceptionIsResolved_F030`, `HealthProcessTests.AProvidersMonth_IsMeasuredAndABreachAsksAPerson_AndItsStatementIsMatchedAndReconciled_F028_F029_F030`.
  - Chronic registrations (a person with the clinical function decides; the requester cannot), disease-management programme enrolment with reviews, monthly chronic medication entitlement, and preventive screening and vaccination schedules by age, sex and interval. **BUILT** (F083–F088; OQ-150, SRS-002). Tests: `ChronicRegistrationTests.TheRequester_CannotDecide_SRS002`, `HealthProcessTests.AChronicRegistration_IsDecidedByTheClinicalFunction_AndAClaimDrawsOnIt_F083_F084_F085_OQ150_OQ161`, `PharmacyWorkedExamples.AScheduledItem_IsCheckedAgainstAgeAndSex_F087_F088`.
  - Complex case management: case manager assignment with reasons, care plans with progress, overdue-activity alerts, and notes shared with a provider only with consent. Clinical referrals are decided, used up and expired, and their reason is visible to the receiver only with consent. **BUILT** (F034–F038; OQ-143, OQ-144). Tests: `ComplexCaseTests.Reassigning_NeedsAReason_AndEndsTheLastAssignment_F035`, `HealthProcessTests.AReferralsReason_IsForItsWriter_ForItsReceiverWithConsent_AndNotForAThirdProvider_F038`.
  - Medical information restricted by role and purpose. Diagnoses come only from separate "clinical" reads, each audited once, and refused reads are audited as DENIED. **BUILT** (HLT-008). Tests: `HealthProcessTests.AComplexCasesClinicalDetail_IsForItsCurrentManager_AndReassigningEndsTheLastManagersAccess_OQ143_F034_F037`, `ChronicRegistrationTests.ADeclinesNotice_NamesNoCondition_HLT008`.
  - Member portal (memberships, claims, chronic registrations) and provider portal (claims, preauthorisations, referrals, chronic requests, remittances, recoveries, credit notes). Each sees only its own records. **BUILT** (BR-091; OQ-084). Tests: `HealthProcessTests.ExternalUsers_ProvidersAndMembersSeeOnlyTheirOwn_ExternalUserAccess`, `PortalListTests.MembersAndBrokers_ListOnlyTheirOwnRecords_AndNeverAnothersByID_BR091`.
  - Migration of memberships and benefit use to date from a previous system. A migrated membership is active with no waiting period or invoice. **BUILT** (OQ-158; B-013). Tests: `HealthProcessTests.AMigratedMembership_IsActive_WithNoWaitingPeriodOrInvoice_OQ158`, `HealthProcessTests.AMigratedMembersUseToDate_CountsAgainstTheirLimit_AndAResubmittedClaimReconciles`.
  - Health plans sold as Product Factory products, with product scopes reaching their memberships. **BUILT** (OQ-080). Tests: `HealthPlanProductTests.APlanOption_NamesItsProduct_TheSameForEveryOptionOfThePlan_OQ080`, `HealthProcessTests.HealthPlans_AreSoldAsProducts_AndProductScopesReachTheirMemberships_OQ080`.
  - Plan change on a membership, modification of a preauthorisation, and scheme renewal or termination. **PARTLY BUILT**: the endpoints and handlers exist (`POST /memberships/{id}/plan`, `/preauthorisations/{id}/modify`, `/schemes/{id}/renew|terminate`), but no test exercising them was found by grep.
  - Wellness, treatment compliance and outcomes. **DEFERRED** (F089–F091; B-004; register #17).
- **Key business rules:**
  - Adjudication order and defaults (ADR-012): **BUILT**, but the ADR is *Proposed* and the worked examples are "engineering cases pending actuarial confirmation" (OQ-060).
  - A pended claim changes no accumulator. A claim and its accumulators are written in one transaction, a deliberate exception to one aggregate per transaction (ADR-012): **BUILT**.
  - A payment run's approval is bound to its total, and a claim can be in only one run (a database trigger, `health.keep_payment_run`): **BUILT** (HLT-009).
  - Maker-checker (SRS-002): payment runs (`PROVIDER_PAYMENT_RUN`), recovery findings (`PROVIDER_RECOVERY_FINDING`), write-offs (`PROVIDER_RECOVERY_WRITE_OFF`), provider credit notes (`PROVIDER_CREDIT_NOTE`) and chronic decisions: **BUILT**.
  - Decline reasons must be DECLINE reasons in force, or the decline is refused (BR-127): **BUILT**.
  - A claim in contribution grace is held, not declined (F006): **BUILT**.
  - Money is decimal plus currency, with each membership's currency shown on its accumulators: **BUILT** (seen in the end-to-end test).
- **Role templates:** `HEALTH_CLAIMS_ASSESSOR` (HEALTH_MEDICAL_CLAIMS, HEALTH_ACCESS_TO_CARE, HEALTH_FRAUD_UTILISATION); `HEALTH_MEMBERSHIP_ADMINISTRATOR` (HEALTH_SCHEMES_PLANS, HEALTH_MEMBERSHIP); `FINANCE_OFFICER` and `FINANCE_MANAGER` (HEALTH_PROVIDER_PAYMENTS; the manager holds the `PROVIDER_PAYMENT_RUN` approval); `HEALTH_AUDITOR` (read-only medical information, HLT-008); `LINE_CONFIGURATION_MANAGER` (CONFIG_HEALTH); `PROVIDER` and `MEMBER` (portals); `AUDITOR` (read-only).
- **Tenant configures:** about 37 `health.cfg_*` tables plus `mst_*` registers. Examples: `cfg_plans_and_options`, `cfg_benefit_schedules`, `cfg_limit_definitions`, `cfg_co_payment_and_deductible_rules`, `cfg_tariff_schedules`, `cfg_tariff_modifiers`, `cfg_provider_contracts`, `cfg_network_definitions`, `cfg_network_restriction_rules`, `cfg_preauthorisation_required_lists`, `cfg_adjudication_edit_rules`, `cfg_health_fraud_indicator_definitions`, `cfg_contribution_grace_rules`, `cfg_provider_recovery_settings`, `cfg_medication_limits`, `cfg_prescription_benefits`, `cfg_preventive_schedules`, `cfg_disease_programmes`, `cfg_provider_measures`; registers `mst_provider_register` and `mst_sponsor_register`. Reference lists: `ref_diagnosis_codes`, `ref_procedure_and_service_codes` and `ref_medicine_formulary`. Approval authority for the approval types above is set in `workflow.cfg_delegation_and_approval_authority`.
- **Outside services:**
  - Payouts to providers and members go through Payments' `IPaymentGateway`, and its only implementation is `SimulatorGateway` (stand-in, #13).
  - Notices go through Communications (stub channels, #6 and #7).
  - Pharmacy switch: none (not built, B-012).
- **Known gaps:**
  - Pharmacy switch adapter (#14, B-012). Ade: needed before go-live for a Health customer with pharmacy claims.
  - Real payment provider (#13).
  - Health fraud cases opened by hand: not built; open question (#20).
  - Wellness F089–F091 (#17).
  - Open Health claims are resubmitted at migration, not imported (#23).
  - Compliance must confirm OQ-161 (what a principal member sees of an adult dependant) and OQ-084.
  - No segregation-of-duties rule yet between `LINE_CONFIGURATION_MANAGER` and `HEALTH_CLAIMS_ASSESSOR` (#34).
  - Plan change, preauthorisation modification and scheme renewal or termination have no test found.
  - ADR-012 and OQ-060 still need business and actuarial sign-off.
- **Notes for the writer:**
  - The sub-domains HEALTH_PHARMACY and HEALTH_CARE_PROGRAMMES exist in the catalogue, but Health declares no screens in them. Pharmacy runs through claims, and chronic and programme screens sit in HEALTH_ACCESS_TO_CARE.
  - HEALTH_PROVIDER_PERFORMANCE (HEALTH_PROVIDER_MANAGEMENT) appears only in `AUDITOR`'s function list, and `HEALTH_RECORD_PROVIDER_QUALITY_REVIEW` is in no template. Inferred: a tenant grants these by hand.
  - Health claim appeals are a Workflow case screen (`HEALTH_CLAIM_APPEALS`, declared in `WorkflowFunctions.cs`). The effect on Health (back to validation) is tested in Health.
  - `docs/traceability/functional-coverage.md` still shows many of these F numbers as "Missing". It is an older baseline, and the code and tests above supersede it.

### Party (`Party`)
- **For:** the shared register of people and organisations: identity, contacts, consent, privacy (POPIA) rights, payee bank accounts and customer due diligence (FICA). **Lines:** shared.
- **Capabilities:**
  - Register individuals and organisations. Each gets the tenant's next party number with a Luhn check digit, and its kind never changes. **BUILT** (PTY-001; BR-029). Tests: `PartyNumberTests.Registration_AssignsTheTenantsNextNumber_WithItsCheckDigit_AndTheSearchFindsIt_BR029`, `PartyTests.Kind_NeverChanges_PTY001`.
  - Addresses, contact points (a new primary demotes the old one), identifiers (returned masked to the last four), relationships, and many roles per party. **BUILT** (PTY-002; BR-026). Tests: `PartyTests.Party_HoldsManyRoles_PTY002`, `PartyIdentifierMaskTests.AnIdentifier_IsReturnedMasked_WithAtMostItsLastFour_BR026`, `PartyTests.NewPrimaryContact_DemotesThePreviousOne`.
  - Party search by name, identifier or status. Every search goes in an access log, with the identifier stored as a hash, and only audit roles read the log. **BUILT** (PTY-005; GAP-022). Tests: `PartySearchTests.EverySearch_IsInTheAccessLog_WithTheIdentifierByHashOnly_PTY005`, `PartySearchTests.TheAccessLog_IsReadByAuditRolesOnly`.
  - Search-log retention (minimised after 12 months, then deleted) and legal holds, where a hold's release needs a second person. **BUILT** (OQ-073, OQ-111). Tests: `SearchLogRetentionTests.Retention_MinimisesAfter12Months_DeletesAfterThePeriod_ExceptUnderAHold_AndAuditsEachRun_OQ073`, `SearchLogHoldReleaseTests.TheRequester_CannotApproveTheirOwnRelease_SRS002`.
  - Duplicate detection at registration: a match is refused with its candidates until a person confirms the party is new and gives a reason. **BUILT** (PTY-004; OQ-127). Test: `PartyDuplicateTests.AStaffRegistration_MatchingAnExistingParty_IsRefusedWithTheCandidates_UntilConfirmedNewWithAReason_OQ127`.
  - Merge two parties. The merged record becomes read-only and points to its survivor, with an audited history, and parties of different kinds cannot be merged. **BUILT** (PTY-004). Tests: `SharedServicesTests.MergedParty_IsReadOnly_WithAnAuditableHistory_PTY004`, `PartyTests.Party_IsNotMergedIntoAPartyOfAnotherKind`.
  - Record a death, and track dormancy (a party with nothing open becomes dormant, and back to active when something opens). **BUILT** (OQ-054). Tests: `PartyTests.ADormantPartysDeath_CanBeRecorded_AndActivityResuming_AllowsANewReview_OQ054`, `PartyAnonymisationTests.APartyGoesDormantWhenNothingIsOpen_AndBackWhenSomethingIs_CountingACase_OQ054`.
  - Consent per purpose and channel: an append-only ledger where the latest record is current, grant and withdraw, and marketing that needs consent when it is sent. **BUILT** (PTY-005; F004; POPIA s69). Tests: `PartyTests.ConsentLedger_IsAppendOnly_AndTheLatestRecordIsCurrent_PTY005`, `CommunicationsTests.Marketing_NeedsConsentWhenSent_AndAMemberOptingOutWithdrawsIt_POPIA69`.
  - POPIA anonymisation (erasure), allowed only when every linked record has passed retention and nothing is held. Retention covers open policies and claims, the tenant's retention period (7-year working default), payments, audit evidence, legal holds and merged parties' records. The rule fails closed when no period is in force. **BUILT** (D-34; OQ-120; F059). Tests: `PartyAnonymisationTests.APartyWhoseRecordsAreAllPastRetention_AndNotHeld_IsAnonymised_D34`, `PartyAnonymisationTests.WithNoRetentionPeriodInForceForPayments_APaymentBlocks_FailClosed_D34`.
  - Data-subject requests (access, correction, deletion). Identity is confirmed, then a second person approves. Requests have a due date (tenant days, default 30), refusals keep their reasons, the access export marks Health records as clinical, and a deletion is refused while records are kept. Members can raise requests from the customer portal. **BUILT** (F059; OQ-159; SRS-002). Tests: `DataSubjectRequestTests.ASecondPerson_ApprovesAfterIdentityIsConfirmed_SRS002`, `DataSubjectRequestTests.HealthRecords_AreClinical_InAnExport_HLT008`, `GeneralProcessTests.ADisclosurePoint_RefusesTheStepUntilAcknowledged_AndADeletionIsRefusedWhileRecordsAreKept_F059_F060`.
  - Payee bank accounts (ADR-013). The account number is encrypted at rest and never returned or published. A change is a new account that supersedes the old one. Verification is a second person's Workflow approval (`BANK_ACCOUNT_VERIFICATION`, and the checker needs `bank_account.verify`). Only an ACTIVE account in the payee's currency is payable, and it is decrypted only at payment. **BUILT** (PAY-001, PAY-003, SRS-002). Tests: `BankAccountTests.TheMaker_CannotApproveTheirOwnAccount_NorCanACheckerWithoutTheVerifyPermission_SRS002`, `BankAccountTests.AccountNumber_IsEncryptedAtRest_AndNeverReturnedOrPublished`, `BankAccountEndingTests.AChangeWhilePending_WithdrawsThatVerification_SoItsLaterWithdrawnEnding_IsAlreadyApplied_D3`.
  - Identity verification of an identifier. With a provider configured, it is checked automatically. Without one, a person verifies it against a document filed against the party. **BUILT, STAND-IN** (identity verification: Integration's `StubPartnerAdapters`, which passes unless the number ends in 0000) (F001). Test: `IdentityVerificationTests.WithAnActiveProvider_AnAddedIdentifierIsChecked_APassVerifiesIt_AndAFailureLeavesItUnverified_F001`.
  - Sanctions, PEP and adverse-media screening. A hit opens a review task and stops clearance. A false positive is cleared with a reason. A true sanctions match blocks the party, needs a FIC report (s28A/s29) and opens a compliance case, and is unblocked only with a second person. A provider list update triggers re-screening. **BUILT, STAND-IN** (screening: `StubPartnerAdapters`, which matches on marker names) (F003; OQ-125). Tests: `DueDiligenceTests.ATrueSanctionsMatch_BlocksTheParty_NeedsAReport_OpensAComplianceCase_AndIsUnblockedOnlyByASecondPerson_F003`, `DueDiligenceTests.AProvidersListUpdate_ScreensAgainThePartiesScreenedOnTheOlderList_OQ125`.
  - Customer due diligence:
    - levels and gates per business step, with defaults signed off, and off for tenants that existed before;
    - a PEP raises the party to enhanced, and clearance needs a second person;
    - beneficial owners;
    - risk classification from configured factors, where an override needs a second person;
    - periodic reviews, where an expired review date blocks until a person reviews (refused on an expired passport).
    
    **BUILT** (F002, F003, F077; OQ-123, OQ-124, OQ-126, OQ-138; BR-109, BR-112). It depends on the screening stand-in. Tests: `DueDiligenceTests.APep_RaisesTheLevelToEnhanced_AndClearanceNeedsAnApproval_TheMakerCannotGive_OQ126`, `DueDiligenceTests.RiskFactors_GiveTheConfiguredClass_AndAnOverrideNeedsASecondPersonAndIsAudited_F077`, `PeriodicReviewTests`.
  - Payout screening: a refund or payout to a payee with a hit waiting is held out of payout batches until the hit is cleared. **BUILT, STAND-IN** (screening) (OQ-123). Test: `DueDiligenceTests.ARefundToAPayeeWithAHitWaiting_IsHeldOutOfTheBatches_AndReleasedOnceTheHitIsCleared_OQ123`.
  - Bulk party import (all or nothing) and party or policy exports, which need a second person. **BUILT** (F076; SRS-002). Tests: `BulkJobTests.AnExport_NeedsASecondPerson_SRS002`, `BulkJobTests.AnImportWithARejectedLine_AcceptsNothing`.
  - Broker portal: a broker lists and registers its own clients. A portal registration that matches an existing party is resolved only on approval. **BUILT** (BR-091). Tests: `PartyTests.MatchedPortalRegistration_IsResolvedOnlyOnApproval_AndARejectionReturnsItToReview_ExternalUserAccess`, `PortalListTests.MembersAndBrokers_ListOnlyTheirOwnRecords_AndNeverAnothersByID_BR091`.
- **Key business rules:**
  - Segregation of duties: whoever adds a payee bank account cannot verify it (`PARTY_ADD_BANK_ACCOUNT` / `PARTY_BANK_ACCOUNT_VERIFY` is a BLOCK pair in RoleTemplates). **BUILT**.
  - Maker-checker (SRS-002) on bank-account verification, data-subject requests, exports, search-log hold release, enhanced due diligence, risk-class override and due-diligence unblock. **BUILT**.
  - Privileged functions: `PARTY_ANONYMISE`, `PARTY_MERGE_PARTIES`, the search-log holds, and `PARTY_UNBLOCK_DUE_DILIGENCE`. **BUILT**.
  - Events carry no personal data (INT-007), and no event, task or audit entry carries a screening hit's list name or score. **BUILT** (`PartyTests.Events_CarryNoPersonalData_INT007`, `DueDiligenceTests.NoEventTaskOrAuditEntry_CarriesAHitsListNameOrScore_AndAPortalSchemaHasNoDueDiligence_DueDiligence3`).
  - Anonymisation is a person's act and fails closed (D-34, OQ-054). **BUILT**.
- **Role templates:** almost every operational staff template holds PARTIES and CONSENT_PREFERENCES. `COMPLIANCE_OFFICER` holds consent and due diligence: hit review, risk override, FIC report and unblock. `DATA_PROTECTION_OFFICER` holds anonymise, merge and the search-log holds. `FINANCE_MANAGER` holds the bank-account verification permission. `HEALTH_MEMBERSHIP_ADMINISTRATOR` and `CUSTOMER_SERVICE_AGENT` hold the data-subject request functions. `MEMBER` raises data-subject requests and `BROKER` holds portal clients.
- **Tenant configures:**
  - due diligence: `party.cfg_due_diligence_gates`, `cfg_due_diligence_requirements`, `cfg_due_diligence_settings`, `cfg_customer_risk_factors`, `cfg_customer_risk_classes`;
  - consent and data subjects: `cfg_consent_purposes`, `cfg_data_subject_settings`;
  - matching and retention: `cfg_matching_rules_and_thresholds`, `cfg_record_retention_policies`;
  - reference lists: `ref_identity_document_types`, `ref_relationship_types` and `ref_party_role_types`.
  
  The screening and identity-verification partner for each tenant comes from Integration's partner directory.
- **Outside services:**
  - Screening (`IScreeningAdapter`): stand-in, `StubPartnerAdapters` (#11).
  - Identity verification (`IIdentityVerificationAdapter`): stand-in, `StubPartnerAdapters` (#11).
  - Bank-account verification: no service. A person verifies through Workflow (ADR-013 `TODO(spec)`: no verification provider is specified).
- **Known gaps:**
  - Real screening and identity-verification adapters (#11).
  - The depth of the POPIA s23 access export, and compliance confirming OQ-159 (#20).
  - Merge is a single privileged person's act with no second-person approval. Not a recorded gap; noted for accuracy.
  - No automated bank-account verification provider (ADR-013).
- **Notes for the writer:**
  - The DUE_DILIGENCE sub-domain exists in the catalogue, but Party declares its due-diligence screen under PARTIES.
  - The customer portal's data-subject request endpoint exists, but grep found no test calling `portal/party/data-subject-requests`.

### Compliance (`Compliance`)
- **For:** regulatory returns, prepared, reviewed, approved and submitted by three different people, and operational reports from a catalogue with role-based access. Database schema `reporting`. **Lines:** shared.
- **Capabilities:**
  - Prepare a regulatory return automatically when an accounting period closes, from the return definition's field mapping (ledger account movements and analytics metrics). One return per code and period, and a person can also prepare one. **BUILT** (§24). Test: `EnterpriseProcessTests.ClosedPeriod_PreparesItsReturn_ApprovedByAnotherOfficerBeforeSubmission_AndReportsFollowTheCatalogue`.
  - Review, then approval by a compliance officer who is neither the preparer nor the reviewer, before submission. Actions need If-Match. **BUILT** (SRS-002; DBA-011; GAP-048). Tests: `ComplianceTests.Return_IsApprovedByAComplianceOfficer_OtherThanItsPreparerAndReviewer_BeforeSubmission`, `ComplianceTests.Lifecycle_MatchesDomainModel`.
  - Submit an approved return to its regulator. **BUILT, STAND-IN** (regulator: `IRegulatorAdapter` implemented by Integration's `StubPartnerAdapters`, which logs the call and returns a `STUB-` reference) (#12). Test: `EnterpriseProcessTests.ClosedPeriod_PreparesItsReturn_…` (asserts `regulatorRef` starts with `STUB-`).
  - Record the regulator's acceptance or rejection by hand. A rejected return is corrected (its values are recalculated, and it goes back to Prepared). **BUILT**. Test: `ComplianceTests.Return_IsApprovedByAComplianceOfficer_OtherThanItsPreparerAndReviewer_BeforeSubmission` (reject, correct and re-approve), and `EnterpriseProcessTests.ClosedPeriod_…` (accept).
  - Returns dashboard: counts by status, awaiting action, and overdue against the definition's submission days. A definition with no deadline is never overdue. **BUILT** (GAP-046; OQ-083). Test: `EnterpriseProcessTests.ClosedPeriod_PreparesItsReturn_…`.
  - Operational report runs from the report catalogue, allowed by the user's roles, with idempotency. CSV export only where the catalogue allows it, and each download is audited. **BUILT**. Test: `EnterpriseProcessTests.ClosedPeriod_PreparesItsReturn_ApprovedByAnotherOfficerBeforeSubmission_AndReportsFollowTheCatalogue`.
  - Return frequencies end with their periods. **BUILT**. Test: `ComplianceTests.Frequencies_EndWithTheirPeriods_AndCoverTheirMonths`.
  - Compliance register: entries are static data, and a readiness check warns about any entry that names no enforcing configuration. **PARTLY BUILT**: enforcement stays in each module (F057), and grep found no test of the `REGISTER_ENFORCEMENT` readiness warning.
  - Risk limits. **Not in this module.** The RISK_LIMITS sub-domain holds only General's aggregation alerts (`GENERAL_AGGREGATION_ALERTS`, alert only, OQ-154). They are BUILT in General, test `GeneralProcessTests.ZoneLimit_RefersAQuoteOverIt_AndNamesItToTheEndorsementApprover_F062`, and the compliance officer holds them.
  - Solvency and capital reporting extracts. **DEFERRED** (F063; B-002; register #15).
- **Key business rules:**
  - Three different people prepare, review and approve a return (SRS-002). **BUILT**.
  - Return actions use ETag and If-Match (DBA-011). **BUILT**.
  - Deadlines are tenant configuration only, never invented (OQ-083). **BUILT** (no deadline means never overdue).
  - Report access and export follow the catalogue's roles. **BUILT**.
- **Role templates:** `COMPLIANCE_OFFICER` (REGULATORY_REPORTING, RISK_LIMITS, CONFIG_COMPLIANCE and others; holds every `COMPLIANCE_*` return and report function); `AUDITOR` (read).
- **Tenant configures:** `reporting.cfg_regulatory_return_definitions` (regulator, frequency, field mapping, submission days), `reporting.cfg_report_catalogue_and_access_rules`, `reporting.cfg_compliance_register_entries` and `reporting.ref_data_classification_scheme`.
- **Outside services:** regulator submission through `IRegulatorAdapter`: stand-in, `StubPartnerAdapters` (#12). Regulator answers are recorded by hand.
- **Known gaps:**
  - A real regulator channel (#12, "If" sent electronically).
  - The actual return deadlines (OQ-083).
  - Solvency extracts (#15, Ade to decide).
  - Compliance answers on wording and points (OQ-157), retention, and the depth of the POPIA access export (#20).
- **Notes for the writer:**
  - Complaints, appeals and other service cases do **not** live here. They are Workflow's `ServiceCase` (`src/Modules/Workflow/Insys.Workflow.Domain/Cases/ServiceCase.cs`), and the compliance officer holds `CASE_*` and `SERVICE_*` functions there.
  - The DATA_PROTECTION, DISCLOSURES_FRAUD and SOLVENCY sub-domains are in the catalogue but hold no Compliance-module functions. Data protection sits in Party, Documents and Audit (inferred from the function lists).
  - Tax returns ("a return's total equals the postings", Wave 6 §10) are tested in `GeneralProcessTests`. Inferred: they belong to Finance or Billing, not this module. Not verified.

### Billing (`Billing`)
- **For:** premium billing accounts, instalment schedules, invoices, credit notes, arrears and refunds for every policy. **Lines:** Life / General / Health (shared module).
- **Capabilities:**
  - Billing obligations created automatically from authorised policy events: Life issue, General issue/endorsement/cancellation, Health memberships (member's part, plus the employer's share on its own schedule, source `HEALTH_ER`). Life billing stops on lapse and resumes on reinstatement. **BUILT** (SRS: BIL-001, BIL-002; LIF-005). Test: `LifeProcessTests.MissedPremiumToLapse_GraceThenLapse_BillingStops_AndReinstatementResumesIt_LIF005`; Health: `HealthProcessTests.HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009`.
  - Billing accounts opened and listed by payer, policy and status, with a collection method (`POST/GET /billing/accounts`). **BUILT** (BR-054). Test: `MoneyFlowTests.Premium_IsBilledCollectedAllocatedAndPosted_WithTheRuleVersionInForceOnTheAccountingDate`.
  - Instalment schedules (monthly, annual and other frequencies), totalling the premium exactly with the last instalment taking the rounding; schedules amended and extended. **BUILT** (BIL-001; OQ-132). Test: `BillingTests.AMonthlyScheduleOfAnAnnualPremium_TotalsItExactly_TheLastInstalmentTakingTheRounding`, `BillingTests.AnExtendedSchedule_BillsTheExtraDaysAsOneInstalment_AfterItsOldEnd_OQ132`.
  - Invoices issued for instalments due, by a daily job or on request, dated by the tenant's business date (ADR-025). **BUILT** (BIL-002). Test: `BusinessDateTests.AtOneThirtyLocal_TheDebitOrderDueTodayIsInvoiced_AndPostsToTheNewPeriod_AfterOctoberIsClosed_GAP044`.
  - Partial payments: invoices settled by allocations, never over-applied; a reversed payment reopens the invoice. **BUILT** (BIL-003). Test: `BillingTests.Invoice_IsSettledByAllocations_AndNeverOverApplied_BIL003`, `BillingTests.ReversedAllocation_ReopensTheInvoice`.
  - Tax included in premium (VAT) shown on the invoice, rounded to the cent; tax-free products unchanged. **BUILT** (OQ-058, OQ-156; F094). Test: `BillingTests.AnInvoicesTax_IsTakenOutOfThePremium_SoWhatIsOwedDoesNotChange_OQ058`.
  - Credit notes as new documents (the invoice is never overwritten), taking their share of tax, up to the invoice's remainder. **PARTLY BUILT**: credit notes whose reason code requires approval are refused ("not available yet", `TODO(spec)` in `Billing.cs`, OQ-058). (BIL-004). Test: `BillingTests.CreditNote_IsANewDocument_TheInvoiceIsNotOverwritten_BIL004`, `BillingPersistenceTests.SecondCreditNote_UpToTheRemainder_CreditsTheInvoiceFully`.
  - Arrears detection and ageing into tenant-configured buckets; first daily run after due date opens the arrears case; with no bucket covering the days overdue the event is parked and alerted (ADR-022). Feeds Life lapse, General/Health grace and arrears notices. **BUILT** (Request 46; LIF-005; BR-103; F006). Test: `ArrearsAgeingTests.TheFirstRunAfterTheDueDate_OpensTheCase_AndWithoutABucketTheOpeningIsParked_Request46_ADR022`.
  - Ageing buckets checked at tenant onboarding to cover every day up to the last action. **BUILT** (Request 46). Test: `OnboardingReadinessTests.AgeingBuckets_MustCoverEveryDayToTheLastAction_Request46`.
  - Refunds: requested on an account to a payee bank account, approved by a second person in Workflow, then paid through a payout batch; refund marked Paid when the payment succeeds. **BUILT** (SRS-002; BR-055). Test: `MoneyFlowTests.Refund_AndItsPayoutBatch_EachNeedASecondPerson_SRS002`, `BillingTests.Refund_NeedsASecondPerson_BeforeItIsPaid_SRS002`.
  - Claims paid less the policy's arrears settle the arrears oldest-first (tenant setting). **BUILT** (F006, OQ-137). Test: `GeneralProcessTests.ALossInGrace_IsAccepted_ItsPaymentHeldUntilPaid_OrPaidLessTheArrears_OrDeclinedIfGraceEndsUnpaid_F006`.
  - Health provider recoveries invoiced to the debtor (source `HEALTH_REC`); paid, it settles the recovery. **BUILT** (F049, F050). Test: `HealthProcessTests.ARecovery_WithNoOffsetFlagIsInvoiced_AndADisputeCaseHoldsOneUntilItCloses_F049_F050_OQ146`.
  - Data migration: outstanding premiums from the source system loaded with control count and amount, counting as missed for lapse. **BUILT** (B-013, OQ-158). Test: `LifeProcessTests.AMigratedPolicysOutstandingPremiums_CountAsMissed_AndLapseItPastGrace_OQ158_LIF005`, `GeneralProcessTests.AMigrationBatch_ReconcilesAgainstTheSourceSystem_AndReportsEveryDifference_B013`.
  - Migrated policies get their remaining instalments scheduled with no new invoice. **BUILT** (OQ-158). Test: `GeneralProcessTests.AMigratedPolicy_IsInForce_WithItsRemainingInstalmentsScheduled_AndNoInvoice_OQ158`.
  - Customer and broker portal invoice views, limited to the user's own relationship. **BUILT**. Test: `GeneralProcessTests.ExternalUsers_SeeAndActOnlyWithinTheirRelationship_ThroughTheirPortal_ExternalUserAccess`.
  - Billing dashboard figures (`GET /billing/summary`, includes arrears). **BUILT** (GAP-046). Test: `ArrearsAgeingTests.ACasePastItsLastBucket_StaysInItAndIsParkedOnce_ThenAgesOnWhenABucketCoversIt_Request46_ADR022`.
  - Debit order mandates. **PARTLY BUILT**: `MandateRef` is only a stored text reference on the account (max 60 chars); no mandate lifecycle, no mandate file. Go-live register #13.
- **Key business rules:**
  - Obligations only from authorised transactions (BIL-001). BUILT.
  - Allocation never exceeds what is owed. Overpayment above the outstanding amount is not applied (`TODO(spec)`, OQ-058). PARTLY BUILT.
  - Refund maker-checker: the checker differs from the maker; a changed refund isn't granted by an old approval (SRS-002, D5). BUILT (`BillingTests.ARefundApprovedByTheChecker_IsGrantedByThem_AndAChangedOneIsNot_D5`).
  - Adjustments are new documents, never overwrites (BIL-004, FIN-002). BUILT.
  - Money is decimal plus currency; rounding to the cent. BUILT.
  - Missing tenant configuration parks the event and doesn't drop it (ADR-022). BUILT (`ConsumerParkingTests.Billing_ParksAPolicyWhoseCollectionMethodIsNotInForce_AndBillsItOncePublished_ADR022`).
- **Role templates:** `FINANCE_OFFICER` and `FINANCE_MANAGER` (sub-domains `BILLING`, `CONFIG_BILLING`); `AUDITOR` (read-only screens); `MEMBER` and `BROKER` (portal invoices only).
- **Tenant configures:** `billing.ref_collection_methods`, `billing.ref_billing_frequencies`, `billing.cfg_arrears_ageing_buckets`, `billing.cfg_allocation_rules`, `billing.ref_adjustment_and_reversal_reason_codes`, `billing.cfg_document_numbering_schemes`. Refund approval authority is in Workflow (`workflow.cfg_delegation_and_approval_authority`, approval type `REFUND`; inferred from the contract constant, not traced in Workflow).
- **Outside services:** none directly. Collection goes through Payments (see below).
- **Known gaps:**
  - Debit order mandates (#13).
  - Credit notes needing approval (OQ-058).
  - Overpayment handling (OQ-058).
  - Health monthly frequency code and individual member contributions not specified (OQ-061).
  - Arrears before a lapse on reinstatement not specified (`TODO(spec)` in `LifePolicyBilling.cs`).
  - Life cancellation with premium refund (#32).
  - Premium financing (#16, B-003, deferred).
- **Notes for the writer:**
  - The comment in `Billing.cs` (around line 647) says Finance doesn't yet post the claim premium offset. OQ-137 says it was built (Finance has a `PREMIUM_OFFSET` posting rule in `Policies.cs`). Inferred: the comment is stale.
  - I found no test that calls account close (`/accounts/{id}/close`), so I left it out.

### Payments (`Payments`)
- **For:** money in and out: premium collections, payouts (refunds, claims, Health provider runs, commission, complaint redress), payout batches, provider callbacks, bank statement reconciliation. **Lines:** shared.
- **Capabilities:**
  - Payments initiated with an `Idempotency-Key`, so a retry never pays twice; each status change kept with its source (provider, callback, timeout, operator). **BUILT** (PAY-001, PAY-002; BR-053). Test: `PaymentTests.EveryStatusChange_IsKeptInHistory_PAY002`.
  - Collection started automatically when an invoice is issued, if its method has a provider in the register (e.g. debit order). Otherwise the payer pays and the bank statement shows it. **BUILT, STAND-IN** (payments: `SimulatorGateway`). Test: `MoneyFlowTests.Premium_IsBilledCollectedAllocatedAndPosted_WithTheRuleVersionInForceOnTheAccountingDate`.
  - Payments allocated to invoices, never more than the amount received; reversal reverses its allocations, once only. **BUILT** (BIL-003). Test: `PaymentTests.Allocation_NeverExceedsTheAmountReceived_BIL003`, `PaymentPersistenceTests.ReversedPayment_IsNotReversedAgain_NorAllocatedAgain`.
  - Unknown outcomes are never success: timeouts (24 h default) and provider errors go Unknown and into reconciliation. Resolving Unknown as succeeded without a matched statement line needs a second person. **BUILT** (PAY-005, BR-042). Test: `MoneyFlowTests.UnknownOutcome_IsNeverTreatedAsSucceeded_PAY005`, `MoneyFlowTests.ResolvingAnUnknownPaymentAsSucceeded_WithoutAMatchedStatementLine_NeedsASecondPerson_BR042`.
  - Payout batches: built by one person (`PAYMENTS_BUILD_BATCH`), approved in Workflow by a different person, then released (`PAYMENTS_RELEASE_BATCH`). A batch whose payments changed after approval is not released; a rejected batch frees its payments for the next one; a sent payment never leaves its batch. **BUILT, STAND-IN** (payments: `SimulatorGateway`) (SRS-002, D1, D5; BR-051). Test: `PaymentTests.PayoutBatch_IsApprovedByAnotherPerson_BeforeRelease_SRS002`, `MoneyFlowTests.APayoutBatchThatChangedAfterItsReleaseWasApproved_IsNotReleased`.
  - Payouts fed from other modules: approved refunds, claim payment instructions, Health provider payment runs, approved commission statements, complaint redress (OQ-104). **BUILT, STAND-IN** (payments). Test: `MoneyFlowTests.ClaimPayment_IsReleasedOnlyAfterApproval_AndDrawsDownTheReserve_SRS002_CLM004`, `MoneyFlowTests.PaidCaseRedress_IsPostedToTheLedger_Once_OQ104`.
  - Payout only to the payee's current, verified bank account. A superseded account fails the payment, and a fresh approval is needed (ADR-009, ADR-013). **BUILT** (inferred from `Payments.cs` Send; the tests seen cover only the verified-account path).
  - Payouts held while the payee's due diligence isn't cleared, released when cleared. **BUILT** (OQ-123). Test: `DueDiligenceTests.ARefundToAPayeeWithAHitWaiting_IsHeldOutOfTheBatches_AndReleasedOnceTheHitIsCleared_OQ123`.
  - A payout with no payout method in force is parked and escalated, then paid once when a method is published. **BUILT** (GAP-038, ADR-022). Test: `PayoutParkingTests.Payout_WithNoPayoutMethodInForce_IsParkedAndEscalated_ThenPaidOnceWhenAMethodIsPublished_GAP038`.
  - Signed provider callbacks: a per-tenant endpoint key and signing secret, shown once and never stored in plain text. Timestamp and replay checks. Rotation with overlap, revocation, and an optional client token. **BUILT** (ADR-023; PAY-003). Test: `ProviderCallbackTests.SignedCallback_AtItsTenantsAddress_IsApplied_WithNoToken_ADR023`, `ProviderCallbackTests.RepeatedCallbackId_IsRefused_AndAppliedOnce_ADR023`.
  - Bank statement import (JSON through the API, `POST /payments/bank-statements`). Lines are matched to payments by payment or provider reference, currency, and amount within a tolerance; anything else raises an "unmatched line" exception, which is assigned, investigated and resolved by the assignee. **BUILT** (PAY-003, BR-052). Test: `MoneyFlowTests.Payments_AreFoundWithoutTheirRecord_WithTheirDetails_AndAnExceptionReadsAlone_BR050_BR052_BR053`.
  - Payment search without a business record (by reference, status, dates). **BUILT** (BR-050). Test: same as above.
  - Payments dashboard figures (`GET /payments/summary`). **BUILT** (GAP-046). Test: `MoneyFlowTests.UnknownOutcome_IsNeverTreatedAsSucceeded_PAY005`.
  - Simulator refused outside Development and Testing: at start-up, on setting a tenant's provider (`PAYMENT_PROVIDER_NOT_ALLOWED`), on tenant activation and reactivation, and on callback. A demo may declare itself non-production; Production can't. **BUILT** (#13). Test: `SimulatorGuardTests.AHost_WhoseTenantNamesTheSimulator_RefusesToStart_OutsideDevelopmentAndTesting`, `SimulatorGuardTests.Production_CannotDeclareItselfNonProduction`, `SimulatorGuardTests.ASignedCallback_CannotConfirmAPayment_OnAProviderThatIsNotAllowed`.
  - A real payment or collection provider (EFT, debit order file, bank API). **SPECIFIED, NOT BUILT**: no real implementation of `IPaymentGateway` exists. Go-live register #13, OQ-058.
- **Key business rules:**
  - Batch maker-checker: the approver differs from the builder (`MakerChecker.EnsureDifferentPeople` in `PayoutBatch.ApproveBatch`). BUILT.
  - Segregation of duties BLOCK rule: `PAYMENTS_BUILD_BATCH` vs `PAYMENTS_RELEASE_BATCH`. `FINANCE_OFFICER` holds build only; `FINANCE_MANAGER` holds release only. BUILT (`RoleTemplateTests.EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111`).
  - The approval is bound to the batch's contents (subject hash). BUILT.
  - Unknown is never success (PAY-005). BUILT.
  - Idempotency on creates (PAY-001). BUILT.
  - Payee bank details are decrypted only for the gateway and never logged (PAY-004). BUILT (in code; not separately verified by a test I saw).
  - `PAYMENTS_REVERSE` is a privileged function (`FINANCE_MANAGER`). BUILT.
- **Role templates:** `FINANCE_OFFICER`, `FINANCE_MANAGER` (`PAYMENTS`, `CONFIG_PAYMENTS`); `INTEGRATION_ADMINISTRATOR` (callback endpoints: issue and revoke); `AUDITOR` (read-only). `PAYMENTS_CALLBACK_CLIENT` is in no template; it is granted to a provider's service identity one by one.
- **Tenant configures:** `payment.mst_payment_provider_and_bank_register`, `payment.ref_payment_methods`, `payment.ref_payment_status_mappings`, `payment.cfg_reconciliation_matching_rules` (tolerance), `payment.mst_tenant_bank_accounts`, `payment.ref_bank_and_branch_directory`, `payment.ref_refund_and_reversal_reason_codes`, plus callback endpoints. Payout approval authority is in Workflow (`workflow.cfg_delegation_and_approval_authority`, approval type `PAYOUT_RELEASE`; V0059 retired the separate payment release limits). Inferred; not traced in Workflow.
- **Outside services:**
  - Payment and collection provider: **stand-in**, `SimulatorGateway` (`Insys.Payments.Infrastructure/Persistence/PaymentsStores.cs`). It accepts payments as pending; an amount ending .13 fails and one ending .99 gets no answer. No money moves.
  - Bank statements arrive only as JSON through the API, not from a bank feed.
- **Known gaps:**
  - Real provider adapter, EFT and debit order files (#13, OQ-058).
  - Debit order mandate lifecycle (#13).
  - The provider is called inside the database transaction (`TODO(spec)`, OQ-058, #13).
  - Unknown timeout of 24 h is a default, not specified (OQ-058).
  - Statement matching doesn't flag duplicated or delayed lines separately (FIN-006). Inferred from `ImportStatementHandler`: a second line with the same reference would also match.
- **Notes for the writer:**
  - Inferred from `ImportStatementHandler`: a matched statement line doesn't itself confirm a pending payment. It serves as evidence when resolving an Unknown payment.
  - Release is a separate step after the Workflow approval; "second-person release" means a different person approves, and the SoD rule keeps the builder from holding release.

### Finance (`Finance`)
- **For:** the insurance subledger: journals posted from business events by versioned posting rules, accounting periods, reversals, tax returns, export to the insurer's general ledger. **Lines:** shared.
- **Capabilities:**
  - Automatic posting from business events: invoices, credit notes, premium received, payments made and reversed, claim reserves, payments and recoveries, commission accrued and clawed back, reinsurance cessions and recoveries, Health claims settled and reversed, provider recoveries. Each journal is balanced and carries the posting rule version in force on the accounting date. **BUILT** (FIN-001, FIN-004, FIN-005). Test: `LedgerTests.PostBusinessEvent_DebitsEqualCredits_WithRuleVersionOnEveryLine_FIN004`, `MoneyFlowTests.Premium_IsBilledCollectedAllocatedAndPosted_WithTheRuleVersionInForceOnTheAccountingDate`.
  - Foreign currency: stores the rate and the base-currency amount. **BUILT**. Test: `LedgerTests.PostBusinessEvent_ForeignCurrency_StoresRateAndBaseAmount`.
  - Posted journals never updated: the database refuses unbalanced, one-sided or mixed-currency journals at commit; module roles hold no UPDATE or DELETE on posted tables; a trigger refuses even the owner. **BUILT** (FIN-002, DBA-009, ADR-011). Test: `PostedFinancialRecordsTests.ModuleRole_CannotChangePostedRecords_FIN002`, `PostedFinancialRecordsTests.EvenTheOwner_CannotChangeAPostedJournal_TheTriggerRefuses`.
  - Corrections by reversal journal: sides swapped, the original unchanged, reversed at most once, a reversal never reversed. **BUILT** (FIN-003). Test: `LedgerTests.ReverseJournal_SwapsSides_KeepsTheOriginalUnchanged_FIN003`, `PostedFinancialRecordsTests.Journal_IsReversedAtMostOnce`.
  - Journal search by business record, number, period and accounting dates. **BUILT** (BR-043). Test: `EnterpriseProcessTests.TheLedger_IsFoundAndExplained_BR041_BR043_BR044_BR045_BR046`.
  - Accounting periods: open, soft-close, close for good. Posting only into an open period. Reopening a soft-closed period needs a second person in Workflow; a closed period never reopens. **BUILT** (BR-045, SRS-002). Test: `LedgerTests.Reopen_NeedsASecondPerson_AndChangesNothingUntilApproved`, `LedgerTests.ClosedPeriod_IsNeverReopened`.
  - Posting exceptions: a failed posting is kept, visible and listed, never lost (e.g. a Health claim reversal with no posting rule). **BUILT** (FIN-007, BR-044). Test: `FunctionAccessTests.AHealthClaimReversal_WithNoPostingRule_WaitsAsAPostingException`.
  - Retrying a posting exception (`POST /posting-exceptions/{id}/retry`). **PARTLY BUILT**: the route and handler exist; I found no test that calls retry.
  - Trial balance per period (`GET /periods/{code}/trial-balance`). **PARTLY BUILT**: the route exists; I found no test that calls it.
  - GL export: posted entries of a date range in a period, summarised by date, account and currency, as CSV in a layout in force. An overlapping range is refused, so nothing is exported twice; the GL's acknowledgement is recorded. **BUILT, STAND-IN** (GL: `StubPartnerAdapters` behind `IGeneralLedgerAdapter`) (F055; BR-046). Test: `EnterpriseProcessTests.PostedEntries_AreExportedToTheGeneralLedger_Summarised_Balanced_AndOnlyOnce_F055`.
  - Tax returns (e.g. VAT) prepared from the ledger, netting credit notes. Approved or rejected by someone other than the preparer; the file is downloadable only after approval. **BUILT** (F094, SRS-002). Test: `TaxReturnTests.WhoPreparedIt_CannotDecideIt_SRS002`, `GeneralProcessTests.ACreditNotesTax_IsPostedBack_AndTheReturnNetsItOff_F094`.
  - Migration opening balances: the source trial balance loaded as one balanced journal, with control totals reconciled per currency. **BUILT** (B-013, BR-128). Test: `GeneralProcessTests.AMigrationBatch_ReconcilesAgainstTheSourceSystem_AndReportsEveryDifference_B013`, `MigrationReconciliationTests.EachCurrency_ReconcilesOnItsOwn_NeverAgainstASumAcrossThem`.
  - Claim settlement paid less arrears posted gross, with a `PREMIUM_OFFSET` for the deduction. **BUILT** (OQ-137). Code: `Policies.cs`. Test: `ClaimsTests.ASettlementPaidLessTheArrears_DrawsTheReserveByTheGross_AndRecordsGrossDeductionAndNet_OQ137` (ClaimsFramework; I didn't see a Finance-side posting assertion).
- **Key business rules:**
  - Posted records insert-only, corrections by reversal (FIN-002, FIN-003, ADR-011). BUILT. ADR-011's file says "Status: Proposed (Phase 3)", but it is enforced in the database and tested.
  - Versioned posting rules (FIN-004). BUILT.
  - Period reopen maker-checker (SRS-002). BUILT.
  - Tax return maker-checker (SRS-002). BUILT.
  - Business dates in the tenant's time zone (ADR-025). BUILT.
- **Role templates:** `FINANCE_OFFICER` and `FINANCE_MANAGER` (`FINANCE`, `CONFIG_FINANCE`; both hold prepare and approve tax return, with the preparer≠approver rule enforced in the domain); `AUDITOR` (read-only). `TENANT_SWITCH_OFF_MONEY_JOBS` (stops billing and payment jobs, OQ-098) is in `FINANCE_MANAGER`.
- **Tenant configures:** `finance.cfg_posting_rules`, `finance.mst_chart_of_accounts`, `finance.cfg_accounting_calendar_and_periods`, `finance.cfg_gl_export_layouts`, `finance.cfg_tax_codes_on_financial_transactions`, `finance.ref_exchange_rates_and_rate_types`, `finance.ref_transaction_types`, `finance.cfg_reconciliation_rules`.
- **Outside services:** general ledger: **stand-in**, `StubPartnerAdapters` (`Insys.Integration.Infrastructure/Partners/StubPartnerAdapters.cs`) implements `IGeneralLedgerAdapter`. It accepts and records each call; nothing reaches a real GL.
- **Known gaps:**
  - Real GL adapter (#10).
  - GL layouts: only CSV; API and fixed-width formats have no field definitions (`TODO(spec)` in `GlExport.cs`).
  - The layout's schedule isn't run by a job; exports are on request (`TODO(spec)`).
  - The export file isn't stored as a document; it's rebuilt from posted entries (`TODO(spec)`).
  - Return of ceded premium on a decreasing cession has no transaction type (OQ-063).
  - Solvency and capital extracts (#15, F063, B-002, deferred).
- **Notes for the writer:**
  - Inferred: `finance.cfg_reconciliation_rules` exists as static data, but I found no Finance code reading it outside generated files. Finance-side reconciliation (FIN-006) may be limited to Payments' statement matching.

### Analytics (`Analytics`)
- **For:** management figures projected from business events: written premium, claims, renewals and retention, and dashboards. **Lines:** Life and General; **not Health**.
- **Capabilities:**
  - Written premium projection by day, product, channel (intermediary or direct) and branch, from Life and General policy events (issue, endorsement deltas, cancellation refunds) (`GET /analytics/written-premium`). **BUILT** (domain model). Test: `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`.
  - Claims incurred projection by month, product and cause, from claim registration and reserve changes (`GET /analytics/claims-incurred`). **PARTLY BUILT**: the route and projection exist; I found no test calling the route or asserting `CLAIMS_INCURRED`. The cause is always `UNKNOWN` until claim events carry one (OQ-065).
  - Dashboard headline figures (`GET /analytics/summary`): written premium, policies written, claims incurred, claims reported, within the user's branch and product scope (F103). Health is listed as not included. **BUILT** (GAP-046). Test: `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002`.
  - Renewal and retention measures for General: due, invited, accepted, renewed, declined, not renewed, lapsed, renewal rate, retention rate, average premium change, retention offers made and accepted. **BUILT** (F026, F027). Test: `GeneralProcessTests.ADeclinedRenewal_IsKeptByARetentionDiscount_AnotherAtRiskLapses_AndTheFiguresCountThem_F026_F027`.
  - Projections rebuilt from Analytics' own event log on request, and once per tenant at release, dated by the tenant's business date. **BUILT** (ADR-025). Test: `BusinessDateTests.AtHalfPastElevenUtc_AnalyticsCountsOnEachTenantsBusinessDate_AndARebuildCorrectsAFactDatedByTheUtcDay_ADR025`, `BusinessDateTests.TheReleaseRebuildsEachTenantsProjectionsOnce_AndRecordsIt`.
  - Duplicate events counted once; a policy whose configuration is missing is parked and projected once published. **BUILT** (INT-005, ADR-022). Test: `ConsumerParkingTests.Analytics_ParksAPolicyWhoseBillingFrequencyIsNotInForce_AndProjectsItOncePublished_ADR022`.
  - Metric totals supplied to Compliance's regulatory returns through a public contract (`analytics.cfg_metric_definitions`). **BUILT** (inferred link: the test prepares the return "from the metric and the ledger"). Test: `EnterpriseProcessTests.ClosedPeriod_PreparesItsReturn_ApprovedByAnotherOfficerBeforeSubmission_AndReportsFollowTheCatalogue`.
  - Tenant isolation of figures and rebuilds. **BUILT** (TEN-004). Test: `CrossTenantSuiteTests.AnotherTenantsRecords_AreNeitherRead_NorFound_NorChanged_ThroughTheApi_TEN004`.
  - Health projections. **DEFERRED** (OQ-065; code note: "decision 3: its figures come with Waves 5a and 5b").
- **Key business rules:**
  - Projections are derived and rebuildable from events; the event log keeps minimal data only (INT-007). BUILT.
  - Figures respect the user's scope (F103). BUILT.
  - One currency per tenant is assumed in projection keys (`TODO(spec)`, OQ-065). PARTLY BUILT.
- **Role templates:** `OPERATIONS_ANALYST` (`ANALYTICS`, including rebuild); `AUDITOR` (screens only, no rebuild); `PLATFORM_STATIC_DATA_STEWARD` (`CONFIG_ANALYTICS`).
- **Tenant configures:** `analytics.cfg_metric_definitions`, `analytics.ref_reporting_dimension_hierarchies`.
- **Outside services:** none.
- **Known gaps:**
  - Health not projected (OQ-065).
  - Claim cause codes not carried (OQ-065).
  - Multi-currency projection keys (OQ-065).
  - Solvency and SAM extracts (#15, B-002).
- **Notes for the writer:**
  - **What reports exist:** Analytics has no report generator. It serves three read endpoints (written premium, claims incurred, summary) plus a rebuild.
  - Catalogue reports with CSV export (`/compliance/report-runs`) and regulatory returns (`/compliance/returns`) belong to the **Compliance** module, not Analytics.
  - The Billing and Payments dashboards are their own modules' `/summary` endpoints.
  - There is no `Insys.Analytics.Tests` unit project; coverage is integration and end-to-end only.

### Identity and access (`Identity`)
- **For:** users, sign-in identities (Keycloak), roles and function grants, approvals for privileged roles, access reviews, administrative access requests, portal user links, each user's menu. **Lines:** shared.
- **Capabilities:**
  - Create a staff user in INSYS's own sign-in realm. INSYS creates the Keycloak account itself through the admin API (`KeycloakProvisioning`, `KeycloakAdmin`, client `insys-user-admin`). The user moves PendingIdentity → Invited → Active at first sign-in. If Keycloak can't be reached, the creation is parked (`KEYCLOAK_UNAVAILABLE`) and retried. If Keycloak refuses the user, it is parked with the reason (`KEYCLOAK_USER_REFUSED`, BR-006). If the username belongs to another tenant, it is parked as `KEYCLOAK_USER_CONFLICT`. Keycloak usernames take the form `<tenantcode>.<username>`. **BUILT** (ADR-017; BR-006, BR-018). Test: `KeycloakProvisioningTests.KeycloakDown_ParksTheCreation_AndItIsCreatedOnceKeycloakAnswers_KeycloakProvisioning`.
  - Retry a failed identity for a tenant administrator request after correcting it (`POST /identity/tenant-administrators/{id}/retry-identity`). The user's failed state is shown with the reason. **BUILT** (BR-018). Test: `KeycloakProvisioningTests.AnIdentityThatFails_ShowsIdentityFailed_WithTheReason_AndARetryAfterTheFixProvisionsIt_BR018`.
  - Invitation email. Keycloak sends its required-actions email: set a password, plus a one-time-code setup when the user is MFA-enrolled. An administrator can resend it to a user who hasn't signed in yet. An email that can't be sent parks only the email (`KEYCLOAK_EMAIL_UNSENT`). **BUILT** (Keycloak sends the mail, so a mail server must be set up in Keycloak). Test: `UserAccountTests.OwnRealmUser_WaitsForItsKeycloakUser_ThenIsInvited_AndCanBeInvitedAgain_KeycloakProvisioning`.
  - Reserved test addresses (`ReservedEmailAddresses`, Ade, 6 November 2026). No invitation is sent to an address under `.test`, `.example` or `.invalid`, or under `example.com`, `example.net` or `example.org`. The delivery counts as done, not parked, and it is audited as `SendInvitation` with outcome `NOT_SENT_RESERVED_ADDRESS`. The user still needs an email address. **BUILT**. Tests: `KeycloakProvisioningTests.AnInvitationToAReservedTestAddress_IsNotSent_AndNothingParks_WhileAnotherAddressIsSentAsBefore`, `ReservedEmailAddressesTests.AnAddressUnderAReservedTestDomain_IsReserved_SoNothingIsSentToIt`.
  - Users from a federated identity provider, registered by their external subject: Invited until their first request. **BUILT** on the INSYS side (IAM-002). Test: `IdentityTests.ProvisionedUser_IsInvited_UntilTheFirstSignIn`.
  - Lock, unlock and disable a user. Disabling revokes sessions at once and disables the Keycloak user (IAM-005). **PARTLY BUILT**: an invited user can't be revoked, locked or disabled (#31). Tests: `UserAccountTests.DisableUser_FromLocked_IsAllowed_AndRaisesUserDisabled_IAM005`, `KeycloakProvisioningTests.Provisioning_CreatesExactlyOneKeycloakUser_InTheUsersTenant_AndDisablingDisablesIt_KeycloakProvisioning`.
  - Assign and revoke roles:
    - with start and end dates, judged by the tenant's business date (BR-040);
    - optionally scoped to products or organisation units (F103, SRS-003);
    - a revoked role is gone on the next request (IAM-005).
    
    **BUILT**. Tests: `IdentityTests.RevokedRole_DeniesTheNextRequestWithTheSameToken_IAM005`, `MeTests.ScopedRoleAssignments_AreAccepted_AndListedForReview_F103`, `AssignRoleBusinessDateTests`.
  - Privileged roles:
    - A role is privileged if it requires MFA or holds one of the 28 privileged functions.
    - Assigning one creates a PendingApproval assignment that gives nothing until a second person approves it in Workflow (`PRIVILEGED_ROLE`). The approver can't be the person who requested it.
    - A role that requires MFA is refused for a user who isn't MFA-enrolled (IAM-003).
    
    **BUILT** (SRS-002, OQ-038, ADR-026). Tests: `MakerCheckerTests.PrivilegedRole_IsPendingAndGivesNoPermission_UntilASecondPersonApproves_OQ038`, `FunctionAccessTests.ARoleHoldingAPrivilegedFunction_IsPrivileged_AndNeedsMfaEnrolment_ADR026`.
  - Segregation-of-duties check when a role is assigned. **PARTLY BUILT**: see Key business rules for exactly what is and isn't checked. Test: `IdentityTests.AssignRole_ThatCombinesBlockedFunctions_IsRefused_SRS002`.
  - Function-based access (ADR-026). Every permissioned route belongs to a function in the catalogue (`iam.ref_function_catalogue`: screens, actions and jobs). A request passes only if all of these hold:
    - a role the user holds grants the function;
    - the tenant hasn't disabled it;
    - the tenant is entitled to its feature;
    - it fits the user's audience;
    - the tenant isn't onboarding, or the function is open during onboarding.
    
    **BUILT**. Tests: `FunctionAccessTests.DisabledFunction_IsRefusedWhenCalled_AndMissingFromTheMenu_ADR026`, `FunctionCatalogueTests.EveryPermissionedRoute_DeclaresAFunctionInTheCatalogue_BR117`.
  - The signed-in user's profile and menu:
    - `GET /identity/me` returns the profile, the tenant, the roles, and the permissions with their scopes.
    - `GET /identity/me/menu` returns the domains, sub-domains, screens and actions the user may use now.
    
    **BUILT** (ADR-026 §3). Tests: `FunctionAccessTests.Menu_ShowsTheScreensTheUserHolds_WithTheirActions_AndNoActionWithoutItsScreen_ADR026`, `MeTests.Me_ReturnsTheProfileTenantLanguages_AndEffectivePermissionsWithTheirScopes`.
  - Default role templates (25), read-only through `GET /identity/role-templates`. For a given tenant, each function is marked available or not. Templates are starting points and are never seeded. **BUILT** (BR-088, OQ-111). Test: `RoleTemplateTests.TheApi_ReturnsExactlyTheTemplateSource_BR088`.
  - A tenant's own roles and their function grants, kept as Tier 1 static data (`iam.cfg_roles_and_permission_sets`, `iam.cfg_role_function_assignments`). A grant must name a screen or action in the catalogue; JOB functions and unknown codes are refused (BR-019). **BUILT**. Test: `StaticDataTests.ARoleFunctionGrant_NamesAFunctionInTheCatalogue_OnCreateAndImport_BR019`.
  - Access reviews. A review snapshots the role assignments of all users or of privileged users only. The assigned reviewer confirms or revokes each item, and revoked roles end when the review completes. **PARTLY BUILT**:
    - the service-account scope is refused (OQ-042);
    - reviews are started by hand; `iam.cfg_access_review_schedules` is read by no code (inferred from grep).
    
    Tests: `AccessTests.AccessReview_RevokedAccess_EndsTheRoleWhenTheReviewCompletes_IAM005`, `AccessTests` (unit) `Review_CannotComplete_UntilEveryItemIsDecided`.
  - Requests for time-limited administrative access. The requester gives a purpose and a window; someone other than the requester approves or rejects (with a reason); the access expires when the window ends (TEN-007). **PARTLY BUILT**:
    - an approved grant changes no permissions (OQ-043, confirmed: the permission resolver doesn't read grants);
    - it can't be revoked early (OQ-041).
    
    Test: `AccessTests.PrivilegedAccess_IsApprovedBySomeoneOtherThanTheRequester_TEN007`.
  - A new tenant's first administrators. A platform administrator names two people, and a second platform administrator approves. Additional administrators are added the same way. Requested functions are checked against what the tenant can hold. **BUILT** (BR-013, OQ-067, BR-088). Test: `TenantAdministratorTests.TwoFirstAdministrators_AreCreatedInTheNewTenant_OnlyAfterASecondPlatformAdministratorApproves_OQ067_BR013`.
  - Portal users (member, broker, provider) linked to the party, intermediary or provider they are. The link gives nothing until a second person approves it (`EXTERNAL_SUBJECT_LINK`), and ending it takes effect at once. **BUILT**. Test: `ExternalUserLinkTests.ALink_TakesEffectOnASecondPersonsApproval_AndEndsAtOnce_ExternalUserAccess`.
  - Register service accounts (`POST /identity/users/service-accounts`). Only these may present a callback token (B-011). **PARTLY BUILT**:
    - the Keycloak client is made by hand (#19, B-014);
    - `iam.cfg_service_account_scopes` is read by no code (inferred), so scopes come only from roles;
    - no test calls this endpoint; tests seed service accounts directly (`IdentitySeed.MarkServiceAccount`).
    
    Covering test: `ProviderCallbackTests.AnEndpointRequiringAClientToken_TakesAHoldersToken_AndNoOther_ADR023`.
  - Display names for user IDs, for showing who did what. Staff only, within their own tenant. An operator ID shows as "Platform operator". **BUILT** (BR-021). Test: `DisplayNameLookupTests.StaffResolveTheirTenantsUsers_AndAnOperatorAsALabel_AndNothingElse_BR021`.
  - Preferred language. **BUILT**. Test: `MeTests.PreferredLanguage_IsSetByTheUser_FromTheRegister`.
  - Realm sign-in policy:
    - tokens carry only `sub`, `tenant_id` and the audience;
    - permission claims in a token are ignored;
    - broker and provider portals always ask for a one-time code.
    
    **BUILT** (OQ-014, BR-008, BR-094). Tests: `KeycloakRealmTests.AccessToken_CarriesSubAndTenantId_AndTheApiAudience_AndNothingAboutTheUser`, `RealmMfaTests.BrokerAndProviderPortals_AlwaysAskForAOneTimeCode_TheMemberPortalOnlyOnceSetUp_BR008`.
- **Key business rules:**
  - Privileged role approval by a second person, never the requester (SRS-002, OQ-038). **BUILT**.
  - MFA is required for privileged roles (IAM-003). **BUILT**. INSYS checks the `mfaEnrolled` flag it holds. ADR-017 says keeping that flag in step with Keycloak's MFA is an onboarding procedure (from docs, not verified in code).
  - Revocation takes effect on the next request (IAM-005). **BUILT**. Each API instance has its own permission cache (ADR-006, #4).
  - Segregation of duties. What the code does, precisely:
    - **(a) The 3 rules in code** (`RoleTemplates.SodRules`: draft/release claim instruction, build/release payout batch, add/verify payee bank account):
      - They are used only by tests: `RoleTemplateTests.EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111` checks each of the 25 templates on its own, and they also generate the role-templates addendum.
      - No production code reads them (grep finds `SodRules` only in `RoleTemplates.cs`, `RoleTemplateTests.cs` and `RoleTemplateAddendum.cs`).
      - They aren't seeded as tenant rules. No migration inserts rows into `iam.cfg_segregation_of_duties_conflict_rules` except V0052, which converts old permission-pair rows.
      - So nothing at runtime stops a tenant's own role, or a user, from holding both sides of one of these pairs.
    - **(b) The check at assignment** (`UserAccount.AssignRole`, `AssignRoleHandler`) is real and works across roles. It compares the new role's functions with the functions of all the user's other roles in effect today, and with each other, against the tenant's own BLOCK rules in `iam.cfg_segregation_of_duties_conflict_rules` (function pairs, Tier 1 static data). WARN rules don't block (`UserAccountTests.AssignRole_WarnRule_DoesNotBlock_SRS002`).
    - **(c) The check is not made:**
      - against assignments still PendingApproval;
      - against assignments that start in the future (only assignments with `IsEffectiveOn(today)` count);
      - when a pending privileged assignment is approved (`DecideAssignment` takes no rules);
      - when function grants are later added to a role that is already assigned (`RoleFunctionGrantValidator` checks only that the function exists and its kind);
      - for a new tenant, which has no BLOCK rules until it configures some.
    - **Correction to register row #34.** Row 34 says "INSYS checks segregation per role today, not across a user's roles". That is accurate for the 3 code rules, which are checked per template and only in tests. It isn't accurate for tenant-configured rules, which are checked across the user's roles in effect at assignment.
    - **The proposed 4th rule** (#34: `LINE_CONFIGURATION_MANAGER` against the underwriter and claims templates) pairs roles. The tenant rule table holds function pairs, so the 4th rule would have to be written as function pairs or as a new check (inferred).
  - Portal links and tenant administrators need a second person (SRS-002). **BUILT**.
  - Sensitive Identity commands are audited, for example `AssignRole`, `CreateKeycloakUser`, `SendInvitation` and `ProvisionServiceAccount` (IAM-006). **BUILT**.
- **Role templates:**
  - `TENANT_ADMIN`: `IDENTITY_ACCESS`, `STATIC_DATA`, `CONFIG_IDENTITY`, `CONFIG_TENANCY`, `CONFIG_STATIC_DATA`, `CONFIG_WORKFLOW`, `AUDIT`. Holds the privileged `ASSIGN_ROLE`, `REVOKE_ROLE` and the role-grant maintain and approve functions. Approves `PRIVILEGED_ROLE` and `EXTERNAL_SUBJECT_LINK`.
  - `PLATFORM_ADMIN` (operator tenant): `TENANTS`, `IDENTITY_ACCESS`, `AUDIT`.
  - `PLATFORM_STATIC_DATA_STEWARD`: `CONFIG_IDENTITY`.
  - `AUDITOR`: reads every staff screen.
- **Tenant configures:**
  - `iam.cfg_roles_and_permission_sets`
  - `iam.cfg_role_function_assignments`
  - `iam.cfg_segregation_of_duties_conflict_rules`
  - `iam.cfg_authentication_and_session_policies` (MFA-required roles)
  - `iam.cfg_access_review_schedules`, `iam.cfg_service_account_scopes` and `iam.cfg_identity_provider_configurations`: stored, but read by no code (inferred from grep)
  - Workflow authority rows for the `PRIVILEGED_ROLE` and `EXTERNAL_SUBJECT_LINK` approvals.
- **Outside services:**
  - Keycloak admin API (`KeycloakAdmin`): **real**. Production needs its address and client secret (#36).
  - Invitation email is sent by Keycloak: **real**, through Keycloak's own mail settings.
  - No stub implementation of `IKeycloakAdmin` exists in `src`.
- **Known gaps:**
  - Revoking an invitation (#31).
  - Service account clients made by hand (#19, B-014).
  - Keycloak provisioning settings in production (#36).
  - Sign-in page name in production (#35).
  - A 4th segregation rule, and the segregation enforcement gaps above (#34).
  - What an administrative access grant permits (OQ-043); early revocation of a grant (OQ-041).
  - Access reviews of service accounts (OQ-042).
  - Permission cache shared across instances (#4, ADR-006).
  - Function and menu wording review (#21, OQ-100).
- **Notes for the writer:**
  - **IAM-004 is service accounts** ("explicit scopes, limited permissions, controlled credentials"), not privileged role approval. Privileged role approval traces to SRS-002, OQ-038 and IAM-003.
  - Federation (IAM-002): the API trusts one issuer (`Authentication:Authority`). An outside provider would come in through Keycloak brokering (inferred from ADR-017; not checked in code).

### Tenancy (`Tenancy`)
- **For:** the platform operator registers tenants, takes them through onboarding to activation, and suspends, reactivates or terminates them. It also holds each tenant's time zone, isolation profile, function availability and feature entitlements. **Lines:** shared.
- **Capabilities:**
  - Register a tenant. The identifier is unique and immutable, and the time zone must be an IANA zone. Registering starts the onboarding case. **BUILT** (TEN-001, ADR-025). Tests: `TenancyTests.TenantCode_IsUnique_TEN001`, `TenancyTests.RegisterTenant_NeedsAnIanaTimeZone_ADR025`.
  - Onboarding in 11 ordered steps, from platform reference data to end-to-end tests. A step completes only when its readiness checks pass; every failing check is listed, and warnings don't block. Attestations are recorded. **BUILT** (TEN-008, OQ-029). Test: `OnboardingReadinessTests.AStep_CompletesOnlyWhenItsConfigurationIsInForce_ListingEveryFailingCheck_TEN008`.
  - Record the end-to-end test run (reference, date, outcome, who ran it). A second operator approves it; the person who recorded it can't. **BUILT** (SRS-002). Covered in the same test, which expects `MAKER_CHECKER_SAME_PERSON` for the recorder.
  - The tenant's own staff see its readiness (`GET /tenancy/readiness`). **BUILT** (BR-012). Test: `TenantAdministratorTests.ATenantsOwnStaff_ReadItsOnboardingReadiness_BR012`.
  - While a tenant is onboarding, its users can load configuration only. Business commands are refused with `TENANT_NOT_ACTIVE`. **BUILT**. Test: `OnboardingReadinessTests.AnOnboardingTenantsUsers_LoadItsConfiguration_ButItsBusinessWaitsForActivation_TEN008`.
  - Activate, suspend and reactivate, each with a required reason kept in the status history. Suspension blocks every user of the tenant and clears cached permissions. **BUILT** (TEN-008). Test: `TenancyTests.Onboarding_ThenActivation_ThenSuspension_BlocksTheTenantsUsers_TEN008`.
  - Activation and reactivation are refused while the tenant names the payment simulator outside development and testing (`PAYMENT_PROVIDER_NOT_ALLOWED`). **BUILT** (#13). Test: `SimulatorGuardTests.ActivatingAnOnboardingTenant_ThatNamesTheSimulator_IsRefused_WithItsCode`.
  - Terminate a tenant: final, can't be reactivated, and users get no access. **PARTLY BUILT**:
    - no termination event, data export, offboarding or retention process was found (grep finds no `TenantTerminated`);
    - the template text "its data enters retention" isn't backed by code (inferred).
    
    Test: `TenantTests.Terminated_CannotBeReactivated_IsInvalidStateTransition`.
  - Isolation profile A, B or C, changed with the reference of an approved data migration and recorded in history. **PARTLY BUILT**:
    - no data migration is built, and the profile is a recorded attribute only (OQ-030);
    - no code was found that routes data differently by profile (inferred from grep).
    
    Test: `TenancyTests.ChangeIsolationProfile_RequiresAnApprovedMigration`.
  - The operator tenant holds no business data. Every business module refuses it with 403 `OPERATOR_TENANT_NOT_BUSINESS`, for reads and changes alike. Tenancy, Identity, Static data, Audit, Integration and Workflow stay open to it. **BUILT** (ADR-024). Test: `OperatorTenantTests.EveryBusinessModuleEndpoint_RefusesTheOperatorTenant_AndNoPlatformModuleEndpointDoes_ADR024`.
  - Tenant isolation:
    - the tenant comes from the token only;
    - row-level security is enabled and forced on every tenant table and partition;
    - permission and rating caches are kept per tenant;
    - event consumers act in the event's own tenant.
    
    **BUILT** (TEN-002, TEN-004, TEN-005). Tests: `TenantIsolationApiTests.List_IgnoresTenantHeaders_TEN002`, `ModuleRoleAndPartitionTests.EveryTenantTable_HasRowLevelSecurityEnabledAndForced_WithATenantPolicy_TEN004`, `CrossTenantSuiteTests.AnotherTenantsRecords_AreNeitherRead_NorFound_NorChanged_ThroughTheApi_TEN004`.
  - Per-tenant function availability and feature entitlements (`tenant.cfg_tenant_function_availability`, `tenant.cfg_feature_entitlements`). **BUILT** (OQ-091). Test: `FunctionAccessTests.FunctionOfAFeatureTheTenantIsNotEntitledTo_IsRefused_AndMissingFromTheMenu_OQ091`.
  - Switch off a tenant's billing and payment daily jobs. This is a privileged function and shows in the operations view. **BUILT** (OQ-098). Test: `FunctionAccessTests.SwitchingOffABillingOrPaymentsDailyJob_IsAPrivilegedFunction_OQ098`.
  - Sample (demo) tenants, marked at creation and never changeable. Their payments and notifications reach only the stubs. **BUILT**. Test: `SampleTenantTests.TheMark_IsSetAtCreation_AndNoOneCanChangeIt`.
  - Platform operator bootstrap: two operators from an empty database (OQ-031). **BUILT** (BR-061). Test: `PlatformBootstrapTests.FromAnEmptyDatabase_TwoBootstrappedOperatorsSignIn_AndOneApprovesTheOthersPrivilegedChange_BR061`.
- **Key business rules:**
  - Every status change needs a reason and is kept in history (TEN-008). **BUILT**.
  - Activation needs every onboarding step completed (TEN-008). **BUILT** (`TenantTests.Activation_RequiresCompletedOnboarding_TEN008`).
  - Tenant administration is for the platform operator only (TEN-007). **BUILT** (`TenancyTests.TenantAdministration_IsForThePlatformOperatorOnly_TEN007`).
  - `SUSPEND_TENANT`, `TERMINATE_TENANT` and `CHANGE_ISOLATION_PROFILE` are privileged functions, so assigning a role that holds them needs approval. The actions themselves are carried out by one operator, with no second person (verified in `ChangeTenantStatus`'s handler). They are audited.
- **Role templates:** `PLATFORM_ADMIN` (`TENANTS`); `TENANT_ADMIN` and `PLATFORM_STATIC_DATA_STEWARD` (`CONFIG_TENANCY`); `FINANCE_MANAGER` holds `TENANT_SWITCH_OFF_MONEY_JOBS`.
- **Tenant configures:**
  - `tenant.cfg_locale_defaults`
  - `tenant.cfg_feature_entitlements`
  - `tenant.cfg_tenant_function_availability`
  - `tenant.cfg_tenant_product_availability`
  - `tenant.cfg_operational_limits` and `tenant.cfg_isolation_profile_assignment`: read by no code outside the generated files (inferred).
- **Outside services:** none.
- **Known gaps:**
  - Isolation profile data migration (OQ-030).
  - Termination offboarding and retention: none found, and no register row; general retention is OQ-054.
  - Hosting for Nigerian tenants (#5).
  - Customer set-up and data migration per customer (#23).
- **Notes for the writer:** the onboarding steps follow the static data load sequence. OQ-029 marks them "Proposed".

### Static data governance (`StaticData`)
- **For:** one governed way to keep every reference and configuration table: versions, effective dates, maker-checker by tier, global rows and tenant overlays, and imports. **Lines:** shared (295 tables across all modules; this module owns 13: `sdm.*` 2, `ops.*` 4, `refdata.*` 7).
- **Capabilities:**
  - Generated maintenance endpoints for every static table. They support reads as at a date, version lists, draft create and edit, submit, activate, deprecate and retire. The code is generated by `tools/Insys.StaticData.Generator`. **BUILT** (ADR-003). Test: `StaticDataTests.AllStaticDataTables_AreReachableThroughTheGeneratedEndpoints`.
  - Only the maker edits a draft, approved versions are immutable, and a rejected change returns to draft. **BUILT** (SDM-002, SDM-003). Test: `StaticDataTests.RejectedChange_ReturnsToDraft_AndOnlyTheMakerEditsIt_SDM003`.
  - Governance tiers:
    - Tier 1 always needs a second person;
    - Tier 2 needs one by default, and each tenant can switch that off;
    - Tier 3 is approved on submission.
    
    Approvals are decided in Workflow (ADR-009). **BUILT** (SDM-003, ADR-003). Tests: `StaticDataChangeTests.MakerChecker_Tier1AlwaysOn_Tier2PerTenant_Tier3Off_SDM003`, `StaticDataTests.Tier1Change_NeedsADifferentChecker_ThenActivates_SDM003`.
  - Switching Tier 2 maker-checker off is itself a Tier 1 change: it waits for a second person and is audited. Switching it on applies at once. **BUILT** (OQ-046, OQ-024). Tests: `StaticDataChangeTests.SwitchingTier2Off_WaitsForASecondPerson_OQ046`, `StaticDataTests.Tier2MakerChecker_CanBeSwitchedOffPerTenant_IsAudited_AndTier1StaysOn_SDM003`.
  - Change requests listed and read, each linking to its table route and version. Governance settings carry an ETag and take If-Match. **BUILT** (BR-014, BR-015). Test: `StaticDataChangeRequestLinkTests.ChangeRequest_GivesItsVersionNumberAndTableRoute_ThatReachTheSubmittedVersion_BR015`.
  - Global rows are kept by platform administrators only, under maker-checker, and tenants overlay them. The API and the database both refuse a tenant writing a global row. **BUILT** (SDM-008, ADR-003 §5). Tests: `StaticDataTests.PlatformAdministrators_MaintainGlobalRows_WithMakerChecker_AndTenantsOverlayThem_SDM008`, `StaticDataTests.ModuleRole_CannotWriteGlobalRows_InTheDatabase_Item6`.
  - Import drafts all or nothing, with every rejected line reported. Import batches can be listed. **BUILT** (SDM-012, F076). Test: `StaticDataTests.Import_IsAllOrNothing_AndReportsEveryRejectedLine_SDM012`.
  - An import is approved as one change, with every line shown to the checker:
    - activated together;
    - a rejection returns every line to draft;
    - a line changed after submission blocks the whole import.
    
    **BUILT** (BR-016). Test: `StaticDataImportApprovalTests.AHundredLineImport_IsApprovedWithOneDecision_TheCheckerSeeingEveryLine_AndActivatedTogether_BR016`.
  - A row naming a tenant can be read only by that tenant, in every static table. **BUILT** (BR-007). Test: `TenantNamedRowsTests.ARowNamingATenant_IsReadOnlyByThatTenant_InEveryStaticTable_BR007`.
  - Stable paging over static lists and the function catalogue. **BUILT** (BR-087). Test: `StaticDataPagingTests.PagingAStaticDataList_ReturnsEveryCodeExactlyOnce_WhenCodesStraddleAPageBoundary_BR087`.
  - Global reference data: countries and subdivisions, currencies, languages, time zones, public holiday calendars, units of measure (`refdata.ref_*`). **BUILT** (F074). Test: `StaticDataTests.AllStaticDataTables_AreReachableThroughTheGeneratedEndpoints`.
- **Key business rules:**
  - The checker must differ from the maker (SDM-003, SRS-002). **BUILT**, and the database also refuses a checker equal to the maker (ADR-003; from docs, not checked in SQL).
  - Global rows only through `/global` endpoints, as the operator (ADR-003). **BUILT**.
- **Role templates:**
  - `TENANT_ADMIN`: `STATIC_DATA`, `CONFIG_STATIC_DATA`; approves `STATIC_DATA_T1` and `STATIC_DATA_T2`.
  - `LINE_CONFIGURATION_MANAGER`: approves T1 and T2 for line configuration.
  - `PLATFORM_STATIC_DATA_STEWARD`: global rows and every `CONFIG_*`.
  - `STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER` is privileged and granted one by one.
- **Tenant configures:** the Tier 2 maker-checker setting, `sdm.cfg_static_data_entity_register` and `sdm.cfg_validation_rule_sets` (whether rule sets are applied wasn't checked).
- **Outside services:** none.
- **Known gaps:**
  - Imports are JSON bodies through the API. No spreadsheet or CSV upload was found (inferred from grep).
  - Bulk export of transactional data isn't built (F076; from docs, not verified in code).
- **Notes for the writer:** the 295-table count comes from the generated descriptors: scope G 40, GT 44, T 211; Tier 1 154, Tier 2 119, Tier 3 22.

### Audit (`Audit`)
- **For:** a tamper-resistant record of sensitive operations: who, which tenant, what, which record, when, and the outcome. Also each record's history, and the monthly partitions behind the large tables. **Lines:** shared.
- **Capabilities:**
  - Sensitive operations are audited through each module's outbox. On success the entry is written in the same transaction as the change, and on failure in a separate one with outcome FAILURE. Details are masked, and each entry is recorded once (the event ID is the row ID). **BUILT** (ADR-004, IAM-006). Tests: `OutboxAndInboxTests.SuccessfulCommand_PublishesAuditInTheSameTransaction_WithMaskedDetails_IAM006`, `OutboxAndInboxTests.FailedCommand_RollsBackAggregateAndOutbox_AndAuditsFailure_IAM006`.
  - Refused requests (403) are audited directly with outcome DENIED (`AuditSink`). **BUILT** (TEN-005, OQ-015). Test: `TenantIsolationApiTests.DeniedRequest_IsAudited_TEN005_IAM006`.
  - Audit events can only be inserted, never updated or deleted. **BUILT** (DBA-016). Test: `RowLevelSecurityTests.AuditEvents_AreInsertOnly_DBA016`.
  - A record's history, newest first, for audit readers (`GET /audit/records/{targetType}/{targetId}`). **BUILT**. Test: `RecordHistoryTests.ARecordsHistory_ListsEveryChange_NewestFirst_ForAuditReaders`.
  - Audit trail search by target type, target ID or actor (`GET /audit/events`). **PARTLY BUILT**: the code exists, but no test calling this endpoint was found.
  - Audit targets come from an enumerated list. **BUILT** (GAP-029). Test: `AuditTargetTypeTests.EveryLiteralAuditTarget_IsAnEnumeratedTargetType_GAP029`.
  - Only audit roles read the audit trail; `AUDITOR` and `HEALTH_AUDITOR` are split for medical data (HLT-008). **BUILT** (OQ-111). Test: `RoleTemplateTests.OnlyAuditRoles_HoldAFunctionThatReadsAuditEvents_OQ111`.
  - Monthly partitions for the eight partitioned tables:
    - created 3 months ahead, at start-up and every 12 hours;
    - row-level security on each partition;
    - the health check reports Degraded under 2 months of cover;
    - months are judged by the application's clock.
    
    **BUILT** (ADR-005, DBA-023). Tests: `ModuleRoleAndPartitionTests.PartitionJob_CreatesPartitionsAhead_WithRlsAndGrants_DBA023`, `ModuleRoleAndPartitionTests.PartitionHealth_IsHealthy_WhenCoveredForTwoMonths_AndDegradedOtherwise_DBA023`.
  - Retention periods per audit class (Security, Financial, Operational) and jurisdiction (`audit.cfg_audit_retention_policies`). Other modules use them; for example, a party can't be anonymised while audit evidence about it is still within its period (D-34). **PARTLY BUILT**: audit events themselves are never purged (no delete in `src` or migrations; OQ-073 says "Nothing enforces audit retention yet"). Test: `PartyAnonymisationTests.TheAuditEvidenceAboutTheParty_BlocksUntilTheLongestAuditClassHasRun_D34`.
- **Key business rules:**
  - An audit row exists if and only if the change committed (ADR-004). **BUILT**.
  - Audit rows are insert-only (DBA-016). **BUILT**.
  - No module role can touch the audit schema (ADR-001, ADR-004). **BUILT** (`ModuleRoleAndPartitionTests.ModuleRole_CannotTouchAnotherModulesSchema_DBA004`).
- **Role templates:** `AUDIT` is held by `TENANT_ADMIN`, `COMPLIANCE_OFFICER`, `DATA_PROTECTION_OFFICER`, `HEALTH_AUDITOR` and `PLATFORM_ADMIN`. `AUDITOR` reads every staff screen. `CONFIG_AUDIT` belongs to `PLATFORM_STATIC_DATA_STEWARD`.
- **Tenant configures:** `audit.cfg_audit_retention_policies` (no values are defined; OQ-073). `audit.ref_audit_event_type_catalogue` is global.
- **Outside services:** none.
- **Known gaps:**
  - Audit retention and disposal aren't enforced (OQ-073, OQ-054; no register row).
  - Audit trail search has no test.
  - The depth of the POPIA access export depends on compliance answers (#20).
- **Notes for the writer:** audit rows appear one relay pass after the change, typically within 2 seconds (ADR-004; from docs).

### Workflow (`Workflow`)
- **For:** second-person approvals for every module, within each approver's authority limits; work processes with tasks, deadlines and escalation; enquiries, complaints, disputes and appeals with regulatory timelines, ombud referral and redress. **Lines:** shared (Life, General, Health).
- **Capabilities:**
  - Maker-checker approvals for sensitive operations in all modules: a module asks for approval in its own transaction, Workflow records it, a different identified person approves or rejects, and the outcome goes back to the module as an event (ADR-009). 29 approval kinds are declared (claims settlements and payment instructions, payout batches, refunds, endorsements, non-renewals, underwriting, role assignments, bank accounts, due diligence, product versions, static data changes, period reopening, commission statements, legal hold release, case redress, Health payment runs, recoveries, credit notes). **BUILT** (SRS: SRS-002, WFL-004; ADR-009). Test: `WorkflowTests.Approval_IsDecidedBySomeoneElse_WithinTheirAuthority_WFL004`, `ApprovalTests.Approval_ByItsMaker_IsForbidden_SRS002`.
  - Approval authority limits: an approver needs authority for the approval type, and with an amount the limit must cover it, counted only in the approval's currency (no limit set = tenant base currency). A module may keep its own limits (Life claims by claim type, `life.cfg_claims_authority_limits`). **BUILT** (WFL-004; OQ-112; ADR-009 addendum). Test: `ApprovalTests.Approval_AboveTheCheckersLimit_IsForbidden_WFL004`, `ApprovalTests.AuthorityLimits_CountOnlyInTheirCurrency_OQ112`.
  - Approval inbox: list approvals, filter to those the caller can decide now; "my authority" shows the signed-in user's limits per type. **BUILT** (BR-002, BR-004, BR-081). Test: `WorkflowTests.DecidableApprovals_AreOnlyThoseTheCallerCanDecideNow_WithPaging_BR004`, `WorkflowTests.MyAuthority_ListsModuleLimitsAndTheStaticDataPermission_AsApprovingChecksThem_BR081`.
  - Approvals bound to exactly what was approved (subject hash, e.g. payee and bank-details hash); a decision for a changed subject is not applied; a grant the record refuses is parked as Not applied for someone to resolve. **BUILT** (ADR-009; D-5). Test: `ApprovalEndingsTests.ARefusedGrant_OnceTheRecordIsPutRight_AndTheParkedDeliveryRetried_IsApprovedAgain_D5`.
  - Approval endings: expiry after a configured period (`cfg_approval_expiry`, default 14 days), withdrawal by the requester, rejection with a reason; each tells the owning module. Optional approval reason kept. **BUILT** (BR-001). Test: `ApprovalTests.Approval_PastItsTimeLimit_ExpiresAndCannotBeApproved`, `ApprovalTests.Withdrawal_ByTheRequesterOnly_TellsTheOwningModule`, `WorkflowTests.Approving_WithAReason_KeepsItOnTheApproval_BR001`.
  - Approvals limited to the approver's branch/product scope. **BUILT** (F103). Test: `WorkflowTests.ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103`.
  - Onboarding readiness check: every approval type the tenant can raise has a role in force able to decide it. **BUILT**. Test: `OnboardingReadinessTests.AnApprovalTypeTheTenantCanRaise_WithNoRoleToDecideIt_IsListed_DueDiligenceOn`.
  - Static data change approvals decided through Workflow (bootstrap exception OQ-050: `static_data.approve` until the tenant configures authority for `STATIC_DATA_T{n}`). **BUILT** (SDM-003; ADR-009). Test: `StaticDataImportApprovalTests` (class).
  - Process engine: versioned process definitions (static data, `cfg_workflow_definitions`), tasks per state, guarded transitions, owning-module validation, start/suspend/resume/cancel, case notes. **BUILT** (WFL-001, WFL-002; BR-096). Test: `WorkflowTests.Process_MovesAlongItsDefinition_AndRefusesAFailedGuard_WFL001`, `ProcessInstanceTests.Process_FollowsTheDefinitionVersionItStartedOn_WFL001`.
  - Task SLAs on business calendars (working hours, weekends, holidays), breach escalation, assignment, survives restarts. **BUILT** (WFL-003, WFL-005). Test: `WorkflowTests.BreachedSla_EscalatesTheTask_AndSurvivesInTheDatabase_WFL003_WFL005`, `BusinessCalendarTests.WorkingTime_SkipsEveningsWeekendsAndHolidays`.
  - Work queues: open tasks listed earliest due first, by queue or assignee. **BUILT** (BR-028). Test: `WorkflowTests.OpenTasks_PageEarliestDueFirst_EveryTaskOnce_BR028`.
  - Service cases in four families (enquiry, complaint, dispute, appeal): register, acknowledge, link records (types allowed per case type), start, wait for complainant, resume, decide with reasons (upheld / partly upheld / rejected; enquiry answered), close, withdraw, reopen (privileged), escalate, messages, share with broker. **BUILT** (PPR 18; OQ-107, OQ-108, OQ-109; BR-126). Test: `CaseTests.AComplaint_IsAcknowledgedExtendedRejectedReviewedByAnotherPerson_AndGoesToTheOmbud_Cases12`, `ServiceCaseTests.Lifecycle_IsTheAddendumsStatuses_CasesSection4`.
  - Case timelines: acknowledge/resolve/review deadlines per case type and step, on the tenant's business calendar and time zone; extension needs a reason within the allowed maximum and is told to the complainant; overdue steps are told to the complainant once (CASE_TIMELINES job). **BUILT** (PPR 18.9; ADR-025). Test: `CaseTests.AnOverdueStep_IsToldToTheComplainantOnce_Cases7`, `CaseTimelineBoundaryTests.ATimeline_RunsOnItsBusinessCalendar_InTheCalendarsTimeZone_AtHalfPastElevenUtc_ADR025`, `ServiceCaseTests.Extension_NeedsAReason_AndTellsTheComplainant_PPR18_9`.
  - Appeals: an appeal is its own case linked to the decided case, decided by a different person; appeal routes per case type are static data (`ref_appeal_routes`, V0126) and checked at onboarding. **BUILT** (PPR 18.6). Test: `ServiceCaseTests.AnAppeal_IsDecidedBySomeoneElse_PPR18_6`, `OnboardingReadinessTests.ACaseTypesAppealRoute_IsReportedUntilItIsInForce_ATenantsOwnOrGlobal`.
  - Ombud referral of a decided case and recording the ombud's outcome (ombud schemes as reference data). **BUILT**. Test: `ServiceCaseTests.AnOmbudReferral_IsOfADecidedCase_AndKeepsItOpenForTheOutcome`.
  - Redress (compensation, goodwill) on a case: decimal amount plus currency, approved by a second person (`CASE_REDRESS`), then paid through a Payments payout batch. **BUILT, STAND-IN** (payments: `SimulatorGateway`, register #13) (PPR 18.8.3; OQ-104). Test: `CaseTests.Redress_IsApprovedByAnotherPerson_ThenPaid_AndARejectedOneIsRejected_OQ104`, `CaseConsumersTests.ApprovedRedress_IsPaidOutToThePayeesAccount_ThroughAPayoutBatch_Once_OQ104`.
  - Portal cases: members, brokers and providers raise cases, message and appeal; each sees only their own (providers their disputes). **BUILT** (BR-101; OQ-108). Test: `CaseTests.APortalMember_RaisesAndSeesOnlyTheirOwnCases_AndAppealsADecision_Cases5`, `CaseTests.AProvider_SeesOnlyItsOwnDisputes_AndStaffReadThemOnlyWithHealthClaimRead_Cases9`.
  - Complaints report for a date range, with reportability (a complaint upheld at once within the query window is not reportable). **BUILT** (PPR 18.1). Test: `CaseTests.AComplaintUpheldWithinTheQueryWindow_IsNotReportable_AndOneUpheldAfterItIs_PPR18_1`.
  - Case retention: closed cases get a retention date and are disposed of by the job after it, unless under a record hold. **BUILT** (OQ-106; D-34). Test: `CaseTests.ARecordHoldOnTheCase_KeepsItPastItsRetention_UntilReleased_D34`.
  - An upheld claim dispute reopens the General, Life or Health claims it links, once. **BUILT** (F011, F013; OQ-103). Test: `CaseConsumersTests.AnUpheldClaimDispute_ReopensTheGeneralLifeAndHealthClaimsItLinks_OnlyOnce_F011_F013_OQ103`.
- **Key business rules:**
  - Checker never the maker; the system identity never decides (SRS-002, BR-109). BUILT (`ApprovalTests.TheSystemRequester_NeverDecidesAnApproval_BR109`).
  - Authority by type, amount and currency (WFL-004, OQ-112). BUILT.
  - Approval may require an extra permission (e.g. `bank_account.verify`) (ADR-009 addendum). BUILT (inferred from ADR and `ApprovalRequested.RequiredPermission`; not separately traced to a test here).
  - Appeal decided by someone other than the original decider (`CASE_DECIDER_SAME_PERSON`, PPR 18.6). BUILT.
  - Rejection or partial upholding needs reasons (PPR 18.7.2); complaint type may require a category (PPR 18.5.3). BUILT (`ServiceCaseTests.ARejection_NeedsReasons_PPR18_7_2`).
  - Closed or withdrawn case takes no change until reopened; reopen is privileged (OQ-109). BUILT.
  - Events carry identifiers only, never the case text (INT-007). BUILT (`ServiceCaseTests.Events_CarryIdentifiersOnly_NeverTheCasesText_INT007`).
- **Role templates:** approvals screen and approve/reject (`WORKFLOW_APPROVALS`, `WORKFLOW_APPROVE`) are held by `TENANT_ADMIN`, `CLAIMS_MANAGER`, `COMPLIANCE_OFFICER`, `FINANCE_MANAGER`, `LINE_CONFIGURATION_MANAGER`, `OPERATIONS_ANALYST` (screen; template says it decides no approvals), `AUDITOR` (screen), `PLATFORM_ADMIN`, `PLATFORM_STATIC_DATA_STEWARD`. Processes: `OPERATIONS_ANALYST`, `AUDITOR`. Cases (`CUSTOMER_SERVICE` sub-domain): `CUSTOMER_SERVICE_AGENT`, `COMPLIANCE_OFFICER` (also `CASE_DECIDE_APPEAL`, the only template with it), `AUDITOR` (read). `CASE_RECORD_REDRESS` and `CASE_REOPEN` are privileged and in no template ("granted one by one"). Portal case functions: `MEMBER`, `BROKER`, `PROVIDER`. `CONFIG_WORKFLOW`: `TENANT_ADMIN`.
- **Tenant configures:** `workflow.cfg_delegation_and_approval_authority` (authority limits by role, type, limit, currency), `cfg_approval_expiry`, `cfg_workflow_definitions`, `cfg_sla_definitions`, `cfg_escalation_rules`, `cfg_task_types_and_work_queues`, `cfg_case_types`, `cfg_case_timelines`, `cfg_case_retention`, `cfg_case_reportability`; reference `ref_business_calendars`, `ref_case_categories`, `ref_ombud_schemes`, `ref_appeal_routes`, `ref_exception_codes`. Onboarding checks APPROVAL_AUTHORITY and the case set-up (OQ-110).
- **Outside services:** none directly. Redress payouts go through Payments (`SimulatorGateway` stand-in, register #13).
- **Known gaps:** a customer's approval roles and authority bands are per-customer set-up (#23); OFTZA's bands deferred (role-templates.md open points, OQ-112). Case period, 7-year retention and final statuses await compliance (#20). Nigerian appeal route waits on #24/#25. The segregation check is per role, not across a user's roles (#34).
- **Notes for the writer:** ADR-009 says static data approvals are also decided on `/{module}/static/{entity}/{code}/versions/{n}/approve`; confirmed mapped in `WorkflowModule.cs`. Default expiry of 14 days is from ADR-009 text, not checked in code.

### Documents (`Documents`)
- **For:** storing, generating and delivering documents per business record, controlling access to sensitive ones, and keeping them for their retention period or under legal hold before disposal. **Lines:** shared.
- **Capabilities:**
  - Upload and store a document against a business record in tenant-scoped storage: local file system, or an S3-compatible object store (`S3ObjectStore`) when configured; content hashed; file name kept without path. **BUILT** (DOC-001; BR-027). Test: `ObjectStoreTests.Document_IsStoredInTheObjectStore_UnderItsTenantsPrefix_AndReadBackThroughTheApi`, `DocumentDetailsTests.AnUpload_KeepsItsFileName_AndTheViewSaysWhatReadingItNeeds_BR027`.
  - Generate a document from the template version in force, recording template and product versions; all merge fields required. **BUILT, STAND-IN** (renderer: `PlainTextDocumentRenderer` outputs plain text listing the merge fields and values; no real template engine or PDF, OQ-056) (DOC-002). Test: `DocumentTests.GeneratedDocument_RecordsItsTemplateAndProductVersions_DOC002`.
  - Automatic documents on business events: General policy issued, endorsed and renewal invited; Life policy issued; Health provider payment run released (remittance advice). A policy whose product has no template is parked (ADR-022). **BUILT, STAND-IN** (same plain-text renderer). Test: `ConsumerParkingTests.Documents_ParksAPolicyWhoseProductHasNoScheduleTemplate_NamingTheProductAndDocumentType_ADR022`, `HealthProcessTests.HealthCareToProviderPayment_EnrolsAuthorisesAdjudicatesAndPaysTheProvider_WithItsCompensations_HLT002_HLT009`.
  - Document language: the party's language, then the tenant default, then English. **BUILT**. Test: `LocalisationTests.Document_IsGeneratedInThePartysLanguage_ThenTheTenantDefault_ThenEnglish_AndRecordsIt`.
  - Sensitive documents: reading confidential/personal/highly sensitive documents needs a purpose and is logged; highly sensitive also needs an allowed role. **BUILT** (DOC-003, DOC-004). Test: `SharedServicesTests.HighlySensitiveDocument_IsReadOnlyByItsAllowedRoles_AndEveryReadIsRecorded_DOC003_DOC004`.
  - Deliver a document by channel, every attempt recorded. **BUILT, STAND-IN** (delivery: `StubDocumentDelivery` records a `STUB-` reference and sends nothing; OQ-056). Test: `DocumentTests.FailedDelivery_IsRecorded_ButDoesNotDeliver`, `SharedServicesTests.GeneratedDocument_RecordsItsVersions_IsDelivered_AndKeptUntilRetention_DOC002`.
  - Retention schedules by category and jurisdiction set the retention date. **PARTLY BUILT**: only schedules triggered at CREATION set a date (`DocumentStores.cs` checks `TriggerEvent != "CREATION"`); policy-end and claim-closure triggers set none, and a document without a date is never disposed of (OQ-056). Test: `DocumentTests.Document_IsNotDisposedOf_BeforeItsRetentionDate`, `DocumentTests.Document_WithoutARetentionDate_IsKept`.
  - Disposal: a privileged action, refused before retention ends or under any hold, audited, content deleted. **PARTLY BUILT**: manual, one document at a time (no disposal job found for documents); disposal methods ANONYMISE and ARCHIVE are not specified, so every disposal deletes (`TODO(spec)`, OQ-056). Test: `DocumentTests.Document_OnLegalHold_IsNotDisposedOf_UntilReleased`.
  - Legal hold on a document with a reason code; release is a request a second person approves in Workflow (`LEGAL_HOLD_RELEASE`); the hold stays until approved. **BUILT** (OQ-111; SRS-002). Test: `LegalHoldReleaseTests.ADocument_IsNotDisposedOf_WhileItsHoldsReleaseWaits_AndIsOnceTheReleaseIsApproved_OQ111`.
  - Record holds on a whole record (party, Life/General policy, Health membership, Life/General/Health claim, case): keeps its documents, blocks case disposal and party anonymisation; released by a second person. **BUILT** (D-34). Test: `LegalHoldReleaseTests.ARecordsHold_IsPlacedOnARecordThatExistsInScope_KeepsItsDocuments_AndIsReleasedByASecondPerson_D34`.
  - Portal documents: members, brokers and providers list and read documents of their own records. **BUILT**. Test: `GeneralProcessTests.ExternalUsers_SeeAndActOnlyWithinTheirRelationship_ThroughTheirPortal_ExternalUserAccess`.
  - Onboarding readiness: at least one template in the default language. **BUILT** (inferred test coverage via onboarding readiness; registration seen in `DocumentsInfrastructure.cs`, no specific test method named here).
- **Key business rules:** not disposed of before retention date or while on hold (§18.1) BUILT; hold release needs a second person, requester cannot approve (OQ-111, SRS-002) BUILT (`DocumentTests.TheRequester_CannotApproveTheirOwnRelease_SRS002`); disposed document is not delivered BUILT; legal hold reason's authority role not checked (OQ-056 "not yet").
- **Role templates:** `DOCUMENTS` sub-domain in `GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER`, `CLAIMS_HANDLER`, `CLAIMS_MANAGER`, `HEALTH_CLAIMS_ASSESSOR`, `HEALTH_MEMBERSHIP_ADMINISTRATOR`, `CUSTOMER_SERVICE_AGENT` (store, generate, deliver). `DATA_PROTECTION_OFFICER` alone holds dispose, place and release legal hold (all privileged); `COMPLIANCE_OFFICER` approves the release. Portal: `MEMBER`, `BROKER`, `PROVIDER`.
- **Tenant configures:** `document.cfg_document_templates`, `cfg_retention_schedules`, `cfg_delivery_channel_settings`; reference `ref_document_categories` (classification), `ref_legal_hold_reason_codes`, `ref_merge_field_dictionary`.
- **Outside services:** object store (S3-compatible, real: `S3ObjectStore`; file system otherwise); renderer (stand-in: `PlainTextDocumentRenderer`); delivery (stand-in: `StubDocumentDelivery`).
- **Known gaps:** production object store keys (#37). Real template rendering and document delivery providers are open under OQ-056 and are **not listed in the go-live register's stub rows 6-14** (none found). Retention triggers other than creation (OQ-056). Disposal methods other than delete (OQ-056). 7-year retention awaits compliance (#20).
- **Notes for the writer:** a customer sees generated documents as plain text today. Flag to the owner that the renderer and delivery stubs are missing from the register.

### Communications (`Communications`)
- **For:** sending notices to parties from business events by email, SMS, push or WhatsApp, in their preferred channel and language, with consent for marketing and delivery tracking. **Lines:** shared.
- **Capabilities:**
  - Notices queued from business events: General and Life claim registered/approved/declined/settled/closed, Health claim decisions and preauthorisations, General renewal invitations and reminders, arrears payment reminders, case acknowledgements, updates and decisions, and a generic request from any module. **BUILT** (F021, F031, F099, F100, F101). Test: `TriggerMappingTests.Claim_event_sends_claim_status_with_its_status_F101`, `CaseConsumersTests.CaseNotices_AreQueuedOnTheirEvents_WithMergeValuesFromTheCase_NotTheEvent_Cases7`.
  - Templates per notification type, channel and language; merge only the type's fields; marketing names the sender and how to stop. **BUILT**. Test: `CommunicationsTests.Templates_MergeOnlyTheirTypesFields_AndMarketingNamesTheSenderAndHowToStop`.
  - Sending: channels tried in the party's preferred order; retryable failures retried with back-off, otherwise the next channel; undeliverable when no address; addresses kept masked; text as sent kept as evidence. **BUILT**. Test: `CommunicationsTests.Preferences_OrderTheChannels_AServiceNoticeKeepsOne_AndARefusedChannelFallsBack`, `CommunicationsTests.AChannelThatRejectsTheAddress_IsNotRetried_AndAPartyWithNoAddressIsUndeliverable`.
  - Email, SMS, push and WhatsApp senders. **BUILT, STAND-IN** (email, SMS, push, WhatsApp: `EmailChannel`, `SmsChannel`, `PushChannel`, `WhatsAppChannel` on `StubNotificationChannel`; validate as a provider would, record in an in-memory stub outbox, answer `STUB-` reference; reported as stubs on `/platform/environment`) (OQ-088). Test: `StubChannelTests.ValidMessage_IsAccepted_WithAStubReference_AndRecordedInTheOutbox`.
  - Party communication preferences, set by staff or by the member in the portal; a service notice keeps at least one channel. **BUILT**. Test: `CommunicationsTests.Preferences_OrderTheChannels_AServiceNoticeKeepsOne_AndARefusedChannelFallsBack`.
  - Marketing only with consent in force when sent, with proof of consent; opting out withdraws it. **BUILT** (POPIA s69). Test: `CommunicationsTests.Marketing_NeedsConsentWhenSent_AndAMemberOptingOutWithdrawsIt_POPIA69`.
  - Delivery callbacks from providers: per-tenant signed endpoints issued and revoked by staff, signature and timestamp checked, replays refused, optional service-account token. **BUILT** (ADR-023). Test: `CommunicationsTests.DeliveryCallbacks_AreSigned_ARepeatIsRefused_AndDeliveryIsRecorded_ADR023`, `CommunicationsTests.AnEndpointRequiringAClientToken_TakesAHoldersToken_AndNoStaffUsers_ADR023`.
  - Member portal: own notifications, preferences, and push devices (token never shown). **BUILT**. Test: `CommunicationsTests.Members_SeeOnlyTheirOwnNotices_OfTypesShownInThePortal_ExternalUserAccess`, `CommunicationsTests.AMember_ListsAndRemovesTheirPushDevices_AndNeverSeesTheToken`.
  - Staff notification list within their scope; Health notice text only for those who may read Health claims. **BUILT** (F103, HLT-008). Test: `CommunicationsTests.Staff_SeeOnlyNotificationsWithinTheirScope_F103`, `CommunicationsTests.AHealthNoticesText_IsShownOnlyToThoseWhoMayReadHealthClaims_HLT008`.
  - Anonymised party's notices are blanked. **BUILT** (POPIA). Test: `CommunicationsTests.AnAnonymisedPartysNotices_AreBlanked_POPIA`.
  - Missing type, provider or template parks the trigger until configured. **BUILT** (ADR-022). Test: `CommunicationsTests.MissingTypeProviderOrTemplate_ParksTheTrigger_ADR022`.
  - Marketing campaigns and inbound STOP replies. **SPECIFIED, NOT BUILT**: no campaign operation; marketing only via a module's request; no inbound reply path (OQ-094 open).
- **Key business rules:** logs carry only channel, correlation ID and reference, never address, subject or body (addendum §10) BUILT (`StubChannelTests.Logs_CarryOnlyChannelCorrelationAndReference_NeverTheAddressSubjectOrBody_Addendum10`); claim amount only on settlement (OQ-086) BUILT; a delivered message stays delivered BUILT (from route description, inferred).
- **Role templates:** `NOTIFICATIONS` in `CUSTOMER_SERVICE_AGENT` (and `AUDITOR` read); callback endpoints in `INTEGRATION_ADMINISTRATOR`; preferences (`CONSENT_PREFERENCES`) in underwriters, `HEALTH_MEMBERSHIP_ADMINISTRATOR`, `CUSTOMER_SERVICE_AGENT`, `DATA_PROTECTION_OFFICER`; portal functions in `MEMBER`. `CONFIG_COMMUNICATIONS` in `PLATFORM_STATIC_DATA_STEWARD`.
- **Tenant configures:** `communication.cfg_notification_types`, `cfg_notification_templates`, `cfg_channel_providers`; reference `ref_channels`; General `reminder_days` on renewal rules (OQ-089).
- **Outside services:** email, SMS, push, WhatsApp: all stand-ins (`StubNotificationChannel` subclasses). No real provider adapter exists.
- **Known gaps:** email sender (#6, blocks go-live), SMS sender (#7, blocks), push sender (#8, needs a mobile app), WhatsApp sender (#9, no basis in the specifications); providers undecided (OQ-088). Campaigns and STOP replies (OQ-094).
- **Notes for the writer:** the stub outbox is in memory (keeps the last 1,000 messages per channel, per `StubChannelTests.Outbox_KeepsOnlyTheLastThousandMessages_PerChannel`): nothing reaches a person today.

### Integration (`Integration`)
- **For:** keeping event deliveries between modules reliable (parking, retry, dead letters), sending events to partners by webhook, partner channel permissions, disclosure texts and acknowledgements, migration reconciliation, and hosting the partner adapters. **Lines:** shared.
- **Capabilities:**
  - Parked deliveries: an event whose tenant configuration is missing is parked, later events for the same record wait behind it, retried in order once configuration is published; retry one or all for a code; discard only with a reason, audited; escalation settings per tenant; operator view by tenant. **BUILT** (ADR-022; OQ-072; BR-011). Test: `ParkedDeliveryTests.ParkedEvent_HoldsLaterEventsForItsRecordOnly_AndIsRetriedInOrderOnceConfigurationIsPublished_ADR022`, `ParkedDeliveryTests.Discard_NeedsAReason_IsAudited_AndReleasesTheDeliveriesWaitingBehindIt_ADR022`, `PlatformParkedDeliveriesMenuTests.OperatorMenu_OffersTheParkedDeliveriesByTenantScreen_AndTheOperatorCanUseIt_BR011`.
  - Exactly-once application: a parked or retried event is never applied twice; slow consumers time out and retry. **BUILT** (INT-005). Test: `ParkedDeliveryTests.ParkedEvent_IsNeverAppliedTwice_OnReplay_OnARepeatedRetry_OrAfterAnInstanceStopsMidRetry_INT005`.
  - Dead letters: a failing consumer is retried, then dead-lettered (error type only, no sensitive text); an authorised operator replays or discards. **BUILT** (INT-006, INT-007). Test: `OutboxAndInboxTests.FailingConsumer_IsRetried_ThenDeadLettered_INT006`, `DeadLetterTests.DeadLetter_ReplayedOnlyByAnAuthorisedOperator_INT006`.
  - Outbound webhooks to partners: subscribed events sent over HTTPS, HMAC-SHA256 signed, retried by policy with back-off, then failed and dead-lettered (discard only); manual retry. **BUILT** (INT-006; OQ-066). Real HTTP transport (`HttpWebhookTransport`) by default; signing keys read from configuration (`ConfigurationSecretStore`). Test: `WebhookDeliveryTests.FailedAttempts_AreRetriedWithBackOff_UpToThePolicyLimit_ThenTheDeliveryFails_INT006`, `EnterpriseProcessTests.QuoteToCash_AccruesCommission_CedesThePolicy_ProjectsIt_AndPaysTheApprovedStatement_DST001_DST004_REI001_REI002` (webhook section).
  - Integration dashboard figures: parked by code, failing, dead letters open, failing webhooks, oldest parked, job functions switched off. **BUILT** (GAP-046). Test: covered within `ConsumerParkingTests` (`/integration/deliveries/summary` read there; inferred as the covering test).
  - Partner channel permissions: per channel, which transactions and products each role may use. **BUILT** (F005). Test: `GeneralProcessTests.ChannelPermissions_AllowOnlyTheTransactionsAndProductsTheChannelGivesTheRoles_F005`.
  - Disclosures and notices: versioned texts by display point, notice type, jurisdiction and language; acknowledgement recorded (staff or portal) with text version; issuing steps in General, Life and Health are refused until the mandatory disclosure is acknowledged; mandatory points checked at onboarding. **BUILT** (F059, F060; OQ-157). Test: `GeneralProcessTests.ADisclosurePoint_RefusesTheStepUntilAcknowledged_AndADeletionIsRefusedWhileRecordsAreKept_F059_F060`, `OnboardingReadinessTests.AMandatoryDisclosurePoint_BlocksInEachCountryAProductIsSoldIn_OtherPointsOnlyWarn_OQ157`.
  - Migration reconciliation: compares a migration batch's loaded counts and amounts (per currency) with the source system's totals and reports every difference; changes nothing. **BUILT** (B-013; BR-128). Test: `MigrationReconciliationTests.EachCurrency_ReconcilesOnItsOwn_NeverAgainstASumAcrossThem`.
  - Partner adapters hosted here: screening, identity verification, general ledger, reinsurer, regulator; every call logged by code and reference only. **BUILT, STAND-IN** (screening, identity verification, GL, reinsurer, regulator: `StubPartnerAdapters`). Test: `IdentityVerificationTests.WithAnActiveProvider_AnAddedIdentifierIsChecked_APassVerifiesIt_AndAFailureLeavesItUnverified_F001`.
  - Imports: the data imports themselves (parties, policies, memberships, open claims, outstanding premiums) are in the line modules, not Integration; not verified here. Integration only reconciles.
  - Exports: no generic export capability found in Integration (inferred from grep).
  - API catalogue, rate-limit policies, event schema versions, credential references, external code mappings, portal branding: static data tables only. **PARTLY BUILT**: they can be maintained as static data, but no code outside the generated static data layer reads them (inferred from grep). API rate limiting is one global per-minute limit from configuration (`RateLimiting:PermitsPerMinute`), not per tenant.
- **Key business rules:** discard never automatic, always with a reason, audited (ADR-022) BUILT; dead-letter replay/discard by an authorised operator (INT-006) BUILT; a failed webhook cannot be replayed, only discarded (OQ-066) BUILT; payloads carry references only (INT-007) BUILT.
- **Role templates:** `INTEGRATION_OPERATIONS` in `OPERATIONS_ANALYST` (retry and replay; discards nothing) and `INTEGRATION_ADMINISTRATOR` (also discards, parking settings, callback endpoints, `CONFIG_INTEGRATION`). Disclosures screen and acknowledgement also in `LIFE_UNDERWRITER`; portal disclosure functions in `MEMBER`. `AUDITOR` reads migration reconciliation.
- **Tenant configures:** `integration.cfg_webhook_subscriptions`, `cfg_retry_and_dead_letter_policies`, `mst_partner_register`, `channel.cfg_channel_definitions`, `channel.cfg_channel_transaction_permissions`, `channel.cfg_disclosure_and_notice_texts`; parking escalation setting.
- **Outside services:** partner webhooks real (`HttpWebhookTransport`; `StubWebhookTransport` only when configured "stub"). Screening, identity verification, GL, reinsurer, regulator: stand-ins (`StubPartnerAdapters`).
- **Known gaps:** GL adapter (#10), screening and identity verification (#11), reinsurer and regulator (#12). Webhook signing secrets come from configuration; whether production feeds them from OpenBao (#1) is not verified.
- **Notes for the writer:** the "imports" a customer cares about belong to the line modules' inventory sections. Inbound callbacks from payment providers are in Payments, not here.

### AI Assist (`Assist`)
- **For:** a planned optional AI helper (explanations of refusals and premiums, product drafting, tenant set-up wizard). Today only the safety foundation is built and no model is connected. **Lines:** shared.
- **Capabilities:**
  - Tenant AI settings: jurisdiction, where inference runs, legal basis, explanations on/off (off by default), If-Match on change; Assist refuses until set and refuses inference outside the jurisdiction without a legal basis. **BUILT** (AIA-050). Test: `AssistTests.Settings_RecordTheJurisdiction_WhereInferenceRuns_AndTheLegalBasis_AIA050`, `AssistTests.InferenceOutsideTheJurisdiction_WithNoBasis_IsRefused_AndSoIsNoSetting_AIA050`.
  - Feature `AI_ASSIST` and six functions in the catalogue, off by default and in no role template; `ASSIST_OVERLAY_APPROVE` privileged and operator-only. **BUILT** (AIA-001). Test: `AssistTests.AFunctionHolder_WithoutTheFeature_IsRefused_AIA001`, `AssistTests.SixFunctions_AreInTheCatalogue_InNoRoleTemplate_AndOverlayApprovalIsPrivileged_ForThePlatformOnly`.
  - Model-call pipeline (internal, no API route): data minimiser masks identifiers and names, data delimited so it cannot pass as instructions, number verifier rejects any number not in the source, run and payload recorded append-only, audited; model has no tools. **BUILT, STAND-IN** (AI model: deployments register `NoAiModelProvider`, which always fails, so every run ends UNAVAILABLE; `StubAiModelProvider` exists only in `tests/Insys.Testing`) (AIA-033, AIA-051, AIA-060, AIA-061). Test: `AssistTests.OutputWithANumberNotInTheSource_IsNotReturned_AndIsKeptForEvaluation_AIA033`, `AssistTests.TheStub_IsRegisteredOnlyByTheTestHost_AndTheDeploymentsDefaultHasNoModel`.
  - Usage metering and monthly token cap (default 100,000 from configuration), checked before each call; monthly usage report. **BUILT** (AIA-073). Test: `AssistTests.TheCap_RefusesTheNextCall_WithItsCode_AndMakesNoCall_AIA073`.
  - INSYS carries on when the model is unavailable. **BUILT** (P8). Test: `AssistTests.AProviderOutage_IsRecorded_AndNothingIsThrown_P8`.
  - Tenant isolation, prompt-injection and no-write-tools security gate. **BUILT** (AIA-060, AIA-061, AIA-062). Test: `AssistTests.Gate_Isolation_ATenantSeesOnlyItsOwn_ThroughTheApiAndTheRole_AIA062`.
  - Explain a refusal, explain a premium, product draft, tenant set-up wizard, country overlay approval. **DEFERRED** (register #29: Wave 7 paused after 7.1 until the Preview is live; batches 7.2-7.8). Functions exist in the catalogue; no routes exist (only `GET/PUT /assist/settings` and `GET /assist/usage`).
  - A real AI model provider. **DEFERRED** (register #29; planned for batch 7.3 behind ADR-027's SDK guard; waits on a provider contract and D-7).
- **Key business rules:** off by default, INSYS works without it (#29) BUILT; residency/legal basis before inference (AIA-050) BUILT; cap before call (AIA-073) BUILT; output with unverified numbers withheld (AIA-033) BUILT.
- **Role templates:** none; the six functions are in no template (granted one by one).
- **Tenant configures:** `assist.tenant_setting` through `PUT /assist/settings` (not static data); the monthly cap is the operator's and has no route yet.
- **Outside services:** AI model: none connected (`NoAiModelProvider`); no real provider is called anywhere.
- **Known gaps:** #29 (Wave 7 paused). From the 7.1 review (`docs/handover-backend.md` lines 196-205): free text must not reach a model until real name detection exists (D-15); currency beside every amount; transaction held open across the model call (AIA-072); no operator route to set a tenant's cap; residency check applies to every feature.
- **Notes for the writer:** do not describe AI Assist as a customer feature: nothing a user can trigger calls a model.

## 3. Cross-cutting capabilities

### Multi-tenancy and isolation
- **Tenant taken only from the signed-in user's token.** A tenant given in the query string or a header is ignored, and a token with no tenant gets 403. **BUILT** (TEN-002). Tests: `TenantIsolationApiTests.List_IgnoresTenantHeaders_TEN002`, `TenantIsolationApiTests.TokenWithoutTenant_Is403_AndReturnsNoData_TEN002`.
- **Tenant set at the start of every database transaction** (`SELECT set_config('app.tenant_id', @tenantId, true)`) in `src/BuildingBlocks/Insys.BuildingBlocks.Infrastructure/Persistence/TenantTransactionInterceptor.cs`. A query with no tenant fails instead of returning data. **BUILT** (DBA-003). Tests: `RowLevelSecurityTests.Query_WithoutTenant_FailsInsteadOfReturningData_DBA003`, `RowLevelSecurityTests.Query_AfterAPreviousTenantTransactionEnded_FailsInsteadOfReturningData_DBA003`.
- **PostgreSQL row-level security, enabled and forced on every tenant table and partition.** The application's roles are not owners and can't bypass it. **BUILT** (TEN-004, DBA-002, DBA-023). Tests: `ModuleRoleAndPartitionTests.EveryTenantTable_HasRowLevelSecurityEnabledAndForced_WithATenantPolicy_TEN004`, `RowLevelSecurityTests.ModuleRole_IsNotOwner_AndCannotBypassRls_DBA002`, `RowLevelSecurityTests.Insert_ForAnotherTenant_IsRejected_TEN004`.
- **EF query filters on every tenant-scoped entity, as a second layer.** **BUILT** (DBA-001). Test: `DbContextSchemaTests.DbContext_TenantScopedEntities_HaveTenantFilter_DBA001` (architecture).
- **One database role per module, and each module owns its own schemas** (ADR-001, ADR-002). A module role can't touch another module's schema. There are 24 DbContexts, each mapped only to its own schema. **BUILT** (DBA-004). Tests: `ModuleRoleAndPartitionTests.ModuleRole_CannotTouchAnotherModulesSchema_DBA004`, `DbContextSchemaTests.OwnedSchemas_MatchTheModuleRoles_AndNoSchemaHasTwoOwners_DBA004`.
- **Tests that prove one tenant can't read, find or change another tenant's data:**
  - Through the API: `CrossTenantSuiteTests.AnotherTenantsRecords_AreNeitherRead_NorFound_NorChanged_ThroughTheApi_TEN004`, `EndpointSecuritySweepTests.EveryEndpoint_WithATokenForAnotherTenant_Is403_TEN003`, `TenantIsolationApiTests.Get_AnotherTenantsRecord_Is404_TEN004`, `GeneralTenantIsolationTests.AnotherTenantsRecords_AreNotFound_TEN002`.
  - In the database: `RowLevelSecurityTests.Query_WithTenant_SeesOnlyThatTenant_TEN004`, `RowLevelSecurityTests.Update_OfAnotherTenantsRow_AffectsNothing_TEN004`, `ModuleRoleAndPartitionTests.Partition_QueriedDirectlyAsTheModuleRole_ReturnsNoOtherTenantsRows_DBA023`.
  - Caches and events: `CrossTenantSuiteTests.PermissionCache_IsKeptPerTenant_TEN004`, `CrossTenantSuiteTests.RatingCache_IsKeptPerTenant_TEN004`, `CrossTenantSuiteTests.EventConsumers_ActInTheEnvelopesTenantOnly_TEN004`, `OutboxAndInboxTests.ConsumerTenantValidation_EventForTenantA_CannotWriteTenantBRows_TEN004`.
- **Isolation profiles A, B and C** (`tenant.cfg_isolation_profile_assignment`, `Tenant.IsolationProfile`). **PARTLY BUILT.** The profile is recorded at registration and can be changed, and a change needs an approved data-migration reference (`TenancyTests.ChangeIsolationProfile_RequiresAnApprovedMigration`, `TenantTests.ChangeIsolationProfile_RequiresAnApprovedDataMigration`). Missing:
  - No data migration is built for a profile change (OQ-030, open).
  - Inferred: the profile changes nothing about where or how data is stored. No code outside Tenancy reads it, so every tenant shares the same schemas under row-level security.
- **The operator tenant holds no business data** (ADR-024). It is bootstrapped with two operators, one approving the other's change. **BUILT.** Test: `PlatformBootstrapTests.FromAnEmptyDatabase_TwoBootstrappedOperatorsSignIn_AndOneApprovesTheOthersPrivilegedChange_BR061`.

### Security
- **Sign-in through Keycloak** (ADR-017): the API accepts the realm's tokens, and a token signed with the development key is refused outside Development and Testing. **BUILT.** Tests: `KeycloakRealmTests.Api_AcceptsTheRealmsToken`, `DevelopmentSigningKeyTests.TokenSignedWithTheDevelopmentKey_IsRejected_OutsideDevelopmentAndTesting`.
- **Keycloak accounts made by INSYS for each user** (provisioning). If Keycloak is down, the request is parked and retried later. **BUILT.** Test: `KeycloakProvisioningTests.KeycloakDown_ParksTheCreation_AndItIsCreatedOnceKeycloakAnswers_KeycloakProvisioning`. Production configuration is still needed (register row 36). Service-account clients are still made by hand (B-014, row 19).
- **Tokens carry only `sub`, `tenant_id` and the API audience.** Only administrators can edit `tenant_id`. **BUILT.** Tests: `KeycloakRealmTests.AccessToken_CarriesSubAndTenantId_AndTheApiAudience_AndNothingAboutTheUser`, `KeycloakRealmTests.TenantId_IsEditableByAdministratorsOnly_NeverThroughTheAccountConsole`.
- **Permissions worked out on every request from the user's role assignments and the function catalogue** (ADR-026). A revoked role or a disabled user is refused on the next request, even with the same token. **BUILT** (IAM-005). Tests: `IdentityTests.RevokedRole_DeniesTheNextRequestWithTheSameToken_IAM005`, `IdentityTests.DisabledUser_IsDeniedOnTheNextRequest_IAM005`. The architecture test `PermissionLiteralRuleTests.NoPermissionCheck_TakesAStringLiteral_ADR026` stops permission checks written as text literals.
- **Permission cache** (ADR-006): held in memory for 30 seconds per tenant and user. A second API instance on the same database refuses to start (`SingleInstanceGuard`). **BUILT.** Test: `OperationsTests.SecondApiInstance_OnTheSameDatabase_RefusesToStart_ADR006`. Running more than one instance is **DEFERRED**: it needs shared cache evictions (register row 4, and only if the load test shows one instance isn't enough).
- **MFA:**
  - Broker and provider portals always ask for a one-time code. The member portal asks once the member has set it up. **BUILT** (BR-008), in the realm configuration. Test: `RealmMfaTests.BrokerAndProviderPortals_AlwaysAskForAOneTimeCode_TheMemberPortalOnlyOnceSetUp_BR008`.
  - A role that holds a privileged function needs MFA enrolment before it can be assigned. **BUILT** (IAM-003). Test: `IdentityTests.AssignRole_ThatRequiresMfa_ToAUserWithoutMfa_IsRefused_IAM003`.
  - Staff step-up through `acr_values` is not configured (`RealmMfaTests.StaffUseTheRealmsBrowserFlow_AndStepUpWithAcrValuesIsNotConfigured_BR008`).
  - MFA in production Keycloak is register row 2.
- **Maker-checker: the checker must be someone other than the maker** (SRS-002, `BuildingBlocks.Domain/MakerChecker.cs`). A privileged role assignment stays pending and gives no permission until a second person approves it (IAM-004). **BUILT.** Tests: `MakerCheckerTests.PrivilegedRole_IsPendingAndGivesNoPermission_UntilASecondPersonApproves_OQ038`, `MakerCheckerTests.SwitchingTier2Off_NeedsASecondPersonsApproval_OQ046`.
- **Segregation of duties when a role is assigned.** **PARTLY BUILT.**
  - **What is checked:** `UserAccount.AssignRole` (`src/Modules/Identity/Insys.Identity.Domain/Users/UserAccount.cs` ~l.497) compares the new role's functions with the functions the user already holds through their other roles in effect. It refuses with `SEGREGATION_OF_DUTIES` only for the function pairs in the tenant's own `iam.cfg_segregation_of_duties_conflict_rules` rows marked `BLOCK`. Tests: `IdentityTests.AssignRole_ThatCombinesBlockedFunctions_IsRefused_SRS002`, `UserAccountTests.AssignRole_CombiningBlockedPermissions_IsRefused_SRS002`.
  - **The three built-in pairs** in `RoleTemplates.SodRules` (who prepares a claim payment doesn't release it; who builds a payout batch doesn't release it; who adds a payee account doesn't verify it) are checked only against the templates, each template on its own (`RoleTemplateTests.EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111`). Inferred: they are not seeded as tenant rows, as no migration or code inserts them. So at assignment they apply only if the tenant configures them as rules.
  - **The fourth rule is missing** (register row 34, "about 1 day", not blocking): `LINE_CONFIGURATION_MANAGER` together with an underwriter or claims role. It pairs whole roles, which the function-pair check above can't express.
  - The row's wording, "per role today, not across a user's roles", fits the template check. The tenant-rule check at assignment already looks across roles (see the notes).
- **Field encryption** (ADR-018, amended by ADR-027):
  - Envelope encryption (`EnvelopeFieldProtector`): data keys are stored in PostgreSQL and protected by a key service (`IKeyManagementService`). The Helm chart sets `FieldProtection__Provider=envelope` and `Kms__Provider=openbao`, so encryption goes through **OpenBao Transit** (`OpenBaoKeyManagementService`). **BUILT.** Tests: `OpenBaoTransitTests.ARotatedTransitKey_StillDecryptsWhatItProtected_AndNewKeysUseTheNewVersion` (against a real OpenBao), `EnvelopeEncryptionTests.IdentifierNumber_IsEncryptedWithTheCurrentDataKey_AndTheRegisterHoldsOnlyItsCiphertext`, `BankAccountTests.AccountNumber_IsEncryptedAtRest_AndNeverReturnedOrPublished`.
  - The default for local work and CI is a local AES-GCM protector (`AesGcmFieldProtector`).
  - Huawei DEW stays as an optional adapter. Its test (`HuaweiCloudTests.DewKms_CreatesADataKey_AndDecryptsItsCiphertextToTheSameKey`) is skipped unless Huawei credentials are present.
- **Idempotency keys on creates.** A missing key gets 422. A replay with the same key returns the original response and runs once. The same key with a different request gets 422. **BUILT** (INT-003). Tests: `IdempotencyTests.Replay_WithSameKeyAndRequest_ReturnsOriginalResponse_AndRunsOnce_INT003`, plus the architecture test `ConcurrencyContractTests.EveryChange_TakesIfMatch_AndEveryCreate_AnIdempotencyKey_BR031`.
- **ETag and If-Match concurrency.** `row_version` is a concurrency token in every DbContext, and a stale version gets 412. **BUILT** (DBA-011, BR-031, BR-116). Tests: `OutboxAndInboxTests.RenameWithStaleVersion_IsPreconditionFailed_AndCurrentVersionIncrements_DBA011`, `DbContextSchemaTests.DbContext_RowVersion_IsConcurrencyToken_DBA011`, `ConcurrencyContractTests.EveryIfMatchOperation_HasAReadByTheSameCaller_GivingItsVersion_BR116`.
- **Errors give no internal detail:** an unexpected failure returns 500 with only a correlation ID. **BUILT.** Test: `ErrorResponseTests.AnUnexpectedFailure_Is500_WithTheCorrelationId_AndNoInternalDetail`.
- **Gaps:**
  - Revoking an invitation is not built (row 31, blocking).
  - Rules evaluation takes its inputs from the caller, not from the proposal (row 33, blocking).
  - The production sign-in page's name (row 35).
  - The penetration test has not been done (row 3).

### Audit (IAM-006, ADR-004)
- **Audit entries for sensitive commands go through the module's outbox in the command's own transaction.** A failed command is audited separately, with outcome FAILURE. Details are masked. **BUILT.** Tests: `OutboxAndInboxTests.SuccessfulCommand_PublishesAuditInTheSameTransaction_WithMaskedDetails_IAM006`, `OutboxAndInboxTests.FailedCommand_RollsBackAggregateAndOutbox_AndAuditsFailure_IAM006`.
- **Refused requests are audited** (written directly through `IAuditSink`). **BUILT** (TEN-005). Test: `TenantIsolationApiTests.DeniedRequest_IsAudited_TEN005_IAM006`.
- **Audit records are insert-only in the database.** **BUILT** (DBA-016). Test: `RowLevelSecurityTests.AuditEvents_AreInsertOnly_DBA016`.
- **Audit can be read:** by event (`GET /audit/events`) and as one record's history (`GET /audit/records/{type}/{id}`), and only by audit roles. **BUILT.** Tests: `RecordHistoryTests.ARecordsHistory_ListsEveryChange_NewestFirst_ForAuditReaders`, `RoleTemplateTests.OnlyAuditRoles_HoldAFunctionThatReadsAuditEvents_OQ111`.
- **Reads that must be audited, such as clinical reads, are written as commands.** No query handler may write an audit entry (architecture rule). **BUILT** (HLT-008). Test: `QueryAuditRuleTests.NoQueryHandler_RecordsAnAuditEntry_HLT008`.
- **Every party search is logged,** with any identifier stored only as a hash. **BUILT** (PTY-005). Test: `PartySearchTests.EverySearch_IsInTheAccessLog_WithTheIdentifierByHashOnly_PTY005`.
- **Audit partitions are created ahead of time** (ADR-005). **BUILT.** Test: `ModuleRoleAndPartitionTests.PartitionJob_CreatesPartitionsAhead_WithRlsAndGrants_DBA023`.
- **Audit retention:** periods per event class are configured (`audit.cfg_audit_retention_policies`) and used to block anonymisation. Inferred: no job deletes audit events once their period ends; no purge code was found in `src/Modules/Audit`.

### Data protection (POPIA)
- **Data subject requests** (`/api/v1/party/data-subject-requests`): access, correction and deletion.
  - The due date comes from the tenant's response days (OQ-159).
  - A second person approves after the person's identity is confirmed.
  - The event published names no party.
  - **BUILT.** Tests: `DataSubjectRequestTests.ASecondPerson_ApprovesAfterIdentityIsConfirmed_SRS002`, `DataSubjectRequestTests.TheEvent_NamesNoParty_POPIA`.
  - End to end, a deletion is refused while records must still be kept: `GeneralProcessTests.ADisclosurePoint_RefusesTheStepUntilAcknowledged_AndADeletionIsRefusedWhileRecordsAreKept_F059_F060`.
- **Access export** (POPIA s23, `GET .../data-subject-requests/{id}/export`). **PARTLY BUILT.**
  - What it does: an approved access request returns the party and the records linked to it, gathered from each module's linked-record source. Health records are included only for the clinical function. The export is audited (`DataSubjectRequests.cs` ~l.202).
  - Unit test: `DataSubjectRequestTests.HealthRecords_AreClinical_InAnExport_HLT008`. No integration test of the export endpoint was found.
  - Open: how deep the export must go is for compliance to decide (register row 20; 3 to 5 days per module if it must go deeper).
  - Inferred: today it lists linked records, not their full contents.
- **Erasure as anonymisation.** A party is anonymised only when none of its records is still open, within retention or under legal hold. Without a retention period for payments, it fails closed. **BUILT** (D-34). Tests: `PartyAnonymisationTests.APartyWhoseRecordsAreAllPastRetention_AndNotHeld_IsAnonymised_D34`, `PartyAnonymisationTests.WithNoRetentionPeriodInForceForPayments_APaymentBlocks_FailClosed_D34`. An anonymised party's notices are blanked: `CommunicationsTests.AnAnonymisedPartysNotices_AreBlanked_POPIA`.
- **Retention:**
  - Working default for South Africa: 7 years after a policy ends (OQ-120, V0064; "for compliance to confirm before go-live").
  - Party dormancy (OQ-054).
  - The search log is minimised after 12 months and deleted after 5 years (OQ-073).
  - **BUILT.** Tests: `PartyAnonymisationTests.WithNoPolicyOfItsOwn_ATenantKeepsAnEndedPolicyForTheWorkingDefault_SevenYears_OQ120`, `SearchLogRetentionTests.Retention_MinimisesAfter12Months_DeletesAfterThePeriod_ExceptUnderAHold_AndAuditsEachRun_OQ073`.
  - The 7-year period and the final statuses are still with compliance (row 20).
- **Legal hold** on a record, a party or its documents. It is released only by a second person. **BUILT.** Tests: `LegalHoldReleaseTests.ARecordsHold_IsPlacedOnARecordThatExistsInScope_KeepsItsDocuments_AndIsReleasedByASecondPerson_D34`, `PartyAnonymisationTests.ALegalHoldOnThePartyItself_BlocksItsAnonymisation_D34`.
- **Marketing consent:** consent is checked when a marketing message is sent, and an opt-out withdraws it (POPIA s69). **BUILT.** Test: `CommunicationsTests.Marketing_NeedsConsentWhenSent_AndAMemberOptingOutWithdrawsIt_POPIA69`.
- **Medical and identity data kept out of events, logs and exceptions.** **BUILT** for the areas tested below.
  - Events carry references, not values: `OutboxAndInboxTests.Command_EventPayload_CarriesReferencesNotValues_INT007`, `PartyTests.Events_CarryNoPersonalData_INT007`.
  - Screening hits stay out of events, tasks and audit: `DueDiligenceTests.NoEventTaskOrAuditEntry_CarriesAHitsListNameOrScore_AndAPortalSchemaHasNoDueDiligence_DueDiligence3`.
  - A decline notice names no medical condition: `ChronicRegistrationTests.ADeclinesNotice_NamesNoCondition_HLT008`.
  - Dead letters never carry an exception's message: `DeadLetterErrorRuleTests.NoDeadLetter_IsGivenAnExceptionsMessage_BR017` (architecture).
  - **Tests that capture and check logs:**
    - `StubChannelTests.Logs_CarryOnlyChannelCorrelationAndReference_NeverTheAddressSubjectOrBody_Addendum10` (notices: address, subject, body).
    - `ProviderCallbackTests.KeyAndSecret_AreNeverStoredInPlainText_ListedOrLogged_ADR023` (payment provider secrets).
    - `KeycloakProvisioningTests` (the client secret is not logged).
  - No log-capture test was found for medical or identity fields in general. Inferred: outside notices and secrets, keeping these out of logs rests on the event and dead-letter rules above, not on a test of the logs.

### Business dates and time zones (ADR-025)
- **Each tenant has an IANA time zone, required at registration.** Business dates are judged in that zone, while timestamps stay in UTC. **BUILT.** Tests: `TenancyTests.RegisterTenant_NeedsAnIanaTimeZone_ADR025`, `BusinessDateTests.BusinessDate_IsTheTenantsLocalDate_WhileTimestampsStayInUtc_ADR025`, `BusinessDateTests.AtOneThirtyLocal_TheDebitOrderDueTodayIsInvoiced_AndPostsToTheNewPeriod_AfterOctoberIsClosed_GAP044`, `AssignRoleBusinessDateTests.At2130Utc_AUtcPlus3Tenant_IsAlreadyOnTheNextDay_AndAUtcPlus2TenantIsNot_BR040`.
- **Architecture rules:**
  - No production code takes a business date from the UTC clock: `BusinessDateRuleTests.NoProductionCode_TakesABusinessDateFromTheUtcClock_BR040`.
  - Application SQL never reads the database's clock: `TimeSourceTests.ApplicationSql_NeverReadsTheDatabasesClock_V0093`.
- **Fixed-date end-to-end runs:** the `end-to-end-dates` matrix in `.github/workflows/ci.yml` (l.331-371) runs the process flows on fixed clocks (`INSYS_TEST_CLOCK`). The dates are the 28th, 30th and 31st, 29 February, and 23:30 UTC on a month end (already the next day in Johannesburg). Each date runs as two jobs (General, then Life, Health and Enterprise). **BUILT**; green on `d488b32`.
- **Default time zone when none is set:** `Africa/Johannesburg` (workflow calendars, ADR-022 decision 6). A Nigerian default is deferred (row 24).

### Money
- **Money is a decimal amount plus an ISO 4217 currency. Adding two different currencies is refused.** **BUILT** (DBA-012). Tests: `MoneyTests.Money_HasNoFloatingPointMembers_DBA012`, `MoneyTests.Money_Add_DifferentCurrency_IsRejected_DBA012`.
  - That float test is a unit test in `Insys.BuildingBlocks.Tests`, not one in `Insys.ArchitectureTests`, and it checks only the `Money` type.
  - A grep found no `double`, `float` or `real` money columns or properties in `src` or `db/migrations` (inferred: no solution-wide rule enforces this).
- **Rounding:**
  - `Money.Round(RoundingRule)` takes decimal places and a midpoint mode.
  - Rating rounds by the tenant's rule per currency (`rating.cfg_rounding_and_currency_precision_rules`, `RatingStores.cs` l.161). A currency without a final rounding rule is incomplete configuration (`RatingEngineTests.Currency_WithoutAFinalRoundingRule_IsIncompleteConfiguration`).
  - Billing spreads an annual premium so the instalments total it exactly (`BillingTests.AMonthlyScheduleOfAnAnnualPremium_TotalsItExactly_TheLastInstalmentTakingTheRounding`).
  - **PARTLY BUILT:** outside Rating, 31 `Math.Round(..., 2, ...)` calls in `src` hard-code two decimal places.
  - Rounding by currency (Rwanda, Senegal, Côte d'Ivoire: currencies with no cents) is **DEFERRED** (register row 26, 5 to 8 days, only if the first customer is there).
  - Limits with a currency and journeys in two currencies (DRC) are **DEFERRED** (row 27).
- **Posted financial records are never changed; corrections are reversals** (ADR-011, FIN-002, FIN-003).
  - Journals must balance when the transaction commits, in one currency.
  - Module roles hold no UPDATE or DELETE on posted tables, and a trigger refuses changes even from the owner.
  - A journal can be reversed at most once.
  - **BUILT.** Tests: `PostedFinancialRecordsTests.EvenTheOwner_CannotChangeAPostedJournal_TheTriggerRefuses`, `PostedFinancialRecordsTests.UnbalancedJournal_IsRefusedAtCommit`, `PostedFinancialRecordsTests.Journal_IsReversedAtMostOnce`, `LedgerTests.ReverseJournal_SwapsSides_KeepsTheOriginalUnchanged_FIN003`.
  - ADR-011's own status line still reads "Proposed".

### Events and integration
- **Outbox per module schema, written in the same transaction as the change.** **BUILT** (DBA-006). Tests: `OutboxAndInboxTests.Command_WritesAggregateAndOutboxEvent_InOneTransaction_DBA006`, `DbContextSchemaTests.DbContext_MapsTheModuleOutbox_DBA006`.
- **Relay to consumers in the event's own tenant. A failed delivery is retried with back-off, then dead-lettered and can be replayed.** **BUILT** (INT-006). Tests: `OutboxAndInboxTests.Relay_PublishesToConsumer_WithEnvelopeTenant_AndSetsPublishedAt`, `OutboxAndInboxTests.FailingConsumer_IsRetried_ThenDeadLettered_INT006`, `TenantIsolationApiTests.Replay_AnotherTenantsRecord_Is404_AndLeavesItUnchanged_TEN004`.
- **Inbox deduplication:** a duplicate delivery is skipped. **BUILT** (INT-005, DBA-007). Test: `OutboxAndInboxTests.DuplicateDelivery_IsSkippedByTheInbox_INT005_DBA007`.
- **Parked deliveries** (ADR-022): an event whose tenant configuration is missing is parked instead of failing. It is delivered once the configuration is published. Deliveries waiting on a disabled job function are parked too. **BUILT.** Tests: `ConsumerParkingTests.Billing_ParksAPolicyWhoseCollectionMethodIsNotInForce_AndBillsItOncePublished_ADR022`, `FunctionAccessTests.AConsumerWhoseJobFunctionIsDisabled_ParksItsDelivery_UntilTheFunctionIsEnabled_ADR026`.
- **Health reports how far the outbox is behind.** **BUILT** (GAP-034). Test: `OperationsTests.Health_ReportsOutboxLag_DegradedThenUnhealthy_WhileReadinessStaysOk_GAP034`.
- **Architecture rules:**
  - Every event mapper is registered, and every event a consumer waits for is built somewhere: `EventMapperRuleTests.EveryEventMapper_IsRegistered`, `EventMapperRuleTests.EveryEventAConsumerWaitsFor_IsBuiltSomewhere`.
- **No message broker:** the bus runs inside the process (ADR-027, "stays out").

### Deployment
- **Cloud-neutral deployment** (ADR-027; supersedes ADR-016 Huawei, which becomes optional notes): needs only Kubernetes and PostgreSQL 18.
  - The Helm chart `deploy/helm/insys` holds the API, the migration job, PostgreSQL, Keycloak, OpenBao, SeaweedFS (an S3-compatible object store) and the console, each in the cluster or pointed at an existing one.
  - It runs one API instance (ADR-006).
  - **BUILT** and proven by the `deployment-smoke` workflow (`.github/workflows/deployment-smoke.yml` → `deploy/smoke/run.sh`) on a kind cluster. It checks that:
    - migrations ran and the API uses non-owner roles;
    - sign-in goes through Keycloak, and the API makes a user's Keycloak account;
    - a protected field is written and read back through OpenBao;
    - a document is stored in and read from the object store;
    - OpenBao and Keycloak keep their keys and accounts across a restart;
    - the console serves each app.
  - Last run: success on `d488b32`, 6 November.
  - By its own statement it proves nothing about any particular provider, load or failover.
- **Only the cloud adapter project uses a cloud SDK** (architecture rule). Tests: `CloudSdkRuleTests.OnlyTheCloudAdapterProject_ReferencesACloudSdk`, `CloudSdkRuleTests.OnlyTheCloudAdapterProject_UsesACloudSdksNamespace`.
- **Payment simulator refused outside Development and Testing.** **BUILT.** Test: `SimulatorGuardTests.SettingATenantsProviderToTheSimulator_IsRefused_WithItsCode`. Real payments remain a stand-in (row 13).
- **The INSYS Preview** (`docs/plans/insys-preview.md`, `docs/preview/`, `deploy/preview/`, `.github/workflows/preview.yml`): one Hetzner CPX42 server in Falkenstein (fsn1), running k3s and the same chart, with sample data only (tenant `OFTZA`). **Not production.** Verified from the `preview` workflow's own runs:
  - upgraded on `d488b32` on 6 November (run 42190162209): chart revision 5, migrations ran, a document stored and read back with the API's keys;
  - Let's Encrypt production certificates on both hosts (`insys.project-demo.app`, `id.insys.project-demo.app`, issuer CN YR1);
  - 51 sample accounts created on 5 November (run 42104013738), and a tenant administrator has signed in;
  - Keycloak's own sign-in emails (invitations) go out for real through Google Workspace's SMTP relay from insys-preview@openfactorgroup.com; port 587 and STARTTLS verified from the server. No identity email goes to reserved test domains (`ReservedEmailAddresses`, test `KeycloakProvisioningTests.AnInvitationToAReservedTestAddress_IsNotSent_AndNothingParks_WhileAnotherAddressIsSentAsBefore`). Every other outside service (payments, INSYS's own email and SMS senders, screening, GL, and so on) is the stand-in;
  - the restore rehearsal's `backup` and `restore` actions are on main; the rehearsal itself has not been run on the Preview yet.
- **Still needed:**
  - ADR-027 runbooks, including dual control of the database owner's credentials (row 1, blocking).
  - Production hosting and set-up: provider, production Keycloak with MFA, keys, monitoring (row 2, blocking). `docs/production-readiness.md` still assumes Huawei.
  - Load, penetration, backup and recovery tests (row 3, blocking).
  - Production Keycloak user-admin secret (row 36) and object-store keys (row 37).
  - Hosting for Nigerian tenants is undecided (row 5, D-13).

### Performance and resilience (measured figures only)
- **No load test has been run** (ADR-020; register row 3: load, penetration, backup and recovery tests are still to do, blocking). The capacity requirement NFR-015 is unmet. Scaling past one instance waits on that result (row 4).
- **Latency smoke tests with budgets** (`tests/Insys.Testing/Latency.cs`): calls made one at a time and 8 at a time, against Testcontainers PostgreSQL on the CI runner. They assert:
  - premium calculation p95 under 3 s (NFR-003): `RatingPerformanceSmokeTests.PremiumCalculation_StaysWithinTheRatingLatencyTarget_NFR003`;
  - payment callback and allocation p95 under 2 s (NFR-002): `MoneyFlowTests.PaymentAllocation_StaysWithinTheStandardLatencyTarget_NFR002`;
  - Health adjudication against benefit accumulators p95 under 2 s: `HealthProcessTests.Adjudication_AgainstTheBenefitAccumulators_StaysWithinTheStandardLatencyTarget_NFR002`;
  - requests during warm-up, with the CPU saturated, answered within 5 s: `WarmupTests.RequestsDuringWarmup_UnderSaturatedCpu_AreAnsweredQuickly_OkOr503`;
  - the first request after warm-up within 1 s: `WarmupTests.FirstRequestAfterWarmup_FindsTheRouterBuilt`.
  - They pass on every green CI run (last: `d488b32`). The actual p50 and p95 figures go only to test output or `INSYS_LATENCY_LOG`; none are recorded in `docs/`. `docs/progress.md` says plainly: "These are not load tests."
  - The NFR targets themselves are "proposed starting target, not a contractual commitment".
- **Other measured figures (from docs, not verified):** a full sample-data seed took 96 minutes locally with development tokens, and about 122 minutes with Keycloak sign-in (handover, 5 November). CI shard times are in `docs/reports` (e.g. 22, 14 and 9 minutes, 27 October). Neither is a product performance figure.
- **Resilience that is built and tested:** retries then dead letters, parked deliveries, the health check for outbox lag, the single-instance guard (sections 2 and 7). The backup and restore rehearsal is local only (handover, 5 November); there is no recovery test in production terms (row 3).

### Architecture tests (`tests/Insys.ArchitectureTests`)
- `DependencyRuleTests`:
  - Domain references only BuildingBlocks.Domain, with no persistence or web frameworks.
  - Application references its own Domain and only other modules' Contracts.
  - Contracts reference only BuildingBlocks.Contracts.
  - No module references another module's Domain, Application or Infrastructure.
  - Building blocks reference no module.
  - Aggregates have no public setters.
- `DbContextSchemaTests`:
  - 24 DbContexts, one per module.
  - Owned schemas match the module roles, and no schema has two owners (DBA-004).
  - Each context maps only its own schema (DBA-004) and its module's outbox (DBA-006).
  - Tenant-scoped entities have the tenant filter (DBA-001).
  - `row_version` is a concurrency token (DBA-011).
- `ConcurrencyContractTests`:
  - every change takes If-Match, and every create takes an Idempotency-Key (BR-031);
  - the record a change acts on is read with its ETag;
  - every If-Match operation has a read that gives its version (BR-116).
- `PermissionLiteralRuleTests`: no permission check uses a string literal (ADR-026).
- `QueryAuditRuleTests`: no query handler writes an audit entry; a read that must be audited is a command (HLT-008).
- `BusinessDateRuleTests`: no production code takes a business date from the UTC clock (BR-040, ADR-025).
- `TimeSourceTests`: application SQL never reads the database's clock; SQL files read it only where reviewed (V0093).
- `CloudSdkRuleTests`: only the cloud adapter project references or uses a cloud SDK (ADR-027).
- `DeadLetterErrorRuleTests`: no dead letter carries an exception's message (BR-017).
- `EventMapperRuleTests`:
  - every event mapper is registered;
  - every event a consumer waits for is built somewhere;
  - every domain event named like a published event has a mapping.
- `IdNumberRuleTests`: no business number is taken from the start of a UUIDv7 ID (BR-118).
- `StateEnumTests`: state enums hold the domain model's states (GAP-008); every aggregate with states is mapped, or shows no state in the API.
- `StaticDataRequiredFieldTests`: the fields the API requires are the catalogue's mandatory fields, and the OpenAPI input lists exactly those as required (BR-084, BR-085).
- `SchemaNameTests`: every module type has its own OpenAPI schema ID, named with its module (GAP-031).
- `TextCursorCollationTests`: a text-key paging cursor is compared bytewise in the database (BR-087).
- `SourceHeaderTests`: every in-scope source file has the proprietary header with the right component.
- There is no architecture test for float or double in money. That check is the unit test `MoneyTests.Money_HasNoFloatingPointMembers_DBA012`.

### Notes for the writer
- **Segregation of duties:** register row 34 says segregation is checked "per role today, not across a user's roles". The code does check across roles, but only for function pairs the tenant has configured. The three built-in pairs are enforced only through the templates. Inferred: if a tenant configures no conflict rules, nothing stops an administrator giving one user two templates that together break a built-in pair. Worth confirming with the Identity owner.
- **ADR status lines:** ADR-011 still says "Proposed", and ADR-016 and ADR-018 still say "Accepted" in their own status lines; ADR-027 is the one that records the supersession.
- **No measured latency numbers** are written down anywhere in `docs/`. Only "within budget on CI" can be claimed.

## 4. Regulatory and market fit

- **South African content today:**
  - **POPIA:** data subject requests, anonymisation, retention, consent (s69), the s23 access export (section 4).
  - **FICA:** a politically exposed or high-risk customer needs enhanced due diligence, cleared by "senior management" (s21F) through the privileged function `PARTY_APPROVE_ENHANCED_DD`. A true sanctions match blocks payment until a second person lifts the block (s26B, `PARTY_UNBLOCK_DUE_DILIGENCE`). **BUILT, STAND-IN** (screening provider stub, row 11). Tests: `DueDiligenceTests.APep_RaisesTheLevelToEnhanced_AndClearanceNeedsAnApproval_TheMakerCannotGive_OQ126`, `DueDiligenceTests.ATrueSanctionsMatch_BlocksTheParty_NeedsAReport_OpensAComplianceCase_AndIsUnblockedOnlyByASecondPerson_F003`. The search-log retention is based on the FICA s23 five-year minimum (OQ-073).
  - **PPR (insurance policyholder protection rules):**
    - a claim decision states the claimant's rights, PPR 17.6.3 (`LifeClaim.cs`);
    - case progress notices, PPR 18.9;
    - search-log retention is also based on PPR rule 16.4(b).
    - Life cancellation in the cooling-off period is not built (row 32, blocking; PPR rules for the business to confirm).
  - **Medical Schemes Act:**
    - appeal routes `CMS` (Council for Medical Schemes) and the PPR ombud route are global static data (V0126). Test: `CommunicationsTests.AClaimDecisionRightsNotice_NamesTheAppealRouteForTheTenantsType_OQ101_OQ102`.
    - a tenant flag "is a medical scheme" sets the default for offsetting provider recoveries (s59(3), V0106, for compliance to confirm).
  - **SAM / solvency:** no code found. Solvency and capital extracts are **DEFERRED** (F063, B-002, row 15, Ade to decide).
  - **ZAR:** appears as the sample currency in static data and in the AI Assist number checker. Inferred: currency is configured per tenant, not hard-coded to ZAR.
  - **SA ID numbers:**
    - identifiers are checked against a pattern the tenant configures (`IdentifierFormatTests.TheWholeNumberHasToMatchThePattern`, which uses a 13-digit example);
    - `party.ref_identity_document_types.check_digit_rule` (Luhn, Mod11) is stored. Inferred: it is not applied, as it appears only in generated code;
    - **PARTLY BUILT:** no South African ID check-digit or date-of-birth validation was found;
    - Luhn is used only for INSYS's own party number (`PartyNumberTests.TheCheckDigit_IsLuhnMod10_BR029`).
  - **Time zone:** `Africa/Johannesburg` is the default (section 5).
  - **Retention:** a 7-year `ZA` default (V0064, V0114).
- **Nigeria: DEFERRED** (Ade, 2 November):
  - Row 24 (retention defaults, default time zone, age of majority): descoped until a first Nigerian customer is chosen, and not started. Only the appeal-route static data was built (V0126), which serves every market; the Nigerian route itself waits on row 25.
  - Row 25: the "no premium, no cover" review and the partner sign-off.
  - Row 5: hosting for Nigerian tenants (D-13).
  - No NAICOM or NDPA content was found in `src` or the migrations.
- **Other markets, each only if the first customer is there:**
  - row 26, rounding by currency (Rwanda, Senegal, Côte d'Ivoire);
  - row 27, limits with a currency and two-currency journeys (DRC);
  - row 28, French wording (Senegal, DRC, Côte d'Ivoire, partly Rwanda).
  - Launch is in English only.

## 5. What's not in the product yet: the go-live register's blocking items

From the go-live register in `docs/handover-backend.md` at `d488b32`. "Blocks: Yes" rows block any first customer; "If" rows block only in the stated case. Sizes are the register's, mostly estimates. This inventory hasn't re-sized them.

**Blocking for any first customer (Yes):**

| # | In business words |
|---|---|
| 1 | Production deployment runbooks, including dual control of the database owner's credentials. The chart, images and deployment job exist (ADR-027) |
| 2 | A production host and its set-up: the provider, production sign-in with MFA, key management and monitoring |
| 3 | Load, penetration, backup and recovery tests by an outside tester (ADR-020). None has been run |
| 6 | A real email provider for INSYS's own notices (today a stand-in) |
| 7 | A real SMS provider (today a stand-in) |
| 10 | A link to the customer's general ledger (today a stand-in) |
| 11 | Real screening (sanctions, PEP) and identity verification providers (today stand-ins) |
| 13 | A real payment and collection provider, with debit order mandates. Nothing sends or collects money today: every collection and payout goes to a simulator, which is refused outside development and test |
| 20 | Answers from compliance and finance: POPIA access export depth, retention periods and final statuses, VAT inside premium per product, and other open questions |
| 21 | A business review of the wording of functions and menus (OQ-100) |
| 22 | An actuary's sign-off of rating step order, cap rule and parameters (OQ-052). Life cash values stay empty until the basis is set |
| 23 | Each customer's set-up: tenant configuration, approval roles, the administrator's permissions, and data migration reconciled with the source system |
| 31 | Revoking an invitation sent to the wrong person, or to someone who leaves before signing in |
| 32 | Cancelling a Life policy, in the cooling-off period with a premium refund, and at the policyholder's request |
| 33 | Rules evaluation taking a case's facts from the proposal itself, not from the caller |
| 35 | The production sign-in page's name |
| 36 | Production configuration of user provisioning in the sign-in service (address and client secret) |
| 37 | Production object-store keys, the same ones the API holds, checked after each deployment |

**Blocking only if (If):**

| # | Blocks if |
|---|---|
| 4 | Running more than one API instance, if the load test shows one isn't enough (ADR-006) |
| 5, 24, 25 | Hosting for Nigerian tenants, Nigerian retention, time zone and age of majority, the "no premium, no cover" review: only if the first customer is Nigerian (descoped, 2 November) |
| 12 | Electronic reinsurer and regulator submissions, if they're needed. Today these are stand-ins |
| 14 | A pharmacy claims switch, for a Health customer with pharmacy claims (B-012). Ade: needed before go-live. No interface exists in code yet |
| 15 | Solvency and capital (SAM) extracts, if the customer expects them from INSYS (F063, B-002). Ade to decide |
| 16 | Premium financing, if the customer offers it (F079, B-003) |
| 26, 27, 28 | Rounding by currency, limits with a currency and two-currency journeys, and French wording, for Rwanda, Senegal, Côte d'Ivoire or DRC |

**Not blocking:** push (#8) and WhatsApp (#9) senders; wellness, treatment compliance and outcomes (#17, F089-F091); code-field allowances in the contract (#18); service accounts made through the sign-in service's admin API (#19); a fourth segregation rule (#34); the sample tenant for demos (#30, done on the Preview); and AI Assist (#29, Wave 7, paused, ships switched off).

**Found by this inventory, not in the register** (for Ade to decide whether they belong):
- **Documents** render as plain text and are delivered through a stub (`PlainTextDocumentRenderer`, `StubDocumentDelivery`, OQ-056). No register row covers them.
- **No role template holds the General renewals sub-domain** (`GENERAL_RENEWALS`: deciding renewals, non-renewal, retention).
- **No default segregation rules for a new tenant:** see "Corrections" below.
- **South African ID numbers** are checked against a tenant-set pattern only. No check digit or date of birth is validated.

## Corrections to existing documents

This inventory checked these points against the code. They differ from what a document says.

- **Go-live register row 34 ("INSYS checks segregation per role today, not across a user's roles"): partly wrong.**
  - `UserAccount.AssignRole` does check across roles (`src/Modules/Identity/Insys.Identity.Domain/Users/UserAccount.cs`, called from `Users.cs` line 315). It checks the new role's functions against the functions of every role the user holds today, using the tenant's BLOCK rules in `iam.cfg_segregation_of_duties_conflict_rules`. Tests: `UserAccountTests` and `IdentityTests` (`SEGREGATION_OF_DUTIES`).
  - What is not checked:
    - The three built-in pairs (`RoleTemplates.SodRules`) are checked only against each template in tests. They are not loaded as tenant rules, so a tenant with no rules configured gets no block.
    - Assignments still pending approval or dated in the future aren't counted as held. This was reported by the research pass and seen in the code: `EffectiveAssignments(today)`.
    - Inferred: functions added to a role after assignment aren't re-checked.
  - So the gap is real, but it differs from the row's wording: the check exists, while the rules it checks against are empty by default.
- **Claim payment maker-checker.** The second-person control is at the approval: Workflow's `Approval` refuses a checker who is the maker (`MakerCheckerTests`). The release step (`ClaimPaymentInstruction.ReleaseInstruction`) doesn't compare the releaser with the drafter. Separation there relies on the templates (`CLAIMS_HANDLER` drafts, `CLAIMS_MANAGER` releases) and on the segregation rules above.
- **ADR status lines.** ADR-011 (posted financial records) still says "Proposed", yet its rule is enforced in the database and tested. ADR-016 (Huawei Cloud) and ADR-018 (DEW KMS) still say "Accepted", though ADR-027 supersedes them.
- **`docs/production-readiness.md`** still describes Huawei Cloud; it carries a banner saying it is superseded (register rows 1 and 2).
- **Configuration tables with no reader.** Several `cfg_` tables appear unread outside generated code, by grep only. Examples: `general.cfg_settlement_authority_limits` (Workflow's authority limits are used instead), `finance.cfg_reconciliation_rules` and `product.cfg_product_eligibility_criteria`. The module sections name them. A tenant setting these changes nothing today.
