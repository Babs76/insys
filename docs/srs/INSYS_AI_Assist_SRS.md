# INSYS AI Assist: Software Requirements Specification (Draft for Review)

Nov 1, 2026 · @AB

## 1. Purpose, scope and status

INSYS AI Assist adds three AI features to INSYS Core: a **Product Setup Assistant** (AI-1) that drafts product configuration from product documents, **plain-language explanations** (AI-2) of refusals and premium calculations, and a tenant configuration wizard (AI-3) that drafts a new tenant's setup from reviewed starter packs and the tenant's own answers. In all three, the AI prepares material and a person decides; the AI never approves, publishes, pays or refuses anything.

**Status: the basis of back-end Wave 7.** Nothing here is built. The back end's Wave 7 addendum turns this specification into a build plan after Wave 6. Open decisions are listed in section 10.2.

**Reviewed 30 October 2026.** Ade accepted the recommendations for D-1, D-2, D-3, D-4 and D-6 (section 10.2); D-5 and D-7 stay open. On 31 October Ade chose Claude as the AI provider (D-1). On 1 November Ade added AI-3, the tenant configuration wizard (section 5A), and set the country order for its starter packs (D-9). The review also changed how refusals are explained (AIA-031, AIA-039), added number-checking rules (AIA-033a), aligned function names with the catalogue, set the build window and added build safeguards (section 9.2).

| Item | Detail |
| --- | --- |
| Document | INSYS AI Assist SRS, draft 0.1 |
| Owner | Product owner, OpenFactor Technology Group |
| Relates to | INSYS Core SRS v1.0; Product Factory, Rating, Rules, Workflow, Audit and Identity modules |
| Requirement IDs | AIA-001 onwards (proposed; not yet in the INSYS SRS or function catalogue) |
| Decision needed | Go / no-go, hosting option, rollout order (section 10) |

### In scope

