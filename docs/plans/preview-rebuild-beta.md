# Preview rebuild for the beta: plan, not carried out

**Status: prepared 19 November 2026.** Ade, 19 November: step 0.5 (the read-only memory measurement) is approved now. The age key matches `PREVIEW_AGE_RECIPIENT` (Ade checked, 17 November), so 0.4 is done. The reset waits for Ade's approval after the front end's repin is proven. AI Assist stays off at the rebuild; section 6 lists what turning it on for the beta needs. Until then the Preview stays frozen: no `upgrade`, `reset`, reboot or rescue mode (`docs/handover-backend.md`, "Rules for the Preview").

**What it does:** brings the Preview from `d488b32` (its last chart run, `upgrade` 42190162209, 6 November) to `main`, with a new OpenBao, through the `reset` action. Between them are 112 commits, migrations V0131 to V0143, and the chart's new ClamAV.

**Why `reset` and not `upgrade`:** OpenBao has been sealed since 7 November, and its unseal key is lost. `upgrade` never initialises OpenBao (`deploy/preview/preview.sh`, `upgrade`), so the API would start against a sealed key store. Row 38's other path, replacing only OpenBao and keeping the databases, has no procedure or script. It would leave the one existing data key's fields unreadable, and how the API treats that orphaned key is untested. So this plan uses the documented `reset`.

**Segregation of duties enforcement (Ade, 20 November 2026, OQ-197):** the release carrying `sod-baseline` refuses the release or verify side to any user holding both sides of a baseline rule, from the moment it is deployed, on every tenant. It reaches the Preview only through a reset (the `reset` action here; for the front end's demo stack, `demo:up`), never through an in-place upgrade (`upgrade` here, `demo:upgrade` in the front end). Any in-place upgrade of an existing tenant first reads `GET /identity/access-reviews/segregation-conflicts` on that tenant and stops if the list isn't empty.

**Approval authority (Ade, 26 November 2026):** any in-place upgrade of an existing tenant also runs the upgrade check: on each existing tenant, every approval type it can raise with no role holding both the authority and the function to decide it. The upgrade stops if the list isn't empty. The check is `tools/Insys.Seed --upgrade-check true`, as the bootstrap's platform administrator: `GET /tenancy/tenants`, then each tenant's `GET /tenancy/tenants/{id}/readiness`, whose `APPROVAL_AUTHORITY` check (step `SHARED_OPERATIONAL_SETUP`) is that rule. `upgrade` runs it after `chart`, because only the new version's code knows the new approval types, so **by the time it runs the new version is serving: "stops" means the run fails with the list, and the plan's next steps don't proceed until the list is empty** (section 8). The standalone `upgrade-check` action (confirm `upgrade-check`) runs it alone, deploying nothing. Tenants checked: ACTIVE and SUSPENDED (a suspended tenant comes back with what it had, without onboarding again); ONBOARDING once its `SHARED_OPERATIONAL_SETUP` step is completed (before that, completing the step runs the same check); never TERMINATED, nor the operator tenant (OQ-205).

## 0. Before approval (no change to the Preview)
**Refresh the back end and the console together (Ade, 25 November 2026).** From `1c3b2aa` (the money group), the reconciliation exceptions list is a cursor page and a dead-letter discard needs a body (`docs/api/contract-changes.md`). A console built for the earlier contract breaks against the new back end, and the reverse. So the Preview's back end and console are always refreshed in the same run, at a back-end commit and the console digest built for it, never one alone.

| # | Step | Who | Check |
|---|---|---|---|
| 0.1 | The front end's repin is proven on `main` (its live run green at the back end's pin) | Front end | Its run's pin and conclusion |
| 0.2 | Pin the console image the beta should serve: the new digest from the front end's `main`, in `deploy/helm/insys/values.yaml`. Today's digest is from the front end's `68b4659`. A values change runs deployment smoke and merges on green, like any `deploy/**` change. | Back end, with the front end's digest | Smoke green on the exact head; the front end names the digest in its `docs/deployment.md`, so its image cleanup keeps it |
| 0.3 | Ade decides the open items below: the unseal key's holder, ClamAV's outbound access, AI Assist | Ade | Recorded in this file |
| 0.4 | Ade's age key is at hand: the private key on his machine and in his password manager. `PREVIEW_AGE_RECIPIENT` in the `preview` Environment is its public line. | Ade | `age-keygen -y` of the private key prints the same `age1…` line as the variable |
| 0.5 | A memory measurement, as nothing measures it today: the `check` action's read-only `Memory` item prints `free -m`, `kubectl top nodes`, `kubectl top pods -A` and the release's requests and limits. `check` opens SSH for its runner only and closes it at the end. `kubectl top` needs k3s's bundled metrics-server; if it doesn't answer, that is said. | Back end, run approved by Ade | **Done:** `check` 44518919932, 19 November 2026, 14:31 UTC, on `2fc904a`. The figures are under ClamAV below. One reading at an idle time, not a peak. |

