# Addendum: benefit programmes (Wave 5b: F039–F047, F083–F088, F092, F093)

**Signed by Ade, 31 October 2026.** OQ-147 to OQ-153 as recommended in §8, with two additions: a pharmacy claims switch adapter in the backlog (B-012), and what a principal member may see (OQ-161, §8). The rules in §1 are as I understand them, for compliance to confirm.

## 0. Why an addendum
The SRS has no pharmacy claim format, prescription data, chronic registration or programme rules. Of these 17 functions, F040, F042, F085 and F086 are "Not specified", and the rest are partly built: generic adjudication and payment already work, while `health.ref_medicine_formulary` and `health.cfg_chronic_condition_list` are static data that no code reads. CLAUDE.md forbids inventing requirements, so this sets out what each would do.

**Deferred, not here:** wellness programmes, treatment compliance and treatment outcomes (F089 to F091, B-004).

## 1. The rules it serves (for compliance to confirm)
- **HLT-006, HLT-007:** claims are validated and adjudicated in the ADR-012 order. A pharmacy claim is a medical claim with more on its lines, not a second engine.
- **HLT-008 and POPIA (ss 26 and 32):** a medicine or a chronic condition reveals health information. It is restricted by role and purpose, and never appears in events, logs, notices or exceptions.
- **Medical Schemes Act, prescribed minimum benefits and the chronic disease list**, as I understand them: a registered medical scheme must fund listed chronic conditions, and may use a formulary and protocols. **Please confirm** whether this applies to the tenants we serve, and what a health insurer must do instead. It decides OQ-150.
- **Medicines and Related Substances Act**, as I understand it: covers who may prescribe and dispense, scheduling, and generic substitution. **Please confirm** whether INSYS must check a prescriber's or pharmacist's registration, or only record it. It decides OQ-148.

## 2. Where it lives
All of it is in Health. Pharmacy lines run through the existing `AdjudicationEngine` as new steps before the existing ones. A chronic registration or programme enrolment is a Health record, using Workflow for tasks and approvals, as complex cases do (OQ-140).

## 3. What is recorded
Names are proposals.

| Table | Holds |
|---|---|
| `health.medical_claim_line` (changed) | For a pharmacy line: NAPPI or product code, quantity dispensed, days' supply, prescription number, prescriber, repeat number. Clinical, restricted |
| `health.ref_medicine_formulary` (changed) | Adds the benefit category, prior authorisation flag, maximum quantity, maximum days' supply, and the generic reference group |
| `health.cfg_prescription_benefits` (CFG) | Per plan and formulary tier: co-payment, the limit, whether it is covered |
| `health.cfg_medication_limits` (CFG) | Per plan and product or ingredient: a quantity or amount per period |
| `health.chronic_registration` | Member, condition, status, approved dates, who decided. Clinical, restricted |
| `health.chronic_entitlement` | Registration, medicine or ingredient, monthly quantity, used to date |
| `health.cfg_disease_programmes` (CFG) | Programme, the conditions it covers, benefits, review interval |
| `health.programme_enrolment` | Member, programme, status, next review |
| `health.cfg_preventive_schedules` (CFG) | Screening or vaccine, benefit category, age band, sex, interval |
| `health.preventive_use` | Member, schedule, the date last used |

## 4. The processes

### 4.1 Pharmacy claims (F039 to F045)
- **Received** through `POST /health/claims` and the provider portal, in a pharmacy format (`cfg_claim_format_definitions`, OQ-147). The format says which line fields are required.
- **Prescription (F040).** Number, prescriber, date and repeat number are required by the format. A claim past the prescription's validity, or a repeat beyond what was prescribed, pends for a person.
- **Coverage (F041).** The product must be on the formulary in force. Not on it: rejected or paid at the plan's rule (OQ-149).
- **Quantity (F042).** Above the maximum quantity or days' supply: the excess is not paid, and the line says why.
- **Cost share (F043).** The tier's co-payment, then the existing deductible and cap steps. Paying above the reference price falls to the member.
- **Adjudication and payment (F044, F045).** The existing engine, accumulators, approval and payment runs. Nothing new about a run.

### 4.2 Medication controls (F046, F047, F092, F093)
- **Limits.** Per product or ingredient, on top of category limits. The most restrictive wins, as today.
- **Duplicates.** The same ingredient for the same patient while the last days' supply has not run out: pends for a person, not rejected. Early refills have a tolerance (OQ-151).
- **Restrictions.** A product needing prior authorisation pends without one, using the existing preauthorisation. No new approval path.

