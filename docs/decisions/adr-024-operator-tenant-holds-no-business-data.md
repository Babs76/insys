# ADR-024: The operator tenant administers the platform and holds no business data

- **Status:** **Accepted** (23 October 2026, signed off). Closes OQ-078. Built on `wave0-5`: `IModule.HoldsBusinessData`, and one endpoint filter (`RefuseOperatorTenant`) on every business module's routes, which leaves static data and platform endpoints open. Tested by `OperatorTenantTests`, which also checks every endpoint. Not merged until the front end's live tests have moved to a business tenant.
- **Requirements:** TEN-001, TEN-002, TEN-007 (platform administration by the operator only), SRS-002, IAM-006
- **Raised by:** the front end, F5 finding 3

## Context
- **The operator tenant** (`Platform:OperatorTenantId`, code `OPERATOR` by the bootstrap) is where platform administrators sign in. Platform endpoints accept only it (TEN-007).
- **Nothing stops business use of it.** Every business endpoint checks only that the caller has the permission in their tenant. An operator user given `policy.create` in the operator tenant can create parties, quotes, policies, claims and payments there. Nothing in the specifications says it should, and:
  - such records would sit beside the platform's own configuration and audit, under the people who administer every tenant;
  - the operator tenant's global static data would be mistaken for a business tenant's configuration;
  - operations views that list tenants (parked deliveries, dead letters, tenants) would show the operator as a business tenant.

## Decision

### The rule
**Business modules refuse the operator tenant.** A request whose tenant is the operator tenant, to an endpoint of a business module, is refused with **403 `OPERATOR_TENANT_NOT_BUSINESS`**, a stable error code, before the handler runs. Reads are refused as well as changes, so that nothing suggests business data could exist there.

| Module | In the operator tenant | Why |
|---|---|---|
| Tenancy, Identity | **Allowed** | Platform administration: tenants, their first administrators, the operator's own users (TEN-007, GAP-019) |
| Static data | **Allowed** | Global static data is maintained here (ADR-003) |
| Audit | **Allowed** | The operator's own audit trail, and the platform search log |
| Integration | **Allowed** | Operations views: dead letters, parked deliveries by tenant, webhooks, partner register |
| Workflow | **Allowed** | Approvals of platform actions (a second administrator approves a tenant administrator, GAP-019) and platform tasks |
| Party, Product Factory, Rating, Rules, General, Life, Health, Claims Framework, Billing, Payments, Finance, Distribution, Reinsurance, Documents, Compliance, Analytics | **Refused** | Business data belongs to business tenants |

### Where it is enforced
- **Requests:** an endpoint filter on each business module's route group, from one building-block helper (`RefuseOperatorTenant()`), so no endpoint is missed. A test walks every endpoint and checks that each business module's routes carry it, as the security sweep does for permissions.
- **Background work:** jobs and the outbox relay already go tenant by tenant over active tenants. Business modules' jobs skip the operator tenant; the relay still delivers the operator tenant's own events (Identity, Tenancy, Audit, Workflow).
- **The database** is unchanged: row-level security already keeps each tenant's rows apart. A check that no business table has a row for the operator tenant runs in the deployment smoke test.

### What does not change
- An operator acting on a business tenant's behalf still uses only the existing, audited paths (`WithinTenant` in Identity and Tenancy).
- Permissions are unchanged: holding `policy.create` in the operator tenant is harmless, because the module refuses the tenant.

## Consequences
- **Error catalogue:** `OPERATOR_TENANT_NOT_BUSINESS` (403), with its message key.
- **OpenAPI:** the business modules' operations document the 403 with that code.
- **The front end:** a platform administrator's session shows only platform screens; a business screen opened in the operator tenant shows the 403 message.
- **Existing data:** none expected. The smoke-test check reports any business row found in the operator tenant for review before the rule is switched on.
