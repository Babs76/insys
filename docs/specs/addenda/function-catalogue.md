# Addendum: the function catalogue, dynamic menus and function-based access (ADR-026)

**Signed off 25 October 2026** (Ade). Built in Wave 1 batch 3; each later batch registers its own functions as it builds.

## Decisions at sign-off (they replace the proposal where they differ)
1. **`audiences` (text[]) replaces `audience`.**
   - Identity & Access, Static Data, Audit and Localisation functions carry `[STAFF, PLATFORM]`; nothing is duplicated.
   - Data separation stays with tenant scope and static data scope.
2. **OQ-092, the taxonomy:** the 12 domains and 54 sub-domains of `docs/specs/INSYS_NAVIGATION.md` (copied from the UI prototype), with their process areas, plus a Configuration domain for static data by module.
   - The operator's screens sit in that file's PLATFORM domain, so no second Platform domain is added.
3. **Portal screens belong to portal sub-domains,** so every function has a sub-domain. They are `BROKER_PORTAL`, `PROVIDER_PORTAL` and `CUSTOMER_PORTAL`, under a PORTALS domain, which is the one domain added beyond the file.
4. **OQ-093:** static data functions per catalogue sub-module (view, maintain, approve), not per table.
5. **OQ-091, the initial features:** `LIFE`, `GENERAL`, `HEALTH`, `REINSURANCE`, `BROKER_PORTAL`, `PROVIDER_PORTAL`, `CUSTOMER_PORTAL`. A function with no feature is base platform.
6. **Members are `CUSTOMER`:** accepted.

