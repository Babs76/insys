# Wave 7 addendum: AI Assist

**Status: resumed on 15 November 2026 (Ade), the INSYS Preview having been live since early November; batch 7.2 started that day, in the D-8 order. It was paused after batch 7.1, which is merged, from 2 November (`docs/plans/insys-preview.md`).** Signed by Ade, with option C, 1 November 2026; D-5, D-7, D-8 and D-10 added the same day. Written from [the AI Assist SRS, revision 93](../../srs/INSYS_AI_Assist_SRS.md), and checked against the code on main. Ships off, per tenant (D-6).

## 1. What it is
Three features. In all three the AI prepares and a person decides; it never approves, publishes, pays or refuses, and INSYS works unchanged without it.
- **AI-2, plain-language explanations:** why INSYS refused a request, and how a recorded premium was calculated.
- **AI-1, Product Setup Assistant:** drafts a Draft product version from product documents, with a citation for every value.
- **AI-3, tenant configuration wizard:** drafts a new tenant's setup from reviewed starter packs and the tenant's answers, applied only after a second person approves.

The SRS's principles P1 to P8 and its requirements AIA-001 to AIA-091 stand as written; this addendum adds how to build them.

## 2. Decisions
| ID | Decision | State |
|---|---|---|
| D-1 | Provider | **Decided, 31 Oct:** Claude, through the Claude API or the deployment's cloud, behind the adapter |
| D-2 | First feature | **Decided, 30 Oct:** AI-2 for staff first |
| D-3 | Portal explanations | **Decided, 30 Oct:** a later release, after staff feedback and legal review |
| D-4 | Languages | **Decided, 30 Oct:** English first |
| D-6 | Timing | **Decided, 30 Oct:** back-end Wave 7 after Wave 6; front-end screens before Phase 13; shipped off |
| D-9 | Starter pack order | **Decided, 1 Nov:** Nigeria, Rwanda, Ghana, Senegal, Kenya, DRC, Côte d'Ivoire. Senegal and Côte d'Ivoire share a CIMA overlay with a thin national layer. South Africa stays as a reference overlay |
| D-11 | How AI-3's setup is applied | **Decided, 1 Nov:** one `SETUP` change set, only for a tenant still onboarding (AIA-086) |
| D-12 | How AI-3's draft is checked for readiness | **Decided, 1 Nov:** a completeness check before approval, then readiness on the tenant after applying, with go-live blocked until it passes (AIA-085) |
| D-13 | Data protection outside South Africa | **Decided, 1 Nov:** each tenant's jurisdiction, where inference runs and the legal basis (AIA-050). Hosting for Nigerian tenants is still open |
| D-14 | Malware scanning | **Decided, 1 Nov:** a scanner port, for all uploads, failing closed (AIA-010) |
| D-5 | Commercial model | **Decided, 1 Nov:** AI-2 included with a per-tenant fair-use cap; AI-1 and AI-3 within onboarding or implementation fees; usage above the cap billed. The cap and rates come from the cost measured in batch 7.3 (§6) |
| D-7 | Line for AI-1's evaluation set | **Decided, 1 Nov:** the first customer's launch line; otherwise a simple Life funeral or term product with age-band rate tables. The insurer's written permission comes first. Fixed before batch 7.5. **Fixed (Ade, 19 November 2026): Life funeral** (Ade named Life funeral or the back end's choice; the back end chose Life funeral, D-7's own fallback). 7.5 is built against sample documents; its evaluation waits for D-7's real documents, with the insurer's written permission |
| D-8 | AI-3's build order | **Decided, 1 Nov:** AI-3 is split: 7.1 to 7.3, then 7.6 and 7.7, then 7.4 and 7.5, then 7.8 (§6) |
| D-15 | Free text and prompts (AIA-051) | **Decided, 2 Nov:** no free text goes to a model until real name detection exists. AI-2's prompts are built only from allowed structured fields: the code, the catalogue entry, field errors, the record's permitted fields, and the rating calculation's recorded inputs and lines. Any free-text field is excluded, not masked (§5.4) |
| D-16 | Documents for AI-1 and AI-3 | **Decided (Ade, 17 November): as recommended, with additions.** A document reaches a model only after, in this order: the malware scan (AIA-010), a deterministic identifier check that refuses rather than masks, and the uploader's confirmation that it holds no customer personal data. The minimiser stays the backstop. For AI-3's tenant documents, the template asks for role titles, not names. Rules in §5.5 |
| D-17 | AIA-072 and the model call | **Decided in batch 7.2 (15 Nov), from the 7.1 review:** no transaction is open while the model answers. A run is a short read (settings, cap, cache), the call with nothing held, then a short command recording the run and the explanation; `RunAssist` is gone. The cost: two calls in flight together can pass the cap by what they use, as before |
| D-18 | Where the starter packs live (§5.2) | **Decided by Ade, 15 November (batch 7.6):** in Assist's own versioned tables (`assist.starter_pack`, `assist.starter_pack_version`, `assist.starter_pack_value`), with the overlay lifecycle of §5.3 there, not in the generic static data framework, whose approval doesn't model a partner's review, its evidence or a hash-bound approval. §5.2's "versioned static data" is read as versioned reference data held by Assist. A pack's rows still reach a tenant only through the `SETUP` change set and the governed import (7.7) |
| D-19 | The fallback's approval (§5.3) | **Decided by Ade, 15 November (batch 7.6):** a separate privileged tenant-side function, `ASSIST_OVERLAY_APPROVE_TENANT`, in no role template, assigned explicitly to the customer's compliance officer. It approves an overlay version for its own tenant only (`TENANT_ONLY`), never one prepared or marked `REVIEWED` by the same person. The platform's `ASSIST_OVERLAY_APPROVE` stays the normal route |
| D-20 | `regulated` and `tailorable` marks (AIA-082) | **Decided by Ade, 15 November (batch 7.6):** each pack row carries both marks explicitly. Until a row has them, a missing `tailorable` means not tailorable, and a missing `regulated` is read from the row's class (FINANCE or COMPLIANCE is regulated): an interpretation, recorded as such on the row. The review sheet shows both marks and their source for the reviewer and the approver to confirm (D-10). AI tailoring (7.8) uses explicit marks only, so a row whose mark is the interim reading is never tailored |
| D-10 | Who approves each country overlay | **Decided, 1 Nov:** two keys, a local compliance partner reviewing and a named OpenFactor compliance lead approving in INSYS; each insurer's compliance officer confirms its own tenant's values. Fallback without a partner: the first customer's compliance team approves for its own tenant only (§5.3) |

