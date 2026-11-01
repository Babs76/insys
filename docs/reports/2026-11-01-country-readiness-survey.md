# Country readiness survey, 1 November 2026

Read from the code on main at `bae3303`. Nothing was changed, and nothing here was run for these currencies. File references are to `src/` unless they say otherwise.

## In short
**Nigeria's blockers are small.** NGN has two decimals, so the rounding problem below doesn't touch it. What blocks Nigeria is the appeal route list (§3), plus three smaller items.

**Rwanda, second on the list, needs the rounding work first,** as do Senegal and Côte d'Ivoire. Roughly 40 places round money to two decimals regardless of currency, so a Rwandan instalment could come out as 3,333.33 RWF, an amount no bank can pay.

| Need | Nigeria | Rwanda | Ghana | Senegal | Kenya | DRC | Côte d'Ivoire |
|---|---|---|---|---|---|---|---|
| Appeal routes configurable (§3) | **Yes** | **Yes** | **Yes** | **Yes** | **Yes** | **Yes** | **Yes** |
| Rounding by currency (§1) | No | **Yes** | No | **Yes** | No | No | **Yes** |
| Limits with a currency (§2) | No | No | No | No | No | **Yes** | No |
| French wording (§4) | No | Partly | No | **Yes** | No | **Yes** | **Yes** |

## 1. Currencies with no minor unit (RWF, XOF)
**The data exists but is ignored.** `refdata.ref_currencies.minor_units` is stored and validated, and no business code reads it. `RoundingRule` in `Money.cs` says callers mustn't hard-code decimals, and has a TODO saying the source is still to be fixed.

| Area | What happens with RWF or XOF | Where |
|---|---|---|
| Premiums | **Right, if configured:** `cfg_rounding_and_currency_precision_rules` is per currency, with a step | `Rating.Domain/Calculations/RatingConfiguration.cs:89`, `RatingEngine.cs:83` |
| Premium tax in rating | **Right:** uses the tax's rounding rule, or the final premium's | `RatingEngine.cs:167` |
| Instalments | **Wrong:** 2 decimals | `Billing.Application/GeneralPolicyBilling.cs:228,232`; `Billing.Domain/Billing/BillingAccount.cs:221`; `General.Domain/Underwriting/Quotes.cs:324,332` |
| Tax on invoices | **Wrong:** 2 decimals | `Billing.Domain/Billing/Invoice.cs:96,265` |
| Pro rata, refunds, endorsements, renewals | **Wrong:** 2 decimals | `General.Domain/Policies/GeneralPolicy.cs:580,612,623,927`; `GeneralEndorsement.cs:217,220,327`; `Renewals/Renewal.cs:395,711` |
| Commission | **Wrong:** 2 decimals | `Distribution.Domain/Commission/CommissionEntitlement.cs:354`; `Distribution.Application/Commission.cs:527` |
| Claims, Health, Life, reinsurance | **Wrong:** 2 decimals | `General.Domain/Claims/GeneralClaim.cs:140`; `Health.Domain/Adjudication/AdjudicationEngine.cs:835`; `Health.Domain/Members/Membership.cs:93`; `Life.Domain/Claims/LifeClaim.cs:382`; `Groups/GroupScheme.cs:101`; `Proposals/LifeProposal.cs:455`; `Reinsurance.Domain/Cessions.cs:122` |
| Amounts typed or imported | **Wrong:** 100.50 RWF is accepted, because the check is for at most 2 decimals | `Payments.Domain/Payments/Payment.cs:281`, `Billing/Invoice.cs:423`, `ClaimsFramework.Domain/Claims/ClaimPaymentInstruction.cs:193`, `ClaimFinancials.cs:136`, `General.Domain/Claims/Assessments.cs:329`, `Reinsurance.Domain/Recoveries.cs:148`, `Workflow.Domain/Cases/CaseRedress.cs:129`, the migration imports |
| Ledger | **Balances, with fractional amounts:** journal lines take what they're given, and the base amount rounds to 2 | `Finance.Domain/Ledger/Journal.cs:114,196` |
| Display in notices and documents | **Wrong:** always `0.00`, so `3050.00 RWF` | `Communications.Application/Triggers/TriggerFormats.cs:48`; `General.Infrastructure/Persistence/GeneralStores.cs:510,606`; `Health...HealthStores.cs:665`; `Life...LifeStores.cs:252` |
| Database | **Fine:** `numeric(18,2)` holds whole numbers | 164 columns in `db/migrations` |
| Screens | The API sends numbers; the front end has to format them by the currency's `minorUnits`, from static data | Front end to confirm |

**A knock-on effect:** the last instalment "takes the rounding", so it absorbs differences only at the precision used. Fixing this means all of these rounding the same way.

**Proposed fix, about 5 to 8 days:** one service giving a currency's rounding (from `minor_units`, with the method from configuration), used at every place above; refusal of amounts finer than the currency allows; a guard test against new `Math.Round(..., 2, ...)` on money; notices formatted by currency; a whole end-to-end journey in RWF. Must land before Rwanda. **Existing amounts in other currencies keep their values.**

