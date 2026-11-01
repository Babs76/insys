# Wave 7 addendum: AI Assist

**Status: DRAFT for Ade's sign-off. Nothing here is built.** Written from *INSYS AI Assist: Software Requirements Specification (Draft for Review)*, draft 0.1 (AIA-001 onwards), which is itself not yet signed.

## 1. What it is
Two narrow features, off unless a tenant switches them on:
- **AI-1, Product Setup Assistant:** reads uploaded product documents and drafts a Draft product version, with a citation for every value.
- **AI-2, plain-language explanations:** explains why INSYS refused a request, and how a recorded premium was calculated.

In both, the AI prepares and a person decides. It never approves, publishes, pays or refuses, and INSYS works unchanged without it. The SRS's eight principles (P1 to P8) and its out-of-scope list stand as written: no AI decision, no writes beyond a Draft version, no customer chat, no training on tenant data.

## 2. Decisions
| ID | Decision | State |
|---|---|---|
| **D-1** | Provider | **Decided (Ade): Claude, behind the provider adapter.** See §3 for what this leaves open on where it runs |
| **D-5** | Commercial model | **Open.** Metering (AIA-073) supports any answer |
| **D-7** | Product lines for AI-1's evaluation set | **Open.** It decides which documents we need before AI-1 can be measured |
| D-2 | Which feature first | *Proposed, as the SRS recommends:* AI-2 for staff first |
| D-3 | Portal explanations | *Proposed:* a later release, after staff feedback and legal review |
| D-4 | Languages | *Proposed:* English only in the first release |
| D-6 | Timing | *Proposed:* after the core waves reach release candidate, unless a pilot customer needs it sooner |

**For sign-off:** D-2, D-3, D-4 and D-6, which you didn't mention. This draft assumes the SRS's recommendations.

## 3. What D-1 settles, and what it doesn't
It settles the provider, and that all calls go through one adapter (P7). It doesn't settle **where inference runs,** which the SRS treats as the same decision:
- **POPIA s72:** AI-2's premium explanations use rating factors, such as age, smoker status and health loadings, which can be personal or special personal information, even with names removed. Unless Claude can be used in South Africa, that is a transfer abroad needing a recorded basis.
- **What the SRS found:** AWS's Bedrock cross-Region inference from Cape Town may process requests in other Regions. No in-country route was confirmed. I haven't re-checked this for this draft.
- **What this draft does:** AIA-050 as written: each tenant records where inference runs and its legal basis, and AI-2 refuses to run offshore without one (`ASSIST_RESIDENCY_NOT_SET`). AI-1 handles product documents, which normally hold no personal data, and is still checked for it.

**Question for Ade:** which route to Claude: Anthropic's API directly, or a cloud's hosting of it? It decides the contract terms AIA-052 needs (no training on INSYS data, a retention limit) and the adapter's first implementation. *Recommendation: settle it with compliance before AI-2 is piloted on real data. The adapter can start against a stub either way, and AI-2 on refusals alone carries the least data.*

**Tension with D-2 worth a look:** the SRS recommends AI-2 first as the smaller feature, and AI-1 first on the offshore route because it has no personal data. *Recommendation: start AI-2 with refusals, whose payload holds no rating factors, then premiums once the basis is recorded, then AI-1.*

## 4. Where the SRS assumes something INSYS doesn't have
Read against main; these change the build or its estimate.
| SRS says | What's there | Proposal |
|---|---|---|
| AIA-010: uploaded files are "scanned before use" | No malware scanning anywhere in INSYS | A question for Ade: build a scanner port (with ClamAV, say), or accept tenant-staff uploads without one for AI-1. It would serve Documents generally |
| AIA-031: explain a refusal "by its correlation ID" (§7.3) | Refusals aren't stored. A ProblemDetails goes back to the caller, and only some refusals are audited | **The request carries the code, field errors and record it got back,** and INSYS checks the code against the catalogue and the caller's access to the record. No new store of refusals. It differs from the SRS's API |
| AIA-031: the catalogue's "description and remedy" | `integration.ref_error_code_catalogue` exists; the remedy wording needs checking per code | Fill the gaps as part of AI-2 |
| AIA-017: the Draft is created "under the identity of the user who ran it" from a background job | Jobs run as a service identity (`ExecutionScope.InitialiseForService`) | The job acts for the recorded user, with their permissions checked when it starts and again on writing. This is a new pattern and needs care: **a security review item** |
| AIA-011: extract PDF, DOCX and XLSX | No extraction libraries | New packages, OSI licences only, checked at build. Scanned pages are "not readable", as the SRS says |
| §9, the evaluation and rollout section | **Missing from the PDF I have:** it goes from section 8 to section 10, though §4.3 refers to it | Please check. §7 below is my proposal in its place |