## As built (Wave 1 batch 3)
- **The catalogue:** 549 functions (screens, actions and 15 JOB functions) in 14 domains: the 12 of `INSYS_NAVIGATION.md`, PORTALS and CONFIGURATION. Generated into `R__function_catalogue.sql` from each module's `Functions.cs` and the permissions its endpoints require as the router builds them; `FunctionCatalogueTests` fails when the file is stale.
- **One permission per screen:** a screen whose reads needed two permissions was split (bank accounts, consent, claim financials, benefit accumulators), so converting a permission set loses nothing. The one exception is static data maintain, which needs create and update together (OQ-096).
- **Static data:** view, maintain and approve per catalogue module (OQ-095).
- **Names are labels:** a function's name is the common record model's `labels` column (`{"en": "…"}`, translations beside it); there is no name column. The catalogue is global and platform-maintained (`tenant_id IS NULL`), so a tenant cannot rename a function.
- **Only what the release has:** every operation a function names must be an endpoint of the build, and every JOB function one a consumer or daily job runs under (`FunctionCatalogueTests`). Planned functions are not catalogued; a function removed from the code is closed by the next release's script.
- **Deviations to confirm:** OQ-097 (the MFA code), OQ-098 (which background work is switchable), OQ-099 (the first administrator's request), OQ-100 (labels).

- **Basis:** Ade's decisions 1–5 (ADR-026), SRS-001 to SRS-003, IAM-001, IAM-006, IAM-007, TEN-001, §4.1, §8.
- **Today:**
  - **Roles:** a role is a permission set with F103 scopes (`iam.cfg_roles_and_permission_sets`).
  - **Operations:** 388 core operations, 383 platform-operator operations (367 of them static data) and 1,661 static data operations. Each declares one permission, and a few handlers check one more (`x-additional-permissions`).
  - **Unused or unenforced:** `iam.ref_permission_catalogue` exists but nothing reads it; `tenant.cfg_feature_entitlements` is configured but not enforced.
  - **Segregation of duties:** `iam.cfg_segregation_of_duties_conflict_rules` pairs permissions.
  - **Channels:** channel transaction permissions (F005) name permission codes in `transaction_type`.
  - **Privileged roles:** a role is privileged when an authentication policy names it in `mfa_required_roles`, and its assignment then needs approval.

## Names: where the proposal meets the conventions
The tables follow the static data standard: the common record model, a catalogue entry, generated endpoints, class, scope and tier. Every table keeps the standard `code` business key beside its own `*_code` field, as the other static tables do. Three points:

1. **`audience`:** the channel definitions already name audiences `STAFF`, `BROKER`, `CUSTOMER`, `PROVIDER` and `PARTNER` (`channel.cfg_channel_definitions`). The catalogue follows that convention: members are **`CUSTOMER`**, not `MEMBER`. `PLATFORM` is added for the operator tenant.
2. **`required_feature`** names a feature in `tenant.cfg_feature_entitlements.feature` (varchar(30)), which is scope G, with a `tenant` column naming the tenant. Enablement follows `tenant.cfg_tenant_product_availability`, so the new table is `tenant.cfg_tenant_function_availability` as proposed.
3. **`required_permissions`** lists codes from `iam.ref_permission_catalogue` (`resource.operation`). That catalogue is seeded for the first time with every permission the code enforces, the same list the GAP-052 test already gathers. It stays the list of permissions; the function catalogue says which functions need them.

## 1. Tables

| Table | Class, scope, tier | Fields | Notes |
|---|---|---|---|
| `iam.ref_business_domains` | REF, G, 2 | `domain_code`, `icon` | Labels in `labels` |
| `iam.ref_business_sub_domains` | REF, G, 2 | `sub_domain_code`, `domain_code`, `icon` | |
| `iam.ref_function_catalogue` | REF, G, 1 | `function_code`, `sub_domain_code`, `process_area`, `interaction_type` (`HUMAN`, `SYSTEM`), `function_kind` (`SCREEN`, `ACTION`, `JOB`), `parent_function_code`, `audience` (`PLATFORM`, `STAFF`, `BROKER`, `PROVIDER`, `CUSTOMER`), `route_key`, `required_permissions` text[], `required_feature`, `privileged`, `allowed_while_onboarding` | Shipped with each release; `sort_order` orders the menu |
| `tenant.cfg_tenant_function_availability` | CFG, T, 1 | `function_code`, `enabled` | A function with no row is enabled |
| `iam.cfg_role_function_assignments` | CFG, T, 1 | `role_code`, `function_code` | One row per grant |

**Rules on the catalogue:**
- An `ACTION` has a `SCREEN` parent. A `JOB` is `SYSTEM` and has no parent or audience. A `SCREEN` has a `route_key`.
- Static data validators check these rules, and that every `required_permissions` code is in the permission catalogue.

**Changes to existing tables, by new versions and a forward migration** (as V0012 did, ADR-010):
- **`iam.cfg_roles_and_permission_sets`:** `permissions` becomes nullable and deprecated; the role and its scopes stay. New role versions carry no permissions.
- **`iam.cfg_segregation_of_duties_conflict_rules`:** gains `function_a` and `function_b`; `permission_a` and `permission_b` become nullable and deprecated (§4).
- **`channel.cfg_channel_transaction_permissions`:** gains `function_code`, replacing `transaction_type` (§5).

## 2. Enforcement
- **Each operation declares its function:**
  - In code: `.RequireFunction("GENERAL_CLAIM_DECLINE")` beside `.RequirePermission(...)`.
  - In the contract: `x-function` beside `x-permission`.
  - Static data endpoints get theirs from their table: `<TABLE>_VIEW`, `<TABLE>_MAINTAIN` (draft, edit, submit, activate, retire, import) and `<TABLE>_APPROVE` (approve, reject).
- **Order of checks** for a request:
  1. the tenant's status: Suspended and Terminated are refused as today; while Onboarding, only `allowed_while_onboarding` functions;
  2. the platform makes the function available: in force in the catalogue;
  3. the tenant enables it: `FUNCTION_DISABLED`;
  4. the tenant is entitled to `required_feature`: `FEATURE_NOT_ENTITLED`;
  5. the user holds it through an in-effect role (`valid_from` ≤ today < `valid_to`, not revoked): `FUNCTION_NOT_GRANTED`;
  6. the endpoint's permission, now derived from the functions, and F103 and F005 as today.
- **Status codes:** all three new codes are 403. A disabled function looks the same whether a user calls it directly or finds it missing from the menu.
- **Feature entitlements are enforced here for the first time.** A function with no `required_feature` needs none. Features are named by the platform; none is invented, so every function starts with none until features are agreed (OQ-091).
- **System functions:**
  - A consumer whose `JOB` function is disabled for the tenant parks the delivery: prerequisite `FUNCTION_DISABLED`, item the function code (ADR-022).
  - A daily job skips the tenant and logs it. Its due work stays due, so nothing is dropped; enabling the function retries parked deliveries (`StaticDataVersionActivated`) and the job picks up the rest.
- **Onboarding:** `allowed_while_onboarding` replaces the module-level `TransactsBusiness` flag and the `.OnboardingConfiguration()` exemptions. The catalogue migration sets it from today's behaviour, so nothing changes (§7). `TENANT_NOT_ACTIVE` stays the code.

## 3. The menu
`GET /api/v1/identity/me/menu` returns:

```
{ "domains": [ { "code", "labelKey", "icon",
    "subDomains": [ { "code", "labelKey", "icon",
      "screens": [ { "functionCode", "labelKey", "routeKey", "actions": [ { "functionCode", "labelKey" } ] } ] } ] } ] }
```

- **Filtered** by:
  - platform availability, tenant enablement and feature entitlement;
  - the user's role grants in effect;
  - audience: from the user's external link (`PARTY` is `CUSTOMER`, `INTERMEDIARY` is `BROKER`, `PROVIDER` is `PROVIDER`), `PLATFORM` in the operator tenant, otherwise `STAFF`;
  - tenant status: only `allowed_while_onboarding` while Onboarding.
- **Shape:** a screen appears only with its parent's sub-domain and domain; an action only under a screen the user holds.
- **Order** is the catalogue's `sort_order`; tenants cannot change it (decision 4). Labels are translation keys, resolved by the front end in the user's language (`labels` column, localisation §3).
- **Cached per user,** with the permission cache. It is evicted:
  - on the user's role assignments;
  - on activation of roles, role-function assignments, function availability, entitlements or the catalogue (`StaticDataVersionActivated`);
  - on tenant status changes (`TenantActivated`, suspension).

## 4. Governance
- **Tier 1, always a second person:** role-function grants and tenant enablement (SDM-003 with `requiresDifferentChecker`).
- **Privileged functions:**
  - A role is privileged when it holds a privileged function, in addition to today's `mfa_required_roles`.
  - Assigning a privileged role needs approval, as today, and the user must be MFA-enrolled (`iam.user_account.mfa_enrolled`); otherwise it is refused (`MFA_ENROLMENT_REQUIRED`, 422).
  - A role-function grant that makes a role privileged is flagged to its checker.
- **Segregation of duties on function pairs:** a rule names `function_a` and `function_b`, with `BLOCK` or `WARN` and an override role, as today. It is checked:
  - when a role-function grant is submitted (within one role);
  - when a role is assigned to a user (across their roles in effect).
- **Converting today's rules:** each permission-pair rule becomes the function pairs whose required permissions contain the two permissions. The before-and-after test covers them (§7).

## 5. Channels (F005)
- **Recommendation:** yes. `channel.cfg_channel_transaction_permissions` keys on `function_code`, so one catalogue serves menus, access and channels.
- **What it means:** a channel row allows an `ACTION` function for a role, optionally for a product, with or without approval. The channel check runs on the request's function, not on its permission.
- **Converting:** each row with a `transaction_type` permission becomes rows for the `ACTION` functions that require it.

## 6. The catalogue is generated
- **Where it comes from:** the functions come from the code: each endpoint's `RequireFunction`, each job's registration, and the static data descriptors. Domains, sub-domains, process areas, labels, icons and order come from one hand-kept file per module (`Functions.cs`), reviewed like any code.
- **How it ships:** a generator writes `db/migrations/R__function_catalogue.sql` (global rows, as `R__error_code_catalogue.sql`).
- **The test:** a contract test fails when an operation declares no function, a function names a permission its endpoint does not need, or the committed file is stale.
- **Taxonomy:** domains and sub-domains follow the areas and capabilities of the platform's function list (`docs/traceability/functional-coverage.md`: about 15 areas, 60 capabilities), with a Configuration domain for static data by module and a Platform domain for the operator (OQ-092).

## 7. Migration: no change in what anyone can do
1. **Catalogue:** a catalogue entry for every existing operation, every job and every static data table (view, maintain, approve).
2. **Roles:** for each tenant role in force, a new version without permissions, and role-function assignments for every function whose required permissions the role held.
   - A permission no function needs (an in-handler check) gets its own `ACTION` function, so nothing is lost (for example `COMMISSION_PAYOUT_VIEW`).
   - The migration records these versions as approved by `migration (ADR-026)`, as V0012 did, because a second person cannot approve a release.
3. **Onboarding:** `allowed_while_onboarding` is set from today's rules: identity, static data and configuration endpoints, reads, and the `.OnboardingConfiguration()` exemptions.
4. **SoD rules and channel rows** are converted (§4, §5).
5. **The test (TEN-001, SRS-001):** before the conversion, every user's effective permissions in every tenant are recorded. After it, each user's permissions derived from functions must equal them exactly, and so must each SoD and channel decision on a fixed set of cases.
6. **Operator tenant seed:** the platform roles get the `PLATFORM` functions they need today. `insys-user-admin` and the first tenant administrator keep exactly what they have.
7. **Test helpers:** `UserWithPermissions` grants the functions that need those permissions, so existing tests keep their meaning. `UserWithFunctions` is added for new ones.

## 8. Scale (from today's contract)

| Source | Functions |
|---|---|
| Static data: 221 tenant tables (scope T or GT) × view, maintain, approve | about 660, `STAFF` |
| Static data: 73 global tables (scope G or GT) × view, maintain, approve | about 220, `PLATFORM` |
| Business screens: 73 resource groups in the core API | about 75 `SCREEN` |
| Business actions: 234 write operations; several share one action, such as approve and reject | about 200 `ACTION` |
| Portal screens and actions (16 resource groups) | about 25, `BROKER`, `PROVIDER`, `CUSTOMER` |
| Platform operator: tenancy, identity providers, operations | about 15 |
| Jobs: background services and daily jobs | about 20 `JOB` |
| **Total** | **about 1,200** |

- **What a user sees:** a staff user's menu shows only screens they hold, usually tens.
- **Static data:** its 880 functions sit under the Configuration domain, by module. Grouping them per sub-module instead of per table would make about 60 (OQ-093), but would lose per-table grants.

## 9. Tests
- **The menu** for each audience (staff, broker, provider, customer, platform) and tenant status (Onboarding shows only `allowed_while_onboarding`; Active shows all granted; Suspended refuses).
- **Filtering:** disabled, unentitled and ungranted functions are missing, and an action is missing under a screen the user lacks.
- **Direct calls:** a disabled function called directly is refused (`FUNCTION_DISABLED`), as are an ungranted action (`FUNCTION_NOT_GRANTED`) and an unentitled one (`FEATURE_NOT_ENTITLED`).
- **System functions:** a disabled `SYSTEM` function parks its consumer's delivery and releases it when re-enabled.
- **Grants:** a privileged function cannot be granted without MFA enrolment; an SoD `BLOCK` pair is refused across two roles.
- **Before and after:** effective permissions, SoD outcomes and channel decisions for every seeded role.
- **The catalogue:** every operation has `x-function`, and the generated catalogue matches its committed file.
- **Cache eviction:** a grant activated changes the menu at once.

## 10. Open points
- **OQ-091** Feature codes for `required_feature`: none exist in the seed data. Every function starts with none until the features are agreed.
- **OQ-092** The domain and sub-domain taxonomy. Proposed: the function list's areas and capabilities, plus Configuration and Platform. The source list of 103 functions is not in the repository.
- **OQ-093** Static data granularity: per table (about 880 functions, as proposed) or per catalogue sub-module (about 60).
