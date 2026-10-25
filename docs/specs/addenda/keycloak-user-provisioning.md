# Addendum: creating Keycloak users for INSYS's own realm

**Signed off 24 October 2026** (Ade). **Built in Wave 0b batch 6;** the points below were settled when it was built (batch 6 report). To settle when it is built, and report before merging: the username convention in the shared realm, how one person is a user in several tenants (with what Keycloak supports cited), a resend-invitation command, the invitation link's lifespan, and that a user with no roles signs in but can do nothing.

- **Basis:**
  - ADR-017 (Keycloak, realm `insys`; tokens carry `sub` and `tenant_id`; `tenant_id` is an admin-only user attribute);
  - IAM-001 (identity lifecycle);
  - SRS-002 (maker-checker);
  - OQ-067 (first tenant administrator);
  - OQ-037 (federated identity providers).
- **Today:**
  - `ProvisionUser` and `ProvisionTenantAdministrator` need an `externalSubject`. Someone must first create the user in Keycloak by hand and copy its ID (the `tenants-and-access.md` runbook).
  - Disabling a user in INSYS leaves the Keycloak user able to sign in. INSYS then refuses the token's subject, but the account stays live in the realm.

## The proposal

### 1. When INSYS creates the Keycloak user
- **On approval** of a first-administrator request (OQ-067: a second platform administrator approves).
- **On provisioning** of a user in a tenant (`ProvisionUser`). Provisioning has no approval of its own today; a privileged role's assignment is what needs approval. The user is created when it is provisioned, with no roles, so it can sign in but do nothing until a role is assigned.
- **Only for realm `insys`** (the IdP code that ADR-017 names INSYS's own). For a federated identity provider, `externalSubject` stays required, and INSYS links the existing user (OQ-037).

### 2. What is created
Through the Keycloak admin REST API, realm `insys`:
1. `POST /admin/realms/insys/users` with:
   - `username`, `email`, `firstName`, `lastName` (from the display name);
   - `enabled: true`, `emailVerified: false`;
   - the attribute `tenant_id` = the user's tenant. It is admin-only in the realm's user profile, so users cannot change it (ADR-017).
2. The new user's ID, from the `Location` header, is recorded as the INSYS user's `externalSubject`.
3. `PUT /admin/realms/insys/users/{id}/execute-actions-email` with:
   - `UPDATE_PASSWORD`;
   - `CONFIGURE_TOTP` when any role the user is given requires MFA (`requiresMfa`, GAP-021). For a user created with no roles, the MFA action is sent when such a role is first assigned.

The new request field `email` is required for realm `insys`. The command returns only the INSYS user, never Keycloak's raw response.

### 3. The service client
- **The client:** a confidential client `insys-user-admin` in realm `insys`, client-credentials only, with these roles from `realm-management`: `manage-users`, `view-users` and `query-users`. Nothing else: no realm, client or role management.
- **Its secret** lives in the secret store (DEW, ADR-018) and is read by the API through `Identity:Keycloak:ClientSecretRef`.
- **Never exposed:** the secret and the client's tokens never reach a browser, a response, an event, a log or the audit trail.
- **Tokens** are fetched per call batch and cached until shortly before expiry, in memory only.

### 4. If Keycloak is unavailable: never a half-created user
- **The INSYS user** is written first, in its own transaction, with status `PENDING_IDENTITY` (new), no `externalSubject`, and an outbox event `UserIdentityRequested`.
- **The Keycloak user** is created by Identity's own consumer of that event.
  - A Keycloak 5xx, timeout or connection failure throws `PrerequisiteMissingException(KEYCLOAK_UNAVAILABLE, "insys")`. The delivery is parked under ADR-022: alerted, retried by the parking job, and escalated on the tenant's schedule. The user stays `PENDING_IDENTITY` and cannot sign in.
  - A 4xx that will not clear by retrying (a username or email already used in the realm) parks with `KEYCLOAK_USER_CONFLICT` and the field. The operator resolves it: a different username, or linking the existing user by its ID.
  - Any other 4xx (other than 401, 408 and 429, which are retried as unavailable) parks with `KEYCLOAK_USER_REFUSED` and Keycloak's reason (its `errorMessage`, for example `error-person-name-invalid-character`), never as unavailable (BR-006). Someone corrects the user, then retries the delivery.
  - A display name with a character Keycloak's name validator refuses (`< > & " $ % ! # ? § ; * ~ / \ | ^ = [ ] { } ( )` and control characters) is refused when the user is provisioned, as a 422 with a field error on `displayName`, so it does not reach Keycloak.
- **Exactly one Keycloak user:**
  - Before creating, the consumer searches the realm for a user with the same username **and** `tenant_id` attribute. If found, it is linked, not created again. That covers a retry after Keycloak created the user but before INSYS recorded its ID.
  - Recording `externalSubject` and moving to `ACTIVE` happen in the consumer's transaction, with its inbox row (INT-005), so a replay of the event finds the user already linked and does nothing.
- **Sending the email** follows creation. If only the email fails, the user is still linked and active, and the email is retried as its own parked step (`KEYCLOAK_EMAIL_UNSENT`).

### 5. Disabling and re-enabling
- `DisableUser` and `LockUser` in INSYS raise `UserDisabled`. Identity's consumer sets `enabled: false` on the Keycloak user, and ends its sessions (`POST …/users/{id}/logout`).
- `UnlockUser` re-enables it.
- If Keycloak is unavailable, this parks under ADR-022 too. INSYS refuses the user's tokens meanwhile, as today, so access stops at once whatever Keycloak's state.

### 6. Tests (when built)
- Approval of a first-administrator request creates exactly one Keycloak user, with `tenant_id` equal to the target tenant, and records its ID as `externalSubject`.
- A replayed approval, a replayed `UserIdentityRequested` and a retry after a simulated crash between "created in Keycloak" and "recorded in INSYS" create no second user.
- Keycloak down: the user stays `PENDING_IDENTITY`; the delivery is parked with `KEYCLOAK_UNAVAILABLE`; it is created once Keycloak answers.
- Disabling the user disables it in Keycloak and ends its sessions.
- The client secret and access tokens never appear in logs (a captured log provider, as in `ProviderCallbackTests`), responses or audit details.
- Federated IdP: `externalSubject` is still required, and no Keycloak call is made.
- Run against a Keycloak container in the integration tests (Testcontainers; Keycloak is Apache-2.0).

## Consequences
- **Identity:**
  - `email` on users (a new migration), and the `PENDING_IDENTITY` status;
  - the consumer and the Keycloak admin client;
  - `Identity:Keycloak:*` settings.
- **The realm export** (`deploy/keycloak`) gains the `insys-user-admin` client, its role mappings, and the admin-only `tenant_id` attribute (already there).
- **Runbook:** `tenants-and-access.md` loses the manual Keycloak step for realm `insys`, and gains rotating the service client's secret.
- **The front end:** the provisioning forms drop `externalSubject` for realm `insys` and add `email`. A new user shows `PendingIdentity` until Keycloak has it.
