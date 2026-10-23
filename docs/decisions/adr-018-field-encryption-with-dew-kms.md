# ADR-018: Field encryption keys from Huawei Cloud DEW KMS (envelope encryption)

- **Status:** Accepted (Phase 8 brief, decisions table, 23 October 2026). DEW KMS is confirmed available in af-south-1 (ADR-016). Creating keys in the production account is still to confirm.
- **Date:** 23 October 2026
- **Requirements:** DBA-014 (encrypted `_enc` and keyed-hash `_hash` columns), PTY-002, ADR-013 (payee bank accounts); resolves the key-custody half of OQ-020

## Context
`AesGcmFieldProtector` encrypts the `_enc` columns with AES-256-GCM, and computes the `_hash` lookup columns with HMAC-SHA256. Its keys come from configuration (`FieldProtection:Keys`, `ActiveKeyId`, `HashKey`).

Each stored value records the ID of the key that encrypted it: version, key ID length, key ID, nonce, tag, ciphertext. Old keys stay configured so that old values can still be read.

Keycloak (ADR-017) is not a key manager. Its vault holds only its own configuration secrets.

## Decision
- **Envelope encryption.** A DEW KMS customer master key (CMK) in af-south-1 protects **data keys**. The data keys encrypt the fields; the CMK never leaves KMS.
- **Data key register.** Each data key is created with `CreateDatakeyWithoutPlaintext` under the CMK. Its **ciphertext** is stored with its key ID and creation date, in configuration or a platform table (decided in Workstream D). The plaintext of a data key never leaves memory.
- **Startup.** The host decrypts the register's data keys with `DecryptDatakey` and holds them in memory. `IFieldProtector` stays synchronous, so no field read calls KMS. If KMS is unreachable at startup, the host does not become ready (a `field-keys` readiness check).
- **The key version per value** is the existing key ID in the stored layout. No schema change is needed.
- **Rotation without rewriting every row:**
  1. Create a new data key and make it active (`ActiveKeyId`). New and changed values use it.
  2. Old values stay readable with their own data key.
  3. A background re-encryption job may rewrite old values in small batches, using the normal module role for each table. It is optional, and is run only when a key is to be retired.
  4. Rotating the CMK in KMS (`EnableKeyRotation`) does not change the data keys: KMS keeps the old CMK versions for decryption.
- **The lookup hash key** (`HashKey`) is a secret, kept in DEW CSMS and read at startup. It is **not rotated by this design**, because rotating it means recomputing every `_hash` column. That stays an open item under OQ-020.
- **Providers, behind `IFieldProtector`:**
  - `local`: the current configuration keys, for development, tests and CI. It is the default.
  - `dew-kms`: production.

  The provider is chosen by `FieldProtection:Provider`. Real-cloud tests run only when credentials are present (Workstream D).

## Consequences
- Production keeps no field encryption keys in configuration. It holds only the CMK ID, the data key ciphertexts and credentials for KMS (an IAM agency on ECS or CCE, not access keys).
- A lost CMK makes the data unreadable. Scheduled deletion of the CMK must be blocked by policy, and the KMS key policy belongs in the security runbook.
- Which .NET client to use, and its licence, are checked in Workstream D (CLAUDE.md, packages).

## As built (Phase 8 Workstream D, 23 October 2026)
- **The data key register** is the table `integration.field_data_key` (V0023), used through the API role: read and add only, ciphertext only.
- **Rotation:** `DataKeyRotationService` creates the first key at startup and a new one after `RotateAfter`. An advisory lock means instances rotating together add one key.
- **Caching:** `DataKeyRing` keeps decrypted keys for `DataKeyCacheDuration` and refreshes them in the background. A test shows 1,000 decryptions on a fresh instance make one KMS call (`EnvelopeFieldProtectorTests`).
- **The DEW adapter** is in `Insys.BuildingBlocks.Cloud`, using Huawei's .NET SDK (`HuaweiCloud.SDK.Kms` 3.1.213, Apache-2.0).
  - Retries follow Huawei's DEW best practice: back-off of 200 ms × 2ⁿ, at most 6 attempts, each wait capped at 5 s and jittered. They apply on 429 or the throttling message, 5xx, timeouts and lost connections.
  - The per-second limit is given in DEW's throttling error. Huawei's documents name no fixed figure, so the design assumes a low one.
  - The real-cloud test (`HuaweiCloudTests`) runs only with credentials.
- **The data key format** is unchanged. The `datakey_cipher_length` sent to DecryptDatakey is the plaintext's 32 bytes, to confirm against the real service when the cloud test first runs.
