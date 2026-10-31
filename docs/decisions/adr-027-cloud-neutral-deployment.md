# ADR-027: Cloud-neutral deployment

- **Status:** **Signed in phases (Ade, 31 October 2026).**
  - **Phase 1, now:** neutral settings, with the old names working one release with a warning, and the SDK guard. Built.
  - **Phase 2, before the first customer:** the Helm chart (one API instance, ADR-006), a generic runbook and the Huawei runbook.
  - **Phase 3, per customer:** that cloud's adapter and key re-wrapping, when that cloud is chosen.
  - **Questions 1 to 3 accepted as recommended:** Google through Vault or OpenBao for now; the provider is named `vault`, tested against OpenBao, with legal to confirm before we host Vault; re-wrapping is in phase 3.
- **Supersedes:** ADR-016 (Huawei Cloud as the only host), which becomes the first reference deployment. Amends ADR-018: envelope encryption stays, DEW becomes one key adapter.
- **Requirements:** NFR-005, NFR-006, NFR-014; DBA-002; SRS §30.

## Decision
INSYS runs on any cloud, or on-premises, that provides:

| Need | Requirement |
|---|---|
| Containers | Kubernetes |
| Database | PostgreSQL 18, managed or self-run, with row-level security and no superuser or `BYPASSRLS` for the application (DBA-002) |
| Object storage | S3-compatible, or Azure Blob |
| Keys | A key service that can protect and recover a data key (envelope operations) |
| Identity | Keycloak (ADR-017), self-run on the same platform |

Huawei `af-south-1` is the first reference deployment. No code outside the Cloud adapter project knows which cloud it is on.

**Stays out:** the outbox and in-process bus stay as they are, so no cloud's messaging is needed. One API instance stays until permission evictions are shared (ADR-006), so the Helm chart must not scale it.

## The targets (checked 31 October 2026, official sources)
"Confirmed" means an official page says so. **Unconfirmed items must be checked in the provider's console before anyone relies on them.**

| Target | In-country region | PostgreSQL 18 | Key service | Object storage |
|---|---|---|---|---|
| **Huawei** | AF-Johannesburg, `af-south-1` (ADR-016) | Offered by RDS generally (18.6, August 2026). **Unconfirmed in `af-south-1`**; ADR-016's console check is still open | DEW KMS: `CreateDatakey`, decrypt (built) | OBS through its S3 API (built; real test exists) |
| **Alibaba through BCX's ALP Cloud** | Johannesburg: Midrand and Teraco Isando, per press reports of October 2023. One region, two zones | Offered by Alibaba's public RDS since 12 December 2025. **Unconfirmed on ALP**: ALP is reported to be built on Apsara Stack, which is a separate product from the public cloud | Alibaba KMS: `GenerateDataKey`, `Decrypt`. Alibaba advises the `Advance*` calls for keys with rotation. **Unconfirmed on ALP** | OSS has an S3-compatible endpoint, with no chunked transfer encoding. **Unconfirmed on ALP** |
| **AWS** | South Africa has a region; `af-south-1`, which I understand to be Cape Town. **Name not confirmed by the page read.** No Johannesburg region found | RDS has 18 (18.6, 25 August 2026). **Unconfirmed in `af-south-1`**: `aws rds describe-orderable-db-instance-options --engine postgres --engine-version 18 --region af-south-1` settles it | AWS KMS: `GenerateDataKey`, `Decrypt` | S3 |
| **Azure** | South Africa North (flexible server region since 2021) | Flexible server has 18 (18.6). **Unconfirmed in South Africa North** | Key Vault: wrap and unwrap only; it does not generate data keys. Symmetric keys are in preview on Premium, so the adapter uses RSA-OAEP-256 and generates data keys itself | Azure Blob |
| **Google** | `africa-south1`, Johannesburg | Cloud SQL has 18 (GA 20 November 2025) and the region has Cloud SQL. **The two together are inferred, not stated** | Cloud KMS. **Not read in official documents, and not in the proposed adapter list: see question 1** | Cloud Storage's XML API works with S3 tools using HMAC keys; an organisation policy can block HMAC keys |
| **On-premises** | The customer's site | PostgreSQL 18 from the community project, self-run | Vault Transit or OpenBao Transit: `datakey`, decrypt. See question 2 | Any S3-compatible store, tested per customer |

## Questions for BCX
1. Is ALP Cloud still built on Apsara Stack, and which release? How far does it lag the public Alibaba Cloud?
2. Can we buy ApsaraDB RDS for PostgreSQL 18 on ALP? If not, which version, and when? May we self-run 18 on ECS?
3. Does RDS on ALP give an account that can create roles and run our migrations, with row-level security and no `BYPASSRLS` needed? Which extensions are allowed (we use `btree_gist`)?
4. Is KMS offered? Does it have `GenerateDataKey` and `Decrypt`, or the `Advance*` calls? Is it the shared service or a dedicated instance, what are its rate limits, and are the keys HSM-backed?
5. Is OSS offered with the S3-compatible endpoint? Which signature versions, and can we test `AWSSDK.S3` against it?
6. Is managed Kubernetes (ACK) offered, and which versions?
7. Which regions or zones are in South Africa today? Has the Cape Town site or the disaster recovery site they announced been built? Does any data, metadata or support access leave the country?
8. Are the official Alibaba .NET SDK and API endpoints usable as they are, or does ALP have its own endpoints and credentials?
9. What certifications does ALP hold that a licensed insurer's regulator would ask for, and what is the SLA?
10. Is pricing in rand for all of the above, and is there a test account we can run our contract tests in?

