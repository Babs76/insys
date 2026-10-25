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
  - `broker-portal`, `provider-portal` and `member-portal` (external-user access §5, §6) are public clients set up like `insys-web`, with the same local redirect. Each environment replaces it with its portals' real URLs; none is set here.
    - **Broker and provider portals** sign in through the flow `portal-mfa-browser`: password, then a one-time code, always.
    - **The member portal** signs in through `member-browser`: password, then a one-time code when the member has set one up. Whether a member must use MFA is the tenant's setting in INSYS (`iam.cfg_authentication_and_session_policies`, `client_ids` and `mfa_required`), which INSYS checks from `amr`.
    - Each step of these flows names its `amr` value (`pwd`, `otp`, in the authenticator configs `amr-pwd` and `amr-otp`), and the `amr` mapper puts them in the token. INSYS reads MFA from `amr` (`otp`, `mfa`, `hwk`, `swk`) or an `acr` of 2 or above.
    - **For a tenant to use a portal,** its channel definitions name the client in `client_ids`, with the portal's audience (BROKER, PROVIDER or CUSTOMER), as the tenant onboarding does (onboarding readiness §5).
- **Client scopes:**
  - `insys-api` adds the `tenant_id` claim (user attribute mapper) and the `insys-core-api` audience.
  - `basic` adds `sub`, `auth_time` and `amr` (the authentication methods used).
  - `insys-web` gets only these two, so its access tokens carry no profile, email or roles. Permissions come from IAM on every request.
- **User profile:**
  - `tenant_id` is a declarative user profile attribute that only administrators can view or edit. It never appears in the account console, and a user's own account update cannot change it.
  - It must be a UUID.
- **Access tokens** last 5 minutes. Revoking access in IAM takes effect on the next request (IAM-005); disabling the user in Keycloak stops new tokens.

### Front-end addresses per environment

Each front-end app signs in through its own public client, and each environment registers that app's own address on it. The front end reads its addresses from its `config.json` (`appBaseUrl`, `postLogoutRedirectUri`, `keycloak.clientId`), so one build is promoted unchanged; the Keycloak client must match.

| App | Client | Valid redirect URIs | Valid post-logout redirect URIs | Web origins |
|---|---|---|---|---|
| Back office | `insys-web` | `<appBaseUrl>/*` | `<appBaseUrl>/*` | `<appBaseUrl>` |
| Broker portal | `broker-portal` | `<appBaseUrl>/*` | `<appBaseUrl>/*` | `<appBaseUrl>` |
| Member portal | `member-portal` | `<appBaseUrl>/*` | `<appBaseUrl>/*` | `<appBaseUrl>` |
| Provider portal | `provider-portal` | `<appBaseUrl>/*` | `<appBaseUrl>/*` | `<appBaseUrl>` |

- **Locally** the realm file registers `http://localhost:5173` for every client (redirect, post-logout and web origin), the front end's development server.
- **Test, demo and production:** each environment's `appBaseUrl` per app is set where it is hosted; this repository does not choose them. Use HTTPS, exact origins, and no wildcard host. The post-logout redirect must be listed, or Keycloak refuses to send the user back after sign-out.
- **Changing an address** (a new host name, or an app moved):
  1. In the realm `insys`, open the app's client and replace the old address in **Valid redirect URIs**, **Valid post logout redirect URIs** and **Web origins**. With the admin REST API: `PUT /admin/realms/insys/clients/{id}` with `redirectUris`, `webOrigins` and the attribute `post.logout.redirect.uris` (several addresses separated by `##`).
  2. Change the app's `config.json` in that environment to the same address.
  3. Sign in and out once on the new address. Remove the old address only when nothing uses it.
- The API needs no change: hosted apps are served from the API's own origin behind the gateway (ADR-016). Outside Development the API allows no cross-origin calls (`Cors:AllowedOrigins` is honoured only there), so an app on another origin cannot call it.

### How a tenant's users get their `tenant_id`

1. The tenant exists in INSYS: the platform operator registers and onboards it (see [tenants-and-access.md](tenants-and-access.md)).
2. A Keycloak administrator creates the user in realm `insys` and sets `tenant_id` to the tenant's ID, in the admin console or through the admin REST API.
   - A person who works for two tenants has two Keycloak users, so that each token names one tenant (TEN-003).
   - Users cannot set or change it themselves.
