# Addendum: cases: enquiries, complaints, disputes and appeals (Wave 2: F010–F013, F032, F033, F050, F061, F066, F068–F073)

**Signed off (25 October 2026, Ade), with these decisions; being built in Wave 2 batch 2.**
- **OQ-101:** both regimes, by configuration: each case type names its appeal route (`appeal_route`: `PPR_NFO` for insurer tenants, `CMS` for medical-scheme tenants).
- **OQ-102:** a partial approval is its own claim outcome (settled below the claimed amount, with a required reason); the declined part sends `CLAIM_DECISION_RIGHTS`.
- **OQ-103:** Life and Health claims gain a reopen, for upheld disputes.
- **OQ-104:** redress is paid through Payments, with maker-checker.
- **OQ-105:** open, for compliance.
- **OQ-106:** case records are kept 5 years after closure, configurable (`workflow.cfg_case_retention`); a legal hold overrides it; for compliance to confirm.
- **OQ-107:** no confirmation step: the acknowledgement to the complainant is the confirmation.
- **OQ-108:** a broker sees only the cases it raised, unless staff mark a case shared with the broker (`shared_with_broker`).
- **OQ-109:** privileged: `CASE_DECIDE_APPEAL` (deciding an appeal), `CASE_RECORD_REDRESS`, `CASE_REOPEN` (of a closed complaint).
- **OQ-110:** the case checks join `SHARED_OPERATIONAL_SETUP`; no new onboarding step.

- **Basis:**
  - The Workflow module as built: process definitions, tasks and queues, SLAs on business calendars, escalation rules, case notes (SRS workflow and case management; OQ-048, OQ-049).
  - ADR-022 (park on missing configuration), ADR-025 (business dates), ADR-026 (functions), `addenda/external-user-access.md`, `addenda/access-scopes.md` (F103), `addenda/communications.md` (notices).
  - South Africa's Policyholder Protection Rules (PPR) under the Long-term and Short-term Insurance Acts, Rules 17 and 18; the National Financial Ombud Scheme (NFO) Rules; the Medical Schemes Act 131 of 1998, sections 29, 47, 48 and 50; the FAIS General Code of Conduct, Part XI. Everything §6 summarises from them is **for compliance to confirm**; sources are listed there.
- **Built today:**
  - **Workflow** runs processes (`workflow.process_instance`) whose states come from a definition, with a task per state routed to a queue, an SLA per task type computed on a business calendar, SLA-breach escalation, and case notes on any record.
  - **Static data** already holds `workflow.cfg_sla_definitions`, `workflow.cfg_escalation_rules`, `workflow.cfg_task_types_and_work_queues`, `workflow.cfg_workflow_definitions` and `workflow.ref_business_calendars`.
  - **Claims:** General claims can be declined, closed and reopened; Life claims declined; Health claims rejected and reversed. A settlement below the claimed amount works but is not called a partial approval (F010). Reopening exists for General only (F013).
  - **Claims Framework** logs a claim's communications; Communications sends notices from events and keeps proof of consent.
  - **Not built:** any complaint, enquiry, dispute or appeal record, table, aggregate or function. `DISPUTES_APPEALS` and `CUSTOMER_SERVICE` are sub-domains with no functions.
- **What the specifications do not say:** none of these 14 functions has a requirement in the SRS (`functional-coverage.md`: "Not in the SRS" or "Not specified"; `requirements.json` has none). What follows is a proposal built on the Workflow capability the SRS does specify, shaped by the PPR.

## 1. One case capability, in Workflow
- **A case** is a record a person raises or staff open: an enquiry, a complaint, a dispute or an appeal. It belongs to the Workflow module (schema `workflow`), which already runs the work behind it. No new module.
- **One shape for every kind.** Each case has a **case type** (configuration, §3) in one of four families, which decide its rules:

  | Family | Functions | Examples of case types |
  |---|---|---|
  | `ENQUIRY` | F066 | Policy enquiry, claim enquiry, broker enquiry |
  | `COMPLAINT` | F061, F068–F070 | Customer complaint (PPR Rule 18) |
  | `DISPUTE` | F011, F033, F050, F071 | Claim dispute (representations under PPR 17.6.3(b)), customer dispute, provider claim dispute, provider payment dispute |
  | `APPEAL` | F012, F032, F072 | Internal appeal or review of a complaint decision (PPR 18.6), claim appeal, Health claim appeal |

