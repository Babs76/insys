# Wave 7 addendum: AI Assist

**Status: for Ade's sign-off. Nothing here is built.** Written from [the AI Assist SRS of 1 November 2026](../../srs/INSYS_AI_Assist_SRS.md), which supersedes the copies of 28 and 30 October, and checked against the code on main. Ships off, per tenant, after Wave 6 (D-6).

## 1. What it is
Three features. In all three the AI prepares and a person decides; it never approves, publishes, pays or refuses, and INSYS works unchanged without it.
- **AI-2, plain-language explanations:** why INSYS refused a request, and how a recorded premium was calculated.
- **AI-1, Product Setup Assistant:** drafts a Draft product version from product documents, with a citation for every value.
- **AI-3, tenant configuration wizard:** drafts a new tenant's setup from reviewed starter packs and the tenant's answers, applied only after a second person approves.

The SRS's principles P1 to P8 and its requirements AIA-001 to AIA-091 stand as written, except where §4 says otherwise.

## 2. Decisions
| ID | Decision | State |
|---|---|---|
| D-1 | Provider | **Decided, 31 Oct:** Claude, through the Claude API or the deployment's cloud, behind the adapter |
| D-2 | First feature | **Decided, 30 Oct:** AI-2 for staff first |
| D-3 | Portal explanations | **Decided, 30 Oct:** a later release, after staff feedback and legal review |
| D-4 | Languages | **Decided, 30 Oct:** English first |
| D-6 | Timing | **Decided, 30 Oct:** back-end Wave 7 after Wave 6; front-end screens before Phase 13; shipped off |
| D-9 | Starter pack order | **Decided, 1 Nov:** Nigeria, Rwanda, Ghana, Senegal, Kenya, DRC, Côte d'Ivoire. Senegal and Côte d'Ivoire share a CIMA overlay with a thin national layer. South Africa stays as a reference overlay |
| D-5 | Commercial model | **Open.** Metering (AIA-073) supports any answer. Needed before any customer is charged |
| D-7 | Line for AI-1's evaluation set | **Open.** Needed before batch 7.5 |
| D-8 | AI-3's build order | **Open.** The SRS recommends AI-2, AI-1, then AI-3. §6 is drawn that way and can move. The pack-only path (AIA-090) can ship earlier |
| D-10 | Who approves each country overlay | **Open.** The SRS recommends a local compliance partner per country. **No overlay can be approved until this is answered,** so AI-3 can't reach a live tenant, even pack-only |

**What D-1 means for AI-2:** Claude runs globally or in the US, not in South Africa. AI-2 can be built first, but waits to be switched on until compliance confirms the POPIA s72 basis and the contract sets zero retention and no training (AIA-052). The route is a per-deployment setting of the adapter.

## 3. Refusal explanations (AIA-039)
**As proposed in the earlier draft, and now what the SRS says.** The console sends the refusal it received: code, field errors, record reference and correlation ID. INSYS rebuilds the context from the error catalogue and from the record as that user may read it (AIA-003), and never trusts record data sent by the screen. The correlation ID is kept for audit and isn't used for lookup. No new store of refusals, and nothing added to the request pipeline.

The cache key (AIA-036) is the code, the field errors, the record's reference and row version, and the prompt version. The catalogue's "remedy" wording (AIA-031) needs checking per code; gaps are filled in batch 7.2.

## 4. Where the SRS and the code differ
Read against main. The first three change the build or its size, and are for Ade to decide.

### For decision
1. **AIA-086: one change set doesn't exist yet.** BR-016 approves one import of **one table,** at that table's tier, bound to its SHA-256. A tenant's setup spans up to 295 tables (`cfg_`, `ref_`, `mst_`; the SRS says about 280), several tiers and several modules' databases. *Proposal:* a new `SETUP` change set grouping one import batch per table, with one approval bound to a hash of all of them, activated in dependency order, and resumable, because no single transaction spans the modules. It reuses the import path per table. New approval type, and the highest-risk part of AI-3.
2. **AIA-085: readiness can't check a draft "before anything is saved".** The checks read in-force rows directly from each module (10 check classes; `ReadinessSql` is used in 27 files). *Proposal:* load the draft into a temporary shadow tenant, run the unchanged checks, then purge it. That gives "the same checks as live onboarding" with no rewrite. The alternative, rewriting every check to read a draft, is larger and could drift. Purge and isolation need a security review.
3. **AIA-050 and §6.1 are written for South Africa, and the first market is Nigeria.** The setting is "in South Africa / outside South Africa", the legal basis is POPIA s72, and SRS §6 assumes INSYS is hosted in South Africa. A Nigerian tenant answers to Nigerian data protection law, with its own transfer rules, and may need hosting elsewhere. *Proposal:* the setting records the tenant's jurisdiction, where inference runs and the basis, and AI-2 refuses when inference is outside that jurisdiction with no basis recorded. The basis for each country goes through compliance with its overlay. **Where Nigerian tenants are hosted is a question for Ade.**

