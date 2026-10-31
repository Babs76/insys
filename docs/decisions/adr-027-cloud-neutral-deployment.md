# ADR-027: Cloud-neutral deployment

- **Status:** **Amendment DRAFT for Ade's sign-off, 31 October 2026.** Phase 1 was signed and is built. Nothing below is built until this is signed.
- **Supersedes:** ADR-016 (Huawei Cloud as the host), which becomes optional deployment notes. Amends ADR-018: envelope encryption stays, and DEW becomes one optional key adapter.
- **Requirements:** NFR-005, NFR-006, NFR-014; DBA-002; SRS §30.

## Decision
INSYS is not tied to any cloud provider. **It needs only Kubernetes and PostgreSQL 18 from whoever hosts it.** Everything else runs with INSYS or is any provider's standard service:

| Need | Default | Requirement |
|---|---|---|
| Containers | Plain Kubernetes | No provider-specific annotations or services |
| Database | PostgreSQL 18, managed or self-run | Row-level security, with no superuser or `BYPASSRLS` for the application (DBA-002) |
| Object storage | Any S3-compatible store, from a provider or self-run | The S3 adapter, already built |
| Keys | OpenBao Transit, run with INSYS | Protects and recovers data keys (envelope operations) |
| Identity | Keycloak (ADR-017), run with INSYS | |

**The reference deployment is plain Kubernetes, not Huawei.** No code outside the Cloud adapter project knows which cloud it is on, and the guard from phase 1 enforces that for SDKs.

**Cloud-specific adapters are optional.** Huawei DEW stays as built, as one option. AWS KMS, Azure Key Vault, Alibaba KMS and Azure Blob are built only when a customer requires that cloud.

**Stays out:** the outbox and in-process bus stay as they are, so no cloud's messaging is needed. One API instance stays until permission evictions are shared (ADR-006), so the Helm chart must not scale it.

## Phases
### Phase 1: built
Neutral settings (`Kms:*`, `ObjectStore:*`), the old names working one release with a warning, and the SDK guard. On main at 1339eae.

### Phase 2: before the first customer
- **The OpenBao Transit adapter** behind `IKeyManagementService`, tested against a real OpenBao server, not a stub. The provider keeps the signed name `vault`.
- **A cloud-neutral Helm chart:** INSYS with one API instance (ADR-006), no provider-specific annotations, and PostgreSQL, the object store, OpenBao and Keycloak either in-cluster or pointed at existing ones.
- **A CI job that is the proof of cloud neutrality:** it deploys the full stack (INSYS, PostgreSQL 18, an S3-compatible store, OpenBao, Keycloak) from that chart to a throwaway cluster (kind or k3d) and runs a smoke test.
- **A generic runbook,** including running OpenBao: high availability, backup and restore, and unsealing.

The smoke test proves these through the deployed API: migrations run on a non-owner role, sign-in through Keycloak, a protected field is written and read back through OpenBao, and a document is stored and read from the object store.

**What the job doesn't prove:** that any particular provider's Kubernetes, PostgreSQL or object store behaves the same, or anything about load or failover. A customer's environment gets the same smoke test on deployment.

### Phase 3: per customer, only if required
That cloud's adapter when a customer requires it, and re-wrapping a tenant's data keys when it moves between key services, which includes moving to or from OpenBao. See Appendix A.

## Phase 2 estimate
Working days for one engineer, with moderate confidence. It replaces the signed phase 2's chart and two runbooks, which were about 7 days.

| Work | Days |
|---|---|
| OpenBao Transit adapter, with its contract test against a real server | 3 |
| Cloud-neutral Helm chart, including the container images, which don't exist yet (the repository has no Dockerfile), and the migrations as a job | 6 to 7 |
| The CI job on kind or k3d: full stack, Keycloak realm set up, the smoke test | 4 to 6 |
| Generic runbook | 2 |
| OpenBao's operations: writing them up, and rehearsing backup, restore and unseal on the throwaway cluster | 2 to 3 |
| **Total** | **17 to 21, so 4 to 5 weeks with review** |

The CI job also lengthens CI. I expect it to run as its own job, on `main` and on deployment changes, rather than on every branch; I'll measure it before fixing that.

## For Ade's security lead: self-run key management versus a provider's HSM-backed service
The default moves custody of the master keys from a provider's service to the operator of INSYS. That is what removes the dependency, and it is also the main risk this amendment takes on.

