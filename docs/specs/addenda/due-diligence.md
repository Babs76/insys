# Addendum: customer due diligence (Wave 3: F001–F003, F077, and the staff duplicate check)

**Signed off (Ade, 29 October 2026), with these decisions. Built in Wave 3: batch 1 (merged at `7eeed73`) and batch 2 (branch `wave3-2`).**
- **OQ-123:** the §5 defaults are accepted. For a tenant with the LIFE feature, the Life issue gate is on by default, and onboarding readiness flags it if it is switched off.
- **OQ-124:** LOW every 5 years, MEDIUM every 3 and HIGH every year, as defaults each tenant can change.
- **OQ-125:** re-screen on list updates and before each payout. A monthly full re-screen is the fallback for providers that do not tell INSYS when lists change.
- **OQ-126:** `PARTY_APPROVE_ENHANCED_DD`, privileged, as proposed; the maker cannot approve.
- **OQ-127:** a 409 with the candidates, and an override with a reason, as proposed.
- **OQ-128:** due diligence records follow the party retention default: 7 years after the last policy, claim or payment ends, which covers FICA's 5-year minimum. A legal hold overrides it.
- **Adverse media screening** (`ADVERSE_MEDIA`) is off by default for each tenant, and is switched on deliberately. It may involve special personal information under POPIA (§1).