## The proposed batch
- **Neutral settings:** `Kms:Provider` = `local|dew|aws|azure|alibaba|vault`; `ObjectStore:Provider` = `s3-compatible|azure-blob`, with `ObjectStore:*`. These replace `FieldProtection:Envelope:Kms`, `Huawei:Kms`, `Huawei:Obs` and `Documents:Storage:Provider`; the old names keep working for one release and log a warning.
- **Key adapters** behind `IKeyManagementService`: AWS KMS, Azure Key Vault, Alibaba KMS, Vault Transit.
- **Storage:** an Azure Blob store behind `IObjectStore`; a check that the S3 adapter works with OBS and OSS, including that `AWSSDK.S3` doesn't use chunked encoding on OSS, which its documents say OSS refuses.
- **Tests:** one contract suite every adapter passes; no real cloud in CI.
- **Guard:** only the Cloud adapter project references cloud SDKs (architecture test).
- **Helm chart** (one API instance, per ADR-006) and a **runbook per cloud**.

### What the contract suite can't prove
**There is no emulator I can name for Azure Key Vault or Alibaba KMS.** LocalStack or similar can stand in for AWS KMS, Azurite for Blob, a real OpenBao dev server for Transit, and an S3-compatible server for the object store. For Azure Key Vault and Alibaba KMS, the suite would run against a stub of their HTTP API that I write, which proves our side only. Each adapter should therefore also have a by-hand real-cloud test, as `cloud-tests.yml` has for Huawei, run before that cloud's first deployment.

### Estimate
Working days for one engineer, with moderate confidence, excluding each cloud's account set-up and BCX's answers.

| Work | Days |
|---|---|
| Neutral settings and the old names | 2 |
| AWS KMS adapter | 2 |
| Azure Key Vault adapter (generates data keys, RSA wrap, managed identity) | 3 |
| Alibaba KMS adapter (more if ALP differs) | 3 |
| Vault / OpenBao Transit adapter | 2 |
| Azure Blob store | 2 |
| S3 adapter against OBS and OSS | 2 |
| Contract suite, emulators and stubs | 5 |
| The guard | 0.5 |
| Moving a tenant's data keys between key services (question 3) | 3 |
| Helm chart | 5 |
| Six runbooks | 6 |
| **Total** | **35.5, so 7 to 9 weeks with review and real-cloud checks** |

## Questions for Ade
1. **Google has no key adapter in the brief's list.** *Recommendation: on Google, use the Vault or OpenBao adapter until a customer needs Cloud KMS, and add it then (about 2 days).*
2. **Vault's licence.** HashiCorp's Business Source License allows production use except offering the work to third parties in competition with its paid versions. It isn't OSI-approved, which is our rule for packages. We would only call its API, and OpenBao's Transit API matches for `datakey`. *Recommendation: name the provider `vault`, test against OpenBao, and let a customer run either; legal to confirm for any Vault we host ourselves.*
3. **Changing key service.** Each data key is stored under its master key's reference (`integration.field_data_key`), so a tenant moving clouds needs its data keys re-wrapped while both services are reachable. It is not in the brief. *Recommendation: include it; without it a customer can't leave a cloud without re-encrypting all its fields.*
4. **Does this come before go-live?** *Recommendation: settings and the guard first (3 days), since they stop more Huawei names spreading; the rest when a customer's cloud is known.*

## Sources
- Huawei RDS: [kernel versions](https://support.huaweicloud.com/intl/en-us/kerneldesc-rds-pg/rds_02_0001.html), [what's new](https://support.huaweicloud.com/intl/en-us/wtsnew-rds-pg/index.html); and ADR-016's sources.
- BCX and ALP: [BCX](https://www.bcx.co.za/solutions/cloud/alibaba-cloud/); press, not official documents: [ITWeb](https://www.itweb.co.za/article/bcx-goes-live-with-alibaba-cloud-service-in-south-africa/5yONPvErzY37XWrb), [TechCentral](https://techcentral.co.za/alibaba-cloud-launched-south-africa-bcx/232951/).
- Alibaba: [RDS PostgreSQL lifecycle](https://www.alibabacloud.com/help/en/rds/apsaradb-rds-for-postgresql/lifecycles-of-major-engine-versions), [KMS envelope encryption](https://www.alibabacloud.com/help/en/kms/key-management-service/use-cases/use-envelope-encryption), [KMS instance API](https://www.alibabacloud.com/help/en/kms/key-management-service/developer-reference/api-overview), [S3 SDKs with OSS](https://www.alibabacloud.com/help/en/oss/developer-reference/use-amazon-s3-sdks-to-access-oss).
- AWS: [RDS PostgreSQL versions](https://docs.aws.amazon.com/AmazonRDS/latest/PostgreSQLReleaseNotes/postgresql-versions.html), [GenerateDataKey](https://docs.aws.amazon.com/kms/latest/APIReference/API_GenerateDataKey.html), [regions](https://aws.amazon.com/about-aws/global-infrastructure/regions_az/).
- Azure: [supported PostgreSQL versions](https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/concepts-supported-versions), [release notes](https://learn.microsoft.com/en-us/azure/postgresql/release-notes/release-notes), [Key Vault keys](https://learn.microsoft.com/en-us/azure/key-vault/keys/about-keys-details).
- Google: [Cloud SQL release notes](https://docs.cloud.google.com/sql/docs/postgres/release-notes), [locations](https://docs.cloud.google.com/sql/docs/postgres/locations), [Cloud Storage interoperability](https://docs.cloud.google.com/storage/docs/interoperability).
- Vault and OpenBao: [Vault Transit API](https://developer.hashicorp.com/vault/api-docs/secret/transit), [OpenBao Transit API](https://openbao.org/api-docs/secret/transit/), [HashiCorp BSL](https://www.hashicorp.com/en/bsl).