## 2. One tenant using two currencies (DRC: CDF and USD)
**Mostly designed for it.** Every money record has its own currency. `Money` refuses to add across currencies (`CURRENCY_MISMATCH`).

**What works, by reading the code:**
- Billing accounts are one per payer and currency.
- A commission statement and a payout batch each hold one currency (`CommissionStatement.cs:136`).
- A claim's bank account must match the policy's currency (`ClaimHandling.cs:369`, `Life.Application/Claims.cs:276`).
- Journals convert to the tenant's one base currency at the spot rate, or refuse with `EXCHANGE_RATE_MISSING` (`Finance.Application/Ledger.cs:256`).
- Approval limits count only in the approval's currency (`Workflow.Domain/Approvals/Approval.cs`, OQ-112).
- Dashboards group by currency (`Analytics...ProjectionStore.cs:202,254`).

**What doesn't work:**
1. **Limits and amounts in configuration have no currency, on 25 of 29 tables with a Money column.** `general.cfg_settlement_authority_limits`, `life.cfg_claims_authority_limits` and `rules.cfg_authority_matrices` are the serious ones. A limit of 1,000,000 means about the same in CDF as a limit in USD does to the code, while USD is worth thousands of CDF, so **someone could approve far above what the tenant intended.** The other tables are: `billing.cfg_allocation_rules`, `finance.cfg_reconciliation_rules`, `general.cfg_aggregate_limit_definitions`, `general.cfg_cancellation_reasons_and_refund_rules`, `general.cfg_extension_and_condition_library`, `general.mst_repairer_and_assessor_panels`, `health.cfg_benefit_schedules`, `health.cfg_co_payment_and_deductible_rules`, `health.cfg_contribution_rules`, `health.cfg_high_cost_thresholds`, `health.cfg_limit_definitions`, `health.cfg_preauthorisation_required_lists`, `health.cfg_tariff_schedules`, `health.ref_medicine_formulary`, `life.cfg_financial_underwriting_limits`, `life.cfg_free_cover_limits`, `life.cfg_withdrawal_limits_and_charges`, `payment.cfg_payment_release_limits`, `payment.cfg_reconciliation_matching_rules`, `rating.cfg_rating_test_case_library`, `reinsurance.cfg_treaty_definitions`, `rules.cfg_acceptance_and_referral_rules`.
2. **A fixed fee has no currency either:** `rating.cfg_premium_parameters` with unit `AMOUNT` (a policy fee of 50). The currency of the quote decides what 50 means.
3. **A payer's new policy in another currency on a named account fails late:** a policy naming a billing account in the wrong currency throws in the event handler (`GeneralPolicyBilling.cs:101`, also Life and Health), so it's dead-lettered, not refused up front. It needs a check when the quote is made.
4. **No exchange gain or loss:** there's no revaluation or realised-difference posting in Finance. For a tenant holding USD, the finance team will ask for it.
5. **Not tested:** nothing runs a whole journey in two currencies.

**Proposed fix, about 6 to 9 days, before DRC:** a currency on the authority tables first, and on the others as each is reviewed; the early check; revaluation is a separate decision for Ade. Other markets may also take USD policies, which brings this forward.

## 3. Rules written for South Africa
**Most of it is configuration already, and "not configurable" is the exception.** Words like FICA, POPIA and PPR mostly appear in comments and API descriptions, not in logic.