| | Self-run OpenBao (default) | A provider's HSM-backed service |
|---|---|---|
| **Dependency** | None on a cloud | Tied to that provider; leaving means re-wrapping every data key |
| **Where the master key lives** | In software, in OpenBao's encrypted storage, unless unsealed through an HSM | In the provider's HSM, by its own attestation; never exported |
| **Who can reach it** | Our or the customer's Kubernetes and OpenBao administrators | The provider's controls, with its access logs |
| **Certification** | Whatever the operator's own controls and audit can show. A regulator may ask for more | The provider's published certifications, to be checked per service and region |
| **Availability** | Ours to run: several nodes, monitoring, on-call | The provider's SLA, usually with rate limits |
| **Loss** | **If OpenBao's data and its unseal or recovery keys are lost, every encrypted field is lost for good.** Backups and the unseal keys must be kept apart, and restores rehearsed | The provider's durability; deleting a key is still unrecoverable |
| **Cost** | No licence (MPL-2.0); staff time and skills | Per key and per call |

**The middle path:** OpenBao's documentation lists PKCS#11 and KMIP auto-unseal, so a customer with an HSM can hold the unsealing key in it. So OpenBao stays the key service while the root of trust is in hardware. From version 2.7 this comes as a plugin (`openbao-plugin-kms-pkcs11`) rather than in the standard binary. I've read that in the documents, not tried it. Unsealing through a cloud's KMS also works, but brings back a cloud dependency.

**Questions for the security lead:**
1. Is software custody with Shamir unsealing acceptable for a licensed insurer's identity and medical fields, or is an HSM root of trust required? *Recommendation: Shamir by default; an HSM wherever a customer or its regulator asks.*
2. Who holds the unseal keys, how many are needed, and where are they kept? The runbook needs the answer. *Recommendation: three of five, each with a different named person, none with Kubernetes administrator access.*
3. Does a customer's regulator accept self-run key management? No regulator has been asked yet.