| 0.6 | **Prepare memory requests and limits** (Ade, 19 November 2026: prepare only). The chart sets none for Keycloak, PostgreSQL, OpenBao or SeaweedFS today: their templates (`deploy/helm/insys/templates/keycloak.yaml`, `postgres.yaml`, `openbao.yaml`, `seaweedfs.yaml`) have no `resources` key, unlike `api.resources` and `scanner.resources`. A branch adds `keycloak.resources`, `postgres.resources`, `openbao.resources` and `seaweedfs.resources` to the chart's values and templates, with the figures below set in `deploy/preview/values.yaml`. It merges on green CI and smoke before the reset, so the reset applies them; it changes nothing on the Preview by itself. See **Memory requests and limits** below. | Back end, on Ade's approval of the figures | `helm template` shows each pod's requests and limits; smoke green on the exact head |
| 0.7 | **Prepare one memory reading during the seed.** `preview.yml` runs one action at a time (`concurrency: preview`), so a `check` started during the reset waits until it ends: there is no read-only way to measure during the seed today. A small change to `deploy/preview/preview.sh`'s `seed` makes the reset itself print `memory_use` (the `check` action's Memory item) once, into the job log, when the seed prints `seed: activation done`: the start of its business stage, the heaviest writes (policies, invoices, payments, journals). By the whole seed's measured stages (run 44699166104, 20 November 2026, below) that is about 105 minutes in; 45 minutes would have fallen in the products stage. If no such line has come by 120 minutes, it reads then instead. It merges on green smoke before the reset. | Back end, on Ade's approval | The reset's log shows a "Memory during the seed" block |

## 0A. First: a backup, then the pending reboot (before the reset; each run approved by Ade)
The 19 November `check` found a reboot pending, for a newer kernel and `libc6`. The server never reboots itself (Ade, 14 November). Doing it first means the reset and the beta start on the patched kernel, and a reboot during the beta isn't needed soon after.
| # | Step | Check after it |
|---|---|---|
| 0A.1 | `backup` | Its counts and the dump listed, as every backup. OpenBao is sealed with its key lost, so its encrypted fields are unreadable in the dump too; the backup is of the databases as they are, in case the reset has to be abandoned. |
| 0A.2 | `reboot` (Ade, 20 November 2026: added to `preview.sh`; it runs only through `preview.yml` in the approved `preview` environment, with `reboot` typed in confirm). It refuses unless 0A.1's backup of the same day is on the server, reboots with `systemctl reboot` (logged in the journal with the run's id), and waits for a new boot. | The run's own check: a new boot time and the kernel before and after; what still waits for a reboot; the k3s node Ready and every pod back within 10 minutes (OpenBao left out of the wait); OpenBao's state, sealed as expected. The run fails if any of these doesn't hold. |
| 0A.3 | `check` | "Restarts and reboots" no longer reports a pending reboot; the kernel is the new one; k3s and every pod are running. OpenBao reports sealed, as before: the reset replaces it. |

## 1. The reset run (one approval, about 2 to 3 hours)
Start `preview.yml` with `action: reset`, `confirm: reset`, `acme: production`. Ade approves it in the `preview` Environment. The run does these steps in order (`preview.sh`, `reset`):