- **The aggregate `ServiceCase`** (`workflow.service_case`) holds what the case is about; the process holds where it is:
  - `case_number` (tenant sequence, as claim numbers), `case_type_code`, `family`, `received_at`, `received_channel` (`communication.ref_channels`, plus `PORTAL`, `PHONE`, `WALK_IN`, `LETTER`, `OMBUD`);
  - the complainant: a party (`party_id`), or a name and contact for someone not yet a party; `on_behalf_of_party_id` when a broker or representative raises it (PPR 18.1 counts a person acting for a complainant);
  - `category_code` (§3; complaints must have one, PPR 18.5.3) and a free-text summary;
  - `reportable` (computed, §6.2), `status`, `outcome` (§4), `decision_reasons`, `decided_by`, `decided_at`;
  - `parent_case_id` for an appeal of a case's decision, so the chain is one history;
  - `product_code` and `org_node_code` from the first linked record, for F103 scope.
- **Links** (`workflow.case_link`): the records a case is about, each `(link_type, record_id, reference)`: `POLICY` (General or Life), `CLAIM` (claim number, any domain), `MEMBERSHIP`, `PAYMENT`, `INVOICE`, `BILLING_ACCOUNT`, `PARTY`, `PROVIDER`, `REMITTANCE`, `INTERMEDIARY`. The reference is the record's own number, for display. Links are identifiers only (ADR-001: no foreign keys across schemas); the case's owning module checks a link through the record's module's public query when it is added.
- **History** (`workflow.case_event`): every step with who and when: received, acknowledged, assigned, updated, communication sent or received, extended, escalated, decided, redress recorded, referred to an ombud, ombud outcome, reopened, withdrawn, closed. Notes stay in `workflow.case_note`; documents in Documents, linked by business reference `CASE`.
- **The process behind it:** opening a case starts its case type's process (`process_code`), so the existing tasks, queues, SLAs on business calendars and escalation rules do the routing. The case's status follows the process (§4).
- **Events** (identifiers only, INT-007): `workflow.CaseOpened`, `CaseAcknowledged`, `CaseEscalated`, `CaseDecided` (outcome, family, links), `CaseReferredToOmbud`, `CaseClosed`, `CaseReopened`.

## 2. What other modules do with cases
- **Claims (F011, F012):** a claim dispute or appeal links its claim. When it is decided *upheld* or *partly upheld*, the claim's module consumes `CaseDecided` and opens a reassessment: General reopens the claim (as today); Life and Health gain a reopen for that purpose (F013, OQ-103). The claim's own decision stays where it is; the case records why it changed.
- **Health (F032, F033, F050):** a Health claim appeal links the medical claim; a provider claim dispute links the provider and the claim; a provider payment dispute links the remittance or payment run. Provider screens use them from Wave 5a; the capability and case types come now.
- **Communications:** notices from case events (§7): acknowledgement, progress, delay with reasons, decision with reasons, escalation and ombud details. The claim's communications log (Claims Framework) also records notices about a claim's case.
- **Payments (redress):** a compensation or goodwill payment decided on a complaint is recorded on the case (amount, type) and, when it is to be paid, requested from Payments as a payment out with maker-checker, as today's payments out. Whether INSYS pays it or only records it is OQ-104.
- **Analytics and Compliance (F073):** case reporting is served by Workflow (§8); a regulatory return can draw on its figures later.

## 3. Configuration (static data)
Every timeline is configuration that compliance sets for the tenant (§6). The platform seeds no durations.

