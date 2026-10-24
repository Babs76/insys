# ADR-017: Keycloak as the identity provider

- **Status:** Accepted (Phase 8 brief, decisions table, 23 October 2026)
- **Date:** 23 October 2026
- **Requirements:** IAM-001 to IAM-005, TEN-002, TEN-003; resolves the IdP choice left open in SRS §30 and `InsysAuthenticationOptions.Authority`; relates to OQ-014 and OQ-037

## Decision
- **Keycloak (OIDC) issues the access tokens.** The API validates them through `Authentication:Authority`, which is the realm's issuer URL, and `Authentication:Audience` (`insys-core-api`).
- **Tokens carry `sub` and `tenant_id` only** (OQ-014, resolved in the Phase 0 review):
  - `tenant_id` comes from a protocol mapper on the API's client scope.
  - Roles, groups, email and profile are kept out of the access token, and permissions still come from IAM role assignments on every request.
- **One realm for the platform.** Each user has a `tenant_id` attribute, mapped into the token by a User Attribute mapper. A user who works for two tenants has two Keycloak users, so each token names exactly one tenant (TEN-003).
- **Clients:**
  - `insys-core-api`, a bearer-only resource server: this is the audience.
  - One public client per front end, with PKCE.
  - Confidential clients for service integrations, with the service account's `tenant_id` set as a hard-coded claim.
- **The platform operator** is a user whose `tenant_id` is `Platform:OperatorTenantId`. Platform endpoints still check the tenant and the IAM permission (TEN-007).
- **Linking to IAM.** `iam.user_account.external_subject` holds the Keycloak `sub`, and `idp_code` is `KEYCLOAK`.
- **MFA** is enforced by Keycloak for privileged roles. IAM already refuses privileged roles to users who are not MFA-enrolled (IAM-003). Keeping the two in step is an onboarding procedure, recorded in the runbook.

## Alternatives considered
**A realm per tenant** gives tenant-specific login pages and password policies. But the API would have to trust many issuers and map each one to a tenant, and the issuer-to-`idp_code` mapping is the open question OQ-037. A single realm keeps one issuer. A tenant that needs its own IdP can be federated into this realm as an identity provider.

## Consequences
- **Workstream D adds Keycloak for local development:** to `docker-compose.yml`, with a realm export containing the `tenant_id` mapper. The deployment runbook gets the realm and client setup. The development signing key stays for tests.
- **OQ-037 is narrowed, not closed.** With one realm there is one issuer. A federated IdP keeps its users' `sub` unique within the realm.
- **Disabling a user (IAM-005)** must be done in both places. IAM refuses at once, and Keycloak stops issuing new tokens. Access tokens should be short-lived (5 minutes is proposed), because IAM's refusal covers the rest.
- **The first administrator of a new tenant (OQ-067)** needs both a Keycloak user with that `tenant_id` and an IAM user.

## Amendment 1: one Keycloak user per person per tenant (24 October 2026)

- **Status:** Accepted (Ade, 24 October 2026), with the Keycloak user provisioning built in Wave 0b batch 6 (`addenda/keycloak-user-provisioning.md`).
- **Context:**
  - Realm `insys` serves every tenant.
  - A token carries one `tenant_id`, from an admin-only user attribute.
  - One person may need to be a user in more than one tenant, for example a consultant of two insurers.
  - Keycloak usernames are unique in a realm.

### Decision
- **One Keycloak user per person per tenant.** Each has its own `tenant_id` attribute, its own password and one-time code, and its own sessions.
- **The username** is `<tenant code>.<username>` in lower case, for example `oft-za.jane.doe`. INSYS creates it when it provisions the user (Keycloak keeps usernames in lower case).
- **The realm allows duplicate emails,** so the same person's accounts in different tenants can share one address. Keycloak then switches "Login with email" off: users sign in with the tenant-qualified username.
- **No change to the token:** it still carries only `sub` and `tenant_id` (and the API audience).

### Trade-offs
- **A person in two tenants has two accounts:** two usernames, two passwords, two one-time code set-ups. Leaving one tenant ends only that account.
- **No sign-in by email,** because duplicate emails disable it. "Forgot password" asks for the username too.
- **A username can collide** with another tenant's user only if the tenant codes and usernames together are the same. Tenant codes are unique, so a collision means the account was created outside INSYS; INSYS parks it for the operator (`KEYCLOAK_USER_CONFLICT`).

### Alternative considered: Keycloak Organizations
- **What it offers:** since Keycloak 26, one user may be a member of several organizations, and a client asks which organization a token is for by scope. Checked on 26.7.5: a user in two organizations got `organization: ["alpha"]` with scope `organization:alpha`.
- **Why not now:**
  - a single account per person would need the tenant to be chosen at sign-in and carried in a different claim, changing the token contract INSYS relies on (`tenant_id`, ADR-017's decision);
  - the API's tenant resolution would change;
  - the front end would need a tenant picker.
- **When to revisit:** if people commonly work in several tenants. That would be a new decision, not this amendment.