**What D-1 means for AI-2:** Claude runs globally or in the US, not in South Africa. AI-2 can be built first, but for each jurisdiction it waits to be switched on until compliance confirms the legal basis (POPIA s72 in South Africa) and the contract sets zero retention and no training (AIA-052). The route is a per-deployment setting of the adapter.

## 3. Refusal explanations (AIA-039)
**As proposed in the earlier draft, and now what the SRS says.** The console sends the refusal it received: code, field errors, record reference and correlation ID. INSYS rebuilds the context from the error catalogue and from the record as that user may read it (AIA-003), and never trusts record data sent by the screen. The correlation ID is kept for audit and isn't used for lookup. No new store of refusals, and nothing added to the request pipeline.

The cache key (AIA-036) is the code, the field errors, the record's reference and row version, and the prompt version. The catalogue's "remedy" wording (AIA-031) needs checking per code; gaps are filled in batch 7.2.

## 4. Where the SRS and the code differ
Read against main.

### Decided by Ade, 1 November, and now in the SRS
1. **`SETUP` change set (AIA-086): approved in principle, for a tenant that isn't live.**
   - BR-016 approves an import of one table at a time. `SETUP` groups one import batch per table across a tenant's setup: up to 295 tables, several tiers and several modules' databases.
   - One approval, bound to a hash of every batch. Whoever ran the wizard can't approve it.
   - Applied in dependency order and resumable, because no single transaction spans the modules.
   - Refused unless the tenant is in `Onboarding`. Live tenants keep per-table BR-016 imports.
   - The tenant can't go live while a set is partly applied.