| Table | Class, scope, tier | Fields | Notes |
|---|---|---|---|
| `workflow.cfg_case_types` | CFG, T, 1 | `case_type_code`, `family`, `process_code`, `default_queue_code`, `link_types` text[] (allowed), `portal_audiences` text[] (`CUSTOMER`, `BROKER`, `PROVIDER`: who may raise it in a portal), `notification_types` (per step), `requires_category`, `review_case_type_code` (the appeal type its decisions go to) | A complaint type must name a review type (PPR 18.6) |
| `workflow.cfg_case_timelines` | CFG, T, 1 | `case_type_code`, `step` (`ACKNOWLEDGE`, `RESOLVE`, `DECIDE_REPRESENTATIONS`, `NOTIFY_DECISION`, `REVIEW`, `REDRESS`), `duration` (ISO 8601), `basis` (`BUSINESS`, on the calendar, or `CALENDAR`), `calendar_code`, `extension_allowed`, `max_extension` | Empty by default; each step becomes a timer and an SLA |
| `workflow.ref_case_categories` | REF, GT, 2 | `category_code`, `family`, `ppr_category` (the nine of PPR 18.5.1), `label` | The nine PPR categories ship global; tenants add their own, each mapped to one of the nine (18.5.2–18.5.3) |
| `workflow.cfg_case_reportability` | CFG, T, 1 | `case_type_code`, `query_window` (ISO 8601 business duration) | The window within which a complaint resolved through the ordinary query process is not reportable (PPR 18.1: 5 business days, for compliance to set) |
| `workflow.ref_ombud_schemes` | REF, GT, 2 | `scheme_code` (`NFO`, `FAIS_OMBUD`, `CMS`), `name`, `contact`, `referral_information` (text shown to complainants), `families` | The details every rejection notice and portal decision shows (PPR 18.9–18.10) |

- **Reused:** `workflow.cfg_sla_definitions` (one per case task type, on a calendar), `workflow.cfg_escalation_rules` (escalation levels and target roles), `workflow.cfg_task_types_and_work_queues`, `workflow.ref_business_calendars`, `refdata.ref_public_holiday_calendars`, `claims.ref_claim_decision_reason_codes`.
- **Missing configuration parks** (ADR-022): a case raised from an event (a claim dispute after a decline) whose case type has no timelines parks with `CASE_TIMELINE_NOT_CONFIGURED`. A case raised by a person is never refused for it: it opens, and the readiness check (§10) and an operations figure say what is missing.

