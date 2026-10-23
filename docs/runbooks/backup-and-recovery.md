# Backup and recovery

**This runbook is a proposal.** The SRS gives starting targets, not commitments:

- RPO of 15 minutes or less for core transactional data (NFR-005);
- RTO of 4 hours or less for a regional disaster (NFR-006);
- encrypted, monitored, access-controlled and restore-tested backups (NFR-014).

The hosting platform, and so the backup tooling, is not decided. Nothing in this repository performs backups.

## What must be backed up

| Data | Where | Notes |
|---|---|---|
| The database | PostgreSQL, every schema | All business data, outboxes, inboxes, audit and static data, in one database |
| Documents | The document store (`Documents:Storage:Root` in development; OQ-056) | Documents are referenced from `document.document` by storage key, so they must be restorable to the same point as the database |
| Keys and secrets | `FieldProtection:*`, `Integration:Secrets:*`, role passwords | Without the field protection keys, encrypted columns cannot be read. Back them up separately from the data, under different access. |

## Proposed approach

- **Continuous WAL archiving plus a daily base backup** (for example pgBackRest, or the managed service's point-in-time recovery). WAL shipped at least every 5 minutes meets the 15-minute RPO, with margin.
- **Encrypted at rest and in transit.** Only the restore role can read the backups (NFR-014).
- **Off-region copies** of the base backups and the WAL, for the regional scenario (NFR-006).
- **Retention:** at least as long as the audit retention policies (`audit.cfg_audit_retention_policies`) require, to be confirmed by Compliance.
- **Document store** versioned and replicated with the same RPO.

## Restore

1. Restore the database to the chosen point in time on a new server, running PostgreSQL 18.
2. Restore the document store to the same point.
3. Run the migration runner with `--roles db/roles`.
   - Against a restored copy, it should report every migration as already applied.
   - If it reports a changed checksum, stop: the restored database does not match this release.
4. Start the host with the same `FieldProtection` keys, and check `/health/ready`.
5. **After restoring to an earlier point:**
   - **Outbox events** written after the restore point are lost along with their records. Events before it that were not yet published are published again by the relay. Consumers deduplicate through their inbox, so nothing is applied twice.
   - **Payments:** anything a provider processed after the restore point is not in the database. Reconcile against the providers' reports and bank statements for the gap (see [money-operations.md](money-operations.md)), and record what is missing through the normal commands.
   - **Webhooks** that partners received after the restore point may be sent again. Partners deduplicate by `eventId`.
   - **Idempotency keys** after the restore point are lost. A client that repeats a call from that window creates the record again, which is correct because the first one is gone.
6. Look at the partition horizon (`/health`). The Audit job recreates partitions ahead if needed.

## Test it

Restore into a separate environment at least once a quarter (NFR-014: restore-tested):

- run the end-to-end suite against it;
- record how long the restore took, against the RTO.