2. **Data protection (AIA-050): approved.** The setting records the tenant's jurisdiction, where inference runs and the legal basis. AI-2 refuses when inference is outside the jurisdiction with no basis recorded. Hosting for Nigerian tenants isn't decided, and **nothing may assume South African hosting.**
3. **Malware scanning (AIA-010): build a scanner port.** ClamAV runs as a separate service and is never linked, because it is GPL-2.0. Check its maintenance and current version at build. Uploads stay quarantined until clean, and if the scanner is down, uploads wait rather than pass.
4. **The AI-1 job acts for the recorded user (AIA-017),** with permissions checked at the start and on writing. Jobs run as a service identity today (`ExecutionScope.InitialiseForService`), so this is a new pattern. **Security review before batch 7.4 merges.**
5. **The tenant administrator runs the wizard first (AIA-080).** ADR-024 refuses business routes to the operator tenant, so an operator path follows only if onboarding needs it, as its own audited platform route.
6. **Global rows load once, by the platform, when an overlay is approved.** Currencies, countries, languages and holidays are global tables, so a tenant's wizard can't write them.

### Readiness before go-live (AIA-085, D-12): option C
**Decided: option C, not the shadow tenant,** which isn't needed.

**How C works:**
- **Before approval,** a deterministic completeness check: no value Missing, and every regulated value from an approved overlay.
- **After approval,** the set is applied to the target tenant, and the unchanged readiness checks run there.
- **Go-live stays blocked until they pass.**

**Why it holds, from the code:** a tenant stays in `Onboarding` until `ActivateTenant`, which needs every onboarding step complete, and a step completes only when its readiness passes (`Tenant.cs`, `Tenants.cs`). A tenant in `Onboarding` already takes governed static data: that's how the live run sets one up.

**What C needs that isn't there:**
- **A guard in activation.** `ActivateTenant` looks only at whether the steps were completed, so a step completed before the set was applied wouldn't be looked at again. Activation must refuse while a set is submitted or partly applied, and steps finished before the set must be run again.
- **A blocking readiness check** for a partly applied set, in the existing `IReadinessCheck` form.
- **The completeness check** itself.

| | Option C | Shadow tenant |
|---|---|---|
| `SETUP` change set, both need it | 7 to 9 days | 7 to 9 days |
| Readiness | 1 to 2 days | 4 to 6 days: creating, loading, purging, isolation, security review |
| **Batch 7.7** | **8 to 11 days** | **11 to 15 days** |
| Readiness result seen | After approval | Before approval |

**The two costs of C:**
1. **The checker approves before seeing some blockers.** Completeness can't catch what only live checks find, such as a rating test case that fails, or a transaction type with no posting rule. A blocker found then is fixed by a further change, and no customer is affected, because the tenant isn't live.
2. **Same-day corrections are awkward.** A new version must start after the one in force (`RETROACTIVE_CHANGE`), so a correction to a set applied today takes effect tomorrow, and readiness passes then. It delays onboarding by a day, and it's acceptable. If onboarding teams find it too slow, the fix is a rule letting a `SETUP` follow-up replace rows on a tenant that isn't live.

### Handled in this addendum
- **AIA-011:** PDF, DOCX and XLSX extraction needs new packages, OSI licences only, checked at build.
- **"Base environment settings" (§5A):** only static data can go through a change set. I've read these as the settings held in static data (`tenant.cfg_locale_defaults`, for example). Deployment configuration stays out.
- **The premium's "recorded steps":** these are `CalculationView.Inputs` and `Lines` (`GET /rating/calculations/{id}`). No change needed.

## 5. What gets built
**Module `Assist`:** own schema and role, forced row-level security, tenant from the token (AIA-062). Tables as SRS §7.2 and §5A.5.

