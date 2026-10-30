# Addendum: finance and compliance (Wave 6: F052, F054, F057–F060, F064, F076, F094)

**DRAFT for Ade's sign-off, 30 October 2026. Nothing here is built, and nothing will be until it is signed.** The decisions are in §8, each with a recommendation. The rules in §1 are as I understand them, for compliance to confirm.

## 0. Why an addendum
These nine functions are all partly built. Exposure snapshots, payout batches, Health fraud cases, consent, anonymisation and static data import work. Three static tables are stored but no code reads them: the compliance register, disclosure texts, and (for invoices) tax rates. Two open questions hold functions back: OQ-054 (party retention) and OQ-058 (invoice tax).

**Deferred, not here:** solvency extracts (F063, B-002), premium financing (F079, B-003).

## 1. The rules it serves (for compliance to confirm)
- **POPIA**, as I understand it: data subjects may ask what is held (s 23) and ask for correction or deletion (s 24), a responsible party must respond within a reasonable time, and records may not be kept longer than needed unless the law requires it (s 14). **Please confirm** the response period to configure.
- **FAIS and the Policyholder Protection Rules**, as I understand them: required disclosures must be given at set points, and their giving must be provable. **Please confirm** the points and wording per product.
- **Value-added tax**, as I understand it: most short-term insurance premiums are taxable and long-term and medical scheme contributions are not. **Please confirm** by product; it decides OQ-156. Finance should also confirm the return's format.
- **FICA and fraud reporting**: suspicious matters already go through due diligence (Wave 3). Fraud cases must not tip off the subject.
- **FIN-002, FIN-003:** corrections are reversals. **SRS-002:** maker and checker differ on sensitive operations.

## 2. Where it lives
- **Reinsurance:** exposure alerts. **Finance:** tax returns, payables as far as OQ-155 decides. **Billing:** tax on invoices.
- **Compliance:** the jurisdictional rules' enforcement record, data-subject requests. **Party:** retention. **Channel and the selling modules:** disclosures. **Workflow:** fraud cases for Life and General, as a case family. **Integration:** bulk export and import.

## 3. What is recorded
Names are proposals.

| Table | Holds |
|---|---|
| `reinsurance.cfg_exposure_limits` (CFG) | Zone, peril, limit, warning percentage |
| `reinsurance.exposure_alert` | Snapshot date, zone, peril, figure, limit, status, who acknowledged it |
| `billing.invoice`, `_line` (changed) | Tax code, rate and amount per line, now filled |
| `finance.tax_return` | Period, tax code, output and input totals, status, who approved it |
| `finance.supplier_invoice` | Only if OQ-155 chooses it: supplier, reference, amount, due date, status |
| `compliance.data_subject_request` | Party, type, received and due dates, status, outcome, reason |
| `channel.disclosure_acknowledgement` | Party, notice type, text version, language, display point, when, by which channel |
| `party.cfg_retention_rules` (CFG) | Record type, period, what starts it |
| `integration.bulk_job` | Type, direction, status, counts, the rejected lines, who ran it |

## 4. The processes

### 4.1 Exposure monitoring (F052)
After the daily snapshot, each zone and peril is compared with its limit. Past the warning or the limit: an alert and a task. It blocks nothing unless OQ-154 says so.

### 4.2 Tax (F094)
Invoices and credit notes carry the tax from the existing rates, by product and date. Finance posts it to its own account. A return totals a period's tax, a second person approves it, and it is exported. Nothing is filed by INSYS.

### 4.3 Payables (F054)
As far as OQ-155 decides.

### 4.4 Jurisdictional rules (F057)
Register entries name the rule, jurisdiction and effective dates. Enforcement stays in each module's existing checks. What is new is a readiness check that every entry applying to the tenant's jurisdiction names the configuration that enforces it, and a warning where none does. No new rules engine.

### 4.5 Data-subject requests (F059)
- **Received** by staff or the member portal, identity confirmed by a person. A due date from the tenant's setting, with a task.
- **Access:** a person generates an export of the party's records, across modules, through a public query each module answers. Medical data only for someone with the clinical function.
- **Correction:** through the existing party change, linked to the request.
- **Deletion:** through the existing anonymise check. A record still within retention or under legal hold is refused, with the reason, and the refusal is the answer to the data subject.
- **Second person** before any export leaves or any deletion runs.