| # | Step | What it does | Check after it |
|---|---|---|---|
| 1.1 | `ssh_key`, `open_ssh`, `os_policy` | Opens SSH for the runner only. Sets no automatic reboot, and needrestart restarts nothing. | Run log; `check` later shows the policy |
| 1.2 | Uninstall | `helm uninstall insys`, delete every PVC and `insys-secrets` | The release and volumes are gone. **The Preview is down from here.** |
| 1.3 | `cluster`, `images` | k3s and helm as pinned; the API and migrator images built from the run's commit and imported | Image tags in the log match the commit |
| 1.4 | `server_secrets` | A new `insys-secrets`, random values generated on the server: database, role and owner passwords, hash key, object store keys, Keycloak admin password. `kmsToken` starts as `pending`. | The Secret exists, with its keys listed by name, never by value |
| 1.5 | `pull_secret`, `chart` | The chart at `main`: migrations V0001 to V0143 on a fresh database, then the role scripts after them (`migrations.createRoles`), as V0142 needs. Then the bootstrap's two platform administrators, ClamAV, Keycloak, SeaweedFS, `realm_name`, `user_admin`, `storage_check`, `realm_smtp`, `smtp_check`. | Migrate job complete; `storage_check` and `smtp_check` pass; ClamAV's pod is ready (its readiness waits for freshclam's first signatures) |
| 1.6 | `openbao` | A new OpenBao: one unseal key and a root token; Transit key `insys` and policy `insys-kms`; a 10-year API token patched into `kmsToken`; the API restarted | `sealed` reports false; the API's `/health/ready` is 200 |
| 1.7 | `encrypt` | The run's `preview-secrets` artifact, encrypted to Ade's age key: the domain and the OpenBao key and root token. The accounts come later. | Artifact present. **Ade downloads it at once** (retention 90 days) |
| 1.8 | `seed` | The OFTZA seed, with `--accounts true`: about 1 h 47 min in November's run; the whole seed measured at 2 h 0 min (7,199 s) on 20 November 2026 (run 44699166104, `full-seed.yml`'s steps, kind on a GitHub runner, every call through Keycloak): roles at 668 s, loaders, locale and configuration at 2,076 s, personas at 2,288 s, products at 3,192 s, parties, line configuration and readiness at 6,298 s, activation at 6,320 s, business at 7,018 s. The Preview's node isn't the runner, so its time may differ. Its dates are the tenant's business date (`8010a1c`). | The seed exits 0; `/health` shows outbox lag settled |
| 1.9 | `encrypt` | The artifact again, now with the 51 accounts' passwords | `age -d … \| jq '[.openbao.unsealKey != null, .openbao.rootToken != null, (.accounts \| length)]'` prints `true, true, 51` |
| 1.10 | `check_tls` | Both hosts on Let's Encrypt production; Traefik's certificates survive the reset, on its own volume | Issuer is production for `insys.project-demo.app` and `id.insys.project-demo.app` |

## 2. After the run
| # | Step | Check |
|---|---|---|
| 2.1 | Ade keeps the artifact offline in two places, and confirms he can read it | The jq check above, from each copy |
| 2.2 | `check` (about 2 minutes) | OpenBao unsealed; no pending restart; the seal alert's timer present |
| 2.3 | `backup` | Its counts match the seed; the dump is listed; a data key decrypts |
| 2.4 | The restore rehearsal (row 38), never yet run on the Preview: `restore` of that day's backup, unseal with the new key, `backup` again | The counts match the first backup; a data key decrypts after the restore |
| 2.5 | ClamAV works | An EICAR test upload is refused as infected, and a clean upload is released. Freshclam's log shows its mirror answered. |
| 2.6 | Front end | A sign-in on each of the four apps with the new accounts; the console's digest is the one pinned in 0.2 |
| 2.7 | Record | Run ids, durations and memory in `docs/handover-backend.md`, and in rows 38 and 39: 0.7's reading during the seed, compared with 0.5's idle reading and with each pod's limit above. A pod within 80% of its limit during the seed gets a higher limit before the beta |

