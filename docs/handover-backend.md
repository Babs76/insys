# Back-end handover

**Draft, 1 November 2026.** The go-live register is written; the state of branches and work in progress is added by the Tuesday 3 November stop.

## Go-live register

Everything found between INSYS and a first live customer, at `91cb494` plus the docs on `next-batch`. Ade decides the first customer, its country and its lines after his trip; the conditional rows wait on that.

- **Size** is working days for one engineer, tests included; review, CI and outside parties' time are not counted. **Doc** means the figure is in a signed or reported document. **Est** means my estimate made for this register, without a chosen provider or specification, so treat it as low confidence.
- **Blocks go-live:** **Yes**, **No**, or **If** with the condition.

**Blocking for any first customer: about 57 to 96 days of engineering**, of which only ADR-027's 17 to 21 is from a document. It excludes the conditional rows, the outside parties' time, and whatever the penetration test or compliance answers find.

| # | Item | Size (days) | Waits on | Blocks go-live |
|---|---|---|---|---|
| **Deployment and assurance** | | | | |
| 1 | ADR-027 phase 2: OpenBao adapter, Helm chart and container images, CI deployment job, runbooks | 17–21 (Doc) | Nothing; scheduled before the first customer | **Yes** |
| 2 | Hosting and production set-up: provider, production Keycloak with MFA, keys, monitoring (`docs/production-readiness.md`, which still assumes Huawei and needs rewriting after ADR-027) | 5–8 (Est) | The customer's or Ade's choice of host; item 1 | **Yes** |
| 3 | Load, penetration, backup and recovery tests (ADR-020) | 10–18 (Est), fixes not included | A production-like environment (items 1, 2); an external tester | **Yes** |
| 4 | More than one API instance: shared permission evictions (ADR-006) | 3–5 (Est) | The load test's result | **If** the load test shows one instance isn't enough |
| 5 | D-13: hosting for Nigerian tenants | Not sized; a decision first | The Nigerian compliance partner; Ade | **If** the first customer is Nigerian |
| **Stubs to replace** | | | | |
| 6 | Email sender | 2–3 (Est) | Provider choice (OQ-088) | **Yes** |
| 7 | SMS sender | 2–3 (Est) | Provider choice (OQ-088) | **Yes** |
| 8 | Push sender | 2–3 (Est) | A portal mobile app, which doesn't exist (OQ-088) | No |
| 9 | WhatsApp sender | 3–4 (Est) | Provider choice; no basis in the specifications | No |
| 10 | General ledger adapter | 3–8 (Est) | The customer's ledger and its layout | **Yes** |
| 11 | Screening and identity verification adapters (also stubs) | 6–10 (Est) | Provider choice | **Yes** |
| 12 | Reinsurer and regulator adapters (also stubs) | 2–5 each (Est) | The customer's reinsurers; the regulator's channel | **If** these are sent electronically; otherwise No |
| 13 | Payment and collection provider. **Nothing sends money out today.** Debit order collections and all payouts (claims, refunds, commission, provider remittances, redress) go through one interface, `IPaymentGateway`, whose only implementation is `SimulatorGateway`: no call to a provider, bank or debit order system, and no EFT, payout or debit order file anywhere. Real and working behind it: collection started on `InvoiceIssued` when the method has a provider, payout batches with second-person release, signed provider callbacks, bank statements imported as JSON through the API with matching, unknown outcomes sent to a person. Missing: a real adapter; debit order mandates (`MandateRef` is only a stored reference, with no mandate lifecycle); the provider call moved out of the transaction (OQ-058). Also: the simulator is registered in every environment with no guard, so a tenant configured with `SIMULATOR` in production would show pending payments, and a signed callback could confirm one, with no money moving. A guard should refuse it outside Development and Testing | 10–20 (Est), for one provider with mandates, before certification | Provider and method choice (OQ-058); no real integration is specified | **Yes** |
| 14 | B-012: pharmacy claims switch adapter | 15–25 (Doc) | A chosen switch and its specification | **If** a Health customer has pharmacy claims. Ade: needed before go-live |
| **Deferred functions** | | | | |
| 15 | F063, B-002: solvency and capital reporting extracts | 8–15 (Est) | Finance on scope. Not in the SRS | **If** the customer expects these from INSYS for SAM. **Ade to decide** |
| 16 | F079, B-003: premium financing | 8–12 (Est) | A customer offering it. Not in the SRS | **If** the customer offers it; otherwise No |
| 17 | F089 to F091, B-004: wellness, treatment compliance and outcomes | 10–20 (Est) | A Health customer asking. Not in the SRS | No |
| **Hardening** | | | | |
| 18 | Code-field allowances: 406 rows marked "to declare" in `docs/api/code-field-allowances.tsv` at `91cb494` (quoted as 407). The API works without them; the contract says less | 5–8 (Est) | Nothing | No |
| 19 | B-014: service account clients through Keycloak's admin API | 2–3 (Doc) | Nothing; made by hand until then | No |
| **Decisions and reviews** | | | | |
| 20 | Compliance and finance: POPIA s23 access export depth (3–5 per module if deeper, Doc), OQ-157, OQ-159, OQ-161, the `CASE` period, 7-year retention and final statuses, VAT inside premium per product, retention-offer frequency, fraud cases opened manually | 0 unless an answer needs a build | Compliance; finance | **Yes**: answers are needed; the build depends on them |
| 21 | BR-009 wording review (OQ-100): function and menu labels | 1–2 (Est) for the changes | The business | **Yes**. Ade made it pre-go-live |
| 22 | OQ-052: actuary on step order, cap rule, PERCENT parameters, with actuary-supplied test cases. Life cash values stay null until the basis is set | 1–3 (Est) for configuration | An actuary | **Yes** for any rated product |
| 23 | Customer set-up: tenant configuration, approval roles, `TENANT_ADMIN` permission list, data migration with reconciliation against the source system. Import is built (B-013); open Health claims are resubmitted | Per customer; not sized | The customer's data | **Yes**; not in the total above |
| **Markets** | | | | |
| 24 | Nigeria blockers: appeal routes, retention defaults, default time zone, age of majority | 3–4 (Doc). Optional: state in the section below | Nothing | **If** the first customer is Nigerian |
| 25 | Nigeria: "no premium, no cover" domain review; starter pack review sheet and partner sign-off | Unknown until reviewed | Nigerian compliance partner; compliance lead | **If** the first customer is Nigerian |
| 26 | Rounding by currency (Rwanda, Senegal, Côte d'Ivoire) | 5–8 (Doc) | Nothing | **If** the first customer is in one of those |
| 27 | Limits with a currency and two-currency journeys (DRC) | 6–9 (Doc) | Ade on revaluation | **If** the first customer is in DRC or takes a second currency |
| 28 | French wording (Senegal, DRC, Côte d'Ivoire, partly Rwanda) | 5–7 (Doc) | Professional translation | **If** the first customer is in one of those |
| **AI Assist** | | | | |
| 29 | Wave 7: AI Assist | 64–85 (Doc) | Ade's return; provider contract; evaluation documents (D-7) | **No**: it ships switched off and INSYS works without it. A tenant set up through AI-3 must pass readiness first (AIA-085) |

The front end's own go-live work isn't in this register.

## State at the stop

To be completed by Tuesday 3 November: branches, what is merged, Nigeria's state, and the last CI and UTC+14 results.
