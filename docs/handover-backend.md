# Back-end handover

**Final, at the stop on Wednesday 4 November 2026.** The go-live register is written, and so is how to continue. The state of branches is under "State at the stop": every branch is merged, and nothing is in progress.

## First: the INSYS Preview milestone
**Decided by Ade, 2 November 2026; it comes before everything else.** A hosted environment for OpenFactor's functional team, with all built functionality, sample data only, and every external integration stubbed and clearly labelled. Not production.

- **Order:** Workstream F is done; the Preview is built but not yet provisioned (`docs/plans/insys-preview.md`, Built so far). Ade's secrets, variables and the console's image access are set, and the environment's reviewer rule is saved. The `check` runs passed. The first `provision` stopped on Hetzner's account core limit (see "The first provision" below).
- **Wave 7 is paused after 7.1** until the Preview is live. Batch 7.2 doesn't start before then.
- **The plan is [`docs/plans/insys-preview.md`](plans/insys-preview.md): one Hetzner server, k3s, the ADR-027 chart. Signed by Ade, 2 November, with decisions recorded at its top.** It starts after Workstream F. The restore rehearsal comes before the team gets access.
- **Never handle Ade's secrets.** The Hetzner API token goes in GitHub, set by Ade, never in a file, chat or session. Generated passwords, and OpenBao's unseal keys and root token, are only ever written encrypted to Ade's key, as this repository is public; the unseal keys and root token are never stored on the server or in GitHub. Nothing sensitive goes in logs or artifacts.

### Running the Preview: the five open points in Ade's deployment guide
1. **The workflow, its actions and the confirmation words.** The workflow is `preview` (`.github/workflows/preview.yml`): Actions, then preview, then Run workflow. It runs in the Environment `preview`, so each run waits for Ade's approval. Its inputs:
   - `action`: `check` (the default), `provision`, `upgrade`, `open-ssh`, `reset`, `rotate-passwords` or `destroy`. `check` creates and changes nothing: it prints PASS or FAIL for the Hetzner token, the server type's availability in the location now, `PREVIEW_SSH_KEY` (with its public fingerprint, to match against Ade's key), `PREVIEW_AGE_RECIPIENT` and the pull token's read of the console's pinned manifest, and no secret.
   - `confirm`: for `reset`, `rotate-passwords` and `destroy` only, the action's own name typed exactly: `reset`, `rotate-passwords`, `destroy`. Anything else stops the run before it starts.
   - `ssh_from`: for `open-ssh`, the address to allow, Ade's own IP.
   - `acme`: `staging` (the default) until a run has shown a staging certificate on both hosts, then `production`.
   - The server: the Environment's variables `PREVIEW_SERVER_TYPE=cpx42` and `PREVIEW_LOCATION=fsn1` (Ade, 3 November), passed through by `preview.yml` (merged 3 November, with `PREVIEW_CONSOLE_PULL_USER`, which it lacked too). `provision` stops if either is unset, or if the reserved IPv4 already exists in another location: an address can't move between locations, so `fsn1` must be set before the first run.
   The first run is `check`, then `provision` with `acme: staging`. It prints the server's address; Ade then creates the two A records (`insys.project-demo.app` and `id.insys.project-demo.app`), and the run waits for them before checking certificates.
2. **Unsealing OpenBao: the key, the user and the command.** SSH is as `root`, with the private key whose text is the `PREVIEW_SSH_KEY` secret: the key pair Ade made for it, kept on his machine. Then:
   1. Run the workflow with `action: open-ssh` and `ssh_from` set to his IP. SSH stays open from that address until the next run closes it.
   2. On his machine, read the unseal key from the run's encrypted file: `age -d -i <his age key file> preview-secrets.age | jq -r .openbao.unsealKey`.
   3. `ssh -i <that private key> root@<the server's address>`, then on the server:
      ```bash
      read -rs key && printf '%s' "$key" | /root/insys/openbao/openbao.sh unseal insys && unset key && kubectl rollout restart deploy/insys-api
      ```
      `read -rs` takes the pasted key without showing it, so it isn't on a command line, in the shell's history or in a file. The script prints nothing on success; `/root/insys/openbao/openbao.sh sealed insys` then prints `false`.
   4. Run any action (`upgrade` changes nothing else) to close SSH again.
