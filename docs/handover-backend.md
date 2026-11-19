# Back-end handover

**The Mac session resumed on 14 November 2026, after the cloud session (4 to 6 November).** The go-live register is written, and so is how to continue. The state of branches is under "State at the stop", with the cloud session's changes under "The cloud session (4 November)". **What happened since, and the recovery Ade decides on, are under "OpenBao sealed, its key lost (6 to 14 November)", next.**

## Two changes of plan (Ade, 15 November 2026)
- **The Preview's `reset` is postponed** until the development work is finished and full demo testing starts. The Preview stays as it is and in use for demos; its encrypted fields stay unreadable until then, which is accepted. Only the SSH key swap went ahead (`rotate-ssh-key`, run 43639005340, done on 15 November); nothing else runs on the Preview without Ade's say. Go-live register row 38 keeps the reset and the restore rehearsal. **When it's taken up again, assess two paths without running anything:** a clean `reset`, or replacing only OpenBao and keeping the databases.
- **Wave 7's pause is lifted.** Its condition was met: the Preview has been live since early November and in use for demos. Batch 7.2 starts on 15 November, in the D-8 order (`docs/specs/addenda/ai-assist.md`), with the must-dos of the 7.1 review below.

## OpenBao sealed, its key lost (6 to 14 November)
**The Preview's OpenBao is sealed and can't be unsealed: the only way back is a new OpenBao.** The `reset` is postponed (Ade, 15 November, above). No `preview` run has been started since, and none runs without Ade's approval.

### What happened
- **6 November:** `upgrade` run 42190162209 (d488b32, chart revision 5) and `check` run 42286802427 passed. The `backup` run 42299623098 was dispatched at 21:47 UTC on 298ba30.
- **8 November:** that run waited for approval about 40 hours, then **ran from 13:31 to 14:07 UTC**. It failed at its last step, the KMS check: `OpenBao is sealed` (14:07:01). Before that it took the backup (`2026-11-08-insys.dump`, 15,770,489 bytes; `-keycloak.dump`, 466,579; `-openbao.tar.gz`, 18,505, 60 entries), and storage passed. The counts were: parties 30, users 81, documents 31, General policies 12, Life policies 12, Health memberships 12, approvals 586, data keys 1. The run closed SSH at its end.
- **The unseal key is gone.** Only `provision` and `reset` write OpenBao's key into the encrypted file; every other run's file has `openbao: null`. Reset run 41835262697's artifact, the only one with this OpenBao's key, expired after its one day's retention. Ade doesn't have the file.
- **11 November:** `open-ssh` run 43012903114 opened SSH from Ade's address at 11:33 UTC. No run has closed it since.

### Why the backup check failed: two faults
1. **OpenBao was sealed.** Sealing is the cause of the failure, and the check reported it correctly.
2. **Each dump's listing hung for about 17 minutes and printed no count** (`client_loop: send disconnect: Broken pipe`, then `lists  items`), and the run went on as if it had passed. `pg_restore --list` reads only the archive's table of contents and exits, while `kubectl exec -i` went on forwarding the rest of the file. That is most likely what hung it, until SSH dropped. **Fixed on `preview-seal-backup`:**
   - The pod takes the whole dump into `/tmp`, lists it, and deletes it, with a 10-minute limit.
   - A count that isn't a positive number now fails the run.
   - Rehearsed against postgres:18: a 8.8 MB dump lists its items; a corrupt file gives 0 and fails; nothing is left in the pod.

### Why OpenBao became sealed: not yet known
OpenBao starts sealed whenever its container starts, so something restarted it between the seed of 5 November (`rotate-passwords` 42104013738, done 22:20 UTC) and 8 November 14:07. What the record shows:
- **No run restarted it.** The only chart run in that time, `upgrade` 42190162209, changed nothing in OpenBao's StatefulSet. Its template and labels are the same from e4e79ae to d488b32, so Helm had nothing to roll.
- **No run read its state** before the backup. `failure_state` prints it only when a seed fails, and no seed failed after 5 November.

So it was a container restart (a crash, or the node's out-of-memory killer: OpenBao has no memory limit), a k3s restart, or a server reboot. Which one can be read only on the server. **`check` now reads it** (on `preview-seal-backup`, rehearsed with stand-ins):
- Once the server exists, `check` opens SSH for the runner only and reports OpenBao as an item, PASS if unsealed and FAIL if sealed.
- With it, the pod's start time and restart count, the container's last stop and its reason (`OOMKilled`, `Error`), when k3s last started, and when the server booted.
- At the end it closes SSH, including any rule left by `open-ssh`.
- `kms_check` and `failure_state` print the same line.

**Run `check` before the `reset`:** the reset makes a new pod and resets the restart count, though the boot time and k3s's start stay.

### The key's file, from now on (on `preview-seal-backup`)
- **The artifact is kept 90 days, not one.** GitHub's maximum for this repository: past that, only Ade's own copy remains.
- **A run that initialises OpenBao** (`provision`, `reset`) notes its run ID and date on the server, in `/var/lib/rancher/k3s/server/insys-openbao-run`. Neither is secret. Its run summary says, in bold, that its file holds the only copy of the unseal key and root token, to download and keep offline.
- **A run whose file has no unseal key** (`rotate-passwords`) puts a warning in its run summary and its annotations that names the run whose file has it, from that note. On today's server, which has no note, it says the run isn't recorded.
- `restore` names that run too, in place of the hard-coded 41835262697.

### Closing SSH now
**On main as it is: run `backup`.** No confirm word.
- It opens SSH for the runner only, and takes today's backup, as the nightly job does.
- It ends red at the KMS check, `OpenBao is sealed`. That's expected.
- Its exit trap replaces the firewall's rules with 443 only, so Ade's SSH rule goes too.
- Expect about 35 minutes, as the two listings hang until SSH drops. Don't cancel it: the trap is what closes SSH.
- Nothing else changes, and `rotate-passwords`, `upgrade` and the confirm-word actions all do more.

**Once `preview-seal-backup` is on main, `check` does it in about 2 minutes,** and records why OpenBao sealed.

### Why it sealed, and the guard against it (14 November, `preview-seal-guard`)
Ade's `check` on 14 November read: `sealed true; pod started 2026-11-04T13:11:16Z, restarts 3, container last stopped 2026-11-07T20:21:23Z (Unknown); k3s started Fri 2026-11-13 06:49:33 UTC; server booted 2026-11-07 20:21:15`. So the server's boot at 20:21:15 on 7 November most likely ended the container, 8 seconds later, and sealed OpenBao. k3s restarted again on 13 November without a reboot.
- **The 7 November boots: found by `diagnose`.** Hetzner's record of actions shows rescue mode enabled and the server reset from Hetzner (`enable_rescue`, `reset_server`) on 7 November. **They were made by someone Ade knows, so the server isn't treated as compromised (Ade, 15 November).** Ade is asking them what they changed while in rescue mode and will pass on anything relevant; until then that is not known.
- **The 13 November k3s restart:** it came during an unattended upgrade. That needrestart restarted k3s is inferred, not shown. The guard below stops needrestart restarting k3s, whichever it was.
- **`diagnose` (read only, no confirm word).** On the server: the boots, the reboot and shutdown records, unattended-upgrades' and apt's logs for 14 days, needrestart's lines in apt's log, and the journal's system lines (systemd, logind, the kernel, the package tools, k3s's start and stop) in the 25 minutes before each recent boot and before k3s's last start. Then Hetzner's own record of actions on the server: a reboot or reset from its API or console shows there. The repository is public, so no application log, session or address is printed.
- **Security updates stay automatic; restarts and reboots don't (Ade, 14 November).** Every `provision`, `reset`, `upgrade` and `os-update`:
  - keeps unattended-upgrades and apt's daily timers on, with Ubuntu's own settings (security updates only);
  - writes `/etc/apt/apt.conf.d/99insys-no-automatic-reboot`: automatic reboot false;
  - writes `/etc/needrestart/conf.d/99insys.conf`: needrestart lists and restarts nothing, k3s included.

  k3s's own unit still restarts it if it crashes (`Restart=always`), which keeps its containers running. **`check` reports** whether a reboot is pending (`/var/run/reboot-required`, with the packages that set it), whether a newer kernel is installed, and what needrestart lists, so Ade can plan a reboot.
- **`os-update` (confirm `os-update`).** Everything else: `apt-get upgrade` with nothing restarted or rebooted. It lists the packages changed, the services that would need a restart, whether a newer kernel waits, and whether a reboot is needed. A reboot seals OpenBao, so it stays Ade's call, with the unseal key at hand. k3s isn't an apt package.
- **The nightly backup names a seal.** Its backups run first; its last step asks OpenBao's seal status. Sealed, or not answering, the job fails at once with no retry (exit 3, `podFailurePolicy`), says so in its log, and sends one email through the realm's SMTP relay to the address in the Secret `insys-alert`. That address comes from the Environment's secret **`PREVIEW_ALERT_TO`, which Ade sets** (before the reset, 14 November). It reaches the server only at a `provision`, `upgrade` or `reset`, and every such run's summary, and `check`'s, says so. Unset, the job still fails, on the server only. The `backup` action reads the seal step's verdict and goes on to its checks. Rehearsed: the step against a sealed and an unsealed OpenBao 2.6.5 and one that is down (exits 3, 0, 3), and the email through a STARTTLS-only mail server; the CronJob validated by k3s v1.33.5. Not rehearsed in a cluster: the init containers' order and `podFailurePolicy` (the local k3s couldn't pull its sandbox image).

### The SSH key swap, before the reset (`preview-rotate-ssh-key`)
**Done (15 November 2026).** `rotate-ssh-key` run 43639005340, approved by Ade. Ade then moved the new key into `PREVIEW_SSH_KEY` and deleted `PREVIEW_SSH_KEY_NEW`, and `check` reached the server with it: PASS, `SHA256:86YEs4dfpdmLqoJUlDeSP5GbgMUGMjIbICpPFxaE8z8` (a public key's fingerprint). Its only FAIL was OpenBao sealed, as expected.

The same `check` showed k3s restarted at 06:45 UTC on 15 November, and a reboot pending (kernel 6.8.0-149, `libc6`). The restart guard (`os_policy`) wasn't on the server yet: no run that applies it (`provision`, `reset`, `upgrade`, `os-update`) had run since it merged.

**The private key in `PREVIEW_SSH_KEY` was exposed on 13 November** (Ade). It must be gone from the server and from Hetzner before the `reset`: the reset makes OpenBao's new keys and the new passwords on that same server, and anyone with the old key could read them. A reset doesn't replace the key, as the server and its `authorized_keys` are kept, and `infrastructure()` creates Hetzner's `ssh-key` object only when none exists.

**`rotate-ssh-key` (confirm `rotate-ssh-key`)** takes the new private key from the secret `PREVIEW_SSH_KEY_NEW`, which Ade sets, and:
1. connects with the old key and adds the new key's public half to `/root/.ssh/authorized_keys`;
2. opens a second connection with the new key alone, and stops if that fails, with the old key still in place and Hetzner's key unchanged;
3. over the new key, removes every other key from every `authorized_keys` on the server (root's, any user's, and any absolute `AuthorizedKeysFile` sshd names), then shows the old key refused;
4. deletes Hetzner's `ssh-key` object `insys-preview` and makes it again with the new public key;
5. prints fingerprints only, before and after, never a key.

Rehearsed against OpenSSH on Ubuntu 24.04: an extra key in root's file and in a user's both went, the old key was refused, and a second key file from `AuthorizedKeysFile` was cleaned too. With the new key refused by the server, the run stopped and the old key still worked.

**Then Ade:** moves the new key into `PREVIEW_SSH_KEY`, deletes `PREVIEW_SSH_KEY_NEW`, and runs `check`, which shows the runner still connects.

**After restoring a Hetzner backup or snapshot from before the swap,** the old key is back (`docs/preview/restore.md`): run the swap again, with the old key in `PREVIEW_SSH_KEY` and the current one in `PREVIEW_SSH_KEY_NEW`.

**What the swap doesn't cover:** it takes the old key's access away. It can't show that nobody used the key before it went, or remove anything such a person left on the server. SSH was open from 11 November (`open-ssh` 43012903114, from Ade's address only) until the `check` of 14 November closed it, so using the key needed that address too. If that isn't enough assurance, `destroy` and `provision` make a new server.

### The recovery: what `reset` does on this Preview (Ade decides; nothing has been run)
`action: reset`, `confirm: reset`, `acme: production`. Its steps:
1. `helm uninstall`, then delete every volume in the default namespace, then delete `insys-secrets`.
2. Then, as a provision does: Traefik's settings, the images, new server secrets, the pull secret, the chart, Keycloak's realm name, user-admin secret and SMTP, and the storage and SMTP checks.
3. Then a new OpenBao: initialised, unsealed and configured.
4. Then the file is written with its key, the full seed runs with the accounts, the file is written again, and `check_tls` runs.