## 3. The decisions in the plan
**The unseal key, and who holds it.**
- Today: one share, threshold 1 (`deploy/openbao/openbao.sh init`), held by Ade alone, encrypted to his age key. Never on the server or in GitHub.
- Dual control (ADR-027 recommends 3 of 5 holders without cluster admin) is open for the security lead and recorded for production (register rows 1 and 2), not the Preview.
- **Proposal for the beta:** Ade holds it, as now, with two offline copies (2.1).
- The key is needed after every restart of OpenBao, k3s or the server. The unseal procedure is in the handover: open-ssh, `age -d`, `openbao.sh unseal`, restart the API, `check`.

**The secrets, and the age key's role in them.**
- In the cluster: `insys-secrets` is generated on the server and never leaves it. `keycloakUserAdminSecret` is copied from Keycloak's client.
- In GitHub's `preview` Environment, set by Ade:
  - secrets: `HCLOUD_TOKEN`, `PREVIEW_SSH_KEY`, `PREVIEW_SSH_KEY_NEW`, `PREVIEW_ALERT_TO`, `PREVIEW_CONSOLE_PULL_TOKEN`;
  - variables: `PREVIEW_DOMAIN`, `PREVIEW_AGE_RECIPIENT`, `PREVIEW_ACME_EMAIL`, `PREVIEW_LOCATION`, `PREVIEW_SERVER_TYPE`, `PREVIEW_CONSOLE_PULL_USER`.
- The age key protects only what leaves the server: the run's artifact, with OpenBao's unseal key and root token and the 51 passwords. It is encrypted to `PREVIEW_AGE_RECIPIENT`, and only Ade's private key reads it.
- **If the age key is lost, only another `reset` recovers.**

**ClamAV: memory and outbound access (row 39).**
- **Memory:** the pod requests 1.5 GB and is limited to 3 GB, as it holds its signatures in memory.
- **Measured (0.5, `check` 44518919932, 19 November 2026, 14:31 UTC, idle, the Preview still at `d488b32`):**
  - `free -m`: 15,608 MiB total, 3,213 used, 9,948 free, 3,069 buffers and cache, 12,395 available. No swap.
  - `kubectl top nodes`: 3,675 MiB (23%), CPU 285m (3%).
  - `kubectl top pods`, the largest: API 904 MiB, Keycloak 758, PostgreSQL 306, SeaweedFS 209, Traefik 148, OpenBao 96. All the pods together: about 2.6 GiB (2,629 MiB).
  - Requests and limits: only the API (512 MiB request, 2 GiB limit) and the console (32 MiB, 128 MiB) set any. Keycloak, PostgreSQL, OpenBao, SeaweedFS and the backup job set none.
- **What it means (inference, not measured under load):** at idle, everything but ClamAV uses about 3.7 GB of 15.6. With ClamAV at its 3 GB limit, that is about 6.7 GB, leaving about 9 GB. That is more room than the earlier estimate of 8.5 to 10 GB used. The peak under load, a migration run or a backup isn't known. Keycloak and PostgreSQL have no limits, so either could grow without bound.
- **Seen in the same check:** a reboot is pending, for a newer kernel and libc6. OpenBao reports sealed, as known; that is the check's one failure. Nothing else failed.

**Memory requests and limits (0.6), proposed from the 19 November reading.** Each figure is my sizing, an inference from one idle reading, not a measurement under load. 0.7's reading during the seed and 2.7's record check it.