### 4.3 Chronic conditions (F083 to F085)
- **Registration.** Requested by a provider or staff with the diagnosis and supporting note. Decided by a person with the clinical function (OQ-150). Approved, it has dates and a review date. Declined, with a reason and the member's rights notice.
- **Benefits.** A registered condition's claims draw on the chronic benefit category, not day-to-day benefits.
- **Entitlement.** The medicines allowed under a registration, with a monthly quantity. A claim above it follows §4.1.

### 4.4 Disease management (F086)
Enrolment by a person, for a member with a covered condition. The programme's benefits apply while enrolled. A review is due at its interval, as a task. It can link to a complex case, but does not need one.

### 4.5 Preventive care and vaccinations (F087, F088)
A claim line for a scheduled item is checked against age, sex and interval: within schedule, it draws on the preventive category with no co-payment if the plan says so; outside it, day-to-day benefits. Nothing is sent to the member in this wave.

## 5. What it gates
**Claims only**, and only lines that are pharmacy or scheduled items. A plan with none of this configured adjudicates exactly as it does today.

## 6. Readiness
- **Blocking**, for a tenant with a pharmacy format in force: a formulary, and prescription benefits for each plan.
- **Warnings:** no reviewer holds the clinical function for chronic registration; a plan has no preventive schedule.

## 7. API (summary)
- **Staff:** `/health/chronic-registrations`, `/health/programme-enrolments`, clinical routes with a stated purpose and every read audited.
- **Changed:** claim lines gain the pharmacy fields; line results gain why a quantity was cut.
- **Provider portal:** pharmacy claims; requesting a chronic registration. **Member portal:** the member's own registrations and what remains of an entitlement.

## 8. Decisions
| OQ | Decision | Options | Recommendation |
|---|---|---|---|
| **OQ-147** | The pharmacy claim format | (a) INSYS fields on the existing claim API; (b) a real switch format, with an adapter; (c) both | **(a)** now. A switch integration needs a chosen switch and its specification, and the fields would map onto (a) later. |
| **OQ-148** | Check prescriber and pharmacist registration? | (a) record only; (b) check against the provider register; (c) an external registry | **(a)**, with (b) for a pharmacist already in our register. A registry needs a provider and contract. Compliance to confirm. |
| **OQ-149** | A medicine not on the formulary | (a) rejected; (b) paid at a plan's non-formulary co-payment; (c) pends for a person | **(b)**, with the rule per plan, defaulting to rejected. |
| **OQ-150** | Does chronic registration need clinical review? | (a) a person with the clinical function decides each; (b) automatic when the diagnosis code is on the list | **(a)**. It is a medical decision that binds benefits. (b) could follow for clear-cut conditions. |
| **OQ-151** | Early refill tolerance | (a) none; (b) a per-plan percentage of days' supply; (c) a fixed number of days | **(b)**, default 10%. To be confirmed by whoever will run the benefit. |
| **OQ-152** | Generic substitution and reference pricing | (a) the member pays above the reference price; (b) also require a generic where one exists | **(a)**. (b) changes clinical choice, and needs a rule per product. |
| **OQ-153** | Medicine codes | (a) a tenant-loaded list (`ref_medicine_formulary`); (b) a licensed national list by feed | **(a)** now, with an import. A feed needs a licence decision. |
| **OQ-161** | What a principal member sees of a dependant | Decided by Ade | The principal member does not see an adult dependant's medicines, chronic conditions or diagnoses. A minor's records stay visible to the principal member. Compliance to confirm. |

## 9. How it is built
- **5b.1 (F039–F045):** the line fields, format, formulary and tier steps in the engine, quantity checks. Worked examples first, as for ADR-012.
- **5b.2 (F046, F047, F092, F093):** medication limits, duplicates and restrictions.
- **5b.3 (F083–F085):** chronic registration, benefits and entitlement.
- **5b.4 (F086–F088):** disease programmes, preventive schedules and vaccinations.

## 10. Tests
- **Money:** worked examples for each tier, the reference price, a quantity cut, and a limit on top of a category limit; a reversal gives back an entitlement's use as well as the accumulators.
- **Dates:** days' supply, refill tolerance and intervals across month ends and 29 February, on the fixed clocks; age bands on a birthday.
- **Privacy:** no medicine, diagnosis or condition in events, logs, notices or audit detail; a user without the clinical function sees none; a provider sees only its own; a member's dependant sees only their own.
- **Unchanged:** a plan with nothing configured adjudicates exactly as today, tested against the existing worked examples.
- **End-to-end:** a pharmacy claim from receipt through a payment run; a chronic registration through to a claim drawing on it.