3. The tenant's administrator provisions the IAM user with that user's Keycloak `sub` as the external subject, and assigns roles (`POST /api/v1/identity/users`).
4. The first administrator of a new tenant has no IAM administrator to provision them. Two platform administrators provision them instead (OQ-067); see the tenants runbook. Step 2 still comes first: the request names the Keycloak user's `sub`.

### Users for tests and personas (front-end request 23)

The realm ships one user, `insys-admin`. Persona users per tenant (a maker, a checker, an administrator) come with the OFT-ZA seed (Workstream F, GAP-004), together with their IAM users and roles. Until then, a test creates its own users through the Keycloak admin REST API. For example, locally:

```bash
TOKEN=$(curl -s -d grant_type=password -d client_id=admin-cli -d username=admin -d "password=$KC_BOOTSTRAP_ADMIN_PASSWORD" \
  http://localhost:8080/realms/master/protocol/openid-connect/token | jq -r .access_token)
curl -si -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' http://localhost:8080/admin/realms/insys/users -d '{
  "username": "acme-maker", "enabled": true, "email": "acme-maker@example.test", "emailVerified": true, "firstName": "Acme", "lastName": "Maker",
  "attributes": { "tenant_id": ["<the tenant ID>"] },
  "credentials": [{ "type": "password", "value": "<a test password>", "temporary": false }] }'
```

The `Location` header ends with the new user's ID: the token's `sub`. Provision the IAM user with it as the external subject (`POST /api/v1/identity/users`), or, for a new tenant's first administrator, request one (`POST /api/v1/identity/tenant-administrators`). A user who works in two tenants needs two Keycloak users (TEN-003).

### In production

Keycloak runs on CCE in af-south-1 (ADR-016), with the realm imported once and then managed in Keycloak. Also:

- an HTTPS issuer, so `RequireHttpsMetadata` stays true;
- MFA required for privileged roles (IAM-003);
- the bootstrap administrator's password from the secret store;
- every front-end client's redirect, post-logout and web-origin addresses limited to the production addresses (see Front-end addresses per environment).

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

## Database connections (GAP-039, OQ-069)

Each API instance opens one connection pool per module database role, 24 in all (Communications added in Wave 1). Their maxima must fit the database's `max_connections`, less a reserve, shared between the instances.

| Pools (`DatabasePools`, OQ-069 option A) | Roles | Maximum each | Total |
|---|---|---|---|
| Hot: `insys_api`, `insys_iam`, `insys_payment`, `insys_billing`, `insys_rating`, `insys_health`, `insys_general`, `insys_integration` | 8 | 8 | 64 |
| Medium: `insys_party`, `insys_product`, `insys_claims`, `insys_life`, `insys_finance`, `insys_workflow`, `insys_document` | 7 | 4 | 28 |
| Low: `insys_sdm`, `insys_rules`, `insys_commission`, `insys_reinsurance`, `insys_reporting`, `insys_analytics`, `insys_tenant`, `insys_audit`, `insys_communication` | 9 | 3 | 27 |
| **Per instance** | 24 | | **119** |

**The rule:** 119 ≤ (`max_connections` − reserve) ÷ instances.

| Where | `max_connections` | Reserve | Instances | Share per instance | Fits |
|---|---|---|---|---|---|
| Local compose | 150 | 20 | 1 | 130 | Yes |
| Default PostgreSQL | 100 | 20 | 1 | 80 | **No**: the front end's GAP-039 |
| RDS 16 GB (OQ-069 starting assumption) | 768 | 20 | 3 | 249 | Yes |
| RDS 16 GB, during a rolling deployment | 768 | 20 | 4 | 187 | Yes |

