# INSYS Preview: the plan

**Status: signed by Ade, with the decisions below, 2 November 2026. Nothing here is built, and no server exists.** Workstream F's remaining slices come first; Wave 7 is paused after 7.1 until the Preview is live.

**What it is:** a hosted environment for OpenFactor's functional team, with all built functionality, sample data only, and every external integration stubbed and labelled. Not production.

## Ade's decisions at signing
1. **Domain:** the console on `insys.project-demo.app`, Keycloak on `id.insys.project-demo.app`. Ade creates both A records when the provision step prints the IP. `.app` is on the HSTS preload list, so HTTPS is mandatory from the first request.
2. **Repository:** stays public for now. Making it private is planned for after Ade's trip, with a self-hosted CI runner, as private Actions minutes at about 18,000 a day would be very expensive. Meanwhile nothing sensitive goes in workflow logs or artifacts; the domain and IP are fine.
3. **Accounts:** as proposed under Users.
4. **TLS:** TLS-ALPN-01 on 443. If it doesn't work, port 80 may open only for the ACME challenge and the HTTPS redirect. No DNS API token.
5. **Durability is a defect for production too:** OpenBao and Keycloak get persistent storage in the chart itself, with a test that a pod restart keeps keys and accounts (register row 1). **OpenBao's unseal keys and root token are never stored on the server or in GitHub.** They go to Ade, encrypted to Ade's age key, like the passwords.

## Server
**One Hetzner CX43 in Nuremberg (NBG1), with Helsinki as the fallback: 8 shared vCPU, 16 GB RAM, 160 GB NVMe, 20 TB traffic.**

