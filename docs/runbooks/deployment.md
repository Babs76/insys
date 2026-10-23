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

## Identity provider: Keycloak (ADR-017)

### Locally

1. Copy `deploy/keycloak/keycloak.env.example` to `deploy/keycloak/keycloak.env` (git ignores it) and set both passwords.
2. Run `docker compose --profile identity up -d keycloak`. This imports the realm `insys` from `deploy/keycloak/insys-realm.json`. The admin console is at http://localhost:8080/admin.
3. To sign in through Keycloak instead of the development token, set:
   - `Authentication:Authority=http://localhost:8080/realms/insys`;
   - `Authentication:Audience=insys-core-api`;
   - `Authentication:RequireHttpsMetadata=false` (local only);
   - an empty `Authentication:DevelopmentSigningKey`.
4. The realm's `insys-admin` user has a fixed `sub`, `0199f000-0000-7000-8000-0000000000a1`, and the operator tenant `0199f000-0000-7000-8000-000000000001`. Bootstrap the operator with `--bootstrap-operator 0199f000-0000-7000-8000-000000000001 --admin-subject 0199f000-0000-7000-8000-0000000000a1`, so that user is the first platform administrator.

### What the realm contains

- **Clients:**
  - `insys-core-api` is bearer-only: it is the audience of access tokens and signs nobody in.
  - `insys-web` is a public client: Authorization Code with PKCE (S256), redirect `http://localhost:5173/*`, web origin `http://localhost:5173`, no secret, no direct password grant. Add the deployed front end's URLs when it is hosted.
- **Client scopes:**
  - `insys-api` adds the `tenant_id` claim (user attribute mapper) and the `insys-core-api` audience.
  - `basic` adds `sub`.
  - `insys-web` gets only these two, so its access tokens carry no profile, email or roles. Permissions come from IAM on every request.
- **User profile:**
  - `tenant_id` is a declarative user profile attribute that only administrators can view or edit. It never appears in the account console, and a user's own account update cannot change it.
  - It must be a UUID.
- **Access tokens** last 5 minutes. Revoking access in IAM takes effect on the next request (IAM-005); disabling the user in Keycloak stops new tokens.

### How a tenant's users get their `tenant_id`

1. The tenant exists in INSYS: the platform operator registers and onboards it (see [tenants-and-access.md](tenants-and-access.md)).
2. A Keycloak administrator creates the user in realm `insys` and sets `tenant_id` to the tenant's ID, in the admin console or through the admin REST API.
   - A person who works for two tenants has two Keycloak users, so that each token names one tenant (TEN-003).
   - Users cannot set or change it themselves.
3. The tenant's administrator provisions the IAM user with that user's Keycloak `sub` as the external subject, and assigns roles (`POST /api/v1/identity/users`).
4. The first administrator of a new tenant has no IAM administrator to provision them. Two platform administrators provision them instead (OQ-067); see the tenants runbook. Step 2 still comes first: the request names the Keycloak user's `sub`.

### In production

Keycloak runs on CCE in af-south-1 (ADR-016), with the realm imported once and then managed in Keycloak. Also:

- an HTTPS issuer, so `RequireHttpsMetadata` stays true;
- MFA required for privileged roles (IAM-003);
- the bootstrap administrator's password from the secret store;
- `insys-web` redirect URIs limited to the production origins.

## Field encryption keys: DEW KMS (ADR-018)

- `FieldProtection:Provider` is `local` by default: keys from configuration, for development and CI.
- Production uses `envelope`, with `FieldProtection:Envelope:Kms=dew`, and needs:
  - `Huawei:Kms:Region` (`af-south-1`);
  - `ProjectId`;
  - `MasterKeyId`, the CMK's ID;
  - `AccessKey` and `SecretKey`, from the secret store.
- **The first data key** is created at startup and recorded in `integration.field_data_key`, which holds ciphertext only. Every encrypted value carries its data key's ID.
- **Rotation:** a new data key is created after `FieldProtection:Envelope:RotateAfter` (90 days). New values use the newest key, and older values stay readable with theirs, so no rows are rewritten.
- **KMS calls:**
  - Decrypted data keys are held in memory for `DataKeyCacheDuration` (1 hour) and refreshed in the background every `RotationCheckInterval` (1 hour). Normal reads and writes make no KMS call.
  - DEW is rate-limited per account. Throttled (429) and failed (5xx) calls are retried with exponential back-off from 200 ms, at most 6 attempts.
- **Never schedule the CMK for deletion:** the data would become unreadable. Block it with a key policy.
- **`HashKey` cannot be rotated in place** (OQ-020). Keep it in the secret store.

## Document storage: OBS (ADR-016)

- `Documents:Storage:Provider` is `filesystem` by default, for development.
- Production uses `object-store`, with:
  - `Huawei:Obs:ServiceUrl` (`https://obs.af-south-1.myhuaweicloud.com`);
  - `Bucket`;
  - `AccessKey` and `SecretKey`, from the secret store.
- Objects are kept under `tenant/<tenant id>/documents/<document id>`, and a key outside the calling tenant's prefix is refused.
- Enable bucket versioning and server-side encryption in OBS.

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
