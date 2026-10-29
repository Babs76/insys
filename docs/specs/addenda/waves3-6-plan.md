# Waves 3 to 6: batch plan, for Ade's review

29 October 2026. These are the waves of the approved build plan (`docs/traceability/functional-coverage.md`, checkpoint A, 23 October 2026), split into batches. The function numbers and their verified status come from the same file.

## What shapes the plan
- **Most of these functions are not in the SRS.** Of the 57 functions, 24 are "Not specified" and 2 are listed by the SRS but not modelled (F028, F038). CLAUDE.md forbids inventing requirements, so each wave starts with an addendum for your sign-off, as Wave 2's cases did (`addenda/cases.md`). The addendum sets out what each function does, its data, its rules and the decisions you need to make. Nothing is built before you sign it off.
- **The other 31 are "Partly" built (F064 is built for documents only).** Their gaps are known and smaller, so they open each wave's build where possible.
- **Batch rules stay as they are.** Each item gets its own commit and its own test. Batches merge under your standing approval: built and tested, CI green on the exact head, and nothing waiting on your decision.
- **Open questions that hold functions back:**
  - OQ-052, the actuarial basis: Life surrender values (F007), BR-082 and BR-083.
  - OQ-054, party retention (F064).
  - OQ-058, invoice tax (F094).
  - OQ-061, collecting provider recoveries (F049).

  Each addendum asks for the decision its wave needs.

## Before Wave 3: the front end's remaining requests
These ride in Wave 3 batch 1, since they are small and two are group A:
- **BR-089 (group A):** ETags on a regulatory return and on communication preferences.
- **BR-096 (group A):** a process definition made through the API must be able to work. It must not need a domain validation that no module provides.
- **BR-090:** document that `sourceId` needs `sourceType` on the notifications list.
- **BR-097:** `pricedOnCreation` on the portal quote view.

## The batches

