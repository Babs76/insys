# Security incidents and key rotation

This runbook covers what the system lets you do. The incident process itself is the organisation's: who declares an incident, who is told, and the regulator notification deadlines. The specifications do not define it.

## Cut off access first

| Situation | Action |
|---|---|
| A user's account or device is compromised | `POST /api/v1/identity/users/{id}/disable` [user.update]. Their open sessions are revoked, and they can no longer act (IAM-005). Also disable them at the identity provider. |
| A user holds a role they should not | `POST …/{id}/revoke-role`. It takes effect from their next request. |
| A user needs blocking while you investigate | `POST …/{id}/lock`. `…/unlock` restores them. |
| A whole tenant must stop | From the platform operator: `POST /api/v1/tenancy/tenants/{id}/suspend` [tenant.update]. Every user of the tenant is refused from the next request. `…/reactivate` restores it. |
| A partner's webhook endpoint is compromised | End the subscription in `integration.cfg_webhook_subscriptions`: retire its version (see [static-data.md](static-data.md)). Then rotate its signing key. |
| A payment provider's callbacks are forged | A callback with an invalid `X-Signature` is recorded and not applied (401 `CALLBACK_SIGNATURE_INVALID`). Check the recorded callbacks. Rotate the provider's key in its gateway. |

## Investigate

The audit trail is at `GET /api/v1/audit/events` [audit_event.read], for each tenant.

- It holds every change and every refused request (ADR-004, OQ-015).
- Entries are tenant-scoped, so use a token for the affected tenant.
- Look for:
  - refusals (403) from the same subject or source address;
  - privileged access grants (`GET /api/v1/identity/privileged-access`);
  - role assignments, and the approvals of them in Workflow;
  - static data changes (`GET /api/v1/static-data/change-requests`).

Logs and events never contain medical or identity data, so they can be shared with the investigators.

## Rotate keys

| Key | Where it is | How to rotate |
|---|---|---|
| Field encryption (`FieldProtection:Keys`, `ActiveKeyId`) | Configuration, until a KMS or HSM is chosen (OQ-020) | See [Field encryption keys](#field-encryption-keys) below. |
| Lookup hash (`FieldProtection:HashKey`) | Configuration | **Cannot be rotated in place.** Every `_hash` column would have to be recomputed from the decrypted values, and no tool for that exists yet. Treat a leak of this key as needing a planned migration. |
| Webhook signing keys (`Integration:Secrets:<ref>`) | Configuration, standing in for a secret store | Agree a change time with the partner. Change the value, or point the subscription at a new reference with a new subscription version, and restart. |
| Token signing | The identity provider | Rotate it at the IdP. The API reads the IdP's keys from `Authentication:Authority`. |
| Database role passwords | Outside this repository (production) | Change the password in PostgreSQL and in `ConnectionStrings:Template` together, then restart. |

### Field encryption keys

1. Add the new key to `FieldProtection:Keys` under a new ID. Each key is 256 bits, base64-encoded.
2. Set `ActiveKeyId` to the new ID, and restart. New values are encrypted with the new key.
3. **Keep the old key in `Keys`.** Each stored value records its key ID, and values encrypted with the old key need it to be decrypted.

There is no re-encryption job. The old key can be removed only once nothing encrypted with it remains, and that needs a tool that has not been built yet.

## After the incident

- Re-enable users only with new credentials at the IdP.
- Review the roles of everyone involved with an access review (see [tenants-and-access.md](tenants-and-access.md)).
- If data was changed, correct it through the normal commands: reversals for money, and new versions for static data. Never edit tables directly. Posted records are never changed.