**Adapter:** `IAiModelProvider`, with a stub for tests and a Claude implementation in the Cloud adapter project, so ADR-027's SDK guard holds. Its SDK joins the guard's list. Check the package's licence at build. Model and prompt versions are configuration (AIA-074).

**Deterministic checks, which are what makes this safe:**
- **Minimiser (AIA-051):** removes names, identifiers, contact and bank details, and policy, claim and member numbers. The payload actually sent is stored.
- **Verifier:** numbers (§5.1), citations resolving to real text (AIA-013, AIA-083), rate tables cell for cell (AIA-015), and for AI-3, each value traced to a pack or a cited tenant input (AIA-087).

**API and error codes:** as SRS §7.3 and §5A.5.

**Functions:** `ASSIST_PRODUCT_DRAFT_RUN`, `ASSIST_EXPLAIN_REFUSAL`, `ASSIST_EXPLAIN_PREMIUM`, `ASSIST_SETTINGS_MANAGE`, `ASSIST_TENANT_SETUP_RUN`, under feature `AI_ASSIST`, off by default, in no role template. The pack-only path needs the function but not the entitlement (AIA-080, AIA-090).

**The functions as built (batch 7.6), all in no role template; the SRS §3 table picks up the last three in its next revision:**
| Function | Audience | Privileged | What it allows |
|---|---|---|---|
| `ASSIST_SETTINGS_MANAGE` | Staff | No | The tenant's settings and usage |
| `ASSIST_EXPLAIN_REFUSAL`, `ASSIST_EXPLAIN_PREMIUM` | Staff | No | AI-2's explanations |
| `ASSIST_PRODUCT_DRAFT_RUN` | Staff | No | AI-1's drafts (7.4) |
| `ASSIST_TENANT_SETUP_RUN` | Staff | No | The wizard (AI-3), and loading the tenant's own fallback overlay, only while it is onboarding (Ade, 15 November) |
| `ASSIST_OVERLAY_MANAGE` | Platform | Yes | Loading a pack version for every tenant and recording the partner's review. It never approves (Ade, 15 November) |
| `ASSIST_OVERLAY_APPROVE` | Platform | Yes | The named lead's approval of a version for every tenant |
| `ASSIST_OVERLAY_APPROVE_TENANT` | Staff | Yes | The fallback: the tenant's compliance officer approving its own version for its use only (D-19) |

Whoever loaded a version or recorded its review can't approve it, whatever functions they hold.

**Touching existing modules, read hooks only (§9.2), except:**
- **Product Factory:** the provenance report on a version.
  - **Amendment (Ade, 19 November 2026), AI-1's writes:** Product Factory's AI-1 port has `Create` (7.4, AIA-017) and `Edit` (7.5, AIA-019). Both write as the user who ran the draft, through Product Factory's normal commands (create a draft version; edit its configuration), so its validation, audit, If-Match and state rules apply as to any user's edit. Why: the configurator's review decisions on a version's own fields must reach the Draft, and going through the normal command keeps every Product Factory rule. Assist never writes Product Factory's tables.
  - **Second narrow exception (OQ-190 (b), Ade, 19 November 2026):** Product Factory's submission for approval asks an optional check declared in its Contracts (`IProductSubmissionCheck`), which Assist implements. A version AI-1 drafted is refused with `PRODUCT_AI_REVIEW_INCOMPLETE` while its review is incomplete (the provenance report's `reviewComplete`). A version AI-1 didn't draft, or a tenant without the AI Assist feature, is unaffected; with no implementation registered, submission is unchanged.
- **Rating:** a public query for a recorded calculation, through Contracts.
- **Static data:** the `SETUP` change set (§4).
- **Tenancy:** the activation guard and the readiness check for a partly applied set (§4).
- **Documents:** the scanner port and quarantine, for all uploads (§4).

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

**Currency (decided by Ade, 2 November):** batch 7.2 states the currency beside every amount in the data sent and checked, as a stated `USD 1240` passes against a plain `1240` under the table above.