| Pod | Idle (19 November) | Request | Limit | Why |
|---|---|---|---|---|
| Keycloak | 758 MiB | 1 GiB | 1.5 GiB | A JVM. The chart passes no heap option, so the image's own launcher sizes the heap. My understanding, not verified here, is that Keycloak 26's launcher sets the heap as a percentage of the container's memory (`MaxRAMPercentage`, about 70%). With no limit, as today, that percentage is of the whole server; with 1.5 GiB, the heap is about 1 GiB, leaving room for the JVM's own memory. Check at the reset: Keycloak's start log, or `java -XX:+PrintFlagsFinal -version` in the pod, shows `MaxHeapSize`. |
| PostgreSQL | 306 MiB | 512 MiB | 2 GiB | The chart passes no settings, so PostgreSQL's defaults apply: `shared_buffers` 128 MB, `work_mem` 4 MB per sort or hash, `max_connections` 100. The seed and the migrations are its peaks; a limit too low ends the database mid-write, so the limit is generous. |
| OpenBao | 96 MiB | 128 MiB | 256 MiB | Small and steady: one Transit key. |
| SeaweedFS | 209 MiB | 256 MiB | 768 MiB | Grows with uploads during the seed and the beta. |
| API (set today) | 904 MiB | 512 MiB | 2 GiB | Unchanged. |
| ClamAV (set today) | not running | 1.5 GiB | 3 GiB | Unchanged; holds its signatures in memory. |
| Console (set today) | 15 MiB | 32 MiB | 128 MiB | Unchanged. |

- **Sums:** requests 4,000 MiB; limits 9,856 MiB. Add k3s's own pods (Traefik, metrics-server, CoreDNS, the path provisioner: about 341 MiB idle, no limits) and the system outside the pods (about 1 GiB: 3,675 MiB on the node less 2,629 MiB in the pods). With every pod at its limit at once: 9,856 + 341 + 1,046 = about 11,240 MiB of 15,608, leaving about 4,360 MiB. That sum is a worst case: the pods don't all peak together.
- **Not limited:** the nightly backup job (a `pg_dump`). It runs at night, outside the seed.
- **Where they are set:** `keycloak.resources`, `postgres.resources`, `openbao.resources` and `seaweedfs.resources` in `deploy/preview/values.yaml`, once 0.6 adds them to the chart. Nothing changes on the Preview until the approved reset applies the chart.
- **Outbound:** freshclam downloads signatures at start and daily over HTTPS.
  - The Preview's firewall is inbound only, and the cluster has no egress policy, so nothing blocks it today.
  - What row 39 needs is Ade's decision to allow it.
  - The mirror's host isn't named in this repository. ClamAV's default is `database.clamav.net` (my knowledge, unverified here), and 2.5 confirms it from freshclam's log.
  - Without the mirror, clamd has no signatures and every upload stays quarantined: the scan fails closed.

**AI Assist.**
- **Proposal: off for the beta rebuild** (`assist.provider: none`, as the plan for the Preview says).
- Turning it on later is its own change:
  - the chart doesn't yet pass a Claude key (`Assist:Claude:ApiKey`), so it needs a chart change with smoke, and a key in `insys-secrets` that isn't the evaluation's;
  - Ade's decision on the region, `Assist:Claude:InferenceGeo` (US, as the evaluation ran, or not set);
  - D-1's contract terms (zero retention, no training);
  - the tenant's `AI_ASSIST` entitlement.
- The stand-in model can't run on the Preview: the chart refuses it whatever the environment.

## 4. How long the Preview is down
From the uninstall (1.2) until the seed finishes (1.8):
- about 2 hours by November's runs (chart and OpenBao within 5 minutes, the seed 1 h 47 min);
- about 2 h 5 min to 2 h 15 min now: the whole seed measured 2 h 0 min against a deployed API through Keycloak (run 44699166104, 20 November 2026), plus the chart and OpenBao. That was a GitHub runner, not the Preview's node, so this is an estimate for the Preview, not a measurement of it;
- plus the wait for Ade's approval before it starts.

The console answers again after 1.5, but has no users or data until the seed ends. The rehearsal (2.4) takes it down again for the restore, for a time not yet measured.

## 5. What is lost and kept
- **Lost:** all of its data, as `reset` deletes every volume:
  - INSYS's database;
  - Keycloak's users, including any TOTP for BROKER01 and PROVIDER01;
  - SeaweedFS documents;
  - the old OpenBao;
  - every earlier password.
