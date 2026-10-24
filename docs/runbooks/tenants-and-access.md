# Tenants and access

## Tenants (platform operator only)

These calls are made from the platform operator tenant [tenant.*]. The same calls from any other tenant are refused, whatever permissions its users hold (TEN-007).

| Step | Call |
|---|---|
| Register | `POST /api/v1/tenancy/tenants` [tenant.create]: the tenant starts in Onboarding |
| Onboard | `POST /api/v1/tenancy/tenants/{id}/complete-onboarding-step` [tenant.update], once per step, in order. These are the eleven steps of the static data load sequence (OQ-029, a proposal). |
| Activate | `POST …/{id}/activate` [tenant.update]: refused until every onboarding step is complete (TEN-008) |
| Suspend | `POST …/{id}/suspend`: from the next request, every user of the tenant is refused, and cached permissions are dropped (Identity consumes TenantSuspended) |
| Reactivate | `POST …/{id}/reactivate` |
| Terminate | `POST …/{id}/terminate`, from Active or Suspended. Final: no transition leaves Terminated. |
| Change isolation profile | `POST …/{id}/change-isolation-profile`, with a migration reference. The data migration itself is not built; the reference is only recorded (OQ-030, open). |

The background jobs act only for active tenants.

### A new tenant's first administrator

Two platform administrators provision it while the tenant is Onboarding, before it has any user (OQ-067, GAP-019).