**Amended (Ade, 30 October 2026): OQ-138, what ends a periodic review (GAP-113). Built in its own batch after Wave 4 batch 4.2.**
- **A review needs a person by default.** Each customer risk class has a review mode: `PERSON` (the default for every class) or `AUTOMATIC` (a tenant's compliance may choose it per class, for example LOW).
  - `PERSON`: an `EXPIRED` party stays expired until a person with the review function (`PARTY_COMPLETE_DUE_DILIGENCE_REVIEW`, permission `due_diligence.review`) completes the review through Party. A fresh screening runs as part of it, and who reviewed and when are recorded. Automatic re-screens still record checks and raise hits, but do not end the review.
  - `AUTOMATIC`: a fresh screening ends the review, as before OQ-138, with the system requester recorded as the reviewer so the audit shows how it cleared.
  - The Workflow review task (`DUE_DILIGENCE_REVIEW`) cannot be completed directly; it closes when the party clears.
- **What the review checks.** It does not redo identity or address checks by itself: the reviewer confirms the details are current, and a changed detail is updated the usual way and runs its own check. An identifier may record its document's expiry date (optional; a company registration has none). While the identity check's document has an expiry date that has passed (by the tenant's business date) and no newer document of that kind is on file, the review cannot complete (`DUE_DILIGENCE_IDENTITY_DOCUMENT_EXPIRED`, naming the document type only). An expired document does not change the party's status between reviews; only the review checks it. A renewed document (expiring later) is recorded beside the one it replaces.
- **The reviewer is a contributor:** under the enhanced-approval rule (BR-109), the reviewer cannot give that party's enhanced approval.
- **`next_review_on`** counts from the day the review completes.
- **Readiness:** with the gates on, a staff role must hold the review function (`DUE_DILIGENCE_REVIEWER`, as for hit review).

## 0. Why an addendum
- **F001 (verify customer identity)** has a basis in the SRS (§6.1, identity documents and verification references). Today:
  - an identifier is stored encrypted and hashed;
  - staff can record a verification reference by hand (`VerifyIdentifier`, no test);
  - no provider verifies anything.
- **F002 (KYC), F003 (CDD with sanctions and PEP screening) and F077 (customer risk classification)** are not in the SRS. CLAUDE.md forbids inventing requirements, so this addendum proposes them for you to decide.
- **The staff duplicate check** was decided on 24 October 2026: staff registrations get the portal's match (the identifier hash, or a close name with the same date of birth).

## 1. The law it serves (summary, **for compliance to confirm**)
INSYS's first tenants are South African. The obligations below come from the Financial Intelligence Centre Act 38 of 2001 (FICA), as amended in 2017, and the FIC's guidance. This is a summary for design, not legal advice: compliance should confirm it before go-live.
- **Who is an accountable institution:** an insurer carrying on life (long-term) insurance business (FICA Schedule 1). Non-life insurers and medical schemes are not. Even so, nobody may make funds or services available to a person on the targeted financial sanctions list (s26B), so payouts in every domain need a sanctions check.
- **CDD for an accountable institution (s21 to s21H):**
  - identify and verify the client, and anyone acting for them;
  - understand the business relationship;
  - identify a legal person's beneficial owners;
  - keep due diligence current (ongoing due diligence);
  - do not establish the relationship, or end it, when CDD cannot be completed (s21E);
  - for a foreign prominent public official, and for a domestic prominent influential person who is high risk, get senior management approval, establish the source of wealth and funds, and monitor the relationship more closely. Their family members and known close associates are treated the same way.
- **A risk-based approach (s42):** the institution's Risk Management and Compliance Programme decides how much due diligence each risk level needs. INSYS supplies the configuration, not the programme.
- **Records (s22, s22A, s23):** CDD records are kept for at least 5 years after the relationship ends.
- **POPIA and adverse media (for compliance to confirm):** adverse media screening may surface special personal information, such as criminal behaviour (POPIA s26 to s33). It is therefore off by default and switched on per tenant (`party.cfg_due_diligence_settings.adverse_media_enabled`), with the tenant's own justification.
- **Reports:** reports to the FIC go through its own system, outside INSYS. They include suspicious transaction reports (s29) and reports of property of a sanctioned person (s28A). INSYS records that a report is needed and who made it (§4.4).

## 2. Where it lives
- **Party owns due diligence.** It already holds the person (identifiers, verification references, duplicate candidates), so it gains a `DueDiligence` aggregate, one per party. It is not a new module.
- **Providers plug in through Integration's adapters, as the GL and reinsurers do (PROMPT §11):**
  - `IIdentityVerificationAdapter` checks an identifier against a source (the Home Affairs population register through a vendor, for example);
  - `IScreeningAdapter` screens a party against sanctions, PEP and adverse media lists.

  The stubs accept each call and record it in `integration.external_call_log`, with a configurable answer for tests. The partner register gains the partner types `IDENTITY_VERIFICATION` and `SCREENING` (a migration widening the CHECK).
- **Other modules read due diligence through a Party contract, `IDueDiligenceStatus`,** and never its internals. They ask it whether a party is cleared for a step (§5).
- **Reviews of hits and approvals use the existing Workflow:** a task for a person to review a hit, and an approval for enhanced due diligence.

## 3. What is recorded
**`party.due_diligence`**, one per party:
- `status`: `NOT_STARTED`, `IN_PROGRESS`, `CLEARED`, `REFERRED`, `BLOCKED` or `EXPIRED`;
- `risk_class`: `LOW`, `MEDIUM` or `HIGH` (F077);
- `level`: `SIMPLIFIED`, `STANDARD` or `ENHANCED`, from the risk class;
- `cleared_at`, `cleared_by`, `next_review_on`;
- the requirement set it was assessed against, and its version.

**`party.due_diligence_check`**, one per check run:
- `check_type`: `IDENTITY`, `ADDRESS`, `SANCTIONS`, `PEP`, `ADVERSE_MEDIA`, `BENEFICIAL_OWNERSHIP`, `SOURCE_OF_FUNDS`;
- `outcome`: `PASSED`, `FAILED`, `HIT`, `WAIVED`;
- the provider and its reference, `checked_at`;
- for a manual check, the document (Documents) and the person.

**`party.screening_hit`:**
- the list, the matched name, the score and the provider's reference;
- the review: `TRUE_MATCH` or `FALSE_POSITIVE`, by whom, the reason and when;
- for a true sanctions match, `report_required` and the FIC reference staff enter once they have reported it.

**`party.beneficial_owner`,** for an organisation: the owning party (itself a party, with its own due diligence), its share or control, and how ownership was established.

**Everything here is personal information, classified HIGHLY_SENSITIVE:**
- It never appears in events, logs or exceptions. Events carry the party ID and the status only.
- Each read is audited (IAM-006).
- It is never shown on a portal (§6).

## 4. The processes

### 4.1 Identity verification (F001)
- **When:** an identifier is added, by staff or a portal, or when staff ask.
- **How:** the identifier goes to `IIdentityVerificationAdapter`, and its answer is a check: `PASSED` with a reference sets `verified_at` and `verification_ref` on the identifier, as `VerifyIdentifier` does today; `FAILED` is a check with the reason.
- **Manual verification stays,** for a document seen in person. It now needs the document (`documentId`) and is a check of its own.
- **When no adapter is configured,** verification is manual only, and readiness says so (§7).

### 4.2 KYC: what must be known (F002)
- **Requirement sets:** `party.cfg_due_diligence_requirements` (Tier 1) says which checks and documents each case needs, by party type (individual or organisation), level and domain. For example, an individual at STANDARD for Life needs `IDENTITY`, `ADDRESS`, `SANCTIONS` and `PEP`, and an organisation also needs `BENEFICIAL_OWNERSHIP`.
- **Checks run as their data arrives.** A requirement not yet met keeps the due diligence `IN_PROGRESS`, and the party view lists what is missing.

### 4.3 Screening and CDD (F003)
- **When screening runs (OQ-125):**
  - at onboarding;
  - before each payout (§5);
  - on each periodic review;
  - when the provider tells INSYS its lists changed. The stub can simulate this, and a daily job re-screens the parties whose last screening predates the list's version;
  - a monthly full re-screen of every party with a live relationship, as the fallback for providers that do not notify list changes.

  Adverse media is screened only where the tenant has switched it on.
- **A hit stops clearance.** It opens a Workflow task (`DUE_DILIGENCE_HIT_REVIEW`) for a holder of the new function `PARTY_REVIEW_SCREENING_HIT`. The reviewer marks it `FALSE_POSITIVE` (with a reason) or `TRUE_MATCH`.
  - **A true sanctions match** sets the due diligence `BLOCKED` and `report_required`, and opens a case for compliance. Unblocking is privileged and needs a second person.
  - **A true PEP match** raises the level to `ENHANCED`.
- **Enhanced due diligence:** source of wealth and source of funds are recorded as checks with their documents. Clearance then goes through a Workflow approval (`DUE_DILIGENCE_ENHANCED`), decided by someone holding the `PARTY_APPROVE_ENHANCED_DD` authority, standing for "senior management" in s21F (OQ-126).
- **Ongoing due diligence:** each risk class has a review interval. When `next_review_on` passes, the status becomes `EXPIRED` and the daily job raises a review task. An expired party is not cleared (§5). What ends the review depends on the class's review mode (OQ-138, at the top of this addendum): by default a person completes it (`POST /party/parties/{id}/due-diligence/review`); for an `AUTOMATIC` class a fresh screening does.

### 4.4 Customer risk classification (F077)
- **The class is worked out from weighted factors** in `party.cfg_customer_risk_factors` (Tier 1). Each factor is a rule on the party's facts: country of residence or registration, party type and legal form, occupation or industry, PEP status, products held, channel (a portal registration that was never met in person), and the source of funds.
- **The weights add up to a score,** and `party.cfg_customer_risk_classes` maps score bands to classes, with each class's level and review interval.
- **A person can override the class,** with a reason. The override goes through a Workflow approval and is audited.
- **The class is worked out again** when a factor's data changes, on review, and when a hit is decided.

### 4.5 Staff duplicate check (decided 24 October 2026)
- **The check:** `RegisterParty` by staff runs the matching portal registration uses (`party.cfg_matching_rules_and_thresholds`: the identifier hash, or a close name with the same date of birth).
- **On a match:** staff may see existing clients, so unlike the portal, the answer is a 409 `PARTY_POSSIBLE_DUPLICATE` listing the candidates (number, display name, the identifier's last four). Staff then either use the existing party or register again with `confirmNew: true` and a reason. That second registration records a `duplicate_candidate` reviewed as `CONFIRMED_NEW` by that user, and is audited (OQ-127).

## 5. What it gates (OQ-123)
**`IDueDiligenceStatus.ClearedFor(partyId, step, domain)`** answers whether a party is cleared for a step. The steps are `QUOTE`, `ISSUE`, `PAYOUT` and `CLAIM_PAYOUT`. `party.cfg_due_diligence_gates` (Tier 1) sets, for each domain and step, which status is needed.

**Proposed defaults:**

| Domain | Quote | Issue | Payout (claims, refunds, surrender, loans) |
|---|---|---|---|
| Life | Allowed | CDD `CLEARED` for the policyholder (FICA) | CDD `CLEARED` for the payee, and sanctions screened in the last 30 days |
| General | Allowed | Allowed | Sanctions screened in the last 30 days with no unreviewed hit (s26B) |
| Health | Allowed | Allowed | Sanctions screened in the last 30 days with no unreviewed hit (s26B), for a member payee. Providers are screened when they register in the provider register and on review |

- **Refusal:** a gated step is refused with `DUE_DILIGENCE_NOT_CLEARED` (422), naming what is outstanding.
- **At the payout gate,** an instruction that is not cleared is not refused outright. It is held: the Claims Framework and Payments park the payout until clearance, and nothing is paid (D-5 style).
- **Changes from today:** Life issue and every payout gain a step that can stop them. This changes agreed behaviour, which is why it is your decision. Existing tenants start with the gates off until their readiness item is met (§7).

## 6. Portals
- A portal registration or identifier triggers verification and screening, as staff ones do.
- A portal user never sees due diligence: no status, no hit, no class. A refused or held step answers a portal user with a neutral "we are reviewing your details" (`PORTAL_UNDER_REVIEW`). This is the same rule as the duplicate answer (external-user access §6, POPIA).

## 7. Readiness
- **The Life issue gate:** for a tenant with the LIFE feature it is on by default, and readiness flags it when switched off (`DUE_DILIGENCE_LIFE_GATE`, OQ-123).
- **For a tenant with a gate on:** readiness needs an active adapter of each kind the requirement sets use, or the gate off (`DUE_DILIGENCE_PROVIDER`). It also needs holders of the hit-review function and of the periodic review function (`DUE_DILIGENCE_REVIEWER`, OQ-138).
- **Without an adapter,** checks are manual only, and readiness warns.

## 8. Decisions (all decided, 29 October 2026; see the top of this addendum)
1. **OQ-123, gates:** the defaults in §5. Do you accept them, or should General and Health also need full CDD at issue?
2. **OQ-124, risk classes and review intervals.** Proposed: LOW every 5 years, MEDIUM every 3, HIGH every year. The starter factors are as in §4.4, and the tenant's compliance sets the weights.
3. **OQ-125, re-screening.** Screen on list updates and before each payout (as §4.3), or on a fixed schedule only (simpler, but slower to catch a newly listed person)?
4. **OQ-126, enhanced due diligence approval:** a function with its own authority, `PARTY_APPROVE_ENHANCED_DD`, privileged and needing a second person, to stand for "senior management". Or a named role?
5. **OQ-127, staff duplicates:** a 409 with the candidates and an override with a reason (§4.5), or a pending review as on the portal?
6. **OQ-128, records:** keep due diligence records 5 years after the last policy or claim with the party ends, configurable, with a legal hold overriding it, as the cases addendum does (OQ-106)?

## 9. API (summary)
- `GET /party/parties/{id}/due-diligence`: the status, class, level, checks and what is missing. Hits appear only for holders of `PARTY_REVIEW_SCREENING_HIT`.
- `POST /party/parties/{id}/due-diligence/checks`: run a check (screening, or identity with an identifier ID), or record a manual one with its document. It needs an Idempotency-Key.
- `POST /party/parties/{id}/due-diligence/hits/{hitId}/review`: true match or false positive, with a reason. It takes If-Match.
- `POST /party/parties/{id}/due-diligence/risk-class`: an override with a reason, through Workflow approval.
- `POST /party/parties/{id}/due-diligence/report`: record the FIC reference of a report made (s28A or s29).
- `POST /party/parties/{id}/due-diligence/review` (OQ-138): complete a party's periodic review, confirming its details are current (`detailsCurrent: true`, optional `note`). It takes If-Match.
- `GET /party/due-diligence`: by status, class, review due date, and open hits. It feeds the compliance work list.
- `RegisterParty` gains `confirmNew` and `reason` (§4.5).
- **New functions:**
  - `PARTY_DUE_DILIGENCE` (screen);
  - `PARTY_RUN_DUE_DILIGENCE_CHECK`, `PARTY_REVIEW_SCREENING_HIT`, `PARTY_OVERRIDE_RISK_CLASS`, `PARTY_RECORD_FIC_REPORT` (actions);
  - `PARTY_APPROVE_ENHANCED_DD`, `PARTY_UNBLOCK_DUE_DILIGENCE` (privileged);
  - `PARTY_DUE_DILIGENCE_JOBS` (job).
- **Events:** `party.DueDiligenceCleared`, `party.DueDiligenceBlocked` and `party.DueDiligenceExpired`, carrying the party ID and status only. Claims and Payments release held payouts on `party.DueDiligenceCleared`.

## 10. How it is built
- **Wave 3 batch 1:**
  - the adapters and their stubs;
  - identity verification (F001) and manual checks with documents;
  - the staff duplicate check;
  - the deterministic test clocks;
  - BR-089, BR-090, BR-096, BR-097 and BR-101.
- **Wave 3 batch 2:**
  - requirement sets and KYC (F002);
  - screening, hit review, enhanced due diligence and reports (F003);
  - risk classification and reviews (F077);
  - the gates (§5), with their readiness items.
- **Migrations:**
  - the four tables in §3;
  - the three configuration tables (catalogue entries first, then the generator);
  - the widened partner types.

## 11. Tests (when built)
- **Verification:** an identifier added on a portal is verified through the stub and gets its reference. A failed verification is a check that leaves the party unverified. A manual check without a document is refused.
- **Screening:**
  - A screening hit opens a review task and stops clearance.
  - A false positive with a reason clears it.
  - A true sanctions match blocks the party, needs a report, and is unblocked only by a second person with the privileged function.
- **PEP:** a PEP raises the level to ENHANCED, and clearance needs the approval. The maker cannot approve their own.
- **Gates:**
  - A Life policy for an uncleared policyholder is refused with `DUE_DILIGENCE_NOT_CLEARED`, and issues once cleared.
  - A General claim payout to a payee with an unreviewed hit is held, and released when the hit is cleared.
  - A Health claim to a cleared member is unaffected.
- **Ongoing due diligence:** a passed review date expires the party, and the job raises the review.
- **Risk classification:** factors give the configured class. An override needs approval and is audited.
- **Portals:** a portal user sees no due diligence field (schema sweep) and gets `PORTAL_UNDER_REVIEW` for a held step.
- **Privacy:** no event, log line or exception carries a hit's list, name or score.
- **Staff duplicates:** a staff duplicate gives 409 with the candidates. `confirmNew` with a reason registers the party and records the review.
- **Scope and tenancy:** F103 scope and tenant isolation, through `OperatorTenantTests` and `FunctionAccessTests`.
