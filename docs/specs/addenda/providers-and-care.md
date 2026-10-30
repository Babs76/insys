# Addendum: providers and care management (Wave 5a: F028–F030, F034–F038, F048–F050)

**Draft for sign-off (Ade). Nothing is built until it is signed off.** The decisions are in §8, each with a recommendation. The rules in §1 are for compliance to confirm.

## 0. Why an addendum
What is built today, checked against the code and `docs/traceability/functional-coverage.md`:

| Function | Today |
|---|---|
| **F028, F029** provider performance and service quality | Nothing. The SRS lists performance (§11.1 F) and no more. |
| **F030** reconcile provider accounts | Remittances are payments and reconcile like any payment (HLT-009). Nothing compares a provider's own statement with them. |
| **F034, F035** complex case and case manager | `UtilisationCase` only: open, review, conclude, with a reviewer. No complex case, no case manager, no reassignment. |
| **F036, F037** care coordination and treatment plans | Nothing, and not specified. |
| **F038** clinical referrals | Listed in SRS §11.1 I. Not modelled. |
| **F048** provider credit notes | Nothing. Billing's credit notes are for premium invoices. |
| **F049** overpayment recovery | Reversing a paid claim gives the benefit use back and raises `MedicalClaimReversed`. Nothing collects the money (OQ-061). |
| **F050** payment disputes | The case type family, the `REMITTANCE` and `PAYMENT_RUN` links and the provider audience exist (Wave 2). The provider screens do not. |

Three gaps found while checking, which this wave closes:
1. **A reversed Health claim is not posted to the ledger.** Finance handles `MedicalClaimSettled` but not `MedicalClaimReversed`, so the paid claim stays in the accounts after its reversal.
2. **A payment run cannot carry a deduction.** `CalculateRun` refuses a remittance of zero or less, and a remittance holds one amount, with no gross and net.
3. **A provider cannot link its own records to a case from the portal** (`CasePortal`, `TODO(spec)`), so it cannot raise a dispute about its remittance.

Of these 11 functions, 6 have no SRS basis and 4 only a listing, so most of the rules below are proposals for you to accept or change.

## 1. The rules it serves (summary, **for compliance to confirm**)
- **HLT-008 and POPIA (ss 26 and 32):** health information is special personal information. It is restricted by role, purpose and minimum necessary access, and never appears in events, logs, notices or exceptions.
- **HLT-009:** provider payments are traceable and reconcilable. Every deduction must be traceable to what it recovers.
- **SRS-002:** maker and checker differ on anything that moves or writes off money.
- **FIN-002, FIN-003:** posted records are never updated; corrections are reversals.
- **Medical Schemes Act s 59(3)**, as I understand it, lets a registered medical scheme deduct from a benefit payable to a supplier an amount paid to it in error. **Please confirm** whether it applies to the tenants we serve and whether a health insurer has the same right, which may rest on the provider contract instead. A time limit on recoveries, and any notice owed before a deduction, are also for compliance to state; I have not assumed one.

## 2. Where it lives
- **Health module.** Provider measures, statements, complex cases, care plans, referrals, credit notes and recoveries. Clinical data stays in the Health schema.
- **Workflow.** Tasks and approvals for all of them. Disputes are Workflow cases (F050). Complex cases: see OQ-140.
- **Finance.** Posts reversals, recoveries, credit notes and write-offs from events.
- **Payments.** Unchanged: it pays a remittance's net amount.
- **Communications.** Notices to providers, with business references only.

## 3. What is recorded
Names are proposals. `CFG` is tenant configuration; the others are records.

| Table | Holds |
|---|---|
| `health.cfg_provider_measures` (CFG) | A measure in use: `measure_code`, optional provider type, the threshold, its direction, the review queue |
| `health.provider_measure_result` | One provider, measure and month: value, claim count, whether it breached |
| `health.provider_quality_review` | A person's scored review of a provider, with a reason |
| `health.provider_statement`, `_line` | A provider's statement and its lines: reference, amount, match result, resolution |
| `health.complex_case` | Member, condition category, status, case manager, opened from what (OQ-140) |
| `health.care_plan`, `_goal`, `_activity` | Goals, activities, responsible provider or person, due dates, progress |
| `health.care_coordination_note` | Contacts between the case manager, providers and the member. Clinical, restricted. |
| `health.clinical_referral` | Member, referring provider, referred-to provider or specialty, valid dates, visits allowed, status |
| `health.provider_credit_note` | A provider's credit note: its number, what it credits, amount, status |
| `health.provider_recovery` | One amount owed by a provider: source (reversal, credit note or finding), original amount, balance, status |
| `health.provider_recovery_offset` | One deduction: the recovery, the remittance, the amount |
| `health.remittance_advice` (changed) | Adds `gross_amount` and `offset_amount`; `amount` stays what is paid |