### For the SRS's next edit
4. **§1's out-of-scope line** says the AI writes to nothing but a Draft product version. AI-3 writes a draft change set; the line should say so. AIA-021 and AIA-060 follow.
5. **§3 and §7.1 say four functions.** AIA-080 makes it five, with `ASSIST_TENANT_SETUP_RUN`. AIA-001 should cover AI-3's AI steps.
6. **§9's estimate covers AI-2 and AI-1 only,** at "about 2 to 4 days". See §6.
7. **§8 and §9 have diagrams missing from the Markdown,** including the six phases and five gates of the roadmap. §7 below uses the text only. Please check I'm not missing a gate.

### Handled in this addendum
8. **AIA-010, "scanned before use":** INSYS has no malware scanning. **Question for Ade:** build a scanner port (ClamAV, say), which would serve Documents generally, or accept uploads from tenant staff without one at first. AI-1 and AI-3's document mapping both depend on it. *Recommendation: build the port.*
9. **AIA-017 and §9.2's "maker-checker needs no new code":** the AI-1 job runs in the background, and jobs run as a service identity (`ExecutionScope.InitialiseForService`). To make the user the maker, the job acts for the recorded user, with permissions checked at the start and on writing. This is a new pattern and **needs a security review,** so it is some new code.
10. **AIA-080, the operator's onboarding team:** ADR-024 refuses business routes to the operator tenant. *Proposal:* the tenant administrator runs the wizard first. An operator path follows as its own platform route, audited, if onboarding needs it.
11. **Global rows:** currencies, countries, languages and holidays are global tables, so a tenant's wizard can't write them. A country overlay's global rows load once, by the platform, when the overlay is approved.
12. **AIA-011:** PDF, DOCX and XLSX extraction needs new packages, OSI licences only, checked at build.
13. **"Base environment settings" (§5A):** only static data can go through a change set. I've read these as the settings held in static data (`tenant.cfg_locale_defaults`, for example). Deployment configuration stays out.
14. **The premium's "recorded steps":** these are `CalculationView.Inputs` and `Lines` (`GET /rating/calculations/{id}`). No change needed.

## 5. What gets built
**Module `Assist`:** own schema and role, forced row-level security, tenant from the token (AIA-062). Tables as SRS §7.2 and §5A.5.

**Adapter:** `IAiModelProvider`, with a stub for tests and a Claude implementation in the Cloud adapter project, so ADR-027's SDK guard holds. Its SDK joins the guard's list. Check the package's licence at build. Model and prompt versions are configuration (AIA-074).

**Deterministic checks, which are what makes this safe:**
- **Minimiser (AIA-051):** removes names, identifiers, contact and bank details, and policy, claim and member numbers. The payload actually sent is stored.
- **Verifier:** numbers (§5.1), citations resolving to real text (AIA-013, AIA-083), rate tables cell for cell (AIA-015), and for AI-3, each value traced to a pack or a cited tenant input (AIA-087).

**API and error codes:** as SRS §7.3 and §5A.5.

**Functions:** `ASSIST_PRODUCT_DRAFT_RUN`, `ASSIST_EXPLAIN_REFUSAL`, `ASSIST_EXPLAIN_PREMIUM`, `ASSIST_SETTINGS_MANAGE`, `ASSIST_TENANT_SETUP_RUN`, under feature `AI_ASSIST`, off by default, in no role template. The pack-only path needs the function but not the entitlement (AIA-080, AIA-090).

**Touching existing modules, read hooks only (§9.2), except:**
- **Product Factory:** the provenance report on a version.
- **Rating:** a public query for a recorded calculation, through Contracts.
- **Static data:** the `SETUP` change set (§4.1).

### 5.1 Number verifier rules (AIA-033a)
The SRS requires these fixed before build. Numbers compare after normalising:
| Rule | Same number |
|---|---|
| Currency sign or code removed | `R1,240` = `1240` = `NGN 1240` |
| Thousands separators: comma or space | `1,240` = `1 240` = `1240` |
| Decimal places | `1240.00` = `1240` |
| Percentages either way | `15%` = `0.15` = `15 percent` |
| Dates as dates | `1 November 2026` = `2026-11-01` |

**Don't trigger a rejection when they're in the source:** list numbers, step numbers, years. **Always rejected:**
- A number not in the source, including a correct sum or a rounded figure: `R1,240.50` written as `about R1,240`.
- An amount in the source written in another currency: `1240 USD` from `1240 NGN`. The sign is removed to compare, but a stated currency must match.
- A rate or percentage matching only an unrelated amount, such as `15%` when the source's only 15 is an amount of 15. Percentages match percentages or their fractions.