## Questions for Ade
1. **Sign this amendment?** It replaces the signed phase 2.
2. **The provider name.** You signed `vault`, tested against OpenBao. OpenBao is now the default, so the setting will read `Kms:Provider=vault` on a stack with no Vault in it. *Recommendation: keep `vault`, since the same adapter serves both, and accept `openbao` as a second name for it (a few minutes' work).*
3. **The self-run object store.** MinIO is what our contract tests use today, but I haven't reviewed its current licence or terms for running it in production. *Recommendation: the chart takes any S3 endpoint, and an in-cluster store is for CI and small installs only; check the licence before naming one for production.*

## Appendix A: optional deployment notes
None of this is part of the decision. It is here for when a customer names a provider. Checked 31 October 2026 and not re-checked for this amendment.

### The providers
"Confirmed" means an official page says so. **Unconfirmed items must be checked in the provider's console before anyone relies on them.**

| Target | In-country region | PostgreSQL 18 | Key service | Object storage |
|---|---|---|---|---|
| **Huawei** | AF-Johannesburg, `af-south-1` (ADR-016) | Offered by RDS generally (18.6, August 2026). **Unconfirmed in `af-south-1`**; ADR-016's console check is still open | DEW KMS: `CreateDatakey`, decrypt (built) | OBS through its S3 API (built; real test exists) |
| **Alibaba through BCX's ALP Cloud** | Johannesburg: Midrand and Teraco Isando, per press reports of October 2023. One region, two zones | Offered by Alibaba's public RDS since 12 December 2025. **Unconfirmed on ALP**: ALP is reported to be built on Apsara Stack, which is a separate product from the public cloud | Alibaba KMS: `GenerateDataKey`, `Decrypt`. Alibaba advises the `Advance*` calls for keys with rotation. **Unconfirmed on ALP** | OSS has an S3-compatible endpoint, with no chunked transfer encoding. **Unconfirmed on ALP** |
| **AWS** | South Africa has a region; `af-south-1`, which I understand to be Cape Town. **Name not confirmed by the page read.** No Johannesburg region found | RDS has 18 (18.6, 25 August 2026). **Unconfirmed in `af-south-1`**: `aws rds describe-orderable-db-instance-options --engine postgres --engine-version 18 --region af-south-1` settles it | AWS KMS: `GenerateDataKey`, `Decrypt` | S3 |
| **Azure** | South Africa North (flexible server region since 2021) | Flexible server has 18 (18.6). **Unconfirmed in South Africa North** | Key Vault: wrap and unwrap only; it does not generate data keys. Symmetric keys are in preview on Premium, so the adapter uses RSA-OAEP-256 and generates data keys itself | Azure Blob |
| **Google** | `africa-south1`, Johannesburg | Cloud SQL has 18 (GA 20 November 2025) and the region has Cloud SQL. **The two together are inferred, not stated** | Cloud KMS. **Not read in official documents, and not in the proposed adapter list: see question 1** | Cloud Storage's XML API works with S3 tools using HMAC keys; an organisation policy can block HMAC keys |
| **On-premises** | The customer's site | PostgreSQL 18 from the community project, self-run | Vault Transit or OpenBao Transit: `datakey`, decrypt. See question 2 | Any S3-compatible store, tested per customer |

### Questions for BCX, if a customer chooses Alibaba through ALP Cloud
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

### Optional adapters, if a customer requires them
- **Key adapters** behind `IKeyManagementService`: AWS KMS, Azure Key Vault, Alibaba KMS. Huawei DEW is built.
- **Storage:** an Azure Blob store behind `IObjectStore`; a check that the S3 adapter works with OBS and OSS, including that `AWSSDK.S3` doesn't use chunked encoding on OSS, which its documents say OSS refuses.
- **Google:** Vault or OpenBao, as signed, until a customer needs Cloud KMS (about 2 days).
- **Vault itself:** its Business Source License isn't OSI-approved. Legal to confirm before we host it. OpenBao is MPL-2.0.
- **Estimates** (working days): AWS KMS 2, Azure Key Vault 3, Alibaba KMS 3 (more if ALP differs), Azure Blob 2, S3 adapter against OBS and OSS 2, moving a tenant's data keys between key services 3, and 1 for each provider's runbook.

### What contract tests can't prove for these adapters
**There is no emulator I can name for Azure Key Vault or Alibaba KMS.** LocalStack or similar can stand in for AWS KMS, Azurite for Blob, a real OpenBao dev server for Transit, and an S3-compatible server for the object store. For Azure Key Vault and Alibaba KMS, the suite would run against a stub of their HTTP API that I write, which proves our side only. Each adapter should therefore also have a by-hand real-cloud test, as `cloud-tests.yml` has for Huawei, run before that cloud's first deployment.

### Sources
- Huawei RDS: [kernel versions](https://support.huaweicloud.com/intl/en-us/kerneldesc-rds-pg/rds_02_0001.html), [what's new](https://support.huaweicloud.com/intl/en-us/wtsnew-rds-pg/index.html); and ADR-016's sources.
- BCX and ALP: [BCX](https://www.bcx.co.za/solutions/cloud/alibaba-cloud/); press, not official documents: [ITWeb](https://www.itweb.co.za/article/bcx-goes-live-with-alibaba-cloud-service-in-south-africa/5yONPvErzY37XWrb), [TechCentral](https://techcentral.co.za/alibaba-cloud-launched-south-africa-bcx/232951/).
- Alibaba: [RDS PostgreSQL lifecycle](https://www.alibabacloud.com/help/en/rds/apsaradb-rds-for-postgresql/lifecycles-of-major-engine-versions), [KMS envelope encryption](https://www.alibabacloud.com/help/en/kms/key-management-service/use-cases/use-envelope-encryption), [KMS instance API](https://www.alibabacloud.com/help/en/kms/key-management-service/developer-reference/api-overview), [S3 SDKs with OSS](https://www.alibabacloud.com/help/en/oss/developer-reference/use-amazon-s3-sdks-to-access-oss).
- AWS: [RDS PostgreSQL versions](https://docs.aws.amazon.com/AmazonRDS/latest/PostgreSQLReleaseNotes/postgresql-versions.html), [GenerateDataKey](https://docs.aws.amazon.com/kms/latest/APIReference/API_GenerateDataKey.html), [regions](https://aws.amazon.com/about-aws/global-infrastructure/regions_az/).
- Azure: [supported PostgreSQL versions](https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/concepts-supported-versions), [release notes](https://learn.microsoft.com/en-us/azure/postgresql/release-notes/release-notes), [Key Vault keys](https://learn.microsoft.com/en-us/azure/key-vault/keys/about-keys-details).
- Google: [Cloud SQL release notes](https://docs.cloud.google.com/sql/docs/postgres/release-notes), [locations](https://docs.cloud.google.com/sql/docs/postgres/locations), [Cloud Storage interoperability](https://docs.cloud.google.com/storage/docs/interoperability).
- Vault and OpenBao: [Vault Transit API](https://developer.hashicorp.com/vault/api-docs/secret/transit), [OpenBao Transit API](https://openbao.org/api-docs/secret/transit/), [HashiCorp BSL](https://www.hashicorp.com/en/bsl).
- OpenBao: [seal configuration](https://openbao.org/docs/configuration/seal/), [high availability](https://openbao.org/docs/concepts/ha/), [licence and repository](https://github.com/openbao/openbao).
