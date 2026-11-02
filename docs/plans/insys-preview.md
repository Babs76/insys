# INSYS Preview: plan for sign-off

**Status: awaiting Ade's sign-off. Nothing here is built, and no server exists.** Milestone decided by Ade, 2 November 2026. Workstream F's remaining slices come first; Wave 7 is paused after 7.1 until the Preview is live.

**What it is:** a hosted environment for OpenFactor's functional team, with all built functionality, sample data only, and every external integration stubbed and labelled. Not production.

## Decisions needed from Ade
1. **Sign off this plan.**
2. **The domain name** for the two DNS records below.
3. **The repository is public** (`gh repo view Babs76/insys`, 2 November), although its files are marked confidential. So workflow logs and artifacts can be read by people outside OpenFactor. The plan is safe either way, because passwords are encrypted before upload. Whether the repository should be public at all is a separate decision, and making it private is the recommendation.
4. **Three account mappings,** under Users below.

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
- **Let's Encrypt through k3s's bundled Traefik, using the TLS-ALPN-01 challenge.** The firewall allows only 443, and the usual HTTP-01 challenge needs port 80. TLS-ALPN-01 needs only 443. I know this from the protocol and haven't tried it here; the first deployment proves it. The fallback is DNS-01, which would need a DNS API token.
- **Two A records for Ade to create,** both to the server's address, which the provision step prints. It's a Hetzner primary IP, so it survives a server rebuild:
  - `preview.<domain>`: the console, with `/api/` going to the API.
  - `id.preview.<domain>`: Keycloak. It can't share the console's host, as everything other than `/api/` goes to the console (ADR-027). Keycloak's `/admin` paths aren't routed, so its admin console isn't public.

## Firewall
A Hetzner Cloud Firewall, created by the workflow.
- **Inbound:** 443 from anywhere, and nothing else. The Kubernetes API (6443) stays closed.
- **SSH:** closed by default. A deployment run adds a rule for its runner's address only, and removes it in a step that runs even if the deployment fails. The next run clears any rule left behind. If Ade wants standing access, their IP goes in a workflow variable.

## Backups
- **Hetzner's daily backups,** seven kept. Its page doesn't say they're consistent, so:
- **A nightly `pg_dump` and OpenBao snapshot** to the server's disk, which those backups then capture. Worst-case loss is a day of the team's work.
- **One restore rehearsed** before handing over to the team.
- **Keys:** OpenBao's unseal key is a Kubernetes secret on the same server. That's acceptable for sample data only and isn't a pattern for production; the security lead's questions in ADR-027 stay open.
- **If all else fails:** rebuild from the seed. Accounts and sample data come back, but not what the team entered.

## How deployment runs
A manually triggered workflow, `preview.yml`, on `Babs76/insys`, in a GitHub Environment `preview` that requires Ade's approval.

