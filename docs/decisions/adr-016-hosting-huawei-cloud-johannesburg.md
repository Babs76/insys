# ADR-016: Hosting on Huawei Cloud, AF-Johannesburg (af-south-1)

- **Status:** Accepted (Phase 8 brief, decisions table, 23 October 2026). Two items are still to confirm in the console (below).
- **Date:** 23 October 2026
- **Requirements:** NFR-005, NFR-006, NFR-014; DBA-002 (no superuser roles); SRS §30 (hosting decisions)

## Decision
Production runs on Huawei Cloud in the **AF-Johannesburg** region (region ID `af-south-1`). Data stays in South Africa.

## What is available in the region (checked 23 October 2026)
Huawei's region pages list AF-Johannesburg but do not list services per region. The evidence below comes from Huawei's own endpoint documentation and from the region tables of Huawei's official Go SDK (`huaweicloud/huaweicloud-sdk-go-v3`, `services/<service>/region/region.go`). Each regional endpoint was also resolved over DNS.

| Need | Service | Evidence for af-south-1 | Status |
|---|---|---|---|
| Database | RDS for PostgreSQL | SDK: `rds.af-south-1.myhuaweicloud.com`; resolves | Service available. **PostgreSQL 18 in this region is not confirmed.** RDS for PostgreSQL added 18 for commercial use on 30 August 2026, and new features "will be successively launched in each region". 17 is on general sale (end of sale November 2030). |
| Key management | DEW KMS (and CSMS for secrets) | DEW endpoint table: SA-Johannesburg, `kms.af-south-1.myhuaweicloud.com`, HTTPS; SDK; resolves | Available. The API has the envelope operations ADR-018 needs: CreateDatakey, CreateDatakeyWithoutPlaintext, EncryptDatakey, DecryptDatakey, EnableKeyRotation. |
| Object storage | OBS | `obs.af-south-1.myhuaweicloud.com` resolves and answers HTTPS (403 to an anonymous request) | Available. The S3-compatible API and the client are decided in Workstream D. |
| Messaging | DMS for Kafka, DMS for RabbitMQ | SDK: `dms.af-south-1.myhuaweicloud.com` for both | Available. Not needed today: events go through each module's transactional outbox and an in-process bus (ADR-001, INT-005). |
| Notifications | SMN | SDK: `smn.af-south-1.myhuaweicloud.com` | Available. A candidate channel adapter for Workstream E. |
| Containers | CCE | SDK: `cce.af-south-1.myhuaweicloud.com` | Available. |

## Consequences
- **To confirm in the console before provisioning:**
  1. that RDS for PostgreSQL 18 can be bought in af-south-1;
  2. that DEW KMS keys can be created in this account in af-south-1.
- **If PostgreSQL 18 is not yet offered there,** the fallback is PostgreSQL 17 on RDS. That needs a check that the migrations use nothing that is new in 18; the code and the CI tests target 18 (OQ-021). The alternative is self-managed PostgreSQL 18 on ECS until RDS offers it. **Proposal:** wait for RDS 18 in the region rather than run two engine versions, and decide when provisioning starts.
- **Database roles:** RDS gives an administrator account, not a superuser. The migration runner's owner role and the module roles (`db/roles/module_roles.csv`) are created from that account. No role has BYPASSRLS (DBA-002). Row-level security does not depend on superuser features.
- **The single-instance guard stays (ADR-006).** One API instance, until permission evictions are shared. DMS or Redis could carry evictions later.
- **Out of scope for Phase 8:** backup, DR and load testing (ADR-020). The backup runbook's proposal (WAL archiving and off-region copies) maps to RDS automated backups with cross-region backup copies. Which second region to use is open, because Johannesburg is Huawei's only South African region.

## Sources
- DEW endpoints: https://support.huaweicloud.com/en-us/api-dew/dew_02_0052.html
- RDS for PostgreSQL, What's New (PostgreSQL 18, 30 August 2026): https://support.huaweicloud.com/intl/en-us/wtsnew-rds-pg/index.html
- RDS for PostgreSQL product lifecycle: https://support.huaweicloud.com/intl/en-us/bulletin-rds-pg/rds_bulletin_0007.html
- Regions: https://www.huaweicloud.com/intl/en-us/securecenter/data_protection/region_query.html
- SDK region tables: https://github.com/huaweicloud/huaweicloud-sdk-go-v3 (`services/{rds/v3,kms/v2,csms/v1,kafka/v2,rabbitmq/v2,smn/v2,cce/v3}/region/region.go`)
