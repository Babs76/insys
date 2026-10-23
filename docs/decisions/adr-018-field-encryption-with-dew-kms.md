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