### 5.4 Allowed fields (AIA-051, D-15)
No pattern finds a name it wasn't told, so masking can't meet AIA-051 for free text. Batch 7.2 therefore:
- builds each prompt from a list of allowed structured fields per explanation, and sends nothing else;
- excludes every free-text field, such as notes, reasons, descriptions and document words;
- removes `UntrustedText` and `MinimiseFreeText` from the runner, which were built in 7.1 before this decision.

The minimiser stays as a second layer. AI-1 and AI-3 read documents, so D-16 must be decided before their batches.

**Built in batch 7.2 (15 November):** `AllowedFacts` takes only codes, amounts with their currency, numbers, yes or no, dates and the error catalogue's own wording; any other value is left out and counted, never masked and sent. Its form is a backstop, not the guard, as a code-shaped word can still be anything: the explanations add only values INSYS itself recorded or configured. A refusal sends its code, whether it is worth retrying, the catalogue's wording and how many fields are in error, never the fields' names, the screen's messages or the record, which are the client's. A premium sends the product and Rating's lines (step, configured factor, value used, amount with its currency), never the inputs, whose names and values are the caller's; for Health, not the factor values either (AIA-038). The verifier compares numbers with the facts' values only, and a stated currency must match an amount in that currency (§5.1, strict for AI-2). Prompt templates are INSYS's own words, never interpolated (AIA-061); 7.3 moves them into configuration.

**Not yet in 7.2 (OQ-166, OQ-167):** a refusal is explained from its code, the catalogue's wording and the fields' names; the record's state as the user may read it (AIA-039) waits on read hooks per record type. Of 260 error codes, 69 have catalogue wording; the rest are shown plain, with no call.

### 5.5 Documents for AI-1 and AI-3 (D-16, Ade, 17 November)
**Allowed files:**
- PDF, DOCX and XLSX only, the inputs AIA-011 names. The type is read from the content, not taken from the file name.
- At most 20 MB a file and 10 files a run.
- Anything else is refused before it is scanned or read.

**In this order:**
1. **The malware scan** (AIA-010). Each upload stays quarantined until clean; if the scanner is down, uploads wait.
2. **The identifier check:** deterministic, on the extracted text. A document holding an obvious identifier is refused for the run, not masked. The refusal names the kind of identifier and the page, never the value. Obvious identifiers are:
   - an email address;
   - a South African ID number: 13 digits with a valid date and check digit;
   - an IBAN that passes its check digits;
   - a number labelled as an identity or account number: "ID number", "identity number", "national ID", "NIN", "BVN", "passport number", "account number", "account no", "acc no", followed by 6 or more digits.

   The labels are English only (D-4); other languages come with the language work.
3. **The uploader's confirmation** that the document holds no customer personal data. It names the person (the signed-in user), the document (its id, file name and content hash) and the date, and is recorded on the run and audited. A document whose content changes needs a new confirmation.
4. **The minimiser**, as the backstop on what is sent (AIA-051).

**AI-3's tenant documents (7.8):** the template asks for role titles, not people's names.

**As built in 7.4, with Ade's decisions of 17 November:**
1. **The uploader confirms, no one else.** A run on a document another user uploaded is refused (`ASSIST_NOT_UPLOADER`); the way round is to upload it again.
2. **Only Found values are written into the Draft (P3: copied from the source, never inferred).** Interpreted and Missing values stay cited proposals on the run for the configurator, like the rate tables.
3. **What the Draft holds (OQ-184, decided):** the version's own fields only. Every other value is a cited proposal, and AIA-021 is unchanged.
4. **The type is read from the content once the scan releases it,** since nothing is read before the scan. The size and the count are refused at the start, from Documents' metadata. A document whose category is PERSONAL or HIGHLY_SENSITIVE is refused, whatever was confirmed.
5. **"Required by the model" (AIA-014) is the version's name and coverages:** a version always has a name, and validation refuses one with no coverages. No table is required yet.

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

### 5.3 Overlay approval (D-10)
An overlay version moves `DRAFT`, `REVIEWED`, `APPROVED`. Only an approved version can supply a regulated value, or else the wizard refuses with `ASSIST_PACK_NOT_APPROVED`, and every approval below names a person and is audited.

