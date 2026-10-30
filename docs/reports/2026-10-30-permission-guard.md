# Permission guard, 30 October 2026

Branch `permission-guard`, from main. It merges before 5a.2.

## The guard
`EveryPermissionChecked_IsGrantedByAFunction_ADR026` (integration): a permission reaches a role only through a function, so one that is checked and granted by no function can never be held. Checked means on an endpoint, named by `AlsoChecks`, or a constant of a `...Permissions` class the source uses. Platform operator routes are left out; they do not work through tenant functions.

`NoPermissionCheck_TakesAStringLiteral_ADR026` (architecture) closes its blind spot: `HasPermission`, `RequirePermission`, `RequirePlatformPermission`, `AlsoChecks`, a policy built from a permission, and an approval's required permission must name a constant. There were no literals. The grants in `FunctionMap.Permission` are not checks and are not covered.

## What it found: `payment_callback.create`
A callback endpoint with `require_client_token` needs a token holding this permission (ADR-023 decision 4). No function granted it, so every callback to such an endpoint was refused with a 401. No test covered that path.

**Fix.** A new function, `PAYMENTS_CALLBACK_CLIENT`, privileged and in no role template: it is for a provider's service identity, not a person.

## Readiness
`PAYMENT_CALLBACK_CLIENT` on `PAYMENTS_AND_DISTRIBUTION`, blocking: an endpoint that is not revoked requires a client token, and no active user holds the function. It lists the providers affected.

**Service identities.** IAM records no kind of user, so the check cannot tell a service identity from a person. It asks for an active user holding the function, which only a role made for the purpose can grant.

**Front end and tenant setup.** Grant `PAYMENTS_CALLBACK_CLIENT` through a role made for the provider's service identity, before requiring a client token on an endpoint.

## Found, not fixed: Communications callbacks
`CommunicationsModule` accepts any authenticated token of the tenant at an endpoint requiring a client token, with no permission check. Payments requires the permission. Recorded for a decision; this branch does not change it.
