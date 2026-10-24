# ADR-023: Payment callbacks authenticate the provider, not a user

- **Status:** **Accepted** (revision 2, 23 October 2026). Revision 1 proposed OAuth 2.0 client credentials as the main mechanism. It was approved with a change: the main mechanism is an endpoint key in the callback address plus a signature, and client credentials are optional. Closes OQ-077.
- **Requirements:** PAY-003 (provider callbacks), PAY-005 (an unknown outcome is never success), TEN-002 (the tenant comes only from authenticated material), IAM-001, SEC-004, INT-007
- **Raised by:** the front end, F5 finding 2 and request 28 (GAP-035)

## Context

### What a callback needs today
`POST /api/v1/payments/providers/{providerCode}/callbacks` needs two things:

1. **A bearer token**, like every other endpoint. It is a Keycloak access token whose `sub` is an IAM user of the tenant holding `payment_callback.create`. The tenant comes from the token's `tenant_id`.
2. **A signature:** `X-Signature`, the HMAC-SHA256 of the raw body under `Payments:Providers:{providerCode}:CallbackSecret`, hex in lower case. If the signature is wrong, the callback is recorded and not applied (`CALLBACK_SIGNATURE_INVALID`).

### Why that does not work for a provider
- **A provider cannot hold a user token.** User tokens are issued to people through a browser sign-in and expire within minutes. A provider's server has no user and no browser, and cannot refresh a token.
- **The secret is per provider, not per tenant.** One `CallbackSecret` per provider code serves every tenant.
- **No replay protection.** The signature covers only the body, so a captured callback can be sent again.

## Decision

### 1. A callback address per tenant and provider, with an unguessable endpoint key
- **The address** is `POST /api/v1/payments/callbacks/{endpointKey}`, with no bearer token. The endpoint key is 32 random bytes in base64url, issued by INSYS for one tenant and one provider. It is given to the provider as part of the callback address it is configured with.
- **The mapping** is held server side, in `payment.callback_endpoint`:
  - the tenant;
  - the provider code;
  - the key's SHA-256 hash, used for the lookup;
  - the signing secret, encrypted with the field-protection key provider (DEW KMS envelope encryption in the cloud, ADR-018);
  - status (`ACTIVE`, `RETIRING`, `REVOKED`);
  - when it was issued, by whom, and when it retires.

  The key itself is never stored, only its hash, so a database read does not reveal a usable address.
- **Tenant context** is set only after:
  1. the key's hash matches an `ACTIVE` or `RETIRING` endpoint;
  2. the body's signature verifies under that endpoint's own secret.

  The lookup runs under a dedicated database function, which returns the tenant and the encrypted secret for a key hash and nothing else. Every later read and write runs in that tenant, under row-level security as usual (TEN-002, TEN-004).
- **An unknown key, a revoked endpoint or a bad signature** is answered with the same 401 `CALLBACK_SIGNATURE_INVALID`. The answer gives no sign of which check failed. Each case is counted for monitoring.
  - If the key is unknown, nothing is recorded in any tenant, because no tenant is known.
  - If the key is known but the signature is bad, the attempt is recorded in that tenant and not applied, as today.

### 2. The signature covers a timestamp and an event ID; replays are refused
- **Headers:**
  - `X-Callback-Timestamp`: Unix seconds;
  - `X-Callback-Id`: the provider's unique ID for this notification;
  - `X-Signature`: HMAC-SHA256, hex in lower case, of `<timestamp>.<callback id>.<raw body>`, under the endpoint's secret.
- **Tolerance:** a timestamp more than `Payments:Callbacks:Tolerance` away from the server's clock is refused. The default is 5 minutes.
- **Replays:** each endpoint's callback IDs are kept for twice the tolerance window, in `payment.callback_receipt`, unique on endpoint and callback ID. A repeated ID is refused with 409 `CALLBACK_REPLAYED` and not applied. A status older than the payment's current one is still never applied (the payment's status machine).

### 3. No user bearer token on callbacks
The new address accepts no bearer token and needs none. The old route, `/providers/{providerCode}/callbacks`, keeps working under the bearer-token rules until the front end's simulator scripts have moved to the new address. It is then removed, and that is announced in `contract-changes.md`. **Removed in Wave 0b batch 5** (24 October 2026), once the front end had moved.

### 4. Client credentials are an optional extra
For a provider that supports OAuth 2.0 client credentials, an endpoint can also require a token. There is one Keycloak confidential client per tenant and provider, whose service account is an IAM service identity in that tenant holding only `payment_callback.create` (ADR-017). When the endpoint's `require_client_token` is set, the callback must carry such a token, and its `tenant_id` must match the endpoint's tenant. It never replaces the key and signature.

### 5. Rotation, storage and logging
- **Rotation:** issuing a new endpoint for a tenant and provider sets the current one to `RETIRING` until a cut-over time (`Payments:Callbacks:Overlap`, 7 days by default). Both are accepted until then. The old one is then `REVOKED`. A revoke is immediate, for a leak.
- **Who manages them:** issuing, rotating and revoking need `payment_callback_endpoint.update`. They are audited. The key and secret are shown once, in the issue response, and never again.
- **Storage:** the secret is encrypted with the field-protection keys (ADR-018: DEW KMS envelope keys in the cloud, local keys in development). The key is kept only as a hash.
- **Never logged:** the endpoint key, the secret and the signature are left out of logs, audit details, problem responses and traces. Logs name the endpoint by its ID only. The request path is logged with the key masked.

### 6. The firewall allow-list stays as a second layer
At the Huawei Cloud WAF (ADR-016), the callback path accepts only each provider's published source addresses. It is not the authentication: addresses change and are shared.

## Options considered
| Option | Why not chosen as the main mechanism |
|---|---|
| **Client credentials** (revision 1) | Not every provider can fetch OAuth tokens. They are kept as an optional extra (decision 4). |
| **mTLS** | TLS ends at the Huawei ELB, which would have to pass the certificate on in a header the API trusts. It is heavier to issue and rotate per tenant and provider. |
| **Tenant in the URL or the body, with the signature only** | The tenant would come from material the caller chooses, not from a secret only that tenant's provider holds. |
| **Allow-list alone** | Addresses are not identities. |

## How it is built (`wave0-5`)
- V0033: `payment.callback_endpoint`, `payment.callback_receipt` and `payment.resolve_callback_endpoint`.
- `POST /api/v1/payments/callbacks/{endpointKey}` (no token), and `GET`/`POST /api/v1/payments/callback-endpoints` and `POST …/{id}/revoke` [`payment_callback_endpoint.read`/`.update`].
- Traces record the callback path with the key masked.
- Tests: `ProviderCallbackTests` covers no token needed; another tenant's key or secret; an old timestamp; an unknown key; a replayed ID; rotation and revocation; and nothing secret stored in plain text, listed or logged.

## Consequences
- **Payments:**
  - `payment.callback_endpoint` and `payment.callback_receipt`, in a new migration, with the key-lookup function;
  - the new route, the signature check with timestamp and replay protection, and endpoint issue, rotate and revoke;
  - tests: a wrong tenant's key, a bad signature, an old timestamp, a replayed ID, rotation overlap, a revoked key, and nothing secret in logs.
- **Configuration:**
  - `Payments:Callbacks:Tolerance` and `Overlap`;
  - `Payments:Providers:<code>:CallbackSecret` stayed only for the old route; it went with it in Wave 0b batch 5.
- **Runbook:** issuing, rotating and revoking a provider's endpoint, in `money-operations.md`.
- **The front end:** the simulator scripts move to the new address and headers. The README gives the format.