- **Kept:** the server, its reserved address and DNS, k3s, Traefik's certificates, `/var/backups/insys` and Hetzner's backups.
- **Never restore a backup older than the reset.**

## 6. Optional follow-up: AI Assist on the beta
Not part of the rebuild. Each item is its own change, merged on green, and turned on only with Ade's decision.

| # | Needed | Why | Who |
|---|---|---|---|
| 6.1 | A provider contract with zero retention and no training, and the compliance view for South Africa's sample data (D-1, POPIA s72) | D-1: Claude runs globally or in the US, and each jurisdiction waits for these | Ade |
| 6.2 | The region: `Assist:Claude:InferenceGeo` set to `us`, as the evaluation ran, or left unset, so no region is sent and a reported region that isn't US or GLOBAL is refused (`ASSIST_RESIDENCY_MISMATCH`) | Ade, 15 November: "Region: not assumed" | Ade |
| 6.3 | A Claude API key for the Preview only, not the evaluation's (`ASSIST_CLAUDE_API_KEY` in `assist-eval`) | Keys stay per environment, and each can be revoked on its own | Ade makes it |
| 6.4 | The chart passes the key: `assistClaudeApiKey`, an optional key of `insys-secrets`, read as `Assist__Claude__ApiKey`. `assist.provider: claude` refuses to start without it. Today the chart passes only `Assist__Provider`. | A chart change, with deployment smoke | Back end |
| 6.5 | The key reaches the server without passing through GitHub's logs. Ade's steps: on `open-ssh`, `read -rs` the key on the server and patch it into `insys-secrets` (as the unseal procedure does), then `upgrade` with `assist.provider: claude` | It is a secret; this repository is public | Ade, with the back end's steps |
| 6.6 | The model and its settings: `claude-sonnet-5-5`, Ade's provisional choice, with `Effort` medium and the 15-second timeout | The evaluation's results per model | Ade |
| 6.7 | OFTZA's `AI_ASSIST` entitlement, `ASSIST_SETTINGS_MANAGE` to switch it on, and each feature's function: `ASSIST_EXPLAIN_REFUSAL` and `ASSIST_EXPLAIN_PREMIUM` (AI-2), `ASSIST_PRODUCT_DRAFT_RUN` (AI-1), `ASSIST_TENANT_SETUP_RUN` (AI-3), all off by default and in no role template | Each feature is off for a tenant until entitled and enabled | Platform administrator, with a second person |
| 6.8 | Each feature only once its evaluation targets are met (SRS: "each feature reaches users only after its acceptance targets are met on an evaluation set"). AI-2's set exists. AI-1's waits for D-7's documents. AI-3's waits for the tenant samples. | The SRS's rule for switching on | Ade, on the evaluation's results |
| 6.9 | Checks: an explanation of one of OFTZA's sample refusals answers from the catalogue's wording, and the API starts with `assist.provider: claude` and its key. The verifier's refusal of a forged number is already proven by the stub's tests, not on the Preview. | Proof before the team uses it | Back end |

**Order:** 6.1, 6.2 and 6.6 are decisions. Then 6.3 and 6.4, then 6.5 and 6.7, then 6.9. 6.8 governs which features are turned on.

## 7. The console's login theme (BR-168, Ade, 22 November 2026): Keycloak only, outside the reset
Two `preview.yml` actions, each run approved by Ade with its name typed in confirm. They change Keycloak only: never the images, `helm upgrade`, the migrations, the seed, OpenBao (sealed since 7 November, left alone) or the encrypted file. So they never bring INSYS forward, which an upgrade would (the segregation-of-duties baseline needs a reset first). Details are in `docs/handover-backend.md`, "Keycloak login theme (BR-168)".