- AI-1: read uploaded product documents (policy wording, brochure, rate tables) and draft a **Draft** product version in Product Factory, with a source citation for every proposed value.
- AI-2: explain, in plain English, why INSYS refused a request (from its stable error code and recorded context) and how a recorded premium was calculated (from the rating calculation's recorded steps).
- AI-3: a tenant configuration wizard that drafts a new tenant's static data and base settings from reviewed starter packs (base, country and line overlays) and the tenant's answers, applied only after a second person approves (section 5A).
- The supporting platform: an Assist module, a model-provider adapter, a per-tenant feature switch, audit, evaluation and cost metering.

### Out of scope

- Any AI decision: underwriting, claims, payment, fraud scoring or pricing. These stay deterministic and human-approved.
- AI that writes to records other than a Draft product version (AI-1) or a draft configuration change set for a tenant that isn't live yet (AI-3), each applied only after a second person approves.
- Customer-facing chat, voice or free-form question answering.
- Languages other than English in the first release (open decision D-4).
- Training or fine-tuning a model on tenant data.

## 2. Design principles

Eight principles bind every requirement below; a requirement that breaks one is a defect.

| # | Principle | What it means in INSYS |
| --- | --- | --- |
| P1 | AI drafts, people decide | The AI is the maker; the existing maker-checker controls stay. A drafted product still goes through validate, simulate, submit and a second person's approval before publication. |
| P2 | Grounded in INSYS data | Explanations use only recorded INSYS data: the error code and its catalogue entry, the rating calculation's recorded steps, the published product version. No general knowledge, no guesses. |
| P3 | Numbers are copied, never computed | The model never calculates a premium, limit or rate. Every number in its output must match a number in its input; a deterministic check rejects output that does not. |
| P4 | Every AI output is traceable | Each run records model and version, prompt template version, input references, output, verifier result and the person who accepted or discarded it. |
| P5 | Least data | Only the fields a task needs are sent; identifiers, bank details and names are left out or masked before any call. |
| P6 | Off unless switched on | A new feature entitlement per tenant, off by default; each feature has its own function so roles grant it separately. |
| P7 | Provider-agnostic | All model calls go through one adapter interface, like INSYS's other partner adapters, so the provider can change without touching business logic. |
| P8 | INSYS works without it | If the AI service is down, slow or switched off, every INSYS process still works; the AI panel simply says it is unavailable. |

## 3. Actors, roles and functions

AI Assist adds five functions to the function catalogue and no new roles; tenant administrators grant the functions to existing roles. Function codes follow the catalogue's convention: an upper-case function code that grants a lower-case permission, for example ASSIST\_EXPLAIN\_REFUSAL granting assist\_explain.refusal.

| Actor | Uses | Proposed function |
| --- | --- | --- |
| Product configurator (maker) | Uploads documents, runs AI-1, reviews and edits the draft | `ASSIST_PRODUCT_DRAFT_RUN` |
| Product approver (checker) | Sees the AI provenance report when approving; cannot be the person who ran the draft | Existing Product Factory approval, plus read of the provenance report |
| Operations, underwriting, claims and service staff | Ask "why was this refused?" and "how was this premium calculated?" | `ASSIST_EXPLAIN_REFUSAL`, `ASSIST_EXPLAIN_PREMIUM` |
| Portal users (customers, brokers) | The same explanations on their own records, in a later release | Same functions, behind a separate portal switch (D-3) |
| Tenant administrator | Switches features on, sets limits, grants functions | `ASSIST_SETTINGS_MANAGE` |
| Tenant administrator (onboarding) | Runs the tenant configuration wizard (AI-3) while the tenant is onboarding; cannot approve a setup they ran | `ASSIST_TENANT_SETUP_RUN` |
| Platform operator | Chooses the provider, sets platform caps, monitors cost and quality | Operator-only configuration |
| Auditor | Reads AI run records | Existing audit roles |

- AIA-001: AI-1 and AI-2 shall each require their own function **and** the tenant's `AI_ASSIST` feature entitlement; either missing refuses the request with the standard codes (`FUNCTION_NOT_GRANTED`, `FEATURE_NOT_ENTITLED`).
- AIA-002: Segregation-of-duties rules shall treat the person who ran AI-1 for a product version as its maker, so they cannot approve that version.
- AIA-003: Explanations shall respect the caller's existing scopes (product, branch, relationship); a user who cannot read a record cannot get an explanation of it.

## 4. AI-1 Product Setup Assistant

AI-1 turns product documents into a Draft product version that a configurator finishes and a second person approves. Its value is time: configuration starts from a cited draft instead of a blank form. It adds no new path to publication.

### 4.1 Inputs and output

|  | Detail |
| --- | --- |
| Inputs | PDF, DOCX or XLSX files: policy wording, product brochure or specification, rate tables; the target product line (Life, General or Health) and the product it belongs to |
| Output | A new **Draft** version in Product Factory, a provenance report, and a set of proposed simulation cases |
| Never | A Validated, Submitted, Approved or Published version; changes to an existing published version |

### 4.2 Functional requirements

- AIA-010: The user shall upload one or more documents to a new AI draft run for a chosen product; files are stored through the Documents module and scanned for malware before use. A scanner port (ClamAV, run as a separate service) holds each upload in quarantine until it is clean; if the scanner is unavailable, uploads wait rather than pass (decided 1 November 2026).
- AIA-011: The system shall extract text and tables from the files; scanned images without a text layer shall be reported as "not readable", not guessed.
- AIA-012: The AI shall propose values only for fields that exist in the Product Factory version model for that product line (for example covers or benefits, eligibility ages, waiting periods, sums assured or limits, excesses, premium frequencies, rate tables). The exact field list shall be generated from the model, not hand-written in the prompt.
- AIA-013: Every proposed value shall carry a citation: file, page and the quoted source text. A value without a citation shall be dropped.
- AIA-014: Each value shall be flagged **Found** (quoted directly), **Interpreted** (derived from wording, needs review) or **Missing** (required by the model, not found in the documents).
- AIA-015: Rate tables shall be copied cell for cell; a deterministic check shall compare row and column counts and every value against the extracted source table and reject the table on any mismatch.
- AIA-016: The AI shall not write rule expressions directly. It may propose a rule in plain words with its citation; the configurator writes the expression in the rules language (ADR-008).
- AIA-017: The Draft version shall be created through the same Product Factory API a person uses, under the identity of the user who ran it, so all existing validations, idempotency and audit apply. Because AI-1 runs as a background job, the job acts for the recorded user, with that user's permissions checked when it starts and again when it writes; this is a new pattern and is security-reviewed before it merges.
- AIA-018: The AI shall propose simulation cases (for example youngest and oldest eligible age, each premium frequency, boundary sums assured) with the expected outcome cited from the documents where the documents state one.
- AIA-019: The configurator shall be able to accept, edit or reject each value; edits shall be recorded against the AI's proposal.
- AIA-020: The provenance report (value, flag, citation, accepted / edited / rejected, by whom) shall be attached to the version and shown to the approver at submission.
- AIA-021: Text in uploaded documents shall be treated as data only. Instructions inside a document (for example "ignore previous instructions") shall have no effect, and the AI shall have no tool that changes any record other than the run's own Draft.

### 4.3 Acceptance criteria (proposed targets)

Targets are measured on an evaluation set of real or realistic product documents per line, built before the pilot (section 9). They are proposals for Ade's review, not measured results.

| Measure | Proposed target |
| --- | --- |
| Values flagged Found that match the document exactly | 98% or more |
| Rate table cells copied correctly (after the deterministic check) | 100%; any mismatch blocks the table |
| Required fields missed without being flagged Missing | 0 in the evaluation set |
| Proposed values with a valid citation | 100% |
| Documents with injected instructions that change the output | 0 |
| Drafts that bypass validate, simulate or second-person approval | 0 (by design; tested) |

## 5. AI-2 Plain-language explanations

AI-2 rewrites what INSYS has already recorded into a short explanation a person can act on. It explains a decision; it never makes, changes or reverses one. Staff get it first; portals follow behind a separate switch (D-3).

### 5.1 What can be explained

| Explanation | Grounded in | Example question |
| --- | --- | --- |
| Refusal | The stable error code, its error-catalogue entry, field errors and the record's state at the time | "Why can't I approve this payment?" |
| Premium calculation | The recorded rating calculation: its inputs, each step and the breakdown | "Why is this quote R1,240 a month?" |
| Rule decision (later) | The recorded rule-set evaluation and its decision | "Why was this proposal referred?" |

### 5.2 Functional requirements

- AIA-030: An "Explain" action shall appear next to a refusal message and on a recorded rating calculation, for users holding the matching function.
- AIA-031: Refusal explanations shall be built from the error code, the catalogue's description and remedy, and the refusal the user's screen received (see AIA-039); they shall state what to do next (for example "ask someone else to approve; you created this batch").
- AIA-032: Premium explanations shall be built only from the recorded calculation (Rating's `get calculation`), never from a new calculation or a simulation.
- AIA-033: Every number in an explanation shall appear in the source data; a deterministic verifier shall reject any explanation containing a number, rate or amount not present in the input, and INSYS shall then show the plain structured breakdown instead.
- AIA-033a: The verifier shall compare numbers after normalising them: currency signs, thousands separators and decimal places (R1,240 = 1240.00 = 1 240), percentages written either way (15% = 0.15), and dates as dates. List numbers, step numbers and years that appear in the source shall not trigger a rejection. The rules and their test cases shall be fixed in the addendum before build.
- AIA-034: Each explanation shall show a "Based on" link to the recorded data it used and the line "AI-generated explanation of a recorded INSYS decision".
- AIA-035: Explanations shall not include personal data beyond what the requesting user can already see on that record, and shall not include internal-only fields in portal channels.
- AIA-036: Identical inputs shall reuse a stored explanation (cache keyed on the calculation or error instance and prompt version) to cut cost and keep answers consistent.
- AIA-037: Users shall be able to mark an explanation as unhelpful or wrong; the mark and reason shall be stored for evaluation.
- AIA-038: Health explanations shall carry no clinical detail beyond what the existing Health notices allow (HLT-008 restrictions apply).
- AIA-039: The console shall send, with an Explain request, the refusal it received: the error code, field errors, the record reference and the correlation ID. The Assist module shall rebuild the context from the error catalogue and from the record as that user may read it (AIA-003); it shall never trust record data sent by the screen. The correlation ID is recorded with the run for audit and is not used to look the refusal up, because INSYS does not record business-rule refusals centrally (section 10.3). This keeps AI-2 out of the request pipeline every call passes through.

### 5.3 Acceptance criteria (proposed targets)

| Measure | Proposed target |
| --- | --- |
| Explanations with a number not found in the source | 0 shown (verifier blocks them) |
| Explanations reviewed as correct and complete by a domain expert on the evaluation set | 95% or more |
| Explanations a reviewer rates easy to understand | 90% or more |
| Explanations that reveal data the user cannot otherwise see | 0 |

## 5A. AI-3 Tenant configuration wizard

AI-3 drafts a new tenant's whole configuration, its static data and base environment settings, from reviewed starter packs and a short questionnaire. Onboarding then starts from a near-complete draft instead of nearly 300 empty tables. People review it, a second person approves it as one change, and the tenant can't go live until onboarding readiness passes.

### 5A.1 Starter packs: templates first, AI second

| Layer | Holds | Scope |
| --- | --- | --- |
| Base pack | Operational defaults: code lists, statuses, task routing, role templates, approval structures, billing and arrears mechanics | One, country-neutral |
| Country overlay | Regulated and local values: currency and its minor unit, tax, identity document types, data protection and AML settings, retention, disclosure texts and language, public holidays | One per country |
| Line overlay | Life, General or Health specifics: grace periods, claim rules, benefit structures | One per line, varied by country where the law differs |

- **Country order (D-9):** Nigeria, Rwanda, Ghana, Senegal, Kenya, DRC, Côte d'Ivoire. Senegal and Côte d'Ivoire share a CIMA regional overlay with a thin national layer; the DRC, outside CIMA, has its own. The current South African values are kept as a reference overlay.
- **Regulated values come only from an approved overlay.** The AI never invents a tax rate, disclosure text, retention period or AML setting for a live tenant. A draft overlay value is marked for review until a local compliance partner (D-10) approves the overlay's version.

### 5A.2 Inputs and output

|  | Detail |
| --- | --- |
| Inputs | A questionnaire: countries, currencies, time zone, lines of business, products, sales channels, payment methods, team roles and approval limits, languages. Optionally the tenant's own documents: chart of accounts, approval matrix, role list, code lists |
| Output | A draft configuration change set across the static data tiers and environment settings, a readiness report on the draft, and a provenance report |
| Never | An approved or in-force configuration without a second person's approval; a change to an active tenant's in-force configuration outside the normal change process |

### 5A.3 Functional requirements

- AIA-080: The wizard is available during onboarding to holders of a new function, ASSIST\_TENANT\_SETUP\_RUN (the tenant administrator or the platform operator's onboarding team), with the tenant's AI\_ASSIST entitlement for its AI steps.
- AIA-081: Questionnaire answers are recorded and versioned; changing an answer re-drafts only the values it affects.
- AIA-082: The draft is assembled deterministically from the base pack and the selected overlays. The AI chooses and tailors only values a pack marks as tailorable. A regulated value with no approved overlay is left Missing and flagged, never filled by the AI.
- AIA-083: The AI maps the tenant's uploaded documents (chart of accounts, approval matrix, role list) onto INSYS tables, citing the document, page and text for each mapped value, and lists what it couldn't map.
- AIA-084: Every value is flagged Pack, Tailored (AI, cited to the answer), Mapped (AI, cited to the document) or Missing.
- AIA-085: Before approval, a deterministic completeness check shows that no value is Missing and that every regulated value comes from an approved overlay. After approval, the set is applied and onboarding readiness runs on the tenant with the same checks as live onboarding; the tenant can't go live until they pass, and onboarding steps completed before the set was applied run again (decided 1 November 2026). Blockers and warnings are explained in plain language under AI-2's verifier rules.
- AIA-086: The approved draft is applied as one SETUP change set: one import batch per table through the existing import path (BR-016), with one approval by a second person bound to a hash of every batch, applied in dependency order and resumable. It is refused unless the tenant is still onboarding; live tenants keep per-table imports, and the tenant can't go live while a set is partly applied (decided 1 November 2026). The person who ran the wizard can't approve it.
- AIA-087: Numbers and codes are copied, never computed: every rate, limit and amount comes from a pack or a cited tenant input, and a deterministic check rejects anything else.
- AIA-088: The questionnaire and documents are business configuration. Any personal data found in an upload is masked before a model call (AIA-051).
- AIA-089: The pack versions used are recorded with the tenant. A later pack version is offered as a reviewable change, never applied automatically.
- AIA-090: With the AI off or unavailable, the wizard still assembles the pack-based draft and runs readiness; only tailoring and document mapping are unavailable (P8).
- AIA-091: Overlays carry their own language texts (French for the CIMA countries, the DRC and Rwanda), and the wizard's screens are translatable.

### 5A.4 Acceptance criteria (proposed targets)

| Measure | Proposed target |
| --- | --- |
| Regulated values not taken from an approved overlay | 0 |
| Mapped values with a valid citation | 100% |
| Mapped values correct on the evaluation set | 95% or more |
| Readiness blockers left, other than facts only the tenant can give | 0 for a standard questionnaire |
| Drafts applied without a second person's approval | 0 (by design; tested) |
| Questionnaire to a complete draft ready for approval | Under 30 minutes |

### 5A.5 Data and API (summary)

- **Tables (schema `assist`):** `setup_run`, `setup_answer`, `setup_item` (value, flag, source or citation, decision). Packs are versioned static data: `starter_pack`, `starter_pack_value`.
- **API:** `POST /assist/tenant-setups`; `GET /assist/tenant-setups/{id}`; `PUT /assist/tenant-setups/{id}/answers`; `POST /assist/tenant-setups/{id}/items/{itemId}/decision`; `POST /assist/tenant-setups/{id}/submit`, which creates the static data change set for approval.
- **New error codes (proposed):** `ASSIST_PACK_NOT_APPROVED`, `ASSIST_SETUP_NOT_READY`.

## 6. Non-functional requirements

The hard constraint is where model inference runs: each tenant's data is governed by its own jurisdiction's law, and an AI call may send it elsewhere. Section 6.1 sets the rules; the choice of option is decision D-1.

### 6.1 Privacy, data protection law and data residency

What POPIA says (verified from the published section text; legal interpretation for compliance to confirm):

- **Section 72** allows transfer of personal information to a third party in a foreign country only on one of five grounds, including a law, binding corporate rules or binding agreement giving adequate protection, or the data subject's consent ([s72](https://popia.co.za/section-72-transfers-of-personal-information-outside-republic/)).
- **Section 71** bars decisions with legal or substantial effect based solely on automated processing intended to profile a person, with exceptions; where an exception applies, the person must be able to make representations and be given enough information about the underlying logic ([s71](https://popia.co.za/section-71-automated-decision-making/)).

What this means for AI Assist (inference, for compliance to confirm):

- Neither feature makes a decision about a person, so section 71's bar is not engaged by AI Assist itself. AI-2 may help tenants give the "sufficient information about the underlying logic" section 71(3) asks for where INSYS rules make automated decisions.
- AI-1 processes product documents, which normally hold no personal information; offshore inference is lower risk but documents shall still be checked for personal data.
- AI-2 premium explanations use rating factors (for example age, smoker status or health loadings) that can be personal or special personal information. They shall be sent without names, identifiers or policy numbers.

Requirements:

POPIA is the South African case. Each target market has its own data protection law with its own rules on sending personal data abroad (for example, Nigeria's Data Protection Act 2023). The legal basis for each jurisdiction is confirmed by compliance with that country's overlay (D-10, D-13), and nothing in this specification assumes that INSYS is hosted in South Africa.

- AIA-050: Each tenant setting shall record the tenant's jurisdiction, where inference runs, and the legal basis the tenant relies on under that jurisdiction's data protection law (POPIA in South Africa, the Nigeria Data Protection Act 2023 in Nigeria, and so on); AI-2 shall not run outside the tenant's jurisdiction without that basis recorded. Nothing assumes South African hosting; where Nigerian tenants are hosted is still to be decided (decided 1 November 2026).
- AIA-051: Before any call, a minimisation step shall remove or mask names, ID numbers, contact details, bank details and policy, claim or member numbers; the payload actually sent shall be stored with the run record.
- AIA-052: The provider contract shall exclude use of INSYS data for model training and set a retention limit for prompts and outputs at the provider.
- AIA-053: Provider credentials shall be held in the platform's secret store, never in tenant configuration or logs.

### 6.2 Security

- AIA-060: The model shall have no tools that write, approve, pay or send; AI-1 writes its Draft through INSYS's own API under the user's identity (AIA-017).
- AIA-061: Uploaded text and recorded data shall be passed as clearly delimited data; tests shall include documents with embedded instructions (prompt injection).
- AIA-062: The Assist module shall follow INSYS's tenant isolation: its own schema and role, forced row-level security, tenant from the token only.

### 6.3 Quality, performance and cost

| Requirement | Proposed target |
| --- | --- |
| AIA-070 AI-2 response time | 10 seconds or less at the 95th percentile; cached answers under 1 second |
| AIA-071 AI-1 run time | Runs as a background job; the user is notified on completion; target under 15 minutes for a 60-page document set |
| AIA-072 Availability | Best effort; INSYS processes never wait on the AI (P8) |
| AIA-073 Cost control | Per-tenant monthly token or call cap set by the operator; calls refused with a clear code once reached; usage reported per tenant for billing |
| AIA-074 Versioning | Model and prompt template versions are configuration; a change goes through the evaluation set before release |
| AIA-075 Monitoring | Verifier rejection rate, unhelpful marks, latency, cost and provider errors tracked per tenant, with alarms |

## 7. Architecture and data

AI Assist is one new module in the modular monolith plus one partner adapter; existing modules gain read hooks only. It reads recorded data, writes a Draft product version through the normal API, and audits every run.

&#91;embedded content: AI Assist architecture · one new module, one adapter\]

The model never touches the database or another module directly: the Assist module minimises the data, calls the provider through the adapter, verifies the answer and records the run.

### 7.1 Components

| Component | Responsibility |
| --- | --- |
| Assist module | Own schema and role with forced row-level security; runs, items, explanation cache, tenant settings; background job for AI-1 |
| `IAiModelProvider` adapter | One interface for model calls (send prompt and data, receive text or structured output); a stub for tests, like INSYS's other partner adapters |
| Minimiser | Removes or masks personal data before any call (AIA-051) |
| Prompt templates | Versioned text templates held as configuration (AIA-074) |
| Verifier | Deterministic checks: numbers present in input, citations resolve to real text, rate tables match cell for cell |
| Feature `AI_ASSIST` and five functions | Entitlement and access, using the existing request checks |

### 7.2 Data (proposed tables in schema `assist`)

| Table | Holds |
| --- | --- |
| `run` | Tenant, feature, user, start and end, model and version, template version, status, token counts, cost |
| `run_payload` | The minimised payload sent and the raw output, retained per the tenant's retention policy |
| `draft_item` | AI-1 proposed value, field path, flag (Found / Interpreted / Missing), citation, user decision |
| `explanation` | AI-2 text, source reference (calculation or correlation ID), verifier result, feedback |
| `tenant_setting` | Enabled features, residency choice and legal basis, monthly cap |

### 7.3 Proposed API

Conventions follow the INSYS API: bearer token, idempotency keys on creates, ETags on changes, ProblemDetails with stable codes.

| Operation | Purpose |
| --- | --- |
| `POST /assist/product-drafts` | Start an AI-1 run for a product with uploaded document IDs; returns 202 and the run ID |
| `GET /assist/product-drafts/{runId}` | Status, the Draft version ID, and the provenance report |
| `POST /assist/product-drafts/{runId}/items/{itemId}/decision` | Accept, edit or reject one proposed value |
| `POST /assist/explanations/refusals` | Explain a refusal the caller received: code, field errors, record reference, correlation ID (AIA-039) |
| `POST /assist/explanations/premiums` | Explain a recorded rating calculation by its ID |
| `POST /assist/explanations/{id}/feedback` | Mark helpful or wrong, with a reason |
| `GET` / `PUT /assist/settings` | Tenant settings (administrator) |

New error codes (proposed): `ASSIST_UNAVAILABLE` (retryable), `ASSIST_CAP_REACHED`, `ASSIST_RESIDENCY_NOT_SET`, `ASSIST_SOURCE_UNREADABLE`, `ASSIST_OUTPUT_REJECTED`.

## 8. Step-by-step guide: how the AI automation runs

In all three features the automated part is short and boxed in: INSYS prepares minimised data, the model returns text, INSYS checks it, and a person uses or discards it.

### 8.1 AI-1 Product Setup Assistant

&#91;embedded content: AI-1 Product Setup Assistant · who does what, 9 steps\]

The model appears in one lane only; everything it produces passes INSYS's checks and two people before a product goes live.

1. **Configurator uploads documents.** In Product Factory, they choose the product and line, upload the wording, brochure and rate tables, and start an AI draft. INSYS stores the files and starts a background job.
2. **INSYS extracts and minimises.** Text and tables are extracted page by page; unreadable pages are listed. Any personal data found (for example a sample policyholder) is masked. INSYS builds the field list from the Product Factory model for that line.
3. **The model proposes values.** It receives the field list and the extracted text as data, and returns a value, a flag (Found, Interpreted or Missing) and a quoted citation for each field. It has no tools and cannot write anything.
4. **INSYS verifies and creates the Draft.** Each citation must match the source text; rate tables must match cell for cell. Failing values are dropped and listed. Passing values are written to a new Draft version through the normal Product Factory API, as the configurator, and the run is recorded. The configurator is notified.
5. **Configurator reviews.** Side by side with the source page, they accept, edit or reject each value, fill Missing fields and write any rule expressions. Interpreted values cannot be left unreviewed.
6. **Configurator validates and simulates.** Product Factory's existing validation runs; the AI's proposed simulation cases run alongside the configurator's own. Failures go back to step 5.
7. **Configurator submits.** The provenance report goes with the submission into Workflow.
8. **Approver decides.** A different person, with approval authority, reviews the version and the provenance report. Rejection returns it to step 5 with reasons.
9. **INSYS publishes after approval.** Publication follows Workflow approval exactly as for a hand-built product.

### 8.2 AI-2 Plain-language explanations

1. **A user meets a refusal or opens a premium calculation** and selects "Explain". INSYS checks their function, the tenant's `AI_ASSIST` entitlement and their scope on the record.
2. **INSYS gathers recorded data only.** For a refusal: the error code and field errors the screen received, the code's catalogue entry, and the record's state as the user may read it (AIA-039). For a premium: the recorded calculation's inputs, steps and breakdown.
3. **INSYS checks the cache.** If the same source and prompt version were explained before, the stored explanation is shown and no call is made.
4. **INSYS minimises and calls the model.** Names, identifiers and numbers that identify a person are removed; the residency setting decides which provider is used, and AI-2 is refused if no legal basis is recorded for an offshore provider.
5. **The model writes the explanation** in plain English, with a next step, from the data supplied.
6. **INSYS verifies.** Every number in the text must appear in the source; otherwise the explanation is discarded and the structured breakdown shown instead.
7. **The user sees the explanation** with a "Based on" link to the recorded data and the AI label, and can mark it helpful or wrong.
8. **INSYS records the run** (model, prompt version, payload, output, verifier result, feedback) for audit and evaluation.

### 8.3 AI-3 tenant configuration wizard, step by step

1. The tenant administrator, holding ASSIST\_TENANT\_SETUP\_RUN, starts a setup run for a tenant that is still onboarding and answers the questionnaire.
2. INSYS assembles the base pack with the chosen country and line overlays, and flags those values Pack. This step needs no AI (AIA-090).
3. If AI Assist is switched on, the AI tailors the values a pack marks as tailorable and maps the tenant's uploaded documents onto INSYS, citing each source, and flags them Tailored or Mapped. Uploads are scanned for malware first. Regulated values come only from an approved overlay; anything else stays Missing.
4. The verifier checks that every value traces to a pack or a cited tenant input, and drops and lists any that do not.
5. A completeness check confirms that nothing is Missing and that every regulated value comes from an approved overlay.
6. The administrator reviews the draft and submits it as one SETUP change set.
7. A second person approves it, bound to a hash of every batch. Whoever ran the wizard cannot approve it.
8. INSYS applies the set to the tenant in dependency order, resuming if interrupted. Onboarding readiness then runs, including any steps completed before the set was applied, and blockers are explained in plain language.
9. The tenant can be activated only when readiness passes and no SETUP set is waiting or partly applied.

### 8.4 When something goes wrong

| Situation | What happens |
| --- | --- |
| Provider down or slow | Request ends with `ASSIST_UNAVAILABLE`; the user carries on without AI; no INSYS process waits |
| Monthly cap reached | `ASSIST_CAP_REACHED`; administrators are alerted |
| Verifier rejects output | AI-1 drops the value and lists it as Missing; AI-2 shows the structured breakdown |
| Document cannot be read | Listed as unreadable; no values guessed from it |
| Feature switched off mid-run | Running AI-1 jobs finish; nothing new starts |

## 9. Delivery roadmap

Delivery is six phases, each closed by a gate with a measurable test. AI-2 goes first because it uses only recorded data and is the quickest to evaluate; the back end has sized the build in its Wave 7 addendum (table below), and AI-3 adds phase 3A, placed by D-8.

&#91;embedded content: Delivery roadmap · 7 phases, 6 gates, not to scale\]

No code is written before the hosting decision, and each feature reaches users only after its acceptance targets are met on an evaluation set.

**Build window (D-6, agreed 30 October 2026).** AI Assist is built as back-end Wave 7, after Wave 6, with AI-2 first and AI-1 second. The front-end screens (the Explain action, the AI draft review and the settings page) come before front-end Phase 13, so the final security, accessibility and performance review covers them. It ships switched off and is turned on per tenant once its acceptance targets are met.

| When | What | Effect on the core build |
| --- | --- | --- |
| Now | D-1 provider check, evaluation documents for one line (D-7), sign-off of this SRS | None |
| While Waves 5b and 6 build | Back end turns this SRS into the Wave 7 addendum for sign-off | None |
| After Wave 6 | Back-end Wave 7: AI-2, then AI-1; AI-3 placed by D-8 | Sized by the back end at 62 to 82 person-days for one engineer (AI-2 and AI-1 alone: 40 to 52); calendar time re-estimated after batch 7.1. The earlier figure of 2 to 4 days was wrong. |
| Before front-end Phase 13 | Front-end AI Assist screens | To be sized by the front end (the earlier 1 to 2 days was not measured) |
| Go-live | Shipped off; switched on per tenant after evaluation | None |

### 9.1 How the build would be run

- **Instructions to the sessions** would come only after this SRS is approved: the back end builds phases 1 to 3 as its own small batches with the usual contract updates; the front end adds the Explain action, the AI draft review screen and the settings page against the pinned contract.
- **Evaluation sets** are built before the features they test: about 100 recorded refusals and premium calculations across lines for AI-2, and real product documents for one line for AI-1 (D-7). Each item has an expert-checked expected result. The sizes are proposals.
- **Tests in CI** use the provider stub, so builds never call a paid model; evaluation runs against the real provider are a separate, manually started job.
- **The decisions log** records D-1 to D-7 and any change to the targets.

### 9.2 Build safeguards: adding AI Assist without breaking the core

The design already isolates AI Assist (its own module and schema, read-only hooks, off by default, drafts created through Product Factory's own API as the user). These rules make that hold in the build:

- **No real model in CI.** Every automated test uses the provider stub, so builds stay deterministic and cost nothing.
- **The existing suites run with the feature off.** A separate suite runs with it on, against the stub.
- **No behaviour change to existing modules.** Existing modules gain read hooks only; the module-boundary architecture tests enforce this.
- **The existing guards apply automatically.** Every permission checked is granted by a function, every portal route declares its actors, and every channel-checked command declares its function; the new module is checked like any other.
- **Maker-checker needs little new code.** The Draft is created under the user's identity (AIA-017), so Product Factory already treats that user as the maker and refuses them as approver (AIA-002). The one new piece is the background job acting for the recorded user, which is security-reviewed (AIA-017).
- **Refusal explanations stay out of the request pipeline.** The screen sends the refusal it received (AIA-039), so no change is needed to the code every request passes through.
- **Merged like any batch.** Wave 7 merges only on green CI of the exact commit, with its own locked end-to-end run.

## 10. Risks, open decisions and sources

Hosting is decided (31 October 2026): Claude is the AI provider (D-1). Its inference runs globally or in the US, not in South Africa, so AI-2 waits for compliance to confirm the legal basis in each tenant's jurisdiction (POPIA s72 in South Africa), while AI-1 holds no personal data and can use it once built. INSYS is cloud-neutral (ADR-027, proposed 30 October 2026), and Claude is reachable from every target cloud; the provider stays swappable behind the adapter (P7).

### 10.1 Risks

| Risk | Effect | Mitigation |
| --- | --- | --- |
| AI misreads a product document | Wrong value in a draft product | Citations on every value; Found / Interpreted / Missing flags; validate, simulate and second-person approval unchanged |
| AI states a wrong number in an explanation | Staff or customer misled | Deterministic number verifier (AIA-033); fall back to the structured breakdown |
| Personal data leaves the tenant's jurisdiction without a basis | Breach of that jurisdiction's transfer rules (POPIA s72 in South Africa) | Minimisation (AIA-051); per-tenant residency setting and legal basis (AIA-050); AI-2 off until recorded |
| Prompt injection in an uploaded document | Manipulated draft | Data-only handling, no write tools, injection tests (AIA-021, AIA-060, AIA-061) |
| Provider price or model change | Cost rise or quality drop | Adapter (P7); evaluation before any model change (AIA-074); per-tenant caps (AIA-073) |
| Distraction from the core build | Core waves slip | Separate small track, started only after D-6; no change to existing modules beyond hooks |
| Over-trust by users | People stop checking drafts | Approver sees the provenance report; Interpreted values need explicit acceptance |

### 10.2 Open decisions for Ade

| ID | Decision | Options | Recommendation |
| --- | --- | --- | --- |
| D-1 | Where inference runs | (a) a model service hosted in South Africa, if one with adequate quality exists on the cloud a deployment runs on (Huawei, Alibaba Cloud through BCX, AWS, Azure or Google) or elsewhere; (b) an offshore provider under a binding agreement (POPIA s72(1)(a)) with minimised data; (c) a self-hosted open-weight model in South Africa | Decided 31 Oct 2026 (Ade): Claude, through the Claude API or the deployment's cloud (Amazon Bedrock, Google Cloud or Microsoft Foundry), behind the provider adapter. AI-1 uses it once built, since product documents hold no personal data. AI-2 uses it once compliance confirms the legal basis in each tenant's jurisdiction (POPIA s72 in South Africa) (Anthropic's data processing agreement, with the minimisation in AIA-051) and the contract sets zero data retention and no training (AIA-052). Claude inference runs globally or in the US, not in South Africa (section 10.3), so an in-country model (option a or c) stays a swappable fallback for any customer or regulator that requires in-country processing. |
| D-2 | Which feature first | AI-1 first; AI-2 first; both together | Agreed 30 Oct 2026: AI-2 for staff first; smaller, uses recorded data only, quick to evaluate |
| D-3 | Portal explanations | With staff release; later release | Agreed 30 Oct 2026: later, after staff feedback and legal review |
| D-4 | Languages | English only; English plus others | Agreed 30 Oct 2026: English only for the first release |
| D-5 | Commercial model | Included in subscription; priced add-on; pass-through usage | Open: a product decision; usage metering (AIA-073) supports any option |
| D-6 | Timing | Now in parallel; after core waves | Agreed 30 Oct 2026: build as back-end Wave 7 after Wave 6, front-end screens before Phase 13, shipped off (section 9) |
| D-7 | Product lines for the AI-1 evaluation set | Life, General, Health | Open: start with one line where real documents are available; Ade to name the line |
| D-8 | Build order of AI-3 | Before AI-1; after AI-1 | Open. Recommended: AI-2, then AI-1, then AI-3, because AI-3 needs approved starter packs and their compliance review takes time. The pack-only path (AIA-090) can ship earlier if a first customer needs it. |
| D-9 | Country order for starter packs | Any order of target markets | Decided 1 Nov 2026 (Ade): Nigeria, Rwanda, Ghana, Senegal, Kenya, DRC, Côte d'Ivoire; a CIMA overlay shared by Senegal and Côte d'Ivoire; South Africa kept as a reference overlay. |
| D-10 | Who approves each country overlay | A local compliance partner per country; OpenFactor's own compliance lead | Open. Recommended: a local compliance partner per country, engaged before that country's first customer. |
| D-11 | How AI-3's setup is applied | (a) one table at a time under BR-016; (b) one SETUP change set over all the tables | Decided 1 Nov 2026 (Ade): (b), only for a tenant still onboarding (AIA-086). |
| D-12 | How AI-3's draft is checked for readiness | (a) load it into a temporary shadow tenant before approval; (c) a completeness check before approval, then full readiness on the tenant after applying, with go-live blocked until it passes | Decided 1 Nov 2026 (Ade): (c) (AIA-085). |
| D-13 | Data protection outside South Africa | A South Africa-only setting, or each tenant's own jurisdiction and legal basis | Decided 1 Nov 2026 (Ade): each tenant's jurisdiction, inference location and legal basis (AIA-050). Hosting for Nigerian tenants is still open. |
| D-14 | Malware scanning of uploads | (a) build a scanner port; (b) accept staff uploads unscanned at first | Decided 1 Nov 2026 (Ade): (a), for all uploads, failing closed (AIA-010). |

### 10.3 What was checked, and what was not

- **Verified:** POPIA sections 71 and 72 text (popia.co.za); AWS's statement that Bedrock global cross-Region inference from Cape Town (af-south-1) may process requests in other Regions while logs stay in af-south-1, and that it is recommended for use cases without data residency needs (AWS blog, 30 January 2026). In INSYS's own code (main branch, 30 October 2026): Rating stores each calculation's steps and returns them (GET /rating/calculations/{id}), so AI-2 premium explanations have recorded data to use; access denials are audited with their correlation ID; business-rule refusals are not recorded centrally, which is why AIA-039 has the screen send the refusal it received. Anthropic's data residency documentation (read 31 October 2026): the Claude API runs inference globally (the default) or US-only, with no African option; Amazon Bedrock, Google Cloud and Microsoft Foundry set the region by endpoint; the API is eligible for zero data retention.
- **Verified only in part:** Huawei Cloud announced Pangu Models 5.5 for South Africa on 4 July 2025; the release does not say which AI services run in the Johannesburg region. ITWeb (29 May 2026) reports Huawei Cloud offering its MaaS model service, with DeepSeek, GLM and Qwen, to South African developers, but does not say where the models run.
- **Not verified:** availability of GPU instances or hosted model services in Huawei Cloud's Johannesburg region; provider prices; any latency figure. All targets in this document are proposals.

### 10.4 Sources

- [POPIA section 72: Transfers of personal information outside the Republic](https://popia.co.za/section-72-transfers-of-personal-information-outside-republic/)
- [POPIA section 71: Automated decision making](https://popia.co.za/section-71-automated-decision-making/)
- [AWS Machine Learning Blog: Scale AI in South Africa using Amazon Bedrock global cross-Region inference with Claude 4.5 models (30 January 2026)](https://aws.amazon.com/blogs/machine-learning/scale-ai-in-south-africa-using-amazon-bedrock-global-cross-region-inference-with-anthropic-claude-4-5-models)
- [Huawei Cloud news release, 4 July 2025: Pangu Models 5.5 in South Africa](https://www.huaweicloud.com/intl/en-us/news/20250704192254304.html)
- [ITWeb, 29 May 2026: Huawei Cloud introduces MaaS to SA developers](https://www.itweb.co.za/article/huawei-cloud-introduces-maas-to-sa-developers/j5alrvQADy8vpYQk)
- [Claude Platform docs: data residency](https://platform.claude.com/docs/en/build-with-claude/data-residency)
- INSYS internal: Product Appreciation Document (Technical), chapters 15 and 16; decisions log.