1. **Create the Keycloak user.** In realm `insys`, create the user and set the `tenant_id` attribute to the new tenant's ID (admin console, or the admin REST API). Note the user's ID: it is the token's `sub`. See [deployment.md](deployment.md#how-a-tenants-users-get-their-tenant_id).
2. **Request the administrator.** A platform administrator calls `POST /api/v1/identity/tenant-administrators` [platform: user.create] with:
   - `tenantId`, `username`, `displayName`, `idpCode`, and `externalSubject` (the Keycloak user's ID from step 1);
   - `mfaEnrolled`;
   - `permissions`: the permissions of the tenant's `TENANT_ADMIN` role. Nothing in the specification fixes this list, so the two administrators agree it. At least `user.read`, `user.create` and `user.update` let the administrator add the tenant's other users.
   It is refused when the tenant is not Onboarding (`TENANT_NOT_ONBOARDING`), already has users (`TENANT_HAS_USERS`), or already has a request waiting (`DUPLICATE_RECORD`).
3. **Approve it.** A second platform administrator approves the Workflow approval (type `TENANT_ADMINISTRATOR`, business reference `TENANT_ADMIN_REQUEST`). They need `approval.read` and `approval.approve`, and a role named in `workflow.cfg_delegation_and_approval_authority` for transaction type `TENANT_ADMINISTRATOR` (WFL-004). The requester cannot approve their own request.
4. **What approval does**, in one transaction:
   - in the new tenant: the `TENANT_ADMIN` role definition in force (approved by the checker), the user (Invited), and an active assignment of `TENANT_ADMIN`, with `approved_by` the checker. The assignment's `assigned_by` is the user itself, because it must be a user of the same tenant, as in the operator bootstrap (OQ-031);
   - in the operator tenant: the request becomes Provisioned and records the user.
   Tenancy then completes the tenant's `ORGANISATION_AND_ACCESS` onboarding step, if it is the current step. Otherwise the operator completes it as usual.
   A rejected request creates nothing.
5. **Sign-in.** The tenant's users, its administrator included, can act once the tenant is Active (TEN-008). Allow up to 30 seconds after activation: a refused sign-in is cached for that long (ADR-006).

Follow a request with `GET /api/v1/identity/tenant-administrators/{id}` (or `?tenantId=` for a tenant's requests). Every later user and privileged assignment goes through the tenant's own administrators and maker-checker.

### Arrears ageing buckets (request 46)
- **Cover every day overdue, from day 1 to the last action,** in `billing.cfg_arrears_ageing_buckets`: no gaps or overlaps between buckets, and the last bucket's action is `LAPSE` or `CANCEL`. The onboarding readiness check `COLLECTION_METHODS` checks it.
- **Without a bucket for the days overdue,** nothing is lost, but nothing moves either:
  - an account's first overdue premium cannot open its arrears case;
  - an open case past the last bucket stays in it.

  Each is a parked delivery (`ARREARS_AGEING_BUCKET_NOT_IN_FORCE`, with the days overdue as item), which alerts as parked deliveries do (ADR-022). Publishing the missing bucket retries it: the case opens, or ages on, and Life's grace and lapse follow from the arrears events.
- **Until then a missed premium cannot lead to a lapse,** so publish the buckets before the tenant's first policy is billed.

## Users and roles (IAM)

- **Provisioning.** A user is provisioned (`POST /api/v1/identity/users` [user.create]) with the IdP subject they will sign in with.
  - The user is Invited until their first request, which makes them Active.
  - Tokens carry only `sub` and `tenant_id`. Everything else comes from the user's role assignments.
- **Assigning a role.** `POST /api/v1/identity/users/{id}/assign-role` [user.update].
  - An ordinary role applies at once.
  - A **privileged role** creates a pending assignment and a Workflow approval of type PRIVILEGED_ROLE (OQ-038). A privileged role is one in an authentication policy's `mfa_required_roles`, or one that holds any of such a role's permissions.
  - The pending assignment gives no permission until a second person approves it (`POST /api/v1/workflow/approvals/{id}/approve` [approval.approve]). That person cannot be the requester and must hold PRIVILEGED_ROLE authority.
  - Privileged roles also need the user to be MFA-enrolled (IAM-003).
- **Revoking a role.** `POST …/{id}/revoke-role` [user.update]. The assignment ends today, so the user's next request is already without it (IAM-005). Other API instances would catch up within the permission cache duration, but only one instance runs (see [deployment.md](deployment.md)).
- **Lock and unlock.** `POST …/{id}/lock` and `…/unlock`: temporary. A locked user is refused.
- **Disable.** `POST …/{id}/disable`: final. The user's open sessions are revoked and they can no longer act (IAM-005). Use this for leavers and for compromised accounts.

Conflicting permissions cannot be held together (SRS-002): an assignment that would combine them is refused.

### Scopes and channels
- **Scopes (F103).** A role assignment with `scopeProducts` or `scopeBranches` (organisation nodes, each covering the nodes below it) limits its permissions to those records. Outside them a record reads as not found and an action is refused with `OUT_OF_SCOPE`. `GET /api/v1/identity/role-assignments/scoped` lists the scoped assignments for review.
- **Channels (F005).** Until a tenant has channel definitions (`channel.cfg_channel_definitions`), channels do not apply.
- **Once it has any:**
  - every command of General, Life, Health, the Claims Framework and payouts must come from a client listed in a channel's `client_ids` (the token's `azp`, for example `insys-web`);
  - the caller's roles need an allowed row in `channel.cfg_channel_transaction_permissions` for that channel, the command's permission code and the product.
- **Set up the back office first:** before adding a partner channel, add the back-office channel with `insys-web` in `client_ids` and rows for the staff roles. Otherwise staff commands are refused with `CHANNEL_NOT_ENABLED`.
- **The refusals:** `CHANNEL_NOT_ENABLED` (client on no channel, or the transaction not enabled), `CHANNEL_NOT_ALLOWED` (no allowed row for the roles and product), `CHANNEL_APPROVAL_REQUIRED` (the row requires approval and the command has no approval flow).
- **Reads** are never channel-scoped.

## Privileged access (TEN-007)

Time-limited access for support, for example to look at a tenant's data during an incident.

1. Request it: `POST /api/v1/identity/privileged-access` [privileged_access.create], with a reason.
2. Someone else decides: `POST …/{id}/approve` or `…/reject` [privileged_access.approve].
3. The grant expires at the end of its window (`Identity:PrivilegedAccess`, 8 h by default).

Limits today:

- A grant **cannot be revoked early**. The REVOKED status exists in the schema, but no transition reaches it (OQ-041). To cut access off early, revoke the user's roles or disable the user.
- A grant records who, why and when, but **does not yet change anyone's permissions** (OQ-043). The access itself comes from roles.

## Access reviews

1. Start a review: `POST /api/v1/identity/access-reviews` [access_review.create]. It snapshots the users and role assignments in its scope.
   - The service-account scope is refused: service accounts are not modelled as users (OQ-042).
2. The assigned reviewer, and only that reviewer (OQ-045), works through the items:
   - `POST …/{id}/items/{itemId}/confirm` keeps an assignment;
   - `POST …/{id}/items/{itemId}/revoke` marks it for removal.
3. The reviewer completes the review: `POST …/{id}/complete`. The revocations take effect then, all in one transaction (OQ-044).

## Checking who did what

`GET /api/v1/audit/events` [audit_event.read] lists the tenant's audit trail.

- Every change is there, and so is every refused request (OQ-015).
- Audit entries are tenant-scoped like every other record: a token for one tenant never returns another tenant's entries (TEN-004).