| Action | What it changes on the server | Check |
|---|---|---|
| `keycloak-theme`, on the commit that synced the theme (`98c53f2`'s, on `keycloak-theme`) | The ConfigMap `insys-keycloak-theme` (the theme, as the chart renders it, marked as the release's); a volume, mount and checksum annotation on `deploy/insys-keycloak`, and a restart after the mount; the realm's `loginTheme` set to `insys`; the ConfigMap `insys-keycloak-theme-previous` noting the theme before; a temporary user `theme-check-<run id>`, whose one-time code and the user are removed at the end | Preflight (read only): Keycloak ready, `/realms/insys` 200 in the cluster, **Keycloak 26.7.5** by its image tag, `version.txt` and admin API (else it stops), the theme exactly the approved files (checked in the checkout, and on the server before anything is applied). Then sign-in, two-factor setup and a one-time code in a browser as the temporary user, every page served with `insys`. **If it fails, the previous theme is set back at once and Keycloak restarted**; the mount stays, harmless |
| `keycloak-theme-revert` | The realm's `loginTheme` set back to the recorded one (or Keycloak's default), and Keycloak restarted; a temporary user, removed at the end | The same check, on that theme |

**Any Keycloak upgrade must test the theme again first:** it is a copy of 26.7.5's `keycloak.v2` template, and `keycloak-theme` refuses any other version.

- **Why a temporary user:** the sample accounts' passwords aren't on the server; the seed gives them only to Ade's encrypted file. The user is Keycloak's only, not INSYS's, with a random password that never leaves the run.
- **At the reset (section 1):** the chart at that commit renders the same ConfigMap and mount, and adopts the ConfigMap `keycloak-theme` made; the realm import takes `loginTheme: insys` from `deploy/preview/values.yaml`, and `chart()` sets it again through `realm-login-theme.sh` after `realm_name`. Step 2.6's sign-ins then show the theme.

## 8. Upgrade steps for OFTZA: the approval authority for the five new types (Ade, 26 November 2026)
For an in-place `upgrade` of the Preview from its back end `ca1bf2b` to a later `main`, not for a `reset` (a reset's seed writes these rows itself). Each step needs Ade's approval of its run, and nothing in this section has been run.

**What is missing, and why.** Five approval types are new since `ca1bf2b` (the approval type enum in `docs/api/openapi-core.json` at `ca1bf2b` against `5fd9f1a`: 28 types then, 33 now): `COMMISSION_CLAWBACK_INVOICE`, `CREDIT_NOTE`, `DEAD_LETTER_DISCARD`, `DISCLOSURE_SETTING_CHANGE` and `PAYOUT_RETRY`. None is tied to a feature or a module's own authority, so every tenant can raise them, and the delegation table decides them. The seed that made OFTZA at `ca1bf2b` wrote no row for them, and an existing tenant's configuration stage isn't written again by a later seed run (`OftzaSeed.Configuration` returns once the tenant's loaders are done). So after the upgrade, the upgrade check lists exactly those five on OFTZA, until the rows below are in force.

**Roles and functions: no change is needed for the check.** A tenant's roles hold the functions copied into `iam.cfg_role_function_assignments` when they were made (the seed's `Roles()`, from the templates at `ca1bf2b`); only a function's permissions are read from the catalogue at request time. At `ca1bf2b` both `FINANCE_MANAGER` and `COMPLIANCE_OFFICER` held `WORKFLOW_APPROVALS`, `WORKFLOW_APPROVE` and `WORKFLOW_REJECT`, so both already hold `approval.approve`, and none of the five types names a further permission for its checker. What `main`'s templates add to those roles since `ca1bf2b` is not on OFTZA and is not needed to decide these five: `COMPLIANCE_OFFICER`'s `CHANGE_DISCLOSURE_SETTING` (to ask for the disclosure setting change, OQ-201) and `CONFIG_PARTY`, `CONFIG_AUDIT`, the documents screens; `FINANCE_MANAGER`'s read screens (DE-021). Bringing OFTZA's roles to `main`'s templates is a role change, and waits for Ade (8.6).

