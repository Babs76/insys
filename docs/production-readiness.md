# Production readiness

What must be in place before the first production go-live. Each item names the decision or work it depends on. The list is kept current as items are done: strike nothing out, mark it done with its date and where it was done.

## Assurance (ADR-020)

| Item | Detail |
|---|---|
| Load test | Against NFR-015, on the target RDS and CCE sizing in af-south-1. It confirms OQ-069's pool sizes, or changes them. |
| Penetration test | External, on the production-like environment. |
| Backup and DR test | A restore against the RPO and RTO targets (NFR-005, NFR-006); see `runbooks/backup-and-recovery.md`. |

## Hosting (ADR-016)

| Item | Detail |
|---|---|
| PostgreSQL 18 on RDS in af-south-1 | Confirm in the console. If 18 is not offered there yet, decide between waiting and the fallback. |
| Connection capacity (OQ-069) | Starting assumption: 3 instances, about 116 connections each, RDS 16 GB. Set `Maximum Pool Size` per module role. |
| More than one API instance | Share permission cache evictions between instances (ADR-006), then lift the single-instance guard. |

## Identity (ADR-017)

| Item | Detail |
|---|---|
| Production Keycloak | Its own PostgreSQL database; at least two nodes (HA) on CCE; TLS on the issuer, so `RequireHttpsMetadata` stays true; the optimised production start (`start`), never `start-dev`; the realm imported once and then managed in Keycloak. |
| MFA | Required for privileged roles (IAM-003). |
| `insys-web` redirect URIs | Limited to the production origins. |
| The first administrator of a new tenant | OQ-067 / GAP-019. |

## Keys and secrets (ADR-018)

| Item | Detail |
|---|---|
| IAM agency credentials | DEW KMS and OBS authenticate through the IAM agency of the ECS or CCE nodes, not an access key pair. The adapters take an access key pair today (`Huawei:Kms:*`, `Huawei:Obs:*`); add the agency (metadata) credential path. |
| CMK protection | A key policy blocks scheduled deletion of the master key: without it, the data would become unreadable. |
| `datakey_cipher_length` | Confirm against DEW when the real-cloud tests first run (`HuaweiCloudTests`). |
| Lookup hash key | Kept in DEW CSMS. It cannot be rotated in place (OQ-020). |
| DEW rate limit | Unconfirmed: Huawei's DEW "Before You Start" page (dew_02_0052), as summarised by a web tool, gives about 20 calls per second per account. The data key cache does not depend on the figure. |

## Database

| Item | Detail |
|---|---|
| Migrations are immutable | From the first production database on, applied migrations are never edited (ADR-021). |
| Development databases | Every one built before the header change must be recreated, including the front end's `insys-web-live`. |