## 4. The processes

### 4.1 Performance and service quality (F028, F029)
- **Measures.** A monthly job works out each measure per provider from claims and cases. The proposed set (OQ-141): claims submitted, claim rejection rate, average paid per claim, average time from receipt to decision, repeat-service rate, disputes and complaints per 100 claims.
- **Small numbers.** A measure with too few claims in the month is shown but not judged (minimum in configuration).
- **Breach.** A result past its threshold opens a task for the configured queue. It never changes a provider's status, contract or payment by itself.
- **Service quality.** The dispute and complaint counts come from Workflow's cases linked to the provider, through a public query, plus reviews a person records.
- **Who sees it.** Staff see every measure. A provider's portal view waits for OQ-141's second part.
- **No clinical data.** Results hold counts and amounts only.

### 4.2 Provider statement reconciliation (F030)
- **In.** A person enters or uploads a provider's statement (OQ-142).
- **Match.** Each line is compared with the provider's claims and remittances: `MATCHED`, `AMOUNT_DIFFERS`, `NOT_ON_OUR_RECORDS` or `NOT_ON_STATEMENT`.
- **Resolve.** A person resolves each exception with a reason, and may open a dispute (F050), a recovery (F049) or a credit note (F048) from it.
- **Effect.** Reconciling changes no payment by itself. The statement ends `RECONCILED` when no exception is open.

### 4.3 Complex medical cases and case managers (F034, F035)
- **Opened** by a person, from a utilisation case or a high-cost threshold, or from a membership directly. Medical data stays in Health (OQ-140).
- **Case manager.** Assigned from the users holding the case manager function, and reassigned with a reason. Both are audited. Assignment gives access to that case's clinical detail (OQ-143).
- **States.** `OPEN`, `ACTIVE`, `ON_HOLD`, `CLOSED`, with a reason to close. Never closed automatically.

### 4.4 Care plans and coordination (F036, F037)
- **Plan.** A case has one current plan: goals, activities, who is responsible, due dates. Changing a plan keeps the earlier version.
- **Progress.** Activities are marked done, missed or changed. An activity past due opens a task for the case manager.
- **Coordination.** Notes record contacts. The member's consent to share with another provider is recorded where POPIA needs it, for compliance to confirm.
- **Scope.** This is a record of care being coordinated. It makes no clinical decision and does not authorise treatment or benefits; those stay with preauthorisation.

### 4.5 Clinical referrals (F038)
- **Recorded** from a provider or by staff, with its validity and visits, and linked to a complex case or preauthorisation when there is one.
- **States.** `REQUESTED`, `APPROVED`, `DECLINED`, `USED`, `EXPIRED`.
- **Effect on claims:** OQ-144.

### 4.6 Provider credit notes (F048)
- **Recorded** against a claim or remittance, with the provider's own number. A second person approves it.
- **Effect.** Approved, it creates a recovery for its amount (§4.7), so there is one way money comes back. The member's benefit use is given back only when it reverses a claim, through the existing reversal.

### 4.7 Overpayment recovery (F049; OQ-061)
- **Raised** by a reversal of a paid provider claim, an approved credit note, or a person's finding (with a second person, over the approval limit).
- **Collected** per OQ-139. Recommended: deducted from the provider's next payment runs, oldest first. A run shows gross, offsets and net per remittance, and its approval covers the net with the offsets visible. Offsets never take a remittance below zero. A provider whose net is zero gets a remittance advice and no payment; the balance carries forward.
- **Not collected.** With no payment for the configured period, the recovery goes to a person, who may invoice the provider (through Billing, OQ-139) or write it off with a second person's approval and a reason. Never dropped.
- **Reversal.** A recovery raised in error is reversed, not edited. An offset already taken is repaid in the next run.
- **Ledger.** Finance posts the claim's reversal (closing gap 1), each offset, each write-off and each credit note.
- **Notice.** The provider gets a notice when a recovery is raised and sees offsets on its remittance. Business references only.
- **Member reimbursements.** A member has no future provider run to deduct from: OQ-145.

### 4.8 Payment disputes (F050)
- **Raised** as a Workflow case of the dispute family, by staff or by the provider through its portal, linking the remittance or run (closing gap 3: a provider may link only its own).
- **Outcome.** Upheld, a person takes the action: a further payment through the next run, or reversing a recovery. A case never moves money itself.
- **While open.** A disputed recovery is held from offsets: OQ-146.