## 4. Lifecycle and outcomes
- **Statuses:** `RECEIVED` → `ACKNOWLEDGED` → `IN_PROGRESS` ⇄ `AWAITING_COMPLAINANT` → `DECIDED` → `CLOSED`; `WITHDRAWN` from any open status; `REOPENED` → `IN_PROGRESS`. Enquiries may go straight from `RECEIVED` to `CLOSED` with outcome `ANSWERED`.
- **Outcomes:** `UPHELD`, `PARTLY_UPHELD`, `REJECTED` (complaints, disputes, appeals; the PPR's "upheld" and "rejected", 18.1), `WITHDRAWN` (an internal status, not a PPR outcome), `ANSWERED` (enquiries). A rejection needs reasons and states the review and ombud options (18.7.2). A decision on an appeal is made by someone other than the person who decided the case appealed (18.6, SRS-002 style: `CASE_DECIDER_SAME_PERSON`, 409).
- **Extensions:** a timeline is extended only if its step allows it, by at most `max_extension`, with a reason; the complainant is told the new date (18.9). Recorded as a case event.
- **Redress:** compensation and goodwill payments by type and amount (money as decimal plus currency), recorded per case (18.8.3).
- **Ombud:** a referral records the scheme, the ombud's reference and date; the outcome records the ombud's finding. A case referred to an ombud stays open for the outcome.

## 5. Portal users
Under `external-user-access.md`: the user's approved link sets what they may raise and see; out-of-scope reads are 404 and actions 403 `OUT_OF_SCOPE`.

| Portal | May raise | May see |
|---|---|---|
| Member (`CUSTOMER`) | A complaint or enquiry about their own policies, claims, memberships or invoices; a dispute of their own declined claim; an appeal of their own case's decision | Their own cases |
| Broker (`BROKER`) | An enquiry about a client record it brokers (its own and its sub-brokers' intermediary codes); a complaint on a client's behalf (`on_behalf_of_party_id`); its own complaint about the insurer's service | Cases it raised, and cases on records it brokers that are marked shareable with the broker (OQ-108) |
| Provider (`PROVIDER`) | A claim dispute or payment dispute about its own claims and remittances (Wave 5a screens) | Its own cases |

- **Only case types whose `portal_audiences` name the portal** can be raised there; the case type's links must be in the user's relationship.
- **Portal views** show: number, type, status, received and expected dates, the communications sent, the decision with reasons, and the escalation and ombud details. They never show internal notes, assignees, queues, tasks, other parties' details or reserves.
- **Raising a case** takes `Idempotency-Key`; adding a message or document to one's own case is allowed while it is open. MFA as for the portal.

## 6. South Africa's Policyholder Protection Rules (**for compliance to confirm**)
### 6.1 What the rules say (summary, with sources)
- **Complaint** (PPR 18.1, Long-term and Short-term alike): an expression of dissatisfaction alleging a breach of an agreement, law, rule or code; maladministration or negligence causing harm or inconvenience; or unfair treatment. A **complainant** includes the policyholder, beneficiary, insured person, premium payer, group member, a potential policyholder, and a person acting for any of them.
- **Reportable complaint:** every complaint except one upheld at once by the person who received it, one upheld through the ordinary query process within **5 business days**, or one received in a way that gave no reasonable chance to record it (18.1).
- **Categories** (18.5.1): policy or service design (premiums, fees, charges); information provided; advice; policy performance; service (premium collection, lapses); accessibility, changes or switches; complaints handling; insurance risk claims (including non-payment); other. Insurers add their own, each under the closest of the nine.
- **Process:** a board-approved framework that sets the insurer's own **timeframes and when they may be extended** (18.2–18.3); trained, impartial handlers (18.4); a simple internal escalation and review by an impartial senior person (18.6); decisions with reasons, and on rejection the escalation and ombud options and time limits (18.7); free, plain-language communication: acknowledgement "within a reasonable time" with contact details and indicative timelines, progress and delays with reasons (18.9); ombud details at sale, in periodic communications and on rejection (18.10).
- **Records** (18.8): per reportable complaint, the complainant and subject, evidence, correspondence, decisions, category and status, including whether it is within the timelines; and per category: received, upheld, rejected with reasons, escalated, referred to an ombud with outcomes, compensation and goodwill payments (number and amount), outstanding. Analysed and reported to the board (18.8.4–18.8.5) and to the Authority as prescribed (18.11).
- **Claims** (Rule 17.6): decide within a reasonable period; notify the decision in writing within **10 days**; a repudiation or dispute notice gives reasons, a right to make representations within **not less than 90 days**, internal escalation details, ombud details and time limits, and any time-bar or the prescription period; decide on representations within **45 days**; a time-bar must allow at least **6 months** after the 90 days; record the claim by the next business day (17.8.7).
- **Ombud:** the National Financial Ombud Scheme (NFO, from 1 March 2024, merging the Long-term and Short-term ombuds) requires the insurer's own process first (NFO Rule 7.3.1), suspends time-bars while it has a complaint (6.1), and may dismiss a complaint more than **3 years** after the cause (4.2.2(e)). In practice it gives insurers 6 weeks on a first complaint (NFO FAQ). The FAIS Ombud gives a provider 6 weeks and a complainant 6 months after the final response, for brokers' complaints.
- **Health:** a medical scheme's rules provide for settling complaints and disputes (Medical Schemes Act s29(1)(j)); complaints to the Council for Medical Schemes (s47: comments within 30 days), appeal to the Council within 3 months (s48) and to the Appeal Board within 60 days (s50(3)). Health insurance products under the Insurance Acts follow the PPR and the NFO instead (OQ-101).
- **Records retention:** Rule 18 states none; FAIS requires complaint records for at least 5 years (OQ-106).

### 6.2 What INSYS does with them
- **Timelines are tenant configuration**, every one of them, including the numbers the rules state (§3). The table above is the reference compliance uses to set them; INSYS seeds none and checks none against the rules.
- **Reportable** is computed when a complaint closes: not reportable if upheld by the person who received it (recorded at receipt), or upheld through the ordinary query process within the case type's configured query window. Everything else is reportable. Staff can see why.
- **Categories:** the nine PPR categories ship as global reference data; complaints must have one (directly, or through the tenant's own category).
- **Notices** carry what 18.7, 18.9, 18.10 and 17.6.3 list (§7), with the ombud details from `workflow.ref_ombud_schemes`. The wording of the templates is the tenant's, for compliance to approve.
- **Records:** every element of 18.8.1–18.8.3 is a field or event of the case (§1, §4), and the report of §8 gives the per-category figures.

## 7. Notices (Communications)
- **New notification types** (service notices, so no consent purpose): `CASE_ACKNOWLEDGED`, `CASE_UPDATE` (progress or delay, with the reason and new date), `CASE_DECISION` (outcome, reasons, review and ombud details), `CLAIM_DECISION_RIGHTS` (the PPR 17.6.3 items, sent with a claim repudiation or dispute; OQ-086's open question answered by configuration).
- **Merge fields** add `{case_number}`, `{case_type}`, `{expected_date}`, `{decision_reasons}`, `{review_options}`, `{ombud_details}`, `{representations_deadline}`.
- **Recipients:** the complainant; the broker as well when it raised the case on a client's behalf; the provider for its disputes.

## 8. Reporting (F073)
- **`GET /workflow/cases/summary`** (dashboard figures): `CASES_OPEN` by family and status, `CASES_OVERDUE` (past a configured timeline), `COMPLAINTS_RECEIVED`, `COMPLAINTS_UPHELD`, `COMPLAINTS_REJECTED`, `COMPLAINTS_REFERRED_TO_OMBUD`, `REDRESS_PAID` (per currency).
- **`GET /workflow/cases/complaints-report?from&to`:** the PPR 18.8.3 data per category (received, upheld, rejected with reasons, escalated, referred to an ombud with outcomes, compensation and goodwill by number and amount, outstanding), for the board report and the Authority's return. The return's format is not verified (OQ-105).
- **Scope:** F103 by the case's product and branch; complaints-report needs its own permission.

## 9. Access, scope and personal data
- **Permissions:** `case.read`, `case.create`, `case.update`, `case.decide`, `case.redress` (with maker-checker for a payment), `case_report.read`; portal endpoints use `case.read` and `case.create` within the relationship.
- **Functions** (ADR-026), in the catalogue's sub-domains:

  | Function | Kind | Sub-domain | Feature |
  |---|---|---|---|
  | `SERVICE_ENQUIRIES`, `SERVICE_COMPLAINTS`, `SERVICE_DISPUTES`, `SERVICE_APPEALS` | Screen | `CUSTOMER_SERVICE` | — |
  | `SERVICE_CASE_REPORTS` | Screen | `CUSTOMER_SERVICE` | — |
  | `CLAIM_DISPUTES`, `CLAIM_APPEALS` | Screen | `DISPUTES_APPEALS` | — |
  | `HEALTH_CLAIM_APPEALS` | Screen | `HEALTH_MEDICAL_CLAIMS` | `HEALTH` |
  | `PROVIDER_DISPUTES` | Screen | `HEALTH_PROVIDER_MANAGEMENT` | `HEALTH` |
  | `CASE_REGISTER`, `CASE_ACKNOWLEDGE`, `CASE_UPDATE`, `CASE_LINK_RECORD`, `CASE_EXTEND_TIMELINE`, `CASE_ESCALATE`, `CASE_DECIDE`, `CASE_RECORD_REDRESS`, `CASE_REFER_TO_OMBUD`, `CASE_RECORD_OMBUD_OUTCOME`, `CASE_REOPEN`, `CASE_WITHDRAW`, `CASE_CLOSE` | Action | under each screen | as the screen |
  | `CUSTOMER_PORTAL_CASES`, `BROKER_PORTAL_CASES`, `PROVIDER_PORTAL_DISPUTES` | Screen (+ raise action) | the portal sub-domains | the portal features |
  | `CASE_TIMELINES` | Job | `CUSTOMER_SERVICE` | — |

  `CASE_DECIDE` on an appeal and `CASE_RECORD_REDRESS` are privileged (OQ-109).
- **F103:** staff see cases within their product and branch scope; a case with no linked record is tenant-wide.
- **Personal data:** complaint text and documents can hold health or identity information (POPIA s26–s33): never in logs or events; Health-linked cases are readable only by those who may read Health claims (as Communications does for Health notices); anonymising a party blanks its cases' free text.

## 10. Readiness
- **Onboarding readiness** (`ORGANISATION_AND_ACCESS` or a new `SERVICE_AND_COMPLAINTS` step, OQ-110): at least one complaint case type with a review type, its timelines for `ACKNOWLEDGE` and `RESOLVE`, a business calendar, the ombud schemes it names, and the three case notification types with templates.
- **Front-end readiness checklist** for the build:

  | Item | What the build provides |
  |---|---|
  | Lists with filters and cursor paging | Cases by family, type, status, category, overdue, assignee queue, linked record |
  | Read by ID; links both ways | Case by ID with links; a policy's, claim's, party's or provider's cases by filter |
  | State enums | Status, outcome, family, link type, step as enums |
  | `x-permission`, `x-function` | Every operation |
  | Idempotency-Key and If-Match | Raising a case, recording redress; If-Match on every change |
  | Error codes in the catalogue | `CASE_DECIDER_SAME_PERSON`, `CASE_TIMELINE_NOT_EXTENDABLE`, `CASE_LINK_NOT_ALLOWED`, `CASE_CLOSED` |
  | `x-eventual-effects` | Decisions (claim reassessment, notices), ombud referral |
  | Real responses against documented schemas | Staff and portal cases, summary and report |

## 11. API (summary)
| Endpoint | Permission |
|---|---|
| `GET /workflow/cases?family&type&status&category&overdue&linkType&linkId` (cursor), `GET /workflow/cases/{id}` | `case.read` |
| `POST /workflow/cases` (Idempotency-Key) | `case.create` |
| `POST /workflow/cases/{id}/acknowledge`, `/messages`, `/links`, `/extend`, `/escalate`, `/withdraw`, `/reopen`, `/close` (If-Match) | `case.update` |
| `POST /workflow/cases/{id}/decide` `{outcome, reasons}` (If-Match) | `case.decide` |
| `POST /workflow/cases/{id}/redress` (Idempotency-Key) | `case.redress` |
| `POST /workflow/cases/{id}/ombud-referral`, `/ombud-outcome` (If-Match) | `case.update` |
| `GET /workflow/cases/summary`, `GET /workflow/cases/complaints-report?from&to` | `case.read` / `case_report.read` |
| `GET`, `POST /portal/cases`, `GET /portal/cases/{id}`, `POST /portal/cases/{id}/messages`, `/appeal` | `case.read` / `case.create`, portal |

Static data: `workflow/static/case-types`, `case-timelines`, `case-categories` (global and tenant), `case-reportability`, `ombud-schemes` (global).

## 12. How it is built
- **One migration** (the next V-number) for the case tables and the static tables, and the catalogue additions; the function catalogue regenerates.
- **Two agents at most, in separate modules:** Workflow (cases, configuration, timers, reporting, portal endpoints), then the consumers: Communications (types, merge fields) and the claim modules (reassessment on a decided dispute, reopen for Life and Health). I own the migration, shared files, catalogue, OpenAPI, error seed and merges.
- **Tests:** a complaint through acknowledgement, extension, rejection, internal review by another person, ombud referral and outcome, with notices and the 18.8.3 report; a claim dispute upheld that reopens the claim; reportability inside and outside the query window; timelines at the 23:30 UTC boundary on a business calendar; portal raising and scope for each audience; missing timelines parking.

## 13. Open points
- **OQ-101:** whether INSYS Health is medical schemes (Medical Schemes Act: scheme disputes committee, CMS) or health insurance products (PPR and NFO), or both, which decides the Health appeal path.
- **OQ-102 (F010):** a partial approval as its own claim outcome, or a settlement below the claimed amount with its reason, as today.
- **OQ-103 (F013):** reopening Life and Health claims, which their models do not have.
- **OQ-104:** whether redress is paid through INSYS (Payments, maker-checker) or only recorded.
- **OQ-105:** the format of complaint reporting to the Authority (FSCA), not verified.
- **OQ-106:** how long case records are kept (Rule 18 states none; FAIS 5 years).
- **OQ-107:** whether a complaint raised by staff on a caller's behalf (phone, walk-in) needs the complainant's confirmation.
- **OQ-108:** what a broker sees of cases on its clients' records that it did not raise.
- **OQ-109:** which case functions are privileged.
- **OQ-110:** the onboarding readiness step for cases.
