# Addendum: product and organisation scopes on permissions (F103, SRS-003)

**Signed off 23 October 2026** (ADR-019); closes OQ-074. Planned as Wave 0b.

- **Basis:**
  - SRS-003: "Permissions MUST be restricted to authorised tenants and, where configured, to specific products, branches, organisations, and transaction categories."
  - Static data:
    - `tenant.mst_organisation_structure`: nodes with `node_code`, `type` (Legal entity, Region, Branch), `parent` and `licences`;
    - `iam.cfg_roles_and_permission_sets`: `scope_products` and `scope_branches`, a role's default scopes.
  - Role assignments carry `scope_products` and `scope_branches` (`iam.user_role_assignment`).
- **What the specifications leave undefined:**
  - which records belong to a branch or organisation, and how a record gets one;
  - whether a scope on a node covers the nodes below it;
  - how a scope applies to lists, to single reads and to actions;
  - what a "transaction category" is.

## What the code does today (checked 23 October 2026)
- **Tenants:** enforced everywhere (row-level security, TEN-002).
- **Products and branches:** stored on role assignments, returned by `/identity/me`, and **not enforced**. A role assigned only for product `GEN-MOTOR` gives its permissions for every product, and `scope_branches` is never read. The per-request permission set is the union of every in-effect assignment, scopes ignored.
- **The organisation structure:** static data that no code reads. No transactional record carries a branch or organisation; only analytics facts have a `branch_code`.
- **Transaction categories:** not modelled anywhere.

## 1. Which records are scoped
- **By product:** every record whose module knows its product:
  - quotes, proposals, policies, endorsements and claims (General, Life);
  - memberships and medical claims (Health, by the scheme's product);
  - billing accounts and invoices for a policy, and claim payment instructions (by the product of the policy or claim they serve).
- **By organisation:** the same records, through a new `org_node_code` on each root record. It is the organisation node (normally a branch) that owns it. Child records inherit it from their root.
- **Not scoped:** parties (shared across products, PTY-003), static data, and platform records. Workflow approvals and tasks follow the record they are about.

## 2. How a record gets its organisation node
- At creation, from the request's `orgNodeCode` when given. It must be a node in force in the tenant's organisation structure, and within the creator's branch scope for that permission.
- Otherwise, from the creator's scope, when it names exactly one node.
- Otherwise, the tenant's root legal entity. A tenant with no organisation structure has no organisation scoping at all, so behaviour is unchanged.
- A policy keeps its quote's node, and a renewal keeps the policy's. Moving a record between nodes is a separate, audited command, for later.

## 3. Hierarchy
A scope on a node covers that node and every node below it (`parent` links). A scope on a legal entity covers all its regions and branches. "Organisations" in SRS-003 are the legal-entity and region nodes, and "branches" are branch nodes, so one list (`scope_branches`, renamed in the API as `scopeOrgNodes`) covers both.

## 4. How a scope is applied
A permission is **in scope for a record** when at least one in-effect assignment that gives the permission:
- has no product scope, or lists the record's product; **and**
- has no organisation scope, or covers the record's node.

| Operation | Rule |
|---|---|
| Action on a record (command) | Refused with 403 `OUT_OF_SCOPE` when the permission is not in scope for the record |
| Read of one record | 404 when not in scope, so that a record outside the caller's scope is not revealed (as for another tenant) |
| Lists and searches | Only records in scope are returned; filtered in the query, so paging stays correct |
| Creation | The new record's product and node must be in scope |
| Approvals (Workflow) | A checker must have the permission in scope for the record the approval is about, as well as approval authority (WFL-004) |

- **Where it is enforced:** at one point per module, the repository or query used to load and list scoped records. The building block supplies the caller's scopes per permission (`IExecutionContext.ScopesFor(permission)`), with no per-endpoint checks.
- **The permission cache** (ADR-006) caches the scopes with the permissions.

## 5. Transaction categories
Not defined anywhere. The proposal: a category is the permission's resource (`claim`, `payment`, `policy` and so on), which permissions already scope. Nothing more is built until a requirement defines categories differently.

## Consequences
- **Migrations:** a nullable `org_node_code` on each scoped root record, with an index on (`tenant_id`, `org_node_code`). It is backfilled with the tenant's root legal entity where one exists.
- **Every domain module changes:** its repositories and list queries take the caller's scopes. That is a wave of its own, about the size of Wave 1.
- **The front end:**
  - gains an optional `orgNodeCode` on creation requests, and sees fewer records for scoped users;
  - `/identity/me` already returns the scopes per permission.

## Additions after sign-off
- **Shared billing accounts (OQ-081, 24 October 2026):**
  - a caller covering part of an account sees only that part;
  - account-wide operations need the whole account;
  - payments against invoices, accounts and refunds follow them.
- **Claim payouts (24 October 2026):**
  - claim payments and payout batch items follow their claim's product and branch;
  - building or releasing a batch needs all of it in scope;
  - a batch's approval is scoped when its payments share one scope.
- **Commission payouts (24 October 2026):** outside branch scope, and read only with `commission_payout.read`.
