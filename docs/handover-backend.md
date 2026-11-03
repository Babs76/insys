# Back-end handover

**Interim, 3 November 2026; final at the stop on Wednesday 4 November (Ade moved it from Tuesday).** The go-live register is written, and so is how to continue. The state of branches is made final at the stop.

## First: the INSYS Preview milestone
**Decided by Ade, 2 November 2026; it comes before everything else.** A hosted environment for OpenFactor's functional team, with all built functionality, sample data only, and every external integration stubbed and clearly labelled. Not production.

- **Order:** finish Workstream F's remaining slices, then stand up the Preview.
- **Wave 7 is paused after 7.1** until the Preview is live. Batch 7.2 doesn't start before then.
- **The plan is [`docs/plans/insys-preview.md`](plans/insys-preview.md): one Hetzner server, k3s, the ADR-027 chart. Signed by Ade, 2 November, with decisions recorded at its top.** It starts after Workstream F. The restore rehearsal comes before the team gets access.
- **Never handle Ade's secrets.** The Hetzner API token goes in GitHub, set by Ade, never in a file, chat or session. Generated passwords, and OpenBao's unseal keys and root token, are only ever written encrypted to Ade's key, as this repository is public; the unseal keys and root token are never stored on the server or in GitHub. Nothing sensitive goes in logs or artifacts.

## Go-live register

Everything found between INSYS and a first live customer, at `91cb494` plus the docs on `next-batch`. Ade decides the first customer, its country and its lines after his trip; the conditional rows wait on that.

- **Size** is working days for one engineer, tests included; review, CI and outside parties' time are not counted. **Doc** means the figure is in a signed or reported document. **Est** means my estimate made for this register, without a chosen provider or specification, so treat it as low confidence.
- **Blocks go-live:** **Yes**, **No**, or **If** with the condition.

**Blocking for any first customer: about 57 to 96 days of engineering**, of which only ADR-027's 17 to 21 is from a document. It excludes the conditional rows, the outside parties' time, and whatever the penetration test or compliance answers find.

