# Deployment and migrations

## What gets deployed

- **The API host** (`src/Host/Insys.Api`): one process that runs every module, the outbox relays and the background jobs.
- **The migration runner** (`tools/Insys.DbMigrator`): it applies `db/migrations`, applies the module grants and, once per environment, bootstraps the platform operator.

Both need PostgreSQL 18.

## Database roles

- The migrations run as the **owner role**, the only role that owns tables.
- The API connects as one **login role per module**, plus `insys_api`. The full list is in `db/roles/module_roles.csv` (ADR-001).
- No module role is a superuser or has BYPASSRLS (DBA-002), so row-level security always applies.
- `db/roles/create_login_roles.sql` is for local development, tests and CI only.
- **In production, create the login roles and their credentials outside this repository**, with the names in `module_roles.csv`. Then run the runner with `--roles db/roles` to apply `module_grants.sql`, and do not set `INSYS_ROLE_PASSWORD`.

## First installation

1. Create the database and the owner role. Create the module login roles (see above).
2. Apply the migrations and the grants, and bootstrap the operator. Pass the connection in the environment, not on the command line:
   ```bash
   INSYS_MIGRATION_CONNECTION="<owner connection>" dotnet run --project tools/Insys.DbMigrator -- --roles db/roles --bootstrap-operator <operator tenant id> --admin-subject <IdP subject of the first administrator>
   ```
   The bootstrap (`db/bootstrap/operator.sql`, OQ-031) creates the operator tenant, its first administrator (status INVITED) and the PLATFORM_ADMIN role. It is the one change that bypasses maker-checker. Run it once per environment: each run makes a new administrator user ID, so a second run is not guaranteed to be a no-op.
3. Configure the host (README, Configuration):
   - `ConnectionStrings:Template`, with `{role}` standing for the module role;
   - `Authentication:Authority` and `Audience`;
   - `Platform:OperatorTenantId`, the same ID as in step 2;
   - `FieldProtection:Keys`, `ActiveKeyId` and `HashKey`, from the secret store (OQ-020);
   - `Integration:Secrets:<ref>` for webhook signing keys.

   Never use the values in `appsettings.Development.json` outside development.
4. Start the host. Check that `/health/ready` answers Healthy (see [monitoring.md](monitoring.md)).
5. **Through the API, provision a second platform administrator** (`POST /api/v1/identity/users` and then `assign-role`). Until one exists, no static data change can be approved, because the checker must not be the maker (SDM-003, SRS-002).

## Upgrades

1. Read the new migrations (`db/migrations/V####__*.sql`) and the release's changes to `docs/api/`.
2. Take a backup (see [backup-and-recovery.md](backup-and-recovery.md)).
3. Run the migration runner with `--roles db/roles`.
   - Each migration runs in its own transaction and is recorded in `public.schema_migration` with its SHA-256.
   - The runner **refuses to continue** if an applied file has changed, or if a new file has a lower version than one already applied. Migrations are forward-only (DBA-017). Never edit an applied migration; add a new one.
   - An advisory lock stops two runners from applying migrations at once.
   - Repeatable scripts (`R__*.sql`, such as the error code catalogue) run again only when their content changes.
4. Deploy the new host and check `/health/ready`.

There is no down migration. To undo a release, restore the backup taken in step 2, or deploy a fix forward with a new migration.

## One instance only

The permission cache is kept per process, so a role change made through one instance would not reach the others for up to `Identity:PermissionCache:Duration` (ADR-006, OQ-032).

- By default, `SingleInstanceGuard` **refuses to start a second API instance** on the same database.
- `Identity:PermissionCache:AllowMultipleInstances=true` lifts the guard. Use it only after a shared cache invalidation has been built, or after accepting up to 30 seconds of stale permissions.
- Plan upgrades as stop-then-start, not rolling.

## Background jobs

They run inside the host and can be switched off per instance:

| Job | Setting | Default |
|---|---|---|
| Outbox relay | `Outbox:RelayEnabled` | on |
| Partition maintenance | `Audit:Partitions:Enabled` | on, every 12 h |
| Webhooks | `Integration:Webhooks:JobEnabled` | on, every 30 s |
| Payments | `Payments:Jobs:Enabled` | on, every 15 min |
| Billing, General, Life, Health, Product Factory | `<Module>:Jobs:Enabled` | on, every 1 h |