| Item | Monthly, excl. VAT |
|---|---|
| CX43, including IPv4 | €16.49 |
| Hetzner daily backups, 20% of the server | about €3.30 |
| Documents in-cluster (SeaweedFS, on the server's disk) | €0 |
| **Total** | **about €19.79** |

- **Checked 2 November 2026.** The server price is from [Hetzner's cost-optimized page](https://www.hetzner.com/cloud/cost-optimized/). The backup rate is from third-party sources only, as Hetzner's pages I could read don't state it. Hetzner repriced twice in 2026, so check the price when ordering.
- **Availability must be checked first.** The pages as I read them marked every plan "currently unavailable", and I can't tell whether that's real or a result of reading the page without choosing a location. The first step is to look in the Hetzner console or API. If CX43 can't be had, the fallback is CPX42 (8 vCPU, 16 GB, 320 GB) at €69.99, with backups about €83.99; the plan stays the same.
- **Why this size (an estimate, not a measurement):** the API's limit is 2 GB, PostgreSQL about 2, Keycloak about 1, k3s and Traefik about 1, OpenBao, SeaweedFS and the console about 1: about 7 GB at peak. An 8 GB CX33 (€8.99) would have no headroom during a seed or a report. The server can be resized if use shows otherwise.
- **Documents:** in-cluster rather than Object Storage (€6.49 a month for 1 TB). Sample documents are small, and they're covered by the server's backup. The S3 adapter is the same either way.

## TLS and DNS
- **Let's Encrypt through k3s's bundled Traefik, using the TLS-ALPN-01 challenge,** which needs only 443. I know this from the protocol and haven't tried it here. If it fails, port 80 opens only for the ACME challenge and the HTTPS redirect. No DNS API token.
- **Because `.app` is HSTS-preloaded,** browsers refuse plain HTTP and offer no click-through on a bad certificate. So the first run proves issuance against Let's Encrypt's staging service with `curl`, then switches to the real one, which also keeps failures clear of its rate limits.
- **Two A records for Ade to create,** both to the server's address, which the provision step prints. It's a Hetzner primary IP, so it survives a server rebuild. The run waits for them to resolve before asking for certificates:
  - `insys.project-demo.app`: the console, with `/api/` going to the API.
  - `id.insys.project-demo.app`: Keycloak. It can't share the console's host, as everything other than `/api/` goes to the console (ADR-027). Keycloak's `/admin` paths aren't routed, so its admin console isn't public.

## Firewall
A Hetzner Cloud Firewall, created by the workflow.
- **Inbound:** 443 from anywhere, and nothing else unless the TLS fallback above is needed. The Kubernetes API (6443) stays closed.
- **SSH:** closed by default. A deployment run adds a rule for its runner's address only, and removes it in a step that runs even if the deployment fails. The next run clears any rule left behind. To unseal OpenBao, Ade needs SSH too: see Unsealing.

## Backups
- **Hetzner's daily backups,** seven kept. Its page doesn't say they're consistent, so:
- **A nightly `pg_dump` and OpenBao snapshot** to the server's disk, which those backups then capture. Worst-case loss is a day of the team's work.
- **One restore rehearsed** before handing over to the team.
- **A restored backup comes up sealed,** so the rehearsal includes unsealing it with Ade's key.
- **If all else fails:** rebuild from the seed. Accounts and sample data come back, but not what the team entered.

## Unsealing
OpenBao's unseal key and root token exist only in the encrypted file for Ade, written once by `bao operator init` in the run and held in memory until encrypted. The API keeps only its narrow token, which can do `datakey` and `decrypt` on the Transit key.

**The consequence: after any restart of OpenBao, such as a server reboot, a k3s upgrade or a Hetzner host move, OpenBao is sealed until Ade unseals it.** Until then, anything reading a protected field fails. Only Ade can do it, including while travelling.

**How:** never through the workflow's inputs, which are public in this repository. A workflow action, `open-ssh`, takes Ade's current IP (the IP is fine in logs) and opens SSH for that address with a closing time; Ade then decrypts the key locally and enters it at `bao operator unseal`'s prompt on the server, so it isn't on a command line or in a file there.

## How deployment runs
A manually triggered workflow, `preview.yml`, on `Babs76/insys`, in a GitHub Environment `preview` that requires Ade's approval.

**Ade sets these in that Environment. They never go in a file, chat or session:**
| Name | What |
|---|---|
| `HCLOUD_TOKEN` | Hetzner API token, for that project only |
| `PREVIEW_SSH_KEY` | The private key the workflow uses to reach the server |
| `PREVIEW_AGE_RECIPIENT` | A variable, not a secret: the public half of an [age](https://github.com/FiloSottile/age) key Ade makes and keeps |
| `PREVIEW_DOMAIN` | A variable: `insys.project-demo.app` |
| `PREVIEW_CONSOLE_PULL_TOKEN` | Read-only, only if the console's image in `insys-web` is private |

**Images:** the API and migrator are built in the run and imported directly to the server. The console's image comes from the front end's pipeline.

**Actions, chosen when triggering:** `provision`, `upgrade`, `open-ssh`, `reset`, `rotate-passwords`, `destroy`. `reset`, `rotate-passwords` and `destroy` need the word typed as confirmation.

## Upgrades and resets
- **Upgrade:** pick a git ref. The run builds it, runs `helm upgrade --atomic`, runs migrations as the chart's job, and then the smoke checks. Data and passwords stay. Migrations only go forward, so a bad one means restoring a backup or resetting.
- **Reset:** removes the release and its volumes, reinstalls, reseeds with dates relative to that day, and generates new passwords and a new OpenBao with new keys, so the old encrypted file stops being any use. **It removes everything the team entered,** which is the point of it.
- **Server secrets** (hash key, database passwords, the API's OpenBao token) are generated on first provision and stay only on the server. OpenBao's unseal key and root token don't: see Unsealing.

## Users
Tenant `OFTZA` from the seed, plus generic accounts, each mapped to one template with no extra grants.

| Accounts | Template |
|---|---|
| ADMIN01/02 | `TENANT_ADMIN` |
| PRODUCT01/02 | `PRODUCT_MANAGER` |
| UNDWRIT01/02 | `GENERAL_UNDERWRITER` |
| LIFEUW01/02 | `LIFE_UNDERWRITER` |
| CLAIMS01/02 | `CLAIMS_HANDLER` |
| CLAIMSMGR01/02 | `CLAIMS_MANAGER` |
| HEALTHCLM01/02 | `HEALTH_CLAIMS_ASSESSOR` |
| HEALTHMEM01/02 | `HEALTH_MEMBERSHIP_ADMINISTRATOR` |
| LINECFG01/02 | `LINE_CONFIGURATION_MANAGER` |
| FIN01/02 | `FINANCE_OFFICER` |
| FINMGR01/02 | `FINANCE_MANAGER` |
| PAY01 / PAY02 | `FINANCE_OFFICER` / `FINANCE_MANAGER` |
| DIST01/02 | `DISTRIBUTION_ADMINISTRATOR` |
| REINS01/02 | `REINSURANCE_OFFICER` |
| COMPL01/02 | `COMPLIANCE_OFFICER` |
| DPO01/02 | `DATA_PROTECTION_OFFICER` |
| CS01/02 | `CUSTOMER_SERVICE_AGENT` |
| OPS01/02 | `OPERATIONS_ANALYST` |
| INTEG01/02 | `INTEGRATION_ADMINISTRATOR` |
| AUDIT01/02 | `AUDITOR` |
| HEALTHAUD01/02 | `HEALTH_AUDITOR` |
| APPROVER01/02 | `OFTZA_APPROVER`, the seed's sample role |
| BROKER01, MEMBER01, PROVIDER01 | `BROKER`, `MEMBER`, `PROVIDER`, each linked to a seeded party |
| OPERATOR01/02 | `PLATFORM_ADMIN`, operator tenant |
| STEWARD01/02 | `PLATFORM_STATIC_DATA_STEWARD`, operator tenant |

That's 51 accounts, with `LINECFG01/02` for `LINE_CONFIGURATION_MANAGER` (role templates B, signed 3 November). Keycloak stores usernames in lower case, and sign-in ignores case. **Built** (`tools/Insys.Seed/PreviewAccounts.cs`, run with `--accounts true`): with Keycloak, the seed prints their new passwords as JSON on stdout and nowhere else, for the run to encrypt at once. The operator tenant's four are made by its two platform administrators (the bootstrap's `insys-admin` and `insys-admin-2`), one approving the other's privileged assignments, so all 51 are built. The TOTP secrets for the portal accounts aren't.

**Why the accounts are mapped this way (accepted by Ade):**
1. **PAY01/02:** there's no payments template; payments are in the finance ones. Building and releasing a batch can't be one person (a blocked conflict), so PAY01 is the officer and PAY02 the manager.
2. **Two people on one template doesn't give maker-checker where the rule needs two templates.** A claim payment is drafted by a handler and released by a manager, which is why CLAIMSMGR and FINMGR are added.
3. **APPROVER01/02:** the three due diligence approvals and the non-renewal need one role holding approval and the type's own function, and no template combines them.

**Who approves a product publication: ADMIN01/02, not PRODUCT01/02.** PRODUCT01 or 02 submits, and the other can't approve, because `PRODUCT_MANAGER` names the approval but holds no approve function. The seed gives `TENANT_ADMIN` the authority in force for `PRODUCT_PUBLICATION`, and an administrator holding exactly that template approves the motor product, by the real route, in `OftzaSeedTests`. So the journey completes, with no extra grant, but through an administrator. That a product manager can't approve is a role-template finding for the business review (Open items in the handover), and the quick-start says so plainly.

**Passwords, and OpenBao's unseal key and root token:**
- Generated in the run, one per account, and encrypted with `age` to Ade's public key before upload. **Only the encrypted file goes in the artifact,** since anyone able to read the repository can download artifacts, and this repository is public today. Retention is one day.
- Never committed, and never printed: they're masked in logs and not passed on command lines.
- Only `provision`, `reset` and `rotate-passwords` change passwords, and `upgrade` doesn't. OpenBao's keys change only with a new OpenBao, on `provision` and `reset`.

**MFA:** broker and provider portal accounts must use it (`external-user-access.md`), and any role in a policy's `mfa_required_roles` too. These accounts will be shared, and a code set up on one person's phone locks out the others. So the run generates each TOTP secret and puts it in the same encrypted file, if Keycloak's admin API lets us set it; otherwise the first person to sign in enrols, and shares it. To be proven in the build.

## Built so far (3 November 2026)
- **Chart:** OpenBao and Keycloak durable (`devMode: false`), proven by the smoke test restarting both; Ingress annotations, the API's path, and a sign-in Ingress for Keycloak (`/realms/` and `/resources/` only); the operator bootstrap as a migration job option.
- **`preview.yml` and `deploy/preview/`:** every action written (`preview.sh`), linted with actionlint and shellcheck. **Not yet run:** it needs Ade's secrets, and its first run is the proof. Expect fixes there.
- **The labels' read route** (`GET /api/v1/platform/environment`), **the tenant's 47 accounts** in the seed, and **Keycloak sign-in for the seed**.
- **The quick-start:** `docs/preview/quick-start.md`.
- **Backups:** the nightly job (`deploy/preview/backup.yaml`) and the restore steps (`docs/preview/restore.md`), **not yet rehearsed**.

## What the chart and code still lack
Written before the build; see Built so far above for what's done.
- **Environment:** the simulator is refused in `Production`, and `Production` can't declare itself non-production. So the Preview runs as `ASPNETCORE_ENVIRONMENT=Preview` with `declaredNonProduction: true`.
- **OpenBao:** the chart's runs in development mode, in memory, so **a restart loses every key and so every encrypted field**. A defect for production too (register row 1): the chart itself gets persistent storage and sealed start-up, and the smoke job restarts the pod, unseals with throwaway keys, and reads a protected field back.
- **Keycloak:** the chart's runs `start-dev` with data inside the container, so **a restart loses every account**. It gets production mode on PostgreSQL in the chart, with the hostname and TLS set, and the smoke job restarts the pod and signs in again.
- **The console:** the chart has no console deployment, and the Ingress sends everything to the API. **`insys-web` has no Dockerfile or published image yet.** The front end owns that, and the Preview can't be live without it (ADR-027).
- **The seed signs tokens with the development key,** which the API accepts only in Development and Testing, so it needs Keycloak sign-in. That's already in Workstream F's list.
- **Email:** creating a user sends a Keycloak action email, and the Preview has no mail server, so that step needs switching off for these accounts, with passwords set through Keycloak's admin API.
- **Quick-start:** a short guide for the team: how to sign in, which account does what, what's stubbed, and where a journey stops.
- **Labels:** a permanent console banner, and a list of the stubs in use: email, SMS, push, WhatsApp, ledger, screening and identity, reinsurer and regulator, payments (`SimulatorGateway`). AI Assist stays off. **The read route is built:** `GET /api/v1/platform/environment` (`docs/api/contract-changes.md`). The banner is the front end's.

## Size
Working days for one engineer. My estimates, made without building it, so low to moderate confidence.

| Work | Days (Est) |
|---|---|
| Chart: durable OpenBao and Keycloak with the restart test, TLS on Traefik, environment | 3 to 4 |
| Chart: console deployment and routing (ADR-027's own 2 to 3) | 2 to 3 |
| Workflow: provision, firewall, k3s, upgrade, reset, destroy | 2 to 3 |
| Accounts tool, encrypted artifact, MFA | 1.5 to 2 |
| Stub labels, back end; the quick-start | 1 |
| Backups and a rehearsed restore | 0.5 to 1 |
| Live proof: run, fix, smoke checks | 1 to 1.5 |
| **Total** | **11 to 15** |

**Workstream F's remainder comes first,** at about 5 to 8 days from its plan, so the Preview is live about **16 to 23 working days** from now. **Outside these figures:** the front end's image and banner, and how long Ade's items take.

## Order
1. Workstream F's remaining slices.
2. The chart work, then the workflow, on branches merged as usual.
3. Ade creates the secrets, then runs `provision` and creates the DNS records.
4. Accounts, the restore rehearsal, and a walk through the flows as generic users, all before the team gets access; Ade already holds the encrypted file from step 3.
5. Wave 7 resumes with batch 7.2.

## Making the age key (Ade)
On your own machine; with Homebrew on a Mac:

```bash
brew install age
```

```bash
age-keygen -o ~/insys-preview-age-key.txt
```

- **The private key** is that file. Keep it there, and put a copy in your password manager. Never put it on the server, in GitHub, in chat or in a session. **If it's lost, the passwords and unseal key can't be read; the only way back is `reset`, which removes the team's work.**
- **What to paste:** the line it prints starting `age1...`, the public key. In `Babs76/insys`: Settings, Environments, `preview`, Environment variables, name `PREVIEW_AGE_RECIPIENT`. It isn't a secret.
- **To read a file the workflow hands you:**

```bash
age -d -i ~/insys-preview-age-key.txt preview-credentials.age
```

## Sources
Checked 2 November 2026: Hetzner's [cost-optimized](https://www.hetzner.com/cloud/cost-optimized/), [regular performance](https://www.hetzner.com/cloud/regular-performance/) and [object storage](https://www.hetzner.com/storage/object-storage/) pages; [backups](https://docs.hetzner.com/cloud/servers/backups-snapshots/overview/); the 20% rate from [VPSBenchmarks](https://www.vpsbenchmarks.com/hosters/hetzner); GitHub's [artifact downloads](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts).