3. **What `destroy` removes.** The server and its firewall. **It keeps the reserved IPv4 address** (made with auto-delete off), so the DNS records stay right for a later `provision`; delete it in Hetzner's console if it's no longer wanted. **It does delete the backups:** Hetzner's backups belong to the server and go with it, and the nightly dumps are on the server's own disk. Take a snapshot in Hetzner's console first if anything must be kept. (Hetzner's documentation says backups go with the server; check the console before relying on it.)
4. **The portal accounts' TOTP secrets: not set by the run.** `BROKER01` and `PROVIDER01` need a second factor. Setting a TOTP secret isn't built: I found no call in Keycloak's admin API that creates one, and didn't try the alternative. So the first person to sign in to each is asked to set it up, scans the code with an authenticator, and shares that setup (the code or the authenticator export) with the others privately. To build later: the realm's partial import, which can create a credential.
5. **The console's pull token.** `PREVIEW_CONSOLE_PULL_TOKEN` is a secret in the Environment `preview`: a GitHub classic personal access token with the `read:packages` scope only, from an account that can read `ghcr.io/babs76/insys-console-web`. The run turns it into the cluster's `ghcr` pull secret, on stdin, never on a command line or in a value. The username defaults to `babs76`; set the variable `PREVIEW_CONSOLE_PULL_USER` if the token belongs to another account. The smoke workflow doesn't use it: it pulls with its own token, which needs the package to give `Babs76/insys` read access (package settings, Manage Actions access).

### The check runs (4 November)
- **First, run 41781131696 (on 13df18c):** PASS for the Hetzner token, `PREVIEW_SSH_KEY`, `PREVIEW_AGE_RECIPIENT` and the pull token (it reads the console's pinned manifest). FAIL for cpx42 in fsn1: it asked Hetzner's datacenter API, which Hetzner has removed. The key's public half is `(ED25519) SHA256:tTdH9PPNZlvytBnQmJat+czm4MI0mNpDA6nCDzA50aI`: Ade matches it against his key. This run started without waiting for approval, because the environment had no required reviewer yet. And the pull token's `::add-mask::` command was printed in the middle of its PASS line, where it doesn't work; the value showed only as `***`, so no secret was printed.
- **Fixed on main (6d6dfbd):**
  - Availability now comes from `server_types[].locations[].available`. It's Hetzner's indicator, not a guarantee.
  - hcloud is pinned to v1.70.2, and nothing in `preview.sh` or `preview.yml` uses datacenters any more.
  - The mask goes out on its own line.
  - Ade saved the reviewer rule: Babs76 is required, on main only.
- **Second, run 41800066571 (on main, 6d6dfbd), dispatched at 08:30 UTC:** it waited for Babs76's approval, so the gate works. Once approved, all five items passed. cpx42 showed as available in fsn1 (Hetzner's indicator, not a guarantee), and the pull token read `babs76/insys-console-web@sha256:e0c06bb5…`.

