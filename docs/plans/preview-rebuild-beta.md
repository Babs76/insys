# Preview rebuild for the beta: plan, not carried out

**Status: prepared 19 November 2026.** Ade, 19 November: step 0.5 (the read-only memory measurement) is approved now. The age key matches `PREVIEW_AGE_RECIPIENT` (Ade checked, 17 November), so 0.4 is done. The reset waits for Ade's approval after the front end's repin is proven. AI Assist stays off at the rebuild; section 6 lists what turning it on for the beta needs. Until then the Preview stays frozen: no `upgrade`, `reset`, reboot or rescue mode (`docs/handover-backend.md`, "Rules for the Preview").

**What it does:** brings the Preview from `d488b32` (its last chart run, `upgrade` 42190162209, 6 November) to `main`, with a new OpenBao, through the `reset` action. Between them are 112 commits, migrations V0131 to V0143, and the chart's new ClamAV.

**Why `reset` and not `upgrade`:** OpenBao has been sealed since 7 November, and its unseal key is lost. `upgrade` never initialises OpenBao (`deploy/preview/preview.sh`, `upgrade`), so the API would start against a sealed key store. Row 38's other path, replacing only OpenBao and keeping the databases, has no procedure or script. It would leave the one existing data key's fields unreadable, and how the API treats that orphaned key is untested. So this plan uses the documented `reset`.

**Segregation of duties enforcement (Ade, 20 November 2026, OQ-197):** the release carrying `sod-baseline` refuses the release or verify side to any user holding both sides of a baseline rule, from the moment it is deployed, on every tenant. It reaches the Preview only through a reset (the `reset` action here; for the front end's demo stack, `demo:up`), never through an in-place upgrade (`upgrade` here, `demo:upgrade` in the front end). Any in-place upgrade of an existing tenant first reads `GET /identity/access-reviews/segregation-conflicts` on that tenant and stops if the list isn't empty.

## 0. Before approval (no change to the Preview)
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