**The normal route has two keys and a confirmation:**
| Step | Who | Recorded in INSYS | Effect |
|---|---|---|---|
| Review | The local compliance partner, outside INSYS | Partner, reviewer's name, date and the signed review document, kept as evidence on the version. Whoever marks it `REVIEWED` can't approve it | `REVIEWED`. Approval is refused without it |
| Approve | The named OpenFactor compliance lead, in INSYS | Approval bound to a hash of the overlay version | `APPROVED`, scope `ALL_TENANTS`. Global rows then load once, by the platform |
| Confirm | The insurer's compliance officer, at onboarding | Confirmation of the tenant's FINANCE and COMPLIANCE values, bound to the setup's hash | Needed before the `SETUP` set can be approved. The insurer stays responsible to its regulator |

**The fallback, with no partner engaged before the first customer:**
- The customer's compliance team approves the overlay in its own tenant, with no partner review and no lead's approval.
- The version's scope is then `TENANT_ONLY`, naming that tenant. **Only that tenant can use it,** and the wizard refuses it for any other (`ASSIST_PACK_NOT_APPROVED`).
- It becomes available to others only through the normal route, as a new version.
- Whoever ran the wizard can't approve in either route, and in the fallback the approval and the confirmation may be the same act.

**Decided by Ade, 1 November:**
1. **Global rows in the fallback.** The fallback tenant can't write global tables, and one customer's approval shouldn't change them for everyone. The platform loads only what is plain reference data (a currency, a holiday calendar) on the compliance lead's approval of those rows alone. Regulated values stay in tenant rows.
2. **A sixth function.** Approving an overlay is a platform act, not one of the SRS's five tenant functions. `ASSIST_OVERLAY_APPROVE`, in the operator tenant, is privileged, for the named lead, with no role template. It is refused to whoever prepared the version or marked it `REVIEWED`. The SRS picks this up in its next revision. The fallback uses a tenant-side grant of the same action, limited to `TENANT_ONLY`.