| Rule | Where | Configurable per jurisdiction? |
|---|---|---|
| **Appeal routes** `PPR_NFO`, `CMS` | `workflow.cfg_case_types.appeal_route` allows only those (`CaseTypes.g.cs:63`, catalogue additions); chosen in `Workflow.Infrastructure/Persistence/CaseStores.cs:358` | **No. Blocks every target market.** Needs a list of routes in static data, and the choice in `CaseStores.cs` by route and line |
| **Rights notice wording** "You may ask for an internal review…" | `Workflow.Application/CasePortal.cs:120` | **No.** English text in code (§4) |
| **Retention defaults** | Platform rows labelled `ZA`, 7 years (`V0064`, `V0114`), which apply to every tenant without its own; `jurisdiction` isn't used to choose | **Partly.** A tenant's own row wins, but a tenant without one silently gets the ZA value. The pack must always supply them |
| **Default time zone** `Africa/Johannesburg` | `BuildingBlocks.Application/Context/BusinessDate.cs:64`, `Workflow...WorkflowStores.cs:392` | **Partly.** Per tenant; used when a tenant has none. Should default to the deployment's, or be refused |
| **Age of majority, 18** | `Health.Infrastructure/Persistence/NoticeRecipients.cs:65` | **No.** Decides who receives a Health notice. To check per country |
| **Complaints:** second decider, reasons required (PPR 18.6, 18.5, 18.9) | `Workflow.Domain/Cases/ServiceCase.cs:389,530,582,592` | **Logic is general good practice; keep it.** Timelines are configurable. Review type is required by readiness (`CaseJobs.cs:202`) |
| **Complaints report** "PPR 18.8.3" | `Workflow.Api/CaseEndpoints.cs:148` | **Shape is South African;** each regulator's return needs comparing |
| **FIC report reference** s28A, s29 | `Party.Domain/Parties/DueDiligence.cs:382`, `Party.Api/PartyModule.cs:607`; command `RecordFicReport` | **Works as a reference to any authority, but is named for South Africa.** Rename in the contract is a breaking change to weigh; labels first |
| **Enhanced approval, beneficial owners** (FICA s21F, s21B) | `Party.Contracts/PartyContracts.cs:58`, `PartyModule.cs:626` | **General AML concepts,** with gates in `party.cfg_due_diligence_settings`. Only wording is South African |
| **Marketing template must name sender and opt-out** (POPIA s69(4)) | `Communications.Infrastructure/Persistence/StaticDataValidators.cs:90,109` | **Not configurable,** though harmless: a stricter rule everywhere. Message text says POPIA |
| **Medical schemes s59(3)** | No rule in the code | **Absent.** Nothing to change; if a market needs one it's new work |
| **Prescribed minimum benefits** | No rule in the code | **Absent** |
| **Identity document types** | `party.ref_identity_document_types`, by country, with pattern and check digit | **Yes** |
| **Public holidays** | `refdata.ref_public_holiday_calendars`, by country; a calendar names one | **Yes** |
| **Premium tax and levies** | `rating.cfg_premium_tax_and_levy_rates`, `finance.cfg_tax_codes_on_financial_transactions`, by jurisdiction | **Yes.** No VAT in code |
| **Disclosure texts** | `channel.cfg_disclosure_and_notice_texts`, by jurisdiction and language | **Yes;** the gate reads the party's country (decided 1 November) |
| **Consent purposes** | `party.cfg_consent_purposes`, by jurisdiction | **Yes** |
| **Bank schemes** (DebiCheck, NAEDO and so on) | Not in the code; collection methods are configuration | **Yes,** with real provider adapters still to build for each market |

**Separately:** the party number uses a Luhn check digit. That is INSYS's own numbering, and isn't a South African rule.

**To check against Nigerian rules, not found in this survey:** "no premium, no cover". INSYS allows instalments, grace and cover while premium is in arrears. Whether a Nigerian General product can be set up to require payment before cover needs a domain review: **it could be more than configuration.**

**Fix, about 3 to 4 days, before Nigeria:** appeal routes as static data, with the preference by line; retention refused when a tenant has none, or at least flagged by readiness; the time zone default; the age of majority as configuration.

## 4. Localisation: what supports French
**The machinery is there, and no French text is.** `docs/specs/addenda/localisation.md`, built and proposed under OQ-068.

| What | State |
|---|---|
| A tenant's languages and default | **Works:** `tenant.cfg_locale_defaults` |
| Request language | **Works:** `Accept-Language`, within the tenant's languages, then default, then English (`BuildingBlocks.Api/Security.cs:386`) |
| Static data labels | **Works:** `labels` per language |
| Error messages | **Mechanism works, no French text:** titles and details from `channel.ref_ui_label_and_translation_bundles` by `message_key`; a detail with no text stays in English and says so (`detailLanguage`) |
| Documents | **Works by template:** party's language, then tenant default, then English (`Documents.Application/DocumentLanguages.cs`); no template in the chain fails, then retries, then dead-letters |
| Notifications | **Works by template:** same chain (`Communications.Application/Queueing.cs:48`) |
| Disclosure texts | **Works:** by jurisdiction and language, **with no fallback.** The caller names the language, and a missing French text finds nothing. Needs testing with a French party |

**What stands in the way of French:**
1. **Text in code:** the internal review sentence in `CasePortal.cs:120`, and `{reviewType}` and duration shown raw (`P42D`, a code, in a customer's notice).
2. **One language per row:** `customer_wording` on claim decision reasons, non-renewal reasons and similar, and an ombud scheme's referral details, have no language. A French customer gets whatever was entered.
3. **Merged values are not localised:** `TriggerFormats` writes dates as `2026-11-01` and amounts as `1234.50`, deliberately the same in every locale. French readers expect `1 234,50`. It needs a formatting step for the party's language; the merge values could stay as they are for machines.
4. **Function labels** (`FunctionCatalogueSeed.cs`, just merged as BR-009) are English; the taxonomy's comment says other languages go in a `labels` column.
5. **`fr` needs loading:** in `refdata.ref_languages`, and about 500 message keys translated. Not found in any migration. Rwandan (`rw`) and Swahili work the same way.

**Estimate, about 5 to 7 days of code,** plus translation, which needs a person who knows insurance French. Legal texts need a local lawyer.

## 5. Order I'd build in
1. **Before Nigeria:** appeal routes and the §3 fixes, then a domain review of "no premium, no cover".
2. **Before Rwanda:** rounding (§1), then §4's text-in-code and formatting, since Rwanda also uses French in part.
3. **Before Senegal:** CIMA overlay, French.
4. **Before DRC:** two currencies (§2), though the authority limit fix may be worth doing earlier, because it costs little and fails badly.

Estimates are one engineer, with moderate confidence, from reading the code rather than building. None of this is scheduled.