| # | Step | Who | Check |
|---|---|---|---|
| 8.1 | `upgrade-check` on the Preview as it is (confirm `upgrade-check`), before the upgrade | Back end, run approved by Ade | Clear. It can only list types the running version knows, so it doesn't show the five; a list here is an older gap, fixed first |
| 8.2 | `upgrade` (with the segregation-of-duties read above first). The chart deploys the new version; then the upgrade check runs | Back end, run approved by Ade | The run **fails as expected**, listing `OFTZA Active: COMMISSION_CLAWBACK_INVOICE, CREDIT_NOTE, DEAD_LETTER_DISCARD, DISCLOSURE_SETTING_CHANGE, PAYOUT_RETRY`. Any other type or tenant listed: stop and report to Ade |
| 8.3 | **Who can enter and approve the rows (Ade decides).** `workflow.cfg_delegation_and_approval_authority` is Tier 1 static data (`STATIC_DATA_T1`), maintained under `SD_WORKFLOW_AND_CASE_MANAGEMENT_MAINTAIN` and approved under `SD_WORKFLOW_AND_CASE_MANAGEMENT_APPROVE` with `static_data.approve`, both in the `CONFIG_WORKFLOW` sub-domain. In a business tenant only the `TENANT_ADMIN` template holds that sub-domain (`RoleTemplates.cs`), and it names `STATIC_DATA_T1` among what it approves. But a checker also needs a `STATIC_DATA_T1` row for their role in force, and OFTZA's seed at `ca1bf2b` gave `STATIC_DATA_T1` and `STATIC_DATA_T2` only to `OFTZA_SEED_LOADER`, whose two users it revoked and disabled at its end. Read first, without changing anything: OFTZA's `STATIC_DATA_T1` rows in force (`GET /workflow/static/delegation-and-approval-authority` as a tenant administrator). If they are the loader's only, the checker needs a role change Ade decides: either the seed's loader checker enabled and given `OFTZA_SEED_LOADER` again for this change only, then revoked and disabled (as the seed's `Loaders()` and `EndLoaders()` do), or a `STATIC_DATA_T1` row for `TENANT_ADMIN` approved by that loader checker. | Ade | His decision recorded here |
| 8.4 | The five rows, through OFTZA's static data (`POST /workflow/static/delegation-and-approval-authority`, one draft each, submitted), by a tenant administrator as maker: `AUTH-PAYOUT_RETRY-FINANCE_MANAGER` (role `FINANCE_MANAGER`, `PAYOUT_RETRY`, limit 5,000,000 ZAR); `AUTH-COMMISSION_CLAWBACK_INVOICE-FINANCE_MANAGER` (`FINANCE_MANAGER`, 5,000,000 ZAR); `AUTH-CREDIT_NOTE-FINANCE_MANAGER` (`FINANCE_MANAGER`, 250,000 ZAR); `AUTH-DEAD_LETTER_DISCARD-FINANCE_MANAGER` (`FINANCE_MANAGER`, 1 ZAR); `AUTH-DISCLOSURE_SETTING_CHANGE-COMPLIANCE_OFFICER` (`COMPLIANCE_OFFICER`, limit 0, no currency). Each with `delegationAllowed` false. The same codes and values as `main`'s seed (`OftzaSeed.cs`), so a later reset makes the same rows | A tenant administrator (maker) | Each row's newest version is SUBMITTED |
| 8.5 | Each change approved, then activated, by a different person: the checker of 8.3 (SRS-002; the maker can't approve their own) | The checker of 8.3 | Each row EFFECTIVE from the business date |
| 8.6 | Bringing OFTZA's `COMPLIANCE_OFFICER` and `FINANCE_MANAGER` to `main`'s templates (above), if Ade wants the disclosure switch and the read screens on the Preview: role-function assignments, Tier 1 static data, maker and checker as 8.4 and 8.5. **Not needed for the check; waits for Ade** | Ade decides | Recorded here |
| 8.7 | `upgrade-check` (confirm `upgrade-check`) | Back end, run approved by Ade | **Clear: every tenant's list empty.** Nothing else in this plan proceeds until it is |

**Checking before the new version serves:** not done. The rule is the new version's code (the types its modules declare) read against the tenant's data in the new version's schema, so it can only answer once the chart has migrated and started that version. A one-off API pod of the new image against the current database would need the new migrations applied first, which is the upgrade itself. 8.1 against the old version is the nearest read-only step before.