| Batch | Functions | What gets built | Needs your decision |
|---|---|---|---|
| **3.0** addendum | F001–F003, F077 | Customer due diligence: identity verification, KYC, CDD with sanctions and PEP screening, and customer risk classification. Each goes through a provider adapter, with a stub as for the GL. It covers what each process gates (quote, issue, payout) and the periodic review. | Which steps gate business; the risk classes and review cycle; whether screening runs at onboarding only or also on a schedule |
| **3.1** | F001; the staff duplicate check (decided 24 October); BR-089, 090, 096, 097 | Identity verification through the adapter, with its outcome kept on the party. Staff registrations get the portal's duplicate match. The four front-end requests. | (in 3.0) |
| **3.2** | F002, F003, F077 | KYC and CDD cases with screening results and a person's review of each hit. The customer risk class and its review date. Gates on the steps 3.0 names. | (in 3.0) |
| **4.0** addendum | F006, F007, F015–F019, F022, F025–F027; B-001 | Renewal and retention: the renewal invitation process for General, with its eligibility, risk changes, claims history, re-rating and renewal underwriting. Grace periods for General and Health. Short-period refund scales. Acceptance of a renewal. Insurer non-renewal with notice. Retention offers and their reporting. B-001, a broker of record endorsement. | Which products renew by invitation and which automatically; how claims history affects terms; notice periods; the short-period scale source |
| **4.1** | F006, F007 (less Life values, OQ-052) | Grace periods for General and Health, configured like Life's; short-period refund scales applied. | (in 4.0) |
| **4.2** | F015–F019 | The renewal invitation: eligibility, risk and claims review, re-rating and referral before the renewal date. | (in 4.0) |
| **4.3** | F022, F025–F027; B-001 | Acceptance or lapse of the invitation; insurer non-renewal with notice; retention actions; Analytics figures for renewal and retention; the broker of record endorsement. | (in 4.0) |
| **5a.0** addendum | F028–F030, F034–F038, F048–F050 | Providers and care management: provider performance and service quality, provider statement reconciliation, complex medical cases with a case manager, care coordination and treatment plans, clinical referrals, provider credit notes, overpayment recovery and payment disputes. Complex cases and disputes reuse Workflow cases. | OQ-061 (recovery against future runs or by invoice); the performance measures |
| **5a.1** | F028–F030 | Provider performance and quality measures from claims and cases; provider statement reconciliation against remittances. | (in 5a.0) |
| **5a.2** | F034–F038 | The complex case, its case manager, its care plan with progress, and clinical referrals. | (in 5a.0) |
| **5a.3** | F048–F050 | Provider credit notes, overpayment recovery, and payment disputes as cases. | (in 5a.0) |
| **5b.0** addendum | F039–F047, F083–F093 | Benefit programmes: the pharmacy claim format and prescription data, formulary coverage and restrictions, quantity and days supply, medication limits and duplicates, chronic condition registration and entitlement, and disease management, preventive, vaccination and wellness programmes, with compliance and outcome monitoring. | The pharmacy claim format; whether chronic registration needs clinical approval; which programmes to build first (F089 to F091 can wait) |
| **5b.1** | F039–F045 | Pharmacy claims end to end: format, prescription, formulary coverage, dispensed quantity, cost share, adjudication and payment. | (in 5b.0) |
| **5b.2** | F046, F047, F092, F093 | Medication limits and duplicates; formulary restrictions (prior authorisation, quantity limits); prescription benefits. | (in 5b.0) |
| **5b.3** | F083–F085 | Chronic condition registration, chronic benefits and the medication entitlement. | (in 5b.0) |
| **5b.4** | F086–F091 | Disease management, preventive screening, vaccination and wellness programmes; compliance and outcome monitoring where the data exists. | (in 5b.0) |
| **6.0** addendum | F052, F054, F057–F060, F063, F064, F076, F079, F094 | Finance and compliance: reinsurance exposure alerts, payables, jurisdictional rules, fraud investigation for Life and General, data-subject requests, disclosures with acknowledgement, solvency extracts, party retention, bulk export and transaction import, premium financing, and tax on invoices with tax returns. | OQ-054, OQ-058; whether payables stop at payouts (the GL is out of scope, §2.2); whether F063 and F079 are wanted at all |
| **6.1** | F052, F094, F054 | Exposure monitoring with alerts against limits; tax on invoices and tax return extracts; payables as far as 6.0 decides. | (in 6.0) |
| **6.2** | F057, F059, F060, F064 | Jurisdictional rules enforced; data-subject requests; disclosures presented and acknowledged; party retention. | (in 6.0) |
| **6.3** | F058, F076, F063, F079 | Fraud investigation cases for Life and General; bulk export and transaction import; solvency extracts and premium financing, if 6.0 keeps them. | (in 6.0) |

That is 5 addenda and 15 build batches.

## Order
Wave 3, then 4, 5a, 5b and 6, as approved. Each addendum is written while the previous wave's last batch is building, so it is ready for your review when that wave merges. Only two things wait on you: signing off an addendum, and any decision a batch raises.

## Estimate, from this project's own figures
- **Front-end batches, Wave 2:**
  - Batch 4: about 7 hours from plan to head.
  - Batch 5: about 13.5 hours.
  - Batch 6: about 5 hours to head, then CI and your approval the next day.
  - Batch 7: today.

  That is about one batch every 12 hours, decision and CI waits included.
- **Waves built from an addendum:**
  - Wave 1, communications: 4 batches on 24 and 25 October.
  - Wave 2, cases: the addendum was signed off, then 2 batches on 25 October.
- **CI:** about 23 minutes per run, the longest job end to end.

On those figures:
- **Build batches:** 15 at about 12 hours each, about 7.5 days.
- **Addenda:** 5, at about half a day each to write. Your review time comes on top, and so far it has been between 2 and 12 hours.
- **Wave starts:** a wave whose addendum is already signed starts the day the previous wave merges.

**Estimate:** about 10 to 12 days of elapsed work. That puts the end of Wave 6 near **10 November 2026** if each addendum is signed within a day, or near **15 November** if decisions take two days each. The largest risk is scope: 26 functions have no SRS basis. If you defer some of them (F063, F079, F089 to F091 are the likeliest), Wave 5b and Wave 6 each lose a batch.