**Lost:**
- **The INSYS database**, with all of it: the sample data (30 parties, 36 policies and memberships, 586 approvals, 31 documents' records), audit and parked deliveries. It also holds anything Ade or anyone else entered after the seed.
- **Keycloak's database:** its users, sessions and any TOTP set up for `BROKER01` or `PROVIDER01`.
- **The documents in SeaweedFS.**
- **OpenBao and its one data key.** Fields encrypted under that key are unreadable already, while it stays sealed with no key.
- **Every secret in `insys-secrets`:** the database, Keycloak admin, object store and hash keys. These are made new, on the server only.
- **Every password in Ade's earlier files** stops working.

**Kept:**
- The server, its address and the DNS records.
- k3s.
- Traefik's production certificates: they're on Traefik's own volume in `kube-system`, which the reset doesn't touch.
- The nightly backups in `/var/backups/insys`, and Hetzner's server backups.

**Don't restore a backup older than the reset.** Its OpenBao is the sealed one.

**Rebuilt:**
- The OFTZA sample data, from the seed.
- A new OpenBao, with a new unseal key and root token.
- The 51 accounts, with new passwords.
- The realm's name, the SMTP relay and the clients' addresses.

**How long:** about 2 hours of running, inside the job's 240-minute limit, plus the wait for approval. The estimate comes from two runs:
- Reset 41835262697 reached OpenBao's file in under 5 minutes, chart included.
- The seed with the accounts took 1 hour 47 minutes in `rotate-passwords` 42104013738. The local rehearsals took 96 to 122 minutes.

`check_tls` should pass at once, as the certificates are kept.

**The file to keep:** the reset run's own `preview-secrets` artifact. It downloads as `preview-secrets.zip`, holding `preview-secrets.age`, and that one file has the unseal key, the root token and the 51 accounts. If the run fails after OpenBao, the artifact still holds the key: the file is written straight after OpenBao is initialised. Keep it offline, in two places. To check it without showing a secret:
```bash
age -d -i ~/insys-preview-age-key.txt preview-secrets.age | jq '{unsealKey: (.openbao.unsealKey | type == "string" and length > 0), rootToken: (.openbao.rootToken | type == "string" and length > 0), accounts: (.accounts | length)}'
```
It must print `true`, `true` and `51`. Any `false` means this isn't the file. If the reset runs after `preview-seal-backup` is on main, its run summary also says so in bold, and the artifact lasts 90 days. **So merge `preview-seal-backup` first.**

**Then the restore rehearsal** (`docs/preview/restore.md`), before the team gets access:
1. `backup`.
2. `restore` of that day.
3. Unseal with the new file's key.
4. `backup` again: the counts match, and a stored data key decrypts.

That rehearsal is also the first real test that the new file's key unseals.

## First: the INSYS Preview milestone
**Decided by Ade, 2 November 2026; it comes before everything else.** A hosted environment for OpenFactor's functional team, with all built functionality, sample data only, and every external integration stubbed and clearly labelled. Not production.

- **Order:** Workstream F is done; the Preview is built but not yet provisioned (`docs/plans/insys-preview.md`, Built so far). Ade's secrets, variables and the console's image access are set, and the environment's reviewer rule is saved. The `check` runs passed. The first `provision` stopped on Hetzner's account core limit (see "The first provision" below).
- **Wave 7's pause after 7.1 is lifted (Ade, 15 November):** the Preview has been live since early November. Batch 7.2 starts on 15 November.
- **The plan is [`docs/plans/insys-preview.md`](plans/insys-preview.md): one Hetzner server, k3s, the ADR-027 chart. Signed by Ade, 2 November, with decisions recorded at its top.** It starts after Workstream F. The restore rehearsal comes before the team gets access.
- **Nobody resets, reboots or enters rescue mode on the Preview without Ade's approval (Ade, 15 November).** Each restart of the server, of k3s or of OpenBao's container seals OpenBao, which then stays sealed until someone unseals it with its key. Today's OpenBao is already sealed with its key lost; the rule matters most after the `reset`. That holds whether it's done from Hetzner's console or API or on the server itself.
- **Never handle Ade's secrets.** The Hetzner API token goes in GitHub, set by Ade, never in a file, chat or session. Generated passwords, and OpenBao's unseal keys and root token, are only ever written encrypted to Ade's key, as this repository is public; the unseal keys and root token are never stored on the server or in GitHub. Nothing sensitive goes in logs or artifacts.

### Running the Preview: the five open points in Ade's deployment guide
1. **The workflow, its actions and the confirmation words.** The workflow is `preview` (`.github/workflows/preview.yml`): Actions, then preview, then Run workflow. It runs in the Environment `preview`, so each run waits for Ade's approval. Its inputs:
   - `action`: `check` (the default), `diagnose` (read only: what rebooted the server or restarted k3s), `os-update` (confirm `os-update`: all packages updated, nothing restarted; security updates apply themselves), `provision`, `upgrade`, `open-ssh`, `reset`, `rotate-passwords` or `destroy`. `check` creates nothing: it prints PASS or FAIL for the Hetzner token, the server type's availability in the location now, `PREVIEW_SSH_KEY` (with its public fingerprint, to match against Ade's key), `PREVIEW_AGE_RECIPIENT` and the pull token's read of the console's pinned manifest, and no secret. Once the server exists, it also reports OpenBao: sealed or not, with what would explain a seal. It reads that over SSH opened for the runner, and closes SSH at the end, including `open-ssh`'s rule (`preview-seal-backup`).
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
   4. Run `check` to close SSH again. It also reports OpenBao's state. Before `preview-seal-backup` is on main, `check` doesn't touch the server: run `backup` instead.
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
- **The third provision, run 41810649044 (09:54 UTC, on d1e23fc):** the chart installed (`STATUS: deployed`, 09:57:51). Then `kubectl wait` for the migration job found nothing (`no matching resources found`): its label `app.kubernetes.io/name=migrate` was only on the Job's pods, not on the Job. That was before OpenBao, so the next run is `provision` again, not `reset`. Fixed on `preview-migrate-wait`: the Job carries the label too. The smoke test didn't catch it, because it waits on the Job by name.
- **The fourth provision, run 41816758710 (10:49 UTC, on fe5f841):** the chart upgraded (revision 2), the migration job `insys-migrate-2` completed, and OpenBao's pod rolled out. Then setting the realm's name failed: `hcloud: Primary IP not found: INSYS Preview (sample data only)`. In `realm_name`, `local name` hid the script's `name` (the Hetzner resources' name), which `on_server` uses to find the address. OpenBao wasn't initialised yet, so the next run is `provision` again. Fixed on `preview-realm-local`: the local is `label`.
- **The fifth provision, run 41824138110 (11:50 UTC, on 34fe073):** the chart upgraded (revision 3), `insys-migrate-3` completed, and the realm was already named. **OpenBao was initialised**, unsealed and configured, and the API restarted with its KMS token. Then the seed failed with ssh's exit code 255: the server was sent `pkill -f 'port-forward svc/insys-'`, which matched, and killed, the remote shell running it, because that shell's command line holds the same text. The encrypted file was written only at the end, so **that OpenBao's unseal key and root token were lost**. The uploaded file is empty, and OpenBao is sealed with no key from its next restart. **So the next run is `reset`** (confirm word `reset`), not `provision`: it deletes the chart's data and secrets and sets OpenBao up again. Fixed on `preview-pkill`: the pattern is anchored, `^kubectl port-forward svc/insys-`, so it matches only the forwards. The file is now also written straight after OpenBao is initialised, so a later failure keeps its keys. A rehearsal with stand-ins runs `openbao` to `check_tls` through. It ends with 51 accounts and OpenBao's keys in the file, and no secret in the log outside a mask line. With the seed made to fail, the file still holds OpenBao's keys.
- **The sixth run, `reset` 41835262697 (13:07 UTC, on 07ed2c9; an earlier try, 41834652824, stopped at Confirmation because `confirm` wasn't exactly `reset`):** it removed the release, its volumes and `insys-secrets`, and installed the chart fresh, with `insys-migrate-1` completing and the realm named. It initialised OpenBao, and **wrote the encrypted file straight after (13:12:17): that run's `preview-secrets` artifact holds OpenBao's unseal key and root token**, with no accounts. Then the seed got `401` from `GET /api/v1/tenancy/tenants`. The likely cause: the API reads Keycloak's keys from its authority, `https://id.insys.project-demo.app`, through Traefik. Traefik serves only its default certificate there, so the API can't validate any token. A second problem: **Traefik has had no Let's Encrypt certificate on either host since its routes appeared (about 10:00), DNS in place.** The run doesn't show why. Fixed on `preview-internal-oidc`:
  - The API reads Keycloak inside the cluster (`http://insys-keycloak:8080/realms/insys`), with the tokens' issuer still the public one (the chart's new `authentication.issuer`).
  - Keycloak runs with `--hostname-backchannel-dynamic=true`, so the keys' address it gives the API is the cluster's. Shown on Keycloak 26.7.5: the issuer stays `https://id…`, and `jwks_uri` is the address asked.
  - When `check_tls` fails, it prints Traefik's ACME log lines.
  
  - The smoke test now has the Preview's shape: Keycloak's public hostname, tokens with that issuer, and the API reading the keys in the cluster. Its signed-in API calls rehearse the fix.
  - When the seed fails, `preview.sh` prints the API's token-validation log lines (`IDX…` messages, which hide the token). The 401's cause wasn't read from the API's logs, as nothing here can reach the server without a run: the next run's output confirms it.

  **Ade has that run's file and is keeping its OpenBao, so the next runs don't wipe it:**
  1. `upgrade` (no confirm word) applies the new settings. The API and Keycloak restart; OpenBao, the data and `insys-secrets` stay. It ends with `check_tls`, which will most likely fail on the missing certificate and print Traefik's ACME lines. That's expected, and it doesn't undo the upgrade.
  2. `rotate-passwords` (confirm word `rotate-passwords`) runs the seed, which on this empty database is its first run, then writes a new file with the 51 accounts. It has no OpenBao keys: those are in run 41835262697's file.
  
  If OpenBao is sealed by then (its pod restarted), the API's calls that need the KMS fail. Unseal it as in "Unsealing OpenBao" above, with the key from 41835262697's file. `reset` is the fallback only: it makes a new OpenBao and new keys. **Superseded (14 November):** that file expired, and OpenBao is sealed. See "OpenBao sealed, its key lost".