## 5. What it gates
- **Payment runs.** They deduct open recoveries. Nothing else about a run changes for a provider with none.
- **Claims.** Only if OQ-144(b) is chosen.
- **Nothing else.** Measures, reviews and care plans block nothing.

## 6. Readiness
- **Blocking**, for a tenant entitled to HEALTH: a role holding the recovery write-off and credit note approvals (through the existing approval authority check).
- **Warnings** (advisory, as added in group A): no provider measures configured; no user holds the case manager function; no queue for a measure's breach.

## 7. API (summary, when built)
- **Staff:** `/health/providers/{id}/measures`, `/health/providers/{id}/quality-reviews`, `/health/provider-statements`, `/health/complex-cases` with its care plan and notes, `/health/referrals`, `/health/provider-credit-notes`, `/health/provider-recoveries`.
- **Changed:** payment run and remittance views gain gross and offset amounts.
- **Provider portal:** its recoveries and offsets, raising a dispute on its own remittance, submitting referrals and credit notes.
- **Permissions:** new functions, with clinical ones separate (`health_case.read_clinical`).

## 8. Decisions
| OQ | Decision | Options | Recommendation |
|---|---|---|---|
| **OQ-139** | How an overpayment is recovered (OQ-061) | (a) deducted from future runs, invoiced by exception; (b) always invoiced; (c) a choice per provider contract | **(a)**: it is what the plan assumed, needs no collection from the provider, and stays traceable on the remittance. Default 90 days before it goes to a person, tenant-set. |
| **OQ-140** | Where a complex case lives | (a) a Health aggregate holding the clinical record, using Workflow for tasks and approvals; (b) a Workflow case, as the plan said, with clinical data alongside | **(a)**. **This changes the plan.** A Workflow case is readable by case roles and its records cross modules; keeping clinical data in Health is the simpler way to meet HLT-008. Disputes stay Workflow cases as planned. |
| **OQ-141** | The performance measures, and whether providers see theirs | The set in §4.1; providers see (a) nothing at first, (b) their own, (c) their own against an average | The §4.1 set, with **(a)** at first: comparing providers has contract and competition questions you may want to settle before showing it. |
| **OQ-142** | How a provider's statement arrives | (a) entered or uploaded as CSV in one tenant-set layout per provider; (b) a fixed INSYS layout; (c) an integration | **(b)** first: a fixed CSV plus manual entry. Layouts per provider come when a real provider needs one. |
| **OQ-143** | Who reads clinical detail on a complex case | (a) the case manager and holders of the clinical function only; (b) anyone with case access | **(a)**, every read audited (HLT-008). |
| **OQ-144** | Does a referral affect claims? | (a) recorded only in this wave; (b) a plan can require one, and the claim is edited without it | **(a)**. (b) changes adjudication and needs rules per plan; it could join Wave 5b if wanted. |
| **OQ-145** | Overpayment to a member | (a) a recovery invoiced to the member; (b) deducted from their later reimbursements, then invoiced; (c) a person decides each | **(c)** for now: recovering from a member may need its own notice, for compliance to confirm. |
| **OQ-146** | A disputed recovery | (a) held from offsets until the dispute ends; (b) offsets continue and are repaid if it is upheld | **(a)**: the fairer default, with the case's timeline limiting the delay. |

Also for compliance: §1's s 59(3) point, any time limit on recoveries, and consent for sharing in §4.4.

## 9. How it is built
- **5a.1 (F028–F030):** measures and their job, quality reviews, statements and matching. Readiness warnings.
- **5a.2 (F034–F038):** complex case, case manager function, care plan with progress, coordination notes, referrals.
- **5a.3 (F048–F050):** recoveries and offsets in the run (gap 2), Finance postings (gap 1), credit notes, provider portal disputes (gap 3), and the hold in OQ-146.

5a.3 changes a money path and the run's approved total, so it gets the fullest tests of the three. The order can change; 5a.3 could go first if the money gap matters most to you.

## 10. Tests (when built)
- **Money:** a run's total is the sum of net remittances and its approval covers it; offsets never exceed the balance or the remittance; a zero net pays nothing and carries forward; the ledger balances after a reversal, an offset, a credit note and a write-off; a redelivered event changes nothing; a write-off needs a second person.
- **Dates:** the monthly job and due dates use the tenant's business date, on the fixed clocks.
- **Privacy:** no clinical data in events, logs, notices or audit detail; a user without the clinical function gets no clinical detail; a provider sees only its own records.
- **End-to-end:** a paid claim reversed, recovered from the next run, disputed and held, then upheld and repaid.