**Fixed for the first release, English only (D-4):** a comma before exactly three digits is a separator, and any other comma isn't read as a number. French formats, such as `1 234,50`, come with the later language work.

**Test cases:** each row and each rejection above, in both directions, plus RWF with no decimals, zero, negative amounts and an empty source.

### 5.2 AI-3 and the starter packs
- **Format:** [`docs/specs/starter-packs`](../starter-packs/README.md) has the structure, drafted: base, country and line overlays. It needs a `tailorable` mark per value (AIA-082). In the build, packs become versioned static data (`starter_pack`, `starter_pack_value`).
- **Line overlays** are Life, General and Health, varied by country where the law differs (§5A.1).
- **Regulated values come only from an approved overlay;** otherwise they are Missing, and the AI can't fill them (AIA-082).
- **Language (AIA-091):** overlays carry their own texts, so French texts in a CIMA overlay are pack data, not AI output, and sit alongside D-4.

**Each country also needs code that isn't there yet,** from [the country survey](../../reports/2026-11-01-country-readiness-survey.md):
| Needed | Before |
|---|---|
| Appeal routes as static data, not `PPR_NFO` or `CMS` only | Nigeria, so every market |
| Rounding by currency, about 40 places fixed at 2 decimals | Rwanda, Senegal, Côte d'Ivoire |
| French: text in code, merged values, one-language wording fields | Senegal, DRC, Côte d'Ivoire; Rwanda in part |
| Limits with a currency | DRC |

These belong to the core product, not to Wave 7, and aren't in §6's figures.

## 6. Batches
Working days for one engineer, with moderate confidence, excluding provider contracts, evaluation documents and overlay reviews. **Order as D-2 and the SRS's D-8 recommendation.**
| Batch | What | Days |
|---|---|---|
| 7.1 | Module, settings, feature and functions, adapter with stub, minimiser, verifier, run records, metering and caps | 8 to 10 |
| 7.2 | AI-2 refusals, then premiums, with cache, feedback and fallback | 6 to 8 |
| 7.3 | Claude adapter, prompt templates, the AI-2 evaluation set and run | 4 to 6 |
| 7.4 | AI-1 extraction, field list from the model, background job, citations, flags, rate table check, injection tests | 12 to 15 |
| 7.5 | AI-1 review, provenance on approval, simulation cases, evaluation (needs D-7) | 8 to 10 |
| 7.6 | AI-3 pack storage and versions, questionnaire, deterministic assembly, flags, loading a real pack (pack-only path, AIA-090) | 8 to 10 |
| 7.7 | `SETUP` change set and shadow-tenant readiness | 10 to 14 |
| 7.8 | AI-3 tailoring and document mapping, evaluation | 6 to 9 |
| **Total** | | **62 to 82; AI-2 and AI-1 alone are 38 to 49** |

**The SRS says about 2 to 4 days for AI-2 and AI-1,** "at the current pace, not measured". I can't reconcile that with this list from what I've measured: mine are person-days, and work here has moved faster than person-days suggest. **I'd plan on the size above being the relative weight, and re-estimate after 7.1.**

## 7. Testing and proof
From SRS §9.1 and §9.2:
- **No real model in CI:** the stub gives fixed outputs, including wrong numbers, bad citations and an injected instruction's effect. Evaluation against the real model is a separate, manually started job.
- **Existing suites run with the feature off,** and a separate suite with it on, against the stub. Merged only on green CI of the exact commit, with its own locked end-to-end run.
- **Guards:** the model has no tool that writes (AIA-060); an AI-1 draft can't skip validation, simulation or a second person; a setup can't be approved by whoever ran it (AIA-086); AI-3 can't place a regulated value from outside an approved overlay (AIA-082, AIA-087); a user can't get an explanation of a record they can't read; Health explanations hold no clinical detail.
- **Evaluation sets are built before their features:** about 100 refusals and calculations for AI-2; one line's real documents for AI-1 (D-7); standard questionnaires and sample tenant documents for AI-3. Targets are SRS §4.3, §5.3 and §5A.4: proposals, none measured.

## 8. For Ade to sign
1. This addendum as the Wave 7 scope.
2. **The three decisions in §4:** the `SETUP` change set, shadow-tenant readiness, and data protection by the tenant's jurisdiction, with where Nigerian tenants are hosted.
3. **Malware scanning** (§4, item 8).
4. **SRS edits** (§4, items 4 to 7).

D-5, D-7, D-8 and D-10 stay open. D-10 is the one that holds up AI-3 in practice, since no overlay can go live without an approver.