### 4.6 Disclosures (F060)
A process step names a display point. The text in force for that point, jurisdiction and language is returned; the step records its acknowledgement, with the version. Without one, the step is refused for the points OQ-157 names. A copy is sent by the existing notices.

### 4.7 Party retention (F064)
Rules per record type (OQ-054). A daily job lists parties past retention for a person. Anonymising remains a person's act, with the existing checks.

### 4.8 Fraud investigation (F058)
A Workflow case family for Life and General, as Health's are: indicators, investigator, outcome. A case's existence is hidden from portals and from the subject (no tipping off). An outcome holds or releases a claim only by a person's act.

### 4.9 Bulk export and import (F076)
- **Export:** a filtered set of records, scope-checked, as a file, audited with the filter and row count. Personal data needs a second person.
- **Import:** all or nothing, as the static data import is, with every rejected line reported. Each row goes through the same command as the API, so every rule applies. Limited to the types OQ-158 names.

## 5. What it gates
Invoices change: they carry tax. Disclosure points refuse a step without an acknowledgement. Nothing else stops business unless OQ-154 says so.

## 6. Readiness
- **Blocking:** tax rates for each product that invoices, once OQ-156 applies; a posting rule for tax; a text in force for each mandatory disclosure point and language; authority for tax return and data-subject approvals.
- **Warnings:** no exposure limits; register entries with nothing enforcing them; no retention rule for a record type.

## 7. API (summary)
- **Staff:** `/reinsurance/exposure-alerts`, `/finance/tax-returns`, `/compliance/data-subject-requests`, `/integration/bulk-jobs`.
- **Changed:** invoice views show tax; process steps with a disclosure point return the text and take an acknowledgement.
- **Portal:** members raise a data-subject request and see its status, and acknowledge disclosures.

## 8. Decisions
| OQ | Decision | Options | Recommendation |
|---|---|---|---|
| **OQ-054** | Party retention | (a) a tenant-set period per record type, counted from the relationship's end; (b) a fixed period | **(a)**, compliance to give the defaults. Anonymising stays a person's act. |
| **OQ-058** | Tax on invoices | (a) from the existing rates, by product and date; (b) a tax engine | **(a)**. The rates exist, and rating already applies them. |
| **OQ-154** | Does an exposure limit block new business? | (a) an alert only; (b) refer new business in that zone and peril; (c) refuse it | **(a)** now. Aggregation limits already refer at quote; (b) could join them. |
| **OQ-155** | How far payables go (the GL is out of scope, §2.2) | (a) stop at payouts and report what is due; (b) also record supplier invoices, without an AP ledger; (c) a full AP ledger | **(a)**. Suppliers' invoices belong in the tenant's finance system. F054 stays partial on purpose. |
| **OQ-156** | Which products carry VAT | (a) set per product by the tax rate records; (b) by domain, fixed | **(a)**, finance to confirm each. |
| **OQ-157** | Which disclosure points are mandatory | The points per product | **Quote, inception, renewal and claim decision**, compliance to confirm wording and points. |
| **OQ-158** | What can be imported | (a) parties and policies; (b) those and claims and payments; (c) anything with a command | **(a)** first, for migration. Money needs its own reconciliation rules. |
| **OQ-159** | The data-subject response period | A tenant-set number of days | **30 days by default**, compliance to confirm. |
| **OQ-160** | Who sees a fraud case | (a) investigators and holders of a fraud function only; (b) anyone with case access | **(a)**, every read audited. |

## 9. How it is built
- **6.1 (F052, F094, F054):** exposure alerts; tax on invoices and returns; payables per OQ-155.
- **6.2 (F057, F059, F060, F064):** register readiness; data-subject requests; disclosures; party retention.
- **6.3 (F058, F076):** fraud cases; bulk export and import.

Tax touches every invoice, so 6.1 gets the fullest money tests, including that invoices with no tax are unchanged.

## 10. Tests
- **Money:** tax on an invoice, its credit note and its posting; a return's totals equal the period's postings; a tax-free product is unchanged.
- **Dates:** the first day a rate is in force; a return's period across month ends; due dates on the fixed clocks.
- **Privacy:** an export holds only the party's own records; medical data only with the clinical function; a fraud case is invisible in portals; a bulk export is audited with its filter; no personal data in events or logs.
- **All or nothing:** one bad line rejects the import and changes nothing, and is named.
- **End-to-end:** a data-subject deletion refused within retention and accepted after it; a disclosure point refusing a step until acknowledged.
