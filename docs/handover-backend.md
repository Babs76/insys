# Back-end handover

**Draft, 2 November 2026.** The go-live register is written, and so is how to continue. The state of branches is final at the Tuesday 3 November stop.

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
| 13 | Payment and collection provider. **Nothing sends money out today.** Debit order collections and all payouts (claims, refunds, commission, provider remittances, redress) go through one interface, `IPaymentGateway`, whose only implementation is `SimulatorGateway`: no call to a provider, bank or debit order system, and no EFT, payout or debit order file anywhere. Real and working behind it: collection started on `InvoiceIssued` when the method has a provider, payout batches with second-person release, signed provider callbacks, bank statements imported as JSON through the API with matching, unknown outcomes sent to a person. Missing: a real adapter; debit order mandates (`MandateRef` is only a stored reference, with no mandate lifecycle); the provider call moved out of the transaction (OQ-058). Done (`f24f33e`, 1 November): the simulator is refused outside Development and Testing at start-up, on configuration (`PAYMENT_PROVIDER_NOT_ALLOWED`) and on callback; a demo must declare itself non-production in its deployment configuration. A tenant naming the simulator can't be activated or reactivated where it is refused | 10–20 (Est), for one provider with mandates, before certification | Provider and method choice (OQ-058); no real integration is specified | **Yes** |
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
| 24 | Nigeria blockers: retention defaults, default time zone, age of majority. **Descoped by Ade, 2 November: deferred until a first Nigerian customer is chosen; not started.** Appeal routes are static data and stay, as they serve every market (V0126); the Nigerian route itself waits on row 25 | 2–3 (Est, from the 3–4 in the survey) | A first Nigerian customer being chosen | **If** the first customer is Nigerian |
| 25 | Nigeria: "no premium, no cover" domain review; starter pack review sheet and partner sign-off | Unknown until reviewed | Nigerian compliance partner; compliance lead | **If** the first customer is Nigerian |
| 26 | Rounding by currency (Rwanda, Senegal, Côte d'Ivoire) | 5–8 (Doc) | Nothing | **If** the first customer is in one of those |
| 27 | Limits with a currency and two-currency journeys (DRC) | 6–9 (Doc) | Ade on revaluation | **If** the first customer is in DRC or takes a second currency |
| 28 | French wording (Senegal, DRC, Côte d'Ivoire, partly Rwanda) | 5–7 (Doc) | Professional translation | **If** the first customer is in one of those |
| **Demos** | | | | |
| 30 | Workstream F: OFT Insurance South Africa sample data, tenant `OFTZA` (`docs/plans/workstream-f-oftza-seed.md`). Needed for demos and the front end's Phase 13 exit | 8–12 (Est), with the sample tenant block | Batch 7.1 merged; signed by Ade, 2 November | No |
| **AI Assist** | | | | |
| 29 | Wave 7: AI Assist. Started by Ade's decision of 2 November | 64–85 (Doc) | Provider contract; evaluation documents (D-7) | **No**: it ships switched off and INSYS works without it. A tenant set up through AI-3 must pass readiness first (AIA-085) |

The front end's own go-live work isn't in this register.

## How to continue from here

Written so that a new session with no memory of this one can carry on from GitHub alone, on this Mac or as a Claude Code cloud session. Read `CLAUDE.md` first.

### The rules
They are also in `CLAUDE.md`.

- Never force-push a shared branch. Merge main into it.
- Merge only on green CI of the exact head, by fast-forward: `git push origin "<full sha>:refs/heads/main"`. Check that main is an ancestor first.
- Report merges and other state only after checking them: `git ls-remote origin main`, and the run's `conclusion` and `headSha`.
- Never commit CI results to a branch about to merge: it moves the head.
- If an approved head fails and the fix is more than tests, report to Ade before merging, even once it is green.
- Merge without asking only when it's built with its test, green on the exact head, and needs no decision from Ade.
- Decisions for Ade go in reports. **Ade decides the first customer after his trip,** and coordinates remotely.
- **Only one back-end session works at a time.** The Mac session stops before a cloud one starts.
- **Wave 7 is under way (Ade, 2 November),** in the D-8 order: 7.1, 7.2, 7.3, 7.6, 7.7, 7.4, 7.5, 7.8. Report at the end of 7.1 with the measured days. No real model, provider SDK or customer data in 7.1. Nigeria's row 24 stays deferred until a first Nigerian customer is chosen.
- No new work after 18:00 on Tuesday 3 November 2026; handover written and stopped by 21:00. What isn't green by 18:00 stays on its branch and goes in the table below.

### Running tests
**CI is the gate.** There's no need for a full local run. Locally, run the affected test classes only:

```bash
dotnet build -warnaserror
dotnet test --project tests/Insys.ArchitectureTests --no-build
dotnet test --project tests/Insys.IntegrationTests --no-build --filter-class '*DueDiligenceTests'
```

- Use `--project` and `--filter-class` or `--filter-method`. `dotnet test <dir>` is refused. Read counts with `grep -E '^\s+(total|failed|succeeded):'`.
- Integration and end-to-end tests need Docker (Testcontainers), or `INSYS_TEST_PG=<admin connection>`. Where neither exists, push and let CI run them.
- **In a Claude Code cloud session, Docker isn't running at start.** Start it before any integration test with `nohup dockerd >/tmp/dockerd.log 2>&1 &`, then check `docker info`. Checked by Ade on 2 November 2026 in a cloud session on `Babs76/insys`: Payments tests then passed 19 of 19.
- **The machine lock is for Ade's Mac only,** where the front end shares the machine: `tools/locked.sh --wait "<run id>" <command...>`. The run ID is required: without it the script runs the shell's `test` builtin, which exits 0 with no tests run. Never clear a lock whose holder is alive. A cloud session doesn't need it.
- Start CI with `gh workflow run ci.yml --ref <branch>`. It has 19 jobs and takes about an hour when others are queued. Poll a run every 5 minutes at most, as `gh run watch` hit the API rate limit.
- `deployment-smoke` is its own workflow, run on main when `deploy/`, `db/` or the Cloud project change, and by hand once it is on main.
- **After changing:** an `[ErrorCode]`, run the catalogue test with `INSYS_UPDATE_SNAPSHOTS=1`; a contract, `tools/regen.sh`; a migration, `dotnet run --project tools/Insys.StaticData.Generator` and the hard-coded table counts in `MigrationTests` and `StaticDataTests`; a code field, `docs/api/code-field-allowances.tsv`.
- End-to-end hosts run on `WindowClock` (22:30 UTC, no TestDate); new hosts and time-signed seeds must use it. Tests seed with fixed dates, never yesterday.

### The order of remaining work
1. `br-reconcile`: run CI, merge. It holds BR-115's beneficial owner test, nine of the front end's reconciled requests, this handover and the rules in `CLAUDE.md`.
2. `adr-027-smoke`: get `deployment-smoke` green, **remove `adr-027-smoke` from the workflow's `push` branches**, merge main in, run CI, merge.
3. Wave 7, batch 7.1, per `docs/specs/addenda/ai-assist.md` and `docs/srs/INSYS_AI_Assist_SRS.md`: see Wave 7 below. Phase 1's gate is its exit: security tests for tenant isolation, prompt injection and no write tools.
4. After 7.1: the rest of ADR-027 phase 2, which stays blocking go-live in the register: the runbook, generic deployment and running OpenBao (high availability, backup and restore, unsealing). BR-100, and the items under Open below.

### Wave 7, batch 7.1
**State slice by slice; this table is kept current at each merge.** Merge each slice on green on its own.

| Slice | What | State |
|---|---|---|
| 1 | Assist module: schema and role, forced row-level security; settings with the tenant's jurisdiction, residency and legal basis, monthly cap (AIA-050, AIA-062) | Not started |
| 2 | Feature `AI_ASSIST` and the six functions: off by default, in no role template; `ASSIST_OVERLAY_APPROVE` privileged, operator tenant only | Not started |
| 3 | `IAiModelProvider` with the test stub only | Not started |
| 4 | Minimiser (AIA-051) | Not started |
| 5 | Verifier with the §5.1 number rules and all their test cases (AIA-033a) | Not started |
| 6 | Run records, metering and caps (AIA-073) | Not started |
| 7 | Phase 1's gate: security tests for tenant isolation, prompt injection, no write tools | Not started |

### Open items
- **BR-100:** the function registry refuses one endpoint under two functions for one audience, so the case reads declare only `SERVICE_COMPLAINTS`. From reading the code, not a test, a role holding only another case screen is refused those reads. It needs a change to the registry (ADR-026).
- **The same decline gap in Life and General:** their decline handlers take `reason?.CustomerWording`, so an unknown reason sends a notice with no wording, as Health did before BR-127. General's lapse job does too. Not asked for and not fixed.
- **A Health decline with no notice recipient** still goes without a notice, silently.
- **OpenBao's token** is a static token from the secret; Kubernetes auth isn't built. The chart's in-cluster OpenBao is development mode only, and running a real one is the runbook's subject. The security lead's three questions in ADR-027 are unanswered.
- **Other relative dates** in test seeds (`current_date - 10`, `- 30`, `- 100` and the like) weren't swept, as `- 1` was.

## State at the stop

**Interim, 2 November 2026.** Completed by the Tuesday stop. Heads and CI change quickly: check them with `gh run list` before relying on this table. On main (`e6ca9a4`): BR-126, BR-127, the small items, BR-115's first tests, the OpenBao Transit adapter, the images and the Helm chart.

| Branch | State | What's left |
|---|---|---|
| `br-reconcile` | Holds this handover | CI on its head, then merge |
| `adr-027-smoke` | Smoke runs in progress; deploys and signs in, but hasn't passed end to end | Green, remove the temporary trigger, merge main in, CI, merge |
| Wave 7 | Not started | See the table above |

Nigeria: appeal routes (V0126) are merged; the rest of row 24 is descoped and wasn't started.