**Ade sets these in that Environment. They never go in a file, chat or session:**
| Name | What |
|---|---|
| `HCLOUD_TOKEN` | Hetzner API token, for that project only |
| `PREVIEW_SSH_KEY` | The private key the workflow uses to reach the server |
| `PREVIEW_AGE_RECIPIENT` | A variable, not a secret: the public half of an [age](https://github.com/FiloSottile/age) key Ade makes and keeps |
| `PREVIEW_DOMAIN` | A variable: the domain |
| `PREVIEW_CONSOLE_PULL_TOKEN` | Read-only, only if the console's image in `insys-web` is private |

**Images:** the API and migrator are built in the run and imported directly to the server. The console's image comes from the front end's pipeline.

**Actions, chosen when triggering:** `provision`, `upgrade`, `reset`, `rotate-passwords`, `destroy`. The last three that lose data or access need the word typed as confirmation.

## Upgrades and resets
- **Upgrade:** pick a git ref. The run builds it, runs `helm upgrade --atomic`, runs migrations as the chart's job, and then the smoke checks. Data and passwords stay. Migrations only go forward, so a bad one means restoring a backup or resetting.
- **Reset:** removes the release and its volumes, reinstalls, reseeds with dates relative to that day, and generates new passwords. **It removes everything the team entered,** which is the point of it.
- **Server secrets** (hash key, database passwords, OpenBao) are generated on first provision and stay only on the server.

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
| BROKER01, MEMBER01, PROVIDER01 | `BROKER`, `MEMBER`, `PROVIDER`, each linked to a seeded party |
| OPERATOR01/02 | `PLATFORM_ADMIN`, operator tenant |
| STEWARD01/02 | `PLATFORM_STATIC_DATA_STEWARD`, operator tenant |

That's 47 accounts. Keycloak stores usernames in lower case, and sign-in ignores case.

**Three mappings for Ade:**
1. **PAY01/02:** there's no payments template; payments are in the finance ones. Building and releasing a batch can't be one person (a blocked conflict), so I've made PAY01 the officer and PAY02 the manager.
2. **Two people on one template doesn't give maker-checker where the rule needs two templates.** A claim payment is drafted by a handler and released by a manager, which is why CLAIMSMGR and FINMGR are added.
3. **Some approvals have no template to hold them.** The three due diligence approvals and the non-renewal need `OFTZA_APPROVER`, a sample role, and `PRODUCT_MANAGER` lacks the approve function for publishing. With no extra grants, generic users can't do these. Recommendation: add APPROVER01/02 on `OFTZA_APPROVER`, which is an existing sample role and not an extra grant, and let the template gap on publishing show as a finding. Say if you'd rather have neither.

**Passwords:**
- Generated in the run, one per account, and encrypted with `age` to Ade's public key before upload. **Only the encrypted file goes in the artifact,** since anyone able to read the repository can download artifacts, and this repository is public today. Retention is one day.
- Never committed, and never printed: they're masked in logs and not passed on command lines.
- Only `provision`, `reset` and `rotate-passwords` change them; `upgrade` doesn't.

**MFA:** broker and provider portal accounts must use it (`external-user-access.md`), and any role in a policy's `mfa_required_roles` too. These accounts will be shared, and a code set up on one person's phone locks out the others. So the run generates each TOTP secret and puts it in the same encrypted file, if Keycloak's admin API lets us set it; otherwise the first person to sign in enrols, and shares it. To be proven in the build.

## What the chart and code still lack
Nothing below exists today, and it's what the size pays for.
- **Environment:** the simulator is refused in `Production`, and `Production` can't declare itself non-production. So the Preview runs as `ASPNETCORE_ENVIRONMENT=Preview` with `declaredNonProduction: true`.
- **OpenBao:** the chart's runs in development mode, in memory, so **a restart loses every key and so every encrypted field**. It needs persistent storage and unsealing.
- **Keycloak:** the chart's runs `start-dev` with data inside the container, so **a restart loses every account**. It needs production mode on PostgreSQL, with the hostname and TLS set.
- **The console:** the chart has no console deployment, and the Ingress sends everything to the API. **`insys-web` has no Dockerfile or published image yet.** The front end owns that, and the Preview can't be live without it (ADR-027).
- **The seed signs tokens with the development key,** which the API accepts only in Development and Testing, so it needs Keycloak sign-in. That's already in Workstream F's list.
- **Email:** creating a user sends a Keycloak action email, and the Preview has no mail server, so that step needs switching off for these accounts, with passwords set through Keycloak's admin API.
- **Labels:** a permanent console banner, and a list of the stubs in use: email, SMS, push, WhatsApp, ledger, screening and identity, reinsurer and regulator, payments (`SimulatorGateway`). AI Assist stays off. It needs a small read route and a front-end request.

## Size
Working days for one engineer. My estimates, made without building it, so low to moderate confidence.

| Work | Days (Est) |
|---|---|
| Chart: durable OpenBao and Keycloak, TLS on Traefik, environment | 2 to 3 |
| Chart: console deployment and routing (ADR-027's own 2 to 3) | 2 to 3 |
| Workflow: provision, firewall, k3s, upgrade, reset, destroy | 2 to 3 |
| Accounts tool, encrypted artifact, MFA | 1.5 to 2 |
| Stub labels, back end | 0.5 |
| Backups and a rehearsed restore | 0.5 to 1 |
| Live proof: run, fix, smoke checks | 1 to 1.5 |
| **Total** | **9.5 to 13.5** |

**Workstream F's remainder comes first,** at about 5 to 8 days from its plan, so the Preview is live about **15 to 22 working days** from now. **Outside these figures:** the front end's image and banner, and how long Ade's items take.

## Order
1. Workstream F's remaining slices.
2. On sign-off, the chart work, then the workflow, on branches merged as usual.
3. Ade creates the secrets, then runs `provision` and creates the DNS records.
4. Accounts, restore rehearsal, and a walk through the flows as generic users; then Ade receives the encrypted file.
5. Wave 7 resumes with batch 7.2.

## Sources
Checked 2 November 2026: Hetzner's [cost-optimized](https://www.hetzner.com/cloud/cost-optimized/), [regular performance](https://www.hetzner.com/cloud/regular-performance/) and [object storage](https://www.hetzner.com/storage/object-storage/) pages; [backups](https://docs.hetzner.com/cloud/servers/backups-snapshots/overview/); the 20% rate from [VPSBenchmarks](https://www.vpsbenchmarks.com/hosters/hetzner); GitHub's [artifact downloads](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts).
