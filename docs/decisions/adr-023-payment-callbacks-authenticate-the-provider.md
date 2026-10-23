# ADR-023: Payment callbacks authenticate the provider, not a user

- **Status:** **Proposed** (23 October 2026), for sign-off. Nothing is built.
- **Requirements:** PAY-003 (provider callbacks), PAY-005 (an unknown outcome is never success), TEN-002 (the tenant comes only from the authenticated principal), IAM-001, SEC-004
- **Raised by:** the front end, F5 finding 2 and request 28 (GAP-035)

## Context

### What a callback needs today
`POST /api/v1/payments/providers/{providerCode}/callbacks` needs two things:

1. **A bearer token** like every other endpoint: a Keycloak access token whose `sub` is an IAM user of the tenant, and whose `tenant_id` is that tenant. That user needs `payment_callback.create` in the tenant. The tenant the callback applies to comes from this token, as the platform requires (TEN-002).
2. **A signature:** `X-Signature`, the HMAC-SHA256 of the raw body under `Payments:Providers:{providerCode}:CallbackSecret`, hex in lower case. A callback with a wrong signature is recorded and not applied (`CALLBACK_SIGNATURE_INVALID`).

### Why that does not work for a provider
- **A provider cannot hold a user token.** User tokens are issued to people, through a browser sign-in, and expire in minutes. A provider's server has no user, no browser and no refresh flow. Today the only way to send a callback is to borrow a person's token, which no provider can do and which ties the provider to a person.
- **The secret is per provider, not per tenant.** One `CallbackSecret` per provider code serves every tenant, so a provider that knows one tenant's secret can sign callbacks for another tenant's payments, if it also had a token.
- **No replay protection.** The signature covers only the body. A captured callback can be sent again. Applying it twice is harmless today (the payment's status machine ignores a repeat), but an older status sent after a newer one is not.

## Decision (proposed)

### 1. A provider authenticates with OAuth 2.0 client credentials, one client per tenant and provider
- **Keycloak** gets a confidential client per tenant and provider, `payments-<tenant code>-<provider code>`, with only the client-credentials grant. ADR-017 already provides for this: confidential clients for service integrations, with the service account's `tenant_id` as a hard-coded claim.
- **The client's service account is an IAM service identity** in that tenant, provisioned like a user (subject = the service account's subject), with one role that holds only `payment_callback.create`. So:
  - the tenant still comes only from the authenticated principal (TEN-002), with no new authentication path in the API;
  - the permission check and the audit trail work unchanged, and the audit shows which provider acted;
  - revoking a provider is disabling its client or its role assignment.
- **The client secret** is kept in the secret store (DEW, ADR-018), referenced from the provider register (`credential_ref`), and given to the provider once, out of band. Rotation follows the secret store's runbook.
- **Token lifetime:** 5 minutes; the provider fetches a new token as it needs one.

### 2. The signature stays mandatory, keyed per tenant and provider, and covers a timestamp
- **The key** is the tenant's own, from the provider register's `credential_ref` in the secret store, instead of one `CallbackSecret` per provider code for all tenants. The configuration setting remains for local development (README, GAP-035).
- **The signed text** becomes `<X-Signature-Timestamp>.<raw body>`, where `X-Signature-Timestamp` is Unix seconds. A callback more than 5 minutes old, or from the future by more than 5 minutes, is refused with `CALLBACK_SIGNATURE_INVALID` and recorded.
- **A status older than the payment's current one** is recorded and not applied (the payment's status machine already refuses going back).

### 3. Network allow-list as defence in depth
At the Huawei Cloud WAF (ADR-016), the callback path accepts only each provider's published source addresses. It is not the authentication: addresses change and are shared.

### Options considered
| Option | Why not chosen |
|---|---|
| **mTLS**, a client certificate per provider | TLS ends at the Huawei ELB, which would have to pass the certificate on in a header the API trusts; certificate issuance and rotation per tenant and provider is heavier than client credentials, which Keycloak already does. Possible later for a provider that requires it. |
| **Signature alone**, with the endpoint open to anonymous calls | The tenant would have to come from the URL or the body, which breaks TEN-002: the tenant must come from the authenticated principal. |
| **Allow-list alone** | Addresses are not identities. |
| **A long-lived API key per provider** | A second credential type to issue, store, rotate and audit, for what client credentials already give. |

## Consequences
- **Keycloak realm:** one confidential client per tenant and provider; a runbook section in `tenants-and-access.md` to create, rotate and revoke it.
- **Identity:** the service identity is an `iam.user_account` with a service flag (OQ to confirm whether the account table needs one, or a naming rule is enough).
- **Payments:** the signature key comes from the provider register's `credential_ref` per tenant; the timestamp check; tests for a wrong tenant's key, an old timestamp and a replay.
- **The front end:** no change. It never calls this endpoint; its simulator scripts follow the README.
- **Until this is built,** callbacks in local development and tests use a test user's token and the configured secret, as today.