- **The guard.** Set `Database:MaxConnections`, `Database:Reserve` (default 20) and `Database:Instances` (the most that run at once, a rolling deployment's extra one included). An instance whose pools do not fit refuses to start, with the arithmetic in the error.
- **Changing a pool.** `Database:Pools:<role>` overrides a pool. So does `Maximum Pool Size` in that role's connection string, which takes second place.
- **When pools are busy.** A request that waits longer than `Database:PoolWaitSeconds` (Npgsql's `Timeout`, 15 s by default) for a connection gets **503 `DATABASE_BUSY`** with `Retry-After: 2`, as does one that PostgreSQL refuses (too many connections). It never gets a 500, and the instance keeps answering.
- **Production values.** `MaxConnections` 768, `Reserve` 20, `Instances` 4 (three plus a rolling deployment), to be confirmed by load testing (ADR-020).

## Health alarms: Cloud Eye (GAP-034)

Readiness (`/health/ready`) says only whether an instance can serve requests: database, audit partitions, router. The load balancer and CCE use it to stop sending traffic, so it never alarms on its own. Outbox lag, and any other check that is not about serving requests, is on `/health` only. Since GAP-034 that is the only place a stuck event shows, so `/health` must be alarmed on.

### What to send to Cloud Eye
A probe reads `/health` on every API instance once a minute and reports custom metrics to Cloud Eye (namespace `INSYS.Health`, dimension `instance`):

| Metric | Value |
|---|---|
| `health_status` | 0 Healthy, 1 Degraded, 2 Unhealthy (the `status` of `/health`) |
| `outbox_lag_seconds` | `checks.outbox-lag.data.lagSeconds` |
| `outbox_pending` | `checks.outbox-lag.data.pending` |
| `parked_deliveries` | `checks.parked-deliveries.data.parked` (ADR-022) |
| `parked_escalated` | `checks.parked-deliveries.data.escalated` |

The probe runs as a CCE CronJob with `curl` and `jq`. It calls `/health` through the instance's pod address, not the load balancer, so that each instance is seen. It reports through the Cloud Eye custom metric API, authenticated by the node's IAM agency, never an access key pair (see `docs/production-readiness.md`).

### Alarm rules
Create the alarm rules on the custom metrics, and notify the operations SMN topic:

| Alarm | Rule | Severity |
|---|---|---|
| `/health` Unhealthy | `health_status` = 2 for 5 consecutive periods (5 minutes) | Critical |
| `/health` Degraded | `health_status` ≥ 1 for 10 periods in any hour | Major |
| Outbox lag | `outbox_lag_seconds` > 300 for 5 periods | Critical |
| Probe silent | `health_status` has no data for 5 periods | Major: the probe or the instance is down |
| Parked delivery | `parked_deliveries` > 0 for 1 period | Major: a tenant is missing configuration (ADR-022). Platform operations is alerted at once (OQ-072) |
| Parked delivery escalated | `parked_escalated` > 0 for 1 period | Critical: the second escalation has passed, so the platform operator acts with the tenant administrator (OQ-072) |
| Claim payment held | LTS keyword `ClaimPaymentReleaseHeld` | Major: an approved claim payment was not released (reserve or payee account); the claims role has a task (`money-operations.md`) |

Also alarm on the load balancer's backend health (`/health/ready`): unhealthy backends for more than 2 minutes, Critical.

### Parked deliveries: what the alarm says, and the log alarm
- **`/health`**, check `parked-deliveries`:
  - `description` names up to five kinds as `<tenant code> <code> <item> xN (<consumer>)`, for example `OFT-ZA BILLING_FREQUENCY_NOT_IN_FORCE MONTHLY x3 (billing.GeneralPolicyBilling.GeneralPolicyIssued)`;
  - `data.tenants` lists every tenant code with all of its kinds, marked `escalated level N` once escalated.

  The probe copies `description` into the alarm notification, so the SMN message names the tenant and the missing configuration.
- **Log alarm (Cloud Eye, LTS keyword alarm)** on the API's log group, so the first park alerts even between probe runs:

| Keyword | Level | Severity | Says |
|---|---|---|---|
| `ParkedDeliveryRaised` | Warning | Major | First park of a kind in a tenant: `tenant <code> <prerequisite code> <item> (<consumer>)` |
| `ParkedDeliveryEscalated` with `level 2` | Error | Critical | Second escalation: the tenant administrator has a task, and platform operations is to follow up |

  Neither names a party, a payload or an event's values. Clearing: publish the configuration (the parking job retries within seconds), retry from `POST /api/v1/integration/parked-deliveries/retry`, or discard with a reason. See `docs/runbooks/dead-letters-and-webhooks.md`.

When `/health` alarms, its `checks` say which check and why. For outbox lag, see `slowestOutbox` and follow `docs/runbooks/monitoring.md` (outbox lag) and `docs/runbooks/dead-letters-and-webhooks.md` (events that need tenant configuration).

The thresholds match `docs/runbooks/monitoring.md`. They are starting values: no requirement sets them (NFR-012 asks for alerts, not values).

## Daily jobs (GAP-042, ADR-025)

Each module's daily transitions run on a timer in every API instance. The work is idempotent: a record moves once, and a second instance (or a repeated run) finds nothing left to do. Each job works tenant by tenant, on **that tenant's business date** (its time zone, ADR-025), and takes everything due on or before it.

| Setting | Does | Default |
|---|---|---|
| `Life:Jobs:Enabled`, `Interval` | Issued policies come into force at inception; policies past grace with enough missed premiums lapse (LIF-005); lapsed policies past their reinstatement window terminate; group schemes start | on, 1 hour |
| `Health:Jobs:Enabled`, `Interval` | Memberships and schemes start on their start date; approved preauthorisations past their validity expire | on, 1 hour |
| `General:Jobs:Enabled`, `Interval` | Policies start and expire; quotes past their validity expire; renewal invitations and renewals fall due; the exposure snapshot is refreshed | on, 1 hour |
| `Billing:Jobs:Enabled`, `Interval` | Instalments due are invoiced (debit orders included); accounts in arrears are aged, which raises the arrears events Life's lapse depends on | on, 1 hour |
| `ProductFactory:Jobs:Enabled`, `Interval` | Approved product versions become effective on their date | on, 1 hour |
| `Payments:Jobs:Enabled`, `Interval` | Payments pending too long are checked for an unknown outcome (PAY-005) | on, 15 minutes |

- **When a change shows.** A transition due on a day happens at the first run after local midnight, so up to one `Interval` late: with the default, by 01:00 local time. A policy issued during the day with cover starting that day goes into force when it is issued; the job only covers what was issued earlier.
- **Intended production schedule:** `PT15M` (15 minutes) for Life, Health, General and Billing, so cover starts and debit orders fall due within a quarter of an hour of local midnight; 1 hour for Product Factory, and 15 minutes (its default) for Payments. This is a starting value; no requirement sets one.
- **Live tests** may set a few seconds (the front end's live runner uses 15 seconds). For a chosen date, use the test-only runner instead.
- **The test-only runner** (Development and Testing only, ADR-025): `POST /api/v1/testing/daily-jobs` `{ "asOf": "2026-12-01" }` [`daily_job.run`].
  - It runs every daily job for the caller's tenant as if its business date were `asOf`, and delivers the events they raise.
  - To test a missed premium leading to a lapse, run it for the due date, then for a date past the grace period.
  - Enable it with `Testing:DailyJobRunner:Enabled`. The host refuses to start with it in any other environment.

## Upgrades

1. Read the new migrations (`db/migrations/V####__*.sql`) and the release's changes to `docs/api/`.
2. Take a backup (see [backup-and-recovery.md](backup-and-recovery.md)).
3. Run the migration runner with `--roles db/roles`.
   - Each migration runs in its own transaction and is recorded in `public.schema_migration` with its SHA-256.
   - The runner **refuses to continue** if an applied file has changed, or if a new file has a lower version than one already applied. Migrations are forward-only (DBA-017). Never edit an applied migration; add a new one.
   - An advisory lock stops two runners from applying migrations at once.
   - Repeatable scripts (`R__*.sql`, such as the error code catalogue) run again only when their content changes.
4. Deploy the new host and check `/health/ready`.
5. Release tasks run by themselves after the host starts (below); check the log for failures.

### Release steps
- **Wave 1 batch 4 (Analytics by business date, ADR-025):** each tenant's Analytics projections are rebuilt once by business date. The host does it by itself after it starts (Wave 2 batch 1): for each active tenant it runs the release task `ANALYTICS_REBUILD_BY_BUSINESS_DATE` and records it in `analytics.release_task` with the rebuilt facts, in one transaction, so it never runs twice; a tenant that fails is logged and tried again at the next start. Before, an event between 00:00 and 02:00 Johannesburg time (22:00 to 24:00 UTC) counted on the day before. Claims incurred are dated by their movement date and do not change. Nothing to do by hand; `POST /api/v1/analytics/projections/rebuild` stays for a rebuild on demand.

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
