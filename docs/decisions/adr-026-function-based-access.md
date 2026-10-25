# ADR-026: Roles hold functions; the menu and access come from one function catalogue

- **Status:** **Accepted** (25 October 2026, Ade), with six changes to the proposal (see `addenda/function-catalogue.md`, "Decisions at sign-off"):
  1. `audiences` (a list), not `audience`.
  2. The taxonomy comes from `docs/specs/INSYS_NAVIGATION.md`.
  3. Portal screens belong to portal sub-domains.
  4. Static data functions are per catalogue sub-module.
  5. There are seven initial features.
  6. Members are `CUSTOMER`.
- **Requirements:** SRS-001 (role-based access), SRS-002 (maker-checker, segregation of duties), SRS-003 (access by product, organisation and relationship), IAM-001, IAM-006, IAM-007, TEN-001 and §4.1 (tenant feature entitlements), §8 (portals).
- **Related:**
  - ADR-017 (Keycloak; tokens carry only `sub` and `tenant_id`);
  - ADR-022 (parking);
  - ADR-024 (the operator tenant);
  - `addenda/access-scopes.md` (F103), `addenda/channel-permissions.md` (F005), `addenda/external-user-access.md`, `addenda/onboarding-readiness.md` (access while Onboarding);
  - the detail is in `addenda/function-catalogue.md`.

## Context
- **What a role holds today:** a permission set (`iam.cfg_roles_and_permission_sets.permissions`), plus F103 scopes. Each endpoint requires one permission (`x-permission`), and a few handlers check another (`x-additional-permissions`).
- **What nothing describes:**
  - which screens and actions exist;
  - how they group into a menu;
  - who may see them.

  The front end hard-codes its menu, and it cannot know whether a tenant uses a capability.
- **What is not enforced:**
  - `tenant.cfg_feature_entitlements` is configured but not enforced.
  - Access while Onboarding is decided per module (`TransactsBusiness`), not per operation.
  - Channel permissions (F005) key on permission codes.

## Decision (Ade's decisions 1–5)
1. **A role is granted functions** (`iam.cfg_role_function_assignments`). Functions replace permission sets as what a role holds.
   - Each function lists the permissions it needs.
   - A user's effective permissions are the union of the required permissions of the functions of their in-effect roles.
   - Permission checks (`x-permission`) keep working on that union, and F103 scopes stay on the role assignment.
2. **One global function catalogue** (`iam.ref_function_catalogue`), maintained by the platform and shipped with each release. Each tenant enables or disables functions (`tenant.cfg_tenant_function_availability`).
3. **Functions cover everything a person or the system does:** screens (`SCREEN`), record actions such as Approve and Reverse (`ACTION`), and system jobs (`JOB`).
4. **Tenants cannot rename or reorder menu items.** Labels come from translation keys in the catalogue's `labels` column.
5. **The back end builds the menu at sign-in from these tables** (`GET /api/v1/identity/me/menu`). Nothing is hard-coded.

And, proposed here:
6. **Every API operation declares its function** (`x-function`, next to `x-permission`). A request is refused unless all four hold:
   - the user holds the function through an in-effect role;
   - the platform makes it available;
   - the tenant enables it;
   - the tenant is entitled to its required feature.

   Stable codes: `FUNCTION_NOT_GRANTED`, `FUNCTION_DISABLED` and `FEATURE_NOT_ENTITLED` (403).
7. **A disabled `SYSTEM` function parks its work** under ADR-022 (prerequisite `FUNCTION_DISABLED`, item the function code). It never drops it.
8. **Onboarding access** (`allowed_while_onboarding`) is decided per function, replacing the per-module `TransactsBusiness` flag.
9. **Channel transaction permissions (F005) key on `function_code`,** so one catalogue serves menus, access and channels.
10. **The catalogue is generated** from the operations' and jobs' declarations into a repeatable migration, as the error code catalogue is. A test fails when an operation has no function, or the committed catalogue is stale.

## Consequences
- **Finer control:** what a role may do is a list of named screens and actions. A per-table static data function (view, maintain, approve) can be granted without granting all static data.
- **No change for any user at the switch:** existing permission sets are converted to function grants so that every user's effective permissions are the same. A test compares them before and after.
- **About 1,200 functions** (addendum §8), about 880 of them static data, grouped by module under a Configuration domain.
- **Generated, not hand-kept:** a new endpoint without a function fails the build's contract test, and Wave 1's operations register their functions as they are built.
- **Two more lookups per request**, both from the per-user cache that permissions already use, invalidated in the same places.
- **The front end** reads the menu and `x-function` instead of hard-coding them. It must handle three new 403 codes.

## Alternatives considered
- **Keep permission sets and add a separate menu table.** Rejected: two lists of what a role may do would drift apart, and the menu would not match access.
- **Functions only for screens, with permissions for actions.** Rejected: Ade's decision 3 makes actions functions, so a role's grants read as the actions people take.
- **Per-tenant catalogues.** Rejected: decision 2. Tenants only enable or disable what the platform ships.
