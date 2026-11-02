# Wave 7 addendum: AI Assist

**Status: signed by Ade, with option C, 1 November 2026; D-5, D-7, D-8 and D-10 added the same day. Nothing here is built, and the build starts only after Wave 6.** Written from [the AI Assist SRS, revision 93](../../srs/INSYS_AI_Assist_SRS.md), and checked against the code on main. Ships off, per tenant (D-6).

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
| D-7 | Line for AI-1's evaluation set | **Decided, 1 Nov:** the first customer's launch line; otherwise a simple Life funeral or term product with age-band rate tables. The insurer's written permission comes first. Fixed before batch 7.5 |
| D-8 | AI-3's build order | **Decided, 1 Nov:** AI-3 is split: 7.1 to 7.3, then 7.6 and 7.7, then 7.4 and 7.5, then 7.8 (§6) |
| D-15 | Free text and prompts (AIA-051) | **Decided, 2 Nov:** no free text goes to a model until real name detection exists. AI-2's prompts are built only from allowed structured fields: the code, the catalogue entry, field errors, the record's permitted fields, and the rating calculation's recorded inputs and lines. Any free-text field is excluded, not masked (§5.4) |
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

**Touching existing modules, read hooks only (§9.2), except:**
- **Product Factory:** the provenance report on a version.
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

The minimiser stays as a second layer. AI-1 and AI-3 read documents, so they need this question answered before their batches: real name detection, or documents checked to hold no personal data.

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

## 8. Still open
Where Nigerian tenants are hosted (D-13), which the Nigerian compliance partner is also being asked about. Engaging that partner, and collecting the evaluation documents with the insurer's written permission (D-7), are the actions that come first. An overlay's approval is what makes AI-3's pack-only path useful.