### The first provision (4 November): failed at the server, on Hetzner's account limit
Ade started run 41801699489 himself, on main (6d6dfbd) with `acme: staging`, and approved it. It failed at 08:46 UTC when the server was created: `hcloud: shared core limit exceeded (resource_limit_exceeded)`. The Hetzner project's limit on shared vCPU cores is below the 8 that cpx42 needs. The check can't see this, because availability is per location, not per account.
- **Already created, and kept for the next run:** the SSH key `insys-preview`, the reserved IPv4 `insys-preview` in fsn1, and the firewall (443 only). There's **no server**. `provision` is idempotent: run again, it skips what exists and creates the server.
- **The address didn't print**, because the run stopped before that line. It's the Primary IP `insys-preview` in Hetzner's console, and it won't change, so Ade can create the two A records from it now.
- **The second provision, run 41803104687 (08:54 UTC, after Hetzner raised the limit to 130 shared vCPUs):** it created the server and installed k3s, and **the address is `2.31.133.22`**: both A records point at it. Then it failed at the chart: `helm` reached for `localhost:8080`, because only k3s's own `kubectl` reads `/etc/rancher/k3s/k3s.yaml` by itself. Fixed on `preview-kubeconfig`: both `helm` calls in `preview.sh` are given `KUBECONFIG`. That branch's state is in "State at the stop". Run `provision` again once it's on main: it skips what exists (the server, k3s, the images' import is repeated, and the secrets are kept) and goes on from the chart.
- **Decided (4 November, 08:53 UTC): raise the limit and keep cpx42.** Ade requests the increase. Once Hetzner grants it, he runs `provision` again himself, with `acme: staging`. The options were: raise the limit (Hetzner console: the account's Limits page, a request for at least 8 shared vCPUs) and run `provision` again with `acme: staging`, unchanged; or choose a smaller type in `PREVIEW_SERVER_TYPE`, which the plan's sizing would need to accept. Don't change `PREVIEW_LOCATION`: the reserved address is in fsn1.

### Decisions Ade asked for (3 November)
- **Role templates, signed by Ade and built:** `docs/plans/role-template-proposals.md`. A: `GENERAL_UNDERWRITER` and `LIFE_UNDERWRITER` rate, evaluate the rules and record the acknowledgement themselves, and `HEALTH_MEMBERSHIP_ADMINISTRATOR` records it at enrolment (six single functions, option 1); each rule evaluation and premium calculation is now on the audit trail. B: the new `LINE_CONFIGURATION_MANAGER` template, held by `LINECFG01/02` in the Preview (51 accounts), and the seed's line configuration managers make and approve the scheme's contribution rule. Option 2 of A is row 33; the fourth segregation rule is row 34.
- **Cancelling a Life policy:** in the register (row 32), planned after the Preview.

## Go-live register

Everything found between INSYS and a first live customer, at `91cb494` plus the docs on `next-batch`. Ade decides the first customer, its country and its lines after his trip; the conditional rows wait on that.

- **Size** is working days for one engineer, tests included; review, CI and outside parties' time are not counted. **Doc** means the figure is in a signed or reported document. **Est** means my estimate made for this register, without a chosen provider or specification, so treat it as low confidence.
- **Blocks go-live:** **Yes**, **No**, or **If** with the condition.

**Blocking for any first customer: about 57 to 96 days of engineering**, of which only ADR-027's 17 to 21 is from a document, plus rows 31 to 33 and 35, added since (6 to 8.5 days, Est). It excludes the conditional rows, the outside parties' time, and whatever the penetration test or compliance answers find.

| # | Item | Size (days) | Waits on | Blocks go-live |
|---|---|---|---|---|
| **Deployment and assurance** | | | | |
| 1 | ADR-027 phase 2: OpenBao adapter, Helm chart and container images, and the CI deployment job are merged; runbooks are left, including that database-owner credentials are held under dual control (`docs/runbooks/deployment.md`). **Defect, Ade, 2 November: the chart's OpenBao and Keycloak lost everything on a restart.** Now both can be durable (`devMode: false`): OpenBao on a volume, sealed at start, and Keycloak on PostgreSQL, with the smoke test restarting both (branch `preview-chart`, check whether it has merged) | 17–21 (Doc), plus 2–3 (Est) for serving the console (BR-132, decided in ADR-027: the chart serves it by default), plus 1–2 (Est) for that defect | Scheduled before the first customer | **Yes** |
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
| 32 | Cancelling a Life policy (Ade, 3 November). The SRS names no Life cancellation and the domain model's Life lifecycle has no cancelled state, but the specification's disclosures include a cooling-off notice ("You may cancel this policy within the cooling-off period") and Life has termination reason codes (`life.ref_termination_reason_codes`, under Surrender and termination). Needed: cancellation in the cooling-off period with the premium refunded, and termination at the policyholder's request for a policy without cash value, each with its reason code, the schedule stopped, notices and audit, and a refund through Billing. For the business to confirm the cooling-off period and refund rules (PPR). Planned after the Preview | 3–4 (Est) | The Preview | **Yes** |
| 33 | Rules evaluation takes the case's facts from the proposal (Ade, 3 November: role templates A, option 2). Today `POST /rules/decisions` evaluates the inputs its caller gives, so an underwriter who evaluates a case's rules and decides it could give inputs that keep it within their own authority. Each evaluation is recorded with its inputs and audited (`EvaluateRuleSet`), so it can be found but not prevented. Needed: for an underwriting or eligibility decision on a proposal or quote, Rules reads the facts from the subject itself (through Life's and General's contracts), not from the request | about 2 (Est) | The Preview | **Yes** |
| 34 | A fourth segregation rule (Ade, 3 November: role templates B, hardening): no user holds `LINE_CONFIGURATION_MANAGER` together with `GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER`, `CLAIMS_HANDLER` or `HEALTH_CLAIMS_ASSESSOR`, so no one changes the rules they work under. INSYS checks segregation per role today, not across a user's roles, so it needs a check on assignment | about 1 (Est) | Nothing | No |
| 35 | The sign-in page's name in production (Ade, 3 November). With `keycloak.realmDisplayName` empty, the realm file's own name shows: production's sign-in page would say "INSYS Core (local development)". Needed: a proper name in production's values, and production's deployment running `deploy/keycloak/realm-name.sh` as the Preview's does, since the chart's import names only a new realm | under 0.5 (Est) | The production name | Yes |
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
- Start CI with `gh workflow run ci.yml --ref <branch>`. It has 22 jobs and takes about an hour when others are queued. Poll a run every 5 minutes at most, as `gh run watch` hit the API rate limit.
- `deployment-smoke` is its own workflow, run on main when `deploy/`, `db/` or the Cloud project change, and by hand once it is on main.
- **After changing:** an `[ErrorCode]`, run the catalogue test with `INSYS_UPDATE_SNAPSHOTS=1`; a contract, `tools/regen.sh`; a migration, `dotnet run --project tools/Insys.StaticData.Generator` and the hard-coded table counts in `MigrationTests` and `StaticDataTests`; a code field, `docs/api/code-field-allowances.tsv`.
- End-to-end hosts run on `WindowClock` (22:30 UTC, no TestDate); new hosts and time-signed seeds must use it. Tests seed with fixed dates, never yesterday.

### The order of remaining work
1. Workstream F's remaining slices: `docs/plans/workstream-f-oftza-seed.md` (its State lists them). The one the Preview needs first is **Keycloak sign-in for the seed**, below.
2. The INSYS Preview, signed by Ade: `docs/plans/insys-preview.md`.
3. Batch 7.2, once the Preview is live, then the D-8 order: 7.3, 7.6, 7.7, 7.4, 7.5, 7.8.
4. The rest of ADR-027 phase 2, blocking go-live in the register: the runbook, generic deployment and running OpenBao (high availability, backup and restore, unsealing). The Preview's chart work covers the console deployment. BR-100, and the items under Open below.

### Workstream F: what's left, and how
The seed (`tools/Insys.Seed`) now loads a working insurer through the API: see the plan's State. Its tests are the `OftzaSeed*Tests` classes in `tests/Insys.IntegrationTests`, about 6 minutes each, except `OftzaSeedResumeTests` at about 30, which stops the seed once after every kind of step and runs it again. They run in CI's own `seed` job (two of them), outside the integration shards: with the resume test in a shard, shard 1 hit its 45-minute limit on 3 November. The shard tool's `SEED` set lists the classes, and the job checks it matches.

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
- **An underwriter couldn't quote or issue alone:** fixed by role templates A (signed 3 November). The underwriters now rate, evaluate and acknowledge, and the seed has them do it; the rules' inputs still come from the caller (row 33).
- **No tenant role maintained Life, General or Health configuration once live:** fixed by role templates B (signed 3 November): `LINE_CONFIGURATION_MANAGER`. The seed's line configuration managers make and approve the scheme's contribution rule after activation.
- **A migrated unpaid premium didn't start a Life policy's grace:** fixed (`PolicyArrearsQuery` also counts a migrated invoice with no instalment; `tests/Insys.EndToEndTests/LifeProcessTests.cs`, `AMigratedPolicysOutstandingPremiums_CountAsMissed_AndLapseItPastGrace_OQ158_LIF005`).
- **The test host supplies `If-Match` and `Idempotency-Key` when a call leaves them out** (`TestPreconditionsHandler`), so a test can pass where a real client is refused. The seed's main test now runs on `StrictClientFor`, which adds nothing, and the seed reads a record's version itself when the API asks. Other API clients may have the same gap.
- **INSYS has no route that cancels a Life policy,** so the sample Life book has no cancelled status. For the business review.
- **A seed run that crosses midnight UTC can fail at activation** with `PRODUCT_PUBLISHED` not ready, seen once; run again, it completed. Not investigated.
- **BR-100:** the function registry refuses one endpoint under two functions for one audience, so the case reads declare only `SERVICE_COMPLAINTS`. From reading the code, not a test, a role holding only another case screen is refused those reads. It needs a change to the registry (ADR-026).
- **The same decline gap in Life and General:** their decline handlers take `reason?.CustomerWording`, so an unknown reason sends a notice with no wording, as Health did before BR-127. General's lapse job does too. Not asked for and not fixed.
- **A Health decline with no notice recipient** still goes without a notice, silently.
- **OpenBao's token** is a static token from the secret; Kubernetes auth isn't built. The chart's in-cluster OpenBao is development mode only, and running a real one is the runbook's subject. The security lead's three questions in ADR-027 are unanswered.
- **Other relative dates** in test seeds (`current_date - 10`, `- 30`, `- 100` and the like) weren't swept, as `- 1` was.

## State at the stop

**Final, 4 November 2026.** Main is at `6d6dfbd`, with CI and the deployment smoke test green on that exact commit (runs 41791431434 and 41791435306). Check with `git ls-remote` and `gh run list` before relying on this. On main: BR-115, 126, 127, 128, the reconciled requests, the small items, the OpenBao Transit adapter, the images, the Helm chart and the smoke job, and these, from 3 and 4 November:

| Branch | State | What's in it |
|---|---|---|
| `preview-console` | Merged (099fc67) | The console in the Preview, pulled with `PREVIEW_CONSOLE_PULL_TOKEN`. The smoke test pulls with its own token |
| `preview-location` | Merged (13df18c) | The pass-through for `PREVIEW_SERVER_TYPE`, `PREVIEW_LOCATION` and `PREVIEW_CONSOLE_PULL_USER`, and the `check` action |
| `preview-realm-name` | Merged (6d6dfbd) | The sign-in page reads "INSYS Preview (sample data only)" through the Preview's values, and `preview.sh` sets it through the admin API on provision and upgrade, idempotently (row 35 covers production's name). It also has the check fixes listed under "The check runs" |
| `br112-test-date` | Merged (260a0c9, inside 6d6dfbd) | The fix for the red CI of 4 November, below |
| `preview-kubeconfig` | This handover, with `helm` given k3s's kubeconfig. If `git ls-remote` shows main at this branch's head, it's merged; if main is behind it, it waits on Ade's go-ahead | The fix for the second provision's failure |
| Next | `provision` again, from main with the fix | See "The first provision" |

**The red CI of 4 November (fixed).** `DueDiligenceTests`' two BR-112 tests failed on main from 4 November, at any hour. The cause was in the tests, not the product. Their host's clock is fixed at 22:30 UTC on 1 November (2 November in Johannesburg), but they seeded config from the run's UTC date, less a day. From 4 November that date falls after the host's, so the screening partner, the gate and the LOW class weren't in force. They now seed from the host's own date. `TestClock` takes `INSYS_TEST_RUN_AT`, which runs the suite as if it started at that time. Before the fix, both tests failed at `2026-11-04T05:44Z` and passed at `2026-11-03T05:44Z` and `T20:50Z`. After it, they pass at all three and at `2027-03-01T23:30Z`. Any other test whose host has a fixed clock and seeds from `TestClock.Today` would fail the same way; a search for hosts built on a literal `FakeTimeProvider` date found only `AssignRoleBusinessDateTests` besides these, and it seeds nothing from `TestClock.Today`. Hosts given their clock some other way weren't searched.

Nigeria: appeal routes (V0126) are merged; the rest of row 24 is descoped and wasn't started.