## 6. Batches
Working days for one engineer, with moderate confidence, excluding provider contracts, evaluation documents and overlay reviews. **This is the sizing in SRS §9, plus overlay approval (§5.3) in 7.6, accepted by Ade at 10 to 13 days for 64 to 85 in total, which the SRS picks up in its next revision; calendar time is re-estimated after 7.1. The build starts only after Wave 6.** Batch numbers stay as they were; the table is in build order (D-8).
| Order | Batch | What | Days |
|---|---|---|---|
| 1 | 7.1 | Module, settings with jurisdiction, feature and functions, adapter with stub, minimiser, verifier, run records, metering and caps | 8 to 10 |
| 2 | 7.2 | AI-2 refusals, then premiums, with cache, feedback and fallback | 6 to 8 |
| 3 | 7.3 | Claude adapter, prompt templates, the AI-2 evaluation set and run. **Reports cost per explanation (D-5)** | 4 to 6 |
| 4 | 7.6 | AI-3 pack storage and versions, overlay approval (§5.3), questionnaire, deterministic assembly, flags, loading a real pack (pack-only path, AIA-090) | 10 to 13 |
| 5 | 7.7 | `SETUP` change set, completeness check, activation guard (option C) | 8 to 11 |
| 6 | 7.4 | Scanner port and quarantine, AI-1 extraction, field list from the model, background job acting for the user (security review before merge), citations, flags, rate table check, injection tests | 14 to 18 |
| 7 | 7.5 | AI-1 review, provenance on approval, simulation cases, evaluation (D-7's line fixed before it starts) | 8 to 10 |
| 8 | 7.8 | AI-3 tailoring and document mapping, evaluation | 6 to 9 |
| | **Total** | | **64 to 85; AI-2 and AI-1 alone are 40 to 52** |

**The D-8 order holds up against the dependencies.** 7.6 and 7.7 use no model and no uploads. 7.8 maps uploaded documents, so it needs the scanner from 7.4, which it follows.

**What batch 7.3 reports for D-5:** cost per explanation, in tokens and money, split by refusal and premium, with the cache hit rate, the model and the prompt version, and the price list used. It comes from the evaluation run, so it's a measured figure on test data, to be read as such. From it Ade sets the fair-use cap and the rate above it. Until then the cap (AIA-073) is the operator's, set low. **INSYS meters usage and gives a monthly usage report per tenant; billing is outside INSYS (Ade, 1 November).** AI-1 and AI-3 are metered too, so onboarding fees can be checked against cost.

**AI-2 evaluation results (batch 7.3, measured on the 110-case evaluation set; sample data, to be read as such):**
| Run | Model | Verified | Rejected, all for a number word | Median / p95 / slowest (s) | Cost per explanation, mean |
|---|---|---|---|---|---|
| 43904917124 (16 November, main `10ea4cf`; templates `refusal-v2`, `premium-v2`) | `claude-opus-5-5` | 108 of 110 | 2 (`twice`) | 3.38 / 6.66 / 8.08 | USD 0.0077 |
| | `claude-sonnet-5-5` | 107 of 110 | 3 (`two`) | 2.47 / 3.30 / 3.80 | USD 0.0026 |
| | `claude-haiku-5-5` | 90 of 110 | 20 (`two` 19 times, `four` 4 times) | 1.49 / 5.03 / 5.56 | USD 0.00023 |

- **What was rejected:** no figure was wrong. Every rejection was a number word, which the verifier refuses whatever it says (§5.1), so no explanation with a number not in the source was shown (SRS §5.3, first target: met).
- **Where it ran:** inference ran in `us` for every case, as the provider reported it (AIA-050), with `inference_geo` `us`.
- **Prices and time:** cost is at the configured list prices (`Assist:Prices`, as listed on 3 November 2026), with effort `medium`. Every p95 is under the 10 s target.
- **The first run** (43862166993, main `3da6665`, v1 templates) verified 68, 44 and 78 respectively. All 140 rejections were list markers or number words, fixed by `verifier-lists` and the v2 templates.
- **Not yet measured:** the other §5.3 targets, explanations reviewed correct and complete by a domain expert (95%) and rated easy to understand (90%), wait on that review.

**Provisional choice for AI-2 (Ade, 16 November): `claude-sonnet-5-5`,** pending the domain expert's review (SRS §5.3). It is near Opus on verification (107 against 108 of 110), at about a third of the cost and half the p95. No default is changed: `Assist:Model` stays unset until the review.

**Roadmap phases and gates (SRS §9), in build order:**
| Phase | What | Batches | Gate |
|---|---|---|---|
| 0 | Decide | | Go decision, provider chosen, SRS approved |
| 1 | Foundations | 7.1 | Security tests pass: isolation, injection, no write tools |
| 2 | AI-2 for staff | 7.2, 7.3 | SRS §5.3 targets met; cost per explanation reported |
| 3A, pack-only | AI-3's pack-only path and `SETUP` | 7.6, 7.7 | SRS §5A.4 targets that apply without AI, and first overlay approved (D-10). Usable only from then |
| 3 | AI-1 drafts | 7.4, 7.5 | SRS §4.3 targets met, product team sign-off |
| 3A, AI | AI-3's tailoring and mapping | 7.8 | The rest of SRS §5A.4: mapped values |
| 4 | Pilot, one tenant | | Pilot review accepted by Ade |
| 5 | Rollout | | |

## 7. Testing and proof
From SRS §9.1 and §9.2:
- **No real model in CI:** the stub gives fixed outputs, including wrong numbers, bad citations and an injected instruction's effect. Evaluation against the real model is a separate, manually started job.
- **Existing suites run with the feature off,** and a separate suite with it on, against the stub. Merged only on green CI of the exact commit, with its own locked end-to-end run.
- **Guards:** the model has no tool that writes (AIA-060); an AI-1 draft can't skip validation, simulation or a second person; a setup can't be approved by whoever ran it (AIA-086); AI-3 can't place a regulated value from outside an approved overlay (AIA-082, AIA-087); a user can't get an explanation of a record they can't read; Health explanations hold no clinical detail.
- **Evaluation sets are built before their features:** about 100 refusals and calculations for AI-2; one line's real documents for AI-1 (D-7); standard questionnaires and sample tenant documents for AI-3. Targets are SRS §4.3, §5.3 and §5A.4: proposals, none measured.

**Premium coverage of the evaluation set (Ade, 17 November):** the rating engine records these line types:
- `EXPOSURE`, `TABLE`, `FACTOR`, `PERCENT` (a percentage loading);
- the adjustment types (`DISCOUNT`, `LOADING`);
- `FEE`, `MINIMUM` and `MAXIMUM` (premium bounds by frequency), `ROUNDING` and `TAX`.

**The sample configuration added to `OFT-HOME`,** labelled as sample in the seed and invented, not a rate:
- a 10% loading (`OFT-HOM-LOAD`);
- a 15% discount (`OFT-HOM-NCD`) and a ZAR 120 loading (`OFT-HOM-FLOOD`), applied in that order;
- monthly bounds of ZAR 800 to ZAR 10,000.

No premium tax until compliance says which applies.

**The set's premiums:**
- `OFT-HOME` is rated without schedules, with each and with both, so its 10 cases became 40.
- The set now holds 66 premiums: `PERCENT` in 40, `DISCOUNT` 20, `LOADING` 20, `MINIMUM` 4, `MAXIMUM` 5. The other products stay `EXPOSURE`, `TABLE`, `FEE` and `ROUNDING`.
- `OFT-HOME`'s test case is now ZAR 2250: 2000 by the table, 2200 after the loading, then the fee.

**Deferred (Ade, 17 November): number words read as their digits.** Proposed after the rerun; not built. §5.1 is unchanged. Revisit only if the domain expert's review favours `claude-haiku-5-5`.
- **The proposal:**
  - (a) The facts carry counts INSYS works out itself, such as `lines: 4` for a premium. A refusal already sends how many fields are in error.
  - (b) "two" to "ten" are read as 2 to 10, and accepted only where they equal one of those count facts, never an amount, a rate or a step number.
  - "one" stays ordinary English, as now. `twice`, `half`, `double`, `both` and words above ten stay refused.
  - Optional: a `decimal places` fact from Rating's rounding rule.
- **Against P3 and AIA-033a:** P3 holds, since every accepted number still matches one in the input, worked out by INSYS. AIA-033a holds, since it adds a rule and loosens none. It would be fixed here first, as AIA-033a requires.
- **The estimate, from run 43904917124's logs (not measured):** small. It would recover Haiku's "four lines" (about 3 or 4 of its 20 rejected cases) and probably none of Opus's or Sonnet's. Most rejected words described the answer ("two options") or a rounding the model worked out ("two decimal places"), and the facts hold no such number.

## 8. Still open
Where Nigerian tenants are hosted (D-13), which the Nigerian compliance partner is also being asked about. Engaging that partner, and collecting the evaluation documents with the insurer's written permission (D-7), are the actions that come first. An overlay's approval is what makes AI-3's pack-only path useful.

### AI-1's review: decided interpretations (Ade, 19 November 2026)
- **Who decides:** only the configurator who ran the draft decides its values. Anyone else runs a draft of their own.
- **A rejection** puts back the value of the version the Draft was copied from.
- **Interpreted rules and simulation cases** must be reviewed too, as much as a version's own fields: they count in the provenance report's `reviewComplete`, and so in the submission check (OQ-190 (b)).
- **The provenance report's function** (`ASSIST_PRODUCT_DRAFT_PROVENANCE`) is an action under Product Factory's `PRODUCTS` screen (sub-domain `PRODUCT_FACTORY`, process area "Definitions & versions"), with no feature, so a report stays readable with AI Assist off. It is in the product manager's, compliance officer's and auditor's templates; the other eight Assist functions stay in none.
