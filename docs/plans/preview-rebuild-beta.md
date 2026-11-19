# Preview rebuild for the beta: plan, not carried out

**Status: prepared 19 November 2026 for Ade's approval. Nothing here has run.** Ade approves once the front end's repin is proven. Until then the Preview stays frozen: no `upgrade`, `reset`, reboot or rescue mode (`docs/handover-backend.md`, "Rules for the Preview").

**What it does:** brings the Preview from `d488b32` (its last chart run, `upgrade` 42190162209, 6 November) to `main`, with a new OpenBao, through the `reset` action. Between them are 112 commits, migrations V0131 to V0143, and the chart's new ClamAV.

**Why `reset` and not `upgrade`:** OpenBao has been sealed since 7 November, and its unseal key is lost. `upgrade` never initialises OpenBao (`deploy/preview/preview.sh`, `upgrade`), so the API would start against a sealed key store. Row 38's other path, replacing only OpenBao and keeping the databases, has no procedure or script. It would leave the one existing data key's fields unreadable, and how the API treats that orphaned key is untested. So this plan uses the documented `reset`.

## 0. Before approval (no change to the Preview)
| # | Step | Who | Check |
|---|---|---|---|
| 0.1 | The front end's repin is proven on `main` (its live run green at the back end's pin) | Front end | Its run's pin and conclusion |
| 0.2 | Pin the console image the beta should serve: the new digest from the front end's `main`, in `deploy/helm/insys/values.yaml`. Today's digest is from the front end's `68b4659`. A values change runs deployment smoke and merges on green, like any `deploy/**` change. | Back end, with the front end's digest | Smoke green on the exact head; the front end names the digest in its `docs/deployment.md`, so its image cleanup keeps it |
| 0.3 | Ade decides the open items below: the unseal key's holder, ClamAV's outbound access, AI Assist | Ade | Recorded in this file |
| 0.4 | Ade's age key is at hand: the private key on his machine and in his password manager. `PREVIEW_AGE_RECIPIENT` in the `preview` Environment is its public line. | Ade | `age-keygen -y` of the private key prints the same `age1…` line as the variable |
| 0.5 | A memory measurement, as nothing measures it today: run `open-ssh`, then on the server `free -m` and `kubectl top pods -A`, with no change. This needs metrics-server, which k3s bundles. If it isn't running, `free -m` alone. | Back end, run approved by Ade | Peak use recorded here, against the 16 GB server |

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
| 1.8 | `seed` | The OFTZA seed, with `--accounts true`: about 1 h 47 min in November's run, and longer now, as the seed batch added rows. Its dates are the tenant's business date (`8010a1c`). | The seed exits 0; `/health` shows outbox lag settled |
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
| 2.7 | Record | Run ids, durations and peak memory in `docs/handover-backend.md`, and in rows 38 and 39 |

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
- **Memory:** the pod requests 1.5 GB and is limited to 3 GB, as it holds its signatures in memory. The earlier estimate for everything else was about 7 GB at peak, never measured. So about 8.5 to 10 GB of 16 GB is expected, an estimate that 0.5 replaces with a measurement.
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
- likely 2 to 3 hours now, an estimate, as the seed has grown;
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
