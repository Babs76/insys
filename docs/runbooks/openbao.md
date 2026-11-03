# Running OpenBao (ADR-027)

OpenBao holds the key that wraps each field data key: identity and medical fields can't be read without it. INSYS uses only its Transit engine, through `Kms:Provider=openbao` (`docs/runbooks/deployment.md`, Field encryption keys). This runbook says how to run it for real data. **What the chart and the smoke test prove is marked as such; everything else is from OpenBao's documentation, not tried here.**

**Open, for the security lead (ADR-027):** who holds the unseal keys, how many are needed, and whether an HSM root of trust is required. Until that's answered, this runbook can't name the key holders. ADR-027 recommends three of five, each held by a different named person, none with Kubernetes administrator access.

## What INSYS needs from it
- **One Transit key** (`Kms:KeyName`, default `insys`) on one mount (`Kms:Mount`, default `transit`).
- **A token for the API allowed only two calls on that key:** `datakey/plaintext` and `decrypt`. Nothing else. `deploy/openbao/openbao.sh configure` makes exactly this policy (`insys-kms`) and token.
- **Never the root token in the API.** The root token is for setting OpenBao up, then kept with the unseal keys or revoked.

## The chart's OpenBao
- **`openbao.devMode: true`** (the default) runs in memory: a restart loses the key, and so every encrypted field. Only for a throwaway install.
- **`openbao.devMode: false`** keeps its data on a volume, starts sealed and is sealed again after every restart. **Proven by the smoke test** (`deploy/smoke/run.sh`): initialised, unsealed, configured, restarted, found sealed, unsealed again, and a protected field read back. It's a single node with file storage: fine for the INSYS Preview, not for a production insurer, which needs the setup below.

## For production: high availability
Not built in the chart, and not tried.
- **Three or five nodes with integrated storage (Raft),** each with its own volume, spread over failure zones. Raft keeps a majority, so three nodes survive one loss, five survive two.
- **TLS between nodes and to clients.** The chart's in-cluster OpenBao listens without TLS inside the cluster; a production one doesn't.
- **Point the chart at it:** `openbao.enabled: false` and `kms.address`.
- **Each node unseals separately** after a restart, unless auto-unseal is used (below).

## Unsealing
- **Shamir by default:** `bao operator init` splits the unseal key into shares; a threshold of them unseals. The chart's helper initialises with one share, for the Preview only.
- **Who holds the shares** is the open question above. Each holder keeps theirs encrypted, offline, never in the cluster or in Git.
- **After any restart OpenBao is sealed,** and every read or write of a protected field fails until it's unsealed. Plan for who can be reached, and how fast, at any hour.
- **To unseal without the share touching a command line or file on the server:** forward the port and send the share on stdin, as `deploy/openbao/openbao.sh unseal <release>` does.
- **Auto-unseal through an HSM** (PKCS#11 or KMIP) keeps the root of trust in hardware, so a restart needs no person. From OpenBao 2.7 it's a plugin (`openbao-plugin-kms-pkcs11`). Read in the documentation, not tried.

## Backup and restore
- **What to keep:** OpenBao's storage, which holds the Transit key, sealed. A backup is useless without the unseal shares, and the shares are useless without a backup: keep them apart, under different people.
- **Raft:** take a snapshot (`bao operator raft snapshot save`) on a schedule, kept encrypted and off the cluster. Restore with `bao operator raft snapshot restore` on a cluster of the same version, then unseal.
- **File storage (the chart's):** copy the volume while OpenBao is sealed or stopped, or accept a copy taken while running, as the Preview's nightly job does (`deploy/preview/backup.yaml`), since Transit's key changes only on rotation.
- **Rehearse a restore** before relying on it, and after every OpenBao upgrade: restore to a scratch cluster, unseal, and read a protected field through a test API.
- **The database and OpenBao must match:** a field data key in the database needs the Transit key version that wrapped it. Restoring an older OpenBao than the database loses the fields written since its key last rotated.

## The API's token
- **Made by `configure`, ten years long** (`ttl` 87600h), with only the `insys-kms` policy, and put in the chart's Secret as `kmsToken`.
- **To rotate it:** make a new token with the same policy, put it in the Secret, restart the API, then revoke the old one (`bao token revoke`). Kubernetes authentication (the API's service account instead of a token) isn't built (handover, open items).

## Rotating the Transit key
`bao write -f transit/keys/insys/rotate` adds a new key version; new data keys use it, old ones still decrypt. INSYS needs nothing else. Re-wrapping old data keys onto the new version isn't built.