The SRS also came in two versions. A file from 28 October describes different features (a setup assistant and onboarding data mapping, with PSA and ODM requirements and nine decisions). I used the one saved on 30 October, whose D-1, D-5 and D-7 match your message. **Please confirm that's the right one.**

## 5. What gets built
**Module `Assist`:** own schema and role, forced row-level security, tenant from the token (AIA-062). Tables `run`, `run_payload`, `draft_item`, `explanation`, `tenant_setting` as SRS §7.2.

**Adapter:** `IAiModelProvider`, with a stub for tests and a Claude implementation in the Cloud adapter project, so the SDK guard from ADR-027 holds. Add its SDK to the guard's list. Anthropic's C# SDK is listed as Apache-2.0 on GitHub; check the package at build. The model and prompt template versions are configuration (AIA-074), changed only after the evaluation set passes.

**Deterministic checks, which are what makes this safe:**
- **Minimiser (AIA-051):** removes names, identifiers, contact and bank details, and policy, claim and member numbers. The payload actually sent is stored.
- **Verifier:** every number in an explanation is in its source (AIA-033); every citation resolves to real text (AIA-013); rate tables match cell for cell (AIA-015). A failure drops the value, or shows the structured breakdown instead.

**API,** as SRS §7.3 except for refusals (§4 above). Five new error codes: `ASSIST_UNAVAILABLE`, `ASSIST_CAP_REACHED`, `ASSIST_RESIDENCY_NOT_SET`, `ASSIST_SOURCE_UNREADABLE`, `ASSIST_OUTPUT_REJECTED`.

**Functions:** the SRS's `assist.*` names become the catalogue's convention: `ASSIST_PRODUCT_DRAFT_RUN`, `ASSIST_EXPLAIN_REFUSAL`, `ASSIST_EXPLAIN_PREMIUM`, `ASSIST_SETTINGS_MANAGE`, under feature `AI_ASSIST`, off by default. Permission `assist_*` names follow. All four are in no role template until a tenant grants them.

**Touching existing modules:** read hooks only, except:
- **Product Factory:** the provenance report on a version, shown to the approver; whoever ran AI-1 counts as the version's maker (AIA-002).
- **Rating:** a public query for a recorded calculation, through Contracts.

## 6. Batches
Working days for one engineer, with moderate confidence, excluding the provider contract and the evaluation documents.
| Batch | What | Days |
|---|---|---|
| 7.1 | Module, settings, feature and functions, adapter with stub, minimiser, verifier, run records, metering and caps | 8 to 10 |
| 7.2 | AI-2 refusals, then premiums, with cache, feedback and fallback | 6 to 8 |
| 7.3 | Claude adapter, prompt templates, the AI-2 evaluation set and run | 4 to 6 |
| 7.4 | AI-1 extraction, field list from the model, background job, citations, flags, rate table check, injection tests | 12 to 15 |
| 7.5 | AI-1 review, provenance on approval, simulation cases, evaluation (needs D-7) | 8 to 10 |
| **Total** | | **38 to 49, so about 8 to 11 weeks with review** |

## 7. Testing and proof
- **No real provider in CI:** the stub gives fixed outputs, including wrong numbers, bad citations and an injected instruction's effect, to prove the verifier and fallback.
- **Guards:** the model has no tools that write (AIA-060); an AI-1 draft can't skip validation, simulation or a second person (tested end to end); a user can't get an explanation of a record they can't read (AIA-003); Health explanations hold no clinical detail (AIA-038).
- **Evaluation set, proposed in place of the missing §9:** real or realistic documents per line (D-7), and refusals and calculations a domain expert marks. It runs by hand against the real model before a pilot and before any model or template change, measured against SRS §4.3 and §5.3. Those targets are proposals, and nothing has been measured.
- **Rollout, proposed:** one pilot tenant, staff only, with a low cap; then wider; portals only after D-3.

## 8. What I need from Ade to sign
1. The route to Claude (§3), with compliance.
2. D-2, D-3, D-4 and D-6 as proposed, or otherwise; D-5 and D-7 stay open.
3. Malware scanning (§4).
4. The refusal API's change from the SRS (§4).
5. Which SRS is current, and the missing §9.

D-7 must be answered before batch 7.5, and D-5 before any customer is charged.