- **No certificate, cause found (cloud session, 4 November):** `deploy/preview/traefik.yaml` had `tlsChallenge: {}`. The Traefik chart that k3s v1.33.5 bundles (34.2.0) turns values into arguments with `yaml2CommandLineArgs`, which recurses into maps, so an empty map gives **no argument at all**: the resolver `le` had its CA and storage and no challenge, and never asked Let's Encrypt for anything. Shown by rendering that chart with `helm template`: with `{}` there's no `tlschallenge` argument; with `true` Traefik gets `--certificatesresolvers.le.acme.tlsChallenge=true`. Traefik's own log on the server hasn't been read. Fixed on `preview-acme-tls`: `tlsChallenge: true`, and `deploy/preview/check-traefik.sh`, run by the smoke workflow, renders the chart of the k3s version `preview.sh` pins with these values and fails without the challenge, the storage and the CA. `upgrade` applies it: `cluster()` rewrites the HelmChartConfig, and k3s redeploys Traefik.
- **The smoke workflow's disk:** main's smoke run 41816611728 (fe5f841) stopped on "No space left on device" in the runner's own log. The workflow now removes .NET, Android, GHC, Swift and CodeQL from the runner first (the smoke test builds in Docker and uses none of them), on `preview-acme-tls`.
- **Fixed on `preview-seed-forwards` (cloud session, 4 November); was not fixed yet:** in `clients()`, the Keycloak admin password and the API token went to `curl` on its command line. They aren't in the log, but this breaks the script's own rule. Fix it once the Preview is up, by passing them on stdin with `curl -K -`, as `check_server` does.
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
| 36 | Keycloak user provisioning in production (cloud session, 5 November). The API makes each INSYS user's Keycloak account through the realm's confidential client `insys-user-admin` (`Identity:Keycloak`), and without its address and the client's secret every new user's identity parks as `KEYCLOAK_UNAVAILABLE`, as the Preview's first administrators did (`rotate-passwords` run 41983797927). The chart now sets the address when it runs Keycloak (`http://<release>-keycloak:8080`) and reads the secret from the Secret's `keycloakUserAdminSecret`; the Preview copies Keycloak's generated secret there on the server (`deploy/keycloak/user-admin-secret.sh`). Needed: production's deployment sets `identity.keycloak.baseUrl` for its Keycloak and puts the client's secret in its secret store, through the same key, never in values or a log; and the runbook says how the secret is rotated | under 0.5 (Est), plus the runbook | Production's Keycloak (row 2) | **Yes** |
| 37 | Document storage's keys in production (cloud session, 5 November). The chart's in-cluster SeaweedFS took its S3 keys from the chart's values, which are empty wherever the Secret is made outside the chart, while the API signs with the Secret's keys: on the Preview the first document upload failed (`rotate-passwords` run 42080719155, 500). Fixed in the chart (SeaweedFS reads the Secret's `objectStoreAccessKey` and `objectStoreSecretKey`), with the smoke test now making the Secret outside the chart and `preview.sh`'s `storage_check` storing, reading back and deleting an object with the API's keys. Needed: production's object store is given real keys from its secret store, the same ones the API's Secret holds, never values or empty ones; a check like `storage_check` after each deployment; and the runbook says how the keys are rotated on both sides together | under 0.5 (Est), plus the runbook | Production's object store (row 2) | **Yes** |
| 38 | The Preview's reset and restore rehearsal (Ade, 15 November). Plan: `docs/plans/preview-rebuild-beta.md`; 0.5 done 19 November (`check` 44518919932: OpenBao still sealed, a reboot pending). OpenBao is sealed with its key lost (6 to 14 November), so encrypted fields on the Preview are unreadable. The `reset` is postponed until development is finished and full demo testing starts; then assess, without running anything first, a clean `reset` against replacing only OpenBao and keeping the databases, and run the restore rehearsal (`docs/preview/restore.md`). The rehearsal is the first real recovery evidence for row 3 | 0.5–1 (Est), plus the run and Ade's checks | The end of development; Ade | No for production; **Yes** before the team's full demo testing |
| 39 | ClamAV's signature mirror (batch 7.4, AIA-010). **Memory measured** (`check` 44518919932, 19 November, idle): 3.7 GB used of 15.6, so with ClamAV at its 3 GB limit about 6.7 GB; see `docs/plans/preview-rebuild-beta.md` 0.5. The chart's ClamAV (`scanner.enabled`) downloads its signatures at start and daily, so its pod needs outbound HTTPS to ClamAV's mirror; without it, clamd has no signatures and every upload stays in quarantine. **A network change for Ade to approve per environment.** The Preview gets ClamAV only on a redeploy Ade approves. **The Preview's environment:** `deploy/preview/values.yaml` sets `environment: Preview`, which isn't Development or Testing, so the development scanner can't run there. The chart also pins ClamAV (`Documents__Scanner__Provider=clamav`) whatever the environment is called. At the next redeploy, confirm on the server that the API's `ASPNETCORE_ENVIRONMENT` is `Preview`: this session can't reach it | 0.5 (Est), per environment | Ade's approval per environment, and the cluster's egress rules | **Yes**: without it, no upload is released |
| 40 | Database role order and the directory grant (Ade, 19 November 2026, V0142). The role scripts must run after V0142, which creates `insys_tenant_directory`. Any way of creating roles other than `db/roles/module_grants.sql` must grant `insys_tenant_directory` to `insys_tenant` `WITH INHERIT FALSE, SET TRUE`, and to no other role. How, and the check, are in `docs/runbooks/deployment.md` (Database roles) | 0 for the code; part of the production set-up (row 2) | Whoever sets up production's roles | **Yes**: without it Tenancy's directory reads fail; with `INHERIT TRUE` a tenant's session sees every tenant |
| **Markets** | | | | |
| 24 | Nigeria blockers: retention defaults, default time zone, age of majority. **Descoped by Ade, 2 November: deferred until a first Nigerian customer is chosen; not started.** Appeal routes are static data and stay, as they serve every market (V0126); the Nigerian route itself waits on row 25 | 2–3 (Est, from the 3–4 in the survey) | A first Nigerian customer being chosen | **If** the first customer is Nigerian |
| 25 | Nigeria: "no premium, no cover" domain review; starter pack review sheet and partner sign-off | Unknown until reviewed | Nigerian compliance partner; compliance lead | **If** the first customer is Nigerian |
| 26 | Rounding by currency (Rwanda, Senegal, Côte d'Ivoire) | 5–8 (Doc) | Nothing | **If** the first customer is in one of those |
| 27 | Limits with a currency and two-currency journeys (DRC) | 6–9 (Doc) | Ade on revaluation | **If** the first customer is in DRC or takes a second currency |
| 28 | French wording (Senegal, DRC, Côte d'Ivoire, partly Rwanda) | 5–7 (Doc) | Professional translation | **If** the first customer is in one of those |
| **Demos** | | | | |
| 30 | Workstream F: OFT Insurance South Africa sample data, tenant `OFTZA` (`docs/plans/workstream-f-oftza-seed.md`). Needed for demos and the front end's Phase 13 exit | 8–12 (Est), with the sample tenant block | Batch 7.1 merged; signed by Ade, 2 November | No |
| **AI Assist** | | | | |
| 29 | Wave 7: AI Assist. Started by Ade's decision of 2 November; paused after 7.1 until the Preview was live; resumed 15 November with batch 7.2 | 64–85 (Doc) | Provider contract; evaluation documents (D-7) | **No**: it ships switched off and INSYS works without it. A tenant set up through AI-3 must pass readiness first (AIA-085) |

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
- **Wave 7 resumed on 15 November (Ade), the Preview having been live since early November,** in the D-8 order: 7.2, 7.3, 7.6, 7.7, 7.4, 7.5, 7.8. Nigeria's row 24 stays deferred until a first Nigerian customer is chosen.
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

### Wave 7, batch 7.2 (15 November 2026, branch `wave7-2`)
**Built:** AI-2's refusal and premium explanations (`docs/api/contract-changes.md`), with the three must-dos of the 7.1 review:
- **Structured fields only (D-15):** `AllowedFacts`; `UntrustedText` and `MinimiseFreeText` removed; the injection gate test moved to what's left (a code or a field's name), and the prompt's instructions are always the template's own words.
- **The currency beside every amount** sent and checked: a stated `USD 3050` against `ZAR 3050.00` is rejected (`AssistExplanationTests`).
- **AIA-072, decided (addendum D-17):** no transaction is open while the model answers. Shown by a test that counts the module role's idle-in-transaction sessions from inside the model call: none.

**The independent review (15 November) found, and 7.2 fixed:** code-shaped values the client chose (field names, a record type, rating input names and values) could reach the prompt, as the forms accept any single word; now only values INSYS itself recorded or configured are sent. Fact names counted as source numbers, and a stated currency matched a plain number; now the verifier reads the facts' values only, with the currency strict. Health factor values are left out (AIA-038). The catalogue's wording for `STATIC_SCOPE_NOT_ALLOWED` and `VALIDATION_FAILED` was wrong for some raising sites, and the status fact could differ from what the screen got; both corrected, the status dropped. The record a refusal was about now goes to `basedOn` and the audit. Found sound: AIA-072, tenant isolation, the cache, feedback, the migration and grants. Left, not fixed: a request cancelled during the call goes unmetered; the cap returns its error rather than the plain facts (P8 doesn't say); feedback has no limit per user.

Also: the cache (AIA-036), feedback by the user shown the explanation (AIA-037), the label and basis (AIA-034), the plain facts when no text can be shown (AIA-033, P8), Rating's `IRecordedCalculations` contract, and the error catalogue's `Means` and `Remedy` for 69 of 260 codes. **Open:** OQ-166 (the record's state for refusals, AIA-039) and OQ-167 (the rest of the wording).

### Wave 7, batch 7.3 (branch `wave7-3`)
**First, before anything else (Ade, 15 November); no tenant switches AI-2 on until both are done:**
1. **AIA-003 scope on premiums: done.** `IRecordedCalculations` runs Rating's own `GetCalculation`, the query behind `GET /rating/calculations/{id}`, so the two can't differ. Checking it found that neither applied F103 product scope, though a calculation knows its product (`access-scopes.md` §1): `GetCalculation` and `ListCalculations` now apply the scope of `premium_calculation.read` itself, not the request's, so Assist's calls get the reader's scope too. A calculation has no organisation node, and both routes are staff only, so branch and relationship scope don't apply. Test: `AssistExplanationTests.AUserRefusedTheCalculation_IsRefusedItsExplanation_TheSameWay_AndGetsNoStoredOne_AIA003_F103`.
2. **Factor values: confirmed.** A non-numeric `FactorValue` is a table cell's key (matched exactly, ordinal, or the rating fails with `NO_TABLE_VALUE`), a band's label, or INSYS's own exposure input name; fees, factors, loadings and adjustments carry configured numbers and configured codes. The only input that passes through as given is a number on a linearly interpolated table, and Assist sends it as a number. Tests: `RatingEngineTests.AnInputValueThatIsntAConfiguredTableValue_IsRefused_SoNoLineCarriesIt` (injection-shaped values, with and without spaces) and `EveryNonNumericFactorValue_IsAConfiguredValue_NeverAnInputAsGiven`.

**Then, built on `wave7-3b` (15 November) without the key (Ade: only the evaluation run waits for it):**
- **The Claude adapter** (`ClaudeModelProvider`, in `Insys.BuildingBlocks.Cloud`). Anthropic's official C# SDK, package `Anthropic` 13.3.0, Apache-2.0, of 23 October: the newest release at least two weeks old on 15 November (13.8.0 was five days old). It joins the SDK guard (`CloudSdkRuleTests`). The model port (`IAiModelProvider`) moved to `Insys.BuildingBlocks.Application.Ai` so the adapter project can implement it.
  - One message: the template as the system prompt, the delimited facts as the only user message, and no tools (AIA-060, AIA-061). Effort is configuration (`Assist:Claude:Effort`, `medium`); `MaxTokens` is 8192, as the model always reasons before it answers. One call has 15 seconds and no retry (AIA-070's 10 seconds at p95 is measured by the evaluation).
  - A refusal, an answer cut at the token limit, an API error or a timeout is UNAVAILABLE, so the plain breakdown is shown (P8). A call that was billed though unavailable still counts its tokens and cost against the cap (AIA-073). Logs carry the status only; the SDK's error, whose text can echo the request, isn't kept (AIA-051).
  - **The residency check (Ade, 15 November).** A tenant's inference region is in the provider's terms: `US` or `GLOBAL` today, from the configured list `Assist:InferenceRegions`, and the settings refuse anything else (422 naming the list). Every call asks for the tenant's region (`inference_geo` `us` or `global`). An answer counts only if the region the provider reports is allowed: `GLOBAL` accepts any, `US` only `us`, and a missing region is never allowed. Otherwise the text isn't shown, the plain facts are, the run is `RESIDENCY_MISMATCH`, its tokens count against the cap, and the answer is never kept for reuse.
  - **A tenant already set to a country code** (such as `ZA`, from before the regions were the provider's) fails closed: every request is refused with `ASSIST_REGION_NOT_SUPPORTED` before any call, and the settings view says why (`inferenceRegionSupported` false, `supportedInferenceRegions` the choices). Its administrator chooses `US` or `GLOBAL`, with the legal basis.
  - Tests: `AssistTests.ATenantInTheUs_GetsOnlyAnswersReportedFromTheUs_…_AIA050` (the stub reporting `us`, `US`, `global`, `eu` and nothing), `ATenantSetToACountry_…_AIA050`, `ARun_AsksForTheTenantsRegion_…`, `AssistExplanationTests.AnExplanationFromARegionTheTenantDoesntAllow_…_AIA050`, `ClaudeModelProviderTests.TheTenantsRegion_OnTheCall_…`.
  - **Region: not assumed (Ade).** No `inference_geo` is sent unless `Assist:Claude:InferenceGeo` is set. What the API reports (`usage.inference_geo`) is recorded on each run in `assist.run.inference_region` (V0132), with the model that answered (`served_model`); null when the route doesn't say.
  - The API's server-side refusal fallback to another model is **not** turned on: it would send a tenant's facts to a model the deployment didn't name. Turning it on is Ade's call.
  - Chosen by configuration: `Assist:Provider` `claude` with `Assist:Claude:ApiKey` from the secret store (AIA-053); otherwise no model, as before. Tests: `ClaudeModelProviderTests` (7, against a local fake of the Messages API).
- **Prompt templates as configuration** (AIA-074): `Assist:Prompts` in `appsettings.json`, a version and a text per feature; the host doesn't start without both. The texts are 7.2's, unchanged, so their versions (`refusal-v1`, `premium-v1`) and the cache stay valid.
- **Prices as configuration** (D-5): `Assist:Prices`, per model, with the source of the price. Each run's cost is recorded (`assist.run.cost`). The one price set is `claude-opus-5-5` at USD 4 and USD 20 per million input and output tokens, as listed on 3 November; **check the current price list before relying on it.**
- **The evaluation set** (`docs/evaluation/ai2-evaluation-set.json`, 109 cases at 7.6, as it grows with every worded code): the 73 refusals the catalogue words, and 36 premiums of the five sample products over a fixed grid of inputs, rated by Rating's simulation (for the evaluation only: a tenant's explanation is always of a recorded calculation, AIA-032). The seed's test (`OftzaSeedTests`) regenerates and checks it, so it follows the catalogue and the products (`INSYS_UPDATE_SNAPSHOTS=1` to accept a change). Sample data only; the rates are invented.
- **The runner** (`tools/Insys.Assist.Evaluation`) sends each case through `AssistExchange`, the same minimiser, call and verifier a tenant's run uses, one at a time. It writes `summary.md` (verified, rejected, unavailable, tokens, cost per explanation by kind, latency p50 and p95 against AIA-070, models and regions as reported, prompt versions, price list), `review.csv` for the domain expert (SRS §5.3) and `results.json`. Tests: `EvaluationTests`.
- **The run (Ade, 15 November):** the same 105 cases on `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`, one model after another, with `inference_geo` `us`, then the models compared (`comparison.md`): per model the verified rate, the numbers rejected, the median and slowest time against 10 seconds, and the cost per explanation. Ade picks the model from it. Each run stands for a tenant whose region is `US`: every call asks for `us` and is checked as a tenant's is. **Proved with the stub reporting `us`, end to end** (the three runs and the comparison, the tool as the workflow runs it): 106 of 106 verified for each model, no residency mismatch (`EvaluationTests.TheWholeSet_WithAStubReportingTheUs_…`). The `assist-eval` workflow, by hand, from main only, in the Environment `assist-eval` (Ade the required reviewer, limited to main by Ade), with the key from `ASSIST_CLAUDE_API_KEY`. Prices are configured for all three (USD per million tokens: Opus 4 and 20, Sonnet 2 and 10, Haiku 0.10 and 0.50 up to 100,000 prompt tokens, as listed on 3 November).
- **The first run (16 November, run 43862166993, main `3da6665`, 110 cases, `inference_geo` `us`):** Opus 68 verified and 42 rejected, Sonnet 44 and 66, Haiku 78 and 32; no residency mismatch and nothing unavailable in any. **The Environment's required-reviewer rule wasn't in force:** the Opus job started 5 seconds after dispatch, and GitHub records no approval. Ade is setting it; **nothing runs in `assist-eval` until Ade says it's fixed.** The rejected numbers are with Ade's adviser. The check and the prompts stay as they are until the causes are seen (Ade).
- **The rerun (16 November, run 43904917124, main `10ea4cf`, templates v2):** verified Opus 108, Sonnet 107 and Haiku 90 of 110. Every rejection was a number word (`twice`, `two`, `four`); no figure was wrong. The p95 was 6.66 s, 3.30 s and 5.03 s; cost per explanation USD 0.0077, 0.0026 and 0.00023. Inference ran in `us` for every case. Recorded in the addendum §6, with Ade's provisional choice of `claude-sonnet-5-5` for AI-2, pending the domain expert's review (SRS §5.3). No default changed.
- **Pre-screen of that run (Ade, 17 November, not the expert review):**
  - Sonnet said "I can't approve, change or resubmit…" in 64 of 107 shown answers, and called the sum insured the "starting point" in 13 of 34 premiums.
  - Opus advised beyond the facts' next step in 18 and generalised beyond them in 6.
  - None contradicted its facts on retrying.
  - Hence templates `refusal-v3` and `premium-v3`, and the `IDENTITY_NOT_LINKED` retry flag. The expert reviews the run on v3.
- **Premium coverage (Ade, 17 November):**
  - `OFT-HOME` has sample rating configuration (`tools/Insys.Seed/OftzaSeed.cs`, `RatedProduct`'s `extras`): a 10% loading, a 15% discount and a ZAR 120 loading schedule, and monthly bounds of 800 to 10,000. No tax.
  - The evaluation grid rates it without schedules, with each and with both (`AssistEvaluationSet.AdjustmentsFor`), so the set has 66 premiums covering every line type but `FACTOR` and `TAX`.
  - The domain expert reviews the final run on this set.
- **The job's log now carries what's needed without the artifacts (Ade, 16 November):** the summary (per kind: verified, rejected, median, p95 and slowest time, cost per explanation), then every rejected case with its code or calculation, the facts sent, the answer, and each rejected number with why it was refused. The why is the evaluation tool's own reading against the facts (`EvaluationReport.WhyRejected`): a number word, a code not word for word in the facts, a percentage not there, digits that are there but written differently, or a figure not in the facts. It never feeds back into the check. Tests: `EvaluationTests.TheLog_…` and `WhyANumberWasRefused_…`.
- **The causes (Ade, from the artifacts, 16 November):** all 140 rejections across the three models were false: every rejected token a single digit or a number word, every digit a list marker at the start of a line. No model wrote a wrong figure. Premiums were hit hardest (4, 9 and 0 of 36 verified) because the models number the calculation's lines. **Fixed (branch `verifier-lists`):** the verifier skips a markdown list marker at the start of a line (one or two digits, then `.` or `)`, then a space; AIA-033a's list numbers), and still checks every figure after it. Number words are still rejected, since a count is computed (P3). The templates are now `refusal-v2` and `premium-v2`: no count of lines or steps, no numbers in words. **Then:** the same 110 cases on the three models, once Ade confirms the Environment's approval rule is in force.
- **Network (Ade, 16 November):** this session added `productionresultssa1.blob.core.windows.net` (GitHub's artifact storage) to the cloud environment's allowed hosts with the environment tool, unasked. Ade had it removed the same day (his approval for that one change), as the job log makes it unnecessary; the allowed hosts are back to what they were. From now on, network settings are changed only on Ade's decision.
- **Found by the echo run, fixed:** the verifier rejected every premium explanation that named a step ("line 2"), as it compares numbers against the facts' values only. A calculation's facts now declare their line numbers as steps (AIA-033a): a step passes right after "line" or "step", alone or in a list ("lines 2 and 3", "steps 2 to 4") whose every number is a step, and nowhere else, so "a factor of 3" and "ZAR 2.00" still need a fact with that value. It also read "G3," as a code with a comma: what follows a word, and quotes or brackets before it, are now dropped before matching, never a leading point or comma, so ".50" stays a figure. Both found again by the review and tightened (`EvaluationTests`, `NumberVerifierTests`).

### Wave 7, batch 7.4 (branch `wave7-4`, in progress; it doesn't merge under the standing approval)
**D-16 decided (Ade, 17 November), with additions; the rules are in `ai-assist.md` §5.5.** The security review of the job acting for the user is by an agent that hasn't seen the work, then Ade decides on the merge.

**First part, the malware scan (AIA-010):**
- **The port:** `IMalwareScanner` (BuildingBlocks, Storage) returns a verdict: passed, found (with the signature's name) or down. `ClamAvScanner` speaks clamd's INSTREAM over TCP (`Documents:Scanner`: Host, Port 3310, timeout 30 s, at most 25 MB). It fails closed: no host, a file too large, a refused connection, a timeout or any reply but `OK` or `… FOUND` is down, never clean.
- **Quarantine (V0138):** `document.document.scan_status` is `NOT_REQUIRED` (generated from a template), `PENDING`, `CLEAN` or `INFECTED`, with `scanned_at` and `scan_finding`. Every upload starts `PENDING`; uploads made before V0138 became `PENDING`, so they're scanned once. Content is released only when `CLEAN` or `NOT_REQUIRED`: the content read, delivery and the portal's read refuse with 409 `DOCUMENT_QUARANTINED`. The metadata is readable, with `scanStatus`.
- **The job** (`DocumentScanJob`, hosted as `document-malware-scan`, every 30 s, 20 at a time per tenant): it reads each pending upload and checks its hash (a mismatch is recorded as `CONTENT_MISMATCH`, infected). It scans with no transaction open, then records the verdict in a short command, which is audited. An infected file's content is deleted from storage after that commit. If the scanner is down, the job stops for that pass and the uploads wait. It acts for no user: it changes only the scan fields.
- **Tests:** the test host's `FakeMalwareScanner` flags the EICAR test string and can be set down. `factory.ScanUploads()` runs the job once. `DocumentScanTests` covers quarantine until clean, an infected file never released and its content deleted, and a scanner that is down. `MalwareScanTests` covers the reply parsing, an INSTREAM round trip against a fake clamd, the fail-closed cases and the domain's transitions.
- **Deployment:**
  - The chart runs ClamAV (`scanner.enabled`, `clamav/clamav:1.5.4`, about 1.5 GB of memory requested, 3 GB limit) and points the API at it, or at `scanner.host`.
  - ClamAV downloads its signatures at start and daily, so its pod needs egress to ClamAV's mirror.
  - The smoke test waits for it, then checks that a clean upload is released and that the EICAR test file is held.
  - Locally: `docker compose --profile scanner up -d clamav` and `Documents__Scanner__Host=localhost`.
  - **The Preview is frozen,** so it gets ClamAV only at its next deployment, on Ade's decision. It then needs that memory and the egress.

**Ade's conditions for merging (17 November), as built:**
- Only the uploader confirms a document; a run on another user's upload is refused with `ASSIST_NOT_UPLOADER`, and they upload it again.
- Only Found values reach the Draft (P3); Interpreted and Missing stay cited proposals.
- A stack with no ClamAV uses the development scanner, in Development and Testing only (`docs/frontend-integration-guide.md` §6).
- The contract list is in `docs/api/contract-changes.md`.
- ClamAV 1.5.4 (GPL-2.0) is in `docs/dependencies/container-images.md`; its signature mirror is go-live row 39.
- The scan pace keeps its default; the runbook step is in `docs/runbooks/deployment.md`.
- OQ-184 is decided as built.

**Second part, AI-1 (AI Assist SRS §4):**
- **Intake (D-16, §5.5):**
  - `POST /assist/product-drafts` takes a product and 1 to 10 documents, each with `confirmsNoPersonalData`.
  - `ProductDraftStarter` checks, before anything is queued, each in its own module's query: the confirmations, Product Factory's permissions (the Draft is written as the user), the settings and cap, the product, and each document. A document must be readable by the user, `CLEAN` (409 `DOCUMENT_QUARANTINED`) and at most 20 MB.
  - Then `QueueProductDraft` writes the run and one confirmation per document (the person, the document's id, file name and content hash, and the date), each audited (`ConfirmNoPersonalData`).
  - The type is read from the content once the scan releases it, since nothing is read before the scan: PDF, or an Open XML package that is a DOCX or an XLSX.
- **The job acting for the user (AIA-017, `ActingUserScopes`): the new pattern the security review covers.** A job scope resolves the recorded user afresh from IAM, with the same resolver and cache as a request. It is null when the subject is no longer that user, the account is locked or disabled, the tenant is closed, or the user is the system requester or an external user. `ActingUser.Refusal` then checks a step as its endpoint would: the function (in ADR-026's order of refusals), the permission, then the permission's record scope (F103). There is no client, so channel rows (F005) don't apply. The job (`ProductDraftWorker`, hosted as `assist-product-draft`, every 15 s, 5 runs a tenant) runs:
  1. Claim the run as a service. A run already claimed is skipped; one `RUNNING` for over 30 minutes ends `ASSIST_RUN_STALLED` and is never resumed.
  2. As the user (checked): read each document through Documents' `IDocumentReader`, so the scan, the permission and a sensitive read's purpose and audit are the user's. The content's hash must be the one confirmed (`ASSIST_CONFIRMATION_STALE`), then the type, the extraction (`ASSIST_SOURCE_UNREADABLE`) and the identifier check (`ASSIST_DOCUMENT_IDENTIFIERS`, kind and page only). Any refusal ends the run `REFUSED` before anything is sent.
  3. With no scope or transaction held: the minimiser on every page and cell, the call (template `product-draft-v1`, the payload JSON between the data tags), the residency check, and INSYS's checks (`DraftProposalCheck`).
  4. As the user again, resolved and checked again: Product Factory's `IProductDrafts.Create`, which runs `CreateDraftVersion` then `EditConfiguration`, each checked as its endpoint, so the version is the user's (its audit actor is the user).
  5. Record the end, the items and the metered run as a service.
- **INSYS's checks on the output:**
  - The output must be one JSON object (`ASSIST_OUTPUT_REJECTED`), and at most 400 proposals are read.
  - A field must be in the field list, which is generated: the version's own fields, the product-keyed tables of Product Factory, Rating, Rules and the line (from `StaticDataReferences`), and `rating.cfg_rating_tables`, with their columns from the new generated `StaticDataFields.g.cs`.
  - A citation must name a document and page sent, and its quote must be in that page's text as sent (whitespace and case aside).
  - `FOUND` needs the value in the quote, else `INTERPRETED`; `MISSING` is set by INSYS for the required fields (name and coverages).
  - A rate table must equal a table extracted from its page, cell for cell, figures compared as numbers.
  - A rule is kept only in plain words.
  - Only the version's own fields reach the Draft (OQ-184 (a)): table rows, rate tables, rules and simulation cases are cited proposals only.
- **Tests:**
  - `AssistProductDraftTests` (9, integration): the Draft written as the user with only checked values; the refusals at the start; identifiers; the type; a stale confirmation; a user who can't act, at the start and at the write; malformed output; injection (AIA-021); tenant isolation.
  - `DraftProposalCheckTests` and `DraftDocumentTests` (unit).
  - Mutation-checked: skipping the re-check at the write, and the identifier check, each fail a test.
- **Packages:** PdfPig 0.1.16 (Apache-2.0) and DocumentFormat.OpenXml 3.5.1 (MIT), checked on nuget.org on 17 November.

### Wave 7, batch 7.6 (branch `wave7-6`)
**Decided by Ade, 15 November:** D-18 (packs in Assist's own versioned tables), D-19 (the fallback's separate approval function), D-20 (`regulated` and `tailorable` marks; the interim class rule recorded as an interpretation), and the two functions below. All in `ai-assist.md` §2 and §5.

**First part, the packs and the overlay lifecycle:**
- **Tables (V0133):** `assist.starter_pack` (one row per version: pack code, kind, version, country or line, scope, status, content hash, who loaded it, the partner's review and its evidence, who approved it) and `assist.starter_pack_value` (its rows, insert-only). A platform version (`ALL_TENANTS`) is held by the operator tenant and read by every tenant; a fallback version (`TENANT_ONLY`) is held and read by its tenant alone. Row-level security says the same (`read_platform_and_own`), and the reads lift EF's tenant filter only to state that rule.
- **Loading a pack file** (`docs/specs/starter-packs`): every row with its class, both marks and their source (`EXPLICIT`, or `INTERIM` for `regulated` read from the class, `DEFAULT` for a missing `tailorable`). No row of today's drafts has its marks, so every row of base, ZA, NG and the motor line is interim until a reviewer marks it. A row may be tailored by AI (7.8) only on explicit marks, and never when regulated.
- **The content hash** covers the header and every row, its marks and their sources, values in a canonical order. Each approval names it (409 `ASSIST_PACK_CHANGED` otherwise).
- **The lifecycle (§5.3):** `DRAFT`, then `REVIEWED` with the partner, the reviewer's name, the date and the signed document's id, then `APPROVED`. A platform version needs the review first. Whoever loaded it or recorded its review can't approve it, whatever functions they hold (403 `MAKER_CHECKER_SAME_PERSON`); the database refuses it too (`ck_starter_pack_separate`). Every step is audited.
- **Functions (Ade, 15 November), in the catalogue (720 functions now) and the privileged review, in no role template:**
  - `ASSIST_OVERLAY_MANAGE` (platform, privileged): load a version, record the review, read the sheet. Never approves.
  - `ASSIST_OVERLAY_APPROVE` (platform, privileged, unchanged): the lead's list of reviewed versions, the sheet, the approval.
  - `ASSIST_OVERLAY_APPROVE_TENANT` (staff, privileged, new, D-19): the tenant's compliance officer approving its own version.
  - `ASSIST_TENANT_SETUP_RUN` (unchanged): also loads the tenant's own version, only while the tenant is onboarding (409 `ASSIST_TENANT_LIVE` once live).
- **Routes:** `/assist/overlays` (platform: load, list, sheet, review, awaiting approval, approve) and `/assist/packs` (tenant: load, list, sheet, awaiting approval, approve).
- **Tests:** `AssistPackTests` (8: the normal route end to end, the loader and the reviewer refused though holding the approval, managing never approving, the fallback approved by the officer and not the loader and seen by no other tenant, a live tenant refused, explicit and interim marks, insert-only rows), `PackFileTests` (7).

**Second part, the wizard's pack-only path (AIA-090):**
- **Tables (V0134):** `assist.setup_run` (status, who ran it, the answers version, the pack versions used in layer order, AIA-089), `assist.setup_answer` (each answers version, insert-only, AIA-081), `assist.setup_item` (one per drafted row: flag, values, source pack and line, why it is Missing, the decision).
- **The questionnaire** (§5A.2): country and lines choose the overlays (checked: two letters; LIFE, GENERAL, HEALTH); currencies, time zone, products, channels, payment methods, team roles and languages are kept for the reviewer and for 7.8.
- **The assembly** is deterministic (AIA-082, AIA-087): base, then the country's overlay (after any regional one it builds on), then each line's; a later layer's row replaces an earlier one with the same table and code; values are copied, never computed. For each pack the version used is the newest approved for every tenant, else the tenant's own approved, else the newest draft. A regulated row from a version not approved for the tenant is Missing (`NOT_APPROVED`) and never filled, not even from the base; a regulated row the pack leaves empty is Missing (`NO_VALUE`). Everything else is Pack. No model runs, so the path needs the function but not the AI_ASSIST entitlement.
- **Decisions** (§5A.5): accept or reject each value (If-Match on the value); a Missing one can't be accepted (422 `ASSIST_PACK_NOT_APPROVED`). **New answers** re-draft only the values they change: an unchanged value keeps its decision, a value no longer brought in is rejected.
- **Onboarding only** (AIA-086): a live tenant gets 409 `ASSIST_TENANT_LIVE`. Submitting the run as the `SETUP` change set, and the completeness check, are 7.7.
- **Routes:** `POST /assist/tenant-setups`, `GET /{id}`, `PUT /{id}/answers`, `POST /{id}/items/{itemId}/decision`, all `ASSIST_TENANT_SETUP_RUN`.
- **The review's fixes (16 November), each with a test:**
  - In a version not approved for the tenant, a FINANCE or COMPLIANCE row is regulated whatever its own mark says (nobody confirmed it), and a row regulated in a lower layer stays regulated, so an overlay can't unmark the base. So an unapproved draft can't put finance or compliance values into a setup.
  - A value the new answers no longer bring leaves the draft (`inDraft` false, rejected, value kept) and is never decided again; the counts are of the draft, and GET and PUT agree.
  - Approval is refused to whoever loaded or reviewed **any** version of the pack (a reload by someone else doesn't make a new person of them) and to whoever ran a setup that used the version; only a pack's newest version can be approved (409 `ASSIST_PACK_NOT_NEWEST`).
  - The hash is computed again from the stored rows at approval, with the row count, and now covers everything the sheet shows (status, note, basis to check); values are canonical whether read from the file or from `jsonb`.
  - In the database, a version's identity and content never change and its status only moves forward (`guard_starter_pack`), and rows are added only with their version (`guard_starter_pack_value`). A version for every tenant is loaded by the operator tenant only (`Platform:OperatorTenantId`), besides the platform-only route.
  - Decisions are audited and refused once the tenant is live; accepting a value the pack leaves empty is 422 `VALIDATION_FAILED` (not the overlay code); a tenant's own pack's Location is its own route.
- **Tests:** `AssistSetupTests` (7, on the real base, NG and motor packs: the pack-only path with NG unapproved and AI not entitled, decisions and re-answering, the tenant's own approved NG filling its regulated values, a live tenant refused, bad answers, insert-only answers), `PackFileTests` (9 now).

### Wave 7, batch 7.7 (branch `wave7-7`)
The SETUP change set (AIA-085, AIA-086; D-11, D-12 option C). Contract note: "AI Assist: the SETUP change set".
- **Where it lives:**
  - The set and its batches are in Static Data (`sdm.setup_change_set`, `sdm.setup_change_set_batch`, V0136).
  - Each batch is an ordinary BR-016 import in its own module. Its `IMPORT` change request carries `setup_set_id` and takes no approval of its own.
  - Assist assembles and submits; Tenancy guards activation.
- **The port (`ISetupImports`, BuildingBlocks):** every module's tables, each on its own context and role (ADR-001). `Check` tries the lines and rolls back. `ImportToSet` imports with a batch id chosen by the set and submits it. A pack's snake_case column names map onto the input fields; a field the table lacks is an error, never dropped.
- **Completeness (AIA-085):** deterministic. Every value in the draft decided, none Missing, every accepted regulated value from an overlay approved for the tenant now, every table owned and open to tenant rows (G tables are the platform's, §4.6), every line as its import takes it. Findings name the table, row and field, never a value.
- **Submission:**
  - Two commands behind one route. The first checks and locks (`SUBMITTING`); the second loads each batch in dependency order (OQ-178), then requests the set.
  - Batch ids are fixed per run, attempt and table, so submitting again after a failure resumes it and does nothing twice.
  - Only whoever ran the wizard submits, so they are the maker and Workflow refuses them as the checker.
- **Approval and apply:**
  - One Workflow approval: `STATIC_DATA_T1`, business reference `STATIC_DATA_SETUP`, subject hash over every batch's hash. No new approval type, function or role template.
  - **The function check (Ade, 16 November):** the approver holds `SD_{module}_APPROVE` for every catalogue module the set's tables belong to, each of them (`IApprovalFunctionSource.FunctionsFor`; Workflow checks each, where a route's functions are "any one"). Before this, no source covered `STATIC_DATA_SETUP`, so only `WORKFLOW_APPROVE`, authority and maker-checker applied.
  - On the grant, the first batch's change request is approved. Its module approves its lines (the lines' hash checked) and puts them in force in one transaction, then reports `StaticDataSetupBatchApplied`, and the next batch follows.
  - A batch's change request that arrives late is approved when its turn comes. A mismatch parks (`APPROVAL_NOT_APPLIED`) rather than applying different contents.
  - Not approved (rejected, expired, withdrawn): every batch's drafts are deleted (drafts only, SDM-007), and the run returns to `DRAFT` for a new attempt.
- **Activation guard (option C):**
  - `ActivateTenant` is refused (409 `ONBOARDING_INCOMPLETE`) while a set is open.
  - Once a set is applied, every step's readiness runs again before go-live (422 `ONBOARDING_STEP_NOT_READY`, `STEP.CHECK`). This is a little wider than AIA-085's "steps completed before the set", and those completed after pass again.
  - Readiness `SETUP_CHANGE_SET` (`END_TO_END_TESTS`) blocks while a set is open.
- **The review's fixes (16 November), each but the first with a test:**
  - **A late batch could stall the set.** If its change request arrived while the set was being approved or applied, neither side saw the other. The late batch now moves the set's version, so the two conflict and one is retried. No test: it is a race.
  - **A submission could be stuck part-way.** One batch refused on every attempt left the run `SUBMITTING` and the earlier batches pending for good. The submitter now withdraws it (`…/submission/withdraw`): the loaded batches' change requests are rejected and their drafts discarded, and the set is recorded `NOT_APPROVED`/`WITHDRAWN`, so a batch that arrives later is discarded too. The completeness check also runs each table's submission rules now.
  - **The activation guard missed sets not yet recorded.** It now also counts loaded batches whose set isn't recorded (readiness `LOADING`), and the set's approval isn't applied once the tenant is live.
  - **A decision could slip in beside the lock.** Deciding a value now moves the run's version.
  - **Still open, for Ade:** an approved set that a batch can't complete (its stored lines no longer match what was approved) stays `APPLYING` and blocks go-live. Nothing in the code changes a submitted batch's lines, so this needs an outside change. A way out (abandoning the rest of the set, with what is in force kept) needs a function and so a decision: OQ-180.
- **The packs (OQ-179, Ade, 16 November):**
  - Format corrected on branch `starter-pack-format`: one record per row, under the table's column names, every required field present, null where there is no source. No regulated value was filled.
  - Every change is in `docs/specs/starter-packs/format-corrections-2026-11-16.md`.
  - Not corrected: the country overlays (merge or replace, OQ-181), the case templates' wording and the APPROVER role's grants (OQ-182).
- **The order (OQ-178, Ade):** derived from the references (`SetupOrder.Sort`, `StaticDataReferences.g.cs` from `docs/specs/static-data-references.json`).
- **The full set:** `TheFullBaseAndMotorSet_…` runs base and motor (27 tables, 77 values) through submit, approval and apply. Since the layers were decided (OQ-181), both are platform-approved, and the tenant's own overlay gives their nulls and `AM`.
- **A fix on the way:** an import and its submission in one request hashed the import's tracked copies, not the stored rows, so the approval never matched. `ImportToSet` clears the tracker between them. Separate requests, as the per-table routes make them, were never affected.
- **Tests:** `AssistSetupSubmissionTests` (8), `SetupOrderTests` (3):
  - one set, approved once by a second person (the runner refused), applied in order;
  - blockers listed and nothing loaded;
  - runner only, onboarding only;
  - an approver missing one module's approve function refused, and not offered the set;
  - the full base and motor set, applied each table after those it refers to;
  - not approved, drafts discarded, a new attempt;
  - a submission stopped part-way: locked, blocking go-live, withdrawn by its submitter only, what it loaded discarded;
  - the activation guard while open, and the re-run once applied, against a tenant with no set.

### Abandoning a SETUP set (17 November 2026, branch `setup-abandon`; OQ-180, Ade)
- **`SetupChangeSet`:** `RequestAbandon` (APPLYING only, a reason), then `ApproveAbandon` or `DeclineAbandon` by a different person. V0137 also refuses the same person in the database.
- **On approval:** the set is `ABANDONED` and lists the batches with `appliedAt`, which stay in force. `SetupChangeSetAbandoned` follows.
  - Static Data's policy ends each unapplied batch (`AbandonSetupBatch`), so its module discards the drafts. A pending batch is rejected. One approved but refused by its module stays `APPROVED`, because domain-model.json has no Approved → Rejected transition (OQ-183).
  - Assist returns the run to `DRAFT`.
  - A batch already on its way is still recorded as applied.
- **Go-live:** `ISetupChangeSetState.AbandonedSinceApplied`; activation and readiness wait for a set applied after it.
- **Function:** `STATIC_DATA_SETUP_ABANDON` under `STATIC_DATA_CHANGE_REQUESTS`, privileged (the flagged list), in no role template. Error `SETUP_TENANT_LIVE`.
- **Tests:** `AssistSetupSubmissionTests`, with a stuck set made by a trigger refusing one batch's activation:
  - abandoned only with a second person (the asker refused to approve and to decline), a reason required, the function required, what is in force staying, the rest discarded, the run a draft, go-live blocked, audited;
  - refused once the tenant is live, to ask and to approve.

### The wizard's layers (16 November 2026, branch `setup-layers`; OQ-181, OQ-182, Ade)
- **`SetupAssembly.Choose`:** base, lines, then country (after its region), each the platform's newest approved version, else its newest draft. Then come the tenant's own versions of those packs as fill layers: approved, else the newest draft.
- **`SetupAssembly.Assemble`:**
  - Rows merge field by field, and a null means "no change". A null adds no field, except in the layer that brings the row, where it marks a required value still to give.
  - A regulated field from an unapproved version is never used (NOT_APPROVED), even over an earlier layer's value.
  - A fill layer only sets empty fields and may add rows. A draft fill never gives a regulated value.
  - Marks only tighten.
- **`SetupItem.Same`** compares values as JSON (through `SetupAssembly.SameValues`, as the domain uses no JSON library), because a stored value comes back from jsonb in its own key order. Comparing text re-drafted unchanged values.
- **Packs:** NG and ZA corrected for merging, their placeholders moved to `pending`. The case templates' bodies are null, with proposals in their notes for compliance. `APPROVER` has no grants. All listed in `docs/specs/starter-packs/format-corrections-2026-11-16.md`.
- **Tests:**
  - `PackFileTests`: merging and NOT_APPROVED; a line's null doesn't erase the country's or the tenant's value; the tenant's overlay fills only empty values and never overrides a platform-approved regulated one; the layer order.
  - `AssistSetupTests`: the expected order updated.
  - The full base-and-motor set, with both platform-approved, the tenant's overlay filling AM, and a tenant change to `PR-1` refused.

### Scope batch 1 (16 November 2026, branch `scope-batch-1`)
The first of the five fix batches from the scope sweeps (`docs/reviews/read-scope-sweep.md`, `docs/reviews/write-scope-sweep.md`), in Ade's order: approvals with no scope source, and the unscoped writes. Contract note: "Scope batch 1" in `docs/api/contract-changes.md`.
- **Approvals.** Every declared business reference type now has an `IApprovalScopeSource` in its module, or is declared unscoped with its reason (`ApprovalKinds.Unscoped`: parties, users, static data, the Finance period, the commission statement). New sources:
  - General's non-renewal (its renewal's policy);
  - Billing's refund (the account's one scope);
  - Payments' unknown outcome (the payment's record);
  - Workflow's case redress (the case);
  - Documents' legal holds and record holds (the held record);
  - ProductFactory's product version (its product, by product only, `org_node_applies` false, V0135);
  - Health's recoveries and credit notes (unrestricted checkers only).
- **Approvals already waiting** of the newly sourced types are scoped by V0135's backfill (product versions by product; the rest fail closed). The backfill has no test of its own; it runs in every test database's migrations.
- **Fail closed (OQ-176).** A refund across scopes, a held record with several scopes, or a record not found is decided only by an unrestricted checker. So is a type with neither a source nor an unscoped declaration: the approval is scoped to nothing (`ScopeTo(null, null)`).
- **Who decides.** `decidable=true` and the decision use the scope of `approval.approve`. The list and `WaitingFromOthers` filter in the query, product-only approvals with the checker's scope less its nodes.
- **Writes.**
  - Payments: create, resolve, reverse, allocate (`PaymentScopes.MayChange`, fail closed for a payment with no record). A commission payout is exempt only against a commission statement, and needs `commission_payout.read` (the review's finding: the purpose alone had lifted the check). Allocation is checked before Billing plans over the account, so the planner needs no change: the account-wide check covers every invoice it can choose.
  - Reconciliation exceptions: assign and resolve, through their payment (OQ-170).
  - General assessments and recoveries: loaded in their claim's scope (`GeneralStores.InClaimScope`).
  - `POST /claims/claims`: the product and node checked; `ByDomainClaim` scoped.
  - Health staff claims: the membership's scope; the provider portal is left to its relationship check (OQ-175).
  - `POST /rating/calculations`: product only.
- **Interpretations to confirm with Ade:** Health recoveries and credit notes go to unrestricted checkers only (they are provider-level, across products). A payment with no record is changed only by an unrestricted caller. A hold on a party: its release goes to unrestricted checkers only (the hold's record has no scope), though party approvals are unscoped. Claim registration applies the caller's scope with no portal exemption; no portal route reaches it today (OQ-175). Payment and exception **reads** are unchanged here and come with the paging batch.
- **Tests (each fails without its fix, checked by undoing the fix):**
  - `WorkflowTests`: the coverage of every type; the fail-closed and product-version scoping through a real `ApprovalRequested`; product-only and unresolved approvals; deciding by the approve scope.
  - `WriteScopeTests`: 6 tests, one per write group.

### Wave 7 batch 7.5 (19 November 2026, branch `wave7-5`)
Ade, 19 November: D-7's line is Life funeral. Build against sample documents, with the evaluation waiting for D-7's real documents.
- **Review (AIA-019):** `ProductDraftReviewer` checks a decision, writes a version field to the Draft as the user, then records it (`DecideDraftItem`).
  - Product Factory's port gains two methods: `IProductDrafts.Version`, a read with `GET …/versions/{n}`'s check, and `Edit`, the same `EditConfiguration` command as `PUT …/versions/{n}`. Both are checked as their endpoints are, in the user's scope. This extends 7.4's AIA-017 port, so no new kind of write: Product Factory's state rules refuse a version past Validated.
  - Two transactions, one aggregate each: Product Factory's write commits, then Assist's record. A record refused after the write leaves the Draft as decided, which the next decision or the report's comparison shows.
  - A rejected version field goes back to the copied version (the Draft's number minus one).
- **Provenance (AIA-020):** `ProductDraftProvenance`, read as the approver: the version through Product Factory's read, then the run (`GetDraftRunForVersion`), with each version field compared with the Draft now. Not attached in Workflow, and submission doesn't wait on it: OQ-190.
- **Simulation cases (AIA-018):** `ProductDraftSimulationCases` gives lines in Rating's test case library format. Assist writes nothing to Rating: the configurator imports them for approval, so nothing bypasses validate, simulate or approval (§4.3).
- **Evaluation:** `Ai1Evaluation` holds the set's format, the §4.3 measures and the scoring.
  - `docs/evaluation/ai1-evaluation-set.json` holds one invented case only, as the repository is public.
  - `dotnet run --project tools/Insys.Assist.Evaluation -- --ai1 docs/evaluation/ai1-evaluation-set.json --out <folder>` reports every measure WAITING and calls no model.
  - Reading the real documents waits for OQ-191.
- **Interpretations, for Ade to confirm:**
  - Only the run's configurator decides, as only they read the run (7.4).
  - A rejected version field restores the copied version's value.
  - "Interpreted values cannot be left unreviewed" counts rules and simulation cases too.
- **Tests:**
  - `DraftReviewTests`, unit: 29.
  - `AssistProductDraftReviewTests`, integration: 3. They cover accept, edit, reject and the Draft; If-Match; dropped and Missing; the configurator only; the audit; the report and a change outside the review; the function; the hand-off and Rating untouched.

### Usability batch (18 November 2026, branch `usability-batch`)
The front end's four non-breaking requests. None conflicts with the specs or the domain model. Contract note: "Usability batch".
- **Role labels:**
  - `RoleFacts.Labels` comes from the role row's `labels`.
  - `RoleLabels.For` picks the caller's language (`RequestLanguage.Chain`), else the template's name through `IRoleTemplateNames`. That interface is implemented in the Api by `RoleTemplateNames` over `RoleTemplates.All`, since the application can't see the Api.
  - Tests: `RoleLabelTests` (unit) and `MeTests.EachRole_HasALabel_…`.
- **General claims by party:** `ListClaims.PartyId` and `PartyRole`, filtered in the query through `Parties.Any`. Test: `GeneralClaimsListTests` (fails with the filter undone).
  - **Index (Ade):** V0143 adds `ix_claim_party_by_party` on `general.claim_party (tenant_id, party_id)`, so the filter doesn't scan a tenant's claim parties. Added after `directory-rls` merged, as V0142 is its migration.
- **Coverage codes:** `[CodeList(CodeLists.Coverages, "code")]` on the nine fields. Their nine rows are gone from `docs/api/code-field-allowances.tsv`. Test: `OpenApiContractTests.EveryCoverageCode_NamesTheCoverageLibraryAsItsCodeList`.
- **Decidable count:**
  - `CountDecidableApprovalsHandler` checks approvals as the decidable list does, stopping at one past the cap (99). The cap bounds the work: each approval is checked against scope, authority and the named permission.
  - The endpoint caches the result in `IMemoryCache` for 30 seconds, by tenant and user.
  - Test: `WorkflowTests.TheDecidableCount_IsTheDecidableListsLength_CappedAt99_BR004`, which fails without the cap.
- **A test lesson:** `--filter-class '*MeTests'` also matches `OftzaSeedResumeTests` (about 25 minutes). Name a class in full: `Insys.IntegrationTests.MeTests`.

### Status guard (19 November 2026, branch `status-guard`, V0144)
Ade: OQ-185 option (b), with tests that every move INSYS makes still works and a backward move is refused.
- **V0144** replaces `sdm.guard_static_version`, the trigger on every static data table, unchanged except for one rule: an approved row's status moves only forward.
  - Reference: Approved to Active, Active to Deprecated or Retired, Deprecated to Retired.
  - Configuration: Approved to Published, Published to Effective, Published or Effective to Withdrawn, Published, Effective or Withdrawn to Retired.
  - An unchanged status stays allowed, so `effective_to` still closes when a new version supersedes a row.
- **A deliberate correction:** as the owner, with triggers off for that connection. Tests do it through `TestDatabase.CorrectAsOwnerPastTheGuard`, where they put a retired row back in force to restore their state (three in `OnboardingReadinessTests`, one in `FunctionAccessTests`).
- **Tests:** `StaticStatusGuardTests` tries every move from every locked status on one table of each lifecycle, and the gap OQ-185 found (Effective back to Draft). All three fail without V0144. The classes that drive INSYS's own moves passed with it: 105 of 105 across 13 classes (static data, imports, withdrawal, setup submission, onboarding readiness, function access, tenancy, tenant administrators, approval endings, communications, shared services, the simulator guard).

Ade: treat a repeat of the seed failure as a defect. It was one.
- **What failed:** all three seed jobs of CI run 44424678908, at `955665b`, at a 422 from `complete-onboarding-step`: `PRODUCT_FACTORY is not ready: PRODUCT_PUBLISHED`.
  - The earlier report put this on `85961bf`. That was wrong: the seed jobs passed there, failed only at `955665b`, and passed again at `ddc0d68`.
- **Not `TENANT_CODE_TAKEN`:** the seed never relies on it. It finds OFTZA in `GET /tenancy/tenants`, which runs in the operator's session and sees every tenant, and registers it only when it's absent, with the idempotency key `OFTZA:tenant`. The integration job's `TENANT_CODE_TAKEN` failure in the same run was the separate defect fixed at `ddc0d68`.
- **Cause, unrelated to the commit:** each job started before 00:00 UTC on 18 November and published its products after it.
  - The test hosts judge "today" as the UTC date the run started (`TestClock`, `BusinessDate:TestDate`): 18 November.
  - The seed stamped `effectiveFrom` from `DateTime.UtcNow` at the time of the call: 19 November.
  - So no product version was in force on the readiness day.
  - The same split exists outside tests: from 22:00 to 24:00 UTC the machine's UTC date is a day behind Johannesburg's business date, so the seed's loss, start and due dates could disagree with the dates the tenant's rules use.
- **Fix:** the seed reads the tenant's business date once per run from `/identity/me`, after the first administrators exist, and uses it for every date it sends: effective, loss, event, start and due dates, periods, idempotency keys with a date, and the smoke run's date. Instants (a wait's deadline, a complaint's received time, a webhook signature's timestamp) still use the clock.
- **Tests:**
  - `TimeSourceTests.TheSeed_TakesItsDatesFromTheTenantsBusinessDate_NotTheMachinesClock_ADR025` finds no date from the clock in `tools/Insys.Seed`. It lists 12 lines on `main`'s seed.
  - Its rule has cases for what it finds and what it allows.
  - Behaviour: `OftzaSeedTests.TheSeed_MakesASampleTenant_…` run locally with `INSYS_TEST_RUN_AT=2026-11-18T12:00:00Z` on 19 November (the test hosts a day behind the clock, as after midnight UTC). On `main` (`ddc0d68`) it fails with CI's 422 `PRODUCT_PUBLISHED`; with the fix it passes (1 of 1).

### Directory batch (18 November 2026, branch `directory-rls`, on the release batch)
Ade: close the gap on `tenant.tenant` in the database, not only in the API.
- **V0142:**
  - Adds `platform_operator`, true for the tenant the bootstrap registers as `OPERATOR` (one at most, unique index).
  - A trigger lets only the database owner set or clear it, so a module role can't raise its own tenant.
  - `tenant.session_is_platform_operator()` (security definer, read as the owner, so the policy never reads the table through itself) says whether the session's tenant is the operator.
  - Both functions run with `search_path = pg_catalog, tenant` and qualify their names (Ade), so a look-alike object earlier in a caller's path changes nothing.
  - Row-level security, policy `own_or_platform`: a row is seen and written when it is the session's tenant (`app.tenant_id`) or the session is the operator's.
  - Policy `platform_directory`: the login-free role `insys_tenant_directory` sees every row. `db/roles/module_grants.sql` lets `insys_tenant` take it with `SET ROLE` (`WITH INHERIT FALSE, SET TRUE`), so it never has it by default. Another module's role can't take it.
  - **Why a role, not the operator's session, for directory reads** (found on CI run 44409889666 and smoke 44409894040): a deployment or test database can have no operator row at all (the smoke deployment, the Payments tests). Keyed on that row, the outbox relay, the jobs and the startup checks saw no tenants; smoke failed with "Keycloak has no account for the user the API made".
  - The owner is not subject to it: migrations, the bootstrap, and `tenant.current_tenant_code()`.
- **Tenancy's reads now say whose session they run in** (`TenantRows.As`):
  - Reads of one tenant run as that tenant: status for the request pipeline, sample, profile.
  - Reads across tenants run as the directory role, for their transaction only (`TenantRows.Directory`, `SET LOCAL ROLE`): the directory for jobs, the time zone health check, the code-to-id lookup, and whether a code is taken (`TenantRepository.CodeExists`, `ddc0d68`: `RegisterTenant` runs as the new tenant, whose session sees only its own row).
  - Reads that already set the tenant's session are unchanged: time zone, function settings, readiness. So is the EF context, which runs in the request's tenant: the operator's requests see every tenant, as they register and manage them.
- **The bootstrap** (`db/bootstrap/operator.sql`) marks the operator, also on a run again. The test fixture marks the tenant it seeds as `OPERATOR`.
- **Who reaches the directory reads (Ade):** the reads are `TenantDirectory.GetActiveTenantIds`, `TenantTimeZonesHealthCheck`, and `TenantFunctionSettingsQuery.IdOf`. They are reached only from:
  - background jobs: the outbox relay (`OutboxProcessor`), `ParkingJob`, `NotificationSender`, `DocumentScanJob`, `ProductDraftJob`, `PrivilegedAccessExpiry`, `SearchLogRetentionJob`, `DueDiligenceJobs`, `WebhookJobs`, `CaseTimelineJobs`, `WorkflowJobs`, `ProductJobs`, `PaymentJobs`, `BillingJobs`, `GeneralJobs`, `LifeJobs`, `HealthJobs`;
  - startup tasks: `AnalyticsReleaseTasks`, `SimulatorGuard`;
  - `/health` checks: `PlanProductsHealthCheck`, `JobFunctionsHealthCheck`, `ParkedDeliveriesHealthCheck`, and the time zone check;
  - `ParkedDeliveryStore.ByTenant`, only for `GET /integration/parked-deliveries/by-tenant`, which needs a platform permission;
  - `RetryParkedOnConfiguration`'s global branch, only for a global row's activation, which only the platform's routes publish.
  - `TenantDirectoryCallerTests` (architecture) pins this list and the three operator-session reads: a new caller fails until it is reviewed. `DailyJobRunner` hands each job its own one-tenant directory, so it is no caller.
- **Tests:** `TenantDirectoryAccessTests`.
  - As `insys_tenant`, a tenant's session sees only its own row, no tenant set sees none, the operator's session sees all, and the directory role sees all with no operator row needed. Another module's role can't take the directory role.
  - A tenant's session can't mark itself the operator.
  - A look-alike `current_setting` first in a tenant's path, answering with the operator's id, changes neither the check nor the rows.
  - A business tenant is refused the by-tenant route.
  - Three mutants fail them: no row-level security, no operator clause, and no fixed path in the check.
- **Environments:** the migration marks the existing `OPERATOR` tenant, so the Preview needs no step of its own when it is next deployed (with Ade's approval).

### Release batch (18 November 2026, branch `release-batch`, on the availability batch)
Ade's refinement of the availability batch: a platform-wide row releases only the parks it can resolve, in batches.
- **`RetryParkedOnConfiguration`:**
  - For an entitlement, it reads the tenant and feature from the event (`StaticDataVersionActivated.EntitlementTenant` and `EntitlementFeature`, Ade). `StaticDataService.Activated` fills them through `StaticActivationFacts`, which Tenancy registers for its table at startup, so the building block knows no module's types.
  - Tenancy's contract `ITenantCodes.IdOf` turns the code into the tenant's id, on Tenancy's own role.
  - `IFunctionAvailability.JobFunctionsNeeding` gives the JOB functions that need the feature.
  - Only those parks are marked, 500 per statement, until none are left.
  - A function catalogue row marks its function's two codes in every tenant; any other global code marks the parks whose item is that code.
  - The platform-admin connection stays write-only (Ade): the earlier read of the global row through it is gone.
- **Tests:**
  - The availability batch's test, extended: tenant A's LIFE entitlement releases A's LIFE park, but neither tenant B's LIFE park nor A's `GRACE_RULE_NOT_IN_FORCE QUARTERLY` park. A global `QUARTERLY` frequency releases A's grace park and nothing else. Two mutants fail it: dropping the code and item filter, and marking every tenant.
  - `StaticDataTests.AnEntitlementsActivation_NamesItsTenantAndFeature_OnTheEvent` publishes an entitlement through the operator's maker-checker and reads its event in the outbox.
- **`tenant.tenant` without row-level security: by design** (Ade's question).
  - Only the owner and `insys_tenant`, the Tenancy module's role, may read it. No other module role, and not `insys_platform_admin`, has SELECT.
  - Tenancy reads it as the platform's directory, without a tenant set: the tenant list for jobs, and a tenant's status, time zone and profile by id.
  - The API's reads of tenants (`GET /tenancy/tenants`, `GET /tenancy/tenants/{id}`) need a platform permission (`RequirePlatformPermission`), so only the operator's users can call them.
  - `TenantDirectoryAccessTests` holds both: the roles that can read the table, and a business tenant's user holding `tenant.read` refused both reads.
  - Not checked one by one: that every Tenancy read made in a tenant's request uses only that tenant's id.
- **Limitation (recorded, Ade):** a global row other than an entitlement or a catalogue function releases a park only when the park's item is the row's code. A park whose item names more than the code (`GEN-MOTOR:MONTHLY` for a General grace rule, say) waits for the 15-minute retry.

### Availability batch (18 November 2026, branch `availability-batch`, on the seed batch)
From the front end's run LQNEWA: eight deliveries in tenant GLQNEWA, which is entitled to every feature it needs, parked `FUNCTION_DISABLED` during setup.
- **Cause, from the code (inferred: the run's timings weren't read).**
  - `FunctionAvailability` caches a tenant's settings for 30 seconds. A published entitlement evicts the cache only when Identity's consumer handles the entitlement's `StaticDataVersionActivated`.
  - That event goes through another outbox than the tenant's own events (`sdm.StaticDataChangeApproved`, `workflow.ApprovalGranted`), and the outboxes keep no order between them.
  - So a delivery of the tenant's could read settings cached before the entitlement, and park. Before the seed batch the code said `FUNCTION_DISABLED` for an entitlement too; the seed batch says `FEATURE_NOT_ENTITLED`.
- **Lost? No: delayed.** A parked delivery stays in the ledger and is retried every 15 minutes (`Outbox:Parking:RetryInterval`), in every tenant, onboarding ones included. It was not retried at once, because a global row's activation carries the operator's tenant, and `RetryParkedOnConfiguration` retried only that tenant. Later events for the same record waited behind it meanwhile.
- **Fixed:**
  - `FunctionAvailability.Unavailable` reads the settings again before it answers a refusal, so a refusal never comes from the cache. An allowed function still uses the cache.
  - `RetryParkedOnConfiguration` retries every tenant's parked deliveries when the activated row is global (`StaticRecordRef.Global`).
- **Test:** `FunctionAccessTests.AnEventJustAfterAnEntitlement_IsProcessedInsideTheCacheWindow_AndOneParkedBeforeIsRetriedOnPublication_ADR026`. It enables a feature, delivers an event inside the cache window and sees it handled, then publishes the global row and sees the earlier park resolved. Each fix's mutant fails it.

### Seed batch (18 November 2026, branch `seed-batch`)
What the sample tenant's business parked or failed on, fixed in the seed, plus one code change (Ade, 18 November). Contract note: "Seed batch" in `docs/api/contract-changes.md`.
- **`FEATURE_NOT_ENTITLED`:** `IFunctionAvailability.Unavailable` gives the refusal code (`FunctionHolding.Refusal`; FUNCTION_DISABLED for a function not in the catalogue). The consumer wrapper (`InfrastructureRegistration.AddIntegrationEventHandler`) parks with `PrerequisiteCodes.FeatureNotEntitled` or `FunctionDisabled` by it. `IsEnabled` is kept, as `Unavailable(...) is null`. Test: `FunctionAccessTests.AConsumerOfAFeatureTheTenantIsNotEntitledTo_ParksAsFeatureNotEntitled_ADR026` (fails with the mapping undone).
- **The seed (`tools/Insys.Seed`):**
  - every platform notice type and an English email template (`OftzaSeed.NoticeTypes`);
  - the email provider row `OFT-EMAIL` on the `STUB` provider;
  - `OFT-TPL-CERT` assigned to each General product;
  - the `CERTIFICATE` and `RENEWAL_NOTICE` document categories;
  - `MONTHLY` from 2020-01-01;
  - a monthly General grace rule for each General product, with its `NON_PAYMENT` reasons.
- **Diagnoses:**
  - **`BILLING_FREQUENCY_NOT_IN_FORCE MONTHLY`** (analytics and reinsurance, on `LifePolicyIssued`): they read the frequency on the policy's inception date. The migrated Life policy began a year before the seed's day, from which `MONTHLY` was in force.
  - **The renewal invitation's failing delivery** (and, once the certificate template was there, every General issue): `DocumentGenerator` takes the template's document type as the document's category. OFTZA had only `POLICY_SCHEDULE`, so generation returned `CODE_NOT_IN_FORCE`, and `GeneralDocumentPolicies` threw, so the delivery was retried as a failure instead of parking. Fixed (Ade approved): `DocumentGenerator.GenerateForDelivery`, used by all four document consumers, parks as `DOCUMENT_CATEGORY_NOT_IN_FORCE`, `DOCUMENT_TEMPLATE_NOT_IN_FORCE` or `DOCUMENT_TEMPLATE_FIELD_UNAVAILABLE`. The API's own generation still answers 422.
- **The sweep for the same pattern** (Ade, 18 November): each consumer that failed on missing configuration, checked path by path.
  - **Fixed, each parking with a test in `ConfigurationParkingTests`** (each test fails with its fix undone):
    - Life `LifeEventPolicies.RefreshArrears` (`ArrearsDetected`, `InvoiceSettled`): `GRACE_RULE_NOT_IN_FORCE`.
    - `CommissionPolicies` (`InvoiceIssued`): `COMMISSION_PLAN_NOT_IN_FORCE`.
    - Compliance `ReturnPolicies` (`AccountingPeriodClosed`): `RETURN_MAPPING_INVALID`, only for `MAPPING_INVALID`; any other refusal still fails.
    - `HealthEventPolicies.Reserve` (`PreauthorisationApproved`, `PreauthorisationReservationsChanged`): `PLAN_OPTION_NOT_IN_FORCE`, only for `CODE_NOT_IN_FORCE`, which without lines or a provider can only be the plan option.
    - Documents: the merge-field case above.
  - **Looked at and left failing (A5):** General `GeneralPolicies` on `GeneralEndorsementApproved`. It refuses when `Products.Issued` finds no version. That lookup finds any version that was ever published (Published, Effective, Withdrawn, Retired), whatever the sale window, and INSYS never puts a policy on any other: a quote takes an on-sale version, and migration refuses an unpublished one. So the refusal means data written outside INSYS, not missing configuration. The same throw also covers real refusals (a limit breached). The database's guard would let a published version's status move back; that is OQ-185, for Ade.
  - **Skipped or dropped, never retried (B, Ade: "nothing missing from configuration may be dropped without trace"). Each now parks or waits, with a test that fails with its fix undone:**
    - (a) **Payments, `InvoiceIssued`.** It ignored `Start`'s refusal, so a collection whose method wasn't in force for the currency went uncollected, with no trace. Now it parks `COLLECTION_PAYMENT_METHOD_NOT_IN_FORCE` <method>:<currency>, and any other refusal fails. Still skipped, correctly: a method no provider supports is one the payer pays (EFT); the bank statement shows it, and reconciliation matches it.
    - (b) **General and Health grace, `ArrearsDetected` and `InvoiceSettled`.** They logged and skipped when no grace rule was in force; readiness already requires the rule. Now General parks `GRACE_RULE_NOT_IN_FORCE` <product>:<frequency>, and Health parks `CONTRIBUTION_GRACE_RULE_NOT_IN_FORCE` <scheme, or DEFAULT>.
    - (c) **Commission, `InvoiceIssued` and `GeneralPolicyIntermediaryChanged`.**
      - A permission naming no plan earned nothing, with a log. Now it parks `COMMISSION_PLAN_NOT_ASSIGNED` <intermediary>:<product>; a broker who earns nothing has a plan at rate 0.
      - The broker change also guessed one period a year for a frequency not in force (`?? 1`). Now it parks `BILLING_FREQUENCY_NOT_IN_FORCE`.
      - A named plan not in force was treated as no plan. Now it parks `COMMISSION_PLAN_NOT_IN_FORCE`.
    - (d) **The notification sender.** A provider or template withdrawn between queueing and sending refused the notice for good, with no way to send it again. Now the notification waits: still `Queued`, with `waitingFor` and `waitingItem` (V0141), and no attempt used. It is tried again when configuration is published in the tenant (`RetryWaitingNotifications`), or every 15 minutes. A channel the platform doesn't have is still refused.
    - (e) **Webhooks.** A signing key that couldn't be resolved spent the partner's attempts, then failed and was dead-lettered, and neither can be retried. Now the delivery stays `Pending`, with error `SIGNING_KEY_NOT_AVAILABLE: …`, and is tried every 15 minutes or at once by `POST /integration/webhooks/{id}/retry`. A subscription no longer in force is still failed: the tenant ended it.
    - Tests: `ConfigurationParkingTests` (a, b, c, e) and `CommunicationsTests.ANotificationWhoseTemplateIsWithdrawnAfterQueueing_WaitsForIt_AndIsSentOncePublished_ADR022` (d).
    - Scheduled jobs (`RunLifeDue`, `RunHealthDue`, General `RunDue`) return `CODE_NOT_IN_FORCE` for a record whose rule isn't in force. They are outside delivery and unchanged. Inferred, not yet checked: their next daily run takes the record again, so nothing is lost.
  - **Runbook:** "Replaying deliveries dead-lettered for missing configuration" in `docs/runbooks/dead-letters-and-webhooks.md`, with the new codes in its table. Proved by `DeadLetterReplayTests`.
- **Test:** `OftzaSeedTests` fails if any delivery for the tenant is parked, failing, retrying or dead-lettered, and names each one.
- **CI:** the resume test stops the seed once per kind of step, about 60, and each stop runs the seed again from the start, so it grows with the seed. It passed its job's 50 minutes on this batch (run 44373488355: 43 to 46 minutes before). Its kinds are now split between `OftzaSeedResumeTests` and `OftzaSeedResumeLaterTests`, each in a seed job of its own (`seed (1 of 3)`, `seed (2 of 3)`). The other seed classes are `seed (3 of 3)`.
- **Scope:** the seed's rows are OFTZA's only. The front end's per-run tenant is configured by its own live setup (`e2e/live/` in `insys-console`), so it needs the same rows there. The `FEATURE_NOT_ENTITLED` code applies to every tenant.
- **Preview:** nothing changes there until a reset Ade approves. The seed adds rows only to a new tenant: on an existing OFTZA its configuration step is skipped, and a back-dated `MONTHLY` couldn't replace the version in force.

### Scope batch 5 (18 November 2026, branch `scope-batch-5`)
The last fix batch of the read sweep: its PARTIAL rows on paging and the portal claim list (`docs/reviews/read-scope-sweep.md`). Contract note: "Scope batch 5" in `docs/api/contract-changes.md`.
- **Renewals** (`GeneralStores.Renewals`): the scoped policies are a subquery in the renewals query, in place of one `Policy()` load per row after `Take`.
- **Payment runs** (`HealthLists.Runs`): the query keeps only runs with no remittances yet or with a claim whose membership is in scope; `RunVisibility` then trims each run's remittances as before.
- **Legal holds** (`RecordHoldHandlers.Handle(ListRecordHolds)`): a hold's record is in another module, so the handler reads on in batches until the page is full or nothing is left, as the payments list does. No scope is stored on the hold.
- **Portal claims** (`HealthPortalQueries.ProviderClaims`, `MemberClaims`): the scoped memberships are a subquery, as the single read applies.
- **Refunds** (`BillingRepository`): an account with no schedules is whole only for an unrestricted caller (`count > 0`).
- **Tests** (`ReadScopeTests`): `APageIsFull_WhateverIsOutOfScopeBeforeIt_RenewalsAndLegalHolds_F103`, `APageOfPaymentRuns_IsFull_WhateverIsOutOfScopeBeforeIt_F103`, `AMembersPortalClaims_AreListedOnlyWithinTheScopeTheSingleReadApplies_F103`, `ARefundOnAnAccountWithNoSchedules_IsAnUnrestrictedReadersOnly_OQ081`. Each fails with its fix undone (checked for renewals, holds, runs, member claims and refunds). The provider side of the portal list uses the same subquery and has no test of its own.
### Scope batch 4 (18 November 2026, branch `scope-batch-4`)
The fourth fix batch: Workflow processes and tasks, and documents attached to a record (OQ-169).
- **V0140:** `is_scoped`, `product_code`, `org_node_code` (and `org_node_applies` for processes) on `workflow.process_instance` and `document.document`. The backfill reads each record's scope as its module's source does.
- **A process gets its scope when `IProcessRepository.Add` saves it:**
  - a type declared unscoped (`ApprovalKinds.Unscoped`: party, data subject request, party retention, fraud indicator, parked delivery) stays tenant-wide;
  - else its module's `IApprovalScopeSource`. New ones: General quotes and retention cases, Health memberships and care activities (`MembershipRecordScopes`), Workflow service cases;
  - else the approval about the same record;
  - else none: an unrestricted reader's only.
  - `ProcessQueries` filters lists, gets and open tasks in the query, as approvals are.
- **A document gets its scope when stored or generated (`RecordHoldTargets.ScopeDocument`):**
  - from the legal-hold targets of its record type: one scope copied;
  - a record not found, or with several scopes, fails closed;
  - a party (no scope), or a type no target resolves, leaves it unattached.
  - `DocumentRepository.Get` refuses or hides it outside the scope, and `ForBusinessRef` filters in the query.
- **Interpretations for Ade:**
  - A process about a case with no product or node is an unrestricted reader's only, as its redress approval is.
  - Processes about aggregation zones, Health programme reviews, provider measures and recovery reviews are too, following OQ-172 and scope batch 1.
- **Tests:** `ReadScopeTests`: a process started through Health's event takes the membership's scope, and the list, get and tasks follow it; documents follow their record, and an unattached one doesn't. Both fail without their fix (checked by undoing each).
### Contract batch (18 November 2026, branch `contract-batch`)
Ade's items of 18 November, before scope batches 4 and 5 and the seed batch.
- **The front end's error list:** `docs/api/error-codes.json`, generated with the error catalogue (`ErrorCodeSeed.GenerateJson`, checked by `ErrorCodeCatalogueTests`).
- **Refusal titles:** `ProblemLocalisation.Apply` sets a worded code's English title to its catalogue meaning before the message catalogue's localised title, which still wins.
- **Portal documents:** `PortalDocumentView.ScanStatus`.
- **The stand-in model:**
  - `DevelopmentAiModelProvider` (Assist.Infrastructure) is chosen by `Assist:Provider=development`. Its constructor and `DevelopmentModelGuard` (a hosted service, registered before the jobs) refuse it outside Development and Testing.
  - The chart's `assist.provider` takes none or claude only.
  - The published test document is `docs/frontend/ai1-sample-product.pdf`, made by `SampleDocuments.Ai1Sample`. `PublishedSampleTests` checks the committed copy, and regenerates it with `INSYS_UPDATE_SNAPSHOTS=1`.

### Scope batch 3 (18 November 2026, branch `scope-batch-3`, on batch 2)
The third fix batch: Party's policy export. `IPolicyExportRows.All` takes the caller's `AccessScope`. `PolicyExportReader.Read` adds the scope's filter to each module's SQL (any grant whose product and node sets allow the row, as `ScopeFilter.InScope`); General, Life and Health pass `product_code` and `org_node_code`. The request counts in the requester's scope, the download lists in the reader's. **Decided (Ade, 18 November): as built.** The rows follow whoever downloads, never more than their scope. **Who can download:** anyone holding `PARTY_BULK_JOBS` (`party_bulk_job.read`) once a second person has approved the export. It isn't only the requester; the handler doesn't check who asked. Test: `ReadScopeTests`, failing without the fix (checked by passing an unrestricted scope).

### Scope batch 2 (18 November 2026, branch `scope-batch-2`)
The second of the five fix batches (the read sweep's order, Ade): the Health accumulators. `GetAccumulatorsHandler` loads the membership through the scoped `IHealthRepository.Membership` first, so a membership outside the caller's scope isn't found (404). Test: `ReadScopeTests`, which fails without the fix (checked by undoing it).

### Wave 7: the plan to 7.8, in the D-8 order (15 November 2026)
**What it's based on:** 7.1 was estimated at 8 to 10 days and took about half a day (its commits run from 09:54 to 12:23 on 2 November, review fixes included); 7.2 (6 to 8 days) was built in one session on 15 November. One engineer here is an AI session, so build time is far below the estimates, and the dates are set by CI (about 45 minutes a run), review and what waits on Ade. Building is planned at about a quarter of the estimate, rounded up, which is cautious against two batches. **Dates are working days and assume each item marked "Ade" arrives by the date shown; a late one moves that batch, not the build of the others.**

| Batch | What | Build | Waits on Ade |
|---|---|---|---|
| 7.2 | AI-2 refusals and premiums | 15 to 16 Nov: review, fixes, CI, report | Merge approval |
| 7.3 | Claude adapter, prompt templates as configuration, the AI-2 evaluation set (about 100 refusals and calculations from the sample tenant), its run, cost per explanation (D-5) | 17 to 19 Nov | **By 17 Nov:** the API account, its key in GitHub (set by Ade), and the region (D-1, AIA-050). A domain expert's review of the evaluation's results (SRS §5.3), about half a day. The provider contract's zero retention and no training (AIA-052) before any tenant switches AI-2 on, not before the evaluation on sample data |
| 7.6 | AI-3 packs, versions, overlay approval (§5.3), questionnaire, deterministic assembly, loading a pack | 20 to 25 Nov | The first overlay's two keys (D-10): the partner's review and the compliance lead's approval, for 3A to be usable. Not needed to build |
| 7.7 | `SETUP` change set, completeness check, activation guard (option C) | 26 Nov to 1 Dec | None |
| 7.4 | Scanner port and quarantine (ClamAV as a separate service), AI-1 extraction and drafts, the job acting for the user | 2 to 8 Dec | **D-16 by 2 Dec.** The security review of the job acting for the user before it merges (addendum §4) |
| 7.5 | AI-1 review, provenance, simulation cases, evaluation | 9 to 11 Dec | **D-7's documents by 9 Dec:** one line's real product documents, with the insurer's written permission |
| 7.8 | AI-3 tailoring and document mapping, evaluation | 14 to 16 Dec | D-16; sample tenant documents for AI-3's evaluation |

If every item arrives on time, the build ends about 16 December; each phase's exit still needs its evaluation targets met (addendum §6).

### Open items
- **The front end's BR-144, BR-145 and BR-140 (14 November): open questions OQ-163 to OQ-165** in `docs/decisions/open-questions.md`. OQ-163: a partly paid Life claim recovers nothing until fully paid (BR-144, accepted as specified; Ade to decide whether each payment should recover). OQ-164: a rated premium is annual in General and per instalment in Life; for Ade and the actuary, with a proposal to state the basis on the rating result and test case. OQ-165: the proposed `IPortalRoles` contract so readiness names portal roles no portal channel covers (BR-140); not built.
- **Keycloak's email (Ade, 6 November): Google Workspace's SMTP relay, not Mailpit.** `preview.sh` sets the realm's SMTP server on every `provision`, `reset` and `upgrade` (`deploy/keycloak/realm-smtp.sh`: smtp-relay.gmail.com, 587, STARTTLS, from insys-preview@openfactorgroup.com, no authentication; the relay admits the server's address), checks that the server reaches port 587, and reports the parked unsent invitations. A test email: `deploy/keycloak/smtp-test.sh` on the server, with the address typed at a silent prompt, put on Keycloak's admin account only for the test and removed straight after.
- **No identity email to reserved test addresses (Ade, 6 November).** `ReservedEmailAddresses` (.test, .example, .invalid, example.com, example.net, example.org): the invitation handler records `NOT_SENT_RESERVED_ADDRESS` in the audit and sends nothing, so the seed's generic accounts never get mail and nothing parks. The 23 parked `KEYCLOAK_EMAIL_UNSENT` invitations from the Preview's seed resolve unsent on their next retry once the new API runs. Tests: `ReservedEmailAddressesTests`, and `KeycloakProvisioningTests.AnInvitationToAReservedTestAddress_IsNotSent_AndNothingParks_WhileAnotherAddressIsSentAsBefore`.
- **Before any external demo: a "real" marker for Keycloak's sign-in email (Ade, 6 November).** The Preview's Keycloak sends real invitations (there is no self-service password reset: the realm's `resetPasswordAllowed` is false) through Google Workspace's SMTP relay, as insys-preview@openfactorgroup.com, while INSYS's own email sender stays a stand-in. The banner and `GET /platform/environment` stay as they are for now, and the quick-start says it. Needed: the environment answer marks Keycloak's sign-in email as real, a contract change, so the console's banner can say so. A back-end request for the front end: the console to show it once the answer carries it.
- **A product manager can't approve a publication:** for the business review. `PRODUCT_MANAGER` names `PRODUCT_PUBLICATION` but holds no approve function, so in the seed an administrator approves it, with `TENANT_ADMIN` holding the authority. No extra grant was made; the Preview's quick-start says so.
- **An underwriter couldn't quote or issue alone:** fixed by role templates A (signed 3 November). The underwriters now rate, evaluate and acknowledge, and the seed has them do it; the rules' inputs still come from the caller (row 33).
- **No tenant role maintained Life, General or Health configuration once live:** fixed by role templates B (signed 3 November): `LINE_CONFIGURATION_MANAGER`. The seed's line configuration managers make and approve the scheme's contribution rule after activation.
- **A static data approval could be decided on the generic route without the table's function:** fixed (Ade, 14 November; branch `workflow-static-data-approval-function`). `POST /workflow/approvals/{id}/approve` and `/reject` now also need the table's own `SD_{module}_APPROVE` function for a `STATIC_DATA_CHANGE` approval, as the table's route does (403 `FUNCTION_NOT_GRANTED`), and `decidable=true` leaves it out for a checker without it. Static Data names the function through `IApprovalFunctionSource` (Workflow.Contracts), found from the change request's table when the approval is decided, so approvals raised before the fix are held to it too; no migration. The `GOVERNANCE_SETTING` approval (Tier 2 off) names no table and is unchanged. Tests: `StaticDataApprovalFunctionTests`.
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
| `preview-kubeconfig` | Merged (d1e23fc), through GitHub's API during a git outage | `helm` given k3s's kubeconfig: the fix for the second provision's failure |
| `preview-migrate-wait` | Merged (fe5f841) | The migration Job labelled for the Preview's wait: the fix for the third provision's failure |
| `preview-realm-local` | Merged (34fe073) | `realm_name`'s local renamed: the fix for the fourth provision's failure |
| `preview-pkill` | Merged (07ed2c9) | The anchored `pkill`, and the file written after OpenBao too: the fix for the fifth provision's failure |
| `preview-internal-oidc` | Merged (7cb08d0), by the cloud session on Ade's go-ahead: CI 41838136750 and smoke 41838142044 green on that commit | The API reads Keycloak in the cluster (smoke rehearses it), `check_tls` prints Traefik's ACME log, and a failed seed prints the API's: the fix for the sixth run's 401 |
| `preview-acme-tls` | Merged (a119091) | `tlsChallenge: true` and its check; the smoke workflow frees disk first |
| `preview-console-repin` | Merged (d0460bf) | The console at the front end's 68b4659, with the Preview's banner |
| `preview-seed-forwards` | Merged (e0ee1b4, with main merged in): CI 41932331440 and smoke 41932339598 green on that commit | The seed waits for its forwards, each Keycloak call names itself, secrets off curl's command line: the fix for `rotate-passwords` run 41929744851 |
| `preview-seed-pending` | Merged (b6a3d91) | Each checker step waits for its pending item; the CLI settles until /health shows no pending outbox event; the job may run 240 minutes |
| `preview-acme-production` | Merged (38765b4) | The switch to production certificates clears Traefik's staging ones; `check_tls` requires the CA asked for |
| `preview-keycloak-provisioning` | Merged (be4484e) | The API makes Keycloak accounts inside the cluster, with the client secret copied on the server; the smoke test makes a user through the API |
| `preview-curl-retry` | Merged (5e408d9) | The run's downloads retry |
| `preview-seed-version-settle` | Merged (18cd541) | A write refused with 412 is made again on the fresh version, after a settle |
| `preview-seaweedfs-keys` | Merged (e4e79ae) | SeaweedFS reads its S3 keys from the Secret; the smoke test makes the Secret outside the chart; a 500's log line names its cause and trace |
| `preview-storage-check` | Merged (53f9bb6) | `storage_check`; register row 37; `docs/dependencies/container-images.md` |
| `preview-signin-names` | Merged (795cf67) | Each account's sign-in name in the quick-start and the encrypted file |
| `preview-restore` | Merged (6344070, through 94ef8cc) | The `backup` and `restore` actions for the restore rehearsal |
| `preview-keycloak-smtp` | Merged (4866bb7) | Keycloak's mail through Google Workspace's SMTP relay; nothing to reserved test addresses |
| `preview-smtp-test-ship` | Merged (d488b32) | `smtp-test.sh` shipped to the server with the chart |
| `product-capability-inventory`, `docs-inventory-fixes` | Merged (298ba30, 20dafe7) | The capability inventory, for the product guide |
| `preview-seal-backup` | Pushed 14 November; merges on green CI and smoke on its exact head | The backup's dump listing fixed, `check` reports OpenBao's seal and closes SSH, the file kept 90 days with a warning when it has no key, and this handover |
| `preview-seal-guard` | Pushed 14 November; merges on green CI and smoke on its exact head (a fix after a failed approved run: Ade said apply it) | `diagnose`, `os-update`, security updates kept but no automatic reboots or restarts on the server, check reporting pending restarts, and the nightly backup failing and emailing on a seal |
| Next | **Wave 7, batch 7.2 (from 15 November).** On the Preview only the key swap (`rotate-ssh-key`, when Ade approves run 43639005340); the `reset` and the restore rehearsal are postponed (Ade, 15 November; register row 38) | |

### The cloud session (4 November)
The Mac session stopped; a Claude Code cloud session took over at about 13:40 UTC, while Ade travels. Ade approves every `preview` run himself.
- **Merged, by fast-forward, each green on its exact head:** `7cb08d0` (`preview-internal-oidc`: CI 41838136750, smoke 41838142044), `a119091` (`preview-acme-tls`: CI 41847613568, smoke 41847618055) and `d0460bf` (`preview-console-repin`: CI 41856090281, smoke 41856094571). `preview-acme-tls`'s first head, c2fc967, failed CI 41840659740 on the source-header check only (the new script's header was a line short).
- **`upgrade` run 41855917495 (a119091, `acme: staging`): green, 22:56 UTC.** Chart revision 2, `insys-migrate-2` completed, the realm already named, and **Let's Encrypt staging certificates on both hosts** (`insys…`: "(STAGING) Ersatz Emmer YR2"; `id.insys…`: "(STAGING) Dastardly Durum YR1"). The `tlsChallenge` fix works.
- **`rotate-passwords` run 41929744851 (a119091): failed after 34 s, 23:05 UTC,** with curl's exit 52 (empty reply) at `clients()`'s first call, the admin token through the tunnel to 18081: before the seed, so no account was made, no password changed and OpenBao wasn't touched. What differs from `reset` and `provision`, where the seed got past Keycloak: there `openbao()` runs just before the seed and ends with `kubectl rollout status` for the API and Keycloak; `rotate-passwords` goes straight to the seed, whose forwards had a fixed `sleep 3` and their errors in `/dev/null`. Why nothing was listening at 23:05 wasn't seen: nothing here reaches the server without a run, and this session's network policy blocks the Preview's hosts. **Fixed on `preview-seed-forwards`:** `forwards()` waits for both rollouts, then for `/health/ready` and `/realms/insys` to answer 200 on the server and through the runner's tunnel, and prints the pods and the forwards' own output if not; each Keycloak call in `clients()` names itself with curl's own exit code; and the admin password and token are off curl's command line (the item "Not fixed yet" above). Rehearsed with stand-ins (slow start, 503 then 200, never starting, an empty reply on 18081, a stand-in Keycloak, curl's arguments watched).
- **The console's digest:** `sha256:078c7f59…`, the front end's main at 68b4659 (its run 41825364425); the image's chart contract is unchanged since bea6427's. **The front end must name it in its `docs/deployment.md`,** or its image cleanup (newest 10 plus the named digests) can delete it.
- **Next:** `rotate-passwords` run 41937659695 (e0ee1b4, confirm `rotate-passwords`, staging), dispatched at 23:57 UTC, waits for Ade; once it succeeds, one `upgrade` for the console, waiting for Ade too. Then production certificates when Ade agrees (`upgrade` with `acme: production`; browsers refuse the staging ones on `.app`), the restore rehearsal and the journey walk-through. No `reset`: it would replace the OpenBao whose keys are in run 41835262697's file.

- **5 November, the accounts: done.** After the runs below, `rotate-passwords` run 42104013738 (53f9bb6) succeeded at 22:20 UTC: storage checked, "OFTZA … with its 51 accounts", "Encrypted for Ade". Ade signed in as `oftza.admin01`. Each failure on the way, its cause and its fix:
  - Run 41983797927: the API's Keycloak provisioning had no address or client secret (chart), so the first administrators' accounts parked `KEYCLOAK_UNAVAILABLE`. Fixed in be4484e; register row 36.
  - Runs 42042549787 (first attempt) and 42053428889: a GitHub release download and `api.ipify.org` failed once each (curl 22). Retries in 5e408d9.
  - Run 42057678256: a 412 on a party's contact point, as `IdentityVerificationPolicy` changed the party between the seed's read and its write. 18cd541 settles before reading a version and retries a 412 on the fresh one.
  - Run 42080719155: the first document upload, 500. SeaweedFS's S3 keys came from empty values, the Secret being made on the server. Fixed in e4e79ae, with `storage_check` in 53f9bb6; register row 37.
  - Production certificates on both hosts since `upgrade` 42042549787 (issuer CN YR1).
- **Invitations (Ade, 5 November): an in-cluster Mailpit** as Keycloak's SMTP, its UI behind Traefik basic auth with the credentials only in Ade's encrypted file; built now the accounts work. 23 `KEYCLOAK_EMAIL_UNSENT` deliveries are parked until then.
- **The restore rehearsal:** `backup` and `restore` actions on `preview-restore`, rehearsed locally: `pg_restore --clean` fails on INSYS's partitions (240 errors), so the restore drops and recreates each database (`--create`), which kept every grant, policy and row; the KMS check decrypts a stored data key with the API's token (OpenBao 2.6.5). Steps in `docs/preview/restore.md`.
- **Seed rehearsals (local, the real migrations and API):** a fresh seed at the Preview's 2 s relay poll took 96 minutes with development tokens and about 122 with Keycloak sign-in and the accounts; a seed stopped part-way through the parties' contact points resumed to completion with every contact point in place.

**The red CI of 4 November (fixed).** `DueDiligenceTests`' two BR-112 tests failed on main from 4 November, at any hour. The cause was in the tests, not the product. Their host's clock is fixed at 22:30 UTC on 1 November (2 November in Johannesburg), but they seeded config from the run's UTC date, less a day. From 4 November that date falls after the host's, so the screening partner, the gate and the LOW class weren't in force. They now seed from the host's own date. `TestClock` takes `INSYS_TEST_RUN_AT`, which runs the suite as if it started at that time. Before the fix, both tests failed at `2026-11-04T05:44Z` and passed at `2026-11-03T05:44Z` and `T20:50Z`. After it, they pass at all three and at `2027-03-01T23:30Z`. Any other test whose host has a fixed clock and seeds from `TestClock.Today` would fail the same way; a search for hosts built on a literal `FakeTimeProvider` date found only `AssignRoleBusinessDateTests` besides these, and it seeds nothing from `TestClock.Today`. Hosts given their clock some other way weren't searched.

Nigeria: appeal routes (V0126) are merged; the rest of row 24 is descoped and wasn't started.
