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

**There is no supported way to do this yet (OQ-067).**

- Users are provisioned only in the caller's own tenant (the token's `tenant_id`).
- A new tenant has no users, so no one can call the API for it.
- The platform operator's bootstrap (`db/bootstrap/operator.sql`) covers only the operator tenant.

Until OQ-067 is decided, an owner can insert the tenant's first administrator and role assignment the same way `operator.sql` does, for the new tenant's ID, as a recorded change. Every later user and privileged assignment then goes through the API and maker-checker.

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