| # | Item | Size (days) | Waits on | Blocks go-live |
|---|---|---|---|---|
| **Deployment and assurance** | | | | |
| 1 | ADR-027 phase 2: OpenBao adapter, Helm chart and container images, and the CI deployment job are merged; runbooks are left, including that database-owner credentials are held under dual control (`docs/runbooks/deployment.md`). **Defect, Ade, 2 November: the chart's OpenBao and Keycloak lose everything on a restart;** both get persistent storage in the chart, with a test that a pod restart keeps keys and accounts | 17–21 (Doc), plus 2–3 (Est) for serving the console (BR-132, decided in ADR-027: the chart serves it by default), plus 1–2 (Est) for that defect | Scheduled before the first customer | **Yes** |
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
| 31 | Revoking an invitation (group A, Ade, 2 November). An administrator can't stop an invitation sent to the wrong person, or to someone who leaves before signing in: `Invited` has no way out but first sign-in, `disable` and `lock` are refused (`UserAccountTests.InvitedUser_CannotBeLockedOrDisabled_IsInvalidStateTransition`), and first sign-in activates the user. Needed: an administrator revokes or disables an Invited user, audited, with the Keycloak user disabled, and a test that the revoked invitation can't be used. Adds `Invited-DisableUser->Disabled` or its own trigger to the domain model's lifecycle | 1–2 (Est) | Nothing | **Yes** |
| **Markets** | | | | |
| 24 | Nigeria blockers: retention defaults, default time zone, age of majority. **Descoped by Ade, 2 November: deferred until a first Nigerian customer is chosen; not started.** Appeal routes are static data and stay, as they serve every market (V0126); the Nigerian route itself waits on row 25 | 2–3 (Est, from the 3–4 in the survey) | A first Nigerian customer being chosen | **If** the first customer is Nigerian |
| 25 | Nigeria: "no premium, no cover" domain review; starter pack review sheet and partner sign-off | Unknown until reviewed | Nigerian compliance partner; compliance lead | **If** the first customer is Nigerian |
| 26 | Rounding by currency (Rwanda, Senegal, Côte d'Ivoire) | 5–8 (Doc) | Nothing | **If** the first customer is in one of those |
| 27 | Limits with a currency and two-currency journeys (DRC) | 6–9 (Doc) | Ade on revaluation | **If** the first customer is in DRC or takes a second currency |
| 28 | French wording (Senegal, DRC, Côte d'Ivoire, partly Rwanda) | 5–7 (Doc) | Professional translation | **If** the first customer is in one of those |
| **Demos** | | | | |
| 30 | Workstream F: OFT Insurance South Africa sample data, tenant `OFTZA` (`docs/plans/workstream-f-oftza-seed.md`). Needed for demos and the front end's Phase 13 exit | 8–12 (Est), with the sample tenant block | Batch 7.1 merged; signed by Ade, 2 November | No |
| **AI Assist** | | | | |
| 29 | Wave 7: AI Assist. Started by Ade's decision of 2 November; paused after 7.1 until the Preview is live | 64–85 (Doc) | Provider contract; evaluation documents (D-7) | **No**: it ships switched off and INSYS works without it. A tenant set up through AI-3 must pass readiness first (AIA-085) |

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
- **Wave 7 is paused after 7.1 until the INSYS Preview is live (Ade, 2 November).** It then resumes in the D-8 order: 7.2, 7.3, 7.6, 7.7, 7.4, 7.5, 7.8. Nigeria's row 24 stays deferred until a first Nigerian customer is chosen.
- **The Mac session stops on Wednesday 4 November 2026:** nothing new after 07:00, the handover merged and checked by 10:00. A cloud session continues from this document and GitHub.

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
1. Workstream F's remaining slices: `docs/plans/workstream-f-oftza-seed.md` (its State lists them). The one the Preview needs first is **Keycloak sign-in for the seed**, below.
2. The INSYS Preview, signed by Ade: `docs/plans/insys-preview.md`.
3. Batch 7.2, once the Preview is live, then the D-8 order: 7.3, 7.6, 7.7, 7.4, 7.5, 7.8.
4. The rest of ADR-027 phase 2, blocking go-live in the register: the runbook, generic deployment and running OpenBao (high availability, backup and restore, unsealing). The Preview's chart work covers the console deployment. BR-100, and the items under Open below.

### Workstream F: what's left, and how
The seed (`tools/Insys.Seed`) now loads a working insurer through the API: see the plan's State. Its tests are the `OftzaSeed*Tests` classes in `tests/Insys.IntegrationTests`, about 6 minutes each, except `OftzaSeedResumeTests` at about 30, which stops the seed once after every kind of step and runs it again. They already load the seed on a fresh database and read it back on every CI run, so the plan's separate CI job isn't needed; a cloud session may decide otherwise if the shards grow past 45 minutes (the timings job warns at 35).

- **Keycloak sign-in: built.** Where the API takes only Keycloak's tokens, run the seed with `--keycloak-url`, `--keycloak-admin` and the admin's password in `INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD` (see `tools/Insys.Seed/Program.cs`). It makes users in INSYS's own realm, so Identity creates their Keycloak accounts (`oftza.<username>`), and signs in as each through the realm's `insys-seed` client, which is disabled in the realm and enabled only for the run. It sets its own generated passwords on the accounts it uses, held in memory, so **the Preview's password step must run after the seed**. Invitation emails park as `KEYCLOAK_EMAIL_UNSENT` without a mail server. Tested against the real realm in `KeycloakRealmTests`; not yet run end to end against a deployed API.
- **Cases of the other families** (enquiry, dispute, appeal): only a complaint about a claim is seeded.
- **Notification templates** beyond the case notices.

### Wave 7, batch 7.1
**State slice by slice; this table is kept current at each merge.** Merge each slice on green on its own.

| Slice | What | State |
|---|---|---|
| 1 | Assist module: schema and role, forced row-level security; settings with the tenant's jurisdiction, residency and legal basis, monthly cap (AIA-050, AIA-062) | Built |
| 2 | Feature `AI_ASSIST` and the six functions: off by default, in no role template; `ASSIST_OVERLAY_APPROVE` privileged, operator tenant only | Built |
| 3 | `IAiModelProvider`: `NoAiModelProvider` in a deployment, the stub only in `Insys.Testing` | Built |
| 4 | Minimiser (AIA-051) | Built |
| 5 | Verifier with the §5.1 number rules and all their test cases (AIA-033a) | Built |
| 6 | Run records, metering and caps (AIA-073) | Built |
| 7 | Phase 1's gate: security tests for tenant isolation, prompt injection, no write tools | Built, and proven by mutation |

All on `wave7-1-fixes`, as one head, as the review's fixes touch every slice. Check `git log origin/main` for whether it has merged.

**What batch 7.2 must do, from the review:**
- Put the currency beside every amount in the data sent and checked (Ade, 2 November): a stated `USD 1240` passes against a plain `1240`, as §5.1's table says it may.
- **No free text goes to a model until real name detection exists (D-15, addendum §5.4).** Build prompts only from allowed structured fields. Remove `UntrustedText` and `MinimiseFreeText` from the runner first, and move the gate's injection test to what's left. Never interpolate record data into `Instructions`.
- Weigh the transaction held open across the model call (`RunAssist`) against AIA-072.

**Deferred from the review, not must-fix:**
- `inference_region` is the tenant's own statement; before 7.3 compare it with the adapter's real route (D-1).
- The operator has no route to set a tenant's cap: the column and `Assist:DefaultMonthlyTokenCap` exist.
- The residency check applies to every feature, while AIA-050 names AI-2. It will wrongly block AI-1 at 7.4.
- The Assist routes refuse the operator tenant (`HoldsBusinessData`), so 7.6's overlay route can't live in that group as it stands. The §5.3 fallback needs its own narrow grant, not `ASSIST_OVERLAY_APPROVE`.
- Over-masking fails safe but costs use: amounts of 8 digits or more are masked, and so explanations of them fall back to the plain breakdown. The verifier's space grouping can join `Step 1 240` into 1240.
- `run_payload` can still be updated and deleted by the module role; a retention job should have its own path.

### Open items
- **A product manager can't approve a publication:** for the business review. `PRODUCT_MANAGER` names `PRODUCT_PUBLICATION` but holds no approve function, so in the seed an administrator approves it, with `TENANT_ADMIN` holding the authority. No extra grant was made; the Preview's quick-start says so.
- **An underwriter can't quote or issue alone:** for Ade, found building the seed. `LIFE_UNDERWRITER` and `GENERAL_UNDERWRITER` hold neither `RATING_CALCULATE_PREMIUM` nor `RULES_EVALUATE_RULE_SET`, nor `INTEGRATION_ACKNOWLEDGE_DISCLOSURE`, and issuing needs all three. So a real underwriter is refused (403 `FUNCTION_NOT_GRANTED`). The templates are signed, so the seed adds no grant: the product manager rates and evaluates, and the integration administrator records the acknowledgement. The Preview's team will meet this.
- **No tenant role maintains Life, General or Health configuration once live:** for Ade, found building the seed. `CONFIG_LIFE`, `CONFIG_GENERAL` and `CONFIG_HEALTH` are only in the platform steward's template, so after activation a tenant's staff can't load, say, a new scheme's contribution rule. The seed's loaders stay until that rule is in, then end.
- **A migrated unpaid premium doesn't start a Life policy's grace:** a defect, found building the seed. `POST /billing/migration/outstanding-premiums` makes an overdue invoice and Billing reports the arrears, but it attaches no instalment, and `PolicyArrearsQuery` counts through instalments. So the missed count stays 0 and the policy never lapses. A migrated Life book in arrears would look in order. The seed goes round it, and the fix is unbuilt.
- **The test host supplies `If-Match` and `Idempotency-Key` when a call leaves them out** (`TestPreconditionsHandler`), so a test can pass where a real client is refused. The seed's main test now runs on `StrictClientFor`, which adds nothing, and the seed reads a record's version itself when the API asks. Other API clients may have the same gap.
- **INSYS has no route that cancels a Life policy,** so the sample Life book has no cancelled status. For the business review.
- **A seed run that crosses midnight UTC can fail at activation** with `PRODUCT_PUBLISHED` not ready, seen once; run again, it completed. Not investigated.
- **BR-100:** the function registry refuses one endpoint under two functions for one audience, so the case reads declare only `SERVICE_COMPLAINTS`. From reading the code, not a test, a role holding only another case screen is refused those reads. It needs a change to the registry (ADR-026).
- **The same decline gap in Life and General:** their decline handlers take `reason?.CustomerWording`, so an unknown reason sends a notice with no wording, as Health did before BR-127. General's lapse job does too. Not asked for and not fixed.
- **A Health decline with no notice recipient** still goes without a notice, silently.
- **OpenBao's token** is a static token from the secret; Kubernetes auth isn't built. The chart's in-cluster OpenBao is development mode only, and running a real one is the runbook's subject. The security lead's three questions in ADR-027 are unanswered.
- **Other relative dates** in test seeds (`current_date - 10`, `- 30`, `- 100` and the like) weren't swept, as `- 1` was.

## State at the stop

**Interim, 2 November 2026.** Completed by the Tuesday stop. Heads and CI change quickly: check them with `gh run list` before relying on this table. On main: BR-115, 126, 127, 128, the reconciled requests, the small items, the OpenBao Transit adapter, the images, the Helm chart and the smoke job, which also passed on main.

| Branch | State | What's left |
|---|---|---|
| Next | Workstream F's remaining slices, then the Preview | See the order of work |

Nigeria: appeal routes (V0126) are merged; the rest of row 24 is descoped and wasn't started.
